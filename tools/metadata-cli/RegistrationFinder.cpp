// RegistrationFinder.cpp — Phase D step 3. See header for design.
//
// Algorithm mirror of dll/src/il2cpp/IL2CPPMetadataRegistration.cpp's
// `TestCandidate`, adapted for file bytes:
//
//   * pointer fields in the on-disk struct hold absolute VAs (i.e.
//     preferredImageBase + targetRVA). The runtime sees these already
//     relocated by the loader; we translate VA → RVA → file-offset
//     via the PE section table.
//
//   * Il2CppMetadataRegistration v29 layout (x64, 0x80 bytes):
//       0x00 i32 genericClassesCount      pad i32   ptr
//       0x10 i32 genericInstsCount        pad i32   ptr
//       0x20 i32 genericMethodTableCount  pad i32   ptr
//       0x30 i32 typesCount               pad i32   ptr  ← we care about this
//       0x40 i32 methodSpecsCount         pad i32   ptr
//       0x50 i32 fieldOffsetsCount        pad i32   ptr
//       0x60 i32 typeDefinitionsSizesCount pad i32  ptr
//       0x70 u64 metadataUsagesCount                ptr
//
//   * Validation: same set of structural checks as the runtime
//     (count < 1<<27 for the first 7, count < 1<<32 for usages,
//     pad == 0, ptr is null OR 8-aligned AND falls inside some
//     mapped section). Plus a types[] sample: 5 spread indices
//     deref → check Il2CppType tag byte at +10 is in [0x01..0x20].
//
// Output: the registration's RVA + types array RVA + count. The
// caller (CLI subcommand or, later, Phase D-4 method resolver)
// consumes those.

#include "RegistrationFinder.h"

#include <algorithm>
#include <cstring>

