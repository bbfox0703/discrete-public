# DLL Spec — ABI, Module Map, Lifecycle

> The C ABI surface, module-by-module map, and lifecycle of the
> backend DLL (`UnityDumper-Mono.dll` / `UnityDumper-IL2CPP.dll`).
>
> Pairs with [pipe-protocol.md](pipe-protocol.md) (the JSON-over-pipe
> wire layer) and [mono-internals.md](mono-internals.md) /
> [il2cpp-internals.md](il2cpp-internals.md) (per-backend deep dives).

---

## Build artifacts

Every backend ships as one DLL with the same C ABI:

| File | Backend | Compile-time macro |
|---|---|---|
| `UnityDumper-Mono.dll` | Mono (`mono-2.0-bdwgc.dll` / `mono.dll`) | `UNITYCEDUMPER_BACKEND_MONO` |
| `UnityDumper-IL2CPP.dll` | IL2CPP (`GameAssembly.dll`) | `UNITYCEDUMPER_BACKEND_IL2CPP` |

**Rule of one backend**: each DLL is built with exactly one
`-DBACKEND={Mono,IL2CPP}` flag — the matching backend's source
compiles in, the other does not. Avoids two `Init()` paths racing
each other in a single binary. Targets one Unity backend per
process.

`-DBACKEND=Both` builds both DLLs side-by-side as separate output
files (default for `.\build.cmd publish`).

---

## C ABI surface

[ExportAPI.h](../dll/src/shared/ExportAPI.h):

```cpp
extern "C" {
    enum UnityDumperBackend : int32_t {
        UnityDumperBackend_Unknown = 0,
        UnityDumperBackend_Mono    = 1,
        UnityDumperBackend_IL2CPP  = 2,
    };

    int      UnityDumper_Init();          // idempotent; 0 = success
    void     UnityDumper_Shutdown();      // idempotent
    int      UnityDumper_StartPipe();     // 0 = success or already running
    void     UnityDumper_StopPipe();
    UnityDumperBackend UnityDumper_GetBackendType();
    uint32_t UnityDumper_GetBuildNumber();
}
```

Eight exports total. Stable across all phases — adding a new export
goes through ABI review (PR comment will flag it).

### `Init` / `Shutdown` / `StartPipe` / `StopPipe`

`DllMain` auto-runs `UnityDumper_Init()` + `UnityDumper_StartPipe()`
on `DLL_PROCESS_ATTACH`. The four exports exist for callers that
want explicit control:

- **CE Lua bridge**: lazy — calls `Init` + `StartPipe` only after
  `dofile([[unity_dump.lua]])` has resolved the proxy / inject path.
  No-op if the DLL self-started already.
- **Tests / hosts that load the DLL without injecting**: can drive
  the pipe lifecycle manually.

Idempotent: second `Init()` is a no-op; second `StartPipe()` returns
the same status.

`Shutdown` tears down the pipe server, flushes logs, releases caches
(class-pointer cache from Phase 17, etc.). The host process is
typically about to exit — we don't try to be re-startable after
shutdown.

### `GetBackendType` / `GetBuildNumber`

Diagnostic helpers. The pipe `handshake` carries the same info plus
much more, but these C exports are reachable from CE Lua via
`executeCodeEx` for very-early-bring-up — before the pipe server is
ready.

---

## Module map

```
dll/src/
├── main.cpp                    DllMain — auto-run Init + StartPipe
├── shared/                     Backend-agnostic infrastructure
│   ├── ExportAPI.{h,cpp}       C ABI + every pipe handler
│   ├── BackendDetector.{h,cpp} Module-sniff Mono / IL2CPP / Unknown
│   ├── BuildInfo.h.in          → BuildInfo.h (build number, commit hash)
│   ├── Logger.{h,cpp}          Per-PID rotated log files (5 channels)
│   ├── MagicStrings.h          Pipe name + every command name + module names
│   ├── Memory.{h,cpp}          SEH-protected SafeRead<T> / SafeReadBytes / IsReadable
│   ├── ModuleHelper.{h,cpp}    GetModuleBase / ResolveExport / EnumerateModules
│   ├── PipeServer.{h,cpp}      Threaded named-pipe server (overlapped IO)
│   ├── AobPattern.{h,cpp}      Phase 18a-3 hand-rolled x64 length disassembler
│   ├── MethodSignatureFormat.h Phase 18 humanSuffix builder (header-only)
│   └── version.rc.in           → version.rc (Win32 file properties)
├── mono/                       Mono backend (see mono-internals.md)
├── il2cpp/                     IL2CPP backend (see il2cpp-internals.md)
└── proxy/                      version.dll / winmm.dll proxies (see proxy-dlls.md)
```

