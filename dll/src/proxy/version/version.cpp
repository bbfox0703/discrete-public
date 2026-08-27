// version.cpp — proxy DLL exporting version.dll's symbols. Forwards every
// export at runtime to the real C:\Windows\System32\version.dll via
// GetProcAddress thunks.
//
// Phase 12a. The Windows headers declare every version.dll export with
// __declspec(dllimport), which clashes with our __declspec(dllexport)
// (C2375). To avoid that we name the C functions Proxy_<symbol> here
// and alias them as the public export via version.def — same approach
// used by BepInEx-style proxy projects.

#include "../loader.h"

#include <windows.h>

#include <atomic>

namespace {

std::atomic<HMODULE> g_real{nullptr};
// Non-blocking and re-entrant — see the twin in winmm.cpp for the full
// reasoning. The old CAS + bounded `for (i < 1000) Sleep(0)` spin blocked the
// losing thread and, if the spin expired, left g_real null so the thunk called
// through a null pointer. Proxy_LoadRealDll is already SRWLOCK-serialised,
// name-cached and refcount-safe, so racing callers converge on their own.
void EnsureReal() noexcept {
    if (g_real.load(std::memory_order_acquire) != nullptr) return;
    HMODULE m = Discrete::Proxy::Proxy_LoadRealDll(L"version.dll");
    g_real.store(m, std::memory_order_release);
}

template <typename Fn>
Fn ResolveExport(const char* name) noexcept {
    EnsureReal();
    return reinterpret_cast<Fn>(Discrete::Proxy::Proxy_GetRealExport(
        g_real.load(std::memory_order_acquire), name));
}

}  // namespace

// Internal symbol Proxy_<name>; aliased to <name> by version.def. The
// alias keeps the public export ABI identical while sidestepping the
// system header's dllimport decl.
//
// `failval` is what the export answers when the PRE-CRT GATE refuses. It has to
// be that export's own DOCUMENTED failure value, because the caller in that
// window is Windows' loader and it will act on the answer.
//
// ⚠ The two null cases are NOT the same, and the macro keeps them apart:
//
//   CRT not up yet   → the resolver was never entered. Answer `failval` and do
//                      NOT forward. Nothing is latched: `fn` stays null and the
//                      host's own next call resolves for real.
//   CRT up, fn null  → the resolver RAN and the name is genuinely absent from
//                      the real DLL (or the real DLL would not load). Fall
//                      through to `fn args` and crash loudly, ON PURPOSE — a
//                      silent stub here would hide a broken proxy behind a
//                      plausible return value.
//
// `static fn_t fn = nullptr` must stay a CONSTANT initialiser, never
// `static auto fn = ResolveExport<fn_t>(...)`. A magic static evaluates its
// initialiser exactly once and latches the result forever, so a pre-CRT first
// call would latch null and leave the export dead for the life of the process —
// worse than the crash the gate removes. It also drags in the CRT's
// `_Init_thread_header` machinery, onto a path that must not need the CRT.
#define PROXY_THUNK(ret, conv, name, params, args, failval)               \
    extern "C" ret conv Proxy_##name params {                             \
        using fn_t = ret (conv*) params;                                  \
        static fn_t fn = nullptr;                                         \
        if (fn == nullptr) {                                              \
            if (!Discrete::Proxy::Proxy_CrtReady()) {                     \
                Discrete::Proxy::Proxy_NotePreCrtCall();                  \
                return failval;                                           \
            }                                                             \
            fn = ResolveExport<fn_t>(#name);                              \
        }                                                                 \
        return fn args;                                                   \
    }

PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoA,
    (LPCSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d), FALSE)
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoW,
    (LPCWSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d), FALSE)
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoExA,
    (DWORD f, LPCSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d), FALSE)
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoExW,
    (DWORD f, LPCWSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d), FALSE)

PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeA, (LPCSTR a, LPDWORD b), (a, b), 0)
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeW, (LPCWSTR a, LPDWORD b), (a, b), 0)
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeExA,
    (DWORD f, LPCSTR a, LPDWORD b), (f, a, b), 0)
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeExW,
    (DWORD f, LPCWSTR a, LPDWORD b), (f, a, b), 0)

PROXY_THUNK(BOOL, WINAPI, VerQueryValueA,
    (LPCVOID a, LPCSTR b, LPVOID* c, PUINT d), (a, b, c, d), FALSE)
PROXY_THUNK(BOOL, WINAPI, VerQueryValueW,
    (LPCVOID a, LPCWSTR b, LPVOID* c, PUINT d), (a, b, c, d), FALSE)

PROXY_THUNK(DWORD, WINAPI, VerLanguageNameA, (DWORD a, LPSTR b, DWORD c), (a, b, c), 0)
PROXY_THUNK(DWORD, WINAPI, VerLanguageNameW, (DWORD a, LPWSTR b, DWORD c), (a, b, c), 0)

#undef PROXY_THUNK

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        // FIRST statement, before anything here can allocate. Reaching DllMain
        // means _DllMainCRTStartup has already run __acrt_initialize, so the
        // heap is real from this point on — which is exactly what every thunk's
        // pre-CRT gate above is asking about. See loader.h.
        Discrete::Proxy::Proxy_MarkCrtReady();
        ::DisableThreadLibraryCalls(self);
        // No EnsureReal() here — resolving under the loader lock while the
        // host .exe's static imports are still initialising is what crashed
        // Octopath Traveler for UE5CEDumper's dxgi proxy. Thunks resolve on
        // first call, on a game thread, after DllMain returns.
        Discrete::Proxy::Proxy_StartBackendLoader(self);
    }
    return TRUE;
}
