// ForegroundHook.cpp — see ForegroundHook.h.
//
// Ported from UE5CEDumper's Grausam. Faithful to the original mechanism;
// adapted to discrete's Logger + shared-MinHook discipline.

#include "ForegroundHook.h"

#include "Logger.h"
#include "MagicStrings.h"

#include <MinHook.h>
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <mutex>
#include <utility>

namespace Discrete::ForegroundHook {

namespace {

template <typename... Args>
void LogF(const char* fmt, Args... args) {
    char buf[512];
    std::snprintf(buf, sizeof(buf), fmt, args...);
    Discrete::Log::Write(Discrete::MagicStrings::LogCategoryInit, buf);
}

// Calling convention MUST match the user32 targets, which are all WINAPI
// (stdcall) on BOTH x86 and x64. Keeping the detours WINAPI matches the target
// convention, so there is no stack double-pop (the __cdecl pitfall that forced
// MonoMainThreadHook to DROP WINAPI does not apply here — those targets are
// cdecl Mono exports, these are stdcall user32 exports).
using GetForegroundWindow_t = HWND(WINAPI*)();
using ClipCursor_t          = BOOL(WINAPI*)(const RECT*);
using SetCursorPos_t        = BOOL(WINAPI*)(int, int);

std::mutex             g_mutex;
GetForegroundWindow_t  g_origGetForegroundWindow = nullptr;
ClipCursor_t           g_origClipCursor          = nullptr;  // trampoline — NEVER call ::ClipCursor from the hook (recursion)
SetCursorPos_t         g_origSetCursorPos        = nullptr;
std::atomic<bool>      g_enabled{false};
std::atomic<HWND>      g_gameWindow{nullptr};
bool                   g_hookInstalled = false;   // guarded by g_mutex
bool                   g_mhInitialized = false;   // guarded by g_mutex

// Hooked target addresses, cached at install so Shutdown() can MH_DisableHook
// them without re-resolving. Guarded by g_mutex.
void* g_pGetForegroundWindow = nullptr;
void* g_pClipCursor          = nullptr;
void* g_pSetCursorPos        = nullptr;

// Per-window property key holding the original WNDPROC of a window we subclassed.
constexpr const wchar_t* kOrigProcProp = L"Discrete_ForegroundHook_OrigProc";

// ── Window enumeration ───────────────────────────────────────────────

BOOL CALLBACK PickLargestProc(HWND hwnd, LPARAM lp) {
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ::GetCurrentProcessId()) return TRUE;
    if (!::IsWindowVisible(hwnd)) return TRUE;
    if (::GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;  // skip owned dialogs/tooltips

    RECT rc{};
    if (!::GetWindowRect(hwnd, &rc)) return TRUE;
    const long area = (rc.right - rc.left) * (rc.bottom - rc.top);
    auto* best = reinterpret_cast<std::pair<HWND, long>*>(lp);
    if (area > best->second) { best->first = hwnd; best->second = area; }
    return TRUE;
}

HWND FindGameWindow() {
    std::pair<HWND, long> best{nullptr, 0};
    ::EnumWindows(&PickLargestProc, reinterpret_cast<LPARAM>(&best));
    return best.first;
}

// MH_CreateHook answers ALREADY_CREATED when we are re-installing a hook that a
// previous Shutdown() disabled but did not remove. The trampoline captured by the
// first install is still valid, so that is a success — re-enabling is all that is
// left to do. Treating it as failure is what made keep-foreground come back dead
// after a Discrete_Shutdown -> Discrete_Init cycle.
bool CreateOrReuseHook(void* target, void* detour, void** orig) {
    const MH_STATUS st = MH_CreateHook(target, detour, orig);
    return st == MH_OK || st == MH_ERROR_ALREADY_CREATED;
}

// ── (1) GetForegroundWindow hook ─────────────────────────────────────

HWND WINAPI HookedGetForegroundWindow() {
    HWND real = g_origGetForegroundWindow ? g_origGetForegroundWindow() : ::GetForegroundWindow();
    if (!g_enabled.load(std::memory_order_relaxed))
        return real;

    DWORD pid = 0;
    if (real) ::GetWindowThreadProcessId(real, &pid);
    if (pid == ::GetCurrentProcessId())
        return real;  // genuinely foreground — keep the truth

    HWND gw = g_gameWindow.load(std::memory_order_relaxed);
    if (!gw || !::IsWindow(gw)) {
        gw = FindGameWindow();
        g_gameWindow.store(gw, std::memory_order_relaxed);
    }
    return (gw && ::IsWindow(gw)) ? gw : real;
}

// ── (3) Cursor-confinement neutralization ────────────────────────────
// Read focus truth through the un-hooked trampoline so it is not fooled by our
// own illusion (HookedGetForegroundWindow).
bool GameGenuinelyForeground() {
    HWND real = g_origGetForegroundWindow ? g_origGetForegroundWindow() : ::GetForegroundWindow();
    if (!real) return false;
    DWORD pid = 0;
    ::GetWindowThreadProcessId(real, &pid);
    return pid == ::GetCurrentProcessId();
}

BOOL WINAPI HookedClipCursor(const RECT* rc) {
    if (!g_origClipCursor) return FALSE;  // defensive — never null once hooked
    // Only intercept CONFINEMENT (rc != null). A release (rc == null) passes
    // through. Confinement while backgrounded → release instead so the cursor is
    // free for the app the user switched to.
    if (rc && g_enabled.load(std::memory_order_relaxed) && !GameGenuinelyForeground())
        return g_origClipCursor(nullptr);
    return g_origClipCursor(rc);
}

BOOL WINAPI HookedSetCursorPos(int x, int y) {
    if (!g_origSetCursorPos) return FALSE;
    // Swallow per-frame recentring while backgrounded so the cursor does not snap
    // back into the game window; report success so the game's own logic is
    // unaffected.
    if (g_enabled.load(std::memory_order_relaxed) && !GameGenuinelyForeground())
        return TRUE;
    return g_origSetCursorPos(x, y);
}

// ── (2) WndProc subclass — neutralize deactivation messages ──────────

LRESULT CALLBACK SubclassProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    auto orig = reinterpret_cast<WNDPROC>(::GetPropW(h, kOrigProcProp));

