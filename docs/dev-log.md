# Dev Log

Append-only activity history, newest first. Each entry references a build number
from `build_number.txt` so commits can be cross-referenced. Existing entries are
never modified or rewritten — archive older entries into `docs/archive/` only if
this file grows too large.

**Reading tip:** grep `^## ` for the index, then read from the top (newest-first).

> **Looking for current state or open backlog?** See [plan.md](plan.md) for the
> current phase, open TODO list, and phase ledger.

-----

## 2026-11-26 (build 1555) — Fix A-M3 + A-M4 (+ A-L6) (named-pipe remote rejection, user+SYSTEM protected DACL, `FILE_FLAG_FIRST_PIPE_INSTANCE`, export single-init ownership gate, and PipeServer Stop/Start hardening + tests)

- **Bug fixes (`A-M3`, `A-M4`, `A-L6`):**
  - **`A-M3` (`PipeSecurity.h`, `PipeServer.cpp`):**
    - Added `dll/src/shared/PipeSecurity.h` providing `BuildCurrentUserAndSystemSddl()`, `ScopedPipeSecurityAttributes`, `MakePipeOpenMode(firstInstance)`, and `MakePipeMode()`.
    - Hardened `CreateNamedPipeW` in `PipeServer.cpp` with `PIPE_REJECT_REMOTE_CLIENTS` (blocking network/SMB access), an explicit protected DACL (`D:P(A;;GA;;;SY)(A;;GA;;;<current-user-sid>)`) granting access exclusively to `SYSTEM` and the current process user, and `FILE_FLAG_FIRST_PIPE_INSTANCE` on the first created instance after `Start()` (stopping cleanly with an explicit log message on `ERROR_ACCESS_DENIED` if another process already owns the pipe name).
  - **`A-M4` (`ModuleLifecycle.h`, `main.cpp`, `ExportAPI.cpp`):**
    - Added module-local single-init ownership tracking (`ModuleLifecycle::OwnsInit()` / `SetOwnsInit()` / `kErrNotOwner = 2`) populated by `OnAttach` after `TryClaimSingleInit()`.
    - Gated public exports `Discrete_Init()` and `Discrete_StartPipe()` on `ModuleLifecycle::OwnsInit()` so a second dormant DLL copy in the same process cannot bypass the per-PID mutex to spawn a competing pipe server or stack duplicate MinHook detours.
  - **`A-L6` (`PipeServer.cpp`):**
    - Added null check on `CreateEventW` before `ConnectNamedPipe` (avoiding `WAIT_FAILED` busy-spin), throttled repeated `CreateNamedPipeW` failure logs to once per 10 s, guarded `PipeServer::Start()` against restarting while a detached previous acceptor thread is still alive (`g_acceptorAlive`), and wrapped both acceptor and detached client `std::thread` launches in `try / catch (...)` with clean state rollback.
  - **Tests:**
    - Added `Test_AM3_AM4_PipeSecurityAndInitOwnership` (+19 checks on both x64 and x86, linking `advapi32`) to `dll/tests/dll_core_test.cpp` exercising `MakePipeMode`/`MakePipeOpenMode`, SDDL & `SE_DACL_PROTECTED` verification, live Windows kernel `CreateNamedPipeW` with `FILE_FLAG_FIRST_PIPE_INSTANCE` conflict (`ERROR_ACCESS_DENIED`), subsequent instance creation, local same-user client connection, and `ModuleLifecycle::OwnsInit()` gating.
  - Marked `A-M3`, `A-M4`, and `A-L6` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1555) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 532 checks passed (x64) / 535 checks passed (x86).

-----

## 2026-11-26 (build 1554) — Fix A-M7 + A-M8 (+ A-L3, A-L16) (wall-clock detour drain & retained trampoline, shutdown ordering with `AbortPending`, preserved disconnect hooks on `StopPipe`, inline main-thread guard + tests)

