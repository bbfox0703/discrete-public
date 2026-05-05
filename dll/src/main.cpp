// main.cpp — DllMain entry point.
// Logs DLL load, lists modules, auto-starts the pipe server (Phase 1).

#include "BuildInfo.h"
#include "shared/ExportAPI.h"
#include "shared/Logger.h"
#include "shared/MagicStrings.h"
#include "shared/PipeServer.h"

#include <windows.h>
#include <psapi.h>

#include <sstream>
#include <string>

namespace {

constexpr const char* BackendName() {
#if defined(UNITYCEDUMPER_BACKEND_MONO)
    return "Mono";
#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)
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
            UnityCEDumper::Log::Write(L"init", "  module: " + Utf8FromWide(name));
        }
    }
}

// Phase 12b: per-process named mutex used as a "first injection wins"
// gate. Both UnityDumper-Mono.dll and UnityDumper-IL2CPP.dll, plus the
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
                   L"Global\\UnityCEDumper-Init-PID-%lu",
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

void OnAttach(HMODULE self) {
    UnityCEDumper::Log::Init();

    if (!TryClaimSingleInit()) {
        std::ostringstream os;
        os << "UnityDumper DLL skipping init: another backend already "
              "claimed PID " << ::GetCurrentProcessId()
           << " (this build=" UNITYCEDUMPER_BUILD_BANNER
           << ", backend=" << BackendName() << ")";
        UnityCEDumper::Log::Init(os.str());
        ::DisableThreadLibraryCalls(self);
        return;
    }

    // Banner with full version + git short + build timestamp + Debug/Release
    // so each per-PID init log unambiguously identifies which DLL build wrote
    // it. The same string is embedded into the PE's VERSIONINFO resource via
    // dll/src/shared/version.rc.in.
    std::ostringstream os;
    os << "UnityDumper DLL attached "
       << UNITYCEDUMPER_BUILD_BANNER
       << " backend=" << BackendName()
       << " pid=" << ::GetCurrentProcessId();
    UnityCEDumper::Log::Init(os.str());

    UnityCEDumper::Log::Write(L"init", "Loaded modules:");
    LogLoadedModules();

    // Suppress DLL_THREAD_ATTACH/DETACH for performance.
    ::DisableThreadLibraryCalls(self);

    // Auto-bring up the pipe server. Doing real work in DllMain is risky, so
    // UnityDumper_StartPipe just spawns a worker thread — the actual pipe
    // creation happens off DllMain.
    if (UnityDumper_Init() == 0 && UnityDumper_StartPipe() == 0) {
        UnityCEDumper::Log::Init("Pipe server thread started");
    } else {
        UnityCEDumper::Log::Init("Pipe server failed to start");
    }
}

void OnDetach() {
    UnityCEDumper::Log::Init("UnityDumper DLL detaching");
    UnityDumper_Shutdown();
    if (g_initMutex != nullptr) {
        ::ReleaseMutex(g_initMutex);
        ::CloseHandle(g_initMutex);
        g_initMutex = nullptr;
    }
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            OnAttach(hModule);
            break;
        case DLL_PROCESS_DETACH:
            OnDetach();
            break;
        default:
            break;
    }
    return TRUE;
}
