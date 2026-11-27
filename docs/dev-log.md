# Dev Log

Append-only activity history, newest first. Each entry references a build number
from `build_number.txt` so commits can be cross-referenced. Existing entries are
never modified or rewritten — archive older entries into `docs/archive/` only if
this file grows too large.

**Reading tip:** grep `^## ` for the index, then read from the top (newest-first).

> **Looking for current state or open backlog?** See [plan.md](plan.md) for the
> current phase, open TODO list, and phase ledger.

-----

## 2026-11-27 (build 1588) — Fix A-L18 + A-L19 (completes 100% of Block A and 118/118 of the entire 2026-11-23 audit: identity guard deny-list / `--staged` / unpushed diff scan / `--list` semantics, pre-commit hook wiring, `proxy_precrt_gate.py` zero-target non-zero exit)

- **Bug fixes (`A-L18`, `A-L19` — completes 100% of Block A and all 118/118 Critical/High/Medium/Low findings in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md)):**
  - **`A-L18` (`tools/check_no_local_paths.py`, `.githooks/pre-commit`):**
    - Added `COMMON_WORDS` deny-list (preventing false positives on CI runner usernames/hostnames such as `runner`, `admin`, `build`, `corp`) plus `DISCRETE_IDENTITY_ALLOW` env-var opt-out in `runtime_identifiers()`.
    - Fixed `--list` semantics so it prints every finding group (commit messages, unpushed diffs, AND tracked files — never early-returning on `msg_hits`) and always returns `EXIT_OK (0)`.
    - Added `unpushed_commit_added_lines()` (`git log --branches --not --remotes --format=COMMIT:%H -p --no-color --unified=0`) so leaks committed locally and later overwritten before push are caught prior to `git push`.
    - Added `--staged` mode (`git diff --cached --name-only -z --diff-filter=ACMR` + `git show :<path>`) and expanded `selftest()` from 11 to 16 cases covering unpushed diff leaks, `--list` multi-category output, `--staged` pass/fail, and `COMMON_WORDS` / `DISCRETE_IDENTITY_ALLOW`.
    - Updated `.githooks/pre-commit` (`#!/usr/bin/env bash`, LF-only) to iterate staged `*.lua` files via NUL-delimited `git diff -z` + `while IFS= read -r -d ''` (safe for filenames with spaces) and run `tools/check_no_local_paths.py --staged` on every commit.
  - **`A-L19` (`tools/verify/proxy_precrt_gate.py`):**
    - Tracked `tested` count in `run_all()`; when zero proxy DLLs are found/tested in `--dist`, prints `NOTHING TESTED - no proxy DLLs found in <dist>` and exits with code `2` instead of reporting `PASS` (exit `0`).
  - Marked `A-L18` and `A-L19` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md) (`118/118` fixed, `0` remaining).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1588) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, `inject-x86.exe`, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; `check_no_local_paths.py --selftest` 16/16 OK, full & `--staged` CHECK OK; `proxy_precrt_gate.py --all` PASS (exit 0 on `dist/`, exit 2 `NOTHING TESTED` on empty dir); C# xUnit v3 tests 1262 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 964 passed (x64) / 964 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1587) — Fix A-L8 + A-L9 + A-L17 (least-privilege injector with PE-arch & loaded-module fallback, `inject-x86` `IsWow64Process` & UTF-8 fixes, filtered-only proxy bulk ops & atomic staged proxy install)

- **Bug fixes (`A-L8`, `A-L9`, `A-L17`):**
  - **`A-L8` (`ui/UnityDumpUI/Platform/WindowsDllInjector.cs`):**
    - Replaced legacy `PROCESS_ALL_ACCESS (0x1F0FFF)` with least-privilege `PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION` (matching `inject-x86`'s `kAccess`).
    - Added `TryReadPeMachine` and `ValidateDllMachineForTarget` to validate the DLL's PE COFF Machine (`0x014C` x86 vs `0x8664` x64) against `IsWow64Process` (failing loudly if `IsWow64Process` itself fails) before touching remote memory or spawning `inject-x86.exe`.
    - Added AOT-safe `TryVerifyModuleLoaded` (`EnumProcessModulesEx` + `GetModuleFileNameExW`) fallback when `GetExitCodeThread` returns `0`, eliminating the ~1/65536 x64 ASLR low-32-bit-zero false-failure bug; set UTF-8 stdout/stderr encoding on `inject-x86.exe` `ProcessStartInfo`.
  - **`A-L9` (`tools/inject-x86/main.cpp`):**
    - Split `IsWow64Process` API failure (`QueryTargetIsWow64` returning `false` → exit `1` with `GetLastError`) from confirmed x64 targets (exit `2`) so access-denied errors never report a misleading bitness mismatch.
    - Converted wide DLL path / invalid PID arguments to UTF-8 via `WideCharToMultiByte(CP_UTF8, ...)` before `printf`/`fprintf` so non-ASCII paths never truncate stdout under the default C locale.
  - **`A-L17` (`ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs`, `ui/UnityDumpUI/Views/ProxyDeployWindow.axaml.cs`, `ui/UnityDumpUI/Platform/WindowsProxyInstaller.cs`):**
    - Scoped `InstallSelectedAsync` and `UninstallSelectedAsync` to `FilteredGames.ToList()` so filter-hidden rows selected earlier are never modified silently (while surfacing `(+N hidden)` in `SummaryText`), and implemented `IDisposable` on `ProxyDeployViewModel` (wired on `ProxyDeployWindow.Closed`) to cancel and release `_scanCts` and row handlers.
    - Rewrote `WindowsProxyInstaller.InstallSync` as a 3-phase atomic staged replacement (`*.discrete.tmp` staging → `.discrete.bak` backup + `File.Move(overwrite: true)` commit → delete backups and mutually-exclusive other Discrete proxy only after every required file succeeds, with full rollback on any lock/copy failure).
  - Added 4 C# regression tests in `ui/UnityDumpUI.Tests/ProxyInstallerAndDeployViewModelTests.cs` (`InstallSelected_AndUninstallSelected_OnlyActOnFilteredVisibleRows`, `Install_RollsBackAtomically_WhenTargetFileIsLocked_LeavingOtherProxyUntouched`, `ProxyDeployViewModel_Dispose_CancelsInFlightScanCleanly`, `WindowsDllInjector_TryReadPeMachine_AndValidateDllMachineForTarget`).
  - Marked `A-L8`, `A-L9`, and `A-L17` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1587) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, `inject-x86.exe`, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1262 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 964 passed (x64) / 964 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1586) — Fix A-L2 + A-L4 + A-L10 + A-L11 + A-L20 (keyed idempotent disconnect hooks & doc sync, zero-copy `WriteLine`, atomic proxy thunks & middleware `winmm` exports, atomic `FILE_APPEND_DATA` Logger, heavy-RPC-gated disconnect GC drain)

- **Bug fixes (`A-L2`, `A-L4`, `A-L10`, `A-L11`, `A-L20`):**
  - **`A-L2` (`dll/src/shared/{PipeServer.{h,cpp},Logger.h,ForegroundHook.h}`, `dll/CMakeLists.txt`, `dll/src/proxy/loader.cpp`, `dll/src/shared/ExportAPI.cpp`):**
    - Made `PipeServer::RegisterDisconnectHook(std::string key, DisconnectHook hook)` genuinely idempotent by key (updating an existing key in place rather than appending duplicates) and updated callers (`"gc-drain"`, `"session-cleanup"`).
    - Synced stale comments across `PipeServer.h` (multi-client `kMaxPipeInstances = 4` + bounded `Stop()` drain), `PipeServer.cpp` (bounded worker drain), `dll/CMakeLists.txt` (x86 proxy builds), `Logger.h` (DllMain fallback on `CreateThread` failure), `ForegroundHook.h` (`UnsubclassAllGameWindows` on `Shutdown()`), and `loader.cpp`.
  - **`A-L4` (`dll/src/shared/PipeServer.cpp`):**
    - Changed `WriteLine` to take `std::string` by value (`std::move(serialized)`), append `'\n'` in place without duplicating large response buffers (halving 32-bit peak memory on large snapshots/scans), and write in 16 MiB overlapped chunks.
  - **`A-L10` (`dll/src/proxy/{version/version.cpp,winmm/winmm.cpp,winmm/winmm.def}`):**
    - Replaced non-atomic `static fn_t fn` in `PROXY_THUNK` with constant-initialized `static std::atomic<fn_t> fn{nullptr}` (acquire/release load/store), eliminating formal UB while preserving pre-CRT zero-init safety (verified via `tools/verify/proxy_precrt_gate.py --all`).
    - Added 48 common middleware `winmm.dll` forwarding thunks + `.def` exports (`waveOutPause/Restart/GetVolume/SetVolume/GetErrorTextA/GetID/Message`, `waveInStop/GetPosition/GetErrorTextA/W/Message`, `midiOutGetNumDevs/GetDevCapsA/W`, `midiIn*`, `midiStream*`, `joyGetPos/GetDevCapsA`, `mixer*`, `mmioOpenA/Read/Write/Seek/Descend/Ascend`) with arities verified against Windows SDK `10.0.26100.0` headers.
  - **`A-L11` (`dll/src/shared/Logger.cpp`):**
    - Replaced `std::ofstream` appends with `CreateFileW(FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, ...)` + single-buffer `WriteFile` so cross-module writes (proxy + backend) land atomically at EOF and never block concurrent rotation with sharing violations.
    - Switched `LogBaseDirectory()` from throwing `fs::temp_directory_path()` to non-throwing `fs::temp_directory_path(ec)` + `GetTempPathW` fallback, and wrapped `Init()` / `Write()` in `try/catch` guards.
  - **`A-L20` (`dll/src/shared/{PipeServer.{h,cpp},ExportAPI.cpp}`):**
    - Added `PipeServer::MarkHeavyCommands(...)` and per-worker `CurrentClientRanHeavyRpc()` tracking (`findFieldReferences`, `searchSymbols`, `snapshotBegin`, `dumpClassInfo`, `dumpIl2cppSymbols`, `beginValueScan`, `beginGroupScan`, `scanFieldValue`, `findInstances`, `listFieldValuesAcrossInstances`, `diagnoseClassReferences`, `dumpLazyInitSlots`), gating both Mono and IL2CPP disconnect GC-drain hooks so lightweight short-lived CE bridge bursts incur zero full-GC stall on disconnect.
  - Added C++ unit test `Test_AL11_LoggerAtomicAppendAndConcurrentRename` in `dll/tests/dll_core_test.cpp` (+8 checks on x64 and x86).
  - Marked `A-L2`, `A-L4`, `A-L10`, `A-L11`, and `A-L20` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1586) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; `proxy_precrt_gate.py --all` PASS; C# xUnit v3 tests 1258 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 964 passed (x64) / 964 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1584) — Fix B-L9 + B-L10 + B-L11 + B-L12 (completes 100% of Block B: ambiguity & open-generic guards before JIT, collision-free `MethodSignatureFormat` suffixes, near-zero backward-scan clamping, `Outer/Inner` `declaringClass` & doc sync)