    if (g_enabled.load(std::memory_order_relaxed)) {
        switch (msg) {
            case WM_ACTIVATEAPP:  w = TRUE;                              break;  // always "activated"
            case WM_NCACTIVATE:   w = TRUE;                              break;  // keep frame active
            case WM_ACTIVATE:     w = MAKEWPARAM(WA_ACTIVE, HIWORD(w));  break;  // low word = WA_ACTIVE
            case WM_KILLFOCUS:    return 0;                                      // swallow focus loss
            default: break;
        }
    }

    if (orig) return ::CallWindowProcW(orig, h, msg, w, l);
    return ::DefWindowProcW(h, msg, w, l);
}

BOOL CALLBACK SubclassEnumProc(HWND hwnd, LPARAM /*lp*/) {
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ::GetCurrentProcessId()) return TRUE;
    if (!::IsWindowVisible(hwnd)) return TRUE;
    if (::GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;
    if (::GetPropW(hwnd, kOrigProcProp)) return TRUE;  // already subclassed by us

    WNDPROC orig = reinterpret_cast<WNDPROC>(
        static_cast<LONG_PTR>(::GetWindowLongPtrW(hwnd, GWLP_WNDPROC)));
    if (!::SetPropW(hwnd, kOrigProcProp, reinterpret_cast<HANDLE>(orig)))
        return TRUE;
    ::SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&SubclassProc));

    RECT rc{}; ::GetWindowRect(hwnd, &rc);
    LogF("[ForegroundHook] Subclassed window %p (%dx%d, unicode=%d)",
         reinterpret_cast<void*>(hwnd),
         static_cast<int>(rc.right - rc.left), static_cast<int>(rc.bottom - rc.top),
         static_cast<int>(::IsWindowUnicode(hwnd)));
    return TRUE;
}

void SubclassAllGameWindows() {
    ::EnumWindows(&SubclassEnumProc, 0);
}

