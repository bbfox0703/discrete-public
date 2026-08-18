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

## Chrome density (2026-08-18)

Column widths decide how a grid uses its width; this section is the other
half — how much *height* the grid is left with after the tab's toolbars.

### Global: `MainWindow.Styles` overrides FluentTheme's touch sizing

FluentTheme sizes for touch: `TabItem` is **24 pt / 48 px** tall, and
`Button` / `ToggleButton` / `TextBox` / `ComboBox` / `NumericUpDown` /
`CheckBox` / `ToggleSwitch` all sit on a **32 px `MinHeight` floor**. In a
tool whose tabs stack 3–7 toolbar rows above a DataGrid, that was eating
roughly half the grid. The 24 pt tab strip also needed ~1100 px of width on
its own, which is why the tab headers ran out of room at 1080p unless the
Assemblies / Classes panes were collapsed first.

`MainWindow.axaml` opens with a `<Window.Styles>` block that drops those
floors to `MinHeight="0"` and lets `Padding` drive the height (~26 px), and
takes `TabItem` to `FontSize="14"`.

- **Precedence**: `Window.Styles` is applied after `Application.Styles`, so
  these win over FluentTheme. They are *style* values, so any control that
  sets the property **locally** keeps its own — the `FontSize="10"
  MinHeight="20"` row-action buttons inside the grids are unaffected.
- `NumericUpDown` needs three selectors, not one: it delegates its chrome to
  `ButtonSpinner`, whose two `RepeatButton`s carry the floor. Shrink
  `NumericUpDown`, `ButtonSpinner`, and
  `ButtonSpinner /template/ RepeatButton` or the spinner keeps it tall.
- **Scope is deliberate**: the block lives on `MainWindow`, not `App.axaml`.
  The main window is a dense inspection tool; the 16 dialogs are short forms
  that read better at FluentTheme's roomier default. Moving the block to
  `Application.Styles` (after the `FluentTheme` + DataGrid `StyleInclude`, so
  it still wins) would make it app-wide if that trade ever flips.

### Per-tab: what belongs above the grid

Only the **find → walk → read loop** earns a permanent docked row. Everything
else costs 0 px until asked for:

| Pattern | Use when | Cost while closed |
|---|---|---|
| `ToggleButton` on the toolbar + a docked strip bound to its `IsChecked` | a compound editor (several inputs + buttons) | **0 px** |
| `Button.Flyout` + `MenuFlyout` | one-shot actions (hand-offs, exports) | **0 px** |
| `Expander` | content that is genuinely read at a glance | ~34 px header |

Prefer the ToggleButton strip over an `Expander` for anything advanced — a
collapsed Expander still holds its header row open forever.

### Toolbars must be `WrapPanel`, not horizontal `StackPanel`

A horizontal `StackPanel` hands every child its full desired width and runs
off the right edge — the controls are simply unreachable at narrow widths.
A `WrapPanel` wraps instead. `WrapPanel` has no `Spacing`, so give the
children a uniform gap with a scoped style (the pattern predates this and is
also used by the Snapshot filter panel):

```xml
<WrapPanel.Styles>
  <Style Selector="WrapPanel > :is(Control)">
    <Setter Property="Margin" Value="0,0,6,3" />
  </Style>
</WrapPanel.Styles>
```

Fixed-column `Grid`s are acceptable too — they squeeze their `*` column
rather than clipping — but a `Grid` cannot reflow onto a second line.

**A label and its control must be one WrapPanel child, not two.** A
`WrapPanel` breaks between any two children, so a bare `<TextBlock Text="Limit"/>`
followed by a `<NumericUpDown/>` will happily strand the label at the end of one
row and its control at the start of the next. Wrap each such pair (label +
input, or input + its own action button) in an inner
`<StackPanel Orientation="Horizontal" Spacing="4" VerticalAlignment="Center">`
so it wraps as a unit — the StackPanel is the WrapPanel child that gets the
scoped margin, and `Spacing` handles the gap inside.

### Inspector tab — the worked example

It went from **seven** docked rows (~300 px) to **two** (~62 px):

| Was | Now |
|---|---|
| toolbar (11 controls, clipped when narrow) | one `WrapPanel` toolbar |
| filter builder row | behind the `🔍 Filter` toggle |
| field-filter JSON row | behind the `🔍 Filter` toggle |
| auto-refresh row (3 buttons + `= N ms`) | one `ComboBox` on the toolbar → `AutoRefreshPresetIndex` |
| 3-line target / class-ptr / walk-summary stack | one wrapping status line |
| bookmarks row | behind the `★ Bookmarks` toggle |
| related-objects `Expander` (Dock=Top) | behind the `🔗 Related` toggle, Dock=**Bottom** |
| `→ Browser` / `→ Pivot` / `Copy CE XML` buttons | `⋯ Send` `MenuFlyout` |

