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
#define PROXY_THUNK(ret, conv, name, params, args)                        \
    extern "C" ret conv Proxy_##name params {                             \
        using fn_t = ret (conv*) params;                                  \
        static fn_t fn = nullptr;                                         \
        if (fn == nullptr) fn = ResolveExport<fn_t>(#name);               \
        return fn args;                                                   \
    }

PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoA,
    (LPCSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d))
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoW,
    (LPCWSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d))
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoExA,
    (DWORD f, LPCSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d))
PROXY_THUNK(BOOL, WINAPI, GetFileVersionInfoExW,
    (DWORD f, LPCWSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d))

PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeA,
    (LPCSTR a, LPDWORD b), (a, b))
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeW,
    (LPCWSTR a, LPDWORD b), (a, b))
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeExA,
    (DWORD f, LPCSTR a, LPDWORD b), (f, a, b))
PROXY_THUNK(DWORD, WINAPI, GetFileVersionInfoSizeExW,
    (DWORD f, LPCWSTR a, LPDWORD b), (f, a, b))

PROXY_THUNK(BOOL, WINAPI, VerQueryValueA,
    (LPCVOID a, LPCSTR b, LPVOID* c, PUINT d), (a, b, c, d))
PROXY_THUNK(BOOL, WINAPI, VerQueryValueW,
    (LPCVOID a, LPCWSTR b, LPVOID* c, PUINT d), (a, b, c, d))

PROXY_THUNK(DWORD, WINAPI, VerLanguageNameA,
    (DWORD a, LPSTR b, DWORD c), (a, b, c))
PROXY_THUNK(DWORD, WINAPI, VerLanguageNameW,
    (DWORD a, LPWSTR b, DWORD c), (a, b, c))

#undef PROXY_THUNK

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        ::DisableThreadLibraryCalls(self);
        // No EnsureReal() here — resolving under the loader lock while the
        // host .exe's static imports are still initialising is what crashed
        // Octopath Traveler for UE5CEDumper's dxgi proxy. Thunks resolve on
        // first call, on a game thread, after DllMain returns.
        Discrete::Proxy::Proxy_StartBackendLoader(self);
    }
    return TRUE;
}
