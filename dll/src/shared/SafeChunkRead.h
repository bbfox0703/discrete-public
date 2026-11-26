// SafeChunkRead.h — SEH-safe PE data-section enumeration and chunked memory
// reading with page-level fallback for anti-cheat page-stripped sections
// (B-M7 + avoids B-L5's whole-chunk loss on a single PAGE_NOACCESS page).
#pragma once

#include "Memory.h"

#include <windows.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

namespace Discrete::SafePeScan {

struct SectionRange {
    const std::uint8_t* base = nullptr;
    std::size_t         size = 0;
};

// Enumerate readable non-executable data sections of a PE module using
// Memory::SafeRead so a stripped/corrupt PE header can never AV.
inline std::vector<SectionRange> ListInitDataSectionsSafe(const std::uint8_t* moduleBase) {
    std::vector<SectionRange> out;
    if (moduleBase == nullptr) return out;

    IMAGE_DOS_HEADER dos{};
    if (!Memory::SafeReadBytes(moduleBase, &dos, sizeof(dos))) return out;
    if (dos.e_magic != IMAGE_DOS_SIGNATURE) return out;
    if (dos.e_lfanew <= 0 || static_cast<std::size_t>(dos.e_lfanew) > 0x10000000u) return out;

    const std::uint8_t* ntBase = moduleBase + static_cast<std::size_t>(dos.e_lfanew);
    DWORD ntSig = 0;
    if (!Memory::SafeReadBytes(ntBase, &ntSig, sizeof(ntSig)) || ntSig != IMAGE_NT_SIGNATURE) return out;

    IMAGE_FILE_HEADER fileHdr{};
    if (!Memory::SafeReadBytes(ntBase + sizeof(DWORD), &fileHdr, sizeof(fileHdr))) return out;

    const std::size_t secCount = std::min<std::size_t>(fileHdr.NumberOfSections, 256u);
    const std::uint8_t* secBase =
        ntBase + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER) + fileHdr.SizeOfOptionalHeader;

    constexpr DWORD kDataMask =
        IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_CNT_UNINITIALIZED_DATA;

    for (std::size_t i = 0; i < secCount; ++i) {
        IMAGE_SECTION_HEADER sec{};
        if (!Memory::SafeReadBytes(secBase + i * sizeof(IMAGE_SECTION_HEADER), &sec, sizeof(sec))) break;
        const DWORD ch = sec.Characteristics;
        if (!(ch & IMAGE_SCN_MEM_READ)) continue;
        if (ch & IMAGE_SCN_MEM_EXECUTE) continue;
        if (!(ch & kDataMask)) continue;
        if (sec.Misc.VirtualSize == 0) continue;
        SectionRange sr{};
        sr.base = moduleBase + sec.VirtualAddress;
        sr.size = sec.Misc.VirtualSize;
        out.push_back(sr);
    }
    return out;
}

// Read `[base, base + size)` in chunks of `chunkBytes` (with `overlapBytes`
// overlap across consecutive chunks) via Memory::SafeReadBytes. When a large
// chunk read fails because an anti-cheat stripped one or more pages inside it,
// falls back to 4 KiB page-by-page reads and invokes `onSpan(localPtr, realAddr,
// spanLen)` for each contiguous readable sub-span — never AVing and never
// losing readable pages adjacent to a stripped page.
//
// `onSpan` returns `true` to continue scanning, `false` to stop early.
template <typename OnSpanFn>
inline void ScanReadableSpans(const std::uint8_t* base,
                              std::size_t size,
                              std::size_t chunkBytes,
                              std::size_t overlapBytes,
                              std::vector<std::uint8_t>& scratch,
                              OnSpanFn&& onSpan) {
    if (base == nullptr || size == 0 || chunkBytes == 0) return;
    if (overlapBytes >= chunkBytes) overlapBytes = 0;
    if (scratch.size() < chunkBytes) scratch.resize(chunkBytes);

    constexpr std::size_t kPageBytes = 4096u;
    std::size_t offset = 0;
    while (offset < size) {
        const std::size_t len = std::min<std::size_t>(chunkBytes, size - offset);
        const std::uint8_t* chunkAddr = base + offset;

        if (Memory::SafeReadBytes(chunkAddr, scratch.data(), len)) {
            if (!onSpan(scratch.data(), chunkAddr, len)) return;
        } else {
            // At least one page inside this chunk is unreadable (e.g. AC
            // PAGE_NOACCESS / PAGE_GUARD). Walk page-by-page and coalesce
            // contiguous readable pages so records crossing page boundaries
            // are preserved without touching bad pages.
            std::size_t pos = 0;
            std::size_t spanStart = 0;
            bool inSpan = false;
            while (pos < len) {
                const std::uintptr_t curAddr =
                    reinterpret_cast<std::uintptr_t>(chunkAddr + pos);
                const std::size_t toPageEnd = kPageBytes - (curAddr & (kPageBytes - 1u));
                const std::size_t step = std::min<std::size_t>(toPageEnd, len - pos);
                if (Memory::SafeReadBytes(chunkAddr + pos, scratch.data() + pos, step)) {
                    if (!inSpan) {
                        spanStart = pos;
                        inSpan = true;
                    }
                } else if (inSpan) {
                    if (!onSpan(scratch.data() + spanStart,
                                chunkAddr + spanStart,
                                pos - spanStart)) {
                        return;
                    }
                    inSpan = false;
                }
                pos += step;
            }
            if (inSpan) {
                if (!onSpan(scratch.data() + spanStart,
                            chunkAddr + spanStart,
                            len - spanStart)) {
                    return;
                }
            }
        }

        if (offset + len >= size) break;
        offset += (len - overlapBytes);
    }
}

}  // namespace Discrete::SafePeScan
