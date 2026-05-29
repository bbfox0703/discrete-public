// ModuleHelper.h — module / export resolution wrappers around Win32 PSAPI.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::ModuleHelper {

struct ModuleInfo {
    std::wstring name;          // Base name, e.g. L"GameAssembly.dll"
    std::wstring path;          // Full path on disk
    void*        baseAddress;   // Image base in process
    std::size_t  sizeOfImage;   // Image size from NT headers
};

// Returns image base of a module already loaded in this process, or nullptr if not loaded.
void* GetModuleBase(const wchar_t* moduleName) noexcept;

// Returns size of a module's image, or 0 if not loaded.
std::size_t GetModuleSize(const wchar_t* moduleName) noexcept;

// Resolves an export by name. Returns nullptr if module not loaded or export missing.
void* ResolveExport(const wchar_t* moduleName, const char* exportName) noexcept;

// Enumerates all currently loaded modules in the host process.
std::vector<ModuleInfo> EnumerateModules();

}  // namespace Discrete::ModuleHelper
