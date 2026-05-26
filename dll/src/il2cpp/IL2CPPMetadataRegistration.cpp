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
// Detection heuristic:
//   * Each of the first 7 pairs has a plausible nonzero int32 count, a zero
//     padding word, and a non-null 8-aligned pointer.
//   * The 8th pair has a plausible 64-bit count and a non-null pointer.
//   * Final verification: deref types[0] and check the type-tag byte at
//     offset 10 is a valid `Il2CppTypeEnum` value (0x01..0x20).

#include "IL2CPPMetadataRegistration.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <cstring>
#include <mutex>
#include <vector>

namespace UnityCEDumper::IL2CPP::MetadataRegistration {

namespace {

std::once_flag g_once;
struct State {
    bool                    found            = false;
    const std::uint8_t*     reg              = nullptr;
    const void* const*      types            = nullptr;
    std::size_t             typesCount       = 0;
    // Phase 28b-v3.1 — metadataUsages array (registration + 0x78,
    // count at +0x70).
    const void* const*      metadataUsages   = nullptr;
    std::size_t             metadataUsagesCount = 0;
} g_state;

constexpr std::uint32_t kMaxCount32   = 1u << 27;        // 134M, generous
constexpr std::uint64_t kMaxCount64   = 1ull << 32;
constexpr std::size_t   kRegBytes     = 0x80;
// Real Unity games always have a substantial type system. SaGa F2R's
// metadata references type#15031+, so 100 is a comfortable lower bound that
// still kills the structural false positives observed in build #48
// (typesCount=1).
constexpr std::uint32_t kMinTypesCount = 100;

struct Section { const std::uint8_t* base; std::size_t size; };

std::vector<Section> ListInitDataSections(const std::uint8_t* moduleBase) {
    std::vector<Section> out;
    auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(moduleBase);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return out;
    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(moduleBase + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return out;
    auto* sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        const DWORD ch = sec->Characteristics;
        if (!(ch & IMAGE_SCN_MEM_READ)) continue;
        if (ch & IMAGE_SCN_MEM_EXECUTE) continue;
        // Initialized or uninitialized data only — skip rsrc / reloc.
        constexpr DWORD kDataMask =
            IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_CNT_UNINITIALIZED_DATA;
        if (!(ch & kDataMask)) continue;
        Section s{};
        s.base = moduleBase + sec->VirtualAddress;
        s.size = sec->Misc.VirtualSize;
        out.push_back(s);
    }
    return out;
}

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

bool TestCandidate(const std::uint8_t* p, std::string* whyNot = nullptr) {
    auto reject = [&](const char* msg) {
        if (whyNot) *whyNot = msg;
        return false;
    };
    // First 7 pairs: int32 count, int32 zero pad, ptr. Allow count=0 paired
    // with null ptr (some small games have no generic classes / generic insts).
    for (int i = 0; i < 7; ++i) {
        const std::uint8_t* slot = p + i * 16;
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
        const std::uint8_t* slot = p + 7 * 16;
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
    std::memcpy(&typesCount, p + 0x30, 4);
    if (typesCount < kMinTypesCount) return reject("typesCount < kMin");

    // Sample 5 spread indices in types[]. A real registration has at least
    // 3 of them pointing at valid Il2CppType structs; null entries are OK
    // (deferred init), but no entry should be unreadable / malformed.
    std::uintptr_t typesPtr = 0;
    std::memcpy(&typesPtr, p + 0x38, 8);
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

void DoFind() {
    auto* base = reinterpret_cast<const std::uint8_t*>(
        ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP));
    if (base == nullptr) {
        Log::Write(MagicStrings::LogCategoryInit,
            "MetadataRegistration: GameAssembly.dll not loaded");
        return;
    }

    auto sections = ListInitDataSections(base);
    std::size_t scanBytes = 0;
    for (const auto& s : sections) scanBytes += s.size;

    // Diagnostic: log up to N near-misses where typesCount looks plausible.
    constexpr int kMaxNearMissLogs = 8;
    int nearMisses = 0;

    for (const auto& sec : sections) {
        if (sec.size < kRegBytes) continue;
        const std::uint8_t* end = sec.base + sec.size - kRegBytes;
        for (const std::uint8_t* p = sec.base; p <= end; p += 8) {
            // Cheap rejector first: padding zero at offset 4. Don't reject
            // count==0 anymore — handled inside TestCandidate.
            std::uint32_t firstPad = 0;
            std::memcpy(&firstPad, p + 4, 4);
            if (firstPad != 0) continue;
            std::uint32_t firstCount = 0;
            std::memcpy(&firstCount, p, 4);
            if (firstCount >= kMaxCount32) continue;

            std::string why;
            if (!TestCandidate(p, &why)) {
                // Log near-misses: candidate where typesCount is plausible.
                std::uint32_t tc = 0;
                std::memcpy(&tc, p + 0x30, 4);
                if (tc >= kMinTypesCount && nearMisses < kMaxNearMissLogs) {
                    char line[256];
                    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                        "MetadataRegistration: near-miss @0x%p typesCount=%u: %s",
                        static_cast<const void*>(p), tc, why.c_str());
                    Log::Write(MagicStrings::LogCategoryInit, line);
                    ++nearMisses;
                }
                continue;
            }

            g_state.found = true;
            g_state.reg   = p;
            std::uint32_t typesCnt = 0;
            std::memcpy(&typesCnt, p + 0x30, 4);
            std::uintptr_t typesPtr = 0;
            std::memcpy(&typesPtr, p + 0x38, 8);
            g_state.typesCount = typesCnt;
            g_state.types = reinterpret_cast<const void* const*>(typesPtr);

            // Phase 28b-v3.1 — pull metadataUsages too (offsets 0x70/0x78).
            std::uint64_t usagesCnt = 0;
            std::memcpy(&usagesCnt, p + 0x70, 8);
            std::uintptr_t usagesPtr = 0;
            std::memcpy(&usagesPtr, p + 0x78, 8);
            g_state.metadataUsagesCount = static_cast<std::size_t>(usagesCnt);
            g_state.metadataUsages =
                reinterpret_cast<const void* const*>(usagesPtr);

            char line[256];
            ::_snprintf_s(line, sizeof(line), _TRUNCATE,
                "MetadataRegistration: hit at 0x%p, types=0x%p count=%u, "
                "metadataUsages=0x%p count=%llu "
                "(scanned %zu bytes across %zu sections)",
                static_cast<const void*>(p),
                reinterpret_cast<const void*>(typesPtr),
                typesCnt,
                reinterpret_cast<const void*>(usagesPtr),
                static_cast<unsigned long long>(usagesCnt),
                scanBytes, sections.size());
            Log::Write(MagicStrings::LogCategoryInit, line);
            return;
        }
    }

    char line[256];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
        "MetadataRegistration: NOT FOUND (scanned %zu bytes across %zu sections, "
        "%d near-misses logged)",
        scanBytes, sections.size(), nearMisses);
    Log::Write(MagicStrings::LogCategoryInit, line);
}

}  // namespace

bool Find() {
    std::call_once(g_once, DoFind);
    return g_state.found;
}

const void*        GetBase()                { Find(); return g_state.reg; }
const void* const* TypesArray()             { Find(); return g_state.types; }
std::size_t        TypesCount()             { Find(); return g_state.typesCount; }
const void* const* MetadataUsagesArray()    { Find(); return g_state.metadataUsages; }
std::size_t        MetadataUsagesCount()    { Find(); return g_state.metadataUsagesCount; }

std::uint8_t GetTypeTag(std::int32_t typeIndex) {
    if (!Find()) return 0;
    if (typeIndex < 0 ||
        static_cast<std::size_t>(typeIndex) >= g_state.typesCount) return 0;
    const void* type = g_state.types[typeIndex];
    if (type == nullptr) return 0;
    std::uint8_t tag = 0;
    if (!Memory::SafeReadBytes(
            reinterpret_cast<const std::uint8_t*>(type) + 10, &tag, 1)) {
        return 0;
    }
    return tag;
}

}  // namespace UnityCEDumper::IL2CPP::MetadataRegistration
