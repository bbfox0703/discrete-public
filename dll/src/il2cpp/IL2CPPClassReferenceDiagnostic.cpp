// IL2CPPClassReferenceDiagnostic.cpp — implementation.

#include "IL2CPPClassReferenceDiagnostic.h"

#include "IL2CPPResolver.h"
#include "IL2CPPStaticFieldsOffset.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/SafeChunkRead.h"
#include "../shared/ScanCancel.h"
#include "../shared/SehFilter.h"

#include <windows.h>

#include <algorithm>
#include <cstring>

namespace Discrete::IL2CPP {

namespace {

#pragma warning(push)
#pragma warning(disable: 4509)
void* SehStaticData(fn_class_get_static_data fn, Il2CppClass* k) {
    __try { return fn(k); } __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
FieldInfo* SehNextField(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    unsigned long code = 0;
    __try { return fn(k, iter); }
    __except ((code = GetExceptionCode()),
              Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        RuntimeHealth::MarkRuntimeFault("il2cpp_class_get_fields", code);
        return nullptr;
    }
}
#pragma warning(pop)

// Walk the PE header + section table. We capture every readable section
// (executable or not) so the caller sees `.text` size for context, even
// though we only scan non-executable sections for pointer slots.
std::vector<DiagnosticSection> EnumerateAllSections(void* moduleBase) {
    std::vector<DiagnosticSection> out;
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
        if ((sh->Characteristics & IMAGE_SCN_MEM_READ) == 0) continue;
        DiagnosticSection s;
        char nameBuf[9] = {};
        std::memcpy(nameBuf, sh->Name, sizeof(sh->Name));
        s.name = nameBuf;
        s.va   = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        s.size = sh->Misc.VirtualSize;
        s.isExecutable = (sh->Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
        s.isWritable   = (sh->Characteristics & IMAGE_SCN_MEM_WRITE)   != 0;
        out.push_back(std::move(s));
    }
    return out;
}

// Capture 128 bytes of context around a matched slot — 64 before + 64
// starting at the slot. Falls back to fewer bytes if the leading window
// underflows the region or the read AV's.
DiagnosticSlot CaptureContext(std::uintptr_t slotAddress,
                              std::uintptr_t regionLow,
                              std::uintptr_t regionHigh) {
    DiagnosticSlot s;
    s.address = slotAddress;
    constexpr std::size_t kBefore = 64;
    constexpr std::size_t kAfter  = 64;
    std::uintptr_t readStart = (slotAddress > kBefore) ? (slotAddress - kBefore) : slotAddress;
    if (readStart < regionLow) {
        readStart = regionLow;
    }
    std::size_t before = static_cast<std::size_t>(slotAddress - readStart);
    std::size_t after  = kAfter;
    if (regionHigh > slotAddress) {
        const std::size_t available = static_cast<std::size_t>(regionHigh - slotAddress);
        if (available < after) after = available;
    } else {
        after = 0;
    }
    const std::size_t total = before + after;
    if (total == 0) return s;
    s.context.resize(total);
    if (!Memory::SafeReadBytes(reinterpret_cast<const void*>(readStart),
                                s.context.data(), total)) {
        // Partial fail — keep whatever we got via zero-init defaults.
        // Try smaller windows (region might straddle a guarded page).
        s.context.clear();
        for (std::size_t shrink : { static_cast<std::size_t>(32),
                                    static_cast<std::size_t>(16),
                                    static_cast<std::size_t>(8) }) {
            if (shrink * 2 > total) continue;
            s.context.resize(shrink * 2);
            if (Memory::SafeReadBytes(
                    reinterpret_cast<const void*>(slotAddress - shrink),
                    s.context.data(), shrink * 2)) {
                s.contextSlotOffset = shrink;
                return s;
            }
            s.context.clear();
        }
        return s;
    }
    s.contextSlotOffset = before;
    return s;
}

// Scan one contiguous byte range for 8-byte aligned qword equal to `target`.
// Used for both module sections and heap regions. Hits beyond `limit` are
// dropped (caller signals via cap-hit boolean).
void ScanRangeForPointer(std::uintptr_t base, std::size_t size,
                         std::uintptr_t target,
                         std::size_t    limit,
                         const std::string& sectionName,
                         unsigned long  regionProtect,
                         unsigned long  regionType,
                         std::uintptr_t regionLow,
                         std::uintptr_t regionHigh,
                         std::vector<DiagnosticSlot>* outSlots,
                         std::size_t*   outBytesScanned,
                         bool*          outCapHit) {
    if (outBytesScanned == nullptr || outSlots == nullptr) return;

    constexpr std::size_t kChunkSize = 1024u * 1024u;
    // Thread-local and reused across calls. This function runs once per
    // (target pointer x committed region), and EnumerateCommittedRegions
    // returns thousands of regions in a Unity process — so a fresh
    // `std::vector<std::uint8_t> buf(kChunkSize)` here meant a 1 MB allocation
    // AND a 1 MB memset per call (vector's sized ctor value-initialises), i.e.
    // gigabytes of pointless zeroing before a single byte was scanned. The
    // buffer is fully overwritten by SafeReadBytes before it is read, so its
    // prior contents never matter. thread_local keeps the parallel scan paths
    // independent without a lock.
    thread_local std::vector<std::uint8_t> buf;
    if (buf.size() < kChunkSize) buf.resize(kChunkSize);

    // B-L5: Use SafePeScan::ScanReadableSpans so an AC-stripped / guarded page
    // inside a 1 MiB chunk falls back to 4 KiB page reads instead of dropping
    // up to ~1 MiB of readable pages ahead of it.
    SafePeScan::ScanReadableSpans(
        reinterpret_cast<const std::uint8_t*>(base),
        size,
        kChunkSize,
        /*overlapBytes=*/0u,
        buf,
        [&](const std::uint8_t* localSpan,
            const std::uint8_t* realSpanBase,
            std::size_t spanLen) -> bool {
            if (ScanCancel::IsCancelRequested()) return false;
            const std::uintptr_t realAddr0 = reinterpret_cast<std::uintptr_t>(realSpanBase);
            const std::size_t alignPad =
                (sizeof(std::uintptr_t) - (realAddr0 & (sizeof(std::uintptr_t) - 1u))) &
                (sizeof(std::uintptr_t) - 1u);
            for (std::size_t i = alignPad; i + sizeof(std::uintptr_t) <= spanLen;
                 i += sizeof(std::uintptr_t)) {
                std::uintptr_t val = 0;
                std::memcpy(&val, localSpan + i, sizeof(val));
                if (val != target) continue;
                if (outSlots->size() >= limit) {
                    if (outCapHit) *outCapHit = true;
                    *outBytesScanned += i;
                    return false;
                }
                DiagnosticSlot s = CaptureContext(realAddr0 + i,
                                                  regionLow, regionHigh);
                s.sectionName   = sectionName;
                s.regionBase    = regionLow;
                s.regionSize    = static_cast<std::size_t>(regionHigh - regionLow);
                s.regionProtect = regionProtect;
                s.regionType    = regionType;
                outSlots->push_back(std::move(s));
            }
            *outBytesScanned += spanLen;
            return true;
        });
}

}  // namespace

DiagnosticResult DiagnoseClassReferences(Il2CppClass*       klass,
                                          const std::string& fieldName,
                                          bool               heapScan,
                                          std::size_t        heapSlotLimitPerPointer,
                                          std::size_t        heapMaxRegionSize) {
    DiagnosticResult out;
    if (klass == nullptr) { out.error = "klass is null"; return out; }
    out.classPtr = reinterpret_cast<std::uintptr_t>(klass);

    const auto& r = GetResolver();
    if (!r.resolved) { out.error = "il2cpp runtime not resolved"; return out; }

    // Static-fields pointer — may be null if class not yet initialised.
    if (r.class_get_static_data != nullptr) {
        if (void* sf = SehStaticData(r.class_get_static_data, klass)) {
            out.staticFieldsPtr = reinterpret_cast<std::uintptr_t>(sf);
        }
    }
    out.staticFieldsOffsetInClass = GetStaticFieldsOffsetInClass();

    // Resolve target FieldInfo* (if caller asked). Walks the parent chain
    // since `<>c` may inherit fields from `System.Object` etc.
    out.targetFieldName = fieldName;
    if (!fieldName.empty() &&
        r.class_get_fields != nullptr &&
        r.field_get_name   != nullptr) {
        void* iter = nullptr;
        for (;;) {
            FieldInfo* f = SehNextField(r.class_get_fields, klass, &iter);
            if (f == nullptr) break;
            const char* n = r.field_get_name(f);
            if (n == nullptr) continue;
            if (fieldName == n) {
                out.targetFieldInfoPtr = reinterpret_cast<std::uintptr_t>(f);
                if (r.field_get_offset != nullptr) {
                    out.targetFieldOffset = r.field_get_offset(f);
                }
                break;
            }
        }
    }

    // 256-byte dump of the class struct for layout inspection.
    out.classStructDump.resize(256);
    if (!Memory::SafeReadBytes(klass, out.classStructDump.data(), out.classStructDump.size())) {
        out.classStructDump.clear();
    }

    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        out.error = "GameAssembly.dll not loaded";
        return out;
    }
    out.moduleBase = reinterpret_cast<std::uintptr_t>(gaBase);
    out.moduleSize = ModuleHelper::GetModuleSize(MagicStrings::ModuleIL2CPP);
    out.sections   = EnumerateAllSections(gaBase);

