// PipeServer.h — newline-delimited JSON named-pipe server.
//
// Wire format (each direction): one JSON object per line, terminated by '\n'.
// Request shape  : {"id": <int>, "cmd": <string>, "args": <object?>}
// Response shape : {"id": <int>, "result": <any>}
//                  or {"id": <int>, "error": {"code": <int>, "message": <string>}}
//
// Phase 1: single in-flight client, overlapped IO with stop event for clean shutdown.
// Multi-client / per-connection thread is deferred to Phase 6 hardening.
#pragma once

#include <nlohmann/json.hpp>

#include <functional>
#include <string>
#include <string_view>
#include <unordered_map>

namespace UnityCEDumper::PipeServer {

using Handler = std::function<nlohmann::json(const nlohmann::json& args)>;

// Register a command handler. Overwrites any previous handler for the same name.
void RegisterHandler(std::string name, Handler handler);

// Start the server thread (idempotent). Returns false if already running or failed to start.
bool Start();

// Stop the server thread and disconnect any client. Idempotent and blocks until stopped.
void Stop();

// True if the server thread is currently running.
bool IsRunning() noexcept;

}  // namespace UnityCEDumper::PipeServer
