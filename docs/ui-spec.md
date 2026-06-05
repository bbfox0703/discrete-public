# UI Spec — Avalonia 12 AOT App

> Architecture of `DiscreteUI.exe` — Avalonia 12 + Native AOT,
> single-file Windows-only build. ViewModels, windows, services,
> bridges. Pairs with [dll-spec.md](dll-spec.md) (the C++ side this
> UI talks to).

---

## Project layout

```
ui/
├── UnityDumpUI/                   <- Avalonia AOT app (AssemblyName = DiscreteUI)
│   ├── App.axaml{,.cs}            App entry, theme + Inter font
│   ├── Program.cs                 .UseWin32().UseSkia().UseHarfBuzz()
│   ├── Constants.cs               Wire-string constants (mirror of MagicStrings.h)
│   ├── ViewModels/                MVVM viewmodels (CommunityToolkit.Mvvm source-gen)
│   │   ├── MainWindowViewModel.cs
│   │   ├── AssemblyBrowserViewModel.cs
│   │   ├── LiveInspectionViewModel.cs
│   │   ├── AddressLookupViewModel.cs        <- Phase 17c
│   │   ├── MethodOverloadsViewModel.cs      <- Phase 18c
│   │   ├── MethodCallViewModel.cs           <- Phase 19d
│   │   ├── ProcessPickerViewModel.cs
│   │   └── ProxyDeployViewModel.cs
│   ├── Views/
│   │   ├── MainWindow.axaml{,.cs}
│   │   ├── ProcessPickerWindow.axaml{,.cs}
│   │   ├── ProxyDeployWindow.axaml{,.cs}
│   │   ├── MethodOverloadsWindow.axaml{,.cs}    <- Phase 18c
│   │   └── MethodCallWindow.axaml{,.cs}         <- Phase 19d
│   ├── Models/                    UI-only DTOs (e.g. ProxyGameRow)
│   └── Resources/Strings/en.axaml English UI strings (single source)
│
├── UnityDumpUI.Core/              <- Protocol + services (AOT-safe, NO Avalonia deps)
│   ├── Constants.cs               Mirror of UI/Constants.cs (used by services)
│   ├── Services/
│   │   ├── PipeClient.cs          Async newline-JSON over named pipe
│   │   ├── BackendAdapter.cs      Typed RPC wrappers (one method per cmd)
│   │   ├── ProtocolModels.cs      Wire DTOs (request/response shapes)
│   │   ├── ProtocolJson.cs        [JsonSerializable] context (source-gen)
│   │   ├── CallArgFactory.cs      Phase 19d public seam over internal JsonContext
│   │   ├── AobMakerBridge.cs      \\.\pipe\AOBMakerCEBridge client (3 verbs)
│   │   ├── FileLogger.cs          Per-PID rotated UI logs
│   │   ├── VersionInfo.cs         Build number / git hash from assembly attrs
│   │   └── Export/                CE XML / CSX / SDK header / AOB AA-script
│   └── Platform/                  Cross-platform interfaces + Windows impls
│       ├── IDllInjector.cs        + WindowsDllInjector
│       ├── IProcessEnumerator.cs  + WindowsProcessEnumerator
│       ├── IProxyInstaller.cs     + WindowsProxyInstaller
│       └── IUnityGameScanner.cs   + WindowsUnityGameScanner (Steam VDF + Registry)
│
└── UnityDumpUI.Tests/             <- xUnit v3
```

`UnityDumpUI.Core` has **no Avalonia dependency** — every type can
be used from xUnit without spinning up the UI framework. The split
exists for testability.

---

## App lifecycle

