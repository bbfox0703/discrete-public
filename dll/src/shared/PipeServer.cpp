// PipeServer.cpp — overlapped named-pipe server with line-delimited JSON dispatch.

#include "PipeServer.h"

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>

#include <atomic>
#include <mutex>
#include <string>
#include <thread>  // Phase 22f: detached worker thread per client
#include <thread>
#include <vector>

using nlohmann::json;

namespace UnityCEDumper::PipeServer {

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

constexpr DWORD kPipeBufferSize = 64 * 1024;

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
            std::thread([pipe]() {
                ServeClient(pipe);
                ::DisconnectNamedPipe(pipe);
                ::CloseHandle(pipe);

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
    if (g_worker.joinable()) g_worker.join();
    if (g_stopEvent) {
        ::CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
    }
}

bool IsRunning() noexcept {
    return g_running.load();
}

}  // namespace UnityCEDumper::PipeServer
