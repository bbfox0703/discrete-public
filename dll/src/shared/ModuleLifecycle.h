// ModuleLifecycle.h — pure Win32 module-lifetime helpers shared between
// main.cpp and offline C++ self-tests (dll/tests/dll_core_test.cpp).
//
// Invariant (A-H1):
//   Once a backend DLL claims the per-PID single-init mutex, it pins itself
//   for the host process lifetime via GET_MODULE_HANDLE_EX_FLAG_PIN. External
//   FreeLibrary calls (CE "unload module", Process Hacker, third-party
//   injectors) therefore become safe no-ops instead of running DLL_PROCESS_DETACH
//   under the OS loader lock or unmapping code under live pipe workers /
//   MinHook detours. The sole supported teardown is the explicit off-DllMain
//   Discrete_Shutdown() export (used by unity_dump.lua).

#pragma once

#include <windows.h>

namespace Discrete::ModuleLifecycle {

/// Pin the module containing `addressInModule` in the current process so
/// subsequent FreeLibrary() calls cannot decrement its load count to zero or
/// unmap its code pages. Safe to call from DllMain(DLL_PROCESS_ATTACH):
/// GetModuleHandleExW(PIN | FROM_ADDRESS) only inspects the already-held loader
/// state and marks the LDR entry pinned.
inline bool PinModuleContaining(const void* addressInModule,
                                HMODULE* outModule = nullptr) noexcept {
    if (addressInModule == nullptr) return false;
    HMODULE mod = nullptr;
    if (!::GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(addressInModule),
            &mod) ||
        mod == nullptr) {
        return false;
    }
    if (outModule != nullptr) *outModule = mod;
    return true;
}

/// Atomically claim a per-process named mutex without taking thread ownership
/// (`bInitialOwner = FALSE`). Ownership is never waited on or released — the
/// kernel object's existence (`ERROR_ALREADY_EXISTS`) is the sole single-init
/// gate, so no thread exit can leave an abandoned mutex and no cross-thread
/// ReleaseMutex can fail with ERROR_NOT_OWNER.
/// Returns the claimed HANDLE on success, or nullptr if already claimed / failed.
inline HANDLE TryClaimProcessMutex(const wchar_t* name) noexcept {
    if (name == nullptr || *name == L'\0') return nullptr;
    HANDLE h = ::CreateMutexW(nullptr, /*bInitialOwner=*/FALSE, name);
    if (h == nullptr) return nullptr;
    if (::GetLastError() == ERROR_ALREADY_EXISTS) {
        ::CloseHandle(h);
        return nullptr;
    }
    return h;
}

}  // namespace Discrete::ModuleLifecycle
