// IL2CPPMetadataRegistration.cpp — AOB-scan GameAssembly.dll for the
// Il2CppMetadataRegistration struct shape and cache the result.
//
// Layout reference (v29 / x64), 0x80 bytes total:
//
//   offset  0x00  int32  genericClassesCount
//   offset  0x04  int32  (padding zero)
//   offset  0x08  ptr    genericClasses
//   offset  0x10  int32  genericInstsCount
//   offset  0x14  int32  (padding zero)
//   offset  0x18  ptr    genericInsts
//   offset  0x20  int32  genericMethodTableCount
//   offset  0x24  int32  (padding)
//   offset  0x28  ptr    genericMethodTable
//   offset  0x30  int32  typesCount
//   offset  0x34  int32  (padding)
//   offset  0x38  ptr    types          ← what we ultimately want
//   offset  0x40  int32  methodSpecsCount
//   offset  0x44  int32  (padding)
//   offset  0x48  ptr    methodSpecs
//   offset  0x50  int32  fieldOffsetsCount
//   offset  0x54  int32  (padding)
//   offset  0x58  ptr    fieldOffsets
//   offset  0x60  int32  typeDefinitionsSizesCount
//   offset  0x64  int32  (padding)
//   offset  0x68  ptr    typeDefinitionsSizes
//   offset  0x70  size_t metadataUsagesCount     (8 bytes — no padding)
//   offset  0x78  ptr    metadataUsages
//
// B-M7 — every module read (PE headers, section scan chunks, types[] entries)
// goes through Memory::SafeRead / SafeReadBytes (1 MiB chunks with 128 B
// overlap and 4 KiB page-by-page fallback on AC-stripped pages) so PAGE_NOACCESS
// or PAGE_GUARD pages inside GameAssembly.dll never AV the game process.

#include "IL2CPPMetadataRegistration.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"
#include "../shared/SafeChunkRead.h"
#include "../shared/ThrottledRetry.h"

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