- **Bug fixes (`A-M7`, `A-M8`, `A-L3`, `A-L16`):**
  - **`A-M7` (`MonoMainThreadHook.cpp`, `IL2CPPMainThreadHook.cpp`, `MainThreadDispatcher.{h,cpp}`):**
    - Made `g_orig_runtime_invoke` `std::atomic<fn_runtime_invoke_t>` and stopped calling `MH_RemoveHook` / nulling `g_orig_runtime_invoke` in `Uninstall()` (reusing `g_hookTarget` with `MH_EnableHook` on subsequent `Install()`), eliminating the entry race where a thread entered `HookedRuntimeInvoke` just before `MH_DisableHook` patched the prologue and before `g_inDetour.fetch_add(1)` (safe because the backend DLL is pinned via `A-H1`).
    - Added shared `Discrete::MainThreadDispatcher::WaitUntilZeroWithDeadline(counter, timeoutMs, pollSleepMs)` using `GetTickCount64()` real wall-clock deadlines instead of iteration-counted `2000 × Sleep(1)` loops (which stretched up to ~31 s under the default 15.6 ms timer tick), and logged a clear `WARN` if in-flight detours outlive the 2 s drain window.
  - **`A-M8` (`MainThreadDispatcher.{h,cpp}`, `PipeServer.cpp`, `ExportAPI.cpp`):**
    - Added `MainThreadDispatcher::AbortPending()` to wake and abort queued `Pending` items without disabling the dispatcher, flipped `g_initialized = false` under `g_mutex` in `Shutdown()`, and added a locked `g_initialized` re-check before `g_queue.push_back(item)` in `SubmitAndWait()` to close the TOCTOU window between `Shutdown()` and enqueue.
    - Updated `Discrete_Shutdown()` to call `MainThreadDispatcher::Shutdown()` BEFORE `PipeServer::Stop()`, and updated `PipeServer::Stop()` to call `MainThreadDispatcher::AbortPending()` and use `WaitUntilZeroWithDeadline` before draining active client workers — preventing workers blocked in `SubmitAndWait` from stalling the 2 s drain and leaking `g_stopEvent`.
  - **`A-L3` (`PipeServer.cpp`, `ExportAPI.cpp`):**
    - Moved `PipeServer::ClearDisconnectHooks()` out of `PipeServer::Stop()` and into `Discrete_Shutdown()` so a `Discrete_StopPipe` → `Discrete_StartPipe` cycle preserves hooks registered by `Discrete_Init()` while `Discrete_Shutdown` → `Discrete_Init` still prevents duplicate hook registration.
  - **`A-L16` (`MainThreadDispatcher.{h,cpp}`):**
    - Added RAII `InsideWorkItemGuard` used by both `Drain()` and `SubmitAndWait()`'s inline main-thread path so `IsCurrentlyDrainingOnMainThread()` is set during inline main-thread closures and always restored even if a closure throws; refreshed stale header comments in `MainThreadDispatcher.h`.
  - **Tests:**
    - Linked `MainThreadDispatcher.cpp` and `Logger.cpp` into `discrete_dll_test` (`dll/tests/CMakeLists.txt`) and added `Test_AM7_AM8_AL16_TeardownAndMainThreadDispatcher` (+32 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` covering `WaitUntilZeroWithDeadline` (immediate zero, async drain, bounded wall-clock timeout), `SubmitAndWait` inline main-thread `IsCurrentlyDrainingOnMainThread` flag (normal + throwing), `AbortPending()` waking a 30 s stalled worker while keeping the dispatcher initialized, and `Shutdown()` waking a stalled worker + rejecting subsequent submits.
  - Marked `A-M7`, `A-M8`, `A-L3`, and `A-L16` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1554) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 513 checks passed (x64) / 516 checks passed (x86).

-----

## 2026-11-26 (build 1553) — Fix B-M10 + C-M1 (+ A-L15) (exception-safe IL2CPP worker pool, cancellable `FindAllInstances` & long-running RPCs, thread-local `ScanThreadCap`, UI cancel flow + tests)

- **Bug fixes (`B-M10`, `C-M1`, `A-L15`):**
  - **`B-M10` (`ParallelScanDriver.h`, `IL2CPPAddressFinder.{h,cpp}`, `MonoAddressFinder.{h,cpp}`):**
    - Added `RunIndexedWorkerPool` to `dll/src/shared/ParallelScanDriver.h` so per-thread indexed pools (`IL2CPP::FindAllInstances` and `IL2CPP::ReverseLookupIndex::Build`) catch thread-spawn failures (`std::system_error` / `std::bad_alloc`), join any already-started workers without hitting `std::terminate`, and drain remaining work inline on slot `0` (accumulating `qScanned[tid] += qs` instead of overwriting).
    - Switched `IL2CPP::FindAllInstances` from `Memory::ScanThreadCount()` to `Memory::EffectiveScanThreads()` so the serial/anti-tamper thread cap applies to IL2CPP snapshot walks as well as value/reverse-lookup scans.
    - Added cooperative `ScanCancel::IsCancelRequested()` checks and `bool cancelled` to both `IL2CPP::BulkInstancesResult` and `Mono::BulkInstancesResult`, discarding partial `byClass` maps on cancellation so truncated heap walks are never returned as complete snapshots.
  - **`A-L15` (`Memory.{h,cpp}`):**
    - Changed `ScanThreadCap` from a process-global `std::atomic<unsigned int>` to `thread_local unsigned int t_scanThreadCap` with save/restore in `ScanThreadCapScope`, preventing concurrent pipe clients from clobbering each other's thread cap.
  - **`C-M1` (`ExportAPI.cpp`, `MonoFieldXref.{h,cpp}`, `IL2CPPFieldXref.{h,cpp}`, `IL2CPPClassReferenceDiagnostic.{h,cpp}`, `PipeClient.cs`, `BackendAdapter.cs`, `ProtocolModels.cs`, `SnapshotViewModel.cs`, `LiveInspectionViewModel.cs`, `FieldReferencesViewModel.cs`, `FieldAcrossInstancesViewModel.cs`):**
    - Opened `ScanCancel::Scope` and checked `ScanCancel::IsCancelRequested()` before returning across all remaining long-running handlers on both backends (`snapshotBegin`, `findInstances`, `listFieldValuesAcrossInstances`, `findFieldReferences`, and IL2CPP `diagnoseClassReferences`), returning `{"ok": false, "cancelled": true, "error": "cancelled"}` on cancel instead of silently returning truncated results or ignoring cancel/disconnect.
    - Added cooperative `ScanCancel` polling and `bool cancelled` propagation inside `Mono::FindFieldReferences`, `IL2CPP::FindStaticFieldReferences` / `FindInstanceFieldReferences`, and `IL2CPP::DiagnoseClassReferences`.
    - Exposed `PipeClient.PipeName` and updated `BackendAdapter.CancelScanAsync` to connect to `_pipe.PipeName` instead of the hardcoded default pipe name; wired `SnapshotViewModel.CancelCaptureAsync` to call `_backend.CancelScanAsync()` alongside `_cts?.Cancel()`; added `[JsonPropertyName("cancelled")]` to `SnapshotBeginResult`, `FindInstancesResult`, `ListFieldValuesResult`, and `FindFieldReferencesResult`, and handled cancelled responses cleanly in `SnapshotViewModel`, `LiveInspectionViewModel`, `FieldReferencesViewModel`, and `FieldAcrossInstancesViewModel`.
  - **Tests:**
    - Added `Test_BM10_AL15_CM1_IndexedPoolThreadCapAndCancel` (+21 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` covering `RunIndexedWorkerPool` parallel partition, mid-pool `std::system_error` inline drain, first-thread `std::bad_alloc` fallback, worker-body exception containment, cross-thread & nested `ScanThreadCapScope` isolation, and `ScanForPointer` cancel + recovery.
    - Added `ui/UnityDumpUI.Tests/ScanCancellationProtocolTests.cs` (3 xUnit v3 tests covering AOT `ProtocolJsonContext` deserialization across all 4 DTOs, real in-process `NamedPipeServerStream` out-of-band `cancelScan` cancelling `snapshotBegin`, and ViewModel cancelled-response handling).
  - Marked `B-M10`, `C-M1`, and `A-L15` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1553) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 481 checks passed (x64) / 484 checks passed (x86).

-----

## 2026-11-26 (build 1550) — Fix C-M2 + C-M3 (nullable RPC error `id`, id-mismatch teardown, `PipeClient` read timeout & non-deadlocking Disconnect/Dispose, DLL `main_thread_timeout_ms` clamp + tests)

- **Bug fixes (`C-M2`, `C-M3`):**
  - **`C-M2` (`ProtocolModels.cs`, `PipeClient.cs`, `PipeServer.cpp`):**
    - Changed `RpcEnvelope<TResult>.Id` from `int` to `int?` so JSON-RPC error envelopes carrying `"id": null` (e.g. `-32700`, `-32600`, `-32603`) deserialize cleanly instead of throwing `JsonException`.
    - Updated `PipeClient.SendAsync` to surface `Id == null && Error != null` as `PipeProtocolException("Pipe error <code>: <message>")`, and on real response `id` mismatch to tear down streams (`DisposeStreamsAsync()`) and fire `Disconnected` off-gate before throwing `PipeProtocolException` so subsequent callers reconnect cleanly instead of drifting permanently out of sync.
    - Updated `dll/src/shared/PipeServer.cpp` to echo the original request's `id` (`request.contains("id") ? request["id"] : json(nullptr)`) when `response.dump(...)` throws `-32603 response serialization failed`.
  - **`C-M3` (`PipeClient.cs`, `MainThreadDispatcher.h`, `ExportAPI.cpp`):**
    - Updated outdated single-client header comment in `PipeClient.cs`, added configurable `PipeClient.ReadTimeout` (and per-call `readTimeout` parameter on `SendAsync`) that tears down the pipe, fires `Disconnected`, and throws `PipeDisconnectedException` on read stall.
    - Fixed `PipeClient.DisconnectAsync()` and `DisposeAsync()` deadlocking behind `_gate` when a `SendAsync()` call is blocked on a stalled server: both now call `AbortUnderlyingPipe()` (atomically exchanging & disposing `_pipe`) BEFORE acquiring `_gate`, waking the blocked `ReadLineAsync` immediately; made `DisposeAsync()` idempotent via `_disposed`.
    - Added `Discrete::MainThreadDispatcher::ClampMainThreadTimeoutMs` (`[1, 60000]` ms) in `MainThreadDispatcher.h` and applied it in both Mono and IL2CPP `callMethod` handlers in `ExportAPI.cpp` so negative or unbounded `main_thread_timeout_ms` values can never turn `SubmitAndWait` into an `INFINITE` wait.
  - **Tests:**
    - Added 6 real in-process `NamedPipeServerStream` + `PipeClient` tests in `ui/UnityDumpUI.Tests/PipeClientReliabilityTests.cs` (`"id": null` error envelope, malformed JSON, id-mismatch teardown & `Disconnected`, per-request `ReadTimeout` stall recovery, non-deadlocking `DisconnectAsync` mid-stall, non-deadlocking & idempotent `DisposeAsync` mid-stall).
    - Added `Test_CM3_ClampMainThreadTimeoutMs` (9 boundary checks on both x64 and x86) to `dll/tests/dll_core_test.cpp`.
  - Marked `C-M2` and `C-M3` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1550) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1214 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 460 checks passed (x64) / 463 checks passed (x86).

