# UnityCEDumper — Session Hand-off

**Branch**: `dev` (clean, pushed)
**Last commit**: `fe079b6` (build fix — Native AOT publish + visible cmake/dotnet output).
**Current state**: **Phases 0–15 + 9.5 + 11.5 + 14c all closed** for x64. **Phase 12d (32-bit Mono)** stays deferred (blocked on engineering effort — parallel x86 pipeline + cross-bitness inject).

This session was a long polish + bug-hunt run on top of the prior phases — no new phase numbers, but enough behavioural changes that the inspector / browser are now usable for serious live-game work. Headline fixes:

- IL2CPP generic instantiations (e.g. `MainGame.ClassAry<MainGame.CARRYITEM>`) now expand correctly — name-based `il2cpp_class_from_name` doesn't index concrete instantiations, so the inspector reads the runtime class pointer out of the object header instead.
- Server-side enum detection + decode using the underlying integer type. Silksong's `mapZone : GlobalEnums.MapZone` now displays as `6` instead of an opaque `0x0000000500000006` qword.
- Walked-fields output is sorted by memory offset; columns are user-sortable; row selection survives instance switches; scroll is preserved.
- Inspector has new per-row action buttons (`addr` / `ptr` / `name` / `HEX`) — the first three copy to clipboard, `HEX` hands off to AOBMaker's CE plugin via `\\.\pipe\AOBMakerCEBridge`.
- Substring filter on Fields / Methods / Inspector grids; collapsible Assemblies / Classes panes; Address column; window icon; classes filter ranks exact match > prefix > substring.
- Vendor refactor: `nlohmann/json` is no longer a git submodule; cloned by `sync_vendor.ps1` into `dll/vendor/nlohmann/`. Same script also pulls reference repos (libil2cpp, Il2CppDumper, BepInEx, mono, …) under `vendor/`.
- Build script no longer swallows `cmake` / `dotnet publish` output and no longer reports `Status: SUCCESS` when a sub-step actually failed (PowerShell function return-stream pollution, fixed by piping native commands through `Out-Host`).

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

#### Inspector + browser polish (this session)

