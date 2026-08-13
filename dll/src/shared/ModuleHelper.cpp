// ModuleHelper.cpp — PSAPI-backed module enumeration / export resolution.

#include "ModuleHelper.h"

#include <windows.h>
#include <psapi.h>

namespace Discrete::ModuleHelper {

void* GetModuleBase(const wchar_t* moduleName) noexcept {
    return reinterpret_cast<void*>(::GetModuleHandleW(moduleName));
}

std::size_t GetModuleSize(const wchar_t* moduleName) noexcept {
    HMODULE mod = ::GetModuleHandleW(moduleName);
    if (!mod) return 0;
    MODULEINFO mi{};
    if (!::GetModuleInformation(::GetCurrentProcess(), mod, &mi, sizeof(mi))) return 0;
    return mi.SizeOfImage;
}

void* ResolveExport(const wchar_t* moduleName, const char* exportName) noexcept {
    HMODULE mod = ::GetModuleHandleW(moduleName);
    if (!mod || !exportName) return nullptr;
    return reinterpret_cast<void*>(::GetProcAddress(mod, exportName));
}

std::vector<ModuleInfo> EnumerateModules() {
    std::vector<ModuleInfo> result;
    HANDLE proc = ::GetCurrentProcess();

    // EnumProcessModules reports in cbNeeded the bytes required for ALL modules,
    // NOT the bytes it wrote. Dividing that by sizeof(HMODULE) and looping over
    // a fixed 1024-entry stack array therefore read PAST THE END of that array
    // in any process with more than 1024 modules, handing garbage HMODULEs to
    // GetModuleBaseNameW / GetModuleInformation. Grow until the buffer is big
    // enough instead — the documented way to use this API.
    std::vector<HMODULE> mods(1024);
    DWORD cbNeeded = 0;
    for (;;) {
        const DWORD cb = static_cast<DWORD>(mods.size() * sizeof(HMODULE));
        if (!::EnumProcessModules(proc, mods.data(), cb, &cbNeeded)) return result;
        if (cbNeeded <= cb) break;                       // everything fitted
        mods.resize(cbNeeded / sizeof(HMODULE) + 16);    // headroom for churn
    }

    DWORD count = cbNeeded / sizeof(HMODULE);
    if (count > mods.size()) count = static_cast<DWORD>(mods.size());   // belt and braces
    result.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        wchar_t baseName[MAX_PATH] = {};
        wchar_t fullPath[MAX_PATH] = {};
        ::GetModuleBaseNameW(proc, mods[i], baseName, MAX_PATH);
        ::GetModuleFileNameExW(proc, mods[i], fullPath, MAX_PATH);

        MODULEINFO mi{};
        ::GetModuleInformation(proc, mods[i], &mi, sizeof(mi));

        result.push_back(ModuleInfo{
            baseName,
            fullPath,
            mi.lpBaseOfDll,
            mi.SizeOfImage,
        });
    }
    return result;
}

}  // namespace Discrete::ModuleHelper
