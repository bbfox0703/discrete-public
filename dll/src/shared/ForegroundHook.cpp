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
            MH_STATUS st = MH_CreateHook(pGfw,
                                         reinterpret_cast<void*>(&HookedGetForegroundWindow),
                                         reinterpret_cast<void**>(&g_origGetForegroundWindow));
            if (st != MH_OK) {
                LogF("[ForegroundHook] MH_CreateHook(GetForegroundWindow) failed: status=%d", static_cast<int>(st));
                return -3;
            }
            st = MH_EnableHook(pGfw);
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
                && MH_CreateHook(pClip,
                                 reinterpret_cast<void*>(&HookedClipCursor),
                                 reinterpret_cast<void**>(&g_origClipCursor)) == MH_OK
                && MH_EnableHook(pClip) == MH_OK) {
                g_pClipCursor = pClip;
            } else {
                LogF("[ForegroundHook] ClipCursor hook unavailable — cursor may stay locked when backgrounded");
            }

            if (pSetP
                && MH_CreateHook(pSetP,
                                 reinterpret_cast<void*>(&HookedSetCursorPos),
                                 reinterpret_cast<void**>(&g_origSetCursorPos)) == MH_OK
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
    // instance and is torn down separately). The WndProc subclass is left in
    // place: it now gates on the false g_enabled and passes through, which is
    // safer than un-subclassing mid-chain, and the DLL only unloads at game exit.
    if (g_pGetForegroundWindow) MH_DisableHook(g_pGetForegroundWindow);
    if (g_pClipCursor)          MH_DisableHook(g_pClipCursor);
    if (g_pSetCursorPos)        MH_DisableHook(g_pSetCursorPos);
    LogF("[ForegroundHook] Shutdown (hooks disabled, subclass left as pass-through)");
}

}  // namespace Discrete::ForegroundHook
