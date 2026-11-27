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

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace Discrete::PipeServer {

using Handler = std::function<nlohmann::json(const nlohmann::json& args)>;

// Register a command handler. Overwrites any previous handler for the same name.
void RegisterHandler(std::string name, Handler handler);

// C-L8: Sorted list of all currently registered command names (emitted in
// handshake `"commands"` so the UI can query capabilities without branching
// on backend strings).
std::vector<std::string> RegisteredCommands();

// C-L6: Per-client worker connection id (non-zero inside a client worker's
// RPC handlers and disconnect hooks; 0 outside PipeServer workers).
std::uint64_t CurrentClientId() noexcept;

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

/// Drop every registered disconnect hook. MUST be called on the teardown that
/// pairs with whatever registered them: the hook list is append-only, and
/// Discrete_Shutdown clears the g_initialized latch, so a Shutdown -> Init
/// cycle would otherwise register a SECOND copy of each backend hook and every
/// later client disconnect would run N full GC drains instead of one.
void ClearDisconnectHooks();

/// Number of per-client worker threads currently inside DLL-owned work.
/// Counted before the worker is detached and decremented as its last act, so
/// inside a disconnect hook the calling worker still counts itself: a value of
/// 1 there means "I am the last client".
int ActiveClientCount();

// Start the server thread (idempotent). Returns false if already running or failed to start.
bool Start();

// Stop the server thread and disconnect any client. Idempotent and blocks until stopped.
void Stop();

// True if the server thread is currently running.
bool IsRunning() noexcept;

}  // namespace Discrete::PipeServer