-----

## 2026-11-26 (build 1549) — Fix A-M9 + A-M10 (proxy coexistence detection with `ForeignProxyPresent` and ProxyDeploy Refresh/bulk race protection + tests)

- **Bug fixes (`A-M9`, `A-M10`):**
  - **`A-M9` (`IProxyInstaller.cs`, `IUnityGameScanner.cs`, `WindowsProxyInstaller.cs`, `SteamGameScanner.cs`, `ProxyGameRow.cs`, `ProxyDeployViewModel.cs`):**
    - Updated `WindowsProxyInstaller.DetectSync` to check `IsOurDll` on both `version.dll` and `winmm.dll`: only Discrete-owned DLLs count toward `ProxyInstallStatus.Kind` and the mutual-exclusion error (`ourVersion && ourWinmm`), while third-party DLLs (ReShade/ENB/SpecialK/mod loaders) are reported separately via the new `ForeignProxyPresent` property.
    - Eliminated the contradiction with `InstallSync`: installing Discrete `winmm.dll` alongside a third-party `version.dll` (or vice versa) now detects cleanly as `Kind = Winmm` with `ErrorMessage = null` and `ForeignProxyPresent = "version.dll"`, and a standalone third-party proxy is no longer misclassified as an outdated Discrete install or targeted by `UpdateAllDeployed`.
    - Surfaced `ForeignProxyPresent` through `UnityGameEntry`, `SteamGameScanner`, and `ProxyGameRow.DeployStatusLabel` (`[other: version.dll]`), and added an internal constructor seam on `WindowsProxyInstaller` for isolated filesystem unit testing.
  - **`A-M10` (`ProxyDeployViewModel.cs`, `ProxyDeployWindow.axaml`):**
    - Added `CanRefresh => !IsScanning && !IsBusy` and `CanRunBulk => !IsBusy && !IsScanning`, wired `CanExecute` + `NotifyCanExecuteChangedFor` on `RefreshCommand` and all three bulk commands, and bound `ProxyDeployWindow.axaml` buttons to `CanRefresh` / `CanRunBulk`.
    - Added `if (IsBusy) return;` + CTS ownership/disposal guards to `RefreshAsync`, and changed `RunBulkAsync` to iterate a snapshot (`Games.ToList()`) so collection mutations can never raise `InvalidOperationException` mid-batch.
  - Added 8 xUnit v3 tests in `ui/UnityDumpUI.Tests/ProxyInstallerAndDeployViewModelTests.cs` covering standalone foreign proxy detection, `winmm.dll` + foreign `version.dll` coexistence install/detect/uninstall, refusing overwrite of foreign `version.dll`, both-Discrete mutex error, `UpdateAllDeployed` skipping foreign-only rows while updating coexisting rows, Refresh gating & snapshot safety mid-bulk, and bulk gating mid-scan.
  - Marked `A-M9` and `A-M10` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1549) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1208 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1548) — Fix E-M5 (DLL-free Mono overload registration: positional `registerSymbolEx`, primitive alias normalization, ambiguity rejection + Lua/C# tests)

- **Bug fix (`E-M5`):**
  - **`scripts/mono_registersymbol_helpers.lua`:**
    - Added `_DISCRETE_PRIMITIVE_ALIASES` + `_discrete_canonical_type()` normalizing Mono `mono_signature_get_desc` shorthands (`int`, `single`, `float`, `string`, `bool`, `uint`, `long`, `ulong`, `short`, `ushort`, `byte`, `sbyte`, `char`, `double`, `object`, `void`, `intptr`/`native int`, `uintptr`/`native uint`, `decimal`) to canonical `System.*` names while preserving exact trailing modifiers (`[]`, `[,]`, `&`, `*`) so `System.Int32` matches `int` and NEVER matches `System.Int32[]`, `int[]`, `int&`, or `System.UInt32`.
    - Added `_discrete_split_sig_params()` to parse rendered signature strings (bare lists, parenthesised forms, generic commas `<...>`, multi-dimensional array ranks `[,]`, `ref`/`out`/`in`, and trailing parameter identifiers) into ordered positional parameter tokens.
    - Upgraded `_G.discrete_mono_registerSymbolEx` to check exact parameter count and exact positional match on BOTH the `mono_method_get_parameters` typename path and the `mono_method_getSignature` fallback path (including when `mono_method_get_parameters` throws or returns nil typenames), and to collect all matching overloads and fail via `eprint` (setting `_G.DISCRETE_HAD_ERROR = true`) on ambiguity instead of silently taking the first hit.
    - Upgraded `_G.discrete_mono_registerSymbolBySignatureMatch` (kept for legacy CTs / fallback) to use token-wise alias-aware matching and reject multi-overload ambiguity via `eprint` instead of registering the first match.
  - **`ui/UnityDumpUI.Core/Services/Export/MonoRegisterSymbolAaScriptGenerator.cs`:**
    - Switched all generated overloads (both zero-arg `{}` and N-arg ordered `{ ... }`) to call `_G.discrete_mono_registerSymbolEx` (updating the lazy-load guard to `_G.discrete_mono_registerSymbolEx == nil`), cleanly distinguishing swapped-order overloads like `(Int32, String)` vs `(String, Int32)` and repeated-type overloads like `(Int32)` vs `(Int32, Int32)`.
  - **Tests:**
    - Updated `ui/UnityDumpUI.Tests/MonoRegisterSymbolAaScriptGeneratorTests.cs` (including swapped/repeated parameter order/count assertions) and added embedded-helper contract checks in `ui/UnityDumpUI.Tests/BridgeLuaResourcesTests.cs`.
    - Added live Lua execution harness `tools/verify/test_mono_registersymbol_helpers.py` (29 Lua assertions running `scripts/mono_registersymbol_helpers.lua` under mocked CE `mono_*` APIs).
  - Marked `E-M5` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1548) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; `python tools/verify/test_mono_registersymbol_helpers.py` passed (29 Lua checks + `luac -p` syntax check); C# xUnit v3 tests 1200 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1547) — Fix E-M7 + E-L4 (layout-accurate C/C++ SDK header exporter with padding/static_assert/math PODs/dedup + `UnityTypeMap` pointerSize + tests)

