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
// Atomic so HookedRuntimeInvoke and Install never race, and NEVER cleared back
// to nullptr once created (A-M7): retaining the trampoline closes the entry
// race where a thread entered HookedRuntimeInvoke before MH_DisableHook patched
// the prologue and before g_inDetour.fetch_add(1).
std::atomic<fn_runtime_invoke_t> g_orig_runtime_invoke{nullptr};
void* g_hookTarget = nullptr;

// Threads currently executing inside HookedRuntimeInvoke. Uninstall() drains
// this to zero with a real wall-clock deadline via GetTickCount64() (A-M7).
std::atomic<int> g_inDetour{0};

constexpr std::uint64_t kUninstallDrainTimeoutMs = 2000;

Discrete::IL2CPP::Il2CppObject* HookedRuntimeInvoke(
    const Discrete::IL2CPP::MethodInfo* method,
    void* obj,
    void** params,
    Discrete::IL2CPP::Il2CppException** exc)
{
    using namespace Discrete::MainThreadDispatcher;
    // Mark presence FIRST so Uninstall's drain observes us.
    g_inDetour.fetch_add(1, std::memory_order_acquire);
    const DWORD tid = ::GetCurrentThreadId();
    if (tid == GetMainThreadId()) {
        NotifyMainThreadTick();
    }
    Discrete::IL2CPP::Il2CppObject* ret = nullptr;
    fn_runtime_invoke_t orig = g_orig_runtime_invoke.load(std::memory_order_acquire);
    if (orig != nullptr) {
        ret = orig(method, obj, params, exc);
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
    if (g_hookTarget != target) {
        void* orig = nullptr;
        MH_STATUS hs = ::MH_CreateHook(
            target,
            reinterpret_cast<void*>(&HookedRuntimeInvoke),
            &orig);
        if (hs != MH_OK && hs != MH_ERROR_ALREADY_CREATED) {
            LogF("[IL2CPPMainThreadHook] MH_CreateHook(il2cpp_runtime_invoke) failed: status=%d", static_cast<int>(hs));
            g_installed.store(false);
            return false;
        }
        if (orig != nullptr) {
            g_orig_runtime_invoke.store(reinterpret_cast<fn_runtime_invoke_t>(orig), std::memory_order_release);
        }
        g_hookTarget = target;
    }
    MH_STATUS hs = ::MH_EnableHook(target);
    if (hs != MH_OK) {
        LogF("[IL2CPPMainThreadHook] MH_EnableHook failed: status=%d", static_cast<int>(hs));
        g_installed.store(false);
        return false;
    }
    LogF("[IL2CPPMainThreadHook] Install: hook armed on il2cpp_runtime_invoke @ %p (main_tid=%lu)",
         target, Discrete::MainThreadDispatcher::GetMainThreadId());
    return true;
}

void Uninstall() {
    if (!g_installed.exchange(false)) return;
    const auto& resolver = Discrete::IL2CPP::GetResolver();
    if (resolver.runtime_invoke != nullptr) {
        void* target = reinterpret_cast<void*>(resolver.runtime_invoke);
        // 1. Stop NEW calls reaching the detour (restores original bytes).
        ::MH_DisableHook(target);
        // 2. Drain threads still inside HookedRuntimeInvoke using a real
        //    wall-clock deadline (A-M7 — 2000 x Sleep(1) took up to ~31 s on
        //    the 15.6 ms timer tick). Never call MH_RemoveHook or null
        //    g_orig_runtime_invoke: the DLL is pinned and retaining the
        //    trampoline closes the pre-fetch_add entry race completely.
        const std::uint64_t startMs = ::GetTickCount64();
        const bool drained = Discrete::MainThreadDispatcher::WaitUntilZeroWithDeadline(
            g_inDetour, kUninstallDrainTimeoutMs, /*pollSleepMs=*/1);
        const int remaining = g_inDetour.load(std::memory_order_acquire);
        const std::uint64_t elapsedMs = ::GetTickCount64() - startMs;
        if (!drained) {
            LogF("[IL2CPPMainThreadHook] Uninstall: WARN detour drain timed out after %llums "
                 "(inDetour=%d) — hook disabled, trampoline retained",
                 static_cast<unsigned long long>(elapsedMs), remaining);
        } else {
            LogF("[IL2CPPMainThreadHook] Uninstall: hook disabled (drained in %llums, trampoline retained)",
                 static_cast<unsigned long long>(elapsedMs));
        }
    } else {
        LogF("[IL2CPPMainThreadHook] Uninstall: runtime_invoke unavailable");
    }
}

bool IsInstalled() {
    return g_installed.load();
}

}  // namespace Discrete::IL2CPP::MainThreadHook
