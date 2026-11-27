// IL2CPPLazyInitSlotScanner.cpp — implementation.

#include "IL2CPPLazyInitSlotScanner.h"

#include "IL2CPPDomain.h"
#include "IL2CPPMetadataRegistration.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "MetadataParser.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"
#include "../shared/SafeChunkRead.h"
#include "../shared/ThrottledRetry.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_map>

namespace Discrete::IL2CPP {

namespace {

// B-L1: Publish-once on successful scan; immutable empty fallback with 1s
// throttled retry if GameAssembly.dll / sections were not ready on first call.
std::atomic<const std::vector<LazyInitSlot>*> g_slotsPtr{nullptr};
const std::vector<LazyInitSlot>               g_emptySlots{};
std::mutex                                    g_scanMu;
std::uint64_t                                 g_lastFailTickMs = 0;

// Section ranges discovered at scan time. Both ranges are absolute VAs
// (NOT RVAs); empty vector means no matching section found.
struct VaRange { std::uintptr_t lo; std::uintptr_t hi; };
struct NamedSection { std::uintptr_t lo; std::uintptr_t hi; std::string name; };

std::vector<VaRange> CollectSections(void* moduleBase, DWORD requiredMask,
                                     DWORD forbiddenMask) {
    std::vector<VaRange> out;
    if (moduleBase == nullptr) return out;
    auto base = reinterpret_cast<std::uint8_t*>(moduleBase);

    auto dos = Memory::SafeRead<IMAGE_DOS_HEADER>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE) return out;
    auto nt = Memory::SafeRead<IMAGE_NT_HEADERS64>(base + dos->e_lfanew);
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE) return out;

    auto sectionsAddr = base + dos->e_lfanew
                        + offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
                        + nt->FileHeader.SizeOfOptionalHeader;
    const std::uint16_t nSect = nt->FileHeader.NumberOfSections;
    out.reserve(nSect);
    for (std::uint16_t i = 0; i < nSect; ++i) {
        auto sh = Memory::SafeRead<IMAGE_SECTION_HEADER>(
            sectionsAddr + i * sizeof(IMAGE_SECTION_HEADER));
        if (!sh) break;
        const DWORD ch = sh->Characteristics;
        if ((ch & requiredMask)  != requiredMask)  continue;
        if ((ch & forbiddenMask) != 0)             continue;
        if (sh->Misc.VirtualSize == 0) continue;
        VaRange r;
        r.lo = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        r.hi = r.lo + sh->Misc.VirtualSize;
        out.push_back(r);
    }
    return out;
}

// Same shape as CollectSections but captures the section name so we can
// filter to specifically `.il2cpp` (the AOT-translated C# code section).
// CRT helpers, fiber-local-storage stubs, Windows API name lookup tables,
// and other non-IL2CPP code live in `.text` — including those produces
// thousands of spurious `lea + call` matches that have nothing to do
// with IL2CPP class init.
std::vector<NamedSection> CollectNamedSections(void* moduleBase,
                                                DWORD requiredMask,
                                                DWORD forbiddenMask) {
    std::vector<NamedSection> out;
    if (moduleBase == nullptr) return out;
    auto base = reinterpret_cast<std::uint8_t*>(moduleBase);

    auto dos = Memory::SafeRead<IMAGE_DOS_HEADER>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE) return out;
    auto nt = Memory::SafeRead<IMAGE_NT_HEADERS64>(base + dos->e_lfanew);
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE) return out;

    auto sectionsAddr = base + dos->e_lfanew
                        + offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
                        + nt->FileHeader.SizeOfOptionalHeader;
    const std::uint16_t nSect = nt->FileHeader.NumberOfSections;
    out.reserve(nSect);
    for (std::uint16_t i = 0; i < nSect; ++i) {
        auto sh = Memory::SafeRead<IMAGE_SECTION_HEADER>(
            sectionsAddr + i * sizeof(IMAGE_SECTION_HEADER));
        if (!sh) break;
        const DWORD ch = sh->Characteristics;
        if ((ch & requiredMask)  != requiredMask)  continue;
        if ((ch & forbiddenMask) != 0)             continue;
        if (sh->Misc.VirtualSize == 0) continue;
        NamedSection s;
        s.lo = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        s.hi = s.lo + sh->Misc.VirtualSize;
        char nameBuf[9] = {};
        std::memcpy(nameBuf, sh->Name, sizeof(sh->Name));
        s.name = nameBuf;
        out.push_back(std::move(s));
    }
    return out;
}