- **Bug fixes (`B-L9`, `B-L10`, `B-L11`, `B-L12` — completes 100% of Block B Critical/High/Medium/Low findings):**
  - **`B-L9` (`dll/src/mono/{MonoResolver.{h,cpp},MonoClassWalker.{h,cpp},MonoMethodFinder.cpp,MonoMethodInvoker.cpp}`, `dll/src/il2cpp/IL2CPPClassWalker.{h,cpp}`, `dll/src/shared/ExportAPI.cpp`, `docs/pipe-protocol.md`):**
    - Opt-bound `mono_class_is_generic` and `mono_method_is_generic` in `MonoResolver` (with arity backtick fallback when unbound) and guarded `MonoMethodFinder::FindMethodInstances`, `MonoMethodInvoker::ResolveOverload`, and `MonoClassWalker::CompileMethodByName` so open-generic classes/methods never invoke `mono_compile_method`.
    - Updated `Mono::CompileMethodByName` and `IL2CPP::GetMethodPointerByName` to scan all matching overloads first, record `ambiguous` / `matchCount`, and bail out before JIT / pointer resolution on ambiguity; surfaced `"ambiguous"` and `"match_count"` in `monoCompileMethod` and updated `docs/pipe-protocol.md`.
  - **`B-L10` (`dll/src/shared/MethodSignatureFormat.h`):**
    - Rewrote `ShortenManagedTypeName` to peel trailing array rank specifiers (`[]`, `[,]`, jagged `[][]`) BEFORE checking the primitive table (`System.Int32[]` → `intArr`), parse and recursively shorten top-level generic arguments in both `<...>` and Mono `[...]` forms (`List`1<System.Int32>` → `List_int` vs `List_string`, `Dictionary`2<K, V>` → `Dictionary_K_V`), and slice the innermost segment after `/` or `.` at bracket depth 0 BEFORE stripping arity backticks (`Ns.Outer`1/Inner` → `Inner`).
  - **`B-L11` (`dll/src/shared/AddressBounds.h`, `dll/src/mono/MonoAddressFinder.cpp`, `dll/src/il2cpp/IL2CPPAddressFinder.cpp`):**
    - Added `AddressBounds::ComputeBackwardScanRange` and migrated `Mono::WhatIsAt`, `IL2CPP::WhatIsAt`, and `IL2CPP::WhatIsAtAcceptingArrays` onto it so near-zero addresses clamp `maxSteps` cleanly with `scannedFrom == rangeStart == start - (maxSteps - 1) * stride` and exact `bufIdx == p - rangeStart` alignment.
  - **`B-L12` (`dll/src/mono/MonoMethodFinder.cpp`, `dll/src/il2cpp/IL2CPPMethodFinder.cpp`, `dll/src/il2cpp/IL2CPPFieldXref.h`):**
    - Walked `class_get_nesting_type` (Mono) / `class_get_declaring` (IL2CPP) in `FindMethodInstances` to emit `Outer/Inner` `declaringClass` and outermost `declaringNamespace` so nested types round-trip back into `FindClass`.
    - Updated `IL2CPPFieldXref.h` header documentation to accurately describe v1 static reads/writes (`0x89`), v2 chain, v3 lazy-init slot, and instance-field heuristic scanning.
  - Added C++ unit test suite `Test_BL10_BL11_MethodSignatureAndScanRange` in `dll/tests/dll_core_test.cpp` (+89 checks on x64 and x86).
  - Marked `B-L9`, `B-L10`, `B-L11`, and `B-L12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1584) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1258 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 956 passed (x64) / 956 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1583) — Fix B-L2 + B-L3 + B-L8 (rank/enum guard on array detection, dead-code & unsorted `vtableSet` cleanup, `mono_domain_get` optBind, SEH & `ResolveStaticBaseAddress` attach-guard parity)

- **Bug fixes (`B-L2`, `B-L3`, `B-L8`):**
  - **`B-L2` (`dll/src/mono/{MonoResolver.{h,cpp},MonoAddressFinder.cpp}`, `dll/src/il2cpp/{IL2CPPResolver.{h,cpp},IL2CPPAddressFinder.cpp}`):**
    - Bound optional `mono_class_get_rank` / `il2cpp_class_get_rank` in both resolvers and added `IsMonoArrayClass` / `IsIl2CppArrayClass` helpers requiring `class_get_rank > 0` (or `!isEnum` fallback when `class_get_rank` is unbound) before trusting `element_class != k`, eliminating boxed-enum-as-array misclassification in both `ResolveInstanceLookup` and `Get*ClassExtentInfo`.
  - **`B-L3` (`dll/src/il2cpp/{IL2CPPLazyInitSlotScanner.{h,cpp},IL2CPPFieldXref.cpp}`, `dll/src/shared/ExportAPI.cpp`, `dll/src/mono/{MonoAddressFinder.cpp,MonoResolver.cpp}`):**
    - Removed dead `g_mapOnce` / `g_typeDefMap` / `BuildTypeDefMap()` and the redundant `ResolveTypeInfoSlotClass` fallback (which only re-read slots v3.0 had just checked) from `IL2CPPLazyInitSlotScanner`, `IL2CPPFieldXref`, and `ExportAPI::dumpLazyInitSlots`.
    - Unified `g_minVtable = 0` initialization in `MonoAddressFinder::BuildClassRangeCache` (matching `InvalidateClassRangeCache`) and replaced the unsorted `std::binary_search` on `cache().vtableSet` inside `FindAllInstances` with O(1) deduplication via `cache().vtableToKlass.emplace(vt, kl).second` followed by a single sort.
    - Demoted unused `mono_domain_get` from `tryBind` to `optBind` in `MonoResolver.cpp`.
  - **`B-L8` (`dll/src/il2cpp/IL2CPPClassWalker.cpp`, `dll/src/mono/{MonoClassWalker.cpp,MonoFieldXref.cpp}`):**
    - Added reentrant `ThreadAttachGuard` to `IL2CPP::ResolveStaticBaseAddress` (matching `MonoClassWalker::ResolveStaticBaseAddress`).
    - Wrapped remaining bare resolver accessor calls across `IL2CPPClassWalker.cpp` (`FindNestedByName`, `ReadClassInfo`, `ResolveStaticFieldAddress`, `GetMethodPointerByName`) and `MonoClassWalker.cpp` (`DescribeClass`, `FindNestedByName`, `ReadClassInfo`, `ResolveStaticFieldAddress`, `CompileMethodByName`, `EnumerateLiteralFields`) in SEH leaf helpers, and routed `MonoFieldXref.cpp`'s `r.type_get_name` through SEH-safe `TypeName()`.
  - Marked `B-L2`, `B-L3`, and `B-L8` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1583) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1258 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 867 passed (x64) / 867 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1581) — Fix B-L1 + B-L4 + B-L5 (race-free throttled-retry resolvers & discoveries, `IsKnownNotArray` cap enforcement & heap-leaked memos, 4 KiB page-fallback chunk scanning)

- **Bug fixes (`B-L1`, `B-L4`, `B-L5`):**
  - **`B-L1` (`dll/src/shared/ThrottledRetry.h`, `dll/src/mono/MonoResolver.cpp`, `dll/src/il2cpp/{IL2CPPResolver,IL2CPPStaticFieldsOffset,IL2CPPMetadataRegistration,IL2CPPLazyInitSlotScanner,IL2CPPAddressFinder}.cpp`):**
    - Added shared `RetryPolicy::CanRetryNow` / `NonZeroTick` (`1000 ms` failure retry throttle) and unified both resolvers (`MonoResolver`, `IL2CPPResolver`) and all four IL2CPP lazy discoveries (`IL2CPPStaticFieldsOffset`, `IL2CPPAddressFinder::GetElementClassOffset`, `IL2CPPMetadataRegistration`, `IL2CPPLazyInitSlotScanner`) on the same "latch permanently on success via atomic acquire/release publication, throttled retry on failure" contract.
    - Eliminated the `MonoResolver` data race (where failed retries mutated `g_resolver.moduleName` and slots in place while concurrent threads read them) by resolving into a local `Resolver`, returning an immutable `g_unresolved` fallback on failure, and publishing an immutable heap-leaked singleton on success.
  - **`B-L4` (`dll/src/il2cpp/IL2CPPAddressFinder.cpp`):**
    - Moved `arrayKlassSet` and `notArrayKlass` into a heap-leaked `ArrayMemoState` singleton (avoiding process-exit teardown over up to 524K `unordered_set` nodes) and added `std::atomic<bool> notArrayFull` enforced in `LooksLikeValueTypeElementArray` so hitting `kNotArrayCap` genuinely stops probing unknown values (and resets cleanly in `ResetArrayKlassMemos`).
  - **`B-L5` (`dll/src/shared/SafeChunkRead.h`, `dll/src/il2cpp/{IL2CPPClassPointerScanner,IL2CPPLazyInitSlotScanner,IL2CPPClassReferenceDiagnostic}.cpp`):**
    - Extended `SafePeScan::ScanReadableSpans` with optional `std::size_t* outFailedPages` tracking and migrated `IL2CPPClassPointerScanner`, `IL2CPPLazyInitSlotScanner`, and `IL2CPPClassReferenceDiagnostic::ScanRangeForPointer` off `pos += 4096` whole-chunk-retry loops so a single `PAGE_NOACCESS` / `PAGE_GUARD` page inside a 1 MiB chunk falls back to 4 KiB page reads without skipping up to ~1 MiB of readable pages ahead of it.
  - Added C++ unit test suite `Test_BL1_BL5_RetryThrottleAndPageFallback` in `dll/tests/dll_core_test.cpp` (+14 checks on x64 and x86, including a live `VirtualAlloc` + `PAGE_NOACCESS` middle-page test).
  - Marked `B-L1`, `B-L4`, and `B-L5` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1581) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1258 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 867 passed (x64) / 867 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1580) — Fix C-L1 + C-L2 (completes 100% of Block C: eliminate literal command names & dead `MagicStrings`, add UI `PipeCommands`, document all 17 missing RPCs in `pipe-protocol.md`)