    // ------------------------------------------------------------------
    // Module pass — scan every readable, non-executable section in
    // GameAssembly.dll for both pointers. Hard limit per pointer high
    // (4096) since module sections are bounded and we want all hits.
    // ------------------------------------------------------------------
    constexpr std::size_t kModulePerPointerLimit = 4096;
    for (const auto& sec : out.sections) {
        if (ScanCancel::IsCancelRequested()) {
            out.cancelled = true;
            out.ok = false;
            return out;
        }
        if (sec.isExecutable) continue;
        if (sec.size == 0) continue;
        const std::uintptr_t low  = sec.va;
        const std::uintptr_t high = sec.va + sec.size;
        bool capHit = false;
        ScanRangeForPointer(sec.va, sec.size, out.classPtr,
                            kModulePerPointerLimit, sec.name, 0, MEM_IMAGE,
                            low, high,
                            &out.moduleClassPtrSlots, &out.moduleBytesScanned,
                            &capHit);
        if (out.staticFieldsPtr != 0) {
            ScanRangeForPointer(sec.va, sec.size, out.staticFieldsPtr,
                                kModulePerPointerLimit, sec.name, 0, MEM_IMAGE,
                                low, high,
                                &out.moduleStaticFieldsSlots,
                                &out.moduleBytesScanned, &capHit);
        }
        if (out.targetFieldInfoPtr != 0) {
            ScanRangeForPointer(sec.va, sec.size, out.targetFieldInfoPtr,
                                kModulePerPointerLimit, sec.name, 0, MEM_IMAGE,
                                low, high,
                                &out.moduleFieldInfoSlots,
                                &out.moduleBytesScanned, &capHit);
        }
    }

