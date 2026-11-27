// AddressBounds.h — pure, runtime-independent helpers for B-H2:
//   1. Managed object byte-extent calculation (regular classes, SZARRAYs,
//      and System.String) + offset containment checking, so WhatIsAt and
//      ReverseLookupIndex never falsely attribute an out-of-bounds address
//      to a neighboring object on the heap.
//   2. Pure-memory dynamic klass validation (for inflated generic instances
//      such as List<T> / Dictionary<K,V> and runtime array klasses T[] absent
//      from TypeDef-only classSet) using self-calibrated struct offsets
//      (image, element_class, name) — zero runtime API calls on unvalidated
//      pointers.
//   3. Bounded ReverseLookupIndex interval lookup with backtracking over
//      embedded false-positive header pointers inside larger objects.
//
// Shared between MonoAddressFinder.cpp, IL2CPPAddressFinder.cpp, and the
// offline x64/x86 C++ self-test suite (dll/tests/dll_core_test.cpp).

#pragma once

#include "ManagedLayout.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <vector>

namespace Discrete::AddressBounds {

inline constexpr std::size_t kMaxPlausibleArrayLen  = 0x04000000u; // 64M elements
inline constexpr std::int32_t kMaxPlausibleStringLen = 16 * 1024 * 1024;
inline constexpr std::size_t kMaxExtentBacktrack    = 16u;

struct ObjectExtentSpec {
    bool         isArray       = false;
    bool         isString      = false;
    std::size_t  instanceSize  = 0;   // from class_instance_size / object_get_size; 0 = unknown
    std::size_t  arrayLength   = 0;   // max_length from SZARRAY header
    std::size_t  arrayElemSize = 0;   // element stride in bytes
    std::int32_t stringChars   = -1;  // UTF-16 char count from String header (< 0 = unknown)
    std::size_t  pointerSize   = ManagedLayout::kPointerSize;
};

/// Compute the byte extent `[0, extent)` of a managed object starting at its
/// header. Returns 0 when the object's size cannot be determined (caller treats
/// 0 as "unbounded" rather than falsely rejecting hits on stripped runtimes).
inline std::size_t ComputeObjectExtent(const ObjectExtentSpec& s) noexcept {
    const std::size_t ptr = (s.pointerSize == 4u) ? 4u : 8u;
    const std::size_t objHeader = 2u * ptr;

    if (s.isArray) {
        const std::size_t dataOff = 4u * ptr;
        if (s.arrayLength > kMaxPlausibleArrayLen) {
            // Corrupted / implausible length: bound strictly to the header so
            // arbitrary heap memory past the header is never swallowed.
            return dataOff;
        }
        const std::size_t elemSize = (s.arrayElemSize > 0u) ? s.arrayElemSize : ptr;
        constexpr std::size_t kMaxSize = (std::numeric_limits<std::size_t>::max)();
        if (s.arrayLength > (kMaxSize - dataOff) / elemSize) {
            return dataOff;
        }
        return dataOff + s.arrayLength * elemSize;
    }

    if (s.isString) {
        const std::size_t charsOff = objHeader + sizeof(std::int32_t);
        if (s.stringChars >= 0 && s.stringChars <= kMaxPlausibleStringLen) {
            // Include the trailing NUL UTF-16 code unit (+1).
            return charsOff + (static_cast<std::size_t>(s.stringChars) + 1u) * 2u;
        }
        if (s.instanceSize > 0u) {
            return (s.instanceSize >= objHeader) ? s.instanceSize : objHeader;
        }
        return 0u;
    }

    if (s.instanceSize == 0u) return 0u;
    return (s.instanceSize >= objHeader) ? s.instanceSize : objHeader;
}

/// True iff `fieldOffset` lies inside `[0, extent)`. When `extent == 0`
/// (runtime size API unavailable), returns true for any non-negative offset so
/// we degrade gracefully instead of dropping every lookup.
inline bool IsOffsetWithinExtent(std::ptrdiff_t fieldOffset,
                                 std::size_t extent) noexcept {
    if (fieldOffset < 0) return false;
    if (extent == 0u) return true;
    return static_cast<std::size_t>(fieldOffset) < extent;
}

// ---------------------------------------------------------------------------
// Self-calibrated klass field-offset discovery + pure-memory validation
// ---------------------------------------------------------------------------

struct KlassFieldOffsets {
    std::size_t imageOffset     = 0;
    bool        hasImageOffset  = false;
    std::size_t elemClassOffset = 0;
    bool        hasElemOffset   = false;
    std::size_t nameOffset      = 0;
    bool        hasNameOffset   = false;

