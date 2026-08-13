// PipeServer.cpp — overlapped named-pipe server with line-delimited JSON dispatch.

#include "PipeServer.h"

#include "Logger.h"
#include "MagicStrings.h"
#include "ScanCancel.h"

#include <windows.h>

#include <atomic>
#include <mutex>
#include <string>
#include <thread>  // Phase 22f: detached worker thread per client
#include <thread>
#include <vector>

using nlohmann::json;

namespace Discrete::PipeServer {

namespace {

std::mutex g_mutex;
std::unordered_map<std::string, Handler> g_handlers;
// Per-client disconnect hooks. Fires once per accepted-client lifetime.
// Read-mostly: written at init (backends register one hook each), read on
// every disconnect. Snapshotted under the same g_mutex as the handler map.
std::vector<DisconnectHook> g_disconnectHooks;
std::thread g_worker;
std::atomic<bool> g_running{false};
HANDLE g_stopEvent = nullptr;
// In-flight per-client ServeClient threads. The acceptor increments this
// synchronously BEFORE detaching each worker, and the worker decrements it
// at exit. Stop() waits for it to reach zero so a subsequent FreeLibrary
// (the "Unload Discrete DLL" path) can't unmap the module while a client
// thread is still running its code — that race was the unload-time CTD.
std::atomic<int> g_activeClients{0};

constexpr DWORD kPipeBufferSize = 64 * 1024;

// Upper bound on how long Stop() waits for the acceptor thread. It normally
// exits in milliseconds once g_stopEvent is signalled; this only exists so the
// DllMain/loader-lock path can never deadlock. Matches the ~2 s cap already
// used below for in-flight client workers.
constexpr DWORD kWorkerExitTimeoutMs = 2000;

void LogPipe(const std::string& msg) {
    Log::Write(MagicStrings::LogCategoryPipe, msg);
}

// Write `data` then a newline. Returns false on failure / disconnect.
bool WriteLine(HANDLE pipe, const std::string& data) {
    std::string framed = data;
    framed.push_back('\n');
    DWORD written = 0;
    OVERLAPPED ov{};
    ov.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return false;

    bool ok = false;
    BOOL result = ::WriteFile(pipe, framed.data(), static_cast<DWORD>(framed.size()), &written, &ov);
    if (!result && ::GetLastError() != ERROR_IO_PENDING) {
        ::CloseHandle(ov.hEvent);
        return false;
    }
    HANDLE waits[2] = { ov.hEvent, g_stopEvent };
    DWORD which = ::WaitForMultipleObjects(2, waits, FALSE, INFINITE);
    if (which == WAIT_OBJECT_0) {
        ok = ::GetOverlappedResult(pipe, &ov, &written, FALSE) && written == framed.size();
    } else {
        ::CancelIoEx(pipe, &ov);
        // Wait (bWait=TRUE) for the cancellation to actually complete before
        // ov leaves scope — CancelIoEx is async, and the kernel may still
        // write the completion status into the stack-resident OVERLAPPED
        // after CancelIoEx returns. Ignore the result; we're tearing down.
        ::GetOverlappedResult(pipe, &ov, &written, TRUE);
    }
    ::CloseHandle(ov.hEvent);
    return ok;
}

// Read until '\n' or disconnect. Returns false if the connection closed before a line arrived
// or the server is being stopped.
bool ReadLine(HANDLE pipe, std::string& out) {
    out.clear();
    char buf[1024];
    OVERLAPPED ov{};
    ov.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return false;

    bool ok = true;
    while (ok) {
        ::ResetEvent(ov.hEvent);
        DWORD read = 0;
        BOOL r = ::ReadFile(pipe, buf, sizeof(buf), &read, &ov);
        if (!r && ::GetLastError() != ERROR_IO_PENDING) {
            ok = false;
            break;
        }
        HANDLE waits[2] = { ov.hEvent, g_stopEvent };
        DWORD which = ::WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (which != WAIT_OBJECT_0) {
            ::CancelIoEx(pipe, &ov);
            // Drain the cancellation (bWait=TRUE) before ov + buf leave scope:
            // CancelIoEx is async, so the kernel may still write into the
            // stack-resident OVERLAPPED / read buffer after it returns.
            ::GetOverlappedResult(pipe, &ov, &read, TRUE);
            ok = false;
            break;
        }
        if (!::GetOverlappedResult(pipe, &ov, &read, FALSE) || read == 0) {
            ok = false;
            break;
        }
        for (DWORD i = 0; i < read; ++i) {
            if (buf[i] == '\n') {
                ::CloseHandle(ov.hEvent);
                return true;
            }
            if (buf[i] != '\r') out.push_back(buf[i]);
        }
        if (out.size() > 1024 * 1024) {  // 1 MiB sanity cap
            ok = false;
            break;
        }
    }
    ::CloseHandle(ov.hEvent);
    return false;
}

json BuildError(json id, int code, const std::string& message) {
    return json{
        {"id", std::move(id)},
        {"error", {{"code", code}, {"message", message}}},
    };
}

json Dispatch(const json& request) {
    json id = request.contains("id") ? request["id"] : json(nullptr);

    if (!request.contains("cmd") || !request["cmd"].is_string()) {
        return BuildError(id, -32600, "missing cmd");
    }
    std::string cmd = request["cmd"].get<std::string>();

    Handler handler;
    {
        std::lock_guard lock(g_mutex);
        auto it = g_handlers.find(cmd);
        if (it != g_handlers.end()) handler = it->second;
    }
    if (!handler) return BuildError(id, -32601, "unknown cmd: " + cmd);

    json args = request.contains("args") ? request["args"] : json::object();
    try {
        json result = handler(args);
        return json{{"id", std::move(id)}, {"result", std::move(result)}};
    } catch (const std::exception& ex) {
        return BuildError(id, -32000, ex.what());
    }
}

void ServeClient(HANDLE pipe) {
    LogPipe("client connected");
    // Disconnect monitor. A long scan handler runs SYNCHRONOUSLY on this thread,
    // so while it's running we can't notice the pipe breaking until it returns
    // (could be minutes). A side thread polls the pipe; if the client vanished
    // (UI force-quit) it arms the cooperative scan-cancel so the DLL stops
    // promptly + frees its work instead of churning to completion. It only
    // probes WHILE a scan is active — then the main thread does no pipe IO, so
    // PeekNamedPipe can't race the overlapped ReadFile on the same handle.
    std::atomic<bool> monitorStop{false};
    std::thread monitor([pipe, &monitorStop]() {
        while (!monitorStop.load(std::memory_order_relaxed)) {
            if (Discrete::ScanCancel::g_scanActive.load(std::memory_order_relaxed)) {
                DWORD avail = 0;
                if (!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &avail, nullptr)) {
                    DWORD e = ::GetLastError();
                    if (e == ERROR_BROKEN_PIPE || e == ERROR_PIPE_NOT_CONNECTED ||
                        e == ERROR_INVALID_HANDLE) {
                        Discrete::ScanCancel::RequestCancel();
                        break;
                    }
                }
            }
            // Snapshot the handle: Stop() may clear g_stopEvent while this
            // monitor is still alive (its client is mid-scan and outlived the
            // drain). A NULL handle makes WaitForSingleObject return WAIT_FAILED
            // *immediately*, which is neither WAIT_OBJECT_0 nor a delay — the
            // loop would then spin at 100% CPU for the rest of the scan. Treat
            // anything that is not a clean signal as "sleep, then re-check".
            HANDLE stopEvt = g_stopEvent;
            const DWORD w = stopEvt ? ::WaitForSingleObject(stopEvt, 150)
                                    : WAIT_FAILED;
            if (w == WAIT_OBJECT_0) break;
            if (w != WAIT_TIMEOUT) ::Sleep(150);   // never busy-wait
        }
    });
    while (g_running.load()) {
        std::string line;
        if (!ReadLine(pipe, line)) break;
        if (line.empty()) continue;

        json request;
        try {
            request = json::parse(line);
        } catch (const std::exception& ex) {
            LogPipe(std::string("parse error: ") + ex.what());
            WriteLine(pipe, BuildError(nullptr, -32700, "invalid json").dump());
            continue;
        }

        json response = Dispatch(request);
        // Use error_handler_t::replace so invalid UTF-8 in any field becomes
        // U+FFFD instead of throwing. Also wrap in try/catch as a final
        // safety net — uncaught exceptions in the pipe worker would otherwise
        // tear down the host process.
        std::string serialized;
        try {
            serialized = response.dump(-1, ' ', false, json::error_handler_t::replace);
        } catch (const std::exception& ex) {
            LogPipe(std::string("response dump failed: ") + ex.what());
            serialized = BuildError(nullptr, -32603,
                                    std::string("response serialization failed: ") + ex.what()).dump();
        }
        if (!WriteLine(pipe, serialized)) break;
    }
    monitorStop.store(true, std::memory_order_relaxed);
    if (monitor.joinable()) monitor.join();
    LogPipe("client disconnected");
}

// Phase 22f live-smoke follow-up (2026-05-12, build #284): bumped
// from 1 to 4 concurrent clients. Phase 11 / 18d ran in
// "either-bridge-or-DiscreteUI" mode where single-instance was safe
// (and useful — prevented game-relaunch ghost-pipe confusion). Phase
// 22's design is "DiscreteUI persistent + CE-Lua-bridge bursts",
// which contend for the single slot — bridge io.open() returns
// ERROR_PIPE_BUSY ("Invalid argument" via Lua's CRT translation) the
// moment DiscreteUI is connected. The fix is twofold:
//   1. nMaxInstances=kMaxPipeInstances so the kernel allows multiple
//      live instances of the alive game's DLL.
//   2. Each accepted client runs on a DETACHED worker thread so the
//      main loop returns to CreateNamedPipeW immediately and the
//      NEXT client's connect doesn't wait for the first client's
//      session to end (could be hours for a held DiscreteUI).
// Handlers are read-mostly (Mono / IL2CPP runtime walks, address
// caches); concurrent invocations are tolerated — worst-case race is
// a duplicate cache fill, not corruption. If a real data race shows
// up under load we add a per-handler mutex; defensive design isn't
// worth the complexity until a concrete scenario demands it.
// Ghost-pipe trade-off (original concern): only relevant during
// game-relaunch, and DiscreteUI's ConnectWithRetryAsync already
// tolerates the transient "Pipe is broken" hits that path produces.
constexpr DWORD kMaxPipeInstances = 4;

void WorkerThread() {
    LogPipe("pipe server started");
    while (g_running.load()) {
        HANDLE pipe = ::CreateNamedPipeW(
            MagicStrings::PipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            kMaxPipeInstances,                                  // nMaxInstances
            kPipeBufferSize, kPipeBufferSize, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            DWORD err = ::GetLastError();
            // ERROR_PIPE_BUSY (231) is the "all instance slots taken"
            // case — should be rare with kMaxPipeInstances=4 but
            // still possible if a ghost holds a slot during game
            // relaunch. ERROR_ACCESS_DENIED (5) shows up under the
            // same conditions on some Windows builds.
            LogPipe("CreateNamedPipeW failed: " + std::to_string(err)
                    + (err == 231 ? " (ERROR_PIPE_BUSY — all "
                                    + std::to_string(kMaxPipeInstances)
                                    + " slots in use)" : ""));
            ::Sleep(500);
            continue;
        }

        OVERLAPPED ov{};
        ov.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);

        BOOL connected = ::ConnectNamedPipe(pipe, &ov);
        DWORD err = ::GetLastError();
        bool ready = false;
        if (!connected && err == ERROR_PIPE_CONNECTED) {
            ready = true;
        } else if (!connected && err == ERROR_IO_PENDING) {
            HANDLE waits[2] = { ov.hEvent, g_stopEvent };
            DWORD which = ::WaitForMultipleObjects(2, waits, FALSE, INFINITE);
            if (which == WAIT_OBJECT_0) {
                DWORD bytes = 0;
                ready = ::GetOverlappedResult(pipe, &ov, &bytes, FALSE) != FALSE;
            } else {
                ::CancelIoEx(pipe, &ov);
                // Drain the cancellation (bWait=TRUE) before ov leaves scope —
                // CancelIoEx is async; the kernel may still write the
                // completion status into the stack-resident OVERLAPPED.
                DWORD bytes = 0;
                ::GetOverlappedResult(pipe, &ov, &bytes, TRUE);
            }
        }

        ::CloseHandle(ov.hEvent);

        if (!g_running.load()) {
            ::CloseHandle(pipe);
            break;
        }

        if (ready) {
            // Detach a worker thread per accepted client. ServeClient
            // is the per-client request-response loop (lifetime =
            // until client disconnects). Detaching means the main
            // WorkerThread loop immediately returns to
            // CreateNamedPipeW for the next client — concurrent
            // DiscreteUI + bridge slot in cleanly.
            //
            // Lifetime / cleanup: on g_running=false the per-client
            // ReadLine bails (it polls g_running each iteration), the
            // worker exits, captures clean up. We do NOT join detached
            // threads on shutdown — DLL unload only happens at game
            // exit and any in-flight ServeClient pipe handle gets
            // forcibly closed when the process tears down.
            //
            // Disconnect hooks fire AFTER ::DisconnectNamedPipe so the
            // OS-side pipe object is already torn down — backends are
            // free to take their time inside the hook (e.g. a Mono GC
            // drain on a 60K-class image is ~100 ms) without holding
            // the pipe slot open against the next ::CreateNamedPipeW
            // in the main loop. Hooks run on the per-client worker
            // thread which is about to exit anyway, so no extra
            // thread cost.
            // Count this worker BEFORE detaching (synchronously, on the
            // acceptor thread) so that once Stop() has joined the acceptor,
            // g_activeClients reflects every live worker — no spawn can slip
            // in after Stop() starts waiting.
            g_activeClients.fetch_add(1, std::memory_order_relaxed);
            std::thread([pipe]() {
              // Exception barrier for the WHOLE thread body. This is a detached
              // std::thread, so anything escaping its function calls
              // std::terminate and takes the GAME process down with it. Dispatch
              // guards handler exceptions, but plenty of allocation happens
              // outside that guard — ReadLine growing its buffer, WriteLine's
              // string copy, the json id/cmd extraction in Dispatch itself, and
              // even BuildError inside Dispatch's own catch (which re-throws if
              // the original exception was bad_alloc). A DLL sharing a 32-bit
              // game's 2 GB address space can genuinely hit bad_alloc on a large
              // scan response. Losing one client beats killing the game.
              try {
                ServeClient(pipe);
              } catch (const std::exception& ex) {
                LogPipe(std::string("client worker aborted: ") + ex.what());
              } catch (...) {
                LogPipe("client worker aborted: non-std exception");
              }
                ::DisconnectNamedPipe(pipe);
                ::CloseHandle(pipe);

                // Run disconnect hooks ONLY for a genuine mid-session client
                // disconnect — NOT during shutdown / unload. The hooks do
                // real runtime work (the IL2CPP one calls gc_collect on this
                // worker thread); running that while the DLL is being torn
                // down + unloaded, with the game main thread still mutating
                // the heap, crashes the game. When g_running is already false
                // the server is stopping, so skip them entirely (the whole
                // DLL is about to go away — any cleanup they'd do is moot).
                if (g_running.load()) {
                    // Snapshot the hook list under the mutex so a concurrent
                    // RegisterDisconnectHook (unlikely after init but cheap to
                    // be safe) doesn't race with iteration.
                    std::vector<DisconnectHook> hooks;
                    {
                        std::lock_guard lock(g_mutex);
                        hooks = g_disconnectHooks;
                    }
                    for (const auto& h : hooks) {
                        if (!h) continue;
                        try {
                            h();
                        } catch (const std::exception& ex) {
                            LogPipe(std::string("disconnect hook threw: ") + ex.what());
                        } catch (...) {
                            LogPipe("disconnect hook threw non-std exception");
                        }
                    }
                }
                // Last thing the worker does: it's now leaving DLL-owned work.
                g_activeClients.fetch_sub(1, std::memory_order_relaxed);
            }).detach();
        } else {
            ::DisconnectNamedPipe(pipe);
            ::CloseHandle(pipe);
        }
    }
    LogPipe("pipe server stopped");
}

}  // namespace

