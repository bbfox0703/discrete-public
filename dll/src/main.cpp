// main.cpp — DllMain entry point.
// Logs DLL load, lists modules, auto-starts the pipe server (Phase 1).

#include "BuildInfo.h"
#include "shared/ExportAPI.h"
#include "shared/Logger.h"
#include "shared/MagicStrings.h"
#include "shared/PipeServer.h"
#include "shared/RustCore.h"

#include <windows.h>
#include <psapi.h>

#include <sstream>
#include <string>

namespace {

constexpr const char* BackendName() {
#if defined(DISCRETE_BACKEND_MONO)
    return "Mono";
#elif defined(DISCRETE_BACKEND_IL2CPP)
    return "IL2CPP";
#else
    return "Unknown";
#endif
}

std::string Utf8FromWide(const wchar_t* w) {
    if (!w || !*w) return {};
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string out(static_cast<size_t>(n - 1), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, w, -1, out.data(), n, nullptr, nullptr);
    return out;
}

void LogLoadedModules() {
    HMODULE mods[1024];
    DWORD cbNeeded = 0;
    HANDLE proc = ::GetCurrentProcess();
    if (!::EnumProcessModules(proc, mods, sizeof(mods), &cbNeeded)) return;

    DWORD count = cbNeeded / sizeof(HMODULE);
    for (DWORD i = 0; i < count; ++i) {
        wchar_t name[MAX_PATH];
        if (::GetModuleBaseNameW(proc, mods[i], name, MAX_PATH) > 0) {
            Discrete::Log::Write(L"init", "  module: " + Utf8FromWide(name));
        }
    }
}

// Phase 12b: per-process named mutex used as a "first injection wins"
// gate. Both Discrete-Mono.dll and Discrete-IL2CPP.dll, plus the
// proxy DLLs, all run this same OnAttach. Without the gate, two
// concurrently-loaded backend DLLs would each try to bind the named pipe
// and the second would fail noisily; or worse, the second copy of the
// same DLL (loaded via the proxy and also via the UI's Pick & inject)
// would re-init handler tables. The mutex makes Init exactly-once per
// host PID regardless of who loaded us.
HANDLE g_initMutex = nullptr;

bool TryClaimSingleInit() noexcept {
    wchar_t name[64];
    ::_snwprintf_s(name, _countof(name), _TRUNCATE,
                   L"Global\\Discrete-Init-PID-%lu",
                   ::GetCurrentProcessId());
    g_initMutex = ::CreateMutexW(nullptr, /*bInitialOwner*/ TRUE, name);
    if (g_initMutex == nullptr) return false;
    if (::GetLastError() == ERROR_ALREADY_EXISTS) {
        // Someone else (a different backend DLL, or a re-injection) has
        // already initialised this process. Release our handle and bail.
        ::CloseHandle(g_initMutex);
        g_initMutex = nullptr;
        return false;
    }
    return true;
}

void StartRetentionSweep();

void OnAttach(HMODULE self) {
    Discrete::Log::Init();

    if (!TryClaimSingleInit()) {
        std::ostringstream os;
        os << "Discrete DLL skipping init: another backend already "
              "claimed PID " << ::GetCurrentProcessId()
           << " (this build=" DISCRETE_BUILD_BANNER
           << ", backend=" << BackendName() << ")";
        Discrete::Log::Init(os.str());
        ::DisableThreadLibraryCalls(self);
        return;
    }

    // Banner with full version + git short + build timestamp + Debug/Release
    // so each per-PID init log unambiguously identifies which DLL build wrote
    // it. The same string is embedded into the PE's VERSIONINFO resource via
    // dll/src/shared/version.rc.in.
    std::ostringstream os;
    os << "Discrete DLL attached "
       << DISCRETE_BUILD_BANNER
       << " backend=" << BackendName()
       << " pid=" << ::GetCurrentProcessId();
    Discrete::Log::Init(os.str());

    // Phase R0: route Rust-side log lines into the same per-PID logs and
    // record the Rust core banner (proves the Rust half linked and ran).
    Discrete::RustCore::Init();

    Discrete::Log::Write(L"init", "Loaded modules:");
    LogLoadedModules();

    // Suppress DLL_THREAD_ATTACH/DETACH for performance.
    ::DisableThreadLibraryCalls(self);

    // Auto-bring up the pipe server. Doing real work in DllMain is risky, so
    // Discrete_StartPipe just spawns a worker thread — the actual pipe
    // creation happens off DllMain.
    if (Discrete_Init() == 0 && Discrete_StartPipe() == 0) {
        Discrete::Log::Init("Pipe server thread started");
    } else {
        Discrete::Log::Init("Pipe server failed to start");
    }

    StartRetentionSweep();
}

// Age-based log retention (Logger.h) walks the log root, so it must not run
// under the loader lock. A short-lived thread does it once per process. The
// thread holds its own module reference and drops it with
// FreeLibraryAndExitThread, so an eject while the sweep runs cannot unmap
// the code under it.
DWORD WINAPI RetentionSweepThread(LPVOID moduleRef) {
    Discrete::Log::RunRetentionSweep();
    ::FreeLibraryAndExitThread(static_cast<HMODULE>(moduleRef), 0);
}

void StartRetentionSweep() {
    HMODULE ref = nullptr;
    if (!::GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                              reinterpret_cast<LPCWSTR>(&RetentionSweepThread), &ref)) {
        return;
    }
    HANDLE h = ::CreateThread(nullptr, 0, &RetentionSweepThread, ref, 0, nullptr);
    if (h != nullptr) {
        ::CloseHandle(h);
        return;
    }
    // CreateThread failed: retention is skipped for this run (the UI's sweep
    // still covers it). The extra module reference is deliberately kept —
    // FreeLibrary from inside DllMain is the riskier of the two.
    Discrete::Log::Init("Log retention sweep skipped: CreateThread failed");
}

void OnDetach() {
    Discrete::Log::Init("Discrete DLL detaching");
    Discrete_Shutdown();
    if (g_initMutex != nullptr) {
        ::ReleaseMutex(g_initMutex);
        ::CloseHandle(g_initMutex);
        g_initMutex = nullptr;
    }
}

}  // namespace

// DllMain — entry point. lpReserved tells us WHY DLL_PROCESS_DETACH fired:
//   lpReserved == NULL   → FreeLibrary called → we should clean up gracefully
//                          (process keeps running; leaving hooks pointing at
//                          unmapped memory would crash the host)
//   lpReserved != NULL   → process is terminating → SKIP all cleanup, the
//                          OS reclaims everything. Cleanup at this point is
//                          actively harmful because:
//                            (a) `std::thread::join()` in PipeServer::Stop
//                                deadlocks under the loader lock (DllMain
//                                holds it; the joinee thread can't make
//                                progress on Windows operations needing the
//                                same lock).
//                            (b) MinHook's MH_RemoveHook SuspendThreads on
//                                every other thread in the dying process
//                                to verify nobody's in the trampoline —
//                                slow (Unity has dozens of threads) and
//                                potentially deadlocks against
//                                TerminateProcess.
//                          Observed live 2026-05-20 against SaGa F2R: ~10s
//                          delay between window-close and process-exit
//                          when the DLL is injected. Fixed by short-
//                          circuiting here.
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            OnAttach(hModule);
            break;
        case DLL_PROCESS_DETACH:
            if (lpReserved != nullptr) {
                // Process termination — let the OS sweep. Returning TRUE
                // immediately is the documented best practice.
                break;
            }
            OnDetach();
            break;
        default:
            break;
    }
    return TRUE;
}
