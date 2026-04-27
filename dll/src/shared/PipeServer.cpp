// PipeServer.cpp — overlapped named-pipe server with line-delimited JSON dispatch.

#include "PipeServer.h"

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>

#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using nlohmann::json;

namespace UnityCEDumper::PipeServer {

namespace {

std::mutex g_mutex;
std::unordered_map<std::string, Handler> g_handlers;
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
        if (!WriteLine(pipe, response.dump())) break;
    }
    LogPipe("client disconnected");
}

void WorkerThread() {
    LogPipe("pipe server started");
    while (g_running.load()) {
        HANDLE pipe = ::CreateNamedPipeW(
            MagicStrings::PipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            kPipeBufferSize, kPipeBufferSize, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            LogPipe("CreateNamedPipeW failed: " + std::to_string(::GetLastError()));
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
            ServeClient(pipe);
        }

        ::DisconnectNamedPipe(pipe);
        ::CloseHandle(pipe);
    }
    LogPipe("pipe server stopped");
}

}  // namespace

void RegisterHandler(std::string name, Handler handler) {
    std::lock_guard lock(g_mutex);
    g_handlers[std::move(name)] = std::move(handler);
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
