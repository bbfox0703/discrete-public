# UnityCEDumper — Session Hand-off

**Branch**: `dev`
**Current phase**: **Phase 11 in flight** — CE Lua bridge scripts shipped. Phases 9.5 / 10 closed (UI-side exports + clipboard variants verified against SaGa F2R; live-data CE XML mode walks real array elements via the new `walkArray` pipe command + element-class metadata helpers). UI exe renamed to `DiscreteUI.exe` to match the repo. Next on the plan ledger: Phase 12 (proxy DLLs) or Phase 13 (offline metadata CLI).

> Companion files in this folder: `plan.md` (full phase ledger), `pipe-protocol.md` (DLL ↔ UI contract), `Archiutecture.md` (rules / invariants).

---

## What's done

| Phase | Status | Highlights |
|---|---|---|
| 0 — Bootstrap | ✅ | repo skeleton, build scripts, single-instance mutex |
| 1 — DLL skeleton | ✅ | named pipe `\\.\pipe\UnityDumpBfx`, JSON-RPC dispatcher, SEH-leaf helpers |
| 2 — Pipe protocol v1 | ✅ | line-delimited JSON, error codes, `ping` / `handshake` / `scan` |
| 3 — Mono backend | ✅ | `MonoResolver` (23/23 exports), 7 introspection commands |
| 4 — IL2CPP backend | ✅ | required/optional split, runtime walker, method-pointer fallback |
| 5 — IL2CPP metadata deep parse | ✅ | `MetadataParser` for v29, `Il2CppMetadataRegistration` AOB scan, type-aware FieldDefault decode (incl. ECMA-335 §II.23.2 sentinels), strip/obfuscation heuristic |
| 6 — Live memory + walking | ✅ | `readMemory` / `writeMemory` / `resolvePointer` / `findInstances` / `walkInstance` / symbol registry / `monoGetVTable` / `getEnumValues` |
| 7 — UI skeleton | ✅ | Avalonia 12 + Native AOT, `PipeClient`, `BackendAdapter`, `FileLogger`, `WindowsProcessEnumerator`, 3-pane shell with handshake card |
| 8 — UI panels (tree & browse) | ✅ | `AssemblyBrowserViewModel`, 5-column shell, virtualized assembly + class lists, fields/methods DataGrids, substring filter, **show inherited members** toggle |
| 11 — CE Lua bridge | 🟡 | `scripts/utils.lua` (callDLL helper, backend module probe, DLL path resolver), `scripts/unity_dump.lua` (the main entry point — detect Mono / IL2CPP, `loadLibrary` the matching `UnityDumper-{Backend}.dll` from `<CE>/discrete/`, call `UnityDumper_StartPipe`, banner with build #), `scripts/unity_dissect.lua` (open the named pipe directly via `io.open([[\\.\pipe\UnityDumpBfx]], "r+b")`, parse `getClassInfo` JSON via a tiny purpose-built parser, build a CE `Structure Dissect` entry with mapped types), `scripts/unity_symbols.lua` (batch-register CE symbols for static fields + IL2CPP method addresses following Architecture.md naming convention). README + scripts ship in `dist/` so users only need that folder, not the repo. Pending live verification inside CE itself. |
| 10 — Export formats (UI-side) | ✅ | `UnityTypeMap` (Unity managed type → CE / C type, with `ExtractElementClass` for generics + `IsCollectionType` heuristic). Three exporters under [ui/UnityDumpUI.Core/Services/Export/](ui/UnityDumpUI.Core/Services/Export/): `SdkHeaderExporter` (`#pragma pack(push,1)` struct + offset comments), `CsxExporter` (CE Structure Dissect XML with nested `<Structure>` for pointer drilldown), `CeXmlExporter` (CE cheat-table hierarchy with `GroupHeader=1` + `Offsets=[0]` deref + `<Options moHideChildren=1/>` collapse). Five UI surfaces share one `ExportOptions` knob set (depth / arrayLimit / collapse / baseAddress) plumbed through an `Export options ▾` flyout: Export .h / .csx / .CT to file, Copy CE XML to clipboard, Copy CE field via right-click. `IClassInfoLookup` lets exporters recurse via `BackendAdapter`; soft-fails to opaque pointers on pipe error. Avalonia 12 clipboard caveat: `IClipboard.SetDataAsync(IAsyncDataTransfer)` is the only writer (legacy `DataObject` / `SetTextAsync` are gone). Open follow-up: `walkInstance` doesn't yet expose live array/list element addresses (UE5CEDumper's `ArrayElements` / `MapElements`), so collection drilldown emits structural placeholders rather than per-element live data — needs a DLL extension to deliver. |
| 9.5 — In-app process picker + injector | ✅ | `IProcessEnumerator.ListUnityProcesses()` filters by detected Mono/IL2CPP modules; `IDllInjector` is an AOT-friendly C# port of `tools/Inject-Dll.ps1` using `LibraryImport` source-gen P/Invokes (caveat: `LibraryImport` needs explicit `EntryPoint=GetModuleHandleW` — does not auto-resolve A/W like classic `DllImport`). New `ProcessPickerWindow` modal + "Pick & inject…" button on the connection bar replaces `Test-Handshake.ps1` for the launch-and-attach workflow. |
| 9 — Live inspection | ✅ | Phase-9 protocol DTOs (`FindInstancesResult` / `WalkInstanceResult` / `GetStaticFieldDataResult` / `ReadMemoryResult`), `BackendAdapter` typed methods, `LiveInspectionViewModel` (find → walk cascade, `PeriodicTimer` auto-refresh, static-field bridge), Inspector tab + right-click "Inspect" on the class list. **Nested-type support** in DLL (Mono + IL2CPP): classes surface as `Outer/Inner`, `FindClass` walks via `class_get_nested_types`. **Verified live**: `MainGame/PLY_WORK` decode matches a CE reference table value-for-value (build #68, SaGa F2R). DataGrids gained resize/reorder/sort; panes split by `GridSplitter`s; long names get hover tooltips. |

### Phase 8 specifics (just landed)

- **Protocol DTOs** in `ui/UnityDumpUI.Core/Services/ProtocolModels.cs`:
  `GetAssembliesResult`, `GetClassesResult`, `GetClassInfoResult`,
  `AssemblyEntry`, `ClassEntry`, `FieldEntry`, `MethodEntry`, `ClassRef` —
  all wired through `ProtocolJsonContext` for source-gen serialization.
- **`BackendAdapter`** gained `GetAssembliesAsync` / `GetClassesAsync` /
  `GetClassInfoAsync`. AOT-safe `Str()` helper serializes string args via
  `ProtocolJsonContext.Default.String` — the reflection-based
  `SerializeToElement<T>` overload trips IL2026 / IL3050.
- **`AssemblyBrowserViewModel`** (`ui/UnityDumpUI/ViewModels/`) coordinates
  the assembly → class → fields+methods cascade. Per-list substring filter,
  cancellation tokens drop superseded fetches, `ShowInherited` walks the
  parent chain (depth cap 32) via repeated `getClassInfo` and merges
  ancestor members oldest-first; every row carries a `Declared in` column.
- **`MainWindow.axaml`** is a 5-column body: Assemblies | Classes | Detail.
  Detail stacks a collapsible Handshake Expander, error banners, the
  selected class header + parent chain, the inheritance toggle, and a
  `TabControl` with Fields / Methods DataGrids. `App.axaml` now includes
  `Avalonia.Controls.DataGrid`'s Fluent theme.
- **`MainWindowViewModel`** auto-kicks `RefreshAssembliesCommand` after
  handshake and wipes browser state on Disconnect.

### Build infra (also just landed)

- **`build.cmd`** — UE5CEDumper-style argument parser. Examples: `build`,
  `build debug`, `build publish clean`, `build mono`, `build il2cpp`,
  `build ui`, `build test`, `build --help`. Logs to `build_log.txt` via
  `Start-Transcript` so the cmd→PowerShell encoding boundary doesn't
  mangle CJK output.
- **`build.ps1`** — every shippable artifact lands in `dist\` regardless
  of target:
  - DLL build copies `*.dll` + `*.pdb` from
    `build\dll-<backend>-<cfg>\bin\` to `dist\`. Per-backend build dir is
    always wiped before cmake configure to dodge cache poisoning across
    `-DBACKEND=` swaps.
  - UI Publish: AOT publish to `build\ui-publish\`, then exe + Skia /
    HarfBuzz / ANGLE side-DLLs + pdbs into `dist\`.
  - UI Debug/Release: self-contained single-file publish, exe (+ pdb in
    Debug) into `dist\`.
  - `build_number.txt`, `scripts\*.lua`, `scripts\*.CT` pulled alongside.
  - End-of-run banner lists every file in `dist\` with its size.
- **`.gitignore`** now excludes `/dist/` and `/build_log.txt`.

### Acceptance verification

- AOT publish: `dotnet publish -c Release -r win-x64 -p:PublishAot=true`
  finishes with **0 warnings, 0 errors** under all four trim/AOT
  analyzers.
- Full build via `build.cmd publish clean` (~60 s) produces in `dist\`:
  - `UnityDumper-IL2CPP.dll` (419 KB), `UnityDumper-Mono.dll` (388 KB)
  - `UnityDumpUI.exe` (43.2 MB Native AOT)
  - `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`,
    pdbs, `build_number.txt`, `unity_dump.lua`

---

## What's next — Phase 9: Live Inspection

From `plan.md` lines 261–269:

- [ ] `InstanceFinderPanel` — scan heap / pointer scan for instances of a class
- [ ] `LiveWalkerPanel` — read instance, render fields as live values, click-to-expand pointers
- [ ] `StaticFieldPanel` — viewer for static field block, type-aware decoding (string, array, list)
- [ ] Refresh / auto-refresh interval

**Acceptance**: pick `PlayerController.health` in a known test game, see live value update as gameplay changes it.

### Suggested first steps for the new session

1. Extend `ProtocolModels.cs` with DTOs for `findInstances`, `walkInstance`,
   `getStaticFieldData`, `readMemory`. Wire each into `ProtocolJsonContext`.
2. Extend `BackendAdapter` with the matching typed methods.
3. New ViewModel: `LiveInspectionViewModel` (or split into
   `InstanceFinderViewModel` + `LiveWalkerViewModel`). Trigger from a
   right-click on a class in the existing class list (cleanest UX
   integration with Phase 8).
4. New panel(s) under `ui/UnityDumpUI/Views/`. Reuse the DataGrid pattern
   from Phase 8. Auto-refresh via `DispatcherTimer`; user-configurable
   interval (off / 250 ms / 1 s).
5. Test target: any IL2CPP game where we can reach a known live value via
   `findInstances` → `walkInstance`. SaGa F2R / BongoCat are the
   already-validated test rigs (see `plan.md` hand-off notes).

---

## Repo layout (relevant bits)

```
discrete/
├── build.cmd / build.ps1       <- new UE5-style wrapper (commit 66240c4)
├── build_number.txt
├── dist/                       <- ignored; built artifacts land here
├── docs/
│   ├── plan.md                 <- full phase ledger
│   ├── pipe-protocol.md        <- DLL ↔ UI contract (current: protocol v1)
│   ├── Archiutecture.md        <- rules / invariants (note: typo in name)
│   └── STATUS.md               <- this file
├── dll/                        <- C++20 injected DLL (Mono + IL2CPP variants)
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 app (AOT)
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   └── AssemblyBrowserViewModel.cs   <- new in Phase 8
│   │   ├── Views/MainWindow.axaml
│   │   ├── Resources/Strings/en.axaml
│   │   └── Platform/WindowsProcessEnumerator.cs
│   ├── UnityDumpUI.Core/       <- protocol + services (cross-platform-shaped)
│   │   └── Services/
│   │       ├── PipeClient.cs
│   │       ├── BackendAdapter.cs   <- extend here for Phase 9
│   │       ├── ProtocolModels.cs   <- extend here for Phase 9
│   │       ├── ProtocolJson.cs     <- add JsonSerializable entries here
│   │       └── FileLogger.cs
│   └── UnityDumpUI.Tests/      <- xUnit v3
├── tools/                      <- PowerShell pipe test helpers
└── scripts/                    <- CE Lua bridge (Phase 11)
```

---

## Known caveats / things to watch

- **AOT analyzers are strict.** Any new JSON serialization path must go
  through the source-gen `ProtocolJsonContext`. Reflection-based
  `JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050.
  Pattern to copy: the `Str(string)` helper in `BackendAdapter.cs`.
- **`CommunityToolkit.Mvvm` source-gen name collision**: a property named
  `Backend` collides with the generated field name. The handshake VM uses
  `Backend2` to dodge it (see `MainWindowViewModel.cs`).
- **`IL2CPP method_get_pointer`** is not exported in Unity 2021.3+. We
  read `MethodInfo.methodPointer` directly via SEH-protected struct read
  (commit `b3dd16c`). Don't reintroduce the export-only path.
- **`-DBACKEND=` cache poisoning**: cmake's cache hates being switched
  between `Mono` / `IL2CPP` / `Both` in the same build dir. `build.ps1`
  wipes the per-backend build dir on every configure for this reason.
- **CJK Windows console** munges em-dashes printed by the script
  unless we route through `Start-Transcript`. `build.cmd` already does.
- **Game must be closed** before rebuilding the DLL — LNK1104 if the DLL
  is still mapped from a previous injection.

---

## Quick reference

```powershell
# Run a clean full build (DLL + UI AOT + tests) -> dist/
.\build.cmd publish clean

# UI only, fast iteration (self-contained single-file in dist\)
.\build.cmd ui

# IL2CPP DLL only
.\build.cmd il2cpp

# Tests
.\build.cmd test
```

Pipe commands documented in `docs/pipe-protocol.md`. PowerShell smoke tests
under `tools/Test-*.ps1`.