```
DiscreteUI.exe starts
   │
   ▼
Mutex single-instance check (only one DiscreteUI.exe at a time)
   │
   ▼
Program.Main:
   AppBuilder.Configure<App>()
       .UseWin32()
       .UseSkia()
       .UseHarfBuzz()
       .WithInterFont()
       .StartWithClassicDesktopLifetime(args)
   │
   ▼
App.OnFrameworkInitializationCompleted:
   new MainWindow()
       └─ new MainWindowViewModel()
              ├─ BackendAdapter (PipeClient)
              ├─ FileLogger
              ├─ AssemblyBrowserViewModel  (Browser)
              ├─ LiveInspectionViewModel   (Inspector)
              ├─ AddressLookupViewModel    (AddressLookup)
              └─ ProxyDeployViewModel      (lazy)
       └─ Wire view-supplied callbacks (clipboard, file picker, etc.)
       └─ DataContext = vm
       │
       ▼
   Window shown
       │
       ▼
   User clicks "Pick & inject…" / "Connect"
       │
       ▼
   InjectAndConnect path:
       • ProcessPickerWindow → ProcessRow
       • WindowsDllInjector.InjectAsync(pid, dllPath)
       • PipeClient.ConnectAsync(MagicStrings.PipeName)
       • BackendAdapter.HandshakeAsync() → fills handshake fields
   │
   ▼
   User browses → ↻ ↻ ↻ → exits → dispose chain
```

### Single instance via Mutex

Standard pattern — `new Mutex(initialOwned: true, name: "Global\\DiscreteUI")`,
release on shutdown. Subsequent launches detect the existing mutex
and exit immediately. No "raise existing window" code yet — the
second launch just no-ops.

---

## Native AOT mandate

Every UI build must publish under Native AOT with **0/0 trim/AOT
analyzer warnings**. This is a load-bearing constraint that drives
several other design decisions:

### No reflection JSON

All wire serialization goes through the source-gen
`ProtocolJsonContext` in [ProtocolJson.cs](../ui/UnityDumpUI.Core/Services/ProtocolJson.cs):

```csharp
[JsonSerializable(typeof(RpcEnvelope<HandshakeResult>))]
[JsonSerializable(typeof(RpcEnvelope<FindMethodInstancesResult>))]
[JsonSerializable(typeof(CallArgRequest))]
[JsonSerializable(typeof(CallMethodResult))]
... etc
internal partial class ProtocolJsonContext : JsonSerializerContext { }
```

Every wire DTO has an entry. New commands need a new entry — the
analyzer flags missing ones at build time.

### Public seam for ViewModels

`UnityDumpUI` (the Avalonia project) can't reach the internal
`ProtocolJsonContext` directly. To build wire payloads, ViewModels
go through [CallArgFactory.cs](../ui/UnityDumpUI.Core/Services/CallArgFactory.cs):

```csharp
public static class CallArgFactory {
    public static CallArgRequest Null();
    public static CallArgRequest Bool(bool v);
    public static CallArgRequest Int(string typeTag, long v);
    public static CallArgRequest UInt(string typeTag, ulong v);
    public static CallArgRequest Float(string typeTag, double v);
    public static CallArgRequest String(string s);
    public static CallArgRequest Address(string hex);
}
```

One method per wire `type` tag. The internal `JsonContext` stays
internal; ViewModels get a clean public surface.

### `LibraryImport` (not `DllImport`) for new P/Invokes

`LibraryImport` is the AOT-friendly source-generated P/Invoke. Note
that unlike classic `DllImport` with `CharSet`, `LibraryImport` does
NOT auto-resolve A/W variants — every entry-point name is taken
literally. Use explicit `EntryPoint = "FooW"`.

### CommunityToolkit.Mvvm only

`[ObservableProperty]` / `[RelayCommand]` source generators — no
hand-rolled `INotifyPropertyChanged`. Compiled XAML bindings only
(`x:DataType=...` on every binding). No reflection-based binding
fallback would work under AOT.

---

## ViewModel organization

`MainWindowViewModel` is the root — it owns everything else and
exposes them as public properties for binding:

```
MainWindowViewModel
├── BackendAdapter (Core.Services.BackendAdapter)
├── Logger (Core.Services.FileLogger)
├── ProcessEnumerator (Platform.IProcessEnumerator)
├── SettingsStore (Core.Services.UserSettingsStore)        ─ Phase 29c
├── Browser       : AssemblyBrowserViewModel
├── Inspector     : LiveInspectionViewModel
├── AddressLookup : AddressLookupViewModel                 ─ Phase 17
├── SymbolFinder  : SymbolFinderViewModel                  ─ Phase 25
├── ValueSearch   : ValueSearchViewModel                   ─ Phase 27
├── Scene         : SceneExplorerViewModel                 ─ Phase 20
├── Snapshot      : SnapshotViewModel                      ─ Phase 29a/b/c
├── SpcQuery      : SpcQueryViewModel                      ─ Phase 29d
└── (lazy) ProxyDeploy : ProxyDeployViewModel
```