- **Generic-type expansion via object header (`klass-from-header`)**: `MainGame.ClassAry<MainGame.CARRYITEM>` (and any other generic instantiation) couldn't be expanded — `il2cpp_class_from_name` doesn't index concrete instantiations, only the open generic definition (`ClassAry\`1`), and the open definition doesn't have per-instantiation field offsets. Wire-side: `walkInstance` now accepts a `klass_from_header_addr` arg that tells the DLL "read the class pointer from `*(uintptr_t*)addr` instead of looking up by name". Sanity-checked via a new SEH-leaf `IL2CPP::ProbeKlassValid` helper before the walker uses it. Inspector tries the header path first for `PointerToClass` / `ArraySlotPointer` kinds; falls back to the legacy assembly/ns/name resolver for `ValueStruct` (no header at the field's storage) and Mono (which ignores the unknown arg). See [LiveInspectionViewModel.LoadPointerChildrenAsync](../ui/UnityDumpUI/ViewModels/LiveInspectionViewModel.cs).
- **Server-side enum detection + decode** (Silksong's `GlobalEnums.MapZone` etc.): added `mono_class_from_mono_type` / `il2cpp_class_from_il2cpp_type` to the resolvers; `ReadClassInfo` now bridges the field's type pointer to a class, then probes `class_is_enum` (Mono) / `class_get_parent.name == "Enum"` (IL2CPP), and reads the `value__` field's underlying type. Wire payload gains `is_enum` / `is_value_type` / `underlying_type`. The walkInstance handler decodes enum fields using the underlying type so an int4 enum comes back as `value: 6`, not `ptr: 0x0000000500000006`. Inspector classifier short-circuits `IsEnum=true` to `Leaf`.
- **walkInstance fields are sorted by offset** before serialisation (DLL-side). Walker emits parent-class fields first, then derived; metadata declaration order doesn't match memory layout, so the post-walk `std::sort` by offset gives the user a monotonic Offset column. Affects auto-refresh and "Copy CE XML" rooted on a walked node too.
- **Sortable Inspector DataGrid columns** with `CanUserSortColumns="True"` + `SortMemberPath="Offset"` (int) so 0x100 sorts after 0x80, not before. Avalonia's `DataGridCollectionView` wrapper sorts the visual layer only — the underlying `Fields` ObservableCollection stays in offset order so the VM's expand / collapse / scroll-into-view logic (which indexes into `Fields` directly) keeps working.
- **Selection + scroll preservation across instance switches**. The inspector's `WalkInstanceAsync` snapshots `SelectedNode.Name` + `DeclaredIn` before the rebuild, then re-finds the matching node in the fresh tree and calls a `ScrollFieldIntoView` callback that posts a `DataGrid.ScrollIntoView` through `Dispatcher.UIThread.Post(..., Background)` so it lands after the new row containers materialise. Latent bug fixed alongside: `OnSelectedInstanceChanged` was eagerly calling `Fields.Clear()` BEFORE `WalkInstanceAsync` ran, which fired `SelectedItem = null` through the TwoWay binding and nulled `SelectedNode` before snapshotting — dropped the eager clear, walk handles it after snapshot.
- **Substring filter on Fields / Methods / Inspector grids** (UI-only — Copy CE XML / `.h` / `.csx` / `.CT` exports keep walking the unfiltered data). Mirrors the existing Assemblies / Classes filter pattern: `FieldFilter` / `MethodFilter` / `Filter` ObservableProperties + `FilteredFields` / `FilteredMethods` ObservableCollection mirrors. The Inspector filter is the trickiest: `Fields` stays the canonical hierarchical tree (since expand/collapse indexes into it), `FilteredFields` is rebuilt from `Fields.CollectionChanged`. **Perf fix on top**: a 1350-field `PlayerData` walk fired CollectionChanged 1350 times during the Add loop, cumulatively ~900k filter iterations + 1350 DataGrid layout passes. Added a `_refilterSuppressionDepth` counter so bulk paths (walk repopulate, expand-children insert, subtree collapse) bracket their loops with `++ / try / -- / RefilterFields()` and refilter once at the end.
- **Per-row action buttons (`addr / ptr / name / HEX`)** in the Inspector. First three copy the relevant string to clipboard. **HEX hands the row's effective address to AOBMaker's Cheat Engine plugin** via a named pipe (`\\.\pipe\AOBMakerCEBridge`) — CE's open Memory View jumps to the address. New [`AobMakerBridge`](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs) in Core: System.IO.Pipes client speaking AOBMaker's wire format (4-byte LE length prefix + UTF-8 JSON, per-request reconnect, source-gen `JsonSerializerContext` for AOT). Fail-soft — pipe-unreachable / timeout / failure all return `false` + log to `ui/view`; rest of inspector keeps working when AOBMaker isn't installed. `WalkedFieldNode` ctor takes a `WalkedFieldActions` bag (Toggle + CopyText + NavigateHex) instead of just the toggle Func.
- **Window icon**: `Assets/MainIcon.ico` is wired both as the PE `<ApplicationIcon>` (File Explorer / taskbar / alt-tab) and as the Avalonia `Window.Icon` on all three windows (`MainWindow`, `ProcessPickerWindow`, `ProxyDeployWindow`) via `avares://DiscreteUI/Assets/MainIcon.ico`. `Assets/**` added to the `AvaloniaResource` glob.
- **Classes filter ranking**: when the user types a query, exact matches come first, then simple-name prefix matches (so typing `PlayerData` lands on the type itself before `PlayerDataBase` / `SetPlayerDataBool`), then the rest of the substring matches, all preserving alphabetical order within each bucket.
- **Assemblies + Classes alphabetically sorted at populate** — backend image-load order is engine-defined, which left `Assembly-CSharp` floating somewhere arbitrary in the list.
- **Collapsible Assemblies / Classes panes**: each pane gets a `◀` / `▶` chevron in its title row; click to collapse to a 28 px rail (just title-letter `A` / `C` + the now-`▶` reopen button), giving the width back to the Inspector / Detail pane. MainWindow code-behind snapshots the live `ColumnDefinition.Width` before collapse so a GridSplitter-resized state survives round trips.
- **New `Address` column** in the Inspector grid (155 px monospace, between Offset and Type). Shows the field's storage address per-instance. Pointer-deref values stay in the Value column; not duplicated.
- **Uninstall-selected fix**: `ProxyDeployViewModel.UninstallSelectedAsync` filtered on `DeployStatus == ProxyDeployStatus.Deployed`, but after the version-stamping work most rows live in `DeployedCurrent` / `DeployedOutdated`. Result: selecting any deployed game and clicking "Uninstall selected" produced "nothing to do (no matching rows)". Extracted an `IsDeployed(row)` helper that returns true for all three deployed states, used in Uninstall, UpdateAll, and the summary counter so the three sites can't drift again.

#### Vendor refactor (this session)

- **`nlohmann/json` no longer a git submodule.** Submodule registration retired; the headers now live at `dll/vendor/nlohmann/` (single `include/` + `single_include/` layout) cloned by [`sync_vendor.ps1`](../sync_vendor.ps1). Reason: every clone of the parent repo was dragging ~150 MB of nlohmann history through `--recurse-submodules`, and contributors kept forgetting the flag. CMake's include path moved accordingly. `.gitmodules` keeps a stub header explaining the switch.
- **`sync_vendor.ps1`** anchors itself to `$PSScriptRoot` (runs from anywhere), uses `--depth 1` on initial clone (history isn't useful for vendoring), `git pull --ff-only` on update, header doc explaining the two buckets:
  * **`dll/vendor/`** — C++ build dependencies (`nlohmann/`, `minihook/`).
  * **`vendor/`** — reference repos kept around for cross-checking IL2CPP struct layouts and looking up Mono runtime exports. **Most useful**: `vendor/libil2cpp/` has Unity's actual IL2CPP runtime source organised as `Unity_<major.minor>/<exact.version>/` — handy for verifying struct layouts (TypeDefinition, Il2CppClass, etc.) for any future v32+ target without empirical hex-dumping. Also: `Il2CppDumper` (C# reference parser), `BepInEx`, `Il2CppAssemblyUnhollower`, `mono`, `UnityExplorer` (mostly background reading; not directly imported).
- Both subtrees added to `.gitignore` so the cloned trees stay out of `git status`.

#### Build script fixes (this session)

- **`build.cmd publish` no longer silently fails.** Two compounding bugs were masking each other:
  1. **XAML AOT compiler rejects `x:Name` on `ColumnDefinition`** (it's not a Control with a NameScope). The collapsible-pane commit added two such names so code-behind could grab them; under `build.cmd ui` (Release single-file) the compiler tolerated it, but under `build.cmd publish` (Native AOT) it failed with `AVLN2000`. Fix: drop the names; code-behind reaches columns via `BodyGrid.ColumnDefinitions[index]` instead.
  2. **build.ps1 swallowed every native command's stdout.** PowerShell collects native-command output (`& cmake ...`, `& dotnet ...`) inside a function into the function's return-value pipeline. Three knock-on effects: (a) the user never sees compiler output — neither in the console nor the transcript log; (b) `return $false` after a failure becomes part of an array `@(...stdout..., $false)` which evaluates as truthy, so the dispatch's `if (-not (Build-Ui)) { $exitCode = 1 }` never trips; (c) final summary therefore reads `Status: SUCCESS` even when a step actually failed — exactly what masked bug #1. Fix: pipe every `& cmake` / `& dotnet` invocation to `| Out-Host`. Out-Host writes to the host display stream (also captured by `Start-Transcript`) and returns nothing, so per-step return values are no longer polluted. Touched: Build-Dll (configure + build), Build-Ui (both publish branches), Run-Tests, Build-MetadataCli.

---

### Carried-forward Phase 12 work

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
fe079b6 fix(build): unbreak Native AOT publish + visible build output
5891b0b vendor + ui: sync_vendor script, collapsible browser panes, address column
3355c52 ui: window icon, per-row action buttons, classes filter ranking, focus fix
88bb880 ui(inspector): batch-suppress refilter so 1350-field walks stay snappy
dfe419d ui: substring filter for Fields / Methods / Inspector tabs (UI-only)
a4e6666 ui: sortable inspector + alpha-sorted browser + scroll preservation
7e3f35d inspector: enum decode + sort by offset (fixes Silksong walk)
fe0f258 inspector: expand pointer-to-class via object header (fixes generics)
e9b4db3 ui(deploy): fix Uninstall selected skipping version-stamped rows
712549e ui(inspector): fix expand on generic types like ClassAry<T>
afc09a6 Phase 14c: IL2CPP metadata v24/v27/v29/v31 dispatch
5d61c8e docs: refresh STATUS.md for end-of-session — open items + verifications
9f7c067 ui(deploy): show installed-vs-source version comparison per row
976ebff build: version stamping across DLLs + UI (PE VERSIONINFO + log banners)
261b0f2 ui: recreate PipeClient on every disconnect — empirical workaround
3ef8a0b dll(pipe): single-instance pipe — kill ghost-accumulation reconnect bug
8f41ecb ui: extend connect retry to ~7s to cover proxy DLL startup
694efa3 ui: fix Connect-after-Disconnect throwing ObjectDisposedException
417448b ui: log prune + connect retry + error-banner hygiene
6be6eaf Phase 12c (deploy): UE5CEDumper-style multi-select bulk deploy panel
b52c5b4 Phase 15: test matrix — verified games + tooling versions ledger
```

This session shipped (in commit order): generic-type expansion (`ClassAry<T>`) via klass-from-header → uninstall-selected fix for version-stamped rows → enum decode + sort-by-offset (fixes Silksong walk) → sortable inspector columns + alpha-sorted browser + scroll preservation → substring filter for Fields / Methods / Inspector → refilter perf fix (1350-field PlayerData) → window icon + per-row addr/ptr/name/HEX buttons + AobMakerBridge + classes filter ranking + focus-after-instance-switch fix → vendor refactor (sync_vendor.ps1, drop nlohmann submodule) + collapsible browser panes + Address column → Native AOT publish unblock (XAML AOT + Out-Host).

---

## What's open

### Phases / sub-phases NOT started (deferred)

| Item | Why deferred | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | All 32-bit support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only, which a 32-bit game can't load. | Engineering effort: parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout based on detected target architecture. IL2CPP-32 is moot — Unity 2018+ ships IL2CPP x64-only. |
### Open follow-ups (not blocking)

- **CSX live mode** — investigated, design dead-end as scoped. CSX is fundamentally an offset-relative *structure* format; "live" only makes sense as a snapshot-derived layout (fix array counts to live values, inline pointer-chain targets). CE XML already provides true absolute-address live mode, so the CSX equivalent's value is fuzzy. Defer until a user explicitly asks.
- ~~**Hollow Knight Silksong live walk** — `PlayerData` walks correctly now: enums (`mapZone : GlobalEnums.MapZone`, `extraRestZone`) come back as ints; fields render in offset order; instance-switch preserves the focused row; column sort works. **Still un-exercised**: the `unity_symbols.lua` Mono `monoCompileMethod` registration path against Unity 6 bdwgc.~~ **Closed.** Re-exercised against Silksong `Assembly-CSharp.GameManager` (10 static fields + 323 method JITs succeeded out of 328 attempted; 5 expected failures for abstracts/generics-without-instantiation). Surfaced and fixed three latent bugs along the way: (1) Lua-side pipe-open race on first RPC after DllMain returns — fixed with a 15× retry loop bracketing `io.open` (3 s budget); (2) CE main-thread starvation under hundreds of back-to-back RPCs — fixed by interleaving `processMessages()` + `sleep(1)` in the rpc helper so Windows can pump messages between calls; (3) symbol-naming convention — `Unity:Asm:Ns.Cls::method` triggered CE's slow Mono symbol fallback resolver and hung the UI for minutes on `getAddress`, renamed to `Unity:Asm:Ns.Cls.m_method` (mirrors the static-field `.s_` form) and updated `Archiutecture.md` accordingly. All three method/static symbols now resolve through the fast local-symbol path: `m_get_GameState → 0x255ABC4F010`, `m_Update → 0x2559D509460`, `s_TIME_PASSES_SCENE_LIMIT → 0x25407796CC0`.
- ~~**Dissect-script path** (`unity_dissect.lua` → `dissectClass(...)`) hasn't been re-exercised since the CE-API portability fixes (`b8ce152`).~~ Re-exercised against SaGa F2R `MainGame/PLY_WORK` (56 fields, IL2CPP); surfaced and fixed a long-standing silent bug — `createStructure` in CE 7.5.x returns a `TDissectedStruct` that is **not** auto-registered, so the script's `Done.` message had been a lie. Patched: scan-and-remove same-label duplicates, then `s.addToGlobalStructureList()` after the field loop. `getStructureCount()` now reports the structure post-build, and it appears in CE's *Structure dissect → Structures* menu.
- **More automated test coverage** — current test suite (`ui/UnityDumpUI.Tests/`) covers protocol, exporters, pipe lifecycle, and a few inspector helpers. Big gaps: classifier (enum vs value-type vs pointer routing — easy unit tests against constructed `WalkedField` payloads with the new `IsEnum` / `IsValueType` flags), end-to-end `MetadataParser` round-trips per version (already partially covered), CE-XML live-mode rendering against a deterministic fixture instance, AobMakerBridge happy + sad-path with a fake pipe server. Each test reduces "verify by running the game" cycles. Not a single change — ongoing chore tracked here so it doesn't slip.
- **Find method instances (Phase 16 candidate)** — CE has a "find method instances" feature with overload disambiguation; we'd add a wire command `findMethodInstances(assembly, ns, class, methodName, [signatureMatch])` to mirror it. Server side: enumerate matching `MethodInfo*` (Mono / IL2CPP), compile + return native pointers, optionally match by signature for overloads. UI: new tab on the class browser surfacing all overloads with their addresses. Useful for the existing `unity_symbols.lua` flow (currently registers all methods of the class wholesale; per-method targeting is finer-grained). Scope: ~one phase. Not started.
- **Address finder / reverse lookup (Phase 17 candidate)** — inverse of `findInstances`: given any address (e.g. one from CE's *Find what writes* / pointer scan), identify which Unity managed instance, class, and field it belongs to. Spec drafted in [docs/address-finder.md](address-finder.md). Strategy A (backward scan from queried address looking for plausible vtable/klass pointers, then resolve field by offset) covers the v1 ad-hoc lookup case in ~512 SEH-protected reads, sub-millisecond. Strategy B (cached heap-walk + interval tree) deferred to v2 if batch lookups become a need. Pipe RPC: `whatIsAt(address)`. UI surface: dedicated *Address Lookup* tab + Inspector right-click action. Scope: ~3.5 days for v1 (DLL handler + UI + Lua bridge `unity_addrfind.lua`). Not started.
- ~~**`vendor/libil2cpp/` cross-check pass** — confirm v24/v27/v29/v31 TypeDefinition struct layouts in `dll/src/il2cpp/MetadataLayout.h` match upstream `Il2CppMetadata.h`.~~ **Done.** Field-by-field cross-check against five Unity reference versions (2019.4.40f1, 2020.3.31f1, 2021.3.39f1, 2022.3.32f1, 6000.0.5f1) — all layouts match byte-for-byte, no code changes needed. Empirical hex-dumping methodology was correct. Two documentation-only deltas surfaced: (1) v24 header uses `XxxCount` field names where v29+ uses `XxxSize` (same byte layout, naming-only rename — our `HeaderV29` reads v24 data correctly); (2) Unity 6 (6000.0.x) reverted to IL2CPP v29 rather than introducing a v32, so v31 dispatch is for Unity 2022.2-2023.x only. Comment in `MetadataLayout.h` updated; full audit captured in [docs/il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md). Future v32+ targets can re-run the same diffing approach against fresh `vendor/libil2cpp/` pulls — no need to re-dump.
- **Highlight matching substring** in the Fields / Methods / Inspector filter results — pure polish on top of the substring filter that landed this session.
- **Inspector classifier unit tests** — easiest concrete test addition: feed `WalkedField` records with `IsEnum=true` / various pointer values into `WalkedFieldNode.FromInstanceField` and assert `Kind`. Catches regressions like the recent enum→ValueStruct misclassification before the user has to launch a game.

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
| **Phase 17 candidate — Address Finder (reverse lookup)** | ~3.5 days | None | New wire command `whatIsAt(address)` that takes any CE-scanned address and returns the containing managed instance + class + field. Inverse of `findInstances`. Closes a long-standing gap: today the user has to manually pointer-chain from a CE write-watch hit back to a managed field. Spec in [docs/address-finder.md](address-finder.md). |
| **Phase 16 candidate — find method instances** | One phase | None | New wire command `findMethodInstances(assembly, ns, class, methodName, [signatureMatch])` to mirror CE's feature. Inventory in the open-follow-ups list above. Useful follow-up to the per-row HEX → AOBMaker pipeline since it gives users a way to navigate to actual native code addresses, not just data. |
| **Phase 12d** — 32-bit Mono support | Real (multi-day) | Engineering only — no external blockers | Required for any pre-Unity-2018 Mono game. Inventory of TODOs in the deferred-phases table above. |
| **Inspector classifier unit tests** | Small | None | Cheapest test win — feed `WalkedField` records into `FromInstanceField`, assert Kind. Catches regressions like the recent enum→ValueStruct misclassification offline. |
| **CSX live mode** | Medium, fuzzy value | Design call | See open-follow-ups note. Defer until a user asks. |

### Live verifications

Confirmed clicked-through against SaGa F2R (IL2CPP v29) and Hollow Knight Silksong (Unity 6 Mono):

- ✅ **Generic-type expansion (`ClassAry<T>`)** — clicking the chevron on `eqpItm: MainGame.ClassAry<MainGame.CARRYITEM>` in SaGa F2R `PLY_WORK` walks the concrete instantiation's fields via the new klass-from-header path. Previously failed with `class not found: MainGame.ClassAry\`3`.
- ✅ **Silksong `PlayerData` walk** — 1350 fields, depth=3 inheritance chain. `mapZone : GlobalEnums.MapZone` shows the int value (e.g. `6`) instead of the catch-all 8-byte ptr. Fields render in offset order. Switching between #11/#12/#13 instances keeps `ProfileID` (offset `0x1F4`) scrolled into view.
- ✅ **DataGrid column sort** — clicking Offset reverses, clicking Name sorts by name. Avalonia's `DataGridCollectionView` sorts the visual layer only; expand/collapse continues to operate on the underlying `Fields` collection.
- ✅ **Substring filter** on Fields / Methods / Inspector — typing `respawn` in the Inspector filter narrows to the respawn-prefixed rows; clearing the filter restores the full view with expand state intact.
- ✅ **Inspector instance-switch perf** — same Silksong session: switching between PlayerData instances dropped from 2-6 s gaps to ~100-300 ms after the refilter-suppression fix.
- ✅ **Per-row buttons** — `addr` / `ptr` / `name` copy correctly. `HEX` (with AOBMaker + CE running) jumps the Memory View to the row's address.
- ✅ **Build script** — `build.cmd publish` now compiles + AOT-publishes in ~67 s; full cmake / dotnet output visible in the transcript log; final `Status: SUCCESS` reflects reality.
- ✅ **Phase 14c — IL2CPP metadata v24 / v27 / v29 / v31** dispatch verified end-to-end against six local games covering all four versions: SaGa F1R (v24, 7,514 types), FFVI PR (v24, 11,438), Townscaper (v27, 8,132 — x86 IL2CPP), SaGa F2R (v29, 10,001), Farthest Frontier (v31, 15,848), Emberward (v31, 23,982). All six leading types match canonical IL tokens (`0x02000001+`).
- ✅ **Phase 13 — metadata-cli** against SaGa F2R global-metadata.dat: `info` returns `version=29, type_tables_parsed=true, type_definition_count=10001, string_literal_count=11392`; `stats` reports `verdict=clean, strip_ratio=0.0011`.
- ✅ **Phase 14a — pipe auto-disconnect**: game-exit flips status to `Disconnected (write failed: Pipe is broken.)`. Reconnect after re-launching the game lands cleanly.
- ✅ **Connect-after-disconnect cycle in same UI session**: PipeClient lifecycle bug closed by [PipeClientLifecycleTests](../ui/UnityDumpUI.Tests/PipeClientLifecycleTests.cs); ghost-pipe routing fixed by single-instance pipe + fresh-PipeClient swap on every disconnect.
- ✅ **Phase 12 — Proxy Deploy panel**: bulk install/uninstall/update-all flow exercised end-to-end. The earlier "Uninstall selected: nothing to do (no matching rows)" bug after version-stamping landed is also closed (`IsDeployed(row)` matches all three deployed states).

- ✅ **`unity_dissect.lua` → `dissectClass(...)`** — clicked through against SaGa F2R `MainGame/PLY_WORK` (IL2CPP, 56 fields). Lua reports `count: 1` after build, structure appears in CE's *Memory View → Ctrl+D → Structures* menu, selectable for live dissect at any instance address. Surfaced + fixed a silent registration bug — see the patched follow-ups list above.
- ✅ **`unity_symbols.lua` → `registerClassSymbols(...)` Mono path** — clicked through against Silksong `Assembly-CSharp.GameManager` (Unity 6 Mono bdwgc, 10 statics + 323 JITs ÷ 328 methods, ~30–60 s walltime). All three symbol kinds (`.s_<field>`, `.m_<method>`) resolve to real addresses post-registration. Surfaced + fixed three bugs (pipe-open race, main-thread starvation, `::` symbol-collision) — see the patched follow-ups above.
- ✅ **Deploy tooling** — `build.cmd deploy` (release: copies dist\) and `build.cmd deploy-scripts` (live: copies scripts\*.lua + dist\*.dll) drive a self-elevating PowerShell installer at `tools\Install-CEScripts.ps1`. Auto-detects CE install via `$env:CE_PATH` → `C:\Program Files\Cheat Engine` → `Program Files (x86)`. UAC-elevates from a non-admin shell when CE is in Program Files. Closes the "manual copy is so bad" iteration friction.

### How to wrap up the session

Next session can pick from the deferred-phases menu (**12d** stays the only formally-deferred phase) or the smaller items in the table above. Two natural next-phase candidates: **Phase 17 (Address Finder)** is the highest-impact pick — closes a structural gap between CE's address-centric scanning and our class-centric inspector, design fully drafted in [address-finder.md](address-finder.md). **Phase 16 (find method instances)** is the natural complement to the per-row HEX → AOBMaker pipeline that landed earlier this round.
