// MainThreadDispatcher.h — Phase 20-MT.
//
// Out-of-process main-thread marshaling for Unity-engine icalls. The pipe
// server accepts `callMethod` requests on worker threads, but most Unity
// engine APIs (Transform.parent, GameObject.name, scene-graph walks, etc.)
// require execution on the Unity main thread or the runtime deadlocks /
// corrupts state. Phase 20a discovered this the hard way on SaGa F2R —
// ~2048 sustained icalls froze the game.
//
// Mechanism: each backend (Mono / IL2CPP) installs a MinHook detour on
// `*_runtime_invoke`. That function is called every frame from the main
// thread (Unity uses it to dispatch MonoBehaviour Update / LateUpdate /
// FixedUpdate). When we observe a call from the cached main-thread id,
// we drain the work queue; recursion is blocked by a thread_local flag
// so our drained items invoking runtime_invoke again don't trigger
// another drain pass.
//
// Public surface is intentionally tiny:
//   * Init() — call once after backend resolver is bound. Captures main
//     thread id from the process snapshot (oldest-created thread = OS
//     process main thread, which is always the Unity main thread).
//   * Shutdown() — uninstall hooks. Optional; DLL usually stays loaded.
//   * SubmitAndWait(work, timeout_ms) — pipe handler entry. Pushes the
//     work item, blocks the worker thread until main-thread drain
//     signals completion (or timeout fires).
//   * NotifyMainThreadTick() — called from the backend's detour hook.
//     Drains the queue with recursion guard. Idempotent across recursive
//     entries.
//   * IsHealthy() — true if a main-thread tick has been observed in the
//     last few seconds (= the hook is wired and Unity is running its
//     update loop). Worker thread can fall back to direct invoke when
//     this returns false (e.g. game still on loading screen).

#pragma once

#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>

namespace Discrete::MainThreadDispatcher {

// Maximum work items queued before SubmitAndWait fails fast with
// `queue_full`. Set conservatively — sustained burst higher than this
// almost certainly means the main thread isn't draining (frozen game,
// loading screen, or hook didn't install). The Lua bridge throttles to
// 1 ms per RPC so even thousands of icalls don't pile up here.
constexpr std::size_t kMaxQueuedItems = 256;

// Soft-health window: if NotifyMainThreadTick hasn't fired in this many
// ms, IsHealthy() returns false. Tuned for the worst-case render frame —
// 32 ms covers even a 30 FPS game with one stutter frame. Lower would
// false-alarm; higher would let frozen games go undetected.
constexpr std::int64_t kHealthyWindowMs = 1000;

/// Opaque handle returned by SubmitAndWait — the dispatcher owns the
/// underlying buffers, the caller just reads `ok`, `error`, and `result`.
struct DispatchResult {
    bool        ok = false;            // true when main thread drained + work completed
    bool        timed_out = false;     // true when timeout_ms elapsed before drain
    bool        rejected = false;      // true when queue was full or dispatcher disabled
    std::string error;                 // human-readable explanation when !ok
    std::string diagnostic_path;       // "queued" / "drained" / "fallback_direct" / ...
};

/// The user-supplied work that the main thread runs. Returns true on
/// success; the closure is responsible for stashing its own result
/// somewhere the caller can read after SubmitAndWait returns.
using WorkFn = std::function<void()>;

/// Initialize the dispatcher. Captures the process main thread id and
/// arms the queue. Safe to call multiple times — second + subsequent
/// calls no-op. Returns false if main thread id couldn't be determined
/// (extremely unlikely; would only happen if Toolhelp32 enumeration
/// returned no threads).
bool Init();

/// Uninstall all hooks and clear pending work. Currently only called on
/// DLL detach; the DLL normally lives until process exit.
void Shutdown();

/// True if Init() has been called and main thread id is known. Both
/// hook installers (Mono / IL2CPP) gate on this.
bool IsInitialized();

/// Returns the cached process main thread id (the OS thread that started
/// the process; for Unity, this is also the engine main thread). 0 if
/// Init() has not been called or detection failed.
std::uint32_t GetMainThreadId();

/// Mark a successful main-thread tick. Both backends' detour hooks call
/// this on entry from the main thread. Also drives IsHealthy()'s freshness
/// window. Increments an internal tick counter for diagnostics.
void NotifyMainThreadTick();

/// Drain the work queue. Must only be called from the main thread (the
/// dispatcher asserts and short-circuits otherwise). Internally
/// thread_local-guarded against recursion — recursive calls return
/// immediately without re-draining.
void Drain();

/// Submit a closure to run on the next main-thread tick. Blocks the
/// caller until the closure has executed OR the timeout elapses.
///
/// Recommended pattern:
///   std::string result;
///   bool ok = false;
///   auto disp = MainThreadDispatcher::SubmitAndWait([&]() {
///       result = DoTheUnityIcall();
///       ok = true;
///   }, 5000);
///   if (!disp.ok) return error_from(disp);
///   // use `result`
DispatchResult SubmitAndWait(WorkFn work, std::int32_t timeout_ms);

/// True if a main-thread tick has been observed in the last
/// kHealthyWindowMs. False if either (a) the hook hasn't installed,
/// (b) the game is on a loading screen, or (c) Unity has frozen for
/// other reasons. callers can use this to decide between routing via
/// the queue or falling back to direct (worker-thread) invoke.
bool IsHealthy();

/// True when the current thread is INSIDE the dispatcher's main-thread
/// Drain pass. Used by backend ThreadAttachGuard implementations to
/// skip mono_thread_attach / mono_thread_detach — the Unity main thread
/// is already attached to the runtime, and re-attaching + then detaching
/// tears down Unity's own thread handle (BongoCat build #54 reproduction).
///
/// Implementation note: this is a thread_local flag the dispatcher sets
/// just before invoking each work item's closure. Cheap (no atomics);
/// callable from any thread (false from worker threads, true only on
/// the main thread inside a Drain iteration).
bool IsCurrentlyDrainingOnMainThread();

/// Returns a snapshot of internal counters for the `getDispatcherStats`
/// pipe command. Read-mostly; mutex-protected internally.
struct Stats {
    bool          initialized;
    std::uint32_t main_thread_id;
    std::uint64_t tick_count;           // total NotifyMainThreadTick calls
    std::uint64_t submit_count;         // total SubmitAndWait entries
    std::uint64_t drain_count;          // total work items drained
    std::uint64_t timeout_count;        // SubmitAndWait that hit timeout_ms
    std::uint64_t reject_count;         // SubmitAndWait that hit queue full
    std::int64_t  last_tick_age_ms;     // ms since last NotifyMainThreadTick
    std::size_t   queue_depth;          // items currently waiting
};
Stats GetStats();

}  // namespace Discrete::MainThreadDispatcher