namespace Discrete::IL2CPP::MetadataRegistration {

namespace {

struct State {
    bool                    found               = false;
    const std::uint8_t*     reg                 = nullptr;
    const void* const*      types               = nullptr;
    std::size_t             typesCount          = 0;
    // Phase 28b-v3.1 — metadataUsages array (registration + 0x78,
    // count at +0x70).
    const void* const*      metadataUsages      = nullptr;
    std::size_t             metadataUsagesCount = 0;
};

// B-L1: Lock-free publish-once on success; 1s throttled retry on failure.
std::atomic<const State*> g_foundState{nullptr};
std::mutex                g_findMu;
std::uint64_t             g_lastFailTickMs = 0;

constexpr std::uint32_t kMaxCount32    = 1u << 27;        // 134M, generous
constexpr std::uint64_t kMaxCount64    = 1ull << 32;
constexpr std::size_t   kRegBytes      = 0x80;
constexpr std::size_t   kScanChunkBytes = 1024u * 1024u;  // 1 MiB
// Real Unity games always have a substantial type system. SaGa F2R's
// metadata references type#15031+, so 100 is a comfortable lower bound that
// still kills the structural false positives observed in build #48
// (typesCount=1).
constexpr std::uint32_t kMinTypesCount = 100;

// Validate one types[] entry. Returns:
//   1 = non-null pointer to a readable Il2CppType with a valid tag (good)
//   0 = null pointer (acceptable — IL2CPP can leave deferred slots null)
//  -1 = unreadable / wrong-shape / bad tag (kills the candidate)
int ValidateTypeEntry(const void* const* arr, std::size_t idx) {
    std::uintptr_t typePtr = 0;
    if (!Memory::SafeReadBytes(arr + idx, &typePtr, 8)) return -1;
    if (typePtr == 0) return 0;
    if ((typePtr & 7u) != 0) return -1;
    std::uint8_t tag = 0;
    if (!Memory::SafeReadBytes(
            reinterpret_cast<const std::uint8_t*>(typePtr) + 10, &tag, 1)) {
        return -1;
    }
    return (tag >= 0x01 && tag <= 0x20) ? 1 : -1;
}

// `localBuf` points into our SEH-copied local scratch buffer (at least
// kRegBytes readable), never directly into module memory.
bool TestCandidate(const std::uint8_t* localBuf, std::string* whyNot = nullptr) {
    auto reject = [&](const char* msg) {
        if (whyNot) *whyNot = msg;
        return false;
    };
    // First 7 pairs: int32 count, int32 zero pad, ptr. Allow count=0 paired
    // with null ptr (some small games have no generic classes / generic insts).
    for (int i = 0; i < 7; ++i) {
        const std::uint8_t* slot = localBuf + i * 16;
        std::uint32_t count = 0; std::memcpy(&count, slot,     4);
        std::uint32_t pad   = 0; std::memcpy(&pad,   slot + 4, 4);
        std::uintptr_t ptr  = 0; std::memcpy(&ptr,   slot + 8, 8);
        if (count >= kMaxCount32) return reject("count too large");
        if (pad != 0)             return reject("padding nonzero");
        if ((ptr & 7u) != 0)      return reject("ptr unaligned");
        if (count == 0) {
            if (ptr != 0) return reject("count==0 but ptr!=0");
        } else {
            if (ptr == 0) return reject("count!=0 but ptr==0");
            if (!Memory::IsReadable(reinterpret_cast<const void*>(ptr), 8))
                return reject("ptr unreadable");
        }
    }
    // 8th pair: u64 count, ptr.
    {
        const std::uint8_t* slot = localBuf + 7 * 16;
        std::uint64_t count = 0; std::memcpy(&count, slot,     8);
        std::uintptr_t ptr  = 0; std::memcpy(&ptr,   slot + 8, 8);
        if (count >= kMaxCount64)  return reject("usages count too large");
        if ((ptr & 7u) != 0)       return reject("usages ptr unaligned");
        if (count == 0) {
            if (ptr != 0) return reject("usages count==0 but ptr!=0");
        } else {
            if (ptr == 0) return reject("usages count!=0 but ptr==0");
            if (!Memory::IsReadable(reinterpret_cast<const void*>(ptr), 8))
                return reject("usages ptr unreadable");
        }
    }
    // Real Unity games have a sizeable type system.
    std::uint32_t typesCount = 0;
    std::memcpy(&typesCount, localBuf + 0x30, 4);
    if (typesCount < kMinTypesCount) return reject("typesCount < kMin");

    // Sample 5 spread indices in types[]. A real registration has at least
    // 3 of them pointing at valid Il2CppType structs; null entries are OK
    // (deferred init), but no entry should be unreadable / malformed.
    std::uintptr_t typesPtr = 0;
    std::memcpy(&typesPtr, localBuf + 0x38, 8);
    auto* types = reinterpret_cast<const void* const*>(typesPtr);
    const std::size_t indices[] = {
        0,
        typesCount / 4,
        typesCount / 2,
        (typesCount * 3) / 4,
        typesCount - 1,
    };
    int valid = 0, nullCount = 0;
    for (std::size_t idx : indices) {
        int v = ValidateTypeEntry(types, idx);
        if (v < 0) return reject("types[] sample invalid");
        if (v > 0) ++valid;
        else       ++nullCount;
    }
    if (valid < 3) return reject("types[] not enough valid samples");
    return true;
}

bool DoFind(State& outState) {
    auto* base = reinterpret_cast<const std::uint8_t*>(
        ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP));
    if (base == nullptr) {
        Log::Write(MagicStrings::LogCategoryInit,
            "MetadataRegistration: GameAssembly.dll not loaded");
        return false;
    }

    auto sections = SafePeScan::ListInitDataSectionsSafe(base);
    std::size_t scanBytes = 0;
    for (const auto& s : sections) scanBytes += s.size;

    // Diagnostic: log up to N near-misses where typesCount looks plausible.
    constexpr int kMaxNearMissLogs = 8;
    int nearMisses = 0;
    std::vector<std::uint8_t> scratch;
    scratch.reserve(kScanChunkBytes);

