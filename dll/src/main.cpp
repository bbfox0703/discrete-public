// main.cpp — DllMain entry point.
// Phase 0: logs DLL load, identifies which backend variant was built, lists loaded modules.

#include "shared/Logger.h"
#include "shared/MagicStrings.h"

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

void OnAttach(HMODULE self) {
    UnityCEDumper::Log::Init();

    std::ostringstream os;
    os << "UnityDumper DLL attached (backend=" << BackendName()
       << ", build=" << UNITYCEDUMPER_BUILD_NUMBER
       << ", pid=" << ::GetCurrentProcessId() << ")";
    UnityCEDumper::Log::Init(os.str());

    UnityCEDumper::Log::Write(L"init", "Loaded modules:");
    LogLoadedModules();

    // Suppress DLL_THREAD_ATTACH/DETACH for performance.
    ::DisableThreadLibraryCalls(self);
}

void OnDetach() {
    UnityCEDumper::Log::Init("UnityDumper DLL detaching");
    UnityCEDumper::Log::Shutdown();
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
