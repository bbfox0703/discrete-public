# UnityCEDumper — Session Hand-off

**Branch**: `dev` (clean, pushed)
**Current state**: **Phases 0–15 + 9.5 + 11.5 + 14c all closed** for x64. One phase stays deferred: **Phase 12d (32-bit Mono)** — blocked on engineering effort (parallel x86 build pipeline + cross-bitness inject). Phase 14c (IL2CPP metadata v24/v27/v29/v31 dispatch) closed this session against six local games covering all four versions. Last commit: `9f7c067` (proxy-deploy version comparison). This session also closed a long-standing Connect-after-disconnect bug (PipeClient lifecycle + DLL pipe single-instance + UI-side fresh-PipeClient swap) and shipped version stamping across every DLL/exe.

> Companion files in this folder: `plan.md` (full phase ledger), `pipe-protocol.md` (DLL ↔ UI contract), `Archiutecture.md` (rules / invariants), `test-matrix.md` (verified games + tooling versions).

---

## What's done

| Phase | Status | Highlights |
|---|---|---|
| 0 — Bootstrap | ✅ | Repo skeleton, build scripts, single-instance mutex |
| 1 — DLL skeleton | ✅ | Named pipe `\\.\pipe\UnityDumpBfx`, JSON-RPC dispatcher, SEH-leaf helpers |
| 2 — Pipe protocol v1 | ✅ | Line-delimited JSON, error codes, `ping` / `handshake` / `scan` |
| 3 — Mono backend | ✅ | `MonoResolver` (23/23 + nested-type + element-class exports), 7 introspection commands |
| 4 — IL2CPP backend | ✅ | Required/optional split, runtime walker, method-pointer fallback (Unity 2021.3+ doesn't export `method_get_pointer`; we read `MethodInfo.methodPointer` directly) |
| 5 — IL2CPP metadata deep parse | ✅ | `MetadataParser` for v29, `Il2CppMetadataRegistration` AOB scan, type-aware FieldDefault decode (incl. ECMA-335 §II.23.2 sentinels), strip/obfuscation heuristic |
| 6 — Live memory + walking | ✅ | `readMemory` / `writeMemory` / `resolvePointer` / `findInstances` / `walkInstance` / `walkArray` / symbol registry / `monoGetVTable` / `getEnumValues` |
| 7 — UI skeleton | ✅ | Avalonia 12 + Native AOT, `PipeClient`, `BackendAdapter`, `FileLogger`, `WindowsProcessEnumerator`, 5-pane shell |
| 8 — UI panels (tree & browse) | ✅ | `AssemblyBrowserViewModel`, virtualized assembly + class lists, fields/methods DataGrids, substring filter, "show inherited members" toggle, GridSplitter resizable panes, hover tooltips for long names |
| 9 — Live inspection | ✅ | DTOs (`FindInstancesResult` / `WalkInstanceResult` / `GetStaticFieldDataResult` / `ReadMemoryResult` / `WalkArrayResult`), `LiveInspectionViewModel` (find → walk cascade, `PeriodicTimer` auto-refresh, static-field bridge), Inspector tab, right-click "Inspect" + "Walk array". Nested-type support (Mono + IL2CPP): classes surface as `Outer/Inner`; `FindClass` walks `class_get_nested_types`. **Verified live**: `MainGame/PLY_WORK` decode matches a CE reference table value-for-value (SaGa F2R, IL2CPP). |
| 9.5 — In-app process picker + injector | ✅ | `IProcessEnumerator.ListUnityProcesses()` filters by detected modules; `IDllInjector` is an AOT-friendly C# port of `tools/Inject-Dll.ps1` using `LibraryImport` source-gen P/Invokes. `ProcessPickerWindow` modal + "Pick & inject…" button replace `Test-Handshake.ps1` for the launch-and-attach workflow. **AOT caveat captured**: `LibraryImport` needs explicit `EntryPoint=GetModuleHandleW` — does not auto-resolve A/W like classic `DllImport`. |
| 10 — Export formats (UI-side) | ✅ | `UnityTypeMap` (managed type → CE / C type, with `ExtractElementClass` + `IsCollectionType`). Three exporters under [ui/UnityDumpUI.Core/Services/Export/](../ui/UnityDumpUI.Core/Services/Export/): `SdkHeaderExporter`, `CsxExporter` (nested `<Structure>` for pointer drilldown), `CeXmlExporter` (hierarchical CheatEntries with `GroupHeader=1` + `Offsets=[0]` deref + `<Options moHideChildren=1/>` collapse, **plus live-data mode** that turns the tree into absolute-rooted real-instance CE entries by chaining `readMemory` + `walkArray` per node). Five UI surfaces share one `ExportOptions` knob set (depth / arrayLimit / collapse / baseAddress / liveData) plumbed through an `Export options ▾` flyout: Export `.h` / `.csx` / `.CT` to file, Copy CE XML to clipboard, Copy CE field via right-click. |
| 10.x — `walkArray` + element-class metadata | ✅ | Backend-agnostic pipe command reads array header (`+0x10 bounds*`, `+0x18 max_length`, `+0x20 vector[0]`) and returns per-slot data. Optional `elementAssembly`/`Namespace`/`Name` triple resolves the element class via `class_value_size` + `class_is_valuetype` (new optional resolver binds in both backends), so struct arrays (`Vector3[]`) walk with the right stride and per-slot drilldown gets correct value-vs-ref semantics. |
| 11 — CE Lua bridge | ✅ | Four scripts in [scripts/](../scripts/): `utils.lua` (callDLL helper + backend module probe + DLL path resolver), `unity_dump.lua` (detect Mono / IL2CPP, inject DLL, banner with build #), `unity_dissect.lua` (open `\\.\pipe\UnityDumpBfx` directly via `io.open` + tiny purpose-built JSON parser, build CE Structure Dissect entries with managed-type → Vartype map matching `CsxExporter`), `unity_symbols.lua` (batch-register CE symbols for static fields + method addresses; IL2CPP reads addresses straight from `getClassInfo`, Mono auto-issues `monoCompileMethod` per method to JIT and register — opt-out via `{compileMethods=false}`). **Verified live** against SaGa F2R: dofile → DLL injects → handshake reports `IL2CPP / 2021.3.39f1 / metadata 29 / 57 assemblies / build 101`. CE-portability fixes: backend detection now uses `enumModules()` (direct OS query) instead of `getModule()` (which lags behind symbolhandler) and accepts `UserAssembly.dll` + `mono-2.0-{sgen,boehm}.dll`; injection uses `injectDLL()` (modern CE) with `loadLibrary` fallback; `reinitializeSymbolhandler()` is forced after injection so `getAddress()` can resolve fresh exports. Optional StartPipe / GetBuildNumber calls degrade to soft warns (DllMain auto-starts the pipe, so they're nice-to-haves). |
| 11.5 — Inspector inline drilldown | ✅ | `WalkedFieldRow` → `WalkedFieldNode` (hierarchical, observable). Field grid is now a flat-with-indent `DataGrid` (`DataGridTemplateColumn` for Name with indent border + chevron Button + load hint, `CanUserSortColumns="False"` so parent-child adjacency survives). Five node kinds — `Leaf` / `PointerToClass` / `Collection` / `ArraySlotPointer` / `ArraySlotLeaf` — drive lazy expansion: pointer-to-class → `walkInstance(ptr, classType)`, `T[]` / `List<T>` → `walkArray` (List auto-deref's `_items` at +0x10), pointer-array slots recurse. Collapse walks the Parent chain (robust against odd indent layouts). Auto-refresh snapshots top-level expanded names + replays expansion after each fresh `walkInstance`, bounded one-RPC-per-expanded-root cost. Right-click any node → Copy CE XML rooted at that node (reuses `CeXmlExporter.RenderAsync` live mode at `EffectiveAddress`) / Copy CE field (single-leaf XML). The separate "Array elements" Expander + "Walk array" button from Phase 10 are gone. **Ride-along fixes**: `UnityTypeMap.CeDisplay(type) → (hex, signed)` centralises CE leaf flags so `Int32` leaves emit `<ShowAsSigned>1</ShowAsSigned>` (was hardcoded 0 + hex-via-`EndsWith("Bytes")`); DLL path resolution switched to `Environment.ProcessPath` so `Pick & inject` works under self-extracting single-file mode (`AppContext.BaseDirectory` was pointing at the per-launch `%LOCALAPPDATA%\Temp\.net\…` extraction dir). |
| 13 — Offline metadata CLI | ✅ | `tools/metadata-cli/main.cpp` + new `BUILD_CLI=ON` cmake option produces a 67 KB Release exe (`dist/metadata-cli.exe`). Subcommands: `info` (version + counts), `stats` (replicates the `getMetadataStats` strip-heuristic verbatim — `clean` / `partial_strip` / `heavy_strip`), `types` (one JSON record per type def to stdout), `type <ns> <name>` (fields + decoded string defaults). Output is line-oriented JSON to stdout; errors → stderr, exit 1 on failure. **Refactor**: `MetadataParser.cpp` now backend-agnostic core; `BackendDetector` dependency moved to `MetadataParserHostPath.cpp` (DLL-only). The CLI links the core + Logger only. **v29-gate** surfaced explicitly: `info` reports `type_tables_parsed: false` + a stderr note when running against v24/v27/v31 metadata (string-literal queries still work; full type-table support lands with Phase 14c). Smoke-tested against SaGa Frontier 1 Remastered (v24): loads, 15182 string literals, type-tables 0 with the hint. Build wiring: `build.cmd cli` / `build.ps1 -Target MetadataCli`. |
| 12 (x64) — Proxy DLLs + bulk deploy panel | ✅ | **12a** `dll/src/proxy/{loader.cpp,version/version.cpp,winmm/winmm.cpp}` build into `version.dll` + `winmm.dll` (~130 KB each). Hand-written export thunks alias internal `Proxy_<symbol>` functions to the public export name via `.def` files (avoids C2375 vs the system header's `__declspec(dllimport)` decls). DllMain pre-loads the real `C:\Windows\System32\<dll>` (so first thunk call is cheap), `DisableThreadLibraryCalls`, then spawns a worker thread that polls for backend modules up to ~5s, detects via `BackendDetector::Detect()`, and `LoadLibraryW`s the matching `UnityDumper-{Backend}.dll` from the same directory. **12b** Per-process named mutex `Global\UnityCEDumper-Init-PID-{pid}` claimed in `OnAttach`; if `ERROR_ALREADY_EXISTS`, log + skip Init. Covers both "proxy already loaded us, then user clicked Pick & inject" and "two backend DLLs accidentally injected" — first claim wins. **12c** `IProxyInstaller` in `Core` + `WindowsProxyInstaller` in UI; mutex install (one of `version.dll` / `winmm.dll` at a time, the other is removed first); source files resolved via `Environment.ProcessPath` (works under AOT publish + self-extracting single-file). `IUnityGameScanner` + `SteamGameScanner` enumerate Steam libraries via `libraryfolders.vdf` (HKCU/HKLM `SteamPath`/`InstallPath`), walk `steamapps\common\*`, detect Unity by markers (`UnityPlayer.dll`, `GameAssembly.dll`, `MonoBleedingEdge\`, `*_Data\Managed`, `*_Data\il2cpp_data`), read PE `IMAGE_FILE_MACHINE` for bitness, classify into `UnityGameEntry` (backend + bitness + currently-installed proxy). **12c-deploy** `ProxyDeployWindow` (UE5CEDumper-style multi-select panel) replaces the original per-game flyout. Toolbar: ↻ Rescan / Select all / Unselect all / Invert selection / "When installing: ◉ version.dll ○ winmm.dll" radios / "Show 32-bit / unsupported" filter. DataGrid columns: checkbox, Game, Backend, Bits, Proxy, Status, Last operation, Path. Bottom bar: **Install selected / Uninstall selected / Update all deployed** (re-installs every already-deployed row with its current proxy kind — the "I rebuilt DiscreteUI, push fresh DLLs everywhere" button) plus a `N selected · M deployed · K total` summary. Window is non-modal, opened from the single `Proxy Deploy…` button on the connection bar (the old `Proxy DLL ▾` flyout + `UnityGamePickerWindow` modal are deleted). `ProxyGameRow` ([ui/UnityDumpUI/Models/ProxyGameRow.cs](../ui/UnityDumpUI/Models/ProxyGameRow.cs)) wraps each `UnityGameEntry` to add the per-row `IsSelected` observable + cached `DeployStatus` enum without polluting the Core data type. Bulk ops preserve selection across `Refresh` (matched by exe path) and use the post-op `ProxyOperationResult.Status` to update each row's snapshot in-place — no per-row second `DetectAsync` call. |
| 15 — Test matrix | ✅ | Initial ledger landed at [docs/test-matrix.md](test-matrix.md). Three verified targets across two backends and two metadata versions (SaGa F2R / SaGa F1R / Hollow Knight Silksong, covering Unity 2021.3 IL2CPP, Unity 2019.4 IL2CPP v24, Unity 6 Mono). Tooling-version pins (Avalonia 12.0.1, .NET 10.0.203, MSVC 14.50, CE 7.5.1) and explicit Phase 14c/12d coverage gaps captured. Reproduction commands documented for re-verifying any row from a fresh shell. |
| 12d — 32-bit Mono support | ⏳ DEFERRED | Not started. Out-of-scope for the x64 Phase 12 ship. Required pieces: parallel x86 build configuration in CMake (current root `CMakeLists.txt` enforces x64 via `CMAKE_SIZEOF_VOID_P EQUAL 8`); x86 build of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified; cross-bitness injection in the UI (current `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process — won't work into a 32-bit target). 32-bit IL2CPP is moot (Unity 2018+ ships IL2CPP x64-only). For x86 Mono targets today: the proxy install path *should* still work because the proxy is loaded by the game itself, BUT we currently only ship x64 proxy DLLs; the UI installer will copy x64 binaries that the 32-bit game can't load. **TODO inventory**: x86 proxy build, x86 backend Mono DLL build, cross-bitness inject (or document "proxy install only" as the supported path for 32-bit), x86 / x64 selection in the install flyout based on detected target architecture. |
| 14c — IL2CPP metadata v24 / v27 / v29 / v31 dispatch | ✅ | `MetadataParser` now supports four IL2CPP metadata versions via empirically-verified TypeDefinition layouts. **v24** (Unity 2018.4-2019.x) gets its own struct (92-byte stride, +`byrefTypeIndex` int32 vs v29). **v27** (Unity 2020.x), **v29** (Unity 2021.x-2022.1), **v31** (Unity 2022.2+) all share the v29 layout (88-byte stride, IL token at offset 84). Strides + token positions verified by hex-dumping each game's `typeDefinitions` table and locating the canonical `0x02000003` row-2 token. Test fixtures (all offline via `metadata-cli`, no game launches): SaGa Frontier Remastered (v24, 7,514 types), FINAL FANTASY VI PR (v24, 11,438), **Townscaper** (v27, 8,132 types — x86 game proves the format is bitness-neutral), SaGa Frontier 2 Remastered (v29, 10,001), Farthest Frontier (v31, 15,848), Emberward (v31, 23,982). All six match canonical leading types (`<Module>`, anonymous types, `Mono.Runtime` etc.) with correct IL tokens. The earlier `_v29Tables` flag became `_tablesLoaded` and `LoadV29Tables` became `LoadTables`; query functions dispatch on `version_` and project to the version-agnostic `TypeDefView`. |
| 14 — Hardening | ✅ | **14a** auto-disconnect on pipe teardown: `PipeClient` raises a `Disconnected` event when next read fails (IO / null); `MainWindowViewModel` runs the same teardown the explicit Disconnect does, tagged `(game exited)`, so Connect / Pick & inject re-enable automatically. Previously: target exit broke every subsequent RPC, only recovery was UI restart. **14b** stripped-metadata banner: opportunistic post-handshake `getMetadataStats` call surfaces `partial_strip` / `heavy_strip` verdicts as a yellow banner, so users hitting an obfuscated build see the warning before they spend an hour wondering why every class is `a`/`b`/`c`. **14d** Mono thread-attach audit: every direct `r.<runtime_fn>` call in pipe handlers reviewed; the only gap (walkArray's Mono element-class lookup running un-attached) fixed by adding a guard around the bare resolver calls (single attach, after `Find*` returns — not nested, per build #54). **14e** log rotation: code review confirmed `RotateIfNeeded` is correct (4 files × 8 MB caps); shipped `tools/Get-LogStats.ps1` for post-session spot checks. **14f** SEH-leaf audit: every `__try` body in `dll/src/` verified leaf-shape (one fn call or one memcpy). Both rules (SEH-leaf + Mono attach policy) now codified in `Archiutecture.md`'s "Rules (MUST follow)". **14c IL2CPP version dispatch (v24/27/31)** deferred — needs Unity 2017 / 2018 / 2023 target games to validate against. |

### Repo / build tweaks landed alongside

- **Proxy Deploy panel surfaces installed-vs-source version comparison**: builds on the version-stamping infrastructure below. `WindowsProxyInstaller.DetectSync` now reads `FileVersionInfo.GetVersionInfo` off both the installed proxy DLL and `dist/`'s copy, attaches both strings to `ProxyInstallStatus`, and hands them through to `ProxyGameRow`. The row's `DeployStatus` enum gained `DeployedCurrent` and `DeployedOutdated` variants; the Status column now reads e.g. `Deployed (version.dll, OUTDATED)` and a new `Version` column shows `0.1.0.139 → 0.1.0.146` for outdated rows, `(would install 0.1.0.146)` for not-deployed rows. Special case: an installed proxy with no readable FileVersion (deployed by a pre-stamping build, ≤140 or so) is treated as outdated since the verdict is otherwise indeterminate. The bottom-bar summary appends `(N outdated)` when any deployed row is stale, so a glance at the panel says "you have N games to update." The existing `Update all deployed` button now matches all three deployed states (`Deployed`, `DeployedCurrent`, `DeployedOutdated`) so re-installs always cover everything.
- **Version stamping across every binary**: each shipped DLL/exe now carries a Windows PE VERSIONINFO resource and a runtime log banner with the same string. CMake reads `VER_MAJOR/MINOR/PATCH` from `project(... VERSION 0.1.0 ...)`, the 4th digit from `build_number.txt`, plus `git rev-parse HEAD/--short HEAD`, dirty flag, and `string(TIMESTAMP ... UTC)`, then `configure_file`s [BuildInfo.h.in](../dll/src/shared/BuildInfo.h.in) and per-target [version.rc.in](../dll/src/shared/version.rc.in) into the build dir. Both backend DLLs (`UnityDumper-Mono.dll`, `UnityDumper-IL2CPP.dll`) and both proxy DLLs (`version.dll`, `winmm.dll`) embed FileVersion `0.1.0.<build>` plus custom string-table entries `GitShort`, `GitCommit`, `BuildTimestamp` — visible in File Explorer's Properties pane and via `Get-ItemProperty`. The DLL's `OnAttach` log now reads `UnityDumper DLL attached 0.1.0.139 261b0f2 (Release, 2026-05-05T14:00:28Z) backend=IL2CPP pid=…` instead of the old bare `build=139`. UI csprojs (`UnityDumpUI`, `UnityDumpUI.Core`) read the same `build_number.txt` via MSBuild expressions and stamp `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` / `Product` / `Company` / `AssemblyTitle`. New [VersionInfo.cs](../ui/UnityDumpUI.Core/Services/VersionInfo.cs) reads the assembly attributes (AOT-safe) and shortens the `+<full-sha>` SourceRevisionId suffix down to 7 chars; `MainWindowViewModel` writes `DiscreteUI 0.1.0.139+261b0f2 session start` as the first line of every UI session's `ui/init/current.log`. Both the C++ regen + the C# version property only re-evaluate after a clean configure / build, which matches the existing build.ps1 behaviour (it already wipes per-backend build dir + UI bin/obj before each pass — same rule as UE5CEDumper).

- **Log prune on UI startup**: `MainWindowViewModel` constructor now calls `FileLogger.PruneOldDllPidDirectories(keepCount: 10)` once. Each game injection (Pick & inject or proxy auto-load) creates a `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\` tree; over months these accumulate to hundreds of dead folders. Prune sorts numeric-PID-named directories by `LastWriteTimeUtc` desc, deletes everything past the 10 newest, leaves the `ui/` directory and any non-PID names alone. Best-effort — `Directory.Delete` failures (PID still belongs to a running game) are swallowed; init-log records `pruned N stale DLL log directories (kept newest 10)`.
- **PipeClient is recreated on every disconnect**: an empirical workaround stacked on top of the single-instance DLL fix below. Even with `nMaxInstances=1`, the same UI process kept getting routed to a residual ghost instance from the dying game (logs showed PID 57148's pipe server up at 21:12:26 but UI's 12-attempt retry between 21:12:44–21:12:51 never reached it; "client connected" never appeared in PID 57148's pipe log). User reported "close UI and restart fixes it" — and indeed the only thing that empirically clears whatever .NET / kernel pipe-cache prefers the dead instance is a fresh process. Now `MainWindowViewModel` swaps in a brand-new `PipeClient` instance after every disconnect (involuntary or explicit) via `BackendAdapter.ReplacePipe`, so the next Connect runs against a completely fresh .NET pipe stack — same effect as restarting the UI, without the user having to. `BackendAdapter._pipe` made mutable to support the swap; `Browser` / `Inspector` references stay valid because we keep the same `BackendAdapter` instance.
- **DLL pipe server is single-instance now**: changed `CreateNamedPipeW` from `PIPE_UNLIMITED_INSTANCES` to `1` (see [dll/src/shared/PipeServer.cpp](../dll/src/shared/PipeServer.cpp)). With unlimited instances, every crashed/exited game left its instance lingering in the kernel namespace; over a debug session of N game launches, N ghost instances accumulated. Client connects then got routed to whichever ghost the kernel found first — `NamedPipeClientStream.ConnectAsync` succeeded, the first handshake write failed with "Pipe is broken." Restarting the UI worked because the eventual restart wall-clock gave the OS enough time to clean up the ghosts. With single-instance the kernel refuses to create a second one while any previous still exists; `CreateNamedPipeW` returns `ERROR_PIPE_BUSY` and the worker thread's existing 500 ms-sleep retry loop handles it cleanly. The Phase 12b per-PID init mutex still prevents two DLLs in the same process from racing, so single-instance is safe alongside that.
- **Connect retry sized to the proxy's startup window**: `MainWindowViewModel.ConnectAsync` now goes through `ConnectWithRetryAsync` — **12 attempts, 600 ms apart, ~7 s total budget** — on `IOException` / `EndOfStreamException` / `PipeDisconnectedException` from the connect+handshake pair. Belt-and-suspenders alongside the single-instance pipe fix above (handles the proxy's own 5 s backend-detect window before the pipe server is even up). Two parallel things make a re-launched game's pipe unhealthy for several seconds: (a) the dying process's ghost named-pipe instance lingers in the kernel namespace, so `NamedPipeClientStream.ConnectAsync` happily opens it before failing on first IO with "Pipe is broken."; and (b) the proxy DLL's `BackendLoaderThread` polls for the engine module for **up to 5 seconds** before `LoadLibraryW`'ing the backend (the only thing that starts the pipe server — see [dll/src/proxy/loader.cpp:65](../dll/src/proxy/loader.cpp:65)). Connection-bar status surfaces `Connecting… (N/12)` during the loop so the user can see progress. `TimeoutException` (no server reachable at all) is NOT retried — bubbles straight to the user. On exhaustion the wrapped error tells the user to wait a few more seconds and retry rather than just "Pipe is broken."
- **PipeClient disconnect/dispose split**: separated `DisconnectAsync` (re-usable: closes streams, gate semaphore stays alive) from `DisposeAsync` (terminal: closes streams + disposes gate). Old code called `DisposeAsync` on every user-initiated Disconnect, which permanently bricked the gate so the next Connect threw `ObjectDisposedException: System.Threading.SemaphoreSlim`. Latent since Phase 7 — only became reachable when Phase 14a's retry loop started exercising the disconnect/reconnect path. `MainWindowViewModel.DisconnectAsync` and the retry loop now call `DisconnectAsync`; `DisposeAsync` is reserved for app shutdown. Regression covered by [PipeClientLifecycleTests](../ui/UnityDumpUI.Tests/PipeClientLifecycleTests.cs).
- **Error-banner hygiene on disconnect/reconnect**: `HandleInvoluntaryDisconnect` clears `Browser.ErrorMessage` + `Inspector.ErrorMessage` + `ErrorMessage` (the `ConnectionStatus` text already carries the disconnect reason — having a redundant red banner was visual noise). `ConnectAsync` start clears the same three so a retry attempt isn't visually polluted by the previous failure. Fixes the "two stacked Pipe is broken." red banners observed after a game-exit + reconnect cycle.
- **UI exe renamed** to `DiscreteUI.exe` (`AssemblyName` change in csproj, `App.axaml` `avares://` updated to match — Avalonia 12 keys the resource scheme off AssemblyName not RootNamespace). Project directory + RootNamespace stay `UnityDumpUI` to keep the source-gen / `using` tree intact. DLLs (`UnityDumper-*.dll`) keep their existing names.
- **`PipeClient` cancellation race fixed**: previously, a caller cancelling between write and read would leave the response in the pipe buffer for the next caller to misread (`Response id mismatch: expected N, got N-1`). Cancellation now only honoured around the gate wait + final pre-commit check; the actual write+read runs uncancellable so the protocol pairing stays consistent. After read, `ct` is re-checked and `OperationCanceledException` is thrown then.
- **`build.cmd publish`** clean takes ~60–70s. AOT publish stays at **0 warnings, 0 errors** under all four trim/AOT analyzers. `dist/` ships the full set: 2× DLL + `DiscreteUI.exe` + Skia/HarfBuzz/ANGLE side-DLLs + pdbs + 4× `.lua` + `README.md` + `unity_dump.lua` + `build_number.txt`.

---

## Recent commit history (most recent first)

```
9f7c067 ui(deploy): show installed-vs-source version comparison per row
976ebff build: version stamping across DLLs + UI (PE VERSIONINFO + log banners)
261b0f2 ui: recreate PipeClient on every disconnect — empirical workaround
3ef8a0b dll(pipe): single-instance pipe — kill ghost-accumulation reconnect bug
8f41ecb ui: extend connect retry to ~7s to cover proxy DLL startup
694efa3 ui: fix Connect-after-Disconnect throwing ObjectDisposedException
417448b ui: log prune + connect retry + error-banner hygiene
6be6eaf Phase 12c (deploy): UE5CEDumper-style multi-select bulk deploy panel
cf36431 docs: refresh STATUS.md as a clean session-handoff
b52c5b4 Phase 15: test matrix — verified games + tooling versions ledger
05ea52d Phase 12c (auto): Steam scan + Unity game picker for proxy install
47d969d Phase 12c: UI install/uninstall flyout + 32-bit deferral notes
a93706b Phase 12a + 12b: proxy DLLs (version.dll, winmm.dll) + double-inject mutex
375806c inspector: inline value-type substruct expansion + classifier fix
8527f22 Phase 13 (b): metadata-cli — offline global-metadata.dat parser
d813339 Phase 13 (a): split MetadataParser core / host-path adapter
b31e74e docs: STATUS.md reflects Phase 14 closure
8b9485c Phase 14d/e/f: Mono attach fix, SEH+attach rules, log-stats tool
978a926 Phase 14a + 14b: pipe reconnect + stripped-metadata banner
```

This session shipped (in commit order): UE5CEDumper-style multi-select bulk deploy panel → log prune on UI startup + connect retry + error-banner hygiene → Connect-after-Disconnect ObjectDisposedException fix (PipeClient lifecycle split) → connect-retry budget bumped to 7 s → DLL pipe set to single-instance to kill ghost accumulation → fresh PipeClient swap on every disconnect (matches the "restart UI" workaround empirically) → version stamping across every DLL/exe (PE VERSIONINFO + UI/DLL log banners) → proxy-deploy panel surfaces installed-vs-source version comparison.

---

## What's open

### Phases / sub-phases NOT started (deferred)

| Item | Why deferred | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | All 32-bit support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only, which a 32-bit game can't load. | Engineering effort: parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout based on detected target architecture. IL2CPP-32 is moot — Unity 2018+ ships IL2CPP x64-only. |
### Open follow-ups (not blocking)

- **CSX live mode** — investigated, design dead-end as scoped. CSX is fundamentally an offset-relative *structure* format; "live" only makes sense as a snapshot-derived layout (fix array counts to live values, inline pointer-chain targets). CE XML already provides true absolute-address live mode, so the CSX equivalent's value is fuzzy. Defer until a user explicitly asks.
- **Hollow Knight Silksong live walk** — partial. PlayerData walk now decodes correctly: enums (`mapZone : GlobalEnums.MapZone`, `extraRestZone`) come back as ints via the server-side enum-detection + underlying-type decode landed this session; fields render in offset order; instance-switch preserves the user's selected field row. The `unity_symbols.lua` Mono `monoCompileMethod` path is still un-exercised (worthwhile follow-up, would close out the "Mono bdwgc end-to-end" verification).
- **Dissect-script path** (`unity_dissect.lua` → `dissectClass(...)`) hasn't been re-exercised since the CE-API portability fixes (`b8ce152`). Sanity run worth ~5 minutes; not blocking.
- **Inspector / Methods search box** — Fields and Methods grids don't yet have a substring filter like the Assemblies and Classes lists do. Add per-tab `TextBox` bound to a new `Filter` property on `LiveInspectionViewModel`/`AssemblyBrowserViewModel`'s methods state, mirroring the `RefilterAssemblies` / `RefilterClasses` pattern. Open question: does the filter affect "Copy CE XML / CSX" output (filter as a *visibility* control) or stay UI-only (export keeps every field)? The Assembly browser's filter is UI-only — natural to follow the same precedent. Highlight matched substring in a different colour as a polish step.
- **More automated test coverage** — current test suite (`ui/UnityDumpUI.Tests/`) covers protocol, exporters, pipe lifecycle, and a few inspector helpers. Big gaps: classifier (enum vs value-type vs pointer routing — easy unit tests against constructed `WalkedField` payloads), end-to-end `MetadataParser` round-trips per version (already partially covered), CE-XML live-mode rendering against a deterministic fixture instance. Each test reduces "verify by running the game" cycles. Not a single change — ongoing chore tracked here so it doesn't slip.
- **Find method instances (Phase 16 candidate)** — CE has a "find method instances" feature with overload disambiguation; we'd add a wire command `findMethodInstances(assembly, ns, class, methodName, [signatureMatch])` to mirror it. Server side: enumerate matching `MethodInfo*` (Mono / IL2CPP), compile + return native pointers, optionally match by signature for overloads. UI: new tab on the class browser surfacing all overloads with their addresses. Useful for the existing `unity_symbols.lua` flow (currently registers all methods of the class wholesale; per-method targeting is finer-grained). Scope: ~one phase. Not started.

---

## Repo layout (relevant bits)

```
discrete/
├── build.cmd / build.ps1       <- UE5-style wrapper; dist-copy step ships scripts/ + README
├── build_number.txt
├── dist/                       <- gitignored; built artifacts land here
├── docs/
│   ├── plan.md                 <- full phase ledger
│   ├── pipe-protocol.md        <- DLL ↔ UI contract (current: protocol v1; walkArray documented)
│   ├── Archiutecture.md        <- rules / invariants (note: filename typo preserved)
│   └── STATUS.md               <- this file
├── dll/
│   ├── src/main.cpp            <- DllMain auto-runs Init + StartPipe
│   ├── src/shared/
│   │   ├── ExportAPI.h/.cpp    <- C ABI: Init/StartPipe/StopPipe/GetBackendType/GetBuildNumber
│   │   │                          + every pipe handler (readMemory, walkInstance, walkArray, ...)
│   │   ├── MagicStrings.h      <- pipe name + command name constants
│   │   ├── Memory.h            <- SEH-protected SafeReadBytes
│   │   ├── PipeServer.h/.cpp   <- threaded named-pipe server
│   │   └── Logger.h            <- per-PID rotated log files
│   ├── src/mono/               <- MonoResolver/Domain/ClassWalker/ThreadAttach/TypeParser
│   └── src/il2cpp/             <- IL2CPPResolver/Domain/ClassWalker/MetadataParser{,HostPath}/...
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 app (AOT). AssemblyName=DiscreteUI.
│   │   ├── App.axaml           <- avares://DiscreteUI/Resources/Strings/en.axaml
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs           <- top-level; bridges Inspector→Browser base addr
│   │   │   ├── AssemblyBrowserViewModel.cs      <- Phase 8 + Phase 10 export commands
│   │   │   ├── LiveInspectionViewModel.cs       <- Phase 9 find/walk + Phase 11.5 hierarchical tree
│   │   │   └── ProcessPickerViewModel.cs        <- Phase 9.5 picker dialog
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml + .cs           <- 5-pane shell, Inspector tab, export flyout
│   │   │   └── ProcessPickerWindow.axaml + .cs  <- modal picker
│   │   ├── Platform/
│   │   │   ├── WindowsProcessEnumerator.cs      <- module probe → backend detect
│   │   │   └── WindowsDllInjector.cs            <- LibraryImport CreateRemoteThread+LoadLibraryW
│   │   └── Resources/Strings/en.axaml
│   ├── UnityDumpUI.Core/       <- Protocol + services (AOT-safe).
│   │   ├── Constants.cs
│   │   ├── Platform/
│   │   │   ├── IProcessEnumerator.cs            <- + ProcessInfo + UnityBackend enum
│   │   │   └── IDllInjector.cs
│   │   └── Services/
│   │       ├── PipeClient.cs                    <- cancellation race fix lives here
│   │       ├── BackendAdapter.cs                <- typed pipe API; implements IClassInfoLookup + ILiveExportSource
│   │       ├── ProtocolModels.cs                <- every DTO (extend here for new commands)
│   │       ├── ProtocolJson.cs                  <- source-gen JsonContext (register here)
│   │       ├── FileLogger.cs
│   │       └── Export/
│   │           ├── ExportOptions.cs
│   │           ├── IClassInfoLookup.cs          <- + ILiveExportSource (live render contract)
│   │           ├── UnityTypeMap.cs              <- managed type → CE/C type table
│   │           ├── SdkHeaderExporter.cs
│   │           ├── CsxExporter.cs
│   │           └── CeXmlExporter.cs             <- template + live render paths
│   └── UnityDumpUI.Tests/       <- xUnit v3
├── tools/                       <- PowerShell pipe helpers + Phase 13 metadata-cli
│   ├── metadata-cli/            <- Phase 13: offline global-metadata.dat parser
│   ├── Get-LogStats.ps1         <- Phase 14e: per-PID log-dir size/cap report
│   └── Test-*.ps1, *.ps1        <- pipe smoke tests (one-shot RPCs)
└── scripts/                     <- CE Lua bridge (Phase 11)
    ├── README.md                <- install layout + workflow
    ├── utils.lua                <- callDLL, detectBackend, dllPath, log
    ├── unity_dump.lua           <- main entry; injects DLL + StartPipe
    ├── unity_dissect.lua        <- pipe RPC + tiny JSON parser → createStructure
    └── unity_symbols.lua        <- registerSymbol for static fields + IL2CPP methods
```

---

## Known caveats / things to watch (carried forward)

- **AOT analyzers are strict.** Any new JSON serialization path must go through the source-gen `ProtocolJsonContext`. Reflection-based `JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050. Pattern to copy: the `Str(string)` / `Int(int)` helpers in `BackendAdapter.cs`.
- **`LibraryImport` does NOT auto-resolve A/W**. Unlike classic `DllImport` with `CharSet`, every entry point name is taken literally. Use explicit `EntryPoint = "FooW"` for any function that has A/W variants. We learned this the hard way with `GetModuleHandle` (commit `0ca3c0a`).
- **Avalonia 12 clipboard API churn**: `DataObject` / `DataFormats` deprecated, `IClipboard.SetTextAsync` / `SetDataObjectAsync` removed. The only supported writer is `IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text` item in a `DataTransfer` (see `MainWindow.axaml.cs.CopyToClipboardAsync`).
- **Avalonia 12 `avares://` keys off AssemblyName, not RootNamespace.** Renamed the exe? Update `App.axaml`'s `ResourceInclude Source="avares://{AssemblyName}/..."` too. (Caught when renaming to `DiscreteUI`.)
- **`CommunityToolkit.Mvvm` source-gen name collision**: a property named `Backend` collides with the generated field name. The handshake VM uses `Backend2` (see `MainWindowViewModel.cs`).
- **IL2CPP `method_get_pointer`** is not exported in Unity 2021.3+. We read `MethodInfo.methodPointer` directly via SEH-protected struct read. Don't reintroduce the export-only path.
- **`-DBACKEND=` cmake cache poisoning**: cmake hates being switched between `Mono` / `IL2CPP` / `Both` in the same build dir. `build.ps1` wipes the per-backend build dir on every configure for this reason.
- **CJK Windows console** munges em-dashes printed by the script unless we route through `Start-Transcript`. `build.cmd` already does.
- **Game must be closed** before rebuilding the DLL — LNK1104 if the DLL is still mapped from a previous injection.
- **Antivirus** flags freshly-built `DiscreteUI.exe` (lots of LibraryImport native calls + remote thread injection P/Invokes). User has `dist/` excluded in their AV; mention this if a fresh dev hits a quarantine error after `build.cmd publish`.
- **`PipeClient` write+read runs uncancellable past commit.** Don't "fix" this back to passing `ct` everywhere — the race it solves (id-mismatch when caller cancels mid-request) is not theoretical, it broke rapid Find Instances clicks. Caller's `ct` is honoured around the gate wait + before commit + after the read returns.

---

## Quick reference

```powershell
# Full clean build (DLL + UI AOT + tests) -> dist/
.\build.cmd publish clean

# UI only, fast iteration (self-contained single-file in dist\)
.\build.cmd ui

# IL2CPP DLL only
.\build.cmd il2cpp

# Tests
.\build.cmd test
```

Pipe protocol contract (current: protocol v1, includes `walkArray`): `docs/pipe-protocol.md`.
PowerShell smoke tests: `tools/Test-*.ps1` — still useful for one-shot pipe RPCs while iterating on the DLL.
CE-side workflow: `scripts/README.md` — install layout + dofile invocations.

---

## Suggested first steps for the new session

### What's left to ship

Pick from this menu — none are blockers, all are independent:

| Option | Effort | Blocked on | Why pick this |
|---|---|---|---|
| **Phase 12d** — 32-bit Mono support | Real (multi-day) | Engineering only — no external blockers | Required for any pre-Unity-2018 Mono game. User explicitly flagged this in the Phase 12 conversation as wanted but deferred. Inventory of TODOs in the deferred-phases table above. |
| **Phase 14c** — IL2CPP version dispatch (v24/v27/v31) | Medium | Need target games to validate against. Have **Unity 2019.4 v24** locally (SaGa F1R) so v24 path can start now; v27 and v31 still need games | Phase 13's CLI surfaces the gate clearly today. v24 path alone would let `metadata-cli` and `getClassInfo` work against SaGa F1R — concrete value. |
| **Hollow Knight Silksong live walk** | Small (~30 min if game runs) | None | Validates Unity 6 Mono binding + the new `monoCompileMethod` path in `unity_symbols.lua` against a non-SaGa target. Pure verification, no code changes expected. |
| **Dissect-script sanity** | Small (~5 min) | None | Re-run `dofile([[...unity_dissect.lua]])` then `dissectClass("Assembly-CSharp","MainGame","PLY_WORK")` against SaGa F2R to confirm the CE-API portability fix in `b8ce152` didn't regress the Structure Dissect path. |
| **CSX live mode** | Medium, fuzzy value | Design call | See open-follow-ups note. Defer until a user asks. |

### Live verifications

Confirmed clicked-through this session against SaGa F2R / SaGa F1R:

- ✅ **Phase 13 — metadata-cli** against SaGa F2R global-metadata.dat: `info` returns `version=29, type_tables_parsed=true, type_definition_count=10001, string_literal_count=11392`; `stats` reports `verdict=clean, strip_ratio=0.0011`.
- ✅ **Phase 14c — IL2CPP metadata v24 / v27 / v29 / v31** dispatch verified end-to-end against six local games covering all four versions: SaGa F1R (v24, 7,514 types), FFVI PR (v24, 11,438), Townscaper (v27, 8,132 — x86 IL2CPP), SaGa F2R (v29, 10,001), Farthest Frontier (v31, 15,848), Emberward (v31, 23,982). All six leading types match canonical IL tokens (`0x02000001+`). Layout discovery: hex-dumped each typeDefinitions table and located row-2 token at +84 to validate stride.
- ✅ **Phase 14a — pipe auto-disconnect**: game-exit flips status to `Disconnected (write failed: Pipe is broken.)`. Reconnect after re-launching the game lands cleanly (took several iterations to get there — see the four UI commits below).
- ✅ **Connect-after-disconnect cycle in same UI session**: PipeClient lifecycle bug (`ObjectDisposedException: SemaphoreSlim`) closed by [PipeClientLifecycleTests](../ui/UnityDumpUI.Tests/PipeClientLifecycleTests.cs); ghost-pipe routing fixed by single-instance pipe + fresh-PipeClient swap on every disconnect.
- ✅ **Phase 12 — Proxy Deploy panel**: bulk install/uninstall/update-all flow exercised end-to-end on multiple games (Steam scan finds 23, install lands fresh proxy + both backend DLLs, version comparison column flags pre-stamping installs as `OUTDATED` with `unknown → 0.1.0.146` and stamped-but-old as `0.1.0.139 → 0.1.0.146`).

Still owed (not blocking):

- **Phase 11.5 follow-up — value-type substruct expansion**: in the Inspector, expand a `MainGame.PLY_WORK` instance, find an inline value-type field (e.g. a `Vector3` if any are exposed at this layer), click the chevron. Should walk the substruct fields without errors. Right-click → Copy CE XML rooted there should produce a CE table whose components are at consecutive offsets from the struct base.
- **Hollow Knight Silksong live walk**: would validate the Unity 6 Mono bdwgc binding + Phase 11 `unity_symbols.lua` Mono `monoCompileMethod` path against a non-SaGa target.
- **Dissect-script path** (`unity_dissect.lua` → `dissectClass(...)`) hasn't been re-exercised since `b8ce152`. Sanity run worth ~5 minutes.

### How to wrap up the session

If the remaining smoke tests look done, the next session can pick from the deferred-phases menu (**12d** is the only one left now) or the small-but-useful items (Hollow Knight walk / dissect-script sanity / value-type substruct verification).