- **Bug fixes (`C-L1`, `C-L2` — completes 100% of Block C Critical/High/Medium/Low findings):**
  - **`C-L1` (`dll/src/shared/MagicStrings.h`, `dll/src/shared/ExportAPI.cpp`, `ui/UnityDumpUI.Core/PipeCommands.cs`, `ui/UnityDumpUI.Core/Services/BackendAdapter.cs`, `docs/Archiutecture.md`):**
    - Added `CmdPing`, `CmdLookupSymbol`, `CmdListSymbols`, `CmdGetMetadataStats`, `CmdIL2CPPGetMetadataTypeFields`, and `CmdIL2CPPGetMetadataRegistration` to `MagicStrings.h` and migrated all 7 literal `PipeServer::RegisterHandler("...")` calls in `ExportAPI.cpp` (0 literal registrations remaining).
    - Removed the 4 unimplemented dead constants (`CmdExportCeXml`, `CmdExportCsx`, `CmdExportSdkHeader`, `CmdIL2CPPGetGenericInstances`) from `MagicStrings.h` and updated `docs/Archiutecture.md` to reflect actual commands + client-side exporters.
    - Added `UnityDumpUI.Core.PipeCommands` (53 constants mirroring `MagicStrings.h` 1-to-1) and migrated `BackendAdapter.BulkCommands` plus all 41 RPC dispatch sites in `BackendAdapter.cs` off raw string literals.
  - **`C-L2` (`docs/pipe-protocol.md`, `dll/src/shared/MagicStrings.h`, `ui/UnityDumpUI.Core/Services/BackendAdapter.cs`):**
    - Documented all 17 previously missing registered commands in `docs/pipe-protocol.md` (`scanFieldValue`, `beginValueScan`, `refineValueScan`, `endValueScan`, `cancelScan`, `beginGroupScan`, `refineGroupScan`, `queryGroupCandidates`, `endGroupScan`, `getDispatcherStats`, `dumpClassInfo`, `setKeepForeground`, `getKeepForeground`, `diagnoseClassReferences`, `dumpLazyInitSlots`, `il2cppGetMetadataRegistration`, `getMetadataStats`), fixed the broken dispatcher error table, and synced concurrency / strict address / `handshake.commands` / snapshot cap & OOM threshold specs.
    - Fixed stale comments & XML docs in `MagicStrings.h` (`scanFieldValue` class-scoped `namespace`/`name`, `callMethod` full Mono+IL2CPP scope, per-client disconnect cleanup) and `BackendAdapter.cs` (`WalkInstanceAsync` strict hex address note, `SnapshotBeginAsync` 8192 / 4096 hard caps).
  - Added C# unit test `PipeCommands_HasNoDuplicateOrEmptyCommandNames` in `ui/UnityDumpUI.Tests/PipeClientReliabilityTests.cs`.
  - Marked `C-L1` and `C-L2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1580) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1258 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 853 passed (x64) / 853 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1579) — Fix C-L3 + C-L6 + C-L8 (twin-handler alignment, per-client session ownership/off-lock snapshot slicing, atomic RPC telemetry, handshake `commands` list, dual-pipe `cancelScan` reuse)

- **Bug fixes (`C-L3`, `C-L6`, `C-L8`):**
  - **`C-L3` (`dll/src/shared/ExportAPI.cpp`):**
    - Aligned Mono `findInstances` and `listFieldValuesAcrossInstances` with IL2CPP's `skipMapped = true` default (and surfaced `regions_skip_mapped` in Mono `findInstances` logs + `scan_stats`).
    - Wired `filterMatches` post-scan header validation into `listFieldValuesAcrossInstances` on both Mono and IL2CPP so instance lists and per-field value listings match.
    - Reported `filter.live_walk = false` + `filter.live_walk_unavailable = true` in IL2CPP `findInstances` when `liveOnly = true` is requested.
    - Moved `r.class_is_valuetype(k)` inside Mono `walkInstance`'s existing `ThreadAttachGuard` (eliminating an unattached Mono runtime call), and wrapped Mono `getMetadataStats` in a single outer `ThreadAttachGuard`.
  - **`C-L6` (`dll/src/shared/PipeServer.{h,cpp}`, `dll/src/shared/{SnapshotSession,ValueScanSession,GroupScanSession}.{h,cpp}`, `dll/src/shared/ExportAPI.cpp`):**
    - Assigned every accepted pipe worker a unique `workerClientId` bound to `thread_local t_currentClientId` (accessible via `PipeServer::CurrentClientId()`) for the full lifetime of its RPCs and disconnect hooks.
    - Enforced per-client `ownerClientId` across `Snapshot`, `ValueScan`, and `GroupScan` `Begin` / `View` / `RefineWith` / `ViewWith` / `End` (with opportunistic expiry on every operation) and replaced the racy `ActiveClientCount() > 1` + `DropAll()` disconnect hook with per-owner `DropForOwner(cid)` + `ExpireOldSessions()`.
    - Released Mono `snapshotBegin`'s `session->plan` (`clear()` + `shrink_to_fit()`) right after decode, moved `session->classes` into immutable `std::shared_ptr<const std::vector<ClassPayload>> sharedClasses`, and moved `snapshotChunk` JSON slice copying outside `SessionManager`'s global mutex.
  - **`C-L8` (`dll/src/shared/PipeServer.{h,cpp}`, `dll/src/shared/ExportAPI.cpp`, `ui/UnityDumpUI.Core/Services/{PipeClient,BackendAdapter,ProtocolModels}.cs`):**
    - Added `PipeClient.SendWithTelemetryAsync<TResult>` returning `(RpcEnvelope<TResult>, RpcTelemetry)` captured atomically inside `_gate`, and switched `BackendAdapter.SnapshotChunkAsync` to consume it instead of reading mutable `LastReadMs` / `LastParseMs` / `LastResponseChars` properties off-gate.
    - Updated `BackendAdapter.CancelScanAsync` to prefer the already-open interactive `_pipe` in dual-pipe mode (falling back to a short-lived connection if single-pipe or busy) to avoid spawning a new pipe + triggering the DLL disconnect GC hook on every cancel.
    - Added `PipeServer::RegisteredCommands()`, emitted `"commands"` in `BuildHandshakeJson`, and exposed `HandshakeResult.Commands` + `SupportsCommand(string)` on the UI side.
    - Skipped negative `relOff < 0` struct member offsets in `CaptureMonoStructFields` and `CaptureIl2cppStructFields` so torn class metadata cannot emit negative numbers into `SnapshotFieldEntry.Offset` (`ulong`).
  - Added C++ unit test suite `Test_CL6_SessionOwnershipAndSharedClasses` in `dll/tests/dll_core_test.cpp` (linked `ValueScanSession.cpp` + `GroupScanSession.cpp`, +26 checks on x64 and x86) and C# unit tests `SendWithTelemetryAsync_ReturnsCallScopedTelemetryInsideGate` & `HandshakeResult_DeserializesCommandsAndSupportsCommand` in `ui/UnityDumpUI.Tests/PipeClientReliabilityTests.cs`.
  - Marked `C-L3`, `C-L6`, and `C-L8` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1579) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1257 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 853 passed (x64) / 853 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1578) — Fix C-L4 + C-L5 + C-L7 (strict `ParseAddress`, RPC parameter bounds/UTF-16 char/null args hardening, dynamic 32-bit OOM threshold & `ComputeGameBuildId` module fallback)

- **Bug fixes (`C-L4`, `C-L5`, `C-L7`):**
  - **`C-L4` (`dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`):**
    - Replaced `_strtoui64`-based `parseAddr` with `ExportHelpers::ParseAddressStrict` / `ParseAddress`: requires full hex string consumption via `std::from_chars(..., 16)` (rejecting trailing garbage, empty/sign/whitespace strings, and 64-bit overflow), rejects negative signed JSON integers and floats, and rejects values exceeding `UINTPTR_MAX` (preventing silent x86 truncation).
  - **`C-L5` (`dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`, `dll/src/shared/PipeServer.cpp`):**
    - Normalized omitted, `null`, or non-object `"args"` to `json::object()` in `PipeServer::Dispatch` so handlers never throw `nlohmann::json::type_error 306` (`-32000`).
    - Added `ClampRpcLimit` so `refineValueScan` / `refineGroupScan` / `queryGroupCandidates` / `beginGroupScan` clamp `limit > 50000` to `50000` (instead of resetting to default), `walkInstance` clamps `dictLimit` to `4096`, `snapshotBegin` clamps `structMemberCap` to `256`, and `walkArray` clamps `elementSize` to `4096`.
    - Rejected negative `offset` in `queryGroupCandidates` with overflow-safe `[offset, end)` bounds; rejected negative values in `setInstanceField`'s `asU64` and `snapshotBegin`'s `memHeadroomMB`; removed unreachable integer branches in `setInstanceField` float/double; decoded `char` / `System.Char` via UTF-8 -> first UTF-16 code unit (`DecodeFirstUtf16CodeUnit`) in both `setInstanceField` and `callMethod` (Mono + IL2CPP).
  - **`C-L7` (`dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`, `dll/src/shared/SnapshotSession.{h,cpp}`):**
    - Replaced fixed 3 GB `kX86PrivateUsageRefuseThreshold` with `QueryX86PrivateUsageRefuseThreshold()` (`75%` of `GlobalMemoryStatusEx().ullTotalVirtual`, protecting both 2 GB non-LAA and 4 GB WOW64 LAA 32-bit games).
    - Changed `ComputeGameBuildId(moduleName)` to return empty when an explicit `moduleName` is not loaded (host EXE is hashed only when `moduleName == nullptr`), unifying handshake and `snapshotBegin` on `CurrentGameBuildId()` so legacy `mono.dll` fallback works on Unity < 5.4.
  - Added C++ unit test suite `Test_CL4_CL5_CL7_ValidationAndBuildId` in `dll/tests/dll_core_test.cpp` (+38 checks on x64 and x86).
  - Marked `C-L4`, `C-L5`, and `C-L7` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1578) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1255 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 827 passed (x64) / 827 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1577) — Fix D-L3 + D-L5 + D-L6 + D-L9 (completes 100% of Block D: Rust fallible allocation & stack panic log, `scan_rip` chunk-boundary dedup, Rust cooperative scan cancellation, `release.yml` SHA-pinned split build/release jobs)

- **Bug fixes (`D-L3`, `D-L5`, `D-L6`, `D-L9` — completes 100% of Block D Critical/High/Medium/Low findings):**
  - **`D-L3` (`dll/rust/src/{ffi,chain/mod,xref/mod,metadata/parser,metadata/ffi}.rs`, `dll/rust/include/discrete_core.h`):**
    - Replaced heap-allocating `format!` in `ffi::guard` and the Rust panic hook with a zero-alloc fixed-capacity UTF-8-safe `StackBuf<512>`.
    - Added fallible `try_push` / `try_insert` helpers and converted result vectors / sets in `chain::detect_cancellable`, `xref::scan_rip_cancellable`, `xref::scan_inst_cancellable`, `metadata::Parser` enumerators & lazy maps, and `dc_md_try_decode_string_default` to use fallible reservations so OOM truncates gracefully instead of aborting the host game process; exposed `dc_*_result_truncated()` accessors.
  - **`D-L5` (`dll/rust/src/xref/mod.rs`, `tools/verify/r2_golden.json`):**
    - Carried the last owned byte `(end_va, last_byte)` across contiguous spans in `scan_rip_cancellable` so `p == 0` at a chunk boundary still sees a trailing REX prefix at `CHUNK_SIZE - 1` and suppresses the duplicate 6-byte hit; rebaselined `rip-synthetic` in `tools/verify/r2_golden.json` via `r2_golden.py --record --rebaseline --no-images` (`1970913 -> 1970879` bytes, removing the 2 boundary duplicates).
  - **`D-L6` (`dll/rust/src/{ffi,chain/mod,xref/mod}.rs`, `dll/rust/include/discrete_core.h`, `dll/src/il2cpp/{IL2CPPChainDetector.h,IL2CPPChainDetectorRs.cpp,IL2CPPFieldXrefScan.h,IL2CPPFieldXrefScanRs.cpp,IL2CPPFieldXref.cpp}`):**
    - Added optional `dc_cancel_fn` cooperative cancellation callback polled at every chunk start, page-fallback step, and instance-scan method; exported `dc_chain_detect_cancellable`, `dc_xref_rip_scan_cancellable`, `dc_xref_inst_scan_cancellable`, and `dc_*_result_cancelled()` accessors.
    - Wired `Discrete::ScanCancel::IsCancelRequested()` into all three Rust scan kernels through C++ wrappers and surfaced `bool* cancelled` to `IL2CPPFieldXref.cpp`.
  - **`D-L9` (`.github/workflows/release.yml`):**
    - Pinned all GitHub Actions (`actions/checkout`, `actions/setup-dotnet`, `actions/upload-artifact`, `actions/download-artifact`) to full commit SHAs.
    - Split the workflow into `build` (`permissions: contents: read`) and `release` (`needs: build`, `permissions: contents: write`, verifies artifact `.sha256` before creating/updating the draft release), and guarded `rustup self update` behind a `< 1.28.0` version check.
  - Added Rust unit tests (`stack_buf_truncates_on_utf8_boundary_without_alloc`, `rip_rex_at_chunk_boundary_is_not_duplicated_in_next_chunk`, `rip_and_inst_support_cooperative_cancellation`, and chain cancellation FFI test).
  - Marked `D-L3`, `D-L5`, `D-L6`, and `D-L9` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1577) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1255 passed (0 failed, 0 skipped, locked mode); Rust core dev 118 (x64) / 119 (x86) + release `+crt-static` 118 (x64) / 119 (x86) passed; C++ `discrete_dll_test` 789 passed (x64) / 789 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1575) — Fix D-L2 + D-L4 + D-L12 (metadata version warnings & `typeTablesSupported`, `Parser::Get()` retry + Windows read-only file mapping, lazy `OnceLock` metadata lookup indices)

- **Bug fixes (`D-L2`, `D-L4`, `D-L12`):**
  - **`D-L2` (`dll/rust/src/metadata/{parser,ffi}.rs`, `dll/rust/include/discrete_core.h`, `dll/src/il2cpp/{MetadataParser.h,MetadataParserRs.cpp}`, `dll/src/shared/ExportAPI.cpp`, `ui/UnityDumpUI.Core/Services/ProtocolModels.cs`, `CLAUDE.md`):**
    - Split the non-verified metadata soft warning in `Parser::load_header()` into an explicit strings-only warning for versions with no `TypeDefinition` layout (v16–23/25/26/28/30) vs an unverified v31-layout warning for v32–34.
    - Removed dead v30 match arms in `method_def_sizeof` and `get_method_definition`.
    - Added `Parser::type_tables_supported()` / `dc_md_type_tables_supported()` / `Parser::typeTablesSupported()`, surfaced `typeTablesSupported` in `getMetadataStats` (both IL2CPP and Mono) and `MetadataStatsResult.TypeTablesSupported`, and clarified `CLAUDE.md` to distinguish strings acceptance (v16–v39) from type-table support (v24/27/29/31–39).
  - **`D-L4` (`dll/src/il2cpp/MetadataParserHostPath.cpp`, `dll/rust/src/metadata/parser.rs`):**
    - Replaced the one-shot magic static in `Parser::Get()` with a mutex-guarded retry loader publishing an immutable `Parser*` atomically once loaded (lock-free steady state, zero data race, retries cleanly if an earlier load failed before `BackendDetector` resolved `gameDataDir` or while the file was locked).
    - Replaced whole-file heap copying in `Parser::load_from_path` on Windows with a read-only `CreateFileMappingW` + `MapViewOfFile` (`PAGE_READONLY` / `FILE_MAP_READ`) backing (`MetadataBacking::Mapped`) with automatic fallback to `ReadFile` and clean `UnmapViewOfFile` on drop.
  - **`D-L12` (`dll/rust/src/metadata/parser.rs`):**
    - Added lazy `OnceLock` indices for `find_type_by_name` (FNV-1a `(ns, 0xFF, name)` -> ascending TypeDef indices), `get_image` / `find_image_containing_method` (cached per-image `(method_start, method_end)` ranges), and `find_field_default` (`global_field_index` -> first matching row index), preserving exact linear-scan first-match semantics while eliminating O(N) / O(types) rescans per lookup.
  - Added Rust unit tests (`version_type_tables_supported_and_split_warnings`, `load_from_path_uses_readonly_file_mapping_and_releases_on_drop`, `lazy_indices_preserve_first_match_semantics`) and C# DTO deserialization test (`MetadataStatsResult_DeserializesTypeTablesSupportedAndDegradedFlags`).
  - Marked `D-L2`, `D-L4`, and `D-L12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1575) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1255 passed (0 failed, 0 skipped, locked mode); Rust core dev 115 (x64) / 116 (x86) + release `+crt-static` 115 (x64) / 116 (x86) passed; C++ `discrete_dll_test` 789 passed (x64) / 789 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1574) — Fix D-L1 + D-L7 + D-L8 + D-L10 + D-L11 (Rust MSRV 1.88, build-only `build_number.txt` bump, stale comments/vars cleanup, golden `--rebaseline` guard, `CodeGenModulesFinder` u32 count + loop/slots fix)