namespace Discrete::Phase_D::Registration {

namespace {

constexpr std::uint32_t kMaxCount32   = 1u << 27;      // 134M (matches runtime)
constexpr std::uint64_t kMaxCount64   = 1ull << 32;
constexpr std::size_t   kRegBytes     = 0x80;
constexpr std::uint32_t kMinTypesCnt  = 100;            // cull tiny false positives
constexpr int           kNearMissLogCap = 8;

// PE section characteristic bits. Avoid pulling in <windows.h> in
// this TU — we already get IMAGE_SCN_* via PEParser.cpp.
constexpr std::uint32_t kScn_Read   = 0x40000000u;
constexpr std::uint32_t kScn_Exec   = 0x20000000u;
constexpr std::uint32_t kScn_CntInit  = 0x00000040u;
constexpr std::uint32_t kScn_CntBss   = 0x00000080u;

// File-bytes read helper. Returns true + populates `dst` on success.
bool ReadAt(const std::uint8_t* bytes, std::size_t size,
            std::uint64_t offset, void* dst, std::size_t n) noexcept {
    if (offset > size || offset + n > size) return false;
    std::memcpy(dst, bytes + offset, n);
    return true;
}

// VA → file offset via section table. Returns nullopt if VA doesn't
// translate cleanly (zero, mis-aligned, outside any section's loaded
// range, or inside a section's virtual-only BSS extent).
std::optional<std::uint64_t> VaToFileOffset(const PE::Image& img, std::uint64_t va) noexcept {
    if (va < img.imageBase) return std::nullopt;
    std::uint64_t rva = va - img.imageBase;
    return img.RvaToFileOffset(rva);
}

// Sample one types[] entry. Returns:
//   1  — typePtr is non-null, deref reads a Il2CppType with a valid tag
//   0  — typePtr is null (acceptable; IL2CPP can leave deferred slots null)
//  -1  — typePtr is non-null but unreadable / mis-aligned / bad tag → reject candidate
int ValidateTypeEntry(const std::uint8_t* bytes, std::size_t size,
                       const PE::Image& img,
                       std::uint64_t typesArrayFileOff, std::size_t idx) noexcept {
    std::uint64_t typePtr = 0;
    if (!ReadAt(bytes, size, typesArrayFileOff + idx * 8, &typePtr, 8)) return -1;
    if (typePtr == 0) return 0;
    if ((typePtr & 7ull) != 0) return -1;
    auto typeFileOff = VaToFileOffset(img, typePtr);
    if (!typeFileOff) return -1;
    // Il2CppType tag byte sits at offset +10 of the struct.
    std::uint8_t tag = 0;
    if (!ReadAt(bytes, size, *typeFileOff + 10, &tag, 1)) return -1;
    // Il2CppTypeEnum is 0x00..0x20; the runtime scanner accepts 0x01..0x20
    // (0x00 = END = unused). Same cull here.
    return (tag >= 0x01 && tag <= 0x20) ? 1 : -1;
}

// Full structural + types-sample validation of one 0x80-byte window.
// `errOut` (optional) receives the first reject reason for diagnostics.
bool TestCandidate(const std::uint8_t* bytes, std::size_t size,
                   const PE::Image& img,
                   std::uint64_t windowFileOff,
                   std::string* errOut = nullptr) noexcept {
    auto reject = [&](const char* msg) {
        if (errOut) *errOut = msg;
        return false;
    };

    // First 7 pairs: i32 count, i32 zero pad, u64 ptr.
    for (int i = 0; i < 7; ++i) {
        std::uint64_t slotFileOff = windowFileOff + i * 16;
        std::uint32_t count = 0; if (!ReadAt(bytes, size, slotFileOff + 0, &count, 4)) return reject("read count failed");
        std::uint32_t pad   = 0; if (!ReadAt(bytes, size, slotFileOff + 4, &pad,   4)) return reject("read pad failed");
        std::uint64_t ptr   = 0; if (!ReadAt(bytes, size, slotFileOff + 8, &ptr,   8)) return reject("read ptr failed");
        if (count >= kMaxCount32) return reject("count too large");
        if (pad != 0)             return reject("padding nonzero");
        if (count == 0) {
            if (ptr != 0) return reject("count==0 but ptr!=0");
        } else {
            if (ptr == 0)            return reject("count!=0 but ptr==0");
            if ((ptr & 7ull) != 0)   return reject("ptr unaligned");
            // Must translate cleanly to a file offset.
            if (!VaToFileOffset(img, ptr)) return reject("ptr does not map to a section");
        }
    }

    // 8th pair: u64 count, u64 ptr.
    {
        std::uint64_t slotFileOff = windowFileOff + 7 * 16;
        std::uint64_t count = 0; if (!ReadAt(bytes, size, slotFileOff + 0, &count, 8)) return reject("read usages count failed");
        std::uint64_t ptr   = 0; if (!ReadAt(bytes, size, slotFileOff + 8, &ptr,   8)) return reject("read usages ptr failed");
        if (count >= kMaxCount64) return reject("usages count too large");
        if (count == 0) {
            if (ptr != 0) return reject("usages count==0 but ptr!=0");
        } else {
            if (ptr == 0)            return reject("usages count!=0 but ptr==0");
            if ((ptr & 7ull) != 0)   return reject("usages ptr unaligned");
            if (!VaToFileOffset(img, ptr)) return reject("usages ptr does not map");
        }
    }

    // typesCount sanity.
    std::uint32_t typesCount = 0;
    if (!ReadAt(bytes, size, windowFileOff + 0x30, &typesCount, 4)) return reject("read typesCount failed");
    if (typesCount < kMinTypesCnt) return reject("typesCount < kMin");

    // Sample 5 spread types[] indices.
    std::uint64_t typesVa = 0;
    if (!ReadAt(bytes, size, windowFileOff + 0x38, &typesVa, 8)) return reject("read typesVa failed");
    auto typesFileOff = VaToFileOffset(img, typesVa);
    if (!typesFileOff) return reject("types[] VA does not map");

    const std::size_t indices[] = {
        0,
        typesCount / 4u,
        typesCount / 2u,
        (typesCount * 3u) / 4u,
        typesCount - 1u,
    };
    int valid = 0;
    for (std::size_t idx : indices) {
        int v = ValidateTypeEntry(bytes, size, img, *typesFileOff, idx);
        if (v < 0) return reject("types[] sample invalid");
        if (v > 0) ++valid;
    }
    if (valid < 3) return reject("types[] not enough valid samples");
    return true;
}

}  // namespace

std::optional<Found> FindOffline(const std::uint8_t* bytes,
                                  std::size_t          size,
                                  const PE::Image&     img,
                                  std::string*         errOut) {
    if (bytes == nullptr || size == 0) {
        if (errOut) *errOut = "empty file";
        return std::nullopt;
    }
    if (img.machine != PE::Machine::Amd64) {
        // x86 has a different layout (4-byte pointers, ~0x40-byte stride).
        // Defer until we hit a 32-bit IL2CPP target — see header limitations.
        if (errOut) *errOut = "only x64 IL2CPP supported (x86 layout TBD)";
        return std::nullopt;
    }

    // Collect init-data sections. Mirror of the runtime's
    // ListInitDataSections: read access + init/bss data + NOT exec.
    // (Excludes .rsrc / .reloc by virtue of their characteristics.)
    std::vector<const PE::Section*> dataSections;
    for (const auto& s : img.sections) {
        std::uint32_t ch = s.characteristics;
        if (!(ch & kScn_Read))                     continue;
        if (ch & kScn_Exec)                        continue;
        if (!(ch & (kScn_CntInit | kScn_CntBss)))  continue;
        dataSections.push_back(&s);
    }
    if (dataSections.empty()) {
        if (errOut) *errOut = "no init-data sections to scan";
        return std::nullopt;
    }

    int nearMissCount = 0;
    for (const PE::Section* sec : dataSections) {
        if (sec->rawSize < kRegBytes) continue;
        std::uint64_t startFileOff = sec->fileOffset;
        std::uint64_t endFileOff   = startFileOff + sec->rawSize;
        if (endFileOff > size) endFileOff = size;
        if (endFileOff < startFileOff + kRegBytes) continue;

        // Iterate 8-byte aligned windows.
        for (std::uint64_t windowOff = startFileOff;
             windowOff + kRegBytes <= endFileOff;
             windowOff += 8) {
            // Cheap rejector: padding zero at offset 4, count not absurd.
            std::uint32_t firstPad = 0;
            if (!ReadAt(bytes, size, windowOff + 4, &firstPad, 4)) continue;
            if (firstPad != 0) continue;
            std::uint32_t firstCount = 0;
            if (!ReadAt(bytes, size, windowOff, &firstCount, 4)) continue;
            if (firstCount >= kMaxCount32) continue;

            std::string why;
            if (!TestCandidate(bytes, size, img, windowOff, &why)) {
                // Count near-misses where typesCount is plausible.
                std::uint32_t tc = 0;
                if (ReadAt(bytes, size, windowOff + 0x30, &tc, 4) &&
                    tc >= kMinTypesCnt) {
                    if (nearMissCount < kNearMissLogCap) {
                        ++nearMissCount;
                    }
                }
                continue;
            }

            // Hit.
            Found out;
            std::uint64_t rva = 0;
            if (auto maybeRva = img.FileOffsetToRva(windowOff); maybeRva) {
                rva = *maybeRva;
            }
            std::uint32_t typesCnt = 0;
            std::uint64_t typesVa  = 0;
            ReadAt(bytes, size, windowOff + 0x30, &typesCnt, 4);
            ReadAt(bytes, size, windowOff + 0x38, &typesVa,  8);
            out.regRva        = rva;
            out.typesCount    = typesCnt;
            out.typesArrayVa  = typesVa;
            out.typesArrayRva = typesVa - img.imageBase;
            out.nearMissCount = nearMissCount;
            out.sectionName   = sec->name;
            return out;
        }
    }

    if (errOut) {
        *errOut = "no Il2CppMetadataRegistration candidate matched the structural shape";
    }
    return std::nullopt;
}

}  // namespace Discrete::Phase_D::Registration