Each VM owns its tab in the right pane of MainWindow (zero-based
indices declared as `MainWindowViewModel.TabIndex*` constants — see
[lessons-learned.md](lessons-learned.md) for the silent-mis-routing
trap when XAML order drifts from these constants).

The Snapshot + SpcQuery pair share state intentionally: SpcQuery
takes a `SnapshotViewModel` in its constructor so saves / drops /
DB-switches in either tab propagate to the other through the
shared `Snapshots` ObservableCollection.

### Cross-VM communication

VMs **don't know about each other** — they communicate through
events on the parent (`MainWindowViewModel`):

- `Inspector.InspectInstanceRequested` event → `MainWindowViewModel`
  switches to Address Lookup tab + pre-fills the address.
- `AddressLookup.InspectInstanceRequested` event → switches to
  Inspector tab + loads the instance.
- `Browser.SelectedClass` change → handled by `MainWindowViewModel`
  via a property listener (no event), feeds into "Inspect selected
  class" command on the Inspector.

Phase 19d-2 added one more cross-VM seam: when the Methods grid
opens `MethodOverloadsWindow` / `MethodCallWindow`, MainWindow's
code-behind threads `Inspector.SelectedInstance.Address` through as
the default `this` — but only when the Browser's selected class
matches the Inspector's target class (strict ordinal equality).
See `GetDefaultMethodCallInstance` in [MainWindow.axaml.cs](../ui/UnityDumpUI/Views/MainWindow.axaml.cs).

### View-supplied callbacks

Window code-behinds wire functions that VMs need but can't construct
themselves (clipboard, file picker, scroll-into-view):

```csharp
// MainWindow.axaml.cs
vm.PickProcessAsync          = ShowPickerDialogAsync;
vm.Browser.SaveFileAsync     = SaveFileAsync;
vm.Browser.CopyToClipboardAsync       = CopyToClipboardAsync;
vm.Inspector.CopyToClipboardAsync     = CopyToClipboardAsync;
vm.Inspector.ScrollFieldIntoView      = ScrollInspectorFieldIntoView;
vm.AddressLookup.CopyToClipboardAsync = CopyToClipboardAsync;
vm.ShowProxyDeploy                    = ShowProxyDeployWindow;
```

Keeps VMs free of `Avalonia.Controls.Window` / `IClipboard` /
`StorageProvider` types — they can be unit-tested without an
Avalonia context.

---

## DataGrid column widths (recurring trap — see CLAUDE.md rule)

Read-only **result / data** grids must size columns with `Width="Auto"`
(or a fixed px width) plus a `MinWidth`, and **never** `Width="*"` / `"2*"`.

Why: a star column makes the DataGrid redistribute the *fixed window width*
across its columns, so the total can never exceed the window — to widen one
column the user must first shrink its neighbours (exactly the friction
reported repeatedly). With `Auto` + `MinWidth`, each column sizes to its
content (or its floor), the row's total may exceed the window, and the grid
shows a horizontal scrollbar (Avalonia default). Widening any column then
just widens the row; nothing else moves.

- ✅ `Width="Auto" MinWidth="200"` / `Width="120"` — independent resize, scrolls.
- ❌ `Width="*"` / `Width="2*"` — forces fit-to-window, couples columns.
- **Exception**: small editable input / selection forms with a fixed handful
  of columns (`MethodCallWindow` param-value cell; the SPC-Query snapshot
  picker) may star-fill — there's no overflow to solve there.

Fixed by build #864 across the result grids: FieldReferencesWindow,
FieldAcrossInstancesWindow, MethodOverloadsWindow, ProcessPickerWindow,
ProxyDeployWindow. The Browser Fields/Methods grids already followed this.