- **Bug fixes (`D-L1`, `D-L7`, `D-L8`, `D-L10`, `D-L11`):**
  - **`D-L1` (`dll/rust/Cargo.toml`):** Updated `rust-version` from `"1.85"` to `"1.88"` to accurately match let-chains in `chain/decode.rs`.
  - **`D-L7` (`build.ps1`):** Restricted `Bump-BuildNumber` to actual binary-producing targets (`DLL`, `ProxyDLL`, `MetadataCli`, `UI`, `All`) so `Test`, `CppTest`, `Deploy`, and `DeployScripts` leave `build_number.txt` aligned with the binaries in `dist\`.
  - **`D-L8` (`build.ps1`, `CMakeLists.txt`, `tools/metadata-cli/CMakeLists.txt`, `dll/rust/src/metadata/layout.rs`):** Removed unused `$SLN` pointing to non-existent `ui\Discrete.sln`, updated Zydis-era include comments to reflect current MinHook-only `dll/vendor/`, and clarified that `layout.rs`'s `Il2CppDumperV35plus` line citations correspond to commit `bbd9c0c2`.
  - **`D-L10` (`tools/verify/metadata_golden.py`, `tools/verify/r2_golden.py`):** Changed `--record` to add missing keys only and refuse overwriting changed existing golden entries unless `--rebaseline` is also passed (printing a per-check diff summary on rebaseline; rejecting `--rebaseline` without `--record`).
  - **`D-L11` (`tools/metadata-cli/CodeGenModulesFinder.{h,cpp}`, `tools/metadata-cli/main.cpp`):** Read `Il2CppCodeGenModule::methodPointerCount` as 4-byte `uint32_t` (ignoring x64 alignment padding), guarded `methodPointersRva` against underflow when `methodPtrsVa == 0`, replaced the fragile `for` loop with an explicit `while` advance (`off = cursor`), and exposed `Found::arraySlots` (`array_slots` in `CmdCodeModules`) including intermediate null slots.
  - Marked `D-L1`, `D-L7`, `D-L8`, `D-L10`, and `D-L11` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1574) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1254 passed (0 failed, 0 skipped, locked mode); Rust core dev 112/113 + release `+crt-static` 112/113 passed; C++ `discrete_dll_test` 789 passed (x64) / 789 passed (x86); `r2_golden.py --no-images` 7/7 passed; verified `--rebaseline` guard on both golden scripts.

-----

## 2026-11-27 (build 1573) — Fix E-L5 + E-L7 + E-L10 + E-L11 (completes 100% of Block E: CE math-struct expansion, Lua UTF-16 surrogate & JSON hardening, handshake capability flag, dead-code & doc cleanup + tests)

- **Bug fixes (`E-L5`, `E-L7`, `E-L10`, `E-L11` — completes 100% of Block E Critical/High/Medium/Low findings):**
  - **`E-L5` (`UnityTypeMap.cs`, `CeXmlExporter.cs`, `CsxExporter.cs`):**
    - Replaced non-standard CE keywords (`12 Bytes` / `16 Bytes` / `24 Bytes`) in `UnityTypeMap.Resolve` with `Array of byte` (with `<ByteLength>` emission in `CeXmlExporter.EmitLeaf`) and added `UnityTypeMap.TryGetKnownMathComponents` covering `Vector2`/`Vector3`/`Vector4`/`Quaternion`/`Color`/`Color32`/`Rect`/`Bounds`.
    - Expanded known Unity math structs into typed `Float` / `Byte` sub-fields across `CeXmlExporter` (fields & arrays) and `CsxExporter` (`Render`, `RenderAsync`, and arrays).
  - **`E-L7` (`scripts/unity_symbols.lua`, `scripts/unity_invoke.lua`):**
    - Upgraded `unity_symbols.lua` with full JSON string escaping (`escapeStr` covering `\`, `"`, `\b`/`\f`/`\n`/`\r`/`\t`, control chars `< 0x20`), full JSON unescaping including UTF-16 surrogate pairs (`\uD800-\uDBFF` + `\uDC00-\uDFFF` -> 4-byte UTF-8), strict malformed-token errors, `resp == nil` / `pcall(parseValue)` guards, and `null` encoding for non-finite numbers.
    - Upgraded `unity_invoke.lua` to decode UTF-16 surrogate pairs into valid 4-byte UTF-8 (fixing CESU-8 output), decode `\b`/`\f`, and serialize `NaN`/`Infinity`/`-Infinity` as JSON `null`.
  - **`E-L10` (`dll/src/shared/ExportAPI.cpp`, `ProtocolModels.cs`, `MainWindowViewModel.cs`, `MainWindow.axaml.cs`, `SnapshotViewModel.cs`):**
    - Added `stable_module_relative_methods` capability flag to DLL handshake (`HandshakeResult.StableModuleRelativeMethods`), wired `MainWindowViewModel.CanExportSymbols()` to `SupportsStableMethodSymbols` (with fallback for older DLLs), simplified `MainWindow.axaml.cs` `supportsSignatureMatch` to `vm.IsConnected`, and checked `gameassembly`/`userassembly`/`il2cpp` before `mono` in `SnapshotViewModel.GuessBackend()`.
  - **`E-L11` (`CeXmlExporter.cs`, `CeXmlExporter.Live.cs`, `MonoRegisterSymbolAaScriptGenerator.cs`, `AaLuaLoader.cs`, `UnityTypeMap.cs`, `SdkHeaderExporter.cs`, `docs/ui-spec.md`):**
    - Deduplicated `MonoRegisterSymbolAaScriptGenerator`'s CT Lua loader onto `AaLuaLoader.AppendSelfLoad` (with optional `missingHint`), rejected multi-parameter generics in `UnityTypeMap.ExtractElementClass`, fell back to a pointer leaf in `CeXmlExporter.Live.cs` when legacy DLL dictionary pairs omit `Index`, removed the duplicate obsolete `<summary>` in `CeXmlExporter.cs`, fixed `docs/Architecture.md` -> `docs/Archiutecture.md` comments, and updated `docs/ui-spec.md` line reference (`MainWindow.axaml:3804`).
  - **Tests:** Added 6 new xUnit tests across `CeXmlAndCsxTemplateExporterTests`, `CeXmlExporterLiveTests`, `BridgeLuaResourcesTests`, and `ValueScanWireAndDtoContractTests`, plus verified `unity_symbols.lua` and `unity_invoke.lua` codecs with a real `lua.EXE` behavioral test (`EL7_EL8_LUA_TESTS_PASSED`).
  - Marked `E-L5`, `E-L7`, `E-L10`, and `E-L11` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1573) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1254 passed (0 failed, 0 skipped, locked mode); Rust core dev 112/113 + release `+crt-static` 112/113 passed; C++ `discrete_dll_test` 789 passed (x64) / 789 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1571) — Fix E-L1 + E-L2 + E-L3 + E-L6 + E-L8 (AA error()/HAD_ERROR/literal hardening, XML 1.0 control-char escaping, unconditional Lua error reporting + tests)

