# UnityCEDumper — Session Hand-off

**Branch**: `dev` (clean, pushed)
**Current state**: **Phases 0–15 + 9.5 + 11.5 all closed** for x64. Two phases stay deferred and have NOT been started: **Phase 12d (32-bit Mono)** and **Phase 14c (IL2CPP version dispatch v24/v27/v31)**. Phase 12d is blocked on engineering effort (parallel x86 build pipeline + cross-bitness inject); Phase 14c is blocked on having Unity 2017 / 2018 / 2023 target games to validate against. Last commit: `b52c5b4` (Phase 15 test matrix).

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
| 14 — Hardening | ✅ | **14a** auto-disconnect on pipe teardown: `PipeClient` raises a `Disconnected` event when next read fails (IO / null); `MainWindowViewModel` runs the same teardown the explicit Disconnect does, tagged `(game exited)`, so Connect / Pick & inject re-enable automatically. Previously: target exit broke every subsequent RPC, only recovery was UI restart. **14b** stripped-metadata banner: opportunistic post-handshake `getMetadataStats` call surfaces `partial_strip` / `heavy_strip` verdicts as a yellow banner, so users hitting an obfuscated build see the warning before they spend an hour wondering why every class is `a`/`b`/`c`. **14d** Mono thread-attach audit: every direct `r.<runtime_fn>` call in pipe handlers reviewed; the only gap (walkArray's Mono element-class lookup running un-attached) fixed by adding a guard around the bare resolver calls (single attach, after `Find*` returns — not nested, per build #54). **14e** log rotation: code review confirmed `RotateIfNeeded` is correct (4 files × 8 MB caps); shipped `tools/Get-LogStats.ps1` for post-session spot checks. **14f** SEH-leaf audit: every `__try` body in `dll/src/` verified leaf-shape (one fn call or one memcpy). Both rules (SEH-leaf + Mono attach policy) now codified in `Archiutecture.md`'s "Rules (MUST follow)". **14c IL2CPP version dispatch (v24/27/31)** deferred — needs Unity 2017 / 2018 / 2023 target games to validate against. |

### Repo / build tweaks landed alongside

- **UI exe renamed** to `DiscreteUI.exe` (`AssemblyName` change in csproj, `App.axaml` `avares://` updated to match — Avalonia 12 keys the resource scheme off AssemblyName not RootNamespace). Project directory + RootNamespace stay `UnityDumpUI` to keep the source-gen / `using` tree intact. DLLs (`UnityDumper-*.dll`) keep their existing names.
- **`PipeClient` cancellation race fixed**: previously, a caller cancelling between write and read would leave the response in the pipe buffer for the next caller to misread (`Response id mismatch: expected N, got N-1`). Cancellation now only honoured around the gate wait + final pre-commit check; the actual write+read runs uncancellable so the protocol pairing stays consistent. After read, `ct` is re-checked and `OperationCanceledException` is thrown then.
- **`build.cmd publish`** clean takes ~60–70s. AOT publish stays at **0 warnings, 0 errors** under all four trim/AOT analyzers. `dist/` ships the full set: 2× DLL + `DiscreteUI.exe` + Skia/HarfBuzz/ANGLE side-DLLs + pdbs + 4× `.lua` + `README.md` + `unity_dump.lua` + `build_number.txt`.

---

## Recent commit history (most recent first)

```
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
87add44 scripts(symbols): Mono method registration via monoCompileMethod
4626c08 fix(inject): resolve DLL via Environment.ProcessPath, not BaseDirectory
2f9f3c4 Phase 11.5: inline drilldown in Inspector field grid
1a11f9a fix(export): CE leaf flags type-aware (signed Int32 not hex)
b8ce152 fix(scripts): CE-API portability — detection, injection, symbol resolve
2d0641d fix(scripts): unity_dump.lua loses script-dir when launched via dofile()
3b287c5 Phase 11: CE Lua bridge — inject + dissect + register-symbols scripts
a8fa2d1 fix(inspector): Find instances button no-op when target unset
97288f3 ui: rename executable to DiscreteUI.exe (matches repo name)
```

This session shipped (in commit order): Phase 14 hardening (a/b/d/e/f) → Mono symbol JIT'ing → Phase 13 offline CLI → inspector value-type substructs → Phase 12 (proxy DLLs + double-inject mutex + UI install flyout + Steam auto-scan picker) → Phase 15 test matrix.

---

## What's open

### Phases / sub-phases NOT started (deferred)

| Item | Why deferred | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | All 32-bit support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only, which a 32-bit game can't load. | Engineering effort: parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout based on detected target architecture. IL2CPP-32 is moot — Unity 2018+ ships IL2CPP x64-only. |
| **Phase 14c — IL2CPP version dispatch (v24/v27/v31)** | `MetadataParser` is hardcoded for v29. Older / newer files load (prefix tables work), but type tables aren't parsed — `metadata-cli info` reports `type_tables_parsed: false` and surfaces a stderr hint. | Need Unity 2017 (v24), 2018-19 (v27), and 2023+ (v31) IL2CPP target games to validate per-version `Il2CppGlobalMetadataHeader` layouts against. Have Unity 2019.4 IL2CPP locally (SaGa Frontier 1 Remastered) which gives v24 — could start there. |

### Open follow-ups (not blocking)

- **CSX live mode** — investigated, design dead-end as scoped. CSX is fundamentally an offset-relative *structure* format; "live" only makes sense as a snapshot-derived layout (fix array counts to live values, inline pointer-chain targets). CE XML already provides true absolute-address live mode, so the CSX equivalent's value is fuzzy. Defer until a user explicitly asks.
- **Hollow Knight Silksong live walk** — listed in [test-matrix.md](test-matrix.md). Would validate the Unity 6 Mono bdwgc binding plus the Phase 11 `unity_symbols.lua` Mono `monoCompileMethod` path. Quick win if the user has time to run the game.
- **Dissect-script path** (`unity_dissect.lua` → `dissectClass(...)`) hasn't been re-exercised since the CE-API portability fixes (`b8ce152`). Sanity run worth ~5 minutes; not blocking.

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

### Live verifications still owed against SaGa F2R

These are paths that landed THIS session but haven't been clicked through with the game running. None are blocking; just smoke tests if the user has 5 minutes:

1. **Phase 14a — pipe auto-disconnect**: kill SaGa F2R while `DiscreteUI.exe` is connected. Status should flip to `Disconnected (game exited)`; Connect / Pick & inject should re-enable. Re-launch + re-inject should work without restarting the UI. Run `tools/Get-LogStats.ps1` afterwards to confirm log caps.

2. **Phase 13 — metadata-cli against SaGa F2R**:
   ```pwsh
   .\dist\metadata-cli.exe info  "<...>\SaGa Frontier 2 Remastered_Data\il2cpp_data\Metadata\global-metadata.dat"
   .\dist\metadata-cli.exe types "<...>\SaGa Frontier 2 Remastered_Data\il2cpp_data\Metadata\global-metadata.dat" | findstr "MainGame.PLY_WORK"
   ```
   Should report `version=29`, `type_tables_parsed=true`, and the type listing should include `MainGame.PLY_WORK` — matching what the live DLL's `getClassInfo` returned during Phase 9.

3. **Phase 12 — proxy DLL install**: open `DiscreteUI.exe` → click `Proxy Deploy…` on the connection bar. The new bulk panel opens with a Steam scan already running. Tick SaGa F2R's row → Install selected (default kind: version.dll) → Launch the game — proxy should auto-start the pipe (no `Pick & inject` needed). Connect → handshake should succeed. Tick the row again → Uninstall selected to clean up. Bonus smoke test for `Update all deployed`: install proxies into 2-3 games, then click `Update all deployed` and confirm each row's "Last operation" column reads "Installed …" without you re-selecting anything.

4. **Phase 11.5 follow-up — value-type substruct expansion**: in the Inspector, expand a `MainGame.PLY_WORK` instance, find an inline value-type field (e.g. a `Vector3` if any are exposed at this layer), click the chevron. Should walk the substruct fields without errors. Right-click → Copy CE XML rooted there should produce a CE table whose components are at consecutive offsets from the struct base.

### How to wrap up the session

If everything above looks done and the live verifications pass, the next session can pick from the deferred-phases menu (12d / 14c) or the small-but-useful items (Hollow Knight walk / dissect-script sanity).
