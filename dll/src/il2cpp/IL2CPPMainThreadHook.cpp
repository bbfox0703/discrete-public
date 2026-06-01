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

namespace Discrete::IL2CPP::MainThreadHook {

namespace {

template <typename... Args>
void LogF(const char* fmt, Args... args) {
    char buf[512];
    std::snprintf(buf, sizeof(buf), fmt, args...);
    Discrete::Log::Write(Discrete::MagicStrings::LogCategoryInit, buf);
}

std::atomic<bool> g_installed{false};
std::atomic<bool> g_minhook_initialized{false};

// Calling convention MUST stay cdecl-compatible — IL2CPP's runtime
// invoke export is C, defaults to __cdecl on x86. There's no shipping
// IL2CPP x86 target right now (Unity 2019+ dropped x86 IL2CPP), but
// keep the symmetry with MonoMainThreadHook so a future 32-bit revival
// (or any niche x86 IL2CPP target that appears) doesn't trip the same
// stack double-pop trap. See MonoMainThreadHook.cpp for the forensic.
using fn_runtime_invoke_t = Discrete::IL2CPP::Il2CppObject* (*)(
    const Discrete::IL2CPP::MethodInfo*,
    void*,
    void**,
    Discrete::IL2CPP::Il2CppException**);
fn_runtime_invoke_t g_orig_runtime_invoke = nullptr;

// Threads currently executing inside HookedRuntimeInvoke. Uninstall() drains
// this to zero (after MH_DisableHook stops new entries) BEFORE MH_RemoveHook
// frees the trampoline and before the DLL can be unloaded — otherwise the
// game main thread (which calls il2cpp_runtime_invoke every frame) is almost
// always inside the detour at unload time, and unmapping it crashes the game.
std::atomic<int> g_inDetour{0};

Discrete::IL2CPP::Il2CppObject* HookedRuntimeInvoke(
    const Discrete::IL2CPP::MethodInfo* method,
    void* obj,
    void** params,
    Discrete::IL2CPP::Il2CppException** exc)
{
    using namespace Discrete::MainThreadDispatcher;
    // Mark presence FIRST so Uninstall's drain can't miss us. After
    // MH_DisableHook no new caller reaches here, so the count only falls.
    g_inDetour.fetch_add(1, std::memory_order_acquire);
    const DWORD tid = ::GetCurrentThreadId();
    if (tid == GetMainThreadId()) {
        NotifyMainThreadTick();
    }
    Discrete::IL2CPP::Il2CppObject* ret = nullptr;
    if (g_orig_runtime_invoke != nullptr) {
        ret = g_orig_runtime_invoke(method, obj, params, exc);
    } else if (exc) {
        *exc = nullptr;
    }
    g_inDetour.fetch_sub(1, std::memory_order_release);
    return ret;
}

}  // namespace

bool Install() {
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return true;
    }

    if (!Discrete::MainThreadDispatcher::IsInitialized()) {
        LogF("[IL2CPPMainThreadHook] Install: MainThreadDispatcher not initialized");
        g_installed.store(false);
        return false;
    }

    const auto& resolver = Discrete::IL2CPP::GetResolver();
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
         target, Discrete::MainThreadDispatcher::GetMainThreadId());
    return true;
}

void Uninstall() {
    if (!g_installed.load()) return;
    const auto& resolver = Discrete::IL2CPP::GetResolver();
    if (resolver.runtime_invoke != nullptr) {
        void* target = reinterpret_cast<void*>(resolver.runtime_invoke);
        // 1. Stop NEW calls reaching the detour (restores original bytes).
        ::MH_DisableHook(target);
        // 2. Drain threads still INSIDE HookedRuntimeInvoke before we free
        //    the trampoline / let the DLL be unmapped. New callers bypass the
        //    detour now, so g_inDetour only decreases. Cap is a safety bound
        //    (a single in-flight managed invoke that runs long); the common
        //    per-frame case drains in well under a frame.
        for (int i = 0; i < 2000 && g_inDetour.load(std::memory_order_acquire) > 0; ++i) {
            ::Sleep(1);
        }
        // 3. Now safe to free the trampoline.
        ::MH_RemoveHook(target);
    }
    g_orig_runtime_invoke = nullptr;
    g_installed.store(false);
    LogF("[IL2CPPMainThreadHook] Uninstall: hook removed");
}

bool IsInstalled() {
    return g_installed.load();
}

}  // namespace Discrete::IL2CPP::MainThreadHook