- **Branch milestone:** Merged all High & Medium audit fixes from `dev` into `main` via PR #3 (`143154b`).
- **Bug fixes (`E-L1`, `E-L2`, `E-L3`, `E-L6`, `E-L8`):**
  - **`E-L1` (`ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs`):**
    - Added `AaLuaLoader.AppendHadErrorReset(sb)` to `[DISABLE] {$lua}` so a prior failed `[ENABLE]` does not keep CE's Lua Engine open on subsequent clean disables.
  - **`E-L2` (`AaLuaLoader.cs`, `AobAaScriptGenerator.cs`, `MonoRegisterSymbolAaScriptGenerator.cs`, `CallMethodAaScriptGenerator.cs`, `AddressLookupAaScriptGenerator.cs`):**
    - Replaced `print + return` with `error('[Discrete] ...')` (keeping `_G.DISCRETE_HAD_ERROR = true`) on AA precondition failures (missing CT Lua file, parse error, bridge not loaded) so Cheat Engine rejects enabling the entry instead of ticking its checkbox.
    - Removed the unnecessary Lua bridge check from `AddressLookupAaScriptGenerator` (pure `{$asm}` `alloc`/`registersymbol`/`dq` needs no bridge and never pops Lua Engine).
  - **`E-L3` (`ui/UnityDumpUI.Core/Services/Export/CallMethodAaScriptGenerator.cs`):**
    - Escaped embedded NUL in Lua single-quoted strings as `\000` (3 decimal digits) so adjacent digits cannot form `\012` etc.
    - Emitted `0/0`, `1/0`, `-1/0` for `NaN`/`Infinity`/`-Infinity` float/double arguments instead of bare undefined Lua globals.
    - Threw `ArgumentException` on unparseable boolean/integer/float/char argument strings (caught and surfaced by `MethodCallViewModel`) instead of silently substituting `0`.
  - **`E-L6` (`TemplateExportHelpers.cs`, `CeXmlExporter.cs`, `CsxExporter.cs`):**
    - Unified XML escaping in `TemplateExportHelpers.XmlEscape`, sanitizing XML 1.0 illegal control characters (`< 0x20` except `\t`/`\n`/`\r`), unpaired UTF-16 surrogates, and `0xFFFE`/`0xFFFF` as `\uXXXX` while preserving valid CJK and surrogate-pair emoji.
  - **`E-L8` (`scripts/unity_dump.lua`, `scripts/unity_symbols.lua`, `BootstrapAaScriptGenerator.cs`):**
    - Upgraded `discreteShutdown` failure logging from `U.warn` to unconditional `U.err`, checked `pcall(discreteShutdown)`'s return value in `BootstrapAaScriptGenerator` `[DISABLE]` (flagging `_G.DISCRETE_HAD_ERROR = true` on failure), and changed `registerMethodSymbol`'s ambiguous `sigFilter` branch to report via `err()` and return `false` instead of silently picking the first overload.
  - **Tests:** Added 19 new xUnit tests across `AobAaScriptGeneratorTests`, `AddressLookupAaScriptGeneratorTests`, `CallMethodAaScriptGeneratorTests`, `BootstrapAaScriptGeneratorTests`, `MonoRegisterSymbolAaScriptGeneratorTests`, `CeXmlAndCsxTemplateExporterTests`, and `BridgeLuaResourcesTests`; verified Lua syntax with `lua.EXE`.
  - Marked `E-L1`, `E-L2`, `E-L3`, `E-L6`, and `E-L8` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1571) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1248 passed (0 failed, 0 skipped, locked mode); Rust core dev 112/113 + release `+crt-static` 112/113 passed; C++ `discrete_dll_test` 789 passed (x64) / 789 passed (x86); `r2_golden.py --no-images` 7/7 passed.

-----

## 2026-11-27 (build 1570) — Fix D-M4 + D-M5 + D-M6 + D-M7 (Rust release `+crt-static` & golden-hash build gates, `release.yml` draft/published guard, `metadata-cli` token-RID method indexing, pinned NuGet/SDK + locked-mode restores)

- **Bug fixes (`D-M4`, `D-M5`, `D-M6`, `D-M7` — completes 100% of High & Medium audit findings across all 5 blocks A–E):**
  - **`D-M4` (`build.ps1`, `dll/rust/src/metadata/tests.rs`, `tools/verify/r2_golden.json`):**
    - Updated `Run-RustTests` in `build.ps1` to also run `cargo test --release --locked` with `CARGO_ENCODED_RUSTFLAGS=-Ctarget-feature=+crt-static` for both `x86_64-pc-windows-msvc` and `i686-pc-windows-msvc` in `Publish` mode (verifying unwinding and ABI under the exact shipping profile + static CRT).
    - Reordered `-Target All` to run `Build-MetadataCli` before `Run-Tests` and added `Run-GoldenTests`: runs `metadata_golden.py` + `r2_golden.py` when fixtures are configured (`exit 1` = build failure, `exit 2` = warning), and runs `r2_golden.py --no-images` synthetic checks even when no game fixtures are present (e.g. CI).
    - Updated `real_metadata_fixtures` (`dll/rust/src/metadata/tests.rs`) to emit `SKIPPED` to stderr when `DISCRETE_METADATA_FIXTURES` is unset/empty, and surfaced `Fixtures:` and `Golden:` status lines in `build.ps1`'s Build Summary.
    - Rebaselined `rip-synthetic` in `tools/verify/r2_golden.json` for the `D-M1` unreadable-page recovery fix (all 4 other synthetic suites unchanged; 7/7 synthetic runs pass).
  - **`D-M5` (`.github/workflows/release.yml`):**
    - Hardened the `Create / update GitHub Release` step to inspect `gh release view $tag --json isDraft,body` first: refuses to overwrite an already-published release (`isDraft == false`), preserves maintainer-rewritten notes on existing drafts (uploading assets only), and checks `$LASTEXITCODE` on every `gh` invocation.
  - **`D-M6` (`dll/src/shared/MethodTokenIndex.h`, `tools/metadata-cli/SymbolPipeline.{h,cpp}`, `tools/metadata-cli/main.cpp`, `dll/tests/dll_core_test.cpp`):**
    - Switched `SymbolPipeline` `methodPointers[]` lookup from `m.index - image.methodStart` to ECMA-335 token RID `(m.token & 0x00FFFFFF) - 1` (rejecting RID 0 and overflow-safe slot bounds), matching IL2CPP's runtime `GetTokenRowId(method.token) - 1`.
    - Added `PipelineResult::methodsIndexMismatch` comparing token-RID vs legacy offset formulas (printed to stderr in `CmdSymbols` when non-zero) and added `Test_DM6_MethodTokenPointerIndex` to `discrete_dll_test`.
  - **`D-M7` (`ui/Directory.Build.props`, `ui/global.json`, `ui/UnityDumpUI/UnityDumpUI.csproj`, `ui/UnityDumpUI.Core/UnityDumpUI.Core.csproj`, `ui/UnityDumpUI.Tests/UnityDumpUI.Tests.csproj`, `build.ps1`, `.github/workflows/release.yml`, `ui/*/packages*.lock.json`):**
    - Pinned `CommunityToolkit.Mvvm` (`8.4.2`), `xunit.v3` (`$(XunitV3Version)` = `3.2.2`), and `xunit.runner.visualstudio` (`3.1.5`).
    - Enabled `RestorePackagesWithLockFile=true` with `RestoreLockedMode=true` on CI (`CI=true`), using `packages.aot.lock.json` when `PublishAot=true` and declaring `RuntimeIdentifiers=win-x64` on `UnityDumpUI.Core` so AOT publish, single-file publish, and `dotnet test` all pass locked restore cleanly without `NU1004`.
    - Tightened `ui/global.json` to `10.0.404` with `rollForward: latestPatch`, updated `release.yml` `setup-dotnet` to `global-json-file: ui/global.json`, and made `build.ps1` run `dotnet --version` and `dotnet publish` from `ui/` so `ui/global.json` governs all .NET invocations.
  - Marked `D-M4`, `D-M5`, `D-M6`, and `D-M7` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `$env:CI='true'; .\build.cmd publish` (build #1570) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`, locked mode), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1229 passed (0 failed, 0 skipped, locked mode); Rust core dev tests 112 passed (x64) / 113 passed (i686) + release `+crt-static` tests 112 passed (x64) / 113 passed (i686); C++ `discrete_dll_test` 789 checks passed (x64) / 789 checks passed (x86); `r2_golden.py --no-images` 7/7 passed (5 matched golden, 0 failed).

-----

## 2026-11-27 (build 1568) — Fix D-M1 + D-M2 + D-M3 (page-by-page readable-span fallback for Rust chain/xref scans, compile-time x64/x86 ABI layout checks for all 14 FFI structs, v38/v39 per-table stride/count validation + `tablesDegraded` + tests)

- **Bug fixes (`D-M1`, `D-M2`, `D-M3`):**
  - **`D-M1` (`dll/rust/src/ffi.rs`, `dll/rust/src/chain/mod.rs`, `dll/rust/src/xref/mod.rs`):**
    - Added `Reader::scan_readable_spans` in `ffi.rs` and wired both `chain::detect` and `xref::scan_rip` through it: keeps the single-read 1 MiB + overlap fast path bit-for-bit identical on fully readable sections, and falls back to 4 KiB page-by-page coalesced readable spans when an anti-cheat stripped (`PAGE_NOACCESS` / `PAGE_GUARD`) page fails a chunk read — preserving all readable code before and after the bad page with chunk-boundary overlap and counting `failed_reads` per unreadable page instead of dropping up to 1 MiB of preceding code.
  - **`D-M2` (`dll/rust/src/lib.rs`, `dll/src/shared/RustCore.cpp`):**
    - Added compile-time `size_of` / `align_of` / `offset_of!` assertions on the Rust side (`lib.rs`) mirrored identically by C++ `static_assert(sizeof/alignof/offsetof)` in `RustCore.cpp` for all 14 `#[repr(C)]` FFI structs in `discrete_core.h` (`DcBytes`, `DcTypeDef`, `DcFieldDef`, `DcMethodDef`, `DcParameterDef`, `DcImageDef`, `DcFieldDefault`, `DcAobResult`, `DcChainMatch`, `DcChainStats`, `DcXrefSection`, `DcXrefRipHit`, `DcXrefRange`, `DcXrefInstHit`) under both 64-bit and 32-bit pointer widths.
  - **`D-M3` (`dll/rust/src/metadata/parser.rs`, `dll/rust/src/metadata/ffi.rs`, `dll/rust/include/discrete_core.h`, `dll/src/il2cpp/MetadataParser.h`, `dll/src/il2cpp/MetadataParserRs.cpp`, `dll/src/shared/ExportAPI.cpp`, `ui/UnityDumpUI.Core/Services/ProtocolModels.cs`):**
    - Added per-table v38/v39 validation (`section.size == section.count * stride` and `section.size % stride == 0`) after bounds checks for `typeDefinitions`, `fields`, `fieldDefaults`, `methods`, `parameters`, and `images`: any mismatched table is zeroed, logged with `WARN`, and sets `tables_degraded = true`.
    - Logged `WARN` and set `tables_degraded = true` when v38+ `type_index_size` inference from `parameters` fails (instead of silently falling back to 4), and when `validate_type_table` demotes a file to strings-only.
    - Exported `dc_md_tables_degraded` through `Parser::tablesDegraded()` to IL2CPP `getMetadataStats` (`tablesDegraded`) and `MetadataStatsResult.TablesDegraded`.
  - **Tests:**
    - Added `unreadable_middle_page_preserves_preceding_and_following_chains` (`chain/tests.rs`), updated `rip_unreadable_page_preserves_preceding_and_following_readable_pages` (`xref/tests.rs`), and added `v38_mismatched_method_and_image_table_strides_zero_only_corrupt_tables` (`metadata/tests.rs`).
  - Marked `D-M1`, `D-M2`, and `D-M3` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1568) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1229 passed (0 failed, 0 skipped); Rust core tests 112 passed (x64) / 113 passed (i686); C++ `discrete_dll_test` 778 checks passed (x64) / 778 checks passed (x86).