// Reverse of SubclassEnumProc: restore the original WNDPROC on every window we
// subclassed and drop our property. Called from Shutdown() so an actual DLL
// unload (FreeLibrary path in DllMain::OnDetach) can't leave a window's
// GWLP_WNDPROC pointing at &SubclassProc in soon-to-be-unmapped memory — the
// next window message would dispatch into freed code and crash the host.
BOOL CALLBACK UnsubclassEnumProc(HWND hwnd, LPARAM /*lp*/) {
    // Same process filter as SubclassEnumProc / PickLargestProc. EnumWindows walks
    // every top-level window on the desktop, and window properties are readable and
    // REMOVABLE cross-process — without this, a second injected game's Shutdown
    // would strip the property off THIS game's windows (whose GWLP_WNDPROC points
    // at its own copy of SubclassProc, so the restore below is skipped) and leave
    // them in exactly the broken state this function exists to prevent.
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ::GetCurrentProcessId()) return TRUE;

    HANDLE origProp = ::GetPropW(hwnd, kOrigProcProp);
    if (!origProp) return TRUE;  // not one of ours

    // Only restore if OUR proc is still the active WndProc. If a foreign subclass
    // chained on top of us, clobbering GWLP_WNDPROC would break their chain.
    WNDPROC cur = reinterpret_cast<WNDPROC>(
        static_cast<LONG_PTR>(::GetWindowLongPtrW(hwnd, GWLP_WNDPROC)));
    if (cur != &SubclassProc) {
        // We are still IN the chain, just no longer at its head. The property must
        // STAY: SubclassProc reads it to forward to the original proc, and without
        // it every message the foreign subclass passes down would fall through to
        // DefWindowProcW instead — permanently bypassing the game's real WndProc
        // (dead input / broken paint / unclosable window). Message rewriting is
        // already off via g_enabled, so leaving the pass-through in place is inert.
        LogF("[ForegroundHook] Window %p re-subclassed by another module — leaving our "
             "subclass in the chain (module must not be unloaded while this stands)",
             reinterpret_cast<void*>(hwnd));
        return TRUE;
    }

    ::SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
                        reinterpret_cast<LONG_PTR>(reinterpret_cast<WNDPROC>(origProp)));
    ::RemovePropW(hwnd, kOrigProcProp);   // only after the restore actually happened
    return TRUE;
}

void UnsubclassAllGameWindows() {
    ::EnumWindows(&UnsubclassEnumProc, 0);
}

}  // namespace

