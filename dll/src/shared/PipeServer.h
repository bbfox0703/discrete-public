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

namespace Discrete::PipeServer {

using Handler = std::function<nlohmann::json(const nlohmann::json& args)>;

// Register a command handler. Overwrites any previous handler for the same name.
void RegisterHandler(std::string name, Handler handler);

/// Per-client cleanup hook. Fired after EVERY accepted client disconnects
/// (clean exit OR pipe error), on the per-client worker thread, after
/// `DisconnectNamedPipe` + `CloseHandle` but before the worker exits.
///
/// Used by backends to drain Mono / IL2CPP GC finalizer queues that piled
/// up during a long-running batch RPC. The classic symptom: a Phase 28
/// `findFieldReferences` walks 2000+ method headers, each leaving a tiny
/// metadata-cache entry; even though every entry is individually freed,
/// BDWGC processes the resulting finalizer queue at process shutdown
/// instead of incrementally, hanging the game's exit by tens of seconds.
/// Forcing a `mono_gc_collect(-1)` at the disconnect boundary drains the
/// queue while DiscreteUI / the bridge is still attached, so the user-
/// perceptible game-shutdown cost drops back to ~1s.
///
/// Hooks may be called concurrently from multiple per-client worker
/// threads (up to `kMaxPipeInstances`). Implementations must be
/// thread-safe.
///
/// Idempotent: registering a hook that's already in the list is a no-op
/// (compared by std::function target address).
using DisconnectHook = std::function<void()>;
void RegisterDisconnectHook(DisconnectHook hook);

// Start the server thread (idempotent). Returns false if already running or failed to start.
bool Start();

// Stop the server thread and disconnect any client. Idempotent and blocks until stopped.
void Stop();

// True if the server thread is currently running.
bool IsRunning() noexcept;

}  // namespace Discrete::PipeServer
