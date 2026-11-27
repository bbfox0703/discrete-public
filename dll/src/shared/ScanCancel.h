// ScanCancel.h — cooperative cancellation for long value / heap scans.
//
// A value scan ("numeric all widths") can run for hundreds of seconds. It runs
// synchronously on the per-client pipe worker thread, so when the UI cancels or
// force-quits, the DLL must be told to STOP and free its work — otherwise the
// injected DLL keeps churning the game's CPU/memory until the scan finishes.
//
// State packing (A-L7):
//   Active scope count and cancel flag live in a single atomic uint64_t:
//     bits [63:1] = active Scope count
//     bit  [0]    = cancel requested flag
//   Packing both into one word makes RequestCancel() ("arm only if active > 0")
//   and Scope::~Scope() ("clear cancel atomically when last scope exits")
//   strictly atomic with zero check-then-store TOCTOU window.
//
// Per-client tracking (A-M1):
//   Each PipeServer worker binds a stack-local `std::atomic<int>` via
//   `ClientSlotGuard` for the lifetime of ServeClient. `Scope` increments and
//   decrements the current thread's bound slot counter alongside the global
//   active count. The client's disconnect monitor polls ONLY its own client
//   counter, so a second client (e.g. a CE bridge burst) disconnecting during a
//   non-scan RPC never probes or cancels another client's active scan.
#pragma once

#include <atomic>
#include <cstdint>

namespace Discrete::ScanCancel {

namespace detail {
// Packed state: (activeCount << 1) | cancelBit.
inline std::atomic<std::uint64_t> g_state{0u};

// Per-worker-thread pointer to the enclosing ServeClient's active scan depth.
inline thread_local std::atomic<int>* t_clientScanDepth = nullptr;
}  // namespace detail

inline bool IsCancelRequested() noexcept {
    return (detail::g_state.load(std::memory_order_relaxed) & 1ull) != 0ull;
}

inline std::uint64_t ActiveCount() noexcept {
    return detail::g_state.load(std::memory_order_relaxed) >> 1u;
}

// Arm cancellation — atomically gated on activeCount > 0 so a late cancelScan
// or disconnect can never leave a stale armed flag for a future scan (A-L7).
inline void RequestCancel() noexcept {
    std::uint64_t cur = detail::g_state.load(std::memory_order_acquire);
    while ((cur >> 1u) > 0ull) {
        if ((cur & 1ull) != 0ull) return; // already armed
        if (detail::g_state.compare_exchange_weak(
                cur, cur | 1ull,
                std::memory_order_acq_rel, std::memory_order_acquire)) {
            return;
        }
    }
}

// Binds the calling client worker thread to its own per-client scan-depth
// counter (observed by that client's disconnect monitor in PipeServer.cpp).
struct ClientSlotGuard {
    std::atomic<int>* prev_ = nullptr;

    explicit ClientSlotGuard(std::atomic<int>& clientScanDepth) noexcept
        : prev_(detail::t_clientScanDepth) {
        detail::t_clientScanDepth = &clientScanDepth;
    }
    ~ClientSlotGuard() noexcept {
        detail::t_clientScanDepth = prev_;
    }
    ClientSlotGuard(const ClientSlotGuard&)            = delete;
    ClientSlotGuard& operator=(const ClientSlotGuard&) = delete;
};

// RAII for the duration of a scan handler: clear any stale flag on outermost
// entry, mark active globally + on this client's slot, and clear atomically
// when the last scope exits.
struct Scope {
    std::atomic<int>* boundClient_ = nullptr;

    Scope() noexcept : boundClient_(detail::t_clientScanDepth) {
        std::uint64_t cur = detail::g_state.load(std::memory_order_acquire);
        for (;;) {
            const std::uint64_t count = cur >> 1u;
            // Outermost scope clears any stale cancel bit; nested/overlapping
            // scopes preserve an already-armed cancel bit.
            const std::uint64_t cancelBit = (count == 0ull) ? 0ull : (cur & 1ull);
            const std::uint64_t next      = ((count + 1ull) << 1u) | cancelBit;
            if (detail::g_state.compare_exchange_weak(
                    cur, next,
                    std::memory_order_acq_rel, std::memory_order_acquire)) {
                break;
            }
        }
        if (boundClient_ != nullptr) {
            boundClient_->fetch_add(1, std::memory_order_acq_rel);
        }
    }

    ~Scope() noexcept {
        if (boundClient_ != nullptr) {
            boundClient_->fetch_sub(1, std::memory_order_acq_rel);
        }
        std::uint64_t cur = detail::g_state.load(std::memory_order_acquire);
        for (;;) {
            const std::uint64_t count = cur >> 1u;
            // Atomically drop the count AND clear cancel bit when the last
            // active scope leaves (zero TOCTOU window against RequestCancel).
            const std::uint64_t next =
                (count <= 1ull) ? 0ull : (((count - 1ull) << 1u) | (cur & 1ull));
            if (detail::g_state.compare_exchange_weak(
                    cur, next,
                    std::memory_order_acq_rel, std::memory_order_acquire)) {
                break;
            }
        }
    }

    Scope(const Scope&)            = delete;
    Scope& operator=(const Scope&) = delete;
};

}  // namespace Discrete::ScanCancel
