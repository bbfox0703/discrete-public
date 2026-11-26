// main.cpp — DllMain entry point.
// Logs DLL load, lists modules, auto-starts the pipe server (Phase 1).

#include "BuildInfo.h"
#include "shared/ExportAPI.h"
#include "shared/Logger.h"
#include "shared/MagicStrings.h"
#include "shared/ModuleLifecycle.h"
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

// Runs under the loader lock, so the whole list goes out as ONE Write (one
// file open/close) rather than one per module.
void LogLoadedModules() {
    HMODULE mods[1024];
    DWORD cbNeeded = 0;
    HANDLE proc = ::GetCurrentProcess();
    if (!::EnumProcessModules(proc, mods, sizeof(mods), &cbNeeded)) return;

    // cbNeeded is the full need even when the buffer was too small.
    DWORD count = cbNeeded / sizeof(HMODULE);
    if (count > _countof(mods)) count = static_cast<DWORD>(_countof(mods));

    std::string text = "Loaded modules:";
    text.reserve(32 + static_cast<size_t>(count) * 40);
    for (DWORD i = 0; i < count; ++i) {
        wchar_t name[MAX_PATH];
        if (::GetModuleBaseNameW(proc, mods[i], name, MAX_PATH) > 0) {
            text += "\n  module: ";
            text += Utf8FromWide(name);
        }
    }
    Discrete::Log::Write(Discrete::MagicStrings::LogCategoryInit, text);
}

// Phase 12b: per-process named mutex used as a "first injection wins"
// gate. Both Discrete-Mono.dll and Discrete-IL2CPP.dll run this OnAttach.
// Without the gate, two concurrently-loaded backend DLLs would each try to
// bind the named pipe and the second would fail noisily; or worse, a second
// copy of the same DLL (loaded via proxy and also via the UI's Pick & inject)
// would re-init handler tables. The mutex makes Init exactly-once per host
// PID regardless of who loaded us. Held open for process lifetime (OS
// reclaims on process exit).
HANDLE g_initMutex = nullptr;

bool TryClaimSingleInit() noexcept {
    wchar_t name[64];
    ::_snwprintf_s(name, _countof(name), _TRUNCATE,
                   L"Global\\Discrete-Init-PID-%lu",
                   ::GetCurrentProcessId());
    g_initMutex = Discrete::ModuleLifecycle::TryClaimProcessMutex(name);
    return g_initMutex != nullptr;
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

    // A-H1: pin this module for the host process lifetime so external
    // FreeLibrary() calls (CE "unload module", Process Hacker, third-party
    // injectors) cannot unmap code pages under live pipe workers or MinHook
    // detours, nor trigger cleanup under the OS loader lock. The sole
    // supported teardown is the explicit off-DllMain Discrete_Shutdown()
    // export (used by unity_dump.lua), which leaves the module mapped.
    if (Discrete::ModuleLifecycle::PinModuleContaining(
            reinterpret_cast<const void*>(&OnAttach))) {
        Discrete::Log::Init("Module pinned (GET_MODULE_HANDLE_EX_FLAG_PIN)");
    } else {
        Discrete::Log::Init("Warning: failed to pin module via GetModuleHandleExW");
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
    // A C++ exception escaping a raw thread proc is unhandled and kills the
    // host game; retention is best-effort housekeeping, so contain it. The
    // note sits in its own try because the likely cause (bad_alloc) can hit
    // Write() too. The module reference is released either way.
    try {
        Discrete::Log::RunRetentionSweep();
    } catch (...) {
        try {
            Discrete::Log::Init("Log retention sweep aborted: exception contained");
        } catch (...) {
        }
    }
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

}  // namespace

// DllMain — entry point.
//
// DLL_PROCESS_DETACH is an intentional no-op on BOTH process termination
// (lpReserved != nullptr) AND FreeLibrary (lpReserved == nullptr):
//   * OnAttach pins the active backend module via GET_MODULE_HANDLE_EX_FLAG_PIN,
//     so external FreeLibrary calls cannot unload it.
//   * Even if pinning failed (or on a dormant second copy that lost single-init),
//     DllMain executes under the OS loader lock: joining/draining pipe workers
//     or calling MH_RemoveHook here inevitably deadlocks against
//     LdrShutdownThread or leaves detached workers returning into unmapped pages.
//   * Graceful teardown runs exclusively through the explicit off-DllMain
//     Discrete_Shutdown() export (scripts/unity_dump.lua), which stops the pipe
//     server and disables hooks while keeping the module mapped.
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    (void)lpReserved;
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            OnAttach(hModule);
            break;
        case DLL_PROCESS_DETACH:
            break;
        default:
            break;
    }
    return TRUE;
}