    for (const auto& sec : sections) {
        if (sec.size < kRegBytes) continue;
        bool hitFound = false;

        SafePeScan::ScanReadableSpans(
            sec.base, sec.size, kScanChunkBytes, kRegBytes, scratch,
            [&](const std::uint8_t* localSpan,
                const std::uint8_t* realSpanBase,
                std::size_t spanLen) -> bool {
                if (spanLen < kRegBytes) return true;
                // Align first candidate offset to an 8-byte boundary in the
                // module's address space.
                const std::uintptr_t realAddr0 = reinterpret_cast<std::uintptr_t>(realSpanBase);
                const std::size_t alignPad = (8u - (realAddr0 & 7u)) & 7u;
                if (alignPad + kRegBytes > spanLen) return true;

                const std::size_t maxOff = spanLen - kRegBytes;
                for (std::size_t off = alignPad; off <= maxOff; off += 8) {
                    const std::uint8_t* pLocal = localSpan + off;
                    const std::uint8_t* pReal  = realSpanBase + off;

                    // Cheap rejector first: padding zero at offset 4.
                    std::uint32_t firstPad = 0;
                    std::memcpy(&firstPad, pLocal + 4, 4);
                    if (firstPad != 0) continue;
                    std::uint32_t firstCount = 0;
                    std::memcpy(&firstCount, pLocal, 4);
                    if (firstCount >= kMaxCount32) continue;

                    std::string why;
                    if (!TestCandidate(pLocal, &why)) {
                        std::uint32_t tc = 0;
                        std::memcpy(&tc, pLocal + 0x30, 4);
                        if (tc >= kMinTypesCount && nearMisses < kMaxNearMissLogs) {
                            char line[256];
                            ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                                "MetadataRegistration: near-miss @0x%p typesCount=%u: %s",
                                static_cast<const void*>(pReal), tc, why.c_str());
                            Log::Write(MagicStrings::LogCategoryInit, line);
                            ++nearMisses;
                        }
                        continue;
                    }

                    outState.found = true;
                    outState.reg   = pReal;
                    std::uint32_t typesCnt = 0;
                    std::memcpy(&typesCnt, pLocal + 0x30, 4);
                    std::uintptr_t typesPtr = 0;
                    std::memcpy(&typesPtr, pLocal + 0x38, 8);
                    outState.typesCount = typesCnt;
                    outState.types = reinterpret_cast<const void* const*>(typesPtr);

                    // Phase 28b-v3.1 — pull metadataUsages too (offsets 0x70/0x78).
                    std::uint64_t usagesCnt = 0;
                    std::memcpy(&usagesCnt, pLocal + 0x70, 8);
                    std::uintptr_t usagesPtr = 0;
                    std::memcpy(&usagesPtr, pLocal + 0x78, 8);
                    outState.metadataUsagesCount = static_cast<std::size_t>(usagesCnt);
                    outState.metadataUsages =
                        reinterpret_cast<const void* const*>(usagesPtr);

                    char line[256];
                    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                        "MetadataRegistration: hit at 0x%p, types=0x%p count=%u, "
                        "metadataUsages=0x%p count=%llu "
                        "(scanned %zu bytes across %zu sections)",
                        static_cast<const void*>(pReal),
                        reinterpret_cast<const void*>(typesPtr),
                        typesCnt,
                        reinterpret_cast<const void*>(usagesPtr),
                        static_cast<unsigned long long>(usagesCnt),
                        scanBytes, sections.size());
                    Log::Write(MagicStrings::LogCategoryInit, line);
                    hitFound = true;
                    return false;  // stop scanning
                }
                return true;
            });

        if (hitFound) return true;
    }

    char line[256];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
        "MetadataRegistration: NOT FOUND (scanned %zu bytes across %zu sections, "
        "%d near-misses logged)",
        scanBytes, sections.size(), nearMisses);
    Log::Write(MagicStrings::LogCategoryInit, line);
    return false;
}

const State* EnsureFound() {
    if (const State* p = g_foundState.load(std::memory_order_acquire)) {
        return p;
    }
    std::lock_guard<std::mutex> lk(g_findMu);
    if (const State* p = g_foundState.load(std::memory_order_relaxed)) {
        return p;
    }
    const std::uint64_t now = ::GetTickCount64();
    if (!RetryPolicy::CanRetryNow(g_lastFailTickMs, now)) {
        return nullptr;
    }
    State local;
    if (DoFind(local) && local.found) {
        auto* published = new State(local);
        g_foundState.store(published, std::memory_order_release);
        return published;
    }
    g_lastFailTickMs = RetryPolicy::NonZeroTick(::GetTickCount64());
    return nullptr;
}

}  // namespace

bool Find() {
    return EnsureFound() != nullptr;
}

const void*        GetBase()                { const State* s = EnsureFound(); return s ? s->reg                 : nullptr; }
const void* const* TypesArray()             { const State* s = EnsureFound(); return s ? s->types               : nullptr; }
std::size_t        TypesCount()             { const State* s = EnsureFound(); return s ? s->typesCount          : 0u; }
const void* const* MetadataUsagesArray()    { const State* s = EnsureFound(); return s ? s->metadataUsages      : nullptr; }
std::size_t        MetadataUsagesCount()    { const State* s = EnsureFound(); return s ? s->metadataUsagesCount : 0u; }

std::uint8_t GetTypeTag(std::int32_t typeIndex) {
    const State* s = EnsureFound();
    if (s == nullptr) return 0;
    if (s->types == nullptr || typeIndex < 0 ||
        static_cast<std::size_t>(typeIndex) >= s->typesCount) {
        return 0;
    }
    const void* type = nullptr;
    if (!Memory::SafeReadBytes(s->types + typeIndex, &type, sizeof(type))) {
        return 0;
    }
    if (type == nullptr) return 0;
    std::uint8_t tag = 0;
    if (!Memory::SafeReadBytes(
            reinterpret_cast<const std::uint8_t*>(type) + 10, &tag, 1)) {
        return 0;
    }
    return tag;
}

}  // namespace Discrete::IL2CPP::MetadataRegistration
