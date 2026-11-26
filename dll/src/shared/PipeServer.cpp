// PipeServer.cpp — overlapped named-pipe server with line-delimited JSON dispatch.

#include "PipeServer.h"

#include "Logger.h"
#include "MagicStrings.h"
#include "MainThreadDispatcher.h"
#include "PipeLineBuffer.h"
#include "PipeSecurity.h"
#include "ScanCancel.h"

#include <windows.h>

#include <atomic>
#include <mutex>
#include <string>
#include <thread>  // Phase 22f: detached worker thread per client
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

enum class ReadLineResult {
    Ok,
    Closed,
    TooLarge,
};

// Read until '\n' or disconnect, preserving bytes after '\n' in `rxBuf` for
// subsequent pipelined requests (A-M2). Reports TooLarge explicitly so the
// caller can emit a -32600 JSON-RPC error before closing (A-L5).
ReadLineResult ReadLine(HANDLE pipe, Discrete::PipeFraming::LineBuffer& rxBuf, std::string& out) {
    using Discrete::PipeFraming::LineBuffer;

    // Fast path: a pipelined request is already buffered from the previous ReadFile.
    {
        auto st = rxBuf.TryPopLine(out);
        if (st == LineBuffer::ExtractStatus::LineReady) return ReadLineResult::Ok;
        if (st == LineBuffer::ExtractStatus::TooLarge)  return ReadLineResult::TooLarge;
    }

    char buf[1024];
    OVERLAPPED ov{};
    ov.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return ReadLineResult::Closed;

    ReadLineResult result = ReadLineResult::Closed;
    for (;;) {
        ::ResetEvent(ov.hEvent);
        DWORD read = 0;
        BOOL r = ::ReadFile(pipe, buf, sizeof(buf), &read, &ov);
        if (!r && ::GetLastError() != ERROR_IO_PENDING) {
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
            break;
        }
        if (!::GetOverlappedResult(pipe, &ov, &read, FALSE) || read == 0) {
            break;
        }
        rxBuf.Append(buf, read);
        auto st = rxBuf.TryPopLine(out);
        if (st == LineBuffer::ExtractStatus::LineReady) {
            result = ReadLineResult::Ok;
            break;
        }
        if (st == LineBuffer::ExtractStatus::TooLarge) {
            result = ReadLineResult::TooLarge;
            break;
        }
    }
    ::CloseHandle(ov.hEvent);
    return result;
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
    // Per-client disconnect monitor (A-M1). A long scan handler runs
    // SYNCHRONOUSLY on this thread, so while it's running we can't notice the
    // pipe breaking until it returns (could be minutes). A side thread polls
    // THIS client's pipe ONLY while THIS client's worker owns a ScanCancel::Scope
    // (`clientScanDepth > 0`) — never on global scan activity. That guarantees:
    //   1. A second client (e.g. a CE bridge burst) disconnecting during a
    //      non-scan RPC never cancels another client's active scan.
    //   2. PeekNamedPipe never races this worker's own overlapped ReadFile.
    std::atomic<int> clientScanDepth{0};
    Discrete::ScanCancel::ClientSlotGuard clientSlotGuard(clientScanDepth);
    std::atomic<bool> monitorStop{false};
    std::thread monitor([pipe, &monitorStop, &clientScanDepth]() {
        while (!monitorStop.load(std::memory_order_relaxed)) {
            if (clientScanDepth.load(std::memory_order_acquire) > 0) {
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
    Discrete::PipeFraming::LineBuffer rxBuf;
    while (g_running.load()) {
        std::string line;
        ReadLineResult rl = ReadLine(pipe, rxBuf, line);
        if (rl == ReadLineResult::TooLarge) {
            LogPipe("request exceeded 1 MiB line cap — sending -32600 and closing");
            WriteLine(pipe, BuildError(nullptr, -32600, "request too large").dump());
            break;
        }
        if (rl != ReadLineResult::Ok) break;
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
            const json reqId = request.contains("id") ? request["id"] : json(nullptr);
            serialized = BuildError(reqId, -32603,
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
std::atomic<bool> g_acceptorAlive{false};

void WorkerThread() {
    struct AcceptorAliveGuard {
        ~AcceptorAliveGuard() noexcept {
            g_acceptorAlive.store(false, std::memory_order_release);
        }
    } aliveGuard;

    Discrete::PipeSecurity::ScopedPipeSecurityAttributes pipeSec;
    if (!pipeSec.IsValid()) {
        LogPipe("WARN: failed to construct user+SYSTEM pipe security descriptor; using default DACL");
    }

    LogPipe("pipe server started");
    bool firstInstance = true;
    std::uint64_t lastCreateFailLogMs = 0;
    std::size_t suppressedCreateFails = 0;
    constexpr std::uint64_t kCreateFailLogThrottleMs = 10000;

    while (g_running.load()) {
        HANDLE pipe = ::CreateNamedPipeW(
            MagicStrings::PipeName,
            Discrete::PipeSecurity::MakePipeOpenMode(firstInstance),
            Discrete::PipeSecurity::MakePipeMode(),
            kMaxPipeInstances,                                  // nMaxInstances
            kPipeBufferSize, kPipeBufferSize, 0, pipeSec.Get());
        if (pipe == INVALID_HANDLE_VALUE) {
            DWORD err = ::GetLastError();
            // A-M3: FILE_FLAG_FIRST_PIPE_INSTANCE returns ERROR_ACCESS_DENIED
            // when another process already owns \\.\pipe\DiscreteBfx. Refuse to
            // hijack or silently share the pipe name.
            if (firstInstance && err == ERROR_ACCESS_DENIED) {
                LogPipe("CreateNamedPipeW rejected (ERROR_ACCESS_DENIED on "
                        "FILE_FLAG_FIRST_PIPE_INSTANCE) — pipe name already "
                        "owned by another process; stopping pipe server");
                g_running.store(false);
                break;
            }
            // A-L6: throttle repeated slot-full / transient creation failures
            // instead of writing a log line every 500 ms.
            const std::uint64_t nowMs = ::GetTickCount64();
            if (lastCreateFailLogMs == 0 ||
                (nowMs - lastCreateFailLogMs) >= kCreateFailLogThrottleMs) {
                std::string msg = "CreateNamedPipeW failed: " + std::to_string(err);
                if (err == ERROR_PIPE_BUSY) {
                    msg += " (ERROR_PIPE_BUSY — all " + std::to_string(kMaxPipeInstances) +
                           " slots in use)";
                }
                if (suppressedCreateFails > 0) {
                    msg += " (suppressed " + std::to_string(suppressedCreateFails) + " repeats)";
                    suppressedCreateFails = 0;
                }
                LogPipe(msg);
                lastCreateFailLogMs = nowMs;
            } else {
                ++suppressedCreateFails;
            }
            ::Sleep(500);
            continue;
        }
        firstInstance = false;
        lastCreateFailLogMs = 0;
        suppressedCreateFails = 0;

        OVERLAPPED ov{};
        ov.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (ov.hEvent == nullptr) {
            // A-L6: never pass a NULL event to WaitForMultipleObjects (would
            // return WAIT_FAILED immediately and busy-spin).
            LogPipe("CreateEventW failed in WorkerThread: " + std::to_string(::GetLastError()));
            ::CloseHandle(pipe);
            ::Sleep(100);
            continue;
        }

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
            try {
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
            } catch (const std::exception& ex) {
                g_activeClients.fetch_sub(1, std::memory_order_relaxed);
                LogPipe(std::string("failed to spawn client worker thread: ") + ex.what());
                ::DisconnectNamedPipe(pipe);
                ::CloseHandle(pipe);
            } catch (...) {
                g_activeClients.fetch_sub(1, std::memory_order_relaxed);
                LogPipe("failed to spawn client worker thread: non-std exception");
                ::DisconnectNamedPipe(pipe);
                ::CloseHandle(pipe);
            }
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
    // A-L6: refuse restart if a previous Stop() timed out and detached a still-
    // running acceptor thread (otherwise flipping g_running=true would run two
    // acceptors concurrently).
    if (g_acceptorAlive.load(std::memory_order_acquire)) {
        LogPipe("PipeServer::Start: previous acceptor thread still alive — refusing restart");
        return false;
    }

    bool expected = false;
    if (!g_running.compare_exchange_strong(expected, true)) return false;

    g_stopEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stopEvent) {
        g_running = false;
        return false;
    }

    g_acceptorAlive.store(true, std::memory_order_release);
    try {
        g_worker = std::thread(WorkerThread);
    } catch (...) {
        // A-L6: roll back state cleanly on thread-spawn failure so Start()
        // never leaks g_stopEvent, leaves g_running=true, or throws into DllMain.
        g_acceptorAlive.store(false, std::memory_order_release);
        ::CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
        g_running = false;
        LogPipe("PipeServer::Start: failed to spawn acceptor thread");
        return false;
    }
    return true;
}

void Stop() {
    if (!g_running.exchange(false)) return;
    if (g_stopEvent) ::SetEvent(g_stopEvent);
    // Arm cooperative scan cancel AND wake any worker blocked in
    // MainThreadDispatcher::SubmitAndWait (A-M8) before draining clients.
    Discrete::ScanCancel::RequestCancel();
    Discrete::MainThreadDispatcher::AbortPending();

    if (g_worker.joinable()) {
        // BOUNDED wait, never a bare join(). Normally Stop() runs off-DllMain
        // via Discrete_Shutdown() (the module is pinned in main.cpp and
        // DLL_PROCESS_DETACH is a no-op), so g_stopEvent wakes the acceptor
        // within milliseconds and WaitForSingleObject returns WAIT_OBJECT_0.
        // Keeping the bounded wait defends against any caller that ever invokes
        // Stop() while a thread is stalled in an OS call.
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

    // Re-abort any item queued by a worker in the brief window while the
    // acceptor was exiting, then drain in-flight ServeClient workers with a
    // real wall-clock deadline (2 s).
    Discrete::MainThreadDispatcher::AbortPending();
    Discrete::MainThreadDispatcher::WaitUntilZeroWithDeadline(
        g_activeClients, kWorkerExitTimeoutMs, /*pollSleepMs=*/5);

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
    // Note (A-L3): do NOT ClearDisconnectHooks() here — Discrete_StopPipe ->
    // Discrete_StartPipe must preserve hooks registered by Discrete_Init().
    // Discrete_Shutdown() owns ClearDisconnectHooks().
}

bool IsRunning() noexcept {
    return g_running.load();
}

}  // namespace Discrete::PipeServer