-----

## 2026-11-27 (build 1567) — Fix E-M4 (bounded-timeout CE Lua bridge pipe RPC via `connectToPipe` + `createThread`/`pump` + tests)

- **Bug fixes (`E-M4` — completes 100% of Block E High & Medium findings, 2/2 High + 7/7 Medium):**
  - **`E-M4` (`scripts/utils.lua`, `scripts/unity_symbols.lua`, `scripts/unity_invoke.lua`, `ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs`):**
    - Replaced unbounded blocking `io.open([[\\.\pipe\DiscreteBfx]], "r+b")` + `f:read("*l")` on CE's GUI thread with a bounded-timeout pipe transaction (`M.pipeRpcRaw` in `utils.lua`, mirrored standalone in `unity_symbols.lua` and `unity_invoke.lua`):
      - Prefers Cheat Engine's native `connectToPipe("DiscreteBfx", timeoutMs)` (`LuaPipeClient`, `FILE_FLAG_OVERLAPPED` + `CancelIo` + `OnTimeout`) inside `createThread`, while the calling GUI thread polls with `sleep(20); pump()` and enforces a wall-clock deadline that tears down the pipe handle once (`_closePipeOnce`) on expiry.
      - Falls back cleanly to `io.open` when running outside CE (or on old CE builds without `connectToPipe`), and rejects truncated EOF responses before `\n` (also preventing `unity_symbols.lua`'s `parseValue(nil)` crash).
    - Scaled `unity_invoke.lua`'s `callUnityMethod` RPC timeout dynamically when `opts.mainThread = true` (`math.max(10000, clamp(mainThreadTimeoutMs, 1, 60000) + 5000)`, plus optional `opts.rpcTimeoutMs`).
    - Switched `BootstrapAaScriptGenerator.cs`'s `[ENABLE]` smoke handshake to `pcall(U.pipeRpcRaw, ..., 5000)` so a stalled DLL cannot freeze CE on table enable.
  - **Tests:**
    - Added `PipeRpcScripts_UseBoundedTimeout_ConnectToPipe_AndWorkerThreadPump` (3 theory cases), `UnityInvoke_ScalesRpcTimeout_WithMainThreadTimeoutMs` to `ui/UnityDumpUI.Tests/BridgeLuaResourcesTests.cs`, and `Build_SmokeHandshake_UsesBoundedTimeoutPipeRpcRawUnderPcall` to `ui/UnityDumpUI.Tests/BootstrapAaScriptGeneratorTests.cs`.
    - Verified all 3 modified Lua files parse cleanly (`lua.EXE` exit 0) and pass 5/5 behavioral mock tests (normal CRLF read, `OnTimeout` single-destroy timeout, truncated EOF rejection, `callUnityMethod` `mainThreadTimeoutMs` scaling, and `unity_symbols.lua` timeout error).
  - Marked `E-M4` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1567) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1229 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 778 checks passed (x64) / 778 checks passed (x86).

-----

## 2026-11-27 (build 1566) — Fix C-M4 + C-M5 (snapshot array struct/enum element layout resolution, IL2CPP streaming instance header revalidation, idempotent cursor replay & lock-free SessionManager decode + tests)

- **Bug fixes (`C-M4`, `C-M5` — completes 100% of Block C High & Medium findings, 3/3 High + 8/8 Medium):**
  - **`C-M4` (`dll/src/shared/ExportHelpers.h`, `MonoClassWalker.{h,cpp}`, `IL2CPPClassWalker.{h,cpp}`, `ExportAPI.cpp`, `ProtocolModels.cs`, `SnapshotViewModel.cs`, `SnapshotValueLocatorEngine.cs`):**
    - Populated per-field 1D array element layout metadata (`arrayElemLayoutKnown`, `arrayElemIsValueType`, `arrayElemIsEnum`, `arrayElemSize`, `arrayElemUnderlyingTypeName`) once during `ReadClassInfo` on both Mono and IL2CPP via `class_get_element_class` (guarded `ec != fc`) + `class_is_valuetype` + `class_value_size` + enum underlying type detection.
    - Added `ResolveSnapshotArrayElementPlan` in `ExportHelpers.h` and wired it into `walkArrayElementsInline`: primitives and enums decode at their exact underlying width (`decodeType = underlyingTypeName`), named Unity math structs (`Vector3[]`, `Color[]`, etc.) fan out via `decodeField` at exact struct stride, custom value-type structs emit raw `hex` blobs at `class_value_size` stride (never `ptr` and never handed to Layer-B `elementFieldCapturer`), reference classes use pointer stride + optional Layer-B capture, and unresolved layouts return `arrayError: "unknown_element_layout"` instead of guessing pointer stride.
    - Added lazy `SnapshotArrayElement.Hex` (`ElementExtras`) in `ProtocolModels.cs` and rendered `hex=…` in `SnapshotViewModel.FormatArrayElement` / `SnapshotValueLocatorEngine.RenderElement`.
  - **`C-M5` (`dll/src/shared/SnapshotSession.h`, `dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`, `ProtocolModels.cs`):**
    - Revalidated every IL2CPP streaming instance's live object header word (`SafeReadBytesSeq(instAddr) == expectedKlass`) before decoding fields in `Il2cppStreamDecoder::DecodeOneClass`, skipping GC-freed/repurposed slots and tracking `staleInstances` in `decodeStats` (surfaced on `SnapshotDecodeStats.StaleInstances`).
    - Made `Il2cppStreamDecoder::DecodeRange` idempotent via `highWaterCursor_` (`ShouldChargeStreamingPlanRow`): replayed cursor ranges still decode payloads without double-counting stats or prematurely exhausting instance/element budgets.
    - Changed `Session::streamDecoder` from `std::unique_ptr` to `std::shared_ptr` so IL2CPP `CmdSnapshotChunk` releases `SessionManager::mu_` immediately after grabbing the `shared_ptr` — running `DecodeRange`, `gc_collect(0)`, and `Sleep(500)` outside the global lock (serialized per-session by `Il2cppStreamDecoder::decodeMu_`, released prior to GC drain and sleep) so concurrent `snapshotBegin` / `snapshotEnd` / disconnect `DropAll()` are never stalled and mid-chunk teardown never causes UAF. Also rejected `domain == nullptr` directly in IL2CPP `CmdSnapshotBegin`.
  - **Tests:**
    - Linked `SnapshotSession.cpp` into `discrete_dll_test` and added `Test_CM4_CM5_SnapshotArrayLayoutAndStreamingSafety` (+40 checks on x64 and x86) verifying `ResolveSnapshotArrayElementPlan` across all element kinds, `IsLiveIl2CppInstanceHeader`, `ShouldChargeStreamingPlanRow` replay idempotency, and concurrent `SessionManager::End` while a mock streaming decoder blocks outside `View`.
    - Added `SnapshotChunk_CustomStructArrayHex_UnknownLayoutError_AndStaleInstances_RoundTrip` to `ui/UnityDumpUI.Tests/ValueScanWireAndDtoContractTests.cs`.
  - Marked `C-M4` and `C-M5` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1566) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1224 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 778 checks passed (x64) / 778 checks passed (x86).

-----

## 2026-11-27 (build 1565) — Fix C-M6 + C-M7 + C-M8 (`searchSymbols` single-assembly `truncated`, handler-level `result.error` & `SnapshotClassEntry.truncated` DTOs, lossless 64-bit value/group scans + tests)

- **Bug fixes (`C-M6`, `C-M7`, `C-M8`):**
  - **`C-M6` (`dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`):**
    - Added `RecordSearchSymbolsAsmCap` in `ExportHelpers.h` and wired it into both Mono and IL2CPP `CmdSearchSymbols`: when a single-assembly query hits the per-assembly match cap, it now sets `truncated: true` (leaving `truncated_assemblies` absent) instead of putting the assembly in `truncated_assemblies` with `truncated: false`. Multi-assembly queries continue to populate `truncated_assemblies`.
  - **`C-M7` (`ui/UnityDumpUI.Core/Services/ProtocolModels.cs`, `ui/UnityDumpUI/ViewModels/AssemblyBrowserViewModel.cs`, `ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs`, `docs/pipe-protocol.md`):**
    - Added `[JsonPropertyName("error")]` (`Error`) to `GetAssembliesResult`, `ClassRangeStatsResult`, `HandshakeResult` (alongside `RuntimePoisoned` / `RuntimeFaultReason`), `PingResult`, and `CancelScanResult`, and added `[JsonPropertyName("truncated")]` (`Truncated`) to `SnapshotClassEntry` so `"memory_budget"` / `"instance_budget"` reasons are preserved.
    - Updated `AssemblyBrowserViewModel` and `SnapshotViewModel` to check `res.Error` after `GetAssembliesAsync` and surface/log backend faults instead of silently treating them as 0 assemblies.
    - Documented the handler-level `result.error` convention in [pipe-protocol.md](pipe-protocol.md).
  - **`C-M8` (`dll/src/shared/ExportHelpers.h`, `dll/src/shared/ExportAPI.cpp`, `ui/UnityDumpUI.Core/Services/ValueScanWireEncoder.cs`, `ui/UnityDumpUI.Core/Services/ProtocolModels.cs`, `ui/UnityDumpUI.Core/Services/BackendAdapter.cs`, `ui/UnityDumpUI/ViewModels/ValueSearchViewModel.cs`, `ui/UnityDumpUI/ViewModels/ValueSearchViewModel.Group.cs`):**
    - Added strict non-truncating JSON numeric parsers (`ParseStrictJsonInt64`, `ParseStrictJsonUInt64`, `ParseStrictJsonDouble`, plus string/double helpers) in `ExportHelpers.h` and switched `PackJsonValueToBytes` in `ExportAPI.cpp` to use them — rejecting NaN/Inf, fractional doubles for integer widths (e.g. `3.7` for `int32`), out-of-range doubles (eliminating C++ UB), trailing string garbage, negative strings for `uint64`, and `> INT64_MAX` values for `int64`.
    - Added Native AOT-safe `ValueScanWireEncoder.TryEncodeNumericArg` encoding integer widths directly as `Int64`/`UInt64` JSON numbers (preserving full 64-bit precision above `2^53`, supporting `0x` hex, rejecting decimals and range overflows) and floating-point widths as `Double` (rejecting NaN/Infinity).
    - Changed `GroupSlotRequest.Value` / `ValueTo` to `JsonElement?`, added lossless `JsonElement` overloads of `BeginValueScanAsync` / `RefineValueScanAsync` in `BackendAdapter`, and updated single-width, aggregate, and group scan paths in `ValueSearchViewModel` to encode per concrete width.
  - **Tests:**
    - Added `Test_CM6_CM8_SearchSymbolsTruncAndStrictJsonNumerics` (+31 checks on x64 and x86) to `dll/tests/dll_core_test.cpp`.
    - Added `ui/UnityDumpUI.Tests/ValueScanWireAndDtoContractTests.cs` (+6 xUnit v3 tests covering single-assembly `searchSymbols` truncation, result-level `error` & `SnapshotClassEntry.Truncated` DTOs, 64-bit `ValueScanWireEncoder` precision, and exact JSON integer emission over a live named pipe).
  - Marked `C-M6`, `C-M7`, and `C-M8` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1565) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1223 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 738 checks passed (x64) / 738 checks passed (x86).

