// PipeServer.h — newline-delimited JSON named-pipe server.
//
// Wire format (each direction): one JSON object per line, terminated by '\n'.
// Request shape  : {"id": <int>, "cmd": <string>, "args": <object?>}
// Response shape : {"id": <int>, "result": <any>}
//                  or {"id": <int>, "error": {"code": <int>, "message": <string>}}
//
// Multi-client concurrent server (up to kMaxPipeInstances = 4): overlapped IO
// on the acceptor + one detached worker thread per accepted client, with a
// stop event and bounded drain on Stop() (A-L2).
#pragma once

#include <nlohmann/json.hpp>

#include <cstdint>
#include <functional>
#include <initializer_list>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace Discrete::PipeServer {

using Handler = std::function<nlohmann::json(const nlohmann::json& args)>;

// Register a command handler. Overwrites any previous handler for the same name.
void RegisterHandler(std::string name, Handler handler);

// A-L20: Mark command names whose execution generates heavy managed/metadata
// allocations (batch scans, IL/AOT xref walks, snapshots, symbol dumps). A
// client worker's CurrentClientRanHeavyRpc() latches true once it dispatches
// any marked command, allowing disconnect hooks (e.g. GC drain) to skip
// lightweight short-lived bridge bursts.
void MarkHeavyCommands(std::initializer_list<const char*> names);

// C-L8: Sorted list of all currently registered command names (emitted in
// handshake `"commands"` so the UI can query capabilities without branching
// on backend strings).
std::vector<std::string> RegisteredCommands();

// C-L6: Per-client worker connection id (non-zero inside a client worker's
// RPC handlers and disconnect hooks; 0 outside PipeServer workers).
std::uint64_t CurrentClientId() noexcept;

// A-L20: True inside a client worker (including its disconnect hooks) iff
// that client dispatched at least one command marked via MarkHeavyCommands().
bool CurrentClientRanHeavyRpc() noexcept;

/// Per-client cleanup hook. Fired after EVERY accepted client disconnects
/// (clean exit OR pipe error), on the per-client worker thread, after
/// `DisconnectNamedPipe` + `CloseHandle` but before the worker exits.
///
/// Used by backends to drain Mono / IL2CPP GC finalizer queues that piled
/// up during a long-running batch RPC (gated on CurrentClientRanHeavyRpc()
/// so short bridge bursts do not stall the game — A-L20), and to drop
/// sessions owned by the disconnecting client (C-L6).
///
/// Hooks may be called concurrently from multiple per-client worker
/// threads (up to `kMaxPipeInstances`). Implementations must be
/// thread-safe.
///
/// Idempotent by `key` (A-L2): re-registering an existing key replaces its
/// hook in place rather than appending a duplicate.
using DisconnectHook = std::function<void()>;
void RegisterDisconnectHook(std::string key, DisconnectHook hook);

/// Drop every registered disconnect hook. MUST be called on the teardown that
/// pairs with whatever registered them: Discrete_Shutdown clears the
/// g_initialized latch, so a Shutdown -> Init cycle clears and re-registers
/// cleanly.
void ClearDisconnectHooks();

/// Number of per-client worker threads currently inside DLL-owned work.
/// Counted before the worker is detached and decremented as its last act, so
/// inside a disconnect hook the calling worker still counts itself: a value of
/// 1 there means "I am the last client".
int ActiveClientCount();

// Start the server thread (idempotent). Returns false if already running or failed to start.
bool Start();

// Stop the server thread and disconnect active clients. Idempotent; waits up
// to a bounded deadline (2 s) for the acceptor and active client workers to
// exit (detaching on timeout rather than deadlocking — A-L2).
void Stop();

// True if the server thread is currently running.
bool IsRunning() noexcept;

}  // namespace Discrete::PipeServer
