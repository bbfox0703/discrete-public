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

// Allocation-free system-DLL path builder (A-L10): called from noexcept
// Proxy_LoadRealDll so it must never allocate via std::wstring / throw bad_alloc.
bool BuildSystemDllPath(const wchar_t* dllName, wchar_t* out, std::size_t outCap) noexcept {
    if (dllName == nullptr || out == nullptr || outCap == 0) return false;
    UINT n = ::GetSystemDirectoryW(out, static_cast<UINT>(outCap));
    if (n == 0 || n >= outCap) return false;
    const std::size_t nameLen = ::wcslen(dllName);
    if (static_cast<std::size_t>(n) + 1u + nameLen + 1u > outCap) return false;
    out[n] = L'\\';
    ::wmemcpy(out + n + 1u, dllName, nameLen + 1u);
    return true;
}

std::wstring DirOfSelf(HMODULE self) {
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD n = ::GetModuleFileNameW(self, buf, static_cast<DWORD>(std::size(buf)));
    if (n == 0) return {};
    std::wstring full(buf, buf + n);
    auto pos = full.find_last_of(L"\\/");
    return (pos == std::wstring::npos) ? std::wstring() : full.substr(0, pos);
}

// Minimal ntdll DLL-load notification ABI (Vista+ stable) for event-driven
// backend discovery (A-M5). Avoids polling timeouts when a game sits at a
// Unity resolution dialog, launcher splash, or slow cold start for >5 s.
constexpr ULONG kLdrDllNotificationReasonLoaded = 1;
using fn_LdrDllNotification = VOID(CALLBACK*)(ULONG reason, const void* data, PVOID context);
using fn_LdrRegisterDllNotification = LONG(NTAPI*)(
    ULONG flags, fn_LdrDllNotification callback, PVOID context, PVOID* cookie);
using fn_LdrUnregisterDllNotification = LONG(NTAPI*)(PVOID cookie);

VOID CALLBACK OnDllLoadedNotify(ULONG reason, const void*, PVOID context) {
    if (reason == kLdrDllNotificationReasonLoaded && context != nullptr) {
        // Pure kernel SetEvent — safe under loader lock, zero heap/CRT use.
        ::SetEvent(static_cast<HANDLE>(context));
    }
}

void LogProxyLine(const char* line) noexcept {
    try {
        Discrete::Log::Init();
        Discrete::Log::Init(line);
    } catch (...) {
    }
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
    if (proxyDir.empty()) {
        char msg[160];
        std::snprintf(msg, sizeof(msg),
                      "[PROXY] ERROR: GetModuleFileNameW failed (GetLastError=%lu); "
                      "cannot locate sibling backend DLL.",
                      ::GetLastError());
        LogProxyLine(msg);
        return 1;
    }

    // A-M5: wait for Unity's runtime DLL (mono* / GameAssembly.dll /
    // UserAssembly.dll) without a brittle 5-second cutoff. Register an ntdll
    // DLL-load notification when available so we wake instantaneously the moment
    // the engine loads (even after a multi-minute resolution dialog or splash
    // screen), backed by capped exponential backoff so idle wait uses ~0% CPU.
    HANDLE dllEvent = ::CreateEventW(nullptr, FALSE /*auto-reset*/, FALSE, nullptr);
    PVOID notifyCookie = nullptr;
    fn_LdrUnregisterDllNotification unregisterFn = nullptr;
    if (dllEvent != nullptr) {
        if (HMODULE ntdll = ::GetModuleHandleW(L"ntdll.dll")) {
            auto regFn = reinterpret_cast<fn_LdrRegisterDllNotification>(
                ::GetProcAddress(ntdll, "LdrRegisterDllNotification"));
            unregisterFn = reinterpret_cast<fn_LdrUnregisterDllNotification>(
                ::GetProcAddress(ntdll, "LdrUnregisterDllNotification"));
            if (regFn != nullptr && unregisterFn != nullptr) {
                if (regFn(0, &OnDllLoadedNotify, dllEvent, &notifyCookie) != 0) {
                    notifyCookie = nullptr;
                }
            }
        }
    }

    const ULONGLONG startMs = ::GetTickCount64();
    bool loggedWaiting = false;
    DWORD waitMs = 100;
    BackendDetector::Backend backend = BackendDetector::Backend::Unknown;
    for (;;) {
        auto d = BackendDetector::Detect();
        if (d.backend != BackendDetector::Backend::Unknown) {
            backend = d.backend;
            break;
        }
        if (!loggedWaiting && (::GetTickCount64() - startMs) >= 5000ull) {
            loggedWaiting = true;
            LogProxyLine("[PROXY] INFO: still waiting for Unity runtime (mono / GameAssembly) to load...");
        }
        if (dllEvent != nullptr && notifyCookie != nullptr) {
            ::WaitForSingleObject(dllEvent, waitMs);
        } else {
            ::Sleep(waitMs);
        }
        if (waitMs < 5000u) {
            waitMs = (waitMs < 2500u) ? (waitMs * 2u) : 5000u;
        }
    }

    if (notifyCookie != nullptr && unregisterFn != nullptr) {
        unregisterFn(notifyCookie);
    }
    if (dllEvent != nullptr) {
        ::CloseHandle(dllEvent);
    }

    const wchar_t* backendLeafW = (backend == BackendDetector::Backend::IL2CPP)
        ? L"Discrete-IL2CPP.dll"
        : L"Discrete-Mono.dll";
    const char* backendLeafA = (backend == BackendDetector::Backend::IL2CPP)
        ? "Discrete-IL2CPP.dll"
        : "Discrete-Mono.dll";

    std::wstring backendDll = proxyDir + L"\\" + backendLeafW;
    HMODULE h = ::LoadLibraryW(backendDll.c_str());
    if (h == nullptr) {
        DWORD err = ::GetLastError();
        char msg[256];
        std::snprintf(msg, sizeof(msg),
                      "[PROXY] ERROR: LoadLibraryW failed for %s (GetLastError=%lu). "
                      "Verify the matching %s backend DLL is deployed next to the proxy.",
                      backendLeafA, err,
                      (sizeof(void*) == 4) ? "x86" : "x64");
        LogProxyLine(msg);
        return 3;
    }
    return 0;
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

    // Slow path: load the real system DLL. Use the absolute System32 path in a
    // stack buffer (allocation-free) so the loader never picks up our proxy
    // again under the same name and cannot throw std::bad_alloc in noexcept.
    wchar_t path[MAX_PATH * 2] = {};
    if (!BuildSystemDllPath(dllName, path, std::size(path))) return nullptr;
    HMODULE h = ::LoadLibraryW(path);

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