-----

## 2026-11-27 (build 1562) — Fix B-M8 + B-M9 (+ B-L7) (Mono MemberRef `0x0A` generic/cross-assembly field xref resolution, IL2CPP `.pdata`-bounded exact enclosing-method attribution via `RtlLookupFunctionEntry`, IL2CPP struct unboxed offset & `Il2CppClass*` nested-class matching + tests)

- **Bug fixes (`B-M8`, `B-M9`, `B-L7` + field-xref parts of `B-L11` & `B-L12` — completes 100% of Block B High & Medium findings, 2/2 High + 11/11 Medium):**
  - **`B-M8` (`dll/src/shared/FieldXrefHelpers.h`, `MonoResolver.{h,cpp}`, `MonoFieldXref.{h,cpp}`, `ExportAPI.cpp`):**
    - Added optional bindings for `mono_field_from_token` and `mono_field_get_parent` in `MonoResolver`.
    - Extended `MonoFieldXref.cpp` to resolve `0x0Axxxxxx` MemberRef operands (used by ECMA-335 for generic types like `Singleton<T>.instance` / `Pool<T>._items` and cross-assembly field accesses) once per unique token via `mono_field_from_token` with a per-walk memo cache, matching against target field pointer, same-image FieldDef token, or same-image canonical declaring definition `(ns, Outer/Inner)` + field name (`MatchesMonoFieldIdentity`).
    - Added `FindFieldRefsResult::note` (surfaced in `ExportAPI.cpp` `CmdFindFieldReferences`) to emit a clear advisory when the declaring class is generic and either `mono_field_from_token` is unbound or unresolved open-generic MemberRef tokens exist with 0 matches.
    - Fixed x86 `size_t` overflow in IL `switch` operand advance (`SafeIlSwitchAdvance`), guarded `limit == 0`, and wrapped all Mono metadata accessors in `MonoFieldXref.cpp` in SEH leaves.
  - **`B-M9` (`dll/src/shared/FieldXrefHelpers.h`, `dll/src/il2cpp/IL2CPPFieldXref.{h,cpp}`):**
    - Replaced unbounded `upper_bound - 1` enclosing-method attribution in `IL2CPPFieldXref.cpp` with `FindEnclosingMethodExact` backed by Win64 `RtlLookupFunctionEntry` (PE `.pdata` unwind table): when `.pdata` knows `[funcBegin, funcEnd)` for `ip`, `methodIndex` must match `entry.address == funcBegin` exactly, cleanly dropping hits inside interleaved functions from other assemblies instead of misattributing them to the target image as `exact`.
    - Added a bounded leaf-function fallback (`< 256 B`) that still rejects hits falling past a preceding `.pdata`-bounded method's `EndAddress`, and clamped per-method decode ranges in `FindInstanceFieldReferences` to `.pdata`'s `EndAddress`.
    - Guarded `limit == 0` and updated the stale `FieldRefKind::Write` comment in `IL2CPPFieldXref.h` (`B-L12`).
  - **`B-L7` (`dll/src/shared/FieldXrefHelpers.h`, `dll/src/il2cpp/IL2CPPFieldXref.cpp`):**
    - Adjusted `FindInstanceFieldReferences` to subtract `ManagedLayout::kObjectHeader` when the declaring class is a value type (`struct`, where AOT `this` is unboxed; `ComputeIl2CppInstanceScanOffset`).
    - Recorded `Il2CppClass* klass` in `MethodIndexEntry` and matched declaring-class methods by pointer equality (`m.klass == target.klass`) instead of `(ns, className)` strings, fixing 0-match recalls on nested classes resolved via dot-fallback (`Ns.Outer` + `Inner` vs `Ns` + `Outer/Inner`).
  - **Tests:**
    - Added `Test_BM8_BM9_BL7_FieldXrefMemberRefPdataAndStructOffset` (+35 checks x64 / +32 checks x86) to `dll/tests/dll_core_test.cpp` verifying token classification, generic `MatchesMonoFieldIdentity` (direct/token/name/cross-image/mismatch), x86-safe `SafeIlSwitchAdvance` overflow guards, `FindEnclosingMethodExact` across interleaved other-assembly functions & leaf fallbacks plus live Win64 `RtlLookupFunctionEntry`, and `ComputeIl2CppInstanceScanOffset`.
  - Marked `B-M8`, `B-M9`, and `B-L7` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1562) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 707 checks passed (x64) / 707 checks passed (x86).

-----

## 2026-11-26 (build 1561) — Fix B-M6 + B-M7 + B-M11 (noexcept `OnAssembly` & global targeted SEH filter + `RuntimeHealth` poison gate, SEH-protected chunked `IL2CPPMetadataRegistration` scan with page fallback, unified Mono `IsPlausibleVTable` domain validation + tests)

- **Bug fixes (`B-M6`, `B-M7`, `B-M11`):**
  - **`B-M6` (`dll/src/shared/RuntimeHealth.h`, `MonoDomain.cpp`, `MonoAddressFinder.cpp`, `MonoClassWalker.cpp`, `IL2CPPClassWalker.cpp`, `MonoMethodInvoker.cpp`, `IL2CPPMethodInvoker.cpp`, `ExportAPI.cpp`, and all remaining Mono/IL2CPP SEH leaves):**
    - Made `MonoDomain.cpp`'s `OnAssembly` callback `noexcept` with a local `try / catch (...)` setting `ctx->callbackFailed` so C++ exceptions (e.g. `std::bad_alloc` under 32-bit Mono memory pressure) can never unwind across Mono's `mono_assemblies_lock` frame and permanently deadlock the game.
    - Replaced every remaining raw `__except (EXCEPTION_EXECUTE_HANDLER)` across all 17 `.cpp` files in `dll/src/{mono,il2cpp,shared}` with `Discrete::Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())`, and refactored `MonoAddressFinder.cpp`'s `SehSweepRegionLoad` to copy 64 KiB windows through a leaf `SehCopyRegionWindow` so vector allocations never run inside `__try`.
    - Added `dll/src/shared/RuntimeHealth.h` (`MarkRuntimeFault`, `IsPoisoned`, `GetSnapshot`, `FormatReason`): SEH faults inside lock-acquiring runtime calls (`mono_assembly_foreach`, `mono_class_vtable`, `*_class_get_fields`, `*_runtime_invoke`) latch the poison flag and log a `WARN`; `ExportAPI.cpp` rejects subsequent heavy RPCs (`snapshotBegin`, `findInstances`, `callMethod`, `findFieldReferences`, `beginValueScan`, `diagnoseClassReferences`) with `runtime_corrupted_restart_game` and surfaces `runtime_poisoned` in `handshake`.
  - **`B-M7` (`dll/src/shared/SafeChunkRead.h`, `dll/src/il2cpp/IL2CPPMetadataRegistration.cpp`):**
    - Added `dll/src/shared/SafeChunkRead.h` providing `ListInitDataSectionsSafe` (reads PE DOS/NT/section headers via `Memory::SafeReadBytes`) and `ScanReadableSpans` (copies 1 MiB chunks with 128 B overlap via `Memory::SafeReadBytes` and falls back to 4 KiB page-by-page coalesced reads when an anti-cheat strips a page to `PAGE_NOACCESS` / `PAGE_GUARD`, preserving adjacent readable pages).
    - Migrated `IL2CPPMetadataRegistration.cpp` (`ListInitDataSections`, `DoFind`, `TestCandidate`, and `GetTypeTag`) to zero raw dereferences into `GameAssembly.dll`.
  - **`B-M11` (`dll/src/mono/MonoVtableFilter.h`, `dll/src/mono/MonoAddressFinder.cpp`):**
    - Added `ValidateMonoVtableHeader` in `MonoVtableFilter.h` and extracted a single shared `IsPlausibleVTable` helper in `MonoAddressFinder.cpp` called by all three paths (`WhatIsAt`, `FindAllInstances`, `ReverseLookupIndex::Build`).
    - `WhatIsAt` now enforces `MonoVTable::domain == rootDomainAddr` on static `classSet` hits as well as dynamic generic/array klasses, eliminating false positives from `MonoType` / `RuntimeTypeHandle` structs and preventing unvalidated pairs from polluting the global `vtableToKlass` memo. Also unified `g_minVtable` update in `FindAllInstances`.
  - **Tests:**
    - Added `Test_BM6_BM7_BM11_RuntimeHealthSafePeScanAndVtableValidation` (+29 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` verifying `RuntimeHealth` root-cause preservation & reset, `ListInitDataSectionsSafe` on null/invalid/live PE bases, `ScanReadableSpans` over a live 3-page `VirtualAlloc` region with a `PAGE_NOACCESS` middle page, and `ValidateMonoVtableHeader` across x64/x86 domain & alignment rules.
  - Marked `B-M6`, `B-M7`, and `B-M11` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1561) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 672 checks passed (x64) / 675 checks passed (x86).

-----

## 2026-11-26 (build 1559) — Fix B-M1 + B-M2 + B-M3 (formal-type CallMethod arg validation, GC-root pinning for allocated string args, safe value-type/enum/bool return decoding + MethodInvoker parity cleanups + tests)

- **Bug fixes (`B-M1`, `B-M2`, `B-M3` + MethodInvoker parts of `B-L8`, `B-L9`, `B-L11`):**
  - **`B-M1` (`dll/src/shared/MethodInvokeValidation.h`, `MonoMethodInvoker.cpp`, `IL2CPPMethodInvoker.cpp`):**
    - Created shared header-only `dll/src/shared/MethodInvokeValidation.h` defining `FormalParamDescriptor`, `MarshalRoute`, and strict allowlist validator `ValidateCallArg`.
    - Captured per-parameter runtime metadata (`class_from_*_type`, `class_is_valuetype`, `class_is_enum`, `class_enum_basetype` / `value__` field fallback, `class_value_size`) in `ResolveOverload` on both Mono and IL2CPP.
    - Enforced exact formal-type matching before marshaling: scalar primitives must match exact width/kind (`arg_type_mismatch` otherwise); `System.IntPtr`/`System.UIntPtr`/unsafe `*` pointers accept `Address`/`Null` and route through `ValueSlot` (`&slot`, fixing value-type indirection); `System.String` accepts `String`/`Null` only; `System.Object` accepts `Address`/`Null`/`String` (rejecting raw scalars/structs so native stack/heap slot pointers can never be passed as `MonoObject*`/`Il2CppObject*`); other reference types accept `Address`/`Null` only; enums accept their underlying integer kind (or exact-size `ValueStruct`); non-enum structs accept `ValueStruct` ONLY with exact `class_value_size` match; by-ref (`&`) parameters are rejected cleanly.
  - **`B-M2` (`MethodInvokeValidation.h`, `MonoResolver.{h,cpp}`, `IL2CPPResolver.{h,cpp}`, `MonoMethodInvoker.cpp`, `IL2CPPMethodInvoker.cpp`):**
    - Added optional bindings for `mono_gchandle_new` / `mono_gchandle_free` (`uint32_t`) and `il2cpp_gchandle_new` / `il2cpp_gchandle_free` (`uintptr_t`, ABI-compatible with both legacy `uint32_t` handles and Unity 2023+/6.0 pointer-sized `Il2CppGCHandle`), plus `il2cpp_class_is_enum` and `il2cpp_class_enum_basetype`.
    - Added RAII `ScopedGcPinList` that both pins allocated managed string arguments via `gchandle_new` (freeing in LIFO order on normal exit or early error) AND retains a volatile stack-resident root array (`kMaxStackRoots = 32`) kept live across `runtime_invoke` for conservative BDWGC stack scanning when `gchandle` exports are unavailable.
  - **`B-M3` (`MethodInvokeValidation.h`, `MonoMethodInvoker.cpp`, `IL2CPPMethodInvoker.cpp`, `ExportAPI.cpp`, `docs/lua-invoke.md`, `docs/pipe-protocol.md`):**
    - Added `ClassifyReturnObject` and runtime `object_get_class(boxed)` + formal return inspection in `DecodeReturnValue` on both backends:
      - `System.Boolean` now produces `CallReturn::Kind::Bool`, serialized as JSON `true`/`false` in `ExportAPI.cpp` (resolving dead `Kind::Bool` and Lua's truthy-`0` footgun).
      - Enum returns are decoded via `class_enum_basetype` (or `value__` field fallback) into `SignedInt`/`UnsignedInt` instead of leaking a transient boxed address.
      - Non-enum value types (`Vector3`, `DateTime`, `Nullable<T>`, custom structs) without `wantStructReturn == true` now return `CallReturn::Kind::Unsupported` (matching header documentation and never leaking an ephemeral GC-collectable boxed address).
  - **Adjacent MethodInvoker cleanups:**
    - Replaced hardcoded `0x10`/`0x14`/`0x18` string/exception offsets in `IL2CPPMethodInvoker.cpp` with `ManagedLayout` constants (`B-L8` partial).
    - Deferred Mono `SehCompile` (and IL2CPP `ReadMethodPointer`) in `ResolveOverload` until after confirming `matchCount == 1` so ambiguous lookups never trigger side-effectful JIT compilation (`B-L9` partial).
    - Unified `CallMethod` exit via `finish()` so early-return error paths also populate `elapsedMs` and write diagnostic log lines (`B-L11` partial).
    - Switched metadata-accessor SEH leaves in both MethodInvoker files to `Seh::MemoryAccessExceptionFilter`.
  - **Tests:**
    - Added `Test_BM1_BM2_BM3_MethodInvokeValidationAndGcRoots` (+62 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` covering primitive/IntPtr/byref/String/Object/reference/enum/ValueStruct `ValidateCallArg` rules, `ScopedGcPinList` LIFO handle release & stack-root fallback, and `ClassifyReturnObject` for Void/Bool/String/Enum/ValueType/Reference returns.
  - Marked `B-M1`, `B-M2`, and `B-M3` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1559) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 643 checks passed (x64) / 646 checks passed (x86).