The reveal toggles are `ShowFilterPanel` / `ShowBookmarkPanel` /
`ShowRelatedPanel` on `LiveInspectionViewModel`.

### The rest of the tabs

The same pass ran across every remaining tab. Docked chrome above the grid,
at rest:

| Tab | Before | After | How |
|---|---|---|---|
| Fields / Methods | 1 row | 1 row | already minimal — a filter box is primary; do **not** hide it behind a reveal |
| Symbol Finder | 2 rows + 2 Expanders + status + busy | 3 rows | assembly picker merged into the search row; both "Auto-find" Expanders → `🎯 Auto fields` / `🎮 Auto methods` toggles; busy bar rides the toolbar; status folded into the result-filter row |
| Address Lookup | 2 rows | 1 row | the backward-scan tunable → `⚙ Scan ▾` flyout |
| Scene | toolbar + busy + status | 1 row | Focus-delay + "Enable runInBackground" → `⏱ Focus fix` toggle; status docked Bottom, gated |
| Value Search | ~8 rows | 3 rows | mode `ToggleSwitch` inlined; Limit / Max / Parallel / Encoding / Match → `⚙ Options` toggle; status + error bands gated |
| Snapshot | ~9 rows | 3 rows | mode toggle inlined; filters + DB store → `🔍 Filters` / `🗄 Store` toggles; every banner gated |
| SPC Query | ~8 rows | 3 rows | mode toggle inlined; 4 stacked banners → 2 mutually-exclusive gated ones; the two static captions deleted |
| Class Pivot | toolbar + 3 banners + caption | 2 rows | 5 of 7 launcher buttons → `🔭 Discover ▾` flyout; 3 banners → 1 gated banner (`HasBanner`) |
| Teleport | ~1700 px, ~2 screens | ~600 px, 1 screen | set-once cards (global hotkeys, keep-foreground) → **collapsed Expanders** with their enable CheckBox hoisted into the header; prose → `ToolTip.Tip` / `PlaceholderText`; unbounded candidate list capped with a `ScrollViewer MaxHeight` |

Teleport is the one place a collapsed `Expander` beats a ToggleButton strip:
the card is configured once per game and its header can carry the enable
CheckBox itself, so no reveal flag is needed in the ViewModel at all.

Reveal-flag properties added by this pass: `ShowGameplayFieldsPanel` /
`ShowGameplayMethodsPanel` (`SymbolFinderViewModel`), `ShowFocusFixPanel`
(`SceneExplorerViewModel`), `ShowScanOptionsPanel` (`ValueSearchViewModel`),
`ShowFiltersPanel` / `ShowStorePanel` (`SnapshotViewModel`). `PivotViewModel`
gained `HasBanner` instead — a computed OR over its three message strings,
because XAML has no OR without a MultiBinding + converter and this project
stays converter-free for AOT. It must be an OR, not a status-only gate: the
"Pick at least 1 snapshot." preflight sets `ErrorMessage` and clears
`StatusMessage` in the same breath.

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

### IME soft-disable on text-field focus

Every input field in the app takes ASCII (hex addresses, AOB patterns,
numbers, class / field / method names, signatures, file paths), so a CJK IME
left in composition mode only gets in the way. `Views/ImeSoftDisable.cs` closes
the Windows IME's open status whenever focus lands on a `TextBox`, dropping it
out of composition so keystrokes flow through as direct alphanumeric input.

- **Soft / one-directional**: we never force the IME off at the OS level and
  never re-open it on focus-out. A user who wants CJK input flips it back on
  manually (Win+Space).
- **One handler per window root**: a bubble `InputElement.GotFocusEvent`
  handler with `handledEventsToo` (so it fires even when a child marks the
  event handled) beats wiring 170+ controls individually. The single `TextBox`
  check also covers `AutoCompleteBox` / `NumericUpDown`, which route focus to an
  inner `TextBox`.
- **Wiring**: `ImeSoftDisable.Attach(window)` is called once per top-level
  window — from `DialogWindowGeometry`'s ctor (every dialog funnels through it)
  and directly from `MainWindow`'s ctor (it manages its own geometry). Any new
  top-level `Window` must add one of those two, or it silently keeps the IME on.
- The IME close is deferred one Background dispatcher tick so it lands *after*
  Avalonia's own Win32 IME association for the same focus event. Ported from
  UE5CEDumper's platform-service IME guard.

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