Also keep `MinWidth` itself modest — it's a floor, not a target. With
`Width="Auto"` the column already grows to content, so an oversized `MinWidth`
just forces dead whitespace (and pushes later columns off-screen needlessly).
The Inspector grid's Name/Type/Value floors were trimmed (220/160/320 →
120/110/140) for this reason (build #864).

---

## Windows

| Window | Modal? | Purpose |
|---|---|---|
| `MainWindow` | — | Main shell. Tabbed right pane: Fields / Inspector / Methods / Symbol Finder / Address Lookup / Scene / Value Search / Snapshot / SPC Query. |
| `ProcessPickerWindow` | Yes | Pick a Unity process to inject into. Shown via `vm.PickProcessAsync`. |
| `ProxyDeployWindow` | No (top-level) | Bulk install/uninstall version.dll/winmm.dll across the user's Steam library. |
| `MethodOverloadsWindow` | Yes | Phase 18c — list every overload of a method, multi-select for AOB AA-script generation. |
| `MethodCallWindow` | Yes | Phase 19d — invoke a single method overload with args + see result / exception. |
| `SnapshotSettingsWindow` | Yes | Phase 29c-7 — configure snapshot DB directory (with `IStorageProvider` folder picker), AutoSave-on-capture toggle, AutoPrune threshold (GB). Returns the updated `UserSettings` via `Close(result)`; caller passes it to `SnapshotViewModel.ApplySettingsAsync` which persists + may reopen the DB. |
| `CaptureWindow` | No (modeless) | 2026-06-05 — snapshot capture configuration (caps / scope / array-element fields / decode budget / targets / static) + `Take Snapshot` + indeterminate progress + `Cancel capture` (`CancelCaptureCommand`). Opened by the tab's `📸 New snapshot…` button (`OnNewSnapshotClicked`, single-instance: re-`Activate()` if already open). **Binds the SHARED `MainWindowViewModel.Snapshot` instance** (not a fresh VM) so captured snapshots flow into the tab's A/B dropdowns and progress mirrors on the tab; closable mid-capture (the capture keeps running). |

All windows share an icon (`avares://DiscreteUI/Assets/MainIcon.ico`)
and the Inter font set up in `App.axaml`.

> **Layout principle (codified 2026-06-05):** "config/action → window;
> result/analysis → on the tab with max space." Set-once configuration and
> occasional actions (capture setup, method invoke, settings) belong in
> buttons-that-open-windows so the primary result surfaces (diff / inspector /
> value-search grids) keep full height. This is why the Snapshot capture block
> moved off the tab into `CaptureWindow`. Filter rows use `WrapPanel` (which has
> no `Spacing` — give children a margin via a scoped `Style`) so they wrap
> instead of overflowing at high DPI.

### Snapshot tab (Phase 29a-c)

Three responsibilities layered on one tab:

1. **Capture** (Phase 29a): `Take Snapshot` runs the DLL-side
   `snapshotBegin` → `snapshotChunk` loop, builds a local
   `SnapshotEnvelope` with `Classes` + lazy `FieldIndex` for diff.
   Cap NumericUpDowns (instance / array) honour auto-clamp on
   x86 targets to avoid the 32-bit OOM trap.
2. **Diff** (Phase 29b): pick A and B, hit "Diff A → B" → walks
   FieldIndex intersection, classifies each row Changed /
   Increased / Decreased / OtherChanged / Added / Removed /
   Unchanged. Result DataGrid with collapsible filter Expander.
   Cross-session diff auto-applies Statics-only mode with a
   blue banner; cross-build adds a yellow warning.
3. **Persistence** (Phase 29c): per-game SQLite under
   `%LOCALAPPDATA%\Discrete_Dumper\snapshots\` (overridable in
   Settings). Filename `{exe_basename}_{build_id[0:8]}.db`. 💾 Save
   button per envelope; 🗑 Delete-from-DB removes both memory + disk.
   Auto-eviction after Diff drops every persisted envelope NOT
   currently in A/B back to lazy metadata-only. Auto-prune keeps
   the DB under threshold (default 200 MB).

### SPC Query tab (Phase 29d)

Cross-snapshot constraint queries. UI shape:

```
┌── Status / Error / Mode banner ────────────────────────────────┐
│ "Cross-session SPC query — Statics-only auto-enabled..."       │
├── Filter Expander (Statics / Synthetic / Numeric / Class /Type)┤
├── ▶ Run SPC Query                                              │
├── Snapshots + per-snapshot predicate (DataGrid, editable) ─────┤
│ ☑ [0] before-quest      Any        —           —               │
│ ☑ [1] after-shop1       DeltaExact -250        —               │
│ ☑ [2] after-shop2       DeltaExact -500        —               │
│ ☑ [3] after-sleep       Unchanged  —           —               │
├── GridSplitter ────────────────────────────────────────────────┤
│ Results (DataGrid, read-only, double-tap → Inspector handoff)  │
│ Class                    Field   Type    Instance  Values      │
│ Mortal.Core.GameStat     _value  Int32   static    10160→9910… │
└────────────────────────────────────────────────────────────────┘
```

Predicates: absolute (`Any` / `Exact` / `Range` / `NotEqual`) +
relative-to-previous (`Unchanged` / `Increased` / `Decreased` /
`DeltaExact` / `DeltaRange`). Predicate at index 0 must be absolute
(no previous snap to compare against — engine enforces).

Engine is `SpcQueryEngine.Run(SpcQuery)` in
[SpcQueryEngine.cs](../ui/UnityDumpUI/ViewModels/SpcQueryEngine.cs).
Pure CPU + heap; selected envelopes must already be hydrated via
`SnapshotViewModel.EnsureLoadedAsync` (the VM does this in `RunAsync`
before calling the engine). After the engine returns, the VM evicts
any envelope it itself hydrated for the query, leaving "already
loaded by user" envelopes untouched.

### `avares://` URIs key off `AssemblyName`

When the AssemblyName is `DiscreteUI` (as it is — set in the .csproj),
all `avares://` URIs in XAML must use `DiscreteUI` as the assembly
prefix, NOT `UnityDumpUI` (the C# namespace / project name). Forget
this and `App.axaml`'s `ResourceInclude Source=` resolves to null
at runtime with cryptic resource-not-found errors.

---

## Services

### `BackendAdapter`

Typed RPC wrapper over `PipeClient`. One method per pipe command:

```csharp
public Task<HandshakeResult> HandshakeAsync(CancellationToken ct);
public Task<GetAssembliesResult> GetAssembliesAsync(...);
public Task<GetClassInfoResult> GetClassInfoAsync(...);
public Task<FindInstancesResult> FindInstancesAsync(...);
public Task<WhatIsAtResult> WhatIsAtAsync(...);                       // Phase 17
public Task<FindMethodInstancesResult> FindMethodInstancesAsync(...); // Phase 18
public Task<GetMethodAobPatternResult> GetMethodAobPatternAsync(...); // Phase 18
public Task<CallMethodResult> CallMethodAsync(...);                   // Phase 19
... etc
```

Builds the request body via the AOT-safe source-gen context, sends
through `PipeClient`, deserializes the response, returns the typed
DTO.

### `PipeClient`

Manages the named-pipe connection lifecycle:

- `ConnectAsync(pipeName, ct)` — opens the pipe, optionally retries
  while the server isn't ready yet.
- `SendAsync<TReq, TResp>(envelope, ct)` — write JSON + read newline
  + parse — **uncancellable past commit** (see
  [lessons-learned.md](lessons-learned.md#pipeclient-write--read-is-intentionally-uncancellable-past-commit)).
- `DisposeAsync` — closes the pipe.

Auto-disconnect: when the host process dies, the next read fails
with `BrokenPipeException`, which surfaces in the UI status bar and
clears connection state.

### `AobMakerBridge`

Phase 18c — a 3-verb client for the AOBMaker CE plugin's pipe
(`\\.\pipe\AOBMakerCEBridge`):

- `NavigateHexViewAsync(addr, ct)` — open Memory View at address.
- `NavigateDisassemblerAsync(addr, ct)` — open Disassembler at
  address.
- `CreateAAScriptAsync(description, script, autoActivate)` — push an
  AA script into CE's address list as a memory record.

All three are **fail-soft** — when CE isn't running or the AOBMaker
plugin isn't loaded, calls return false / log a warning, never
throw. The UI wires this once at startup and shares the same
instance across all VMs.

### `FileLogger`

Mirrors the DLL's per-PID rotated log layout, but for the UI:
`%LOCALAPPDATA%\Discrete_Dumper\Logs\<UI-pid>\view\`. Channels: `view`
(VM-side events). The DLL writes to `init` / `scan` / `metadata` /
`pipe` / `walk` from inside the host process.

---

## Tests

[xUnit v3](../ui/UnityDumpUI.Tests) — runs against `UnityDumpUI.Core`
only (no Avalonia init). Current coverage:

- `ProtocolJsonContextTests` — round-trip every wire DTO through the
  source-gen serializer, catch shape regressions.
- `CeXmlExporterTests` / `CsxExporterTests` / `SdkHeaderExporterTests`
  — golden-output regression on a deterministic class fixture.
- `PipeClientLifecycleTests` — connect / disconnect / cancellation
  with an in-proc fake pipe server.

Run via `.\build.cmd test`.

Gaps documented in [CONTRIBUTING.md §"Testing"](../CONTRIBUTING.md#testing):
classifier kind-routing, end-to-end `MetadataParser` round-trips,
CE-XML live-mode rendering, AobMakerBridge happy + sad paths.

---

## Avalonia 12 specifics

Heavy detail in [lessons-learned.md §"Avalonia 12 Windows-only build"](lessons-learned.md#avalonia-12-windows-only-build).
Highlights:

- All three of `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz`
  must be wired (`UseWin32().UseSkia().UseHarfBuzz()`). Missing
  HarfBuzz exits the process with `0xC0000409` before logging
  starts.
- Clipboard API is `IClipboard.SetDataAsync(IAsyncDataTransfer)` —
  no other writer surface available.
- `DataGridCheckBoxColumn` requires double-click to enter edit mode
  before responding. Use `DataGridTemplateColumn` with inline
  `<CheckBox Focusable="False">` for single-click toggles.
- `CommunityToolkit.Mvvm`: a property name that collides with the
  generated `_field2` slot needs a `2` suffix on the property too
  (`Backend2`).
- `[RelayCommand(CanExecute = ...)]` on a parent VM is NOT
  re-evaluated when items inside `ObservableCollection<T>` raise
  `PropertyChanged`. Subscribe to each row's `PropertyChanged` and
  call `Notify*Changed()` manually.

---

## Source map

| File | What |
|---|---|
| [Program.cs](../ui/UnityDumpUI/Program.cs) | AOT entry — `.UseWin32().UseSkia().UseHarfBuzz()` |
| [App.axaml{,.cs}](../ui/UnityDumpUI/App.axaml) | Theme + Inter font + DI seed |
| [MainWindow.axaml{,.cs}](../ui/UnityDumpUI/Views/MainWindow.axaml) | Main shell, all four tabs |
| [BackendAdapter.cs](../ui/UnityDumpUI.Core/Services/BackendAdapter.cs) | Typed RPC wrappers |
| [PipeClient.cs](../ui/UnityDumpUI.Core/Services/PipeClient.cs) | Async pipe lifecycle |
| [ProtocolModels.cs](../ui/UnityDumpUI.Core/Services/ProtocolModels.cs) | Wire DTOs |
| [ProtocolJson.cs](../ui/UnityDumpUI.Core/Services/ProtocolJson.cs) | Source-gen JsonContext |
| [CallArgFactory.cs](../ui/UnityDumpUI.Core/Services/CallArgFactory.cs) | Phase 19d public seam |
| [AobMakerBridge.cs](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs) | CE AOBMaker pipe client |
| [Resources/Strings/en.axaml](../ui/UnityDumpUI/Resources/Strings/en.axaml) | All UI strings (single source) |

## Related

- [dll-spec.md](dll-spec.md) — the C++ DLL this UI talks to.
- [pipe-protocol.md](pipe-protocol.md) — the wire contract.
- [export-formats.md](export-formats.md) — what the export commands
  emit.
- [lessons-learned.md](lessons-learned.md) — Avalonia 12 + AOT
  gotchas.