- **Bug fixes (`E-M7`, `E-L4`):**
  - **`E-M7` (`SdkHeaderExporter.cs`, `AssemblyBrowserViewModel.cs`):**
    - Rewrote `SdkHeaderExporter` to emit compile-ready, memory-layout-accurate C/C++ headers (`#pragma once`, `#include <cstddef>`, `#include <cstdint>`): collects all non-static instance fields across ancestors + self, sorts by runtime byte offset, inserts explicit `uint8_t _pad_XXXX[0xNN]` from offset `0` (covering both the managed object header `0x10`/`0x08` and inter-field gaps), skips invalid negative offsets (e.g. normalized ThreadStatic `-1`) as comments, and emits overlapping explicit-layout fields as comments without shifting subsequent members.
    - Added guarded `#ifndef DISCRETE_UNITY_MATH_TYPES` POD definitions (`Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Color`, `Color32`, `Rect`, `Bounds`) whenever any emitted field references a built-in Unity math type.
    - Added `SdkHeaderExporter.RenderAsync(..., options, lookup, ct)` and wired `AssemblyBrowserViewModel.ExportSdkHeaderAsync` to use it so custom enums unwrap to their underlying integer C type (`uint8_t`/`int16_t`/`int32_t`/etc.), custom ValueType structs emit `uint8_t name[0xNN]` (or comment-only when size is unknown so padding preserves alignment), and offline fallback infers small non-pointer gaps `< ptr` as `uint8_t name[0xNN]` to avoid overlapping the next field.
    - Disambiguated shadowed/duplicate member names across inheritance with `_<DeclaringClass>` (and `_0x<Offset>` if still colliding), escaped C++ reserved keywords (`class_`, `default_`, etc.), and appended `static_assert(offsetof(Struct, member) == 0xNN, ...)` checks after `#pragma pack(pop)`.
  - **`E-L4` (`UnityTypeMap.cs`, `CeXmlExporter.Live.cs`):**
    - Added optional `int pointerSize = 8` parameter to `UnityTypeMap.Resolve` so `System.IntPtr`, `System.UIntPtr`, and pointer fallbacks return 4-byte widths (`4 Bytes`, `int32_t`/`uint32_t`, `SizeBytes = 4`) on x86 targets; passed `options.PointerSize` in `CeXmlExporter.Live` flattened struct & collapsed leaf-pointer paths. (Together with `E-H2` and `E-M2`, all hardcoded 8-byte pointer sites listed in `E-L4` are now resolved.)
  - Added 14 new xUnit v3 test cases in `ui/UnityDumpUI.Tests/SdkHeaderExporterTests.cs` including a packed-struct layout verifier checking every member's computed offset against both its `// +0xNN` comment and `static_assert(offsetof(...))`, covering sorting/padding, Unity math PODs, inherited shadowing & C++ keywords, async enum/ValueType/unknown-struct/class lookup, offline small-field gap inference, overlapping explicit layout, x86 4-byte layout, `UnityTypeMap.Resolve(type, 4)`, and cancellation propagation.
  - Marked `E-M7` and `E-L4` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1547) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1199 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1546) — Fix E-M2 + E-M3 (+ E-L9) (Address Lookup String dereference & x86 pointer width, Mode-B `ce_table_bootstrap.lua` `injectDLL`/backend/logging fixes + tests)

- **Bug fixes (`E-M2`, `E-M3`, `E-L9`):**
  - **`E-M2` (`AddressLookupAaScriptGenerator.cs`, `AobMakerBridge.cs`, `AddressLookupViewModel.cs`, `MainWindowViewModel.cs`):**
    - Added `PointerSize` (default `8`) to `AddressLookupRequest`, wired `AddressLookup.GamePointerSize = hs.GamePointerSize ?? 0` in `MainWindowViewModel` alongside `Inspector` and `Browser`, and updated `MapClrToCe` so `System.IntPtr`, `System.UIntPtr`, and unknown reference-type fallbacks map to `CeValueType.Dword` on x86 and `CeValueType.Qword` on x64.
    - Fixed `System.String` child records in `AddressLookupAaScriptGenerator.BuildChild` to dereference the managed `String` object pointer before reading UTF-16 `_firstChar` (`2 * ptr + 4`: `+0x14` on x64, `+0x0C` on x86), emitting `Offsets = [charsOffset, f.Offset]` in Cheat Engine's `.CT` / `MemoryRecord.Offset[0..n-1]` order (`Offsets[0]` applied last, `Offsets[n-1]` applied first after dereferencing `Address`). Clarified `AaScriptChildRecord.Offsets` XML docs to match AOBMaker's `CreateAAScriptWithRecords` mapping.
  - **`E-M3` + `E-L9` (`scripts/ce_table_bootstrap.lua`):**
    - Replaced `if injectDLL(dllPath) == nil then` with `if not injectDLL(dllPath) then` so `false` returns from CE's `injectDLL` properly raise an error instead of silently continuing.
    - Synchronized `_detectBackendInTarget()` with `scripts/utils.lua`'s `M.detectBackend()` (calling `reinitializeSymbolhandler`, normalizing full-path module names via basename extraction, recognizing `userassembly.dll` / `mono-2.0-sgen.dll` / `mono-2.0-boehm.dll`, and adding `getModule` fallback).
    - Reset `rawset(_G, "DISCRETE_HAD_ERROR", false)` at the start of both `bootstrap()` and `teardown()`, and split `_extractDirPath()` / `_ensureDir()` so `teardown()` never runs `mkdir` and `bootstrap()` skips `os.execute('mkdir ...')` when the extract directory already exists or CE's `createDir` succeeds (`E-L9`).
  - Added 10 new xUnit v3 test cases across `ui/UnityDumpUI.Tests/AddressLookupAaScriptGeneratorTests.cs` (9 cases covering x64/x86 `System.String` 2-level pointer offsets, nearest-field String, and x86/x64 `IntPtr`/`UIntPtr`/reference mapping) and `ui/UnityDumpUI.Tests/BridgeLuaResourcesTests.cs` (regression checks for `ce_table_bootstrap.lua` boolean `injectDLL`, backend parity, `DISCRETE_HAD_ERROR` reset, teardown dir behavior, and LF-only endings).
  - Marked `E-M2`, `E-M3`, and `E-L9` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1546) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1185 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1545) — Fix E-M1 + E-M6 (exporter cancellation propagation, CTS ownership, and Browser stale `_currentChain` + tests)

- **Bug fixes (`E-M1`, `E-M6`):**
  - **`E-M1` (`CeXmlExporter.cs`, `CeXmlExporter.Live.cs`, `CsxExporter.cs`, `AssemblyBrowserViewModel.cs`, `LiveInspectionViewModel.cs`):**
    - Replaced all 9 remaining bare `catch { ... }` blocks across `CeXmlExporter.cs` and `CeXmlExporter.Live.cs` with `catch (Exception ex) when (ex is not OperationCanceledException)` and added `ct.ThrowIfCancellationRequested()` checks at recursive entry points and element loops in `CeXmlExporter` and `CsxExporter`, so cancelling or superseding an export aborts cleanly instead of returning truncated XML/CSX.
    - Hardened export CTS ownership in `AssemblyBrowserViewModel` (`CopyCeXmlAsync`, `CopyCeFieldAsync`, `ExportAsync`) and `LiveInspectionViewModel` (`CopyCeXmlInstanceAsync`, `CopyCeFieldNodeCoreAsync`): each invocation owns and disposes its own `CancellationTokenSource` in `finally`, and checks `ReferenceEquals(_exportCts, myCts)` before writing to the clipboard/file, updating progress/errors, or clearing `IsExporting` — preventing a superseded export from clobbering the newer export's clipboard or busy indicator.
  - **`E-M6` (`AssemblyBrowserViewModel.cs`):**
    - Cleared `_currentChain` and cancelled `_detailCts` immediately in `OnSelectedClassChanged`, and notified `CanExecuteChanged` on all export commands.
    - Updated `CanExport()` to require `_currentChain.Count > 0` with `_currentChain[^1]` matching `SelectedClass.Name` and `SelectedClass.Namespace`, and guarded `LoadClassDetailAsync` against stale CTS completion so switching classes (or a failed `getClassInfo`) can never export the previously selected class.
  - Added 8 regression tests in `ui/UnityDumpUI.Tests/ExportCancellationAndBrowserChainTests.cs` covering live/template CE XML & CSX cancellation propagation, non-cancellation fault tolerance, and `AssemblyBrowserViewModel` selection/chain synchronization.
  - Marked `E-M1` and `E-M6` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1545) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1175 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1544) — Fix A-M11 + A-M12 (AVX2 string-scan scalar tail alignment & identity-guard regex ReDoS + tests)

