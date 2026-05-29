// loader.cpp — shared proxy-DLL helpers (real-DLL forwarding + backend
// detection + sibling-DLL load). See loader.h for the contract.
//
// Phase 12a. Reuses BackendDetector + ModuleHelper from the main DLL tree
// to avoid duplicating module-probing logic. The proxy compiles those .cpp
// files into the same target so we don't need a separate static lib.

#include "loader.h"

#include "../shared/BackendDetector.h"

#include <windows.h>
#include <psapi.h>

#include <cstring>
#include <string>

namespace Discrete::Proxy {

namespace {

// Single cached real-DLL handle per name. Set once on first lookup; never
// freed (the proxy holds the ref for the lifetime of the host process so
// the export thunks stay valid).
struct CachedRealDll {
    const wchar_t* name;   // pointer to a string literal in the proxy's static data
    HMODULE        handle;
};
constexpr int kMaxCachedDlls = 4;
CachedRealDll  g_cache[kMaxCachedDlls] = {};
int            g_cacheCount = 0;
SRWLOCK        g_cacheLock = SRWLOCK_INIT;

std::wstring SystemDllPath(const wchar_t* dllName) {
    wchar_t sysDir[MAX_PATH];
    UINT n = ::GetSystemDirectoryW(sysDir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return {};
    std::wstring path(sysDir, sysDir + n);
    path += L"\\";
    path += dllName;
    return path;
}

std::wstring DirOfSelf(HMODULE self) {
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD n = ::GetModuleFileNameW(self, buf, static_cast<DWORD>(std::size(buf)));
    if (n == 0) return {};
    std::wstring full(buf, buf + n);
    auto pos = full.find_last_of(L"\\/");
    return (pos == std::wstring::npos) ? std::wstring() : full.substr(0, pos);
}

// Worker-thread payload: detects backend, then LoadLibrary's the matching
// Discrete-{Backend}.dll from the proxy's directory. Run from a dedicated
// thread because DllMain holds the loader lock — calling LoadLibrary there
// for a DLL with its own DllMain would deadlock.
DWORD WINAPI BackendLoaderThread(LPVOID lpParam) {
    HMODULE proxySelf = reinterpret_cast<HMODULE>(lpParam);
    std::wstring proxyDir = DirOfSelf(proxySelf);
    if (proxyDir.empty()) return 1;

    // Backend detection polls loaded modules, which can race with the host's
    // own engine-DLL load. Give the engine up to ~5 seconds to settle.
    BackendDetector::Backend backend = BackendDetector::Backend::Unknown;
    for (int attempt = 0; attempt < 50; ++attempt) {
        auto d = BackendDetector::Detect();
        if (d.backend != BackendDetector::Backend::Unknown) {
            backend = d.backend;
            break;
        }
        ::Sleep(100);
    }
    if (backend == BackendDetector::Backend::Unknown) {
        // Give up silently — the host game probably isn't Unity, or the
        // engine never loaded.
        return 2;
    }

    std::wstring backendDll = proxyDir + L"\\";
    backendDll += (backend == BackendDetector::Backend::IL2CPP)
        ? L"Discrete-IL2CPP.dll"
        : L"Discrete-Mono.dll";

    HMODULE h = ::LoadLibraryW(backendDll.c_str());
    return (h != nullptr) ? 0 : 3;
}

}  // namespace

HMODULE Proxy_LoadRealDll(const wchar_t* dllName) noexcept {
    if (dllName == nullptr) return nullptr;

    // Fast read-only check first.
    ::AcquireSRWLockShared(&g_cacheLock);
    for (int i = 0; i < g_cacheCount; ++i) {
        if (::wcscmp(g_cache[i].name, dllName) == 0) {
            HMODULE h = g_cache[i].handle;
            ::ReleaseSRWLockShared(&g_cacheLock);
            return h;
        }
    }
    ::ReleaseSRWLockShared(&g_cacheLock);

    // Slow path: load the real system DLL. Use the absolute System32 path
    // so the loader doesn't pick up our proxy again under the same name.
    std::wstring path = SystemDllPath(dllName);
    HMODULE h = ::LoadLibraryW(path.c_str());

    ::AcquireSRWLockExclusive(&g_cacheLock);
    // Re-check under the exclusive lock in case another thread populated.
    for (int i = 0; i < g_cacheCount; ++i) {
        if (::wcscmp(g_cache[i].name, dllName) == 0) {
            HMODULE existing = g_cache[i].handle;
            ::ReleaseSRWLockExclusive(&g_cacheLock);
            if (h && h != existing) ::FreeLibrary(h);
            return existing;
        }
    }
    if (g_cacheCount < kMaxCachedDlls && h != nullptr) {
        g_cache[g_cacheCount].name   = dllName;
        g_cache[g_cacheCount].handle = h;
        ++g_cacheCount;
    }
    ::ReleaseSRWLockExclusive(&g_cacheLock);
    return h;
}

FARPROC Proxy_GetRealExport(HMODULE realDll, const char* exportName) noexcept {
    if (realDll == nullptr || exportName == nullptr) return nullptr;
    return ::GetProcAddress(realDll, exportName);
}

void Proxy_StartBackendLoader(HMODULE proxySelf) noexcept {
    // Spawn the loader thread DETACHED — we don't care about its result and
    // CloseHandle on the thread handle right after lets the OS reclaim it
    // when it exits.
    HANDLE h = ::CreateThread(nullptr, 0, &BackendLoaderThread,
                              proxySelf, 0, nullptr);
    if (h != nullptr) ::CloseHandle(h);
}

}  // namespace Discrete::Proxy