inline bool InRanges(const std::vector<VaRange>& rs, std::uintptr_t v) {
    for (const auto& r : rs) if (v >= r.lo && v < r.hi) return true;
    return false;
}

// REX prefix family detector — accept any 0x40..0x4F.
inline bool IsRexByte(std::uint8_t b) noexcept {
    return (b & 0xF0u) == 0x40u;
}
// mod=00, r/m=101 — the canonical RIP-relative ModRM. The reg field
// (bits 3-5) doesn't change addressing.
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept {
    return (m & 0xC7u) == 0x05u;
}
inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    return  static_cast<std::int32_t>(
            static_cast<std::uint32_t>(p[0])
          | (static_cast<std::uint32_t>(p[1]) <<  8)
          | (static_cast<std::uint32_t>(p[2]) << 16)
          | (static_cast<std::uint32_t>(p[3]) << 24));
}

bool DoScan(std::vector<LazyInitSlot>& outSlots) {
    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: GameAssembly.dll not loaded");
        return false;
    }

    // ---- Source sections: ONLY the `il2cpp` section ----
    // Phase 28b-v3.1 finding: scanning all exec sections including `.text`
    // produces ~45K false-positive matches (vendored CRT code, Fiber-Local-
    // Storage shims, Windows API name lookup tables — every `lea reg,
    // [string]; call helper` qualifies). The post-call IL2CPP class-init
    // signature filter below kills most of these, but it's still 10x faster
    // and cleaner to skip `.text` entirely. `.il2cpp` is where Unity's
    // AOT-translated C# code lives, and that's where class-init patterns
    // come from.
    auto allExecSections = CollectNamedSections(gaBase, IMAGE_SCN_MEM_EXECUTE, 0);
    if (allExecSections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: no exec sections in GameAssembly.dll");
        return false;
    }
    std::vector<NamedSection> scanSections;
    for (const auto& s : allExecSections) {
        // Section name comparison is byte-wise; PE section names are
        // 8 bytes with NUL padding. Modern IL2CPP uses "il2cpp" (six
        // chars) — Unity's IL2CPP code-gen output. Match prefix in case
        // future Unity versions append a suffix.
        if (s.name == "il2cpp" || s.name.find("il2cpp") == 0) {
            scanSections.push_back(s);
        }
    }
    if (scanSections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: no `il2cpp` section in GameAssembly.dll "
            "(this likely means a non-IL2CPP target; v3 scan unavailable)");
        return false;
    }

    // ---- Call-target destinations: ANY exec section of the module ----
    // (init function might live in `.text` even though the call site is in
    // `.il2cpp`.)
    std::vector<VaRange> execRanges;
    execRanges.reserve(allExecSections.size());
    for (const auto& s : allExecSections) execRanges.push_back({s.lo, s.hi});

    // ---- Lea-target destinations: any readable non-exec section ----
    auto dataSections = CollectSections(
        gaBase,
        IMAGE_SCN_MEM_READ,
        IMAGE_SCN_MEM_EXECUTE);
    if (dataSections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: no data sections in GameAssembly.dll");
        return false;
    }

    // 1 MB chunk + tail overlap. Signature length is 24 bytes (lea 7 +
    // call 5 + lock-or-fence 5 + mov-byte 7); 32 byte overlap gives
    // plenty of margin.
    constexpr std::size_t kChunkSize    = 1024u * 1024u;
    constexpr std::size_t kTailOverlap  = 32u;
    std::vector<std::uint8_t> buf;
    buf.reserve(kChunkSize);

    // Post-call IL2CPP class-init signature bytes (15 bytes total, including
    // disp32 wildcards via the mov-byte instruction).
    //
    // The canonical sequence after the init `call`:
    //   F0 83 0C 24 00              lock or dword ptr [rsp], 0   (5 bytes)
    //   C6 05 ?? ?? ?? ?? 01        mov byte ptr [rip+disp32], 1 (7 bytes)
    //
    // These two together form the "init complete" memory-barrier + flag-set
    // pair that IL2CPP emits after every class-init helper call. CRT and
    // system code never emit this exact pair — `lock or [rsp], 0` is rare
    // outside the IL2CPP runtime's specific class-init protocol.
    //
    // A small (0-3) gap is allowed between the call and the signature to
    // tolerate compiler scheduling (e.g. unrelated `xor eax, eax` between
    // call and fence — observed once on AAA targets).
    constexpr std::uint8_t kLockOrPattern[5] = { 0xF0, 0x83, 0x0C, 0x24, 0x00 };
    // mov byte [rip+disp32], 1 = C6 05 XX XX XX XX 01 (length 7)

    std::size_t bytesScanned   = 0;
    std::size_t leaCandidates  = 0;
    std::size_t failedReads    = 0;
    std::size_t matchedPairs   = 0;
    std::size_t signatureMisses = 0;  // lea+call matched but post-init sig didn't

    for (const auto& sec : scanSections) {
        const std::size_t size = sec.hi - sec.lo;
        // B-L5: Use SafePeScan::ScanReadableSpans so an AC-stripped page inside
        // a 1 MiB chunk falls back to 4 KiB page-by-page reads instead of
        // dropping readable code before the bad page.
        SafePeScan::ScanReadableSpans(
            reinterpret_cast<const std::uint8_t*>(sec.lo),
            size,
            kChunkSize,
            kTailOverlap,
            buf,
            [&](const std::uint8_t* localSpan,
                const std::uint8_t* realSpanBase,
                std::size_t spanLen) -> bool {
                bytesScanned += spanLen;
                if (spanLen < 24u) return true;
                const std::uintptr_t spanVa = reinterpret_cast<std::uintptr_t>(realSpanBase);
                const std::size_t scanEnd = spanLen - 24u + 1u;

                for (std::size_t p = 0; p < scanEnd; ++p) {
                    // ---- 1. lea reg, [rip+disp32] ----------------------------
                    std::size_t leaLen = 0;
                    std::int32_t leaDisp = 0;
                    {
                        std::size_t off = 0;
                        if (IsRexByte(localSpan[p])) ++off;
                        if (p + off + 6 > spanLen) continue;
                        if (localSpan[p + off] != 0x8D) continue;
                        if (!IsRipRelativeModrm(localSpan[p + off + 1])) continue;
                        leaDisp = ReadInt32LE(localSpan + p + off + 2);
                        leaLen  = off + 2 + 4;
                    }

                    // REX-skip dedup.
                    if (leaLen == 6 && p > 0 && IsRexByte(localSpan[p - 1])) continue;

                    const std::uintptr_t leaIP    = spanVa + p;
                    const std::uintptr_t leaAfter = leaIP + leaLen;
                    const std::uintptr_t leaTarget =
                        leaAfter + static_cast<std::intptr_t>(leaDisp);

                    if (!InRanges(dataSections, leaTarget)) continue;
                    ++leaCandidates;

                    // ---- 2. call rel32 (immediately after lea) ---------------
                    const std::size_t callPos = p + leaLen;
                    if (callPos + 5 > spanLen) continue;
                    if (localSpan[callPos] != 0xE8) continue;

                    const std::int32_t callDisp = ReadInt32LE(localSpan + callPos + 1);
                    const std::uintptr_t callIP    = spanVa + callPos;
                    const std::uintptr_t callAfter = callIP + 5;
                    const std::uintptr_t callTarget =
                        callAfter + static_cast<std::intptr_t>(callDisp);

                    if (!InRanges(execRanges, callTarget)) continue;

                    // ---- 3. Required: `lock or [rsp], 0` within next 4 bytes ----
                    // Allow tiny gap (0-3 bytes) for compiler scheduling.
                    const std::size_t afterCall = callPos + 5;
                    bool sigOk = false;
                    for (std::size_t gap = 0; gap <= 3; ++gap) {
                        const std::size_t cand = afterCall + gap;
                        if (cand + 5 + 7 > spanLen) break;
                        if (std::memcmp(localSpan + cand,
                                        kLockOrPattern, 5) != 0) continue;
                        // Check the trailing `mov byte [rip+disp32], 1`.
                        const std::size_t movPos = cand + 5;
                        if (localSpan[movPos] != 0xC6) continue;
                        if (localSpan[movPos + 1] != 0x05) continue;
                        if (localSpan[movPos + 6] != 0x01) continue;
                        sigOk = true;
                        break;
                    }
                    if (!sigOk) { ++signatureMisses; continue; }

                    LazyInitSlot s;
                    s.slotAddress = leaTarget;
                    s.leaIP       = leaIP;
                    s.callIP      = callIP;
                    s.callTarget  = callTarget;

                    // Phase 28b-v3.1 — read slot value at scan time. If high
                    // 32 bits are zero AND low 32 bits decode to a valid
                    // metadata-usage token (kind in 1..6), capture the
                    // token + decoded kind/index for later static mapping.
                    std::uint64_t slotVal = 0;
                    if (Memory::SafeReadBytes(
                            reinterpret_cast<const void*>(leaTarget),
                            &slotVal, sizeof(slotVal))) {
                        const std::uint32_t hi32 =
                            static_cast<std::uint32_t>(slotVal >> 32);
                        const std::uint32_t lo32 =
                            static_cast<std::uint32_t>(slotVal & 0xFFFFFFFFu);
                        if (hi32 == 0 && lo32 != 0) {
                            // Token-shaped. Decode kind = top 3 bits of lo32,
                            // index = bottom 29 bits.
                            const std::uint8_t  kind  =
                                static_cast<std::uint8_t>((lo32 >> 29) & 0x7u);
                            const std::uint32_t index = lo32 & 0x1FFFFFFFu;
                            if (kind >= 1 && kind <= 6 && index != 0) {
                                s.tokenAtScan = lo32;
                                s.tokenKind   = kind;
                                s.tokenIndex  = index;
                            }
                        }
                    }

                    outSlots.push_back(s);
                    ++matchedPairs;
                }
                return true;
            },
            &failedReads);
    }

    // Dedup by slotAddress — many methods reference the same slot, but
    // for v3's purposes we only need one entry per slot (the lookup is
    // "does the slot's current value match the target class ptr"
    // regardless of which method's init-call wrote it).
    std::sort(outSlots.begin(), outSlots.end(),
        [](const LazyInitSlot& a, const LazyInitSlot& b) {
            return a.slotAddress < b.slotAddress;
        });
    outSlots.erase(
        std::unique(outSlots.begin(), outSlots.end(),
            [](const LazyInitSlot& a, const LazyInitSlot& b) {
                return a.slotAddress == b.slotAddress;
            }),
        outSlots.end());

    Log::Write(MagicStrings::LogCategoryWalk,
        "LazyInitSlotScanner: scanned " + std::to_string(bytesScanned) +
        " bytes (.il2cpp only); leaCandidates=" + std::to_string(leaCandidates) +
        " matchedPairs=" + std::to_string(matchedPairs) +
        " signatureMisses=" + std::to_string(signatureMisses) +
        " uniqueSlots=" + std::to_string(outSlots.size()) +
        " failedReads=" + std::to_string(failedReads));
    return true;
}

}  // namespace