- **Bug fixes (`A-M11`, `A-M12`):**
  - **`A-M11` (`dll/src/shared/Memory.{h,cpp}`):** After the 32-byte AVX2 loop in `SehMemcmpProbe` exited, the scalar tail resumed directly at `i` (a multiple of 32). When `stride` did not divide 32 (e.g. `3, 5, 6, 7, 12, 64`), the tail stepped on an off-grid sequence (`32, 32+s, ...`), missing aligned hits and reporting unaligned false positives. Fixed by rounding `i` up to the next multiple of `stride` (with overflow guard) before entering the scalar tail, and exposed `Discrete::Memory::ScanStringSpan` for direct unit testing.
  - **`A-M12` (`tools/check_no_local_paths.py`):** `SESSION_DIR_PATTERN`'s intermediate segment class `[^\s:*?"<>|]{0,120}` did not exclude path separators (`/` and `\`), causing exponential backtracking ($O(2^N)$) on long non-matching paths with many separators. Excluded `/` and `\` (`_NO_SEP`) so every path decomposes uniquely in linear time.
  - Added `Test_AM11_StringScanAvx2TailAlignment` to `dll/tests/dll_core_test.cpp` (targeted stride=6 off-grid decoy test + exhaustive scalar-oracle parity across 13 strides `1..64` and varied tail lengths on both x64 and x86) and `tools/verify/test_check_no_local_paths.py` (positive/negative cases + 50-separator ReDoS timing assertions `< 0.1s`).
  - Marked `A-M11` and `A-M12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1544) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; `python tools/verify/test_check_no_local_paths.py` passed (4 positive, 3 negative, 3 ReDoS checks); C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1542) — Fix A-M1 + A-M2 (+ A-L5, A-L7) (per-client disconnect monitor, pipelined ReadLine buffer, atomic ScanCancel + tests)

- **Bug fixes (`A-M1`, `A-M2`, plus `A-L5` and `A-L7` in `PipeServer` / `ScanCancel`):**
  - **`A-M1` + `A-L7` (`dll/src/shared/ScanCancel.h`, `dll/src/shared/PipeServer.cpp`):**
    - Packed global `activeCount` and `cancelBit` into a single `std::atomic<std::uint64_t>` (`(activeCount << 1) | cancelBit`), eliminating the check-then-store TOCTOU between `RequestCancel()` and `Scope::~Scope()` (`A-L7`).
    - Added per-worker `ClientSlotGuard` binding a stack-local `std::atomic<int> clientScanDepth` in `ServeClient`, incremented/decremented by `ScanCancel::Scope` on that thread. Each client's disconnect monitor now checks ONLY its own `clientScanDepth > 0` instead of the global scan count, so a second client (e.g. a CE Lua bridge burst) disconnecting during a non-scan RPC never probes or cancels another client's active scan (`A-M1`) and never races its own overlapped `ReadFile`.
  - **`A-M2` + `A-L5` (`dll/src/shared/PipeLineBuffer.h`, `dll/src/shared/PipeServer.cpp`):**
    - Added persistent per-connection `Discrete::PipeFraming::LineBuffer` so bytes arriving after `'\n'` in the same `ReadFile()` chunk are preserved for subsequent pipelined requests instead of being silently dropped (`A-M2`).
    - Returned explicit `ReadLineResult::TooLarge` when a request exceeds the 1 MiB line cap and sent a `-32600 "request too large"` JSON-RPC error + log entry before closing (`A-L5`); removed duplicate `#include <thread>`.
  - Added `Test_AM1_PerClientScanCancelAndAtomicState` (including a 20,000-iteration concurrent `Scope` vs `RequestCancel` race test) and `Test_AM2_PipeLineBufferPreservesPipelinedBytes` (+48 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp`.
  - Marked `A-M1`, `A-M2`, `A-L5`, and `A-L7` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1542) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 445 checks passed (x64) / 448 checks passed (x86).

-----

## 2026-11-26 (build 1541) — Fix B-H2 (object-extent bounds check & pure-memory generic/array klass recognition on Mono + IL2CPP + tests)

- **Bug fix (`B-H2` — final High-severity audit finding, 10/10 High complete):** `BuildClassRangeCache` only enumerated TypeDef rows (`0x02xxxxxx` on Mono, `il2cpp_image_get_class` on IL2CPP), omitting runtime-synthesized generic instantiations (`List<T>`, `Dictionary<K,V>`) and arrays (`T[]`), and neither `WhatIsAt` nor `ReverseLookupIndex::Lookup` verified that `addr - instance < objectSize`. As a result, addresses inside generic instances, arrays, or gaps were falsely attributed to the nearest preceding TypeDef neighbor object with huge `nearestFieldDelta`, and Mono lacked IL2CPP's array fallback parity.
  - Added shared pure header `dll/src/shared/AddressBounds.h`:
    - `ComputeObjectExtent` & `IsOffsetWithinExtent` for regular objects (`class_instance_size` / `object_get_size`, clamped to `>= 2*ptr`), SZARRAYs (`4*ptr + max_length * elemSize`, capped at 64M with overflow protection), and `System.String` (`2*ptr + 4 + (charLen + 1) * 2`).
    - `IntersectCandidateOffsets`, `LooksLikePlausibleClassName` (ASCII + valid UTF-8), and `ValidateDynamicKlassMemory` — pure-memory structural validation of dynamic generic/array klasses using self-calibrated `(image, element_class, name)` offsets with zero runtime API calls on unvalidated memory.
    - `SelectContainingEntry` — bounded `ReverseLookupIndex` interval lookup that rejects out-of-bounds neighbor hits and backtracks up to 16 entries past embedded false-positive header pointers inside larger enclosing objects.
  - Added optional runtime bindings: `mono_class_instance_size` (`MonoResolver.{h,cpp}`) and `il2cpp_class_instance_size` + `il2cpp_object_get_size` (`IL2CPPResolver.{h,cpp}`).
  - Updated `MonoAddressFinder.{h,cpp}` and `IL2CPPAddressFinder.{h,cpp}` symmetrically:
    - Cached sorted loaded `imageSet`, per-class static `extentInfo`, and `dynamicClassSet` + capped negative memo `notDynamicClass`; calibrated `(image, element_class, name)` offsets once across up to 16 TypeDef samples.
    - Enforced `IsOffsetWithinExtent` in `WhatIsAt`, `WhatIsAtAcceptingArrays`, and `ReverseLookupIndex::Lookup` (including `lastExtent_` on the locality fast-path).
    - Accepted validated dynamic generic & array klasses in Mono `WhatIsAt`, `FindAllInstances`, and `ReverseLookupIndex::Build` (gated by `vtable->domain == rootDomain` + pure-memory validation) and in IL2CPP `WhatIsAt` / `WhatIsAtAcceptingArrays` (feeding the shared `dynamicClassSet` merged into `FindAllInstances` / `ReverseLookupIndex::Build`).
  - Documented object-extent bounds and generic/array coverage in [address-finder.md](address-finder.md).
  - Added `Test_BH2_AddressBoundsAndDynamicKlass` (+43 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp`.
  - Marked `B-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1541) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 397 checks passed (x64) / 400 checks passed (x86).

-----

## 2026-11-26 (build 1539) — Fix A-H1 (+ A-L1, A-L12) (pin backend module, make DllMain detach a no-op, add x64/x86 OS-loader probe tests)

- **Bug fix (`A-H1`, plus `A-L1` and `A-L12` in `main.cpp`):** Previously `DllMain(DLL_PROCESS_DETACH)` with `lpReserved == NULL` (external `FreeLibrary` from CE "unload module", Process Hacker, or a third-party injector) ran `OnDetach() -> Discrete_Shutdown() -> PipeServer::Stop()` + `MainThreadHook::Uninstall()` under the OS loader lock. Because exiting worker/monitor threads block on `LdrShutdownThread` waiting for the same loader lock, the wait inevitably timed out and detached threads returned into unmapped code pages (access violation / game crash).
  - Added shared header `dll/src/shared/ModuleLifecycle.h` with `PinModuleContaining()` (`GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS)`) and `TryClaimProcessMutex()` (`CreateMutexW(..., bInitialOwner = FALSE, ...)`).
  - Updated `dll/src/main.cpp`:
    - `OnAttach` pins the active backend DLL via `PinModuleContaining(&OnAttach)` immediately after claiming the per-PID single-init mutex, so external `FreeLibrary` calls become safe no-ops and cannot unmap code under live pipe workers or MinHook detours.
    - Removed `OnDetach()` and made `DllMain(DLL_PROCESS_DETACH)` an unconditional no-op returning `TRUE` on both `lpReserved != nullptr` and `lpReserved == nullptr`; explicit teardown remains exclusively the off-DllMain `Discrete_Shutdown()` export (`scripts/unity_dump.lua`).
    - Fixed `A-L1` (`bInitialOwner = FALSE`, no cross-thread `ReleaseMutex`) and `A-L12` (`MagicStrings::LogCategoryInit` instead of raw `L"init"`).
  - Updated stale `OnDetach()` comment in `dll/src/shared/PipeServer.cpp` while keeping the bounded `WaitForSingleObject` wait as defense-in-depth.
  - Added test-only `dll/tests/pin_probe_dll.cpp` (built alongside `discrete_dll_test` in `build/cpp-tests-release-{x64,x86}/bin`, never copied to `dist\`) and `Test_AH1_PinnedModuleSurvivesFreeLibrary` in `dll/tests/dll_core_test.cpp`: verifies `TryClaimProcessMutex` duplicate-rejection & cross-thread acquire (no `WAIT_ABANDONED`), then loads `discrete_pin_probe.dll` with a live worker thread, invokes `FreeLibrary` 3×, asserts <1 s elapsed / module stays mapped / `DLL_PROCESS_DETACH` never fires / worker stays alive, and verifies explicit off-DllMain `Probe_Shutdown()` joins cleanly — on both x64 and x86 (WOW64).
  - Marked `A-H1`, `A-L1`, and `A-L12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1539) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 354 checks passed (x64) / 357 checks passed (x86) (+25 A-H1 checks on each arch).

