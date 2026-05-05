# UnityCEDumper — Session Hand-off

**Branch**: `dev`
**Current phase**: **Phase 13 closed** — `metadata-cli.exe` ships in `dist/` (offline `global-metadata.dat` parser). Subcommands: `info` / `stats` / `types` / `type <ns> <name>`. Reuses `MetadataParser` from the DLL tree via a split — backend-agnostic core in `MetadataParser.cpp`, host-process singleton (`Get()`/`Load()`) in `MetadataParserHostPath.cpp` only. CLI links the core + Logger, no `BackendDetector` pull-in. Phases 0–11.5 + 9.5 + 13 + 14 all closed.

> Companion files in this folder: `plan.md` (full phase ledger), `pipe-protocol.md` (DLL ↔ UI contract), `Archiutecture.md` (rules / invariants).

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
| 14 — Hardening | ✅ | **14a** auto-disconnect on pipe teardown: `PipeClient` raises a `Disconnected` event when next read fails (IO / null); `MainWindowViewModel` runs the same teardown the explicit Disconnect does, tagged `(game exited)`, so Connect / Pick & inject re-enable automatically. Previously: target exit broke every subsequent RPC, only recovery was UI restart. **14b** stripped-metadata banner: opportunistic post-handshake `getMetadataStats` call surfaces `partial_strip` / `heavy_strip` verdicts as a yellow banner, so users hitting an obfuscated build see the warning before they spend an hour wondering why every class is `a`/`b`/`c`. **14d** Mono thread-attach audit: every direct `r.<runtime_fn>` call in pipe handlers reviewed; the only gap (walkArray's Mono element-class lookup running un-attached) fixed by adding a guard around the bare resolver calls (single attach, after `Find*` returns — not nested, per build #54). **14e** log rotation: code review confirmed `RotateIfNeeded` is correct (4 files × 8 MB caps); shipped `tools/Get-LogStats.ps1` for post-session spot checks. **14f** SEH-leaf audit: every `__try` body in `dll/src/` verified leaf-shape (one fn call or one memcpy). Both rules (SEH-leaf + Mono attach policy) now codified in `Archiutecture.md`'s "Rules (MUST follow)". **14c IL2CPP version dispatch (v24/27/31)** deferred — needs Unity 2017 / 2018 / 2023 target games to validate against. |

### Repo / build tweaks landed alongside

- **UI exe renamed** to `DiscreteUI.exe` (`AssemblyName` change in csproj, `App.axaml` `avares://` updated to match — Avalonia 12 keys the resource scheme off AssemblyName not RootNamespace). Project directory + RootNamespace stay `UnityDumpUI` to keep the source-gen / `using` tree intact. DLLs (`UnityDumper-*.dll`) keep their existing names.
- **`PipeClient` cancellation race fixed**: previously, a caller cancelling between write and read would leave the response in the pipe buffer for the next caller to misread (`Response id mismatch: expected N, got N-1`). Cancellation now only honoured around the gate wait + final pre-commit check; the actual write+read runs uncancellable so the protocol pairing stays consistent. After read, `ct` is re-checked and `OperationCanceledException` is thrown then.
- **`build.cmd publish`** clean takes ~60–70s. AOT publish stays at **0 warnings, 0 errors** under all four trim/AOT analyzers. `dist/` ships the full set: 2× DLL + `DiscreteUI.exe` + Skia/HarfBuzz/ANGLE side-DLLs + pdbs + 4× `.lua` + `README.md` + `unity_dump.lua` + `build_number.txt`.

---

## Recent commit history (most recent first)

```
8b9485c Phase 14d/e/f: Mono attach fix, SEH+attach rules, log-stats tool
978a926 Phase 14a + 14b: pipe reconnect + stripped-metadata banner
87add44 scripts(symbols): Mono method registration via monoCompileMethod
4626c08 fix(inject): resolve DLL via Environment.ProcessPath, not BaseDirectory
2f9f3c4 Phase 11.5: inline drilldown in Inspector field grid
1a11f9a fix(export): CE leaf flags type-aware (signed Int32 not hex)
b8ce152 fix(scripts): CE-API portability — detection, injection, symbol resolve
2d0641d fix(scripts): unity_dump.lua loses script-dir when launched via dofile()
3b287c5 Phase 11: CE Lua bridge — inject + dissect + register-symbols scripts
a8fa2d1 fix(inspector): Find instances button no-op when target unset
97288f3 ui: rename executable to DiscreteUI.exe (matches repo name)
61f9a7e fix(export): collapse not honoured on root + live-mode group nodes
7f591da fix(export): primitive array slots emitted as 8 Bytes instead of element type
9759bea Phase 10 (f): live-data CE XML export — real per-element addresses
7a2e424 fix(pipe): id-mismatch race when caller cancels mid-request
fd49342 Phase 10 (e): walkArray element-class metadata via runtime lookup
de50449 Phase 10 (d): walkArray pipe command + Inspector UI for live array drilldown
4f4efcc fix(export): export buttons stayed disabled (CanExecute race)
296c415 Phase 10 (c): Copy CE XML / Copy CE field clipboard commands
d50d180 fix(export): bridge Inspector.SelectedInstance -> Browser.CeXmlBaseAddress
2fea770 Phase 10 (b): nested CSX + CE XML exporter + tunable export options
```

---

## What's open

### Phase 11 / 11.5 follow-ups (optional)

- **Dissect-script path** (`unity_dissect.lua` → `dissectClass("Assembly-CSharp", "MainGame", "PLY_WORK")`) hasn't been re-exercised since the CE-API portability fixes — worth a single sanity run before the next session leans on it heavily, but not blocking.
- **Inspector value-type substruct expansion** — Phase 11.5 only expands pointer-to-class and array fields. Inline value-type struct fields (e.g. `Vector3`) still render as opaque values; expanding them would need a `walkInstance(parentAddr + offset, structType)` call without the managed-object header (DLL-side currently expects a header). Defer until a user actually asks.

### Phases not started

| Phase | Scope sketch |
|---|---|
| 12 — Proxy DLLs | `version.dll` / `winmm.dll` / `dxgi.dll` proxy that auto-loads the dumper on game start. Useful for games where runtime injection is awkward. New `dll/src/proxy/` tree + `.def` forwarders + UI deploy panel. |
| 14c — IL2CPP version dispatch (deferred from Phase 14) | Per-version `Il2CppGlobalMetadataHeader` layouts for v24 (Unity 2017) / v27 (Unity 2018-19) / v29 (current) / v31 (Unity 2023+). Currently hardcoded to v29. Now visibly impacts Phase 13's CLI (returns `type_tables_parsed: false` on non-v29 input) — extra motivation. Needs Unity 2017 / 2018 / 2023 target games to validate against. |
| 15 — Test matrix | Game/Engine combinations + CE version compat table in `plan.md`. |

### Open follow-ups (not blocking any phase)

- **CSX live mode** — CE XML got the live-render treatment (real per-element addresses + walkArray-driven children); CSX still emits structural placeholders. CSX `Element` addressing is relative to slot start (no absolute mode), so it's a different design problem. Worth doing once a user actually asks.
- **Inspector value-type substruct expansion** — Phase 11.5 expands pointer-to-class and array fields. Inline value-type struct fields (e.g. `Vector3`) still render as opaque values. Would need a DLL-side header-less `walkInstance` variant (current path expects a managed object header).

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

1. **Pick the next phase** (or open follow-up):
   - **Phase 12 (proxy DLLs)** — `version.dll` / `winmm.dll` / `dxgi.dll` proxy that auto-loads the dumper on game start. Useful for games where runtime injection is awkward.
   - **Phase 15 (test matrix)** — game/engine combinations + CE version compat table in `plan.md`. Phase 13's CLI gives a clean way to enumerate samples (run `metadata-cli info` against every game backup you have).
   - **Phase 14c (deferred)** — Phase 13 made the gap visible (non-v29 metadata returns `type_tables_parsed: false`). Worth doing once a Unity 2017 / 2018 / 2023 target shows up.
   - **Open follow-ups** for quick wins:
     - CSX live mode (CE XML already has it; CSX `Element` addressing is relative to slot start so it's a different design problem)
     - Inspector value-type substruct expansion (Phase 11.5 deferred this; would need DLL-side header-less walkInstance variant)

2. **Live verification of Phase 14**: kill SaGa F2R while `DiscreteUI.exe` is connected — should flip to `Disconnected (game exited)` and re-enable Connect / Pick & inject. Re-launch + re-inject should work without restarting the UI. Then run `tools/Get-LogStats.ps1` to confirm log dirs are within caps.

3. **Live verification of Phase 13**: with SaGa F2R installed, run
   ```
   dist\metadata-cli.exe info  "<...>\SaGa Frontier 2 Remastered_Data\il2cpp_data\Metadata\global-metadata.dat"
   dist\metadata-cli.exe types "<...>\SaGa Frontier 2 Remastered_Data\il2cpp_data\Metadata\global-metadata.dat" | findstr "MainGame.PLY_WORK"
   ```
   Should report `version=29`, `type_tables_parsed=true`, and the type listing should include `MainGame.PLY_WORK` — matching what the live DLL's `getClassInfo` returned during Phase 9.

4. **Optional sanity check**: re-exercise `unity_dissect.lua` (`dissectClass("Assembly-CSharp", "MainGame", "PLY_WORK")`) — hasn't been run since the CE-API portability fixes in `b8ce152`.