const std::vector<LazyInitSlot>& EnumerateLazyInitSlots() {
    if (const auto* p = g_slotsPtr.load(std::memory_order_acquire)) {
        return *p;
    }
    std::lock_guard<std::mutex> lk(g_scanMu);
    if (const auto* p = g_slotsPtr.load(std::memory_order_relaxed)) {
        return *p;
    }
    const std::uint64_t now = ::GetTickCount64();
    if (!RetryPolicy::CanRetryNow(g_lastFailTickMs, now)) {
        return g_emptySlots;
    }
    std::vector<LazyInitSlot> local;
    if (DoScan(local)) {
        auto* published = new std::vector<LazyInitSlot>(std::move(local));
        g_slotsPtr.store(published, std::memory_order_release);
        return *published;
    }
    g_lastFailTickMs = RetryPolicy::NonZeroTick(::GetTickCount64());
    return g_emptySlots;
}

// --- Phase 28b-v3.1 typeDef → Il2CppClass* global map -------------------
//
// IL2CPP `kIl2CppMetadataUsageTypeInfo` tokens carry a **typeDefIndex**
// (into global-metadata.dat's `Il2CppTypeDefinition` table), NOT an index
// into `Il2CppMetadataRegistration->types[]`. The latter is a smaller
// table of `Il2CppType*` covering parameterised types (`T<int>`, `T[]`,
// etc.); the former covers every TypeDef in the metadata file (including
// nested + compiler-generated like `<>c`).
//
// To resolve TypeInfo tokens we build a global `typeDefIndex →
// Il2CppClass*` map by:
//   1. Walking metadata images (`MetadataParser::EnumerateImages()`) to
//      get each image's `typeStart` (the global typeDefIndex of its
//      first TypeDef).
//   2. For each metadata image, finding the matching runtime
//      `Il2CppImage*` by name (`FindImageByName`).
//   3. Iterating per-image classes via `il2cpp_image_get_class(image, i)`
//      — IL2CPP enumerates classes in the same order as TypeDefs in the
//      image, so the global index is `image.typeStart + i`.
//
// Cached via std::call_once. Build time on SaGa F2R-scale game: ~50-200ms
// (single thread-attach + a couple of image walks).
//
// First-call discovery is gated by `BuildTypeDefMap()`; per-slot lookup
// is O(1) hash lookup.