### Module dependency graph

```
                   ExportAPI (C ABI + pipe handlers)
                         │
            ┌────────────┼────────────────────┐
            │            │                    │
            ▼            ▼                    ▼
       BackendDetector   PipeServer       (backend) Mono / IL2CPP modules
            │                                  │
            └─────► Logger ◄──────────── Memory │ ModuleHelper
                                                │
                                                ▼
                                        AobPattern (Phase 18)
                                        MethodSignatureFormat
```

`shared/` only knows about backend selection through compile-time
macros. The pipe handler dispatch table in `ExportAPI.cpp`
conditionally compiles the Mono or IL2CPP arm based on the
`-DBACKEND=` flag.

---

## Pipe server

[PipeServer.cpp](../dll/src/shared/PipeServer.cpp):

- **Pipe name**: `\\.\pipe\UnityDumpBfx` (single instance per host
  process — second connection from a second client is rejected with
  `ERROR_PIPE_BUSY`).
- **IO model**: overlapped IO + dispatcher thread. Each connection
  is single-threaded — pipe handlers run sequentially per connection.
- **Wire format**: newline-delimited JSON. Request:
  `{"id": N, "cmd": "...", "args": {...}}\n`. Response:
  `{"id": N, "result": {...}}\n` or `{"id": N, "error": "..."}\n`.

The single-instance + single-threaded-per-connection design keeps
the per-PID class-pointer cache (Phase 17) and the resolver
singletons safe to access without locks.

See [pipe-protocol.md](pipe-protocol.md) for the every command's
shape.

---

## Logger

[Logger.cpp](../dll/src/shared/Logger.cpp):

- Five channels: `init`, `scan`, `metadata`, `pipe`, `walk`.
- Per-PID subfolder under `%LOCALAPPDATA%\Discrete_UEDumper\Logs\<pid>\<channel>\`.
- 8 MB rotation, 4-file ring per channel.
- Lock-free writer (per-thread buffer + atomic enqueue).

Useful for post-mortem analysis when the pipe handler returns an
error — the channel logs carry stack-context the wire response
strips for size.

---

## Memory safety

Every cross-process struct read goes through
[Memory.h](../dll/src/shared/Memory.h):

```cpp
template <typename T>
bool SafeRead(uintptr_t addr, T* out) noexcept;          // SEH-protected

bool SafeReadBytes(uintptr_t addr, void* buffer, size_t n) noexcept;
bool IsReadable(uintptr_t addr, size_t n) noexcept;      // VirtualQuery
```

- `__try` / `__except` around the actual read so a page fault
  becomes `false` instead of an AV.
- `IsReadable` is a fast pre-check for tight loops (Phase 17 backward
  scan) — does a `VirtualQuery` only when crossing a page boundary,
  caches the result.

**Bare `*(T*)addr` reads on remote process memory are forbidden**.
The game can mutate, free, or unmap the page at any time.

---

## SEH leaf for risky calls

Specific RPC handlers wrap risky calls in `__try` / `__except` so a
hard crash inside e.g. the JIT becomes a wire error code, not a host
tear-down:

| Handler | Risky call |
|---|---|
| `findMethodInstances` | `mono_compile_method` (Mono JIT can fault on weird generics) |
| `callMethod` | `mono_runtime_invoke` / `il2cpp_runtime_invoke` (managed code can do anything) |
| `getMethodAobPattern` | `SafeReadBytes` over arbitrary code addresses |
| `walkInstance` | UTF-16 string decode + pointer dereferences |
| `whatIsAt` | Backward scan over arbitrary process memory |

Each handler returns a backend-specific error code identifying
which leaf faulted (e.g. `native_crash_in_runtime_invoke`,
`native_crash_in_compile_method`).

---

## Lifecycle

```
Host process (Unity game) starts
   │
   ▼
