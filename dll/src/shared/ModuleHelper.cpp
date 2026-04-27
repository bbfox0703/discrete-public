// ModuleHelper.cpp — PSAPI-backed module enumeration / export resolution.

#include "ModuleHelper.h"

#include <windows.h>
#include <psapi.h>

namespace UnityCEDumper::ModuleHelper {

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
    HMODULE mods[1024];
    DWORD cbNeeded = 0;
    if (!::EnumProcessModules(proc, mods, sizeof(mods), &cbNeeded)) return result;

    DWORD count = cbNeeded / sizeof(HMODULE);
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

}  // namespace UnityCEDumper::ModuleHelper