std::once_flag                                       g_mapOnce;
std::unordered_map<std::uint32_t, Il2CppClass*>      g_typeDefMap;

void BuildTypeDefMap() {
    const auto& r = GetResolver();
    if (!r.resolved ||
        r.image_get_class_count == nullptr ||
        r.image_get_class       == nullptr) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: typeDefMap unavailable — "
            "il2cpp_image_get_class[_count] resolver unbound");
        return;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: typeDefMap: no il2cpp domain");
        return;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: typeDefMap: thread_attach failed");
        return;
    }

    const auto& meta = Metadata::Parser::Get();
    if (!meta.loaded()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: typeDefMap: MetadataParser not loaded "
            "(global-metadata.dat couldn't be read at startup) — v3.1 "
            "static slot resolution unavailable");
        return;
    }

    auto metaImages = meta.EnumerateImages();
    auto runtimeAsm = EnumerateAssemblies();

    // Build name → runtime image map for O(1) lookup. Names match the
    // metadata's image name without `.dll` (EnumerateAssemblies strips it).
    std::unordered_map<std::string, const Il2CppImage*> rtByName;
    rtByName.reserve(runtimeAsm.size() * 2);
    for (const auto& a : runtimeAsm) {
        if (a.image != nullptr) rtByName[a.name] = a.image;
    }

    std::size_t imagesMatched = 0;
    std::size_t imagesSkipped = 0;
    std::size_t classesMapped = 0;

    for (const auto& mi : metaImages) {
        // metadata image name includes `.dll`; strip for comparison.
        std::string nm = mi.name;
        if (nm.size() >= 4 &&
            nm.compare(nm.size() - 4, 4, ".dll") == 0) {
            nm.resize(nm.size() - 4);
        }

        auto it = rtByName.find(nm);
        if (it == rtByName.end()) {
            ++imagesSkipped;
            continue;
        }
        const Il2CppImage* rtImg = it->second;
        ++imagesMatched;

        // image_get_class[_count] are well-tested IL2CPP runtime exports
        // — no SEH needed for valid (image, idx) pairs. If the runtime
        // is in an exotic state (debug-only image, etc.) classCount may
        // be 0 or the per-iteration call returns null, both of which we
        // tolerate.
        const std::size_t classCount = r.image_get_class_count(rtImg);
        for (std::size_t i = 0; i < classCount; ++i) {
            Il2CppClass* k = r.image_get_class(rtImg, i);
            if (k == nullptr) continue;
            const std::uint32_t globalIdx =
                static_cast<std::uint32_t>(mi.typeStart + i);
            g_typeDefMap.emplace(globalIdx, k);
            ++classesMapped;
        }
    }

    char buf[256];
    std::snprintf(buf, sizeof(buf),
        "LazyInitSlotScanner: typeDefMap built — %zu classes mapped "
        "across %zu/%zu images (skipped %zu unmatched), map size=%zu",
        classesMapped, imagesMatched, metaImages.size(),
        imagesSkipped, g_typeDefMap.size());
    Log::Write(MagicStrings::LogCategoryWalk, buf);
}