DLL injected (via DiscreteUI Pick & Inject, proxy DLL, or Test-Handshake.ps1)
   │
   ▼
DllMain (DLL_PROCESS_ATTACH):
   1. Logger::EnsureRoot() — create %LOCALAPPDATA%\Discrete_UEDumper\Logs\<pid>\
   2. UnityDumper_Init() — initialize backend resolver singletons (lazy bind)
   3. UnityDumper_StartPipe() — spawn pipe server thread
   4. Return TRUE
   │
   ▼
Pipe server accepts connections at \\.\pipe\UnityDumpBfx
   │
   ▼
Per-RPC dispatch:
   • Read newline-delimited JSON request
   • Look up handler by `cmd`
   • Mono path → MonoThreadAttach guard → call handler
   • IL2CPP path → IL2CPPThreadAttach guard → call handler
   • Wrap risky calls in SEH leaf
   • Serialize response, write to pipe
   │
   ▼
Host process exits
   │
   ▼
DllMain (DLL_PROCESS_DETACH):
   1. UnityDumper_StopPipe() — break the pipe server's accept loop
   2. UnityDumper_Shutdown() — flush logs, release caches
   3. Return TRUE
```

The DLL stays mapped for the entire host process lifetime. Re-injection
is unsupported (and `LoadLibrary` of the same DLL twice is a no-op
anyway). To re-inject after a code change, close the host process
first.

---

## Build numbering

[BuildInfo.h.in](../dll/src/shared/BuildInfo.h.in) is a CMake
`configure_file` template that generates `BuildInfo.h` with:

- `UNITYCEDUMPER_BUILD_NUMBER` — auto-incrementing integer from
  `build_number.txt` (root of repo, bumped by `build.ps1` on every
  build).
- Build date / commit hash / configuration (Debug / Release).

The build number flows into `UnityDumper_GetBuildNumber()` and the
`handshake` response, so the UI can display "Build #234" in its
status bar.

---

## ABI / wire compatibility

| Layer | Stability |
|---|---|
| C ABI (`UnityDumper_*` exports) | **Stable** — additions are vetted, removals never |
| Pipe protocol (`cmd` names, request/response JSON shape) | **Versioned** via `protocol` field in `handshake`; current = `1` |
| Wire `error` codes | **Documented** in [pipe-protocol.md](pipe-protocol.md); new error codes flow through PR |
| Internal C++ APIs | **Unstable** — refactor freely |

The UI's `BackendAdapter` reads the handshake's `protocol` and
gates feature usage. Bumping the protocol is a breaking change —
every UI in the wild needs the matching DLL.

---

## Source map

| File | What |
|---|---|
| [ExportAPI.h](../dll/src/shared/ExportAPI.h) | C ABI declarations |
| [ExportAPI.cpp](../dll/src/shared/ExportAPI.cpp) | All pipe handlers (~2700 lines — one giant dispatch table) |
| [main.cpp](../dll/src/main.cpp) | DllMain — auto-runs Init + StartPipe |
| [PipeServer.cpp](../dll/src/shared/PipeServer.cpp) | Named-pipe server + dispatcher |
| [BackendDetector.cpp](../dll/src/shared/BackendDetector.cpp) | Module-sniff Mono/IL2CPP/Unknown |
| [Memory.cpp](../dll/src/shared/Memory.cpp) | SEH-protected reads + readability checks |
| [Logger.cpp](../dll/src/shared/Logger.cpp) | Per-PID rotated logs |
| [AobPattern.cpp](../dll/src/shared/AobPattern.cpp) | Phase 18a-3 hand-rolled x64 length disassembler |
| [MagicStrings.h](../dll/src/shared/MagicStrings.h) | Single source of truth for command names + module names |
| [BuildInfo.h.in](../dll/src/shared/BuildInfo.h.in) | CMake configure template for build metadata |