    bool Ready() const noexcept {
        return hasImageOffset && hasElemOffset && hasNameOffset;
    }
};

/// Intersect a running candidate-offset set against one new class sample's
/// matching offsets. Keeps ascending order.
inline void IntersectCandidateOffsets(std::vector<std::size_t>& candidates,
                                      bool& seeded,
                                      const std::vector<std::size_t>& sampleHits) {
    if (sampleHits.empty()) {
        candidates.clear();
        seeded = true;
        return;
    }
    if (!seeded) {
        candidates = sampleHits;
        seeded = true;
        return;
    }
    std::vector<std::size_t> next;
    next.reserve(candidates.size());
    for (std::size_t off : candidates) {
        if (std::find(sampleHits.begin(), sampleHits.end(), off) != sampleHits.end()) {
            next.push_back(off);
        }
    }
    candidates.swap(next);
}

/// Validate that `buf[0..maxLen)` contains a NUL-terminated non-empty plausible
/// managed class name (printable ASCII or valid UTF-8 multibyte, no C0/DEL
/// control chars). Safe on arbitrary untrusted memory buffers.
inline bool LooksLikePlausibleClassName(const char* buf, std::size_t maxLen) noexcept {
    if (buf == nullptr || maxLen == 0u) return false;
    std::size_t len = 0u;
    while (len < maxLen && buf[len] != '\0') ++len;
    if (len == 0u || len == maxLen) return false; // empty or missing NUL

    for (std::size_t i = 0u; i < len; ) {
        const auto b0 = static_cast<unsigned char>(buf[i]);
        if (b0 < 0x20u || b0 == 0x7Fu) return false;
        if (b0 < 0x80u) {
            ++i;
            continue;
        }
        // Strict RFC 3629 UTF-8 multibyte sequence (2..4 bytes): rejects
        // overlong encodings, UTF-16 surrogates (U+D800..U+DFFF), and code
        // points above U+10FFFF while accepting CJK / Unicode identifiers (B-M5).
        std::size_t need = 0u;
        if ((b0 & 0xE0u) == 0xC0u && b0 >= 0xC2u)      need = 2u;
        else if ((b0 & 0xF0u) == 0xE0u)                need = 3u;
        else if ((b0 & 0xF8u) == 0xF0u && b0 <= 0xF4u) need = 4u;
        else return false;

        if (i + need > len) return false;
        const auto b1 = static_cast<unsigned char>(buf[i + 1u]);
        if ((b1 & 0xC0u) != 0x80u) return false;
        if (need == 3u) {
            if (b0 == 0xE0u && b1 < 0xA0u)  return false;  // overlong 3-byte
            if (b0 == 0xEDu && b1 >= 0xA0u) return false;  // UTF-16 surrogate
        } else if (need == 4u) {
            if (b0 == 0xF0u && b1 < 0x90u)  return false;  // overlong 4-byte
            if (b0 == 0xF4u && b1 >= 0x90u) return false;  // > U+10FFFF
        }
        for (std::size_t k = 2u; k < need; ++k) {
            const auto bk = static_cast<unsigned char>(buf[i + k]);
            if ((bk & 0xC0u) != 0x80u) return false;
        }
        i += need;
    }
    return true;
}

/// Pure-memory structural validation of a candidate dynamic MonoClass* /
/// Il2CppClass* that is absent from TypeDef-only `classSet` (such as inflated
/// generic instantiations `List<T>`, `Dictionary<K,V>` or array types `T[]`).
///
/// Calls ZERO runtime APIs on `klassPtr` — only invokes the caller-supplied
/// SafeRead callbacks and set membership predicates.
template <typename ReadPtrFn, typename ReadBytesFn,
          typename ContainsImageFn, typename ContainsClassFn>
bool ValidateDynamicKlassMemory(
    std::uintptr_t klassPtr,
    const KlassFieldOffsets& offsets,
    ReadPtrFn&& readPtr,
    ReadBytesFn&& readBytes,
    ContainsImageFn&& containsImage,
    ContainsClassFn&& containsClass,
    bool& outIsArray,
    std::uintptr_t& outElemClass) noexcept
{
    outIsArray   = false;
    outElemClass = 0u;

    if (!offsets.Ready()) return false;
    if (klassPtr < 0x10000u || (klassPtr & 7u) != 0u) return false;

    // 1. klass->image must point to a known loaded assembly image.
    std::uintptr_t imgPtr = 0u;
    if (!readPtr(klassPtr + offsets.imageOffset, imgPtr)) return false;
    if (!containsImage(imgPtr)) return false;

    // 2. klass->element_class must be either:
    //    - self (`klassPtr`) for a non-array generic instantiation (List<T>, Dictionary<K,V>, ...), OR
    //    - a valid element class (in TypeDef classSet, or itself carrying a known image,
    //      e.g. List<T>[] / int[][]) for an array klass.
    std::uintptr_t elemPtr = 0u;
    if (!readPtr(klassPtr + offsets.elemClassOffset, elemPtr)) return false;
    if (elemPtr == klassPtr) {
        outIsArray   = false;
        outElemClass = klassPtr;
    } else if (elemPtr >= 0x10000u && (elemPtr & 7u) == 0u) {
        bool elemOk = containsClass(elemPtr);
        if (!elemOk) {
            std::uintptr_t elemImg = 0u;
            if (readPtr(elemPtr + offsets.imageOffset, elemImg) &&
                containsImage(elemImg)) {
                elemOk = true;
            }
        }
        if (!elemOk) return false;
        outIsArray   = true;
        outElemClass = elemPtr;
    } else {
        return false;
    }

    // 3. klass->name must point to a plausible NUL-terminated class name.
    std::uintptr_t namePtr = 0u;
    if (!readPtr(klassPtr + offsets.nameOffset, namePtr)) return false;
    if (namePtr < 0x10000u) return false;

    char nameBuf[64] = {};
    if (!readBytes(namePtr, nameBuf, sizeof(nameBuf))) return false;
    // Guarantee NUL check happens strictly within the bytes actually read.
    if (!LooksLikePlausibleClassName(nameBuf, sizeof(nameBuf))) return false;

    return true;
}

// ---------------------------------------------------------------------------
// Bounded ReverseLookupIndex interval selection
// ---------------------------------------------------------------------------

template <typename Entry>
struct ContainingSelection {
    bool           found    = false;
    Entry          entry{};
    std::size_t    extent   = 0u;
    std::uintptr_t nextBase = 0u;
};

/// Select the nearest entry in ascending-sorted `bases` with `base <= start`
/// and `base >= lowest` whose byte extent actually contains `addr`.
///
/// Backtracks up to `kMaxExtentBacktrack` entries when a closer entry's extent
/// does not reach `addr` (e.g. an embedded klass/vtable pointer field inside a
/// larger enclosing object), while rejecting hits that fall past every nearby
/// object's end (preventing neighbor-object misattribution).
template <typename Entry, typename ExtentFn>
ContainingSelection<Entry> SelectContainingEntry(
    const std::vector<Entry>& bases,
    std::uintptr_t addr,
    std::uintptr_t start,
    std::uintptr_t lowest,
    ExtentFn&& extentFn)
{
    ContainingSelection<Entry> res;
    if (bases.empty()) return res;

    auto it = std::upper_bound(
        bases.begin(), bases.end(), start,
        [](std::uintptr_t val, const Entry& e) { return val < e.base; });
    const std::uintptr_t nextBase = (it != bases.end()) ? it->base : 0u;
    if (it == bases.begin()) return res;

    --it; // greatest base <= start
    for (std::size_t hops = 0u; hops < kMaxExtentBacktrack; ++hops) {
        if (it->base < lowest) break;
        if (addr >= it->base) {
            const std::ptrdiff_t offset = static_cast<std::ptrdiff_t>(addr - it->base);
            const std::size_t extent    = extentFn(*it);
            if (IsOffsetWithinExtent(offset, extent)) {
                res.found    = true;
                res.entry    = *it;
                res.extent   = extent;
                res.nextBase = (hops == 0u) ? nextBase : 0u;
                return res;
            }
        }
        if (it == bases.begin()) break;
        --it;
    }
    return res;
}

// ---------------------------------------------------------------------------
// B-L11: Unified backward-scan range calculation (near-zero clamp safety)
// ---------------------------------------------------------------------------

struct BackwardScanRange {
    std::uintptr_t start      = 0u;  // stride-aligned highest step address
    std::uintptr_t rangeStart = 0u;  // lowest step address (== scannedFrom)
    std::uintptr_t scannedTo  = 0u;  // start + stride (exclusive)
    std::size_t    maxSteps   = 0u;  // clamped so start >= (maxSteps - 1) * stride
    std::size_t    bulkBytes  = 0u;  // maxSteps * stride
};

/// Compute the backward scan window for `WhatIsAt` / `WhatIsAtAcceptingArrays`.
/// Clamps `maxSteps` near address 0 so `rangeStart == start - (maxSteps - 1) * stride`
/// never underflows, `scannedFrom == rangeStart`, and `bufIdx == p - rangeStart`
/// holds for every step `i in [0, maxSteps)`.
inline BackwardScanRange ComputeBackwardScanRange(
    std::uintptr_t addr,
    std::size_t    maxBackwardScan,
    std::size_t    stride = 8u) noexcept
{
    BackwardScanRange r;
    if (stride == 0u) stride = 8u;
    r.start = addr & ~static_cast<std::uintptr_t>(stride - 1u);
    std::size_t steps = maxBackwardScan / stride;
    if (steps == 0u) steps = 1u;
    const std::size_t maxPossibleSteps = static_cast<std::size_t>(r.start / stride) + 1u;
    if (steps > maxPossibleSteps) steps = maxPossibleSteps;
    r.maxSteps   = steps;
    r.bulkBytes  = steps * stride;
    r.rangeStart = r.start - (steps - 1u) * stride;
    r.scannedTo  = r.start + stride;
    return r;
}

}  // namespace Discrete::AddressBounds