int SetKeepForeground(bool enable) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (enable) {
        if (!g_hookInstalled) {
            if (!g_mhInitialized) {
                // MinHook is shared across the DLL (MonoMainThreadHook /
                // IL2CPPMainThreadHook init it too); ALREADY_INITIALIZED is fine.
                MH_STATUS st = MH_Initialize();
                if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
                    LogF("[ForegroundHook] MH_Initialize failed: status=%d", static_cast<int>(st));
                    return -1;
                }
                g_mhInitialized = true;
            }

            HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
            void* pGfw  = user32 ? reinterpret_cast<void*>(::GetProcAddress(user32, "GetForegroundWindow")) : nullptr;
            void* pClip = user32 ? reinterpret_cast<void*>(::GetProcAddress(user32, "ClipCursor"))          : nullptr;
            void* pSetP = user32 ? reinterpret_cast<void*>(::GetProcAddress(user32, "SetCursorPos"))        : nullptr;
            if (!pGfw) {
                LogF("[ForegroundHook] GetProcAddress(user32!GetForegroundWindow) failed");
                return -2;
            }

            // (1) GetForegroundWindow — the primary illusion. Fatal on failure.
            if (!CreateOrReuseHook(pGfw,
                                   reinterpret_cast<void*>(&HookedGetForegroundWindow),
                                   reinterpret_cast<void**>(&g_origGetForegroundWindow))) {
                LogF("[ForegroundHook] MH_CreateHook(GetForegroundWindow) failed");
                return -3;
            }
            MH_STATUS st = MH_EnableHook(pGfw);
            if (st != MH_OK) {
                LogF("[ForegroundHook] MH_EnableHook(GetForegroundWindow) failed: status=%d", static_cast<int>(st));
                MH_RemoveHook(pGfw);
                g_origGetForegroundWindow = nullptr;
                return -4;
            }
            g_pGetForegroundWindow = pGfw;

            // (3) ClipCursor + SetCursorPos — best-effort. A game that never
            // confines the cursor never triggers them, and a failure here must
            // NOT abort the primary lock. Enable PER-TARGET, never MH_ALL_HOOKS —
            // MonoMainThreadHook / IL2CPPMainThreadHook share this MinHook
            // instance and must not be re-enabled here.
            if (pClip
                && CreateOrReuseHook(pClip,
                                     reinterpret_cast<void*>(&HookedClipCursor),
                                     reinterpret_cast<void**>(&g_origClipCursor))
                && MH_EnableHook(pClip) == MH_OK) {
                g_pClipCursor = pClip;
            } else {
                LogF("[ForegroundHook] ClipCursor hook unavailable — cursor may stay locked when backgrounded");
            }

            if (pSetP
                && CreateOrReuseHook(pSetP,
                                     reinterpret_cast<void*>(&HookedSetCursorPos),
                                     reinterpret_cast<void**>(&g_origSetCursorPos))
                && MH_EnableHook(pSetP) == MH_OK) {
                g_pSetCursorPos = pSetP;
            }

            g_hookInstalled = true;
        }

        g_gameWindow.store(FindGameWindow(), std::memory_order_relaxed);
        g_enabled.store(true, std::memory_order_relaxed);
        // Subclass the game window(s) so WM_ACTIVATEAPP-driven pauses are defeated
        // too (the GetForegroundWindow hook alone only covers the polling path).
        SubclassAllGameWindows();
        LogF("[ForegroundHook] Keep-foreground ENABLED (fg-window=%p)",
             reinterpret_cast<void*>(g_gameWindow.load()));
        return 1;
    }

    // Soft-disable: leave the hooks + subclass installed (they gate on g_enabled
    // and pass through when off) to avoid an unhook/unsubclass race with an
    // in-flight call.
    g_enabled.store(false, std::memory_order_relaxed);
    // Actively release any confinement we were masking so the cursor is freed the
    // instant the lock goes off. Skip if the game is genuinely foreground — the
    // user is playing and a live in-game clip must be left alone.
    if (g_origClipCursor && !GameGenuinelyForeground())
        g_origClipCursor(nullptr);
    LogF("[ForegroundHook] Keep-foreground DISABLED");
    return 0;
}

int IsKeepForegroundEnabled() {
    return g_enabled.load(std::memory_order_relaxed) ? 1 : 0;
}

void Shutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_hookInstalled && !g_enabled.load(std::memory_order_relaxed)) return;

    g_enabled.store(false, std::memory_order_relaxed);
    if (g_origClipCursor && !GameGenuinelyForeground())
        g_origClipCursor(nullptr);

    // Disable our own MinHook targets so no new detour entry occurs after we go
    // away. Per-target only (never MH_ALL_HOOKS — the main-thread hook shares the
    // instance and is torn down separately).
    if (g_pGetForegroundWindow) MH_DisableHook(g_pGetForegroundWindow);
    if (g_pClipCursor)          MH_DisableHook(g_pClipCursor);
    if (g_pSetCursorPos)        MH_DisableHook(g_pSetCursorPos);

    // Restore the original WNDPROC on every subclassed window. g_enabled is
    // already false, so no new message is intercepted; SetWindowLongPtrW is
    // per-window atomic, so any SubclassProc call already on the game UI
    // thread's stack finishes safely and no NEW dispatch enters &SubclassProc
    // afterward. This is what makes an eventual FreeLibrary unload safe — the
    // subclass must not outlive the module's mapped code.
    UnsubclassAllGameWindows();

    // The detours are DISABLED but still created (MinHook keeps the trampolines).
    // Clear the installed flag so a later SetKeepForeground(true) — e.g. after a
    // Discrete_Shutdown -> Discrete_Init cycle — walks the install path again and
    // re-enables them. Leaving it set made that path skip the whole block, so the
    // lock reported ENABLED while the GetForegroundWindow illusion stayed dead and
    // only the WndProc subclass still worked. CreateOrReuseHook absorbs the
    // ALREADY_CREATED that MH_CreateHook then returns.
    g_hookInstalled = false;

    LogF("[ForegroundHook] Shutdown (hooks disabled, subclass restored)");
}

}  // namespace Discrete::ForegroundHook
