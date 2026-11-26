// MonoVtableFilter.h — MonoVTable* alignment and structural validation
// (B-H1 + B-M11). Shared by MonoAddressFinder.cpp and offline x64/x86 C++
// self-tests so WhatIsAt, FindAllInstances, and ReverseLookupIndex enforce
// identical vtable criteria.
#pragma once

#include <cstddef>
#include <cstdint>

namespace Discrete::Mono {

// MonoVTable* is pointer-aligned (4 on x86, 8 on x64): on 32-bit Mono,
// classes implementing any interface prepend a 19-slot IMT (19 * 4 = 76 bytes,
// 4 mod 8) ahead of the vtable inside an 8-byte-aligned mempool.
inline constexpr std::uintptr_t kMonoVtableAlignMask    = alignof(void*) - 1;
inline constexpr std::uintptr_t kMinPlausibleVtableAddr = 0x10000u;
inline constexpr std::size_t    kMonoVtableDomainOffset = 2u * sizeof(std::uintptr_t);

inline constexpr bool IsAlignedMonoVtablePtr(std::uintptr_t addr) noexcept {
    return addr != 0 && (addr & kMonoVtableAlignMask) == 0;
}

inline constexpr bool IsPlausibleMonoVtableCandidate(std::uintptr_t addr) noexcept {
    return IsAlignedMonoVtablePtr(addr) && addr >= kMinPlausibleVtableAddr;
}

// B-M11 — unified structural check for a MonoVTable header:
//   * vtAddr is pointer-aligned and >= 0x10000
//   * vt[+0] (MonoClass*) is 8-byte aligned and >= 0x10000
//   * vt[+2*ptr] (MonoDomain*) equals the process's non-null rootDomainAddr
// Rejects MonoType / RuntimeTypeHandle / internal structs whose +0 holds a
// MonoClass* across WhatIsAt, FindAllInstances, and ReverseLookupIndex alike.
inline constexpr bool ValidateMonoVtableHeader(std::uintptr_t vtAddr,
                                               std::uintptr_t klassAt0,
                                               std::uintptr_t domainAt2Ptr,
                                               std::uintptr_t rootDomainAddr) noexcept {
    if (!IsPlausibleMonoVtableCandidate(vtAddr)) return false;
    if (klassAt0 < kMinPlausibleVtableAddr || (klassAt0 & 7u) != 0) return false;
    if (rootDomainAddr == 0 || domainAt2Ptr != rootDomainAddr) return false;
    return true;
}

}  // namespace Discrete::Mono
