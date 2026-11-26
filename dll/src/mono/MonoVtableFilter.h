// MonoVtableFilter.h — MonoVTable* alignment and candidate screening (B-H1).
// Shared by MonoAddressFinder.cpp and offline x64/x86 C++ self-tests.
#pragma once

#include <cstddef>
#include <cstdint>

namespace Discrete::Mono {

// MonoVTable* is pointer-aligned (4 on x86, 8 on x64): on 32-bit Mono,
// classes implementing any interface prepend a 19-slot IMT (19 * 4 = 76 bytes,
// 4 mod 8) ahead of the vtable inside an 8-byte-aligned mempool.
inline constexpr std::uintptr_t kMonoVtableAlignMask    = alignof(void*) - 1;
inline constexpr std::uintptr_t kMinPlausibleVtableAddr = 0x10000u;

inline constexpr bool IsAlignedMonoVtablePtr(std::uintptr_t addr) noexcept {
    return addr != 0 && (addr & kMonoVtableAlignMask) == 0;
}

inline constexpr bool IsPlausibleMonoVtableCandidate(std::uintptr_t addr) noexcept {
    return IsAlignedMonoVtablePtr(addr) && addr >= kMinPlausibleVtableAddr;
}

}  // namespace Discrete::Mono