void RegisterHandler(std::string name, Handler handler) {
    std::lock_guard lock(g_mutex);
    g_handlers[std::move(name)] = std::move(handler);
}

void RegisterDisconnectHook(DisconnectHook hook) {
    if (!hook) return;
    std::lock_guard lock(g_mutex);
    g_disconnectHooks.push_back(std::move(hook));
}

void ClearDisconnectHooks() {
    std::lock_guard lock(g_mutex);
    g_disconnectHooks.clear();
}

int ActiveClientCount() {
    return g_activeClients.load(std::memory_order_relaxed);
}

bool Start() {
    bool expected = false;
    if (!g_running.compare_exchange_strong(expected, true)) return false;

    g_stopEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stopEvent) {
        g_running = false;
        return false;
    }
    g_worker = std::thread(WorkerThread);
    return true;
}

void Stop() {
    if (!g_running.exchange(false)) return;
    if (g_stopEvent) ::SetEvent(g_stopEvent);
    // Arm the cooperative scan cancel. A client worker sitting in a multi-minute
    // value scan polls this and bails within milliseconds; without it the drain
    // below simply burns its full 2 s and gives up while the scan grinds on,
    // which is the case that then trips the g_stopEvent lifetime problem handled
    // at the end of this function.
    Discrete::ScanCancel::RequestCancel();

    if (g_worker.joinable()) {
        // BOUNDED wait, never a bare join(). Stop() is reachable from DllMain:
        // main.cpp OnDetach() -> Discrete_Shutdown() -> here, on the
        // lpReserved == NULL (FreeLibrary) path — and DllMain runs holding the
        // loader lock. The worker's own exit epilogue (_endthreadex ->
        // ExitThread -> ntdll!LdrShutdownThread) must ACQUIRE that same lock to
        // run DLL_THREAD_DETACH across every loaded module, so it can never
        // finish while we hold it. An unconditional join() therefore wedges the
        // calling thread forever *while still holding the loader lock*, which
        // then blocks every LoadLibrary and every thread exit in the game —
        // the whole process freezes, not just us. DisableThreadLibraryCalls on
        // our own module (main.cpp) does not help: the walk still covers
        // ucrtbase / UnityPlayer / mono-2.0-bdwgc / GameAssembly.
        //
        // g_stopEvent is already signalled above, so a worker that is free to
        // run exits within milliseconds and the wait returns at once. Only the
        // pathological case falls through to detach.
        const DWORD w = ::WaitForSingleObject(g_worker.native_handle(),
                                              kWorkerExitTimeoutMs);
        if (w == WAIT_OBJECT_0) {
            g_worker.join();   // already terminated — returns immediately
        } else {
            g_worker.detach();
            LogPipe("PipeServer::Stop: worker did not exit within "
                    "the timeout — detached rather than deadlocking");
        }
    }

    // The acceptor has stopped — no new client threads will be created, so
    // g_activeClients can now only decrease. Wait for in-flight ServeClient
    // workers to finish running DLL code before returning. Without this, the
    // "Unload Discrete DLL" path's FreeLibrary could unmap the module while a
    // detached worker is still executing it → CTD. The g_stopEvent we just
    // signalled breaks any worker blocked in ReadLine, so they exit promptly;
    // the cap is a safety bound, not the expected wait.
    for (int i = 0; i < 400 && g_activeClients.load(std::memory_order_relaxed) > 0; ++i) {
        ::Sleep(5); // up to ~2 s worst case
    }

    // Only reclaim the event once the drain actually succeeded. Client workers
    // and their disconnect monitors read g_stopEvent without synchronisation, so
    // closing it while any of them is still alive is a use-after-close: the
    // monitor either waits on a NULL handle (WAIT_FAILED, handled above) or — far
    // worse — on a recycled handle value that now names an unrelated kernel
    // object. If the drain timed out, deliberately leak this one event handle;
    // one HANDLE for the remaining process lifetime is cheaper than that race,
    // and Start() allocates a fresh one if the server is ever restarted.
    if (g_activeClients.load(std::memory_order_relaxed) == 0) {
        if (g_stopEvent) {
            ::CloseHandle(g_stopEvent);
            g_stopEvent = nullptr;
        }
    } else {
        LogPipe("PipeServer::Stop: clients still active after drain — "
                "leaking the stop event rather than closing it under them");
    }

    // Registered by Discrete_Init, which can run again after Discrete_Shutdown
    // clears the init latch. The hook list is append-only, so without this a
    // Shutdown -> Init cycle leaves two copies of each backend hook and every
    // later client disconnect runs that many full GC drains (~100 ms each).
    ClearDisconnectHooks();
}

bool IsRunning() noexcept {
    return g_running.load();
}

}  // namespace Discrete::PipeServer
