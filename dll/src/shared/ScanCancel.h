// ScanCancel.h — cooperative cancellation for long value / heap scans.
//
// A value scan ("numeric all widths") can run for hundreds of seconds. It runs
// synchronously on the per-client pipe worker thread, so when the UI cancels or
// force-quits, the DLL must be told to STOP and free its work — otherwise the
// injected DLL keeps churning the game's CPU/memory until the scan finishes.
//
// Mechanism: a process-global cooperative flag the long loops poll. Set by the
// `cancelScan` RPC (explicit Cancel button) OR by the pipe server's per-client
// disconnect monitor (UI force-quit). A scan handler opens a Scope at the top,
// which clears any stale flag and marks a scan active; the loops bail on
// IsCancelRequested(). Resources free via RAII (the session is created only
// after the loops, so a cancel never leaks one).
//
// Global (not per-client): heavy scans are effectively serialized (DiscreteUI
// runs one at a time; the CE-Lua bridge doesn't value-scan), so a single flag is
// sufficient. RequestCancel() is gated on `g_scanActive` so a stray cancel can't
// arm the NEXT scan before it starts; worst case of collateral cancel between
// two concurrent clients is an empty result + a re-run.
#pragma once

#include <atomic>

namespace Discrete::ScanCancel {

inline std::atomic<bool> g_scanCancel{false};
inline std::atomic<bool> g_scanActive{false};

inline bool IsCancelRequested() noexcept {
    return g_scanCancel.load(std::memory_order_relaxed);
}

// Arm cancellation — but only while a scan is actually running, so a late
// cancelScan / a disconnect can't kill a future scan.
inline void RequestCancel() noexcept {
    if (g_scanActive.load(std::memory_order_relaxed))
        g_scanCancel.store(true, std::memory_order_relaxed);
}

// RAII for the duration of a scan handler: clear any stale flag + mark active.
struct Scope {
    Scope() noexcept {
        g_scanCancel.store(false, std::memory_order_relaxed);
        g_scanActive.store(true,  std::memory_order_relaxed);
    }
    ~Scope() noexcept {
        g_scanActive.store(false, std::memory_order_relaxed);
    }
    Scope(const Scope&)            = delete;
    Scope& operator=(const Scope&) = delete;
};

}  // namespace Discrete::ScanCancel
