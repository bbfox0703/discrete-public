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
// The Scope clears the cancel flag on BOTH ends (ctor and dtor) so it can never
// stay armed past the scan that owns it. Handlers may therefore still poll
// IsCancelRequested() anywhere inside the Scope — including after the loops, to
// answer `{"cancelled": true}` — because the dtor only runs once the handler
// returns. Code that polls WITHOUT owning a Scope must still treat a set flag as
// advisory and must never publish partial results built under one.
//
// Global (not per-client): one flag, so a cancel applies to whatever scan is
// running rather than to a particular client. RequestCancel() is gated on there
// being a live Scope, so a stray cancel can't arm the NEXT scan before it starts.
//
// `g_scanActive` is a COUNT, not a bool, because scans genuinely do overlap — a
// second pipe client (the harness, a CE bridge, a second DiscreteUI) can start
// one while the first is still running, and `scanFieldValue` now owns a Scope
// too. With a bool, the second Scope's ctor cleared the first scan's cancel
// (making it uncancellable) and the second's dtor dropped `active` while the
// first was still scanning (making it uncancellable a second way). Counting
// fixes both: only the OUTERMOST scope clears on entry, and only the LAST one to
// leave clears on exit. It also makes nesting safe, should a scan handler ever
// call another.
//
// Known trade-off, unchanged: the flag carries no scan identity, so a cancel
// aimed at scan A also stops a scan B that overlaps it. Worst case is an empty
// result plus a re-run, and the alternative (a per-scan token) would have to be
// threaded through every poll site including Memory::RunParallelScan's workers.
#pragma once

#include <atomic>

namespace Discrete::ScanCancel {

inline std::atomic<bool> g_scanCancel{false};
inline std::atomic<int>  g_scanActive{0};   // live Scope count

inline bool IsCancelRequested() noexcept {
    return g_scanCancel.load(std::memory_order_relaxed);
}

// Arm cancellation — but only while a scan is actually running, so a late
// cancelScan / a disconnect can't kill a future scan.
inline void RequestCancel() noexcept {
    if (g_scanActive.load(std::memory_order_relaxed) > 0)
        g_scanCancel.store(true, std::memory_order_relaxed);
}

// RAII for the duration of a scan handler: clear any stale flag + mark active.
struct Scope {
    Scope() noexcept {
        // Only the outermost scope clears. If a scan is already running, its
        // cancel is still live and must not be stolen by this one starting.
        if (g_scanActive.fetch_add(1, std::memory_order_acq_rel) == 0)
            g_scanCancel.store(false, std::memory_order_relaxed);
    }
    ~Scope() noexcept {
        // Clear the cancel flag only when the LAST scan leaves. The flag's
        // lifetime must not outlive the scans it applies to — a leaked armed flag
        // poisons every later IsCancelRequested() poll (that was P2: a stale flag
        // made `scanFieldValue`'s ReverseLookupIndex bail on its first region and
        // silently hand back a near-empty index) — but it must also survive while
        // a concurrent scan is still polling it.
        //
        // Order matters: the count drops FIRST, so RequestCancel()'s gate is
        // already closed for any cancel arriving while we clear `cancel` below.
        if (g_scanActive.fetch_sub(1, std::memory_order_acq_rel) == 1)
            g_scanCancel.store(false, std::memory_order_relaxed);
    }
    Scope(const Scope&)            = delete;
    Scope& operator=(const Scope&) = delete;
};

}  // namespace Discrete::ScanCancel
