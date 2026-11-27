// ForegroundHook.h — native "keep the game running when unfocused".
//
// Ported from UE5CEDumper's Grausam. Three Win32 illusions, all gated by one
// atomic so the game never learns it lost focus:
//   1. MinHook user32!GetForegroundWindow → report the game's own window as
//      foreground (defeats poll-based idle / Application.HasFocus-style checks).
//   2. Subclass every top-level game window's WndProc and rewrite the
//      deactivation messages (WM_ACTIVATEAPP / WM_ACTIVATE / WM_NCACTIVATE →
//      "active", swallow WM_KILLFOCUS) → defeats WM_ACTIVATEAPP-driven pauses.
//   3. MinHook user32!ClipCursor + SetCursorPos → while the lock is on but the
//      game is BACKGROUNDED, release cursor confinement / swallow recentring so
//      the mouse isn't trapped across the whole desktop; pass through untouched
//      when the game is genuinely foreground so in-game mouse-look is unaffected.
//
// Why native (not the managed UnityEngine.Application.set_runInBackground path
// that Scene/SceneEnumerator already drives): IL2CPP STRIPS set_runInBackground
// on real shipping games (see memory reference_il2cpp_strips_runinbackground —
// R-Type Dimensions EX, 0 refs), so the managed setter is method-not-found. This
// hook is pure Win32 / user32: it works regardless of the managed runtime and
// even while the game thread is idle. Engine-agnostic — compiles into both the
// Mono and IL2CPP DLLs (lives in shared/).
#pragma once

namespace Discrete::ForegroundHook {

// Enable/disable the foreground illusion. Idempotent. Returns:
//   1  = enabled (hooks active)
//   0  = disabled (soft; hooks left installed but pass through)
//  <0  = error installing the hook (GetForegroundWindow hook is fatal-on-fail)
int SetKeepForeground(bool enable);

// 1 if the illusion is currently active, else 0.
int IsKeepForegroundEnabled();

// Full teardown for DLL shutdown: soft-disable + release any masked cursor
// confinement + MH_DisableHook our own targets + restore the original WNDPROC
// on every subclassed window (via UnsubclassAllGameWindows) so no subclass
// callback can outlive the module. Safe when never enabled.
void Shutdown();

}  // namespace Discrete::ForegroundHook
