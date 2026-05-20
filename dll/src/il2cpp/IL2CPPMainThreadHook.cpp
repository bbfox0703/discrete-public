// IL2CPPMainThreadHook.cpp — Phase 20-MT (IL2CPP backend).
//
// Mirror of MonoMainThreadHook.cpp. See that file's header comment for the
// full architectural explanation — the only differences here are the
// runtime function symbol (il2cpp_runtime_invoke) and the IL2CPP-typed
// signature (const MethodInfo* + Il2CppObject* + Il2CppException**).

#include "IL2CPPMainThreadHook.h"

#include "IL2CPPResolver.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/MainThreadDispatcher.h"

#include <MinHook.h>
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <string>

namespace UnityCEDumper::IL2CPP::MainThreadHook {

namespace {

template <typename... Args>
void LogF(const char* fmt, Args... args) {
    char buf[512];
    std::snprintf(buf, sizeof(buf), fmt, args...);
    UnityCEDumper::Log::Write(UnityCEDumper::MagicStrings::LogCategoryInit, buf);
}

std::atomic<bool> g_installed{false};
std::atomic<bool> g_minhook_initialized{false};

using fn_runtime_invoke_t = UnityCEDumper::IL2CPP::Il2CppObject* (*)(
    const UnityCEDumper::IL2CPP::MethodInfo*,
    void*,
    void**,
    UnityCEDumper::IL2CPP::Il2CppException**);
fn_runtime_invoke_t g_orig_runtime_invoke = nullptr;

UnityCEDumper::IL2CPP::Il2CppObject* WINAPI HookedRuntimeInvoke(
    const UnityCEDumper::IL2CPP::MethodInfo* method,
    void* obj,
    void** params,
    UnityCEDumper::IL2CPP::Il2CppException** exc)
{
    using namespace UnityCEDumper::MainThreadDispatcher;
    const DWORD tid = ::GetCurrentThreadId();
    if (tid == GetMainThreadId()) {
        NotifyMainThreadTick();
    }
    if (g_orig_runtime_invoke == nullptr) {
        if (exc) *exc = nullptr;
        return nullptr;
    }
    return g_orig_runtime_invoke(method, obj, params, exc);
}

}  // namespace

bool Install() {
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return true;
    }

    if (!UnityCEDumper::MainThreadDispatcher::IsInitialized()) {
        LogF("[IL2CPPMainThreadHook] Install: MainThreadDispatcher not initialized");
        g_installed.store(false);
        return false;
    }

    const auto& resolver = UnityCEDumper::IL2CPP::GetResolver();
    if (resolver.runtime_invoke == nullptr) {
        LogF("[IL2CPPMainThreadHook] Install: il2cpp_runtime_invoke not bound");
        g_installed.store(false);
        return false;
    }

    bool needsInit = false;
    if (!g_minhook_initialized.exchange(true)) {
        needsInit = true;
    }
    if (needsInit) {
        MH_STATUS s = ::MH_Initialize();
        if (s != MH_OK && s != MH_ERROR_ALREADY_INITIALIZED) {
            LogF("[IL2CPPMainThreadHook] MH_Initialize failed: status=%d", static_cast<int>(s));
            g_installed.store(false);
            g_minhook_initialized.store(false);
            return false;
        }
    }

    void* target = reinterpret_cast<void*>(resolver.runtime_invoke);
    MH_STATUS hs = ::MH_CreateHook(
        target,
        reinterpret_cast<void*>(&HookedRuntimeInvoke),
        reinterpret_cast<void**>(&g_orig_runtime_invoke));
    if (hs != MH_OK) {
        LogF("[IL2CPPMainThreadHook] MH_CreateHook(il2cpp_runtime_invoke) failed: status=%d", static_cast<int>(hs));
        g_installed.store(false);
        return false;
    }
    hs = ::MH_EnableHook(target);
    if (hs != MH_OK) {
        LogF("[IL2CPPMainThreadHook] MH_EnableHook failed: status=%d", static_cast<int>(hs));
        ::MH_RemoveHook(target);
        g_orig_runtime_invoke = nullptr;
        g_installed.store(false);
        return false;
    }
    LogF("[IL2CPPMainThreadHook] Install: hook armed on il2cpp_runtime_invoke @ %p (main_tid=%lu)",
         target, UnityCEDumper::MainThreadDispatcher::GetMainThreadId());
    return true;
}

void Uninstall() {
    if (!g_installed.load()) return;
    const auto& resolver = UnityCEDumper::IL2CPP::GetResolver();
    if (resolver.runtime_invoke != nullptr) {
        void* target = reinterpret_cast<void*>(resolver.runtime_invoke);
        ::MH_DisableHook(target);
        ::MH_RemoveHook(target);
    }
    g_orig_runtime_invoke = nullptr;
    g_installed.store(false);
    LogF("[IL2CPPMainThreadHook] Uninstall: hook removed");
}

bool IsInstalled() {
    return g_installed.load();
}

}  // namespace UnityCEDumper::IL2CPP::MainThreadHook
