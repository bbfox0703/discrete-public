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

namespace {

HMODULE       g_real     = nullptr;
volatile LONG g_initOnce = 0;

void EnsureReal() noexcept {
    if (g_real != nullptr) return;
    if (::InterlockedCompareExchange(&g_initOnce, 1, 0) == 0) {
        g_real = UnityCEDumper::Proxy::Proxy_LoadRealDll(L"version.dll");
    } else {
        for (int i = 0; i < 1000 && g_real == nullptr; ++i) ::Sleep(0);
    }
}

template <typename Fn>
Fn ResolveExport(const char* name) noexcept {
    EnsureReal();
    return reinterpret_cast<Fn>(
        UnityCEDumper::Proxy::Proxy_GetRealExport(g_real, name));
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
        EnsureReal();
        UnityCEDumper::Proxy::Proxy_StartBackendLoader(self);
    }
    return TRUE;
}