Il2CppClass* ResolveTypeInfoSlotClass(const LazyInitSlot& slot) {
    if (slot.tokenKind != static_cast<std::uint8_t>(MetadataUsageKind::TypeInfo)) {
        return nullptr;
    }
    if (slot.tokenIndex == 0) return nullptr;

    // Phase 28b-v3.1 design pivot — discovered empirically that the
    // token's `index` field is NOT a typeDefIndex (the typeDef table
    // is 10001 entries, but observed indices reach 50999). It's an
    // index into `Il2CppMetadataRegistration->metadataUsages[]` — a
    // flat array of `void**` slot pointers covering all metadata-usage
    // kinds (TypeInfo + Type + Method + Field + StringLit + MethodRef
    // unified). AOT code reads `*(g_MetadataUsages[index])` to get
    // the resolved metadata pointer.
    //
    // Resolution:
    //   1. Look up `g_MetadataUsages[index]` → slot pointer (e.g.
    //      0x195D6EF0).
    //   2. Verify it matches the slot we're resolving (sanity check).
    //   3. Read the slot's current value. If it's an Il2CppClass*
    //      pointer (already initialised), return it.
    //   4. If the slot is still token-form (uninitialised), fall back
    //      to the typeDefMap lookup — but with the correct index this
    //      time. We don't currently have a way to map usage-index →
    //      typeDef-index without additional metadata-table walking
    //      (would need Il2CppMetadataUsageTable + per-kind sub-tables).
    //      So if slot is null, return nullptr (caller surfaces "not
    //      mapped, lambda hasn't run yet").
    const void* const* usages = MetadataRegistration::MetadataUsagesArray();
    const std::size_t  count  = MetadataRegistration::MetadataUsagesCount();
    if (usages == nullptr || count == 0) return nullptr;
    if (slot.tokenIndex >= count) return nullptr;

    // Read the usage slot pointer (a void** — pointer to the actual
    // slot cell where the resolved metadata pointer lives).
    std::uintptr_t usageSlotPtr = 0;
    if (!Memory::SafeReadBytes(
            usages + slot.tokenIndex, &usageSlotPtr,
            sizeof(usageSlotPtr))) {
        return nullptr;
    }
    if (usageSlotPtr == 0) return nullptr;

    // Sanity: the usage entry should equal the slot address we found
    // statically via `lea reg, [rip+SLOT]` matching. If they don't
    // match, the token decoding or the metadata-usages reading is off.
    if (usageSlotPtr != slot.slotAddress) {
        // Mismatch — log once for diagnosis but don't crash. Phase
        // 28b-v3.1 cookbook is still being worked out for v29; this
        // is the canonical "we got the encoding wrong" signal.
        static std::once_flag s_warnOnce;
        std::call_once(s_warnOnce, [&]() {
            char buf[256];
            std::snprintf(buf, sizeof(buf),
                "LazyInitSlotScanner: usage/slot mismatch — "
                "g_MetadataUsages[%u]=0x%llX but our slot=0x%llX "
                "(token decoding may be off for v29; see plan.md "
                "§Phase 28b-v3.1)",
                slot.tokenIndex,
                static_cast<unsigned long long>(usageSlotPtr),
                static_cast<unsigned long long>(slot.slotAddress));
            Log::Write(MagicStrings::LogCategoryWalk, buf);
        });
        return nullptr;
    }

    // Re-read the slot's current value (may have been initialised
    // since scan time).
    std::uintptr_t curVal = 0;
    if (!Memory::SafeReadBytes(
            reinterpret_cast<const void*>(slot.slotAddress),
            &curVal, sizeof(curVal))) {
        return nullptr;
    }
    if (curVal == 0) return nullptr;

    // Heuristic: if the value looks like a heap pointer (high 32 bits
    // non-zero, 8-byte aligned), assume it's a valid Il2CppClass*.
    // Verifying via the resolver `class_get_name` would be more rigorous
    // but risks AV on garbage values (lesson from build #583 crash).
    if ((curVal & 7u) != 0 || curVal < 0x10000) return nullptr;
    const std::uint32_t hi32 = static_cast<std::uint32_t>(curVal >> 32);
    if (hi32 == 0) return nullptr;  // still token, not initialised

    return reinterpret_cast<Il2CppClass*>(curVal);
}

}  // namespace Discrete::IL2CPP
