// MonoMainThreadHook.h — Phase 20-MT (Mono backend).
//
// Installs a MinHook detour on `mono_runtime_invoke` that, when called
// from the Unity main thread, calls `MainThreadDispatcher::NotifyMainThreadTick`
// before delegating to the original function. This piggybacks our queue
// drain on Unity's per-frame MonoBehaviour update tick: every Update /
// LateUpdate / FixedUpdate dispatch goes through runtime_invoke on the
// main thread.
//
// Public surface is tiny:
//   * Install() — call once after MonoResolver bound runtime_invoke. Idempotent.
//   * Uninstall() — call on DLL detach.
//   * IsInstalled() — diagnostic.
//
// Recursion safety lives in MainThreadDispatcher (thread_local guard).

#pragma once

#include <cstdint>

namespace Discrete::Mono::MainThreadHook {

/// Install the runtime_invoke detour. Requires:
///   * MonoResolver has bound `mono_runtime_invoke`
///   * MainThreadDispatcher::Init() has been called (main thread id cached)
/// Returns false if either precondition fails or MinHook setup errors.
/// Logs all failures to the "init" log category.
bool Install();

/// Uninstall the hook. Safe to call even if Install never succeeded.
void Uninstall();

/// True if Install succeeded and the detour is currently active.
bool IsInstalled();

}  // namespace Discrete::Mono::MainThreadHook