-----

## 2026-11-26 (build 1538) — Fix E-H2 (template CE XML & CSX enum/value-struct/collection layout + tests)

- **Bug fix (`E-H2`):** Template-mode CE XML (`CeXmlExporter.cs`, the Assembly Browser default when `CeXmlBaseAddress` is `"INSTANCE_ADDRESS"`) and CE Structure Dissect (`CsxExporter.cs`) previously treated all unknown game types as dereferenced pointers (`Offsets=[0]` / `Vartype="Pointer"`) — causing enum and custom `System.ValueType` struct fields to dereference inline integers/bytes as addresses — and emitted broken collection layouts (missing the SZARRAY header `+0x20`/`+0x10`, omitting `List<T>._items` dereference `+0x10`/`+0x08`, striding reference-type arrays by `ComputeStructSize(child)` without per-slot deref, and expanding `HashSet`/`IList`/`IEnumerable` as flat arrays).
  - Added shared internal `ui/UnityDumpUI.Core/Services/Export/TemplateExportHelpers.cs`: `IsEnumClass`, `IsValueTypeClass`, `ResolveEnumUnderlyingType` (reads `value__` underlying integer type with `System.Int32` fallback), `ComputeValueTypeHeaderAdjust` (strips Mono/IL2CPP boxed `2*ptr` header offsets when structs are emitted inline), `ComputeInlineStructSizeAsync` (natural-aligned inline struct stride), `IsNonArrayCollectionType`, and pointer-size helpers.
  - Updated `CeXmlExporter.cs` template mode: resolves game field types via `IClassInfoLookup` even at depth cap so `System.Enum` emits an inline scalar leaf (using its underlying type) and `System.ValueType` emits an inline group with `offsets: null` (or inline leaf at depth cap); contiguous `T[]` and `List<T>` use `4*ptr` SZARRAY data start, `List._items` (`2*ptr`) deref, pointer-strided reference slots with `Offsets=[0]`, and inline contiguous slots for primitive/enum/value-struct elements; non-array collections (`HashSet`, `IList`, `IEnumerable`, `Dictionary`, …) emit watchable pointer leaves; root template header annotates `(x64 layout)` / `(x86 layout)` and uses `PointerVartype(options)`.
  - Updated `CsxExporter.cs` in sync: enums emit flat scalar `<Element>`s; `System.ValueType` structs flatten inline at `parentOffset + (memberOffset - headerAdjust)` (or `Array of byte` at depth cap, never `Pointer`); `T[]` and `List<T>` emit `List` (`_items` + `_size`) and SZARRAY structures at exact `ptr`-dependent offsets with accurate `AutoCreateStructsize`.
  - Updated `CeXmlExporter.Live.cs` (`EmitMetadataFieldAsync`) to reuse `ResolveEnumUnderlyingType` so non-`Int32` enums in metadata-filled null slots also emit their true width/signedness.
  - Wired `Browser.GamePointerSize = hs.GamePointerSize ?? 0` in `MainWindowViewModel.cs` and passed `PointerSize` through `AssemblyBrowserViewModel.CurrentExportOptions`.
  - Added 13 regression test cases in `ui/UnityDumpUI.Tests/CeXmlAndCsxTemplateExporterTests.cs` covering enums (Int32, Byte, depth 0), custom boxed-offset ValueType structs, x64 + x86 `T[]` & `List<T>` reference arrays, inline struct/enum/primitive arrays, non-array collections, and layout tags across both `CeXmlExporter` and `CsxExporter`.
  - Marked `E-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1538) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

-----

## 2026-11-26 (build 1537) — Fix D-H1 (32-bit x86 ISA mode for Rust AOB pattern builder + tests)

- **Bug fix (`D-H1`):** `dll/rust/src/aob.rs` previously decoded every method byte window strictly in x86-64 mode regardless of target architecture. On 32-bit Mono JIT code, `0x40..0x4F` (`INC`/`DEC r32`) were consumed as REX prefixes (desynchronizing all subsequent instructions), `0xA0..0xA3` (`MOV AL/EAX, moffs32`) consumed 8 bytes instead of 4 and left absolute static addresses un-wildcarded, and `mod=00 SIB base=5` absolute jump-table displacements stayed literal.
  - Added 32-bit protected-mode decoding (`build_pattern_for_mode(..., bitness)` and C ABI `dc_aob_build_pattern_mode` in `dll/rust/src/aob.rs` + `dll/rust/include/discrete_core.h`), while keeping `dc_aob_build_pattern` / `build_pattern` 100% byte-identical in 64-bit mode.
  - In 32-bit mode: `0x40..0x4F` decode as 1-byte `INC`/`DEC r32`; `0xA0..0xA3` decode as 4-byte wildcarded `moffs32` (preserving width under `0x66` operand-size prefix); `mod=00 rm=5` and `mod=00 SIB base=5` wildcard 32-bit absolute `disp32`; 32-bit-only opcodes (`PUSHAD`/`POPAD`, `0x82`, segment push/pop, BCD ops) decode cleanly.
  - Updated `AobPattern.h` and `AobPatternRs.cpp` to pass `bitness` (defaulting to `sizeof(void*) * 8` — 32 on x86 Mono DLL, 64 on x64 Mono/IL2CPP DLLs).
  - Added 6 Rust unit / FFI / small-window invariant tests in `dll/rust/src/aob.rs` (running on both x86_64 and i686).
  - Marked `D-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1537) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