-----

## 2026-11-26 (build 1557) — Fix B-M4 + B-M5 (+ B-L6) (reject literal/thread-static static-field addresses & unify `int32_t` field offsets, strict RFC 3629 UTF-8 class/image names in walkers, `System.Enum` namespace check + tests)

- **Bug fixes (`B-M4`, `B-M5`, `B-L6`):**
  - **`B-M4` (`dll/src/shared/ManagedLayout.h`, `MonoClassWalker.{h,cpp}`, `IL2CPPClassWalker.{h,cpp}`, `IL2CPPFieldXref.cpp`, `ExportAPI.cpp`):**
    - Added `kFieldAttrStatic`, `kFieldAttrLiteral`, `NormalizeFieldOffset32<T>`, `StaticFieldStorageKind` (`Normal`, `NonStatic`, `Literal`, `ThreadStatic`), `ClassifyStaticFieldStorage`, and `StaticFieldStorageReason` in `ManagedLayout.h`.
    - Unified `FieldEntry::offset` and `StaticFieldAddress::offset` to signed `std::int32_t` across both Mono and IL2CPP walkers (previously `std::size_t` in IL2CPP, where a `-1` thread-static sentinel sign-extended to `0xFFFFFFFFFFFFFFFF` on x64 and wrapped `staticBase + offset` to `staticBase - 1`), and added `StaticFieldAddress::reason`.
    - Gated `ResolveStaticFieldAddress` (Mono & IL2CPP), `IL2CPPFieldXref::ResolveStaticField`, IL2CPP snapshot static capture, and `CmdGetStaticFieldData` (surfacing `reason`) so `FIELD_ATTRIBUTE_LITERAL` (`const`, no runtime storage) and thread-static fields (`offset < 0` / `THREAD_STATIC_FIELD_OFFSET == -1`) are cleanly rejected instead of silently reading offset 0 or `staticBase - 1`.
    - Guarded instance field-offset sites in `ExportAPI.cpp` (Mono/IL2CPP snapshot schema & element walks, `findInstances` `sane` probes, and class-scoped `scanFieldValue`) against negative offsets before casting to `std::size_t` / `std::uintptr_t`.
  - **`B-M5` (`dll/src/shared/AddressBounds.h`, `MonoClassWalker.cpp`, `IL2CPPClassWalker.cpp`):**
    - Tightened `LooksLikePlausibleClassName` to enforce strict RFC 3629 UTF-8 validation (rejecting 3-byte overlong sequences, UTF-16 surrogates `U+D800..U+DFFF`, 4-byte overlongs, and code points `> U+10FFFF`) alongside printable ASCII checks.
    - Replaced the ASCII-only `SehNameLooksSane` (`0x20..0x7E`) in both `MonoClassWalker.cpp` and `IL2CPPClassWalker.cpp` with `SehCopyNameBounded` (using `MemoryAccessExceptionFilter`) followed by `LooksLikePlausibleClassName`, so valid UTF-8 CJK/Cyrillic/Unicode class, namespace, and image names from obfuscated or localized Unity games are accepted while corrupt pointers and invalid byte sequences are still rejected.
  - **`B-L6` (`IL2CPPClassWalker.cpp`, `IL2CPPAddressFinder.cpp`):**
    - Qualified IL2CPP enum parent detection with `ns == "System"` (`parentName == "Enum" && parentNs == "System"`), preventing user-defined classes named `Enum` in another namespace from being misclassified as CLR enums.
  - **Tests:**
    - Added `Test_BM4_BM5_StaticFieldStorageAndUtf8Names` (+36 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` verifying `NormalizeFieldOffset32` (`-1`, `0xFFFFFFFF`, `0xFFFFFFFFFFFFFFFF`), `ClassifyStaticFieldStorage` (`Normal` / `NonStatic` / `Literal` / `ThreadStatic`), `StaticFieldStorageReason`, and `LooksLikePlausibleClassName` across ASCII, valid 2/3/4-byte UTF-8 (Cyrillic, CJK, emoji), and malformed UTF-8 (overlongs, surrogates, truncated sequences, `> U+10FFFF`, control chars).
  - Marked `B-M4`, `B-M5`, and `B-L6` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1557) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 581 checks passed (x64) / 584 checks passed (x86).

-----

## 2026-11-26 (build 1556) — Fix A-M5 + A-M6 (+ A-L13, A-L14) (event-driven proxy backend loader with logging, strict SEH-leaf window scans, targeted SEH filter preserving `PAGE_GUARD` & C++ exceptions, remove dead `AOBScan` + tests)

- **Bug fixes (`A-M5`, `A-M6`, `A-L13`, `A-L14` — completes 100% of Block A High & Medium findings, 2/2 High + 12/12 Medium):**
  - **`A-M5` (`dll/src/proxy/loader.{h,cpp}`):**
    - Replaced the brittle 5-second `50 × 100 ms` silent timeout in `BackendLoaderThread` with event-driven `LdrRegisterDllNotification` (dynamically resolved from `ntdll.dll`) backed by capped exponential backoff (`100 ms` → `5000 ms`) so games sitting at a Unity resolution dialog, launcher splash, or slow cold start wake and attach immediately the moment `mono*` / `GameAssembly.dll` loads.
    - Added diagnostic logging (`LogProxyLine` → init log) for `DirOfSelf` failure, >5 s waiting advisory, and `LoadLibraryW` failure (including backend DLL name, `GetLastError`, and architecture).
    - Replaced `std::wstring SystemDllPath` with an allocation-free stack buffer `BuildSystemDllPath` inside `noexcept` `Proxy_LoadRealDll` (eliminating `std::bad_alloc` → `std::terminate` hazard) and updated the stale x86 comment in `loader.h`.
  - **`A-M6` + `A-L14` (`dll/src/shared/SehFilter.h`, `dll/src/shared/Memory.cpp`):**
    - Added `dll/src/shared/SehFilter.h` with `MemoryAccessExceptionFilter`: handles ONLY `EXCEPTION_ACCESS_VIOLATION`, `EXCEPTION_IN_PAGE_ERROR`, and `STATUS_GUARD_PAGE_VIOLATION` — returning `EXCEPTION_CONTINUE_SEARCH` for C++ exceptions (`0xE06D7363` / `std::bad_alloc`) so they unwind properly instead of being swallowed as read failures, and restoring `PAGE_GUARD` on the exact faulted page via `VirtualProtect(faultAddr, 1, ...)` before handling `STATUS_GUARD_PAGE_VIOLATION` so TOCTOU hits never strip another thread's stack guard page.
    - Refactored `Memory.cpp` pointer (`ScanPointerChunkSafe`), value (`ScanValueChunkSafe`), and string (`ScanStringChunkSafe`) chunk scanners to copy stride-aligned 64 KiB windows (with `elemSize - 1` overlap) through leaf `ReadSeh` into a `thread_local` scratch buffer and run all comparison + `matches.push_back` strictly outside `__try`, bringing every `__try` in `Memory.cpp` into strict SEH-leaf compliance.
  - **`A-L13` (`dll/src/shared/Memory.{h,cpp}`):**
    - Removed unreferenced, un-SEH-guarded dead functions `AOBScan` and `AOBScanModule`.
  - **Tests:**
    - Added `Test_AM6_AL14_SehFilterGuardPageAndWindowStraddle` (+13 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp` verifying `MemoryAccessExceptionFilter` classification, real `throw std::bad_alloc()` propagation through SEH, live `PAGE_GUARD` preservation across a raw SEH read fault, and multi-window (>128 KiB) `ScanForValue` / `ScanForString` 64 KiB boundary straddle accuracy with non-dividing stride.
    - Ran `python tools/verify/proxy_precrt_gate.py --all` — PASS on both `dist\version.dll` and `dist\winmm.dll`.
  - Marked `A-M5`, `A-M6`, `A-L13`, and `A-L14` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1556) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Proxy DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; `proxy_precrt_gate.py --all` passed; C# xUnit v3 tests 1217 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 545 checks passed (x64) / 548 checks passed (x86).

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
