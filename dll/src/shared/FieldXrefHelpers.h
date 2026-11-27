// FieldXrefHelpers.h — shared B-M8 / B-M9 / B-L7 / B-L11 logic for
// MonoFieldXref and IL2CPPFieldXref.
//
// Header-only and runtime-independent so dll_core_test can exercise MemberRef
// matching, x86 IL switch overflow protection, .pdata-bounded enclosing-method
// attribution, and IL2CPP value-type instance-offset adjustment on both x64
// and x86 without a live Unity runtime.
#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::FieldXref {

// ECMA-335 metadata token table prefixes (high byte).
inline constexpr std::uint32_t kTokenTableMask = 0xFF000000u;
inline constexpr std::uint32_t kTokenFieldDef  = 0x04000000u;
inline constexpr std::uint32_t kTokenMemberRef = 0x0A000000u;

inline constexpr bool IsFieldDefToken(std::uint32_t tok) noexcept {
    return (tok & kTokenTableMask) == kTokenFieldDef;
}

inline constexpr bool IsMemberRefToken(std::uint32_t tok) noexcept {
    return (tok & kTokenTableMask) == kTokenMemberRef;
}

inline bool IsGenericTypeName(const std::string& className) noexcept {
    return className.find('`') != std::string::npos;
}

// B-L11: overflow-safe IL `switch` instruction advance (1 opcode + 4-byte
// count + 4 * count int32 jump targets). Returns 0 if truncated or if
// `count * 4` would overflow size_t (notably on 32-bit x86).
inline constexpr std::size_t SafeIlSwitchAdvance(std::uint32_t count,
                                                 std::size_t remaining) noexcept {
    if (remaining < 5u) return 0u;
    const std::size_t maxCount = (remaining - 5u) / 4u;
    if (static_cast<std::size_t>(count) > maxCount) return 0u;
    return 5u + static_cast<std::size_t>(count) * 4u;
}

// B-M8: decide whether a Mono MemberRef (`0x0Axxxxxx`) resolved via
// `mono_field_from_token` refers to the same field definition as `target`.
// Handles:
//   1. Exact MonoClassField* pointer equality (non-generic MemberRef)
//   2. Same-image FieldDef token equality (inflated generic field sharing
//      its generic definition's 0x04xxxxxx token)
//   3. Same-image + same declaring definition `(ns, name)` + same `fieldName`
//      (inflated generic class whose MonoClass* differs from the open generic
//      definition and whose field token may be 0 on some Mono builds).
struct MonoFieldIdentity {
    const void*   fieldPtr   = nullptr;
    const void*   imagePtr   = nullptr;
    std::uint32_t fieldToken = 0;
    std::string   ns;
    std::string   className;  // "Outer/Inner" form (e.g. "Singleton`1")
    std::string   fieldName;
};

inline bool MatchesMonoFieldIdentity(const MonoFieldIdentity& candidate,
                                     const MonoFieldIdentity& target) noexcept {
    if (candidate.fieldPtr == nullptr || target.fieldPtr == nullptr) return false;
    if (candidate.fieldPtr == target.fieldPtr) return true;
    if (candidate.imagePtr == nullptr || candidate.imagePtr != target.imagePtr) return false;
    if (target.fieldToken != 0 && candidate.fieldToken == target.fieldToken) return true;
    if (!target.className.empty() && !target.fieldName.empty() &&
        candidate.ns == target.ns &&
        candidate.className == target.className &&
        candidate.fieldName == target.fieldName) {
        return true;
    }
    return false;
}

// B-M9: exact enclosing-method attribution with `.pdata` function bounds.
//
// `Entry` must expose `std::uintptr_t address`. `idx` must be sorted ascending
// by `address`. `lookupBounds(addr, outBegin, outEnd)` returns true iff the
// runtime/PE unwind table (e.g. Win64 `RtlLookupFunctionEntry`) knows the exact
// `[outBegin, outEnd)` range containing `addr`.
//
// Rules:
//   1. If `lookupBounds(ip)` succeeds: `ip` MUST lie in `[funcBegin, funcEnd)`
//      and `idx` MUST contain an entry with `entry.address == funcBegin`. Any
//      hit inside another assembly's interleaved AOT function has a different
//      `funcBegin` and is cleanly rejected instead of misattributed to the
//      preceding method in `idx`.
//   2. If `lookupBounds(ip)` returns false (rare leaf function with no .pdata):
//      find the largest `entry.address <= ip`, require `ip - entry.address <
//      kLeafFallbackMaxBytes`, AND reject if `entry.address` itself HAS a
//      `.pdata` entry whose `end <= ip` (proving `ip` lies past `entry`'s end).
inline constexpr std::size_t kLeafFallbackMaxBytes = 256u;

template <typename Entry, typename LookupBoundsFn>
inline const Entry* FindEnclosingMethodExact(const std::vector<Entry>& idx,
                                             std::uintptr_t ip,
                                             LookupBoundsFn&& lookupBounds) {
    if (idx.empty()) return nullptr;

    std::uintptr_t funcBegin = 0;
    std::uintptr_t funcEnd   = 0;
    if (lookupBounds(ip, funcBegin, funcEnd)) {
        if (ip < funcBegin || ip >= funcEnd) return nullptr;
        auto it = std::lower_bound(
            idx.begin(), idx.end(), funcBegin,
            [](const Entry& e, std::uintptr_t value) {
                return e.address < value;
            });
        if (it != idx.end() && it->address == funcBegin) {
            return &(*it);
        }
        // Hit lies inside a known function whose entry is NOT in this image's
        // method index (e.g. another assembly or runtime helper interleaved in
        // GameAssembly.dll .text) — reject cleanly.
        return nullptr;
    }

    // Rare leaf-function fallback (no .pdata entry covers `ip`).
    auto it = std::upper_bound(
        idx.begin(), idx.end(), ip,
        [](std::uintptr_t value, const Entry& e) {
            return value < e.address;
        });
    if (it == idx.begin()) return nullptr;
    --it;
    if (ip - it->address >= kLeafFallbackMaxBytes) return nullptr;

    // If the candidate entry itself has .pdata bounds that end at or before
    // `ip`, then `ip` belongs to a subsequent un-indexed leaf function, NOT
    // this entry.
    std::uintptr_t entryBegin = 0;
    std::uintptr_t entryEnd   = 0;
    if (lookupBounds(it->address, entryBegin, entryEnd) && ip >= entryEnd) {
        return nullptr;
    }
    return &(*it);
}

// B-L7: IL2CPP `il2cpp_field_get_offset` includes the boxed Il2CppObject header
// (`kObjectHeader`, 0x10 on x64 / 0x08 on x86) even on value types (structs),
// whereas AOT instance methods on structs receive an UNBOXED `this` pointer.
// Subtract `objectHeaderBytes` for value-type declaring classes so the AOT
// displacement scanner matches the real `[this + disp]` encoding.
inline constexpr std::size_t ComputeIl2CppInstanceScanOffset(
    std::size_t metadataFieldOffset,
    bool declaringClassIsValueType,
    std::size_t objectHeaderBytes) noexcept {
    if (declaringClassIsValueType && metadataFieldOffset >= objectHeaderBytes) {
        return metadataFieldOffset - objectHeaderBytes;
    }
    return metadataFieldOffset;
}

}  // namespace Discrete::FieldXref