    // ------------------------------------------------------------------
    // Heap pass — every committed RW region NOT inside GameAssembly.dll's
    // image. This finds runtime-allocated metadata tables, usage caches,
    // dispatcher arrays, etc.
    // ------------------------------------------------------------------
    if (heapScan) {
        const std::uintptr_t gaLo = out.moduleBase;
        const std::uintptr_t gaHi = gaLo + out.moduleSize;
        auto regions = Memory::EnumerateCommittedRegions();
        for (const auto& reg : regions) {
            if (ScanCancel::IsCancelRequested()) {
                out.cancelled = true;
                out.ok = false;
                return out;
            }
            const std::uintptr_t base = reinterpret_cast<std::uintptr_t>(reg.base);
            const std::uintptr_t end  = base + reg.size;
            // Skip MEM_IMAGE inside GameAssembly.dll (already scanned via
            // section table above; the EnumerateCommittedRegions view of
            // it would double-emit hits with regionType=MEM_IMAGE).
            if (base >= gaLo && end <= gaHi) continue;
            // Skip giant regions (Unity scratch arenas).
            if (reg.size > heapMaxRegionSize) {
                ++out.heapRegionsSkipped;
                continue;
            }
            // PAGE_NOACCESS / guard pages already filtered by EnumerateCommittedRegions.
            ++out.heapRegionsScanned;
            bool capHitClass = false;
            bool capHitSf    = false;
            if (out.heapClassPtrSlots.size() < heapSlotLimitPerPointer) {
                ScanRangeForPointer(base, reg.size, out.classPtr,
                                    heapSlotLimitPerPointer,
                                    /*sectionName*/ "", reg.protect, reg.type,
                                    base, end,
                                    &out.heapClassPtrSlots, &out.heapBytesScanned,
                                    &capHitClass);
            }
            if (out.staticFieldsPtr != 0 &&
                out.heapStaticFieldsSlots.size() < heapSlotLimitPerPointer) {
                ScanRangeForPointer(base, reg.size, out.staticFieldsPtr,
                                    heapSlotLimitPerPointer,
                                    /*sectionName*/ "", reg.protect, reg.type,
                                    base, end,
                                    &out.heapStaticFieldsSlots, &out.heapBytesScanned,
                                    &capHitSf);
            }
            bool capHitFi = false;
            if (out.targetFieldInfoPtr != 0 &&
                out.heapFieldInfoSlots.size() < heapSlotLimitPerPointer) {
                ScanRangeForPointer(base, reg.size, out.targetFieldInfoPtr,
                                    heapSlotLimitPerPointer,
                                    /*sectionName*/ "", reg.protect, reg.type,
                                    base, end,
                                    &out.heapFieldInfoSlots, &out.heapBytesScanned,
                                    &capHitFi);
            }
            if (capHitClass || capHitSf || capHitFi) out.heapCapHit = true;
            if (out.heapClassPtrSlots.size() >= heapSlotLimitPerPointer &&
                (out.staticFieldsPtr == 0 ||
                 out.heapStaticFieldsSlots.size() >= heapSlotLimitPerPointer) &&
                (out.targetFieldInfoPtr == 0 ||
                 out.heapFieldInfoSlots.size() >= heapSlotLimitPerPointer)) {
                break;  // all caps reached
            }
        }
    }

