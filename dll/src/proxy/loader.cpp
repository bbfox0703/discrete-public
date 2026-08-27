// loader.cpp — shared proxy-DLL helpers (real-DLL forwarding + backend
// detection + sibling-DLL load). See loader.h for the contract.
//
// Phase 12a. Reuses BackendDetector + ModuleHelper from the main DLL tree
// to avoid duplicating module-probing logic. The proxy compiles those .cpp
// files into the same target so we don't need a separate static lib.

#include "loader.h"

#include "../shared/BackendDetector.h"
#include "../shared/Logger.h"

#include <windows.h>
#include <psapi.h>

#include <cstdio>
#include <cstring>
#include <string>

namespace Discrete::Proxy {

namespace {

// ── The pre-CRT latch's storage ────────────────────────────────────────────
//
// MUST stay plain `volatile LONG`s with CONSTANT initialisers. They are read on
// a path where the CRT does not exist, so they have to live in zero-initialised
// .data with NO dynamic initialiser — anything with a runtime constructor
// (including a std::atomic that is not constant-initialised) would be read
// before it was written. An aligned LONG load is atomic on x86/x64, and
// g_crtReady is a one-way 0→1 latch, so the read needs no interlock; the write
// uses one anyway because it costs nothing.
//
// See loader.h for why this exists at all.
volatile LONG g_crtReady    = 0;
volatile LONG g_preCrtCalls = 0;

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

// Report pre-CRT refusals, once, from OFF the loader lock.
//
// Deliberately not reported from DllMain: that would put file I/O under the
// loader lock on every single launch. Here it costs nothing on the normal path
// (the counter is zero and we return before touching the logger) and still
// lands before anything else this thread does.
//
// The count is final by the time we run: g_crtReady is set as DllMain's first
// statement, so no further call can be refused after that.
void ReportPreCrtCalls() {
    LONG n = Proxy_PreCrtCallCount();
    if (n == 0) return;
    Discrete::Log::Init();
    // ASCII only, and sized for the whole message: this is the one line that
    // explains an otherwise invisible event, so a silently truncated tail
    // would defeat the point of writing it. 512 leaves ~200 bytes of slack.
    char msg[512];
    std::snprintf(msg, sizeof(msg),
                  "[PROXY] WARN: %ld forwarded call(s) arrived BEFORE our CRT "
                  "was initialised and were refused - each answered its "
                  "export's documented failure value instead of allocating on "
                  "a null heap. Nothing is latched; later calls resolve "
                  "normally. This is the AppCompat shim engine's fingerprint; "
                  "see docs/proxy-dlls.md.",
                  n);
    Discrete::Log::Init(msg);
}

// Worker-thread payload: detects backend, then LoadLibrary's the matching
// Discrete-{Backend}.dll from the proxy's directory. Run from a dedicated
// thread because DllMain holds the loader lock — calling LoadLibrary there
// for a DLL with its own DllMain would deadlock.
DWORD WINAPI BackendLoaderThread(LPVOID lpParam) {
    ReportPreCrtCalls();

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

void Proxy_MarkCrtReady() noexcept  { ::InterlockedExchange(&g_crtReady, 1); }
bool Proxy_CrtReady() noexcept      { return g_crtReady != 0; }
void Proxy_NotePreCrtCall() noexcept{ ::InterlockedIncrement(&g_preCrtCalls); }
LONG Proxy_PreCrtCallCount() noexcept { return g_preCrtCalls; }

HMODULE Proxy_LoadRealDll(const wchar_t* dllName) noexcept {
    if (dllName == nullptr) return nullptr;

    // ⛔ PRE-CRT GATE. Must precede BOTH the std::wstring below (which mallocs
    // on a heap that may not exist yet) and LoadLibraryW (which would be a
    // recursive load under a loader lock the pre-CRT caller is still holding).
    // Nothing is latched — the cache stays empty, so the next call retries.
    // The thunks gate themselves too and answer a documented failure value;
    // this one guards Proxy_LoadRealDll as a public entry point in its own
    // right. See the pre-CRT latch commentary in loader.h.
    if (!Proxy_CrtReady()) {
        Proxy_NotePreCrtCall();
        return nullptr;
    }

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
    // SystemDllPath returns empty when GetSystemDirectoryW failed or the result
    // would not fit. Refuse rather than handing LoadLibraryW a bare file name:
    // the proxy IS named version.dll / winmm.dll in the game directory, which
    // precedes System32 in the search order, so a name-only load would resolve
    // back to US.
    if (path.empty()) return nullptr;
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
    if (h != nullptr) {
        ::CloseHandle(h);
        return;
    }

    // This thread is the ONLY thing the proxy does. If it never starts, the
    // proxy is an inert forwarder for the life of the process and every
    // downstream symptom ("installed but nothing attaches") points somewhere
    // else. Worth the one-off file I/O under the loader lock — we are already
    // broken by the time we get here, and the normal path never reaches it.
    DWORD err = ::GetLastError();
    Discrete::Log::Init();
    char msg[160];
    std::snprintf(msg, sizeof(msg),
                  "[PROXY] ERROR: CreateThread for the backend loader failed "
                  "(GetLastError=%lu). No backend DLL will be loaded.",
                  err);
    Discrete::Log::Init(msg);
}

}  // namespace Discrete::Proxy
