// pin_probe_dll.cpp — test-only DLL used by discrete_dll_test (A-H1) to verify
// at the real Windows OS loader level (x64 + x86 WOW64) that:
//   1. PinModuleContaining() pins the DLL during DLL_PROCESS_ATTACH.
//   2. External FreeLibrary() calls do not deadlock under loader lock, do not
//      fire DLL_PROCESS_DETACH, and do not unmap code under a live worker thread.
//   3. Explicit off-DllMain shutdown cleanly signals and joins the worker thread.
//
// Built only in build/cpp-tests-release-{x64,x86}/bin — never shipped in dist\.

#include "ModuleLifecycle.h"

#include <windows.h>

#include <atomic>
#include <thread>

namespace {

std::atomic<bool> g_pinned{false};
std::atomic<bool> g_workerRunning{false};
std::atomic<int>  g_detachCount{0};
HANDLE            g_stopEvent = nullptr;
std::thread       g_worker;

void WorkerLoop() {
    g_workerRunning.store(true, std::memory_order_release);
    if (g_stopEvent != nullptr) {
        ::WaitForSingleObject(g_stopEvent, INFINITE);
    }
    g_workerRunning.store(false, std::memory_order_release);
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    (void)lpReserved;
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            ::DisableThreadLibraryCalls(hModule);
            g_pinned.store(
                Discrete::ModuleLifecycle::PinModuleContaining(
                    reinterpret_cast<const void*>(&DllMain)),
                std::memory_order_release);
            g_stopEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (g_stopEvent != nullptr) {
                g_worker = std::thread(&WorkerLoop);
            }
            break;
        case DLL_PROCESS_DETACH:
            // Intentional no-op under loader lock (mirrors main.cpp A-H1).
            g_detachCount.fetch_add(1, std::memory_order_relaxed);
            break;
        default:
            break;
    }
    return TRUE;
}

extern "C" {

__declspec(dllexport) int Probe_IsPinned() {
    return g_pinned.load(std::memory_order_acquire) ? 1 : 0;
}

__declspec(dllexport) int Probe_IsWorkerRunning() {
    return g_workerRunning.load(std::memory_order_acquire) ? 1 : 0;
}

__declspec(dllexport) int Probe_DetachCount() {
    return g_detachCount.load(std::memory_order_relaxed);
}

__declspec(dllexport) int Probe_Shutdown() {
    if (g_stopEvent != nullptr) {
        ::SetEvent(g_stopEvent);
    }
    if (g_worker.joinable()) {
        g_worker.join();
    }
    if (g_stopEvent != nullptr) {
        ::CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
    }
    return g_workerRunning.load(std::memory_order_acquire) ? 0 : 1;
}

}  // extern "C"