    // ------------------------------------------------------------------
    // Navigation-candidate extraction.
    //
    // For each heap class-ptr slot, scan its 128-byte context for any
    // qword that falls inside a GameAssembly.dll EXECUTABLE section
    // (.text or .il2cpp). Those are very likely AOT method pointers
    // (an IL2CPP MethodInfo's `methodPointer` field is at +0x00 of the
    // struct, and `klass` is a few qwords later — so for any heap hit
    // on `klass`, the methodPointer sits nearby in the same context
    // window).
    //
    // Dedup + cap so the user sees a manageable list. These are the
    // addresses to navigate to in CE Memory View to capture the AOT
    // disasm that v3 needs to design around.
    // ------------------------------------------------------------------
    {
        const std::uintptr_t gaLo = out.moduleBase;
        const std::uintptr_t gaHi = gaLo + out.moduleSize;
        // Walk just the EXECUTABLE sections — that's the filter we want
        // for AOT method pointers (don't surface .rdata strings etc).
        std::vector<std::pair<std::uintptr_t, std::uintptr_t>> execRanges;
        for (const auto& sec : out.sections) {
            if (!sec.isExecutable) continue;
            execRanges.emplace_back(sec.va, sec.va + sec.size);
        }
        auto inExecSection = [&](std::uintptr_t v) {
            for (const auto& r : execRanges) {
                if (v >= r.first && v < r.second) return true;
            }
            return false;
        };
        (void)gaLo; (void)gaHi;

        std::vector<NavigationCandidate> cands;
        for (const auto& slot : out.heapClassPtrSlots) {
            if (slot.context.empty()) continue;
            const std::size_t qwords = slot.context.size() / sizeof(std::uintptr_t);
            for (std::size_t i = 0; i < qwords; ++i) {
                std::uintptr_t v = 0;
                std::memcpy(&v, slot.context.data() + i * sizeof(v), sizeof(v));
                if (inExecSection(v)) {
                    NavigationCandidate c;
                    c.methodPointer = v;
                    c.slotAddress   = slot.address;
                    cands.push_back(c);
                }
            }
        }
        // Sort + dedup by methodPointer (keep first slotAddress per ptr).
        std::sort(cands.begin(), cands.end(),
            [](const NavigationCandidate& a, const NavigationCandidate& b) {
                return a.methodPointer < b.methodPointer;
            });
        cands.erase(
            std::unique(cands.begin(), cands.end(),
                [](const NavigationCandidate& a, const NavigationCandidate& b) {
                    return a.methodPointer == b.methodPointer;
                }),
            cands.end());
        // Cap at 16 — more than enough to pick a sample for disasm.
        if (cands.size() > 16) cands.resize(16);
        out.navigationCandidates = std::move(cands);
    }

    Log::Write(MagicStrings::LogCategoryWalk,
        "ClassRefDiagnostic: class=0x" +
        [](std::uintptr_t v) {
            char b[24]; std::snprintf(b, sizeof(b), "%llX",
                static_cast<unsigned long long>(v));
            return std::string(b);
        }(out.classPtr) +
        " staticFields=0x" +
        [](std::uintptr_t v) {
            char b[24]; std::snprintf(b, sizeof(b), "%llX",
                static_cast<unsigned long long>(v));
            return std::string(b);
        }(out.staticFieldsPtr) +
        " fieldInfo=0x" +
        [](std::uintptr_t v) {
            char b[24]; std::snprintf(b, sizeof(b), "%llX",
                static_cast<unsigned long long>(v));
            return std::string(b);
        }(out.targetFieldInfoPtr) +
        " moduleClassSlots=" + std::to_string(out.moduleClassPtrSlots.size()) +
        " moduleSfSlots=" + std::to_string(out.moduleStaticFieldsSlots.size()) +
        " moduleFiSlots=" + std::to_string(out.moduleFieldInfoSlots.size()) +
        " heapClassSlots=" + std::to_string(out.heapClassPtrSlots.size()) +
        " heapSfSlots=" + std::to_string(out.heapStaticFieldsSlots.size()) +
        " heapFiSlots=" + std::to_string(out.heapFieldInfoSlots.size()) +
        " heapRegionsScanned=" + std::to_string(out.heapRegionsScanned) +
        " heapRegionsSkipped=" + std::to_string(out.heapRegionsSkipped) +
        " heapBytesScanned=" + std::to_string(out.heapBytesScanned));

    if (ScanCancel::IsCancelRequested()) {
        out.cancelled = true;
        out.ok = false;
        return out;
    }

    out.ok = true;
    return out;
}

}  // namespace Discrete::IL2CPP