-----

## 2026-11-26 (build 1536) — Add offline C++ DLL self-test suite (`discrete_dll_test`, x64 + x86) for C-H1 / C-H2 / C-H3 / A-H2 / B-H1

- **What changes:** Added the repository's first offline C++ DLL unit test executable (`dll/tests/dll_core_test.cpp`), compiled with `/W4 /WX` and executed on **both x64 and x86 (WOW64)** by `.\build.cmd test` and `.\build.cmd publish` (never copied into `dist\`).
  - Extracted pure, runtime-independent helpers into shared headers included by both the production DLLs and the test suite (zero code duplication):
    - `dll/src/shared/ExportHelpers.h` — `NormalizeFieldOffset` (`C-H1`), `ResolveArrayElementLayout` (`C-H2`), and `DecodeCompressedSignedInt` / `ClrTypeNameToIl2CppTag` / `DecodeIl2CppFieldDefaultByTag` (`C-H3`), replacing inline copies in `ExportAPI.cpp`.
    - `dll/src/shared/ParallelScanDriver.h` — `RunWorkerPool` (`A-H2`), used by `Memory.cpp::RunParallelScan`.
    - `dll/src/mono/MonoVtableFilter.h` — `IsAlignedMonoVtablePtr` / `IsPlausibleMonoVtableCandidate` (`B-H1`), used by `MonoAddressFinder.cpp`.
  - **Covered (`329` checks on x64, `332` checks on x86):**
    - `C-H1`: normal offsets, x64/x86 unsigned `SIZE_MAX` (`0xFFFFFFFFFFFFFFFF` / `0xFFFFFFFF`), `> INT32_MAX`, and negative signed offsets → `-1`.
    - `C-H2`: resolved reference types ignoring `class_value_size` and caller hints (returning pointer width 8 on x64 / 4 on x86), inline value structs (`Vector3`), enums, primitives, and unresolved fallbacks.
    - `C-H3`: v24/v27 raw little-endian (`01 00 00 00` → `1`, `FF FF FF 7F` → `Int32.MaxValue`, signed `-1` vs unsigned max, truncated blobs), v29/v31/v39 `0xFE`/`0xFF` sentinels + 1-/2-byte compressed signed ints, `I8`, `R4` `+Infinity`, and `ClrTypeNameToIl2CppTag`.
    - `A-H2`: compile-time `static_assert(!noexcept(...))` on `ScanForValue` and `ScanForString`; `RunWorkerPool` normal parallel run, mid-pool `std::system_error` spawn failure with join + inline drain, first-thread `std::bad_alloc` fallback, worker-body `std::bad_alloc` containment; and live `ScanForValue` / `ScanForString` over a `VirtualAlloc` RW page.
    - `B-H1`: `0 mod 8` and `19 * sizeof(void*)` IMT-prefixed vtable pointers (`4 mod 8` accepted on x86, rejected on x64), unaligned/null/`< 0x10000` rejection.
  - Wired `-DBUILD_TESTS=ON` in `CMakeLists.txt` and `Run-CppTests` (`-Target CppTest` + integrated into `-Target Test` / `All`) in `build.ps1`.
- **Tests & build:** `.\build.cmd publish` (build #1536) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

-----

## 2026-11-26 (build 1534) — Fix B-H1 (x86 Mono vtable `& 7` filter dropping interface-implementing classes)

- **Bug fix (`B-H1`):** In `dll/src/mono/MonoAddressFinder.cpp`, both `WhatIsAt` (`:706`) and `SehSweepRegionLoad` (`:839`, shared by `FindAllInstances` and `ReverseLookupIndex::Build`) filtered candidate `MonoVTable*` pointers with `& 7u`. On 32-bit Mono, classes implementing any interface prepend a 19-slot IMT (`19 * 4 = 76` bytes, `4 mod 8`) ahead of the `MonoVTable` in an 8-byte-aligned mempool, so every array (`T[]`), `List<T>`, `Dictionary<K,V>`, and interface-implementing game class had a `4 mod 8` vtable address and was silently dropped.
  - Changed both vtable alignment checks to `alignof(void*) - 1` (`& 3u` on x86, `& 7u` on x64), while preserving the 8-byte alignment checks for `instAddr` (BDWGC 8-byte granule) and `MonoClass*` (mempool-allocated).
  - Marked `B-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1534) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1533) — Fix A-H2 (`ScanForValue` / `ScanForString` `noexcept` & `RunParallelScan` thread-pool `std::terminate`)

- **Bug fix (`A-H2`):** `Memory::ScanForValue` and `Memory::ScanForString` were declared `noexcept` despite allocating region/chunk/result vectors and spawning worker threads via `RunParallelScan`; on 32-bit hosts under address-space pressure, any `std::bad_alloc` or `std::system_error` invoked `std::terminate()` and killed the host game. Additionally, if `pool.emplace_back(worker)` threw after earlier threads had started, `pool`'s destructor destroyed joinable `std::thread` objects (triggering `std::terminate` even for non-`noexcept` `ScanForPointer`), and uncaught exceptions inside a worker thread would also terminate the host.
  - Removed `noexcept` from `ScanForValue` and `ScanForString` in `dll/src/shared/Memory.h` and `dll/src/shared/Memory.cpp` (so allocations failing under memory pressure propagate cleanly to `PipeServer::Dispatch` as JSON-RPC errors), and updated the stale single-threaded doc comment in `Memory.h`.
  - Wrapped `RunParallelScan`'s worker lambda in `try / catch (...)` (`noexcept`) and guarded `pool.emplace_back(worker)` so thread-creation failure joins any already-started workers before finishing remaining chunks inline on the calling thread.
  - Marked `A-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1533) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe` (including `r2-dump` / `xref-dump` harnesses); identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1532) — Fix C-H3 (IL2CPP `getEnumValues` FieldDefault decoder unified with `il2cppGetMetadataTypeFields`)

- **Bug fix (`C-H3`):** In `dll/src/shared/ExportAPI.cpp`, IL2CPP `getEnumValues` previously carried its own older `decodeCompressedSigned` / `decodeTyped` lambdas that decoded every `<=32-bit` enum FieldDefault as an ECMA-335 compressed signed integer without checking metadata version or v29+ `0xFE`/`0xFF` min/max sentinels. On v24/v27 (Unity 2018–2020) games where FieldDefaults are raw little-endian, every `<=32-bit` enum value decoded wrong (e.g. `01 00 00 00` → `-64`), and on v29+ games `int.MaxValue`/`int.MinValue` members failed with `decode_error`.
  - Extracted `DecodeCompressedSignedInt`, `ClrTypeNameToIl2CppTag`, and `DecodeIl2CppFieldDefaultByTag` into file-scope `#if defined(DISCRETE_BACKEND_IL2CPP)` helpers and routed both `il2cppGetMetadataTypeFields` and `getEnumValues` through them (net -62 lines in `ExportAPI.cpp`).
  - Marked `C-H3` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1532) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1531) — Fix C-H2 (`walkArray` reference-type element stride overwritten by `class_value_size`)

