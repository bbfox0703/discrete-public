// ThrottledRetry.h — B-L1 shared retry-throttle policy for lazy runtime
// resolvers and offset/table discoveries: latch permanently on first success,
// and on failure throttle subsequent retry attempts so a missing export or
// early startup call neither locks out recovery forever (std::call_once) nor
// floods logs / wastes syscalls on every RPC.
#pragma once

#include <cstdint>

namespace Discrete::RetryPolicy {

inline constexpr std::uint64_t kDefaultRetryIntervalMs = 1000u;

// Returns true when no failure has been recorded yet (`lastFailTickMs == 0`)
// or at least `intervalMs` have elapsed since `lastFailTickMs` (using unsigned
// subtraction so GetTickCount64 wraparound is well-defined).
inline bool CanRetryNow(std::uint64_t lastFailTickMs,
                        std::uint64_t nowTickMs,
                        std::uint64_t intervalMs = kDefaultRetryIntervalMs) noexcept {
    if (lastFailTickMs == 0u) return true;
    return (nowTickMs - lastFailTickMs) >= intervalMs;
}

// Non-zero tick stamp helper (avoids colliding with the 0 "never failed"
// sentinel if GetTickCount64 ever returned 0).
inline std::uint64_t NonZeroTick(std::uint64_t nowTickMs) noexcept {
    return (nowTickMs == 0u) ? 1u : nowTickMs;
}

}  // namespace Discrete::RetryPolicy
