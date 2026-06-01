// MonoMainThreadHook.cpp — Phase 20-MT (Mono backend).
//
// Installs a MinHook trampoline on `mono_runtime_invoke`. Mechanism:
//   1. MinHook's `MH_CreateHook(target, detour, &orig)` redirects target →
//      detour; trampoline `orig` carries the original instruction prologue
//      so we can still call through to Unity's implementation.
//   2. Our detour, when called from the Unity main thread, notifies the
//      MainThreadDispatcher (which records the tick + drains the queue),
//      then forwards to the trampoline.
//   3. Recursion is impossible to avoid here — drained work items will
//      themselves invoke `mono_runtime_invoke` to actually do the user's
//      Unity icall. The dispatcher's thread_local guard short-circuits
//      the inner Drain so we don't re-drain inside ourselves.
//
// The thread-id check is intentional: worker threads can ALSO go through
// `mono_runtime_invoke` if a `callMethod` arrives in the un-marshaled
// path (i.e. `main_thread=false`). Calling NotifyMainThreadTick from a
// worker thread would falsely advance the freshness window AND let Drain
// run on the wrong thread. The dispatcher's `IsMainThread` guard inside
// `Drain` would catch that anyway, but cheaper to skip here.

#include "MonoMainThreadHook.h"

#include "MonoResolver.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/MainThreadDispatcher.h"

#include <MinHook.h>
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <string>

namespace Discrete::Mono::MainThreadHook {

namespace {

template <typename... Args>
void LogF(const char* fmt, Args... args) {
    char buf[512];
    std::snprintf(buf, sizeof(buf), fmt, args...);
    Discrete::Log::Write(Discrete::MagicStrings::LogCategoryInit, buf);
}

std::atomic<bool>  g_installed{false};

// MinHook installs `mh_initialize` once per process; track our own init
// so multiple Install() calls don't double-init.
std::atomic<bool>  g_minhook_initialized{false};

// Original trampoline. Filled by MH_CreateHook. NULL until Install succeeds.
// Calling convention MUST match `fn_runtime_invoke` from MonoResolver.h.
// Mono is C, and Mono's exports use __cdecl on x86 (Microsoft's default
// for unmarked C functions). On x64 Windows there's only one calling
// convention so the keyword is irrelevant — but on x86, a stdcall
// (WINAPI) detour against a cdecl target causes a stack double-pop and
// the game crashes the next time Unity touches the runtime. Drop the
// WINAPI keyword on the hook function below so it stays cdecl across
// both bitnesses. (Verified live 2026-05-20: LEAVES / Unity 5.4 x86
// crashed after exactly one hook fire under the stdcall variant.)
using fn_runtime_invoke_t = Discrete::Mono::MonoObject* (*)(
    Discrete::Mono::MonoMethod*,
    void*,
    void**,
    Discrete::Mono::MonoObject**);
fn_runtime_invoke_t g_orig_runtime_invoke = nullptr;

// Threads currently executing inside HookedRuntimeInvoke. Uninstall() drains
// this to zero (after MH_DisableHook stops new entries) BEFORE MH_RemoveHook
// frees the trampoline and before the DLL can be unloaded — otherwise a
// thread inside the detour at unload time would run/return into unmapped code.
std::atomic<int> g_inDetour{0};

// Our detour. C-style function (no captures) so MinHook can route control
// flow into it via a near-jump. NO `WINAPI` — see comment above.
Discrete::Mono::MonoObject* HookedRuntimeInvoke(
    Discrete::Mono::MonoMethod* method,
    void* obj,
    void** params,
    Discrete::Mono::MonoObject** exc)
{
    using namespace Discrete::MainThreadDispatcher;
    // Mark presence FIRST so Uninstall's drain can't miss us. After
    // MH_DisableHook no new caller reaches here, so the count only falls.
    g_inDetour.fetch_add(1, std::memory_order_acquire);
    // Cheap thread-id check first — main thread id is a fixed std::atomic<uint32_t>.
    const DWORD tid = ::GetCurrentThreadId();
    if (tid == GetMainThreadId()) {
        NotifyMainThreadTick();  // also calls Drain() under the recursion guard
    }
    Discrete::Mono::MonoObject* ret = nullptr;
    if (g_orig_runtime_invoke != nullptr) {
        ret = g_orig_runtime_invoke(method, obj, params, exc);
    } else if (exc) {
        // Defensive: if somehow the trampoline got cleared (Uninstall race),
        // we have nothing to delegate to. Returning NULL would corrupt
        // managed callers — better to bail with no exception sentinel.
        *exc = nullptr;
    }
    g_inDetour.fetch_sub(1, std::memory_order_release);
    return ret;
}

}  // namespace

bool Install() {
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return true;  // already installed
    }

    if (!Discrete::MainThreadDispatcher::IsInitialized()) {
        LogF("[MonoMainThreadHook] Install: MainThreadDispatcher not initialized");
        g_installed.store(false);
        return false;
    }

    const auto& resolver = Discrete::Mono::GetResolver();
    if (resolver.runtime_invoke == nullptr) {
        LogF("[MonoMainThreadHook] Install: mono_runtime_invoke not bound");
        g_installed.store(false);
        return false;
    }

    // One-time MinHook init. MH_OK is the success status; MH_ERROR_ALREADY_INITIALIZED
    // is fine — it means some other code in the DLL already called it.
    bool needsInit = false;
    if (!g_minhook_initialized.exchange(true)) {
        needsInit = true;
    }
    if (needsInit) {
        MH_STATUS s = ::MH_Initialize();
        if (s != MH_OK && s != MH_ERROR_ALREADY_INITIALIZED) {
            LogF("[MonoMainThreadHook] MH_Initialize failed: status=%d", static_cast<int>(s));
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
        LogF("[MonoMainThreadHook] MH_CreateHook(mono_runtime_invoke) failed: status=%d", static_cast<int>(hs));
        g_installed.store(false);
        return false;
    }
    hs = ::MH_EnableHook(target);
    if (hs != MH_OK) {
        LogF("[MonoMainThreadHook] MH_EnableHook failed: status=%d", static_cast<int>(hs));
        ::MH_RemoveHook(target);
        g_orig_runtime_invoke = nullptr;
        g_installed.store(false);
        return false;
    }
    LogF("[MonoMainThreadHook] Install: hook armed on mono_runtime_invoke @ %p (main_tid=%lu)",
         target, Discrete::MainThreadDispatcher::GetMainThreadId());
    return true;
}

void Uninstall() {
    if (!g_installed.load()) return;
    const auto& resolver = Discrete::Mono::GetResolver();
    if (resolver.runtime_invoke != nullptr) {
        void* target = reinterpret_cast<void*>(resolver.runtime_invoke);
        // 1. Stop NEW calls reaching the detour (restores original bytes).
        ::MH_DisableHook(target);
        // 2. Drain threads still INSIDE HookedRuntimeInvoke before we free the
        //    trampoline / let the DLL be unmapped. New callers bypass the
        //    detour now, so g_inDetour only decreases. Cap is a safety bound.
        for (int i = 0; i < 2000 && g_inDetour.load(std::memory_order_acquire) > 0; ++i) {
            ::Sleep(1);
        }
        // 3. Now safe to free the trampoline.
        ::MH_RemoveHook(target);
    }
    g_orig_runtime_invoke = nullptr;
    g_installed.store(false);
    LogF("[MonoMainThreadHook] Uninstall: hook removed");
}

bool IsInstalled() {
    return g_installed.load();
}

}  // namespace Discrete::Mono::MainThreadHook