- **Bug fix (`C-H2`):** In `dll/src/shared/ExportAPI.cpp` (`CmdWalkArray`), when the caller passed an element class (`elementAssembly` / `elementNamespace` / `elementName`), both the IL2CPP and Mono branches unconditionally called `class_value_size(k)` and overwrote `elemSize`. For reference-type elements (e.g. `UserClass[]`), `class_value_size` returns `instance_size - sizeof(ObjectHeader)` rather than pointer size, causing every array element after `[0]` to step by the wrong stride and breaking both Inspector array expansion and live CE XML export.
  - Guarded `class_value_size` behind `elemIsValueType` (using `SehValueSize`) in both IL2CPP and Mono paths, and explicitly set `elemSize = ManagedLayout::kPointerSize` whenever `elemClassResolved && isPointer` (`!elemIsValueType`).
  - Marked `C-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1531) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1530) — Fix C-H1 (`[ThreadStatic]` `SIZE_MAX` field offsets breaking JSON deserialization)

- **Bug fix (`C-H1`):** IL2CPP encodes `[ThreadStatic]` fields with `THREAD_STATIC_FIELD_OFFSET = (size_t)-1` (`18446744073709551615` on x64, `4294967295` on x86). Emitting raw unsigned offsets broke `System.Text.Json` deserialization into `int` / `int?` / `long?` DTO fields on `getClassInfo`, `dumpClassInfo`, `searchSymbols`, `whatIsAt`, `getStaticFieldData`, and `findFieldReferences`, aborting browsing/batch exports for any class or assembly with `[ThreadStatic]` fields (e.g. `System.Threading.Thread`, `UniTask`, `Newtonsoft`).
  - **DLL (`dll/src/shared/ExportAPI.cpp`):** Added `NormalizeFieldOffset<T>` (mapping negative values and unsigned values `> INT32_MAX` to `-1`) and applied it across all Mono and IL2CPP field-offset JSON serialization sites (`getClassInfo`, `dumpClassInfo`, `searchSymbols`, `findFieldReferences`, `getFieldInfo`, `getStaticFieldData`, `whatIsAt`).
  - **UI (`ui/UnityDumpUI.Core/Services/`):** Added AOT-safe [FieldOffsetJsonConverter.cs](../ui/UnityDumpUI.Core/Services/FieldOffsetJsonConverter.cs) (`FieldOffsetJsonConverter`, `NullableFieldOffsetJsonConverter`, `NullableLongFieldOffsetJsonConverter`) on `FieldEntry.Offset`, `WalkedField.Offset`, `GetStaticFieldDataResult.Offset`, `WhatIsAtField.Offset`, `SearchSymbolMatch.Offset`, and `FieldReferenceFieldInfo.Offset` in `ProtocolModels.cs` as defense in depth against older DLLs or corrupt offsets.
  - **PipeClient (`ui/UnityDumpUI.Core/Services/PipeClient.cs`):** Wrapped `JsonSerializer.Deserialize` `JsonException` into `PipeProtocolException` so callers receive a consistent protocol exception.
  - **Tests:** Added 5 regression tests in `ui/UnityDumpUI.Tests/FieldOffsetProtocolTests.cs` covering x64/x86 unsigned `SIZE_MAX` sentinels across all affected DTOs and malformed-JSON `PipeProtocolException` wrapping.
  - **Docs:** Marked `C-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1530) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1528) — Fix E-H1 (x86 `List<T>._items` pointer read in live CE XML export), add `GEMINI.md` & badge, rename audit docs

- **Bug fix (`E-H1`):** `CeXmlExporter.Live.cs` (`EmitCollectionLiveAsync`) previously read a hardcoded 8 bytes for `List<T>._items` and parsed them with `ParseLeQword` (which required 16 hex chars). On 32-bit Mono targets (`PointerSize == 4`), that swept the adjacent `_size` field at `+0xC` into the upper 32 bits (`_items | (_size << 32)`), causing `WalkArrayAsync` to fail on every non-empty `List<T>`.
  - Changed the `ReadMemoryAsync` size to `ptr` (4 on x86, 8 on x64) and replaced `ParseLeQword` in `CeXmlExporter.cs` with `ParseLePointer(string leHex, int byteCount)`.
  - Added two x86 regression tests in `ui/UnityDumpUI.Tests/CeXmlExporterLiveTests.cs` (`RenderLive_X86_NonEmptyList_ReadsFourByteItemsPointer` and `RenderLive_X86_NonEmptyList_ParsesExactFourByteHexResponse`), and updated `FakeLive.ReadMemoryAsync` to slice returned hex to `size * 2` chars and record `LastReadMemorySize`.
- **Build warning cleanup:** Fixed a pre-existing x86 MSVC `warning C4244` (`uint64_t` → `uintptr_t`) in `dll/src/shared/ExportAPI.cpp:4622` with `static_cast<std::uintptr_t>`.
- **Docs & metadata:**
  - Added [GEMINI.md](../GEMINI.md) with `CLAUDE.md` as its first rule, plus workflow rules (append-only `docs/dev-log.md`, zero-error/zero-warning `.\build.cmd publish`, Python-first tooling with UTF-8 care, `vendor/` via `.\sync_vendor.ps1`, read-only sibling repos).
  - Added the Gemini badge to [README.md](../README.md) and [README.zh-TW.md](../README.zh-TW.md).
  - Renamed `docs/AUDIT_PLAN.md` → [audit-2026-11-23-plan.md](audit-2026-11-23-plan.md) and `docs/BUG_REPORT.md` → [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md), updated internal cross-references, and marked `E-H1` fixed.
  - Created this append-only [dev-log.md](dev-log.md).
- **Tests & build:** `.\build.cmd publish` (build #1528) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1149 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).
