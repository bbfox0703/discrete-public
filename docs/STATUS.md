# UnityCEDumper — Session Hand-off

**Branch**: `dev` (2026-05-29 — **Phase 29d SPC tab live-smoke polish complete**, head `82647cd` / build #682+ UI / DLL #650).

## TL;DR — what's left to pick up (next session, post-29d polish)

Phase 29d shipped earlier this day. User ran the SPC Query tab live and
surfaced six UX issues across three batches of fixes. All shipped +
green on 462/462 unit tests, both branches in sync.

### What landed since the Phase 29d ship (commits 832f471 → 82647cd)

| # | Issue | Fix |
|---|---|---|
| 1 | **Run SPC Query button stayed disabled** with 4 snapshots in the pool | `RelayCommand` `CanExecute` doesn't auto-fire on external collection changes — wired `RunCommand.NotifyCanExecuteChanged()` into the `Snapshots.CollectionChanged` hook |
| 2 | **Operand / Operand 2 cells uneditable on first click** | Avalonia `DataGridTextColumn` needs double-click to enter edit mode. Swapped to `DataGridTemplateColumn` with live `TextBox` so single click works |
| 3 | **Use-column checkbox same trap** — first click selected the row, didn't toggle | Swapped to `DataGridTemplateColumn` with live `CheckBox` |
| 4 | **Columns couldn't be resized** | `CanUserResizeColumns="True"` on both DataGrids. Sort stays off (would scramble per-snap value sequences) |
| 5 | **Class / Type filters were plain TextBoxes** — user couldn't remember exact names | Upgraded to `AutoCompleteBox`. `KnownTypes` seeded with 22 curated managed primitives (Int*/UInt*, Single, Double, Vector*, Color, Quaternion, GameObject, Transform); both lists augment from hydrated snapshots on each Run. Typing `u` surfaces all UInt* on first keystroke |
| 6 | **Silent "0 matches" when Exact predicate had empty Operand** | Preflight in `RunAsync` now validates per-row that required operand(s) are filled. Error names the row index + predicate kind so the user knows where to look |
| 7 | **Snapshot dropdown didn't show which were unsaved** | `SnapshotEnvelope.DisplayName` prefixes `● unsaved · ` when `IsDirty=true`. Stale badge still wins position |
| 8 | **Handshake Expander wasted a row in the right pane** | Moved into a Flyout button `ℹ Handshake` on the top toolbar next to Tools |

### Live-smoke STILL TODO (from Phase 29d, partly verified)

| # | Case | Status |
|---|---|---|
| 1 | **Killer use case literal** — 4 snaps with `10160→9910→9410→9410` SPC signature on Mortal.Core.GameStat | ✅ Verified 2026-05-29 — screenshot showed `Values: 10160 → 10160 → 9910 → 9860` so the engine + UI works end-to-end |
| 2 | Constant-value finder — all `Exact V` | Not yet tried live |
| 3 | Range narrowing — all `Range min..max` | Not yet tried live |
| 4 | Cross-session statics-only auto-mode + banner | Not yet tried live (needs game relaunch between snaps) |
| 5 | Cross-build diff banner | Not yet tried live (needs game version change) |
| 6 | Context menu — right-click result row → Inspect / Copy variants | Not yet tried live |
| 7 | Logs at `…\Logs\ui\snapshot\current.log` show `SpcQuery: snaps=N preds=[...] → M matches in Xms` | Not yet inspected |
| 8 | Auto-prune at 200 MB threshold across many wide snaps | Not yet tried live |
| 9 | Settings dialog DB-directory change → live DB switch | Not yet tried live |

### Next priorities

* Continue live-smoke on the items above; surface any further UX papercuts.
* Phase 29e research stub (Class-centric Key/Value pivot — "for the
  Player instance with name='Bob', show HP across N snaps") still
  documented in plan.md. Waiting on a concrete real-world need before
  designing it.
* Per-key streaming hydrate (constant RAM regardless of N) deferred
  from 29d-4 — not needed until live data hits the practical N>10
  wall.

---

## Previous milestone — Phase 29d SPC ship (same day, earlier)

**Phase 29d SPC-style multi-snapshot constraint query SHIPPED**. New
"SPC Query" tab (MainWindow position 8) answers the user's killer use
case: given N snapshots from the same game build, find every field
whose value sequence walks a specific signature — e.g. `10160 → 9910
→ 9410 → 9410` across 4 sessions uniquely identifies the 銀兩 GameStat
by its real-world spending pattern, ignoring every noise field that
happens to land in plausible ranges. CE's two-snap diff fundamentally
can't do this.

### What shipped

| Sub-phase | What landed |
|---|---|
| **29d-1** | [SpcQueryEngine.cs](../ui/UnityDumpUI/ViewModels/SpcQueryEngine.cs) — `SpcPredicate{Any|Exact|Range|NotEqual|Unchanged|Increased|Decreased|DeltaExact|DeltaRange}` enum + factory helpers. `SpcQuery` (snapshots + predicates + filters) + `SpcMatch` (per-snap value/numeric arrays). `Run()` anchors on smallest FieldIndex, walks intersection, short-circuits on first failing predicate. "Increased by N" / "Decreased by N" intentionally not separate predicates — `DeltaRange [-N,-N]` covers it. 22 unit tests covering every predicate kind, cross-session key collapse, all filters, anchor selection, edge cases. |
| **29d-2** | [SpcQueryViewModel.cs](../ui/UnityDumpUI/ViewModels/SpcQueryViewModel.cs) + new "SPC Query" tab in MainWindow.axaml. Shares Snapshot tab's envelope pool. Per-snapshot row DataGrid (Include + PredicateKind ComboBox + 2 operand TextBoxes), filters (Statics / Synthetic / Numeric / Class / Type), Run button, results DataGrid with per-snap "Values" column rendered via [SpcValueSequenceConverter.cs](../ui/UnityDumpUI/Views/Converters/SpcValueSequenceConverter.cs) (`v0 → v1 → v2`). GridSplitter between picker + results. |
| **29d-3** | Cross-session / cross-build auto-mode (mirrors Phase 29c-5 diff): different `session_id` → auto-enable StaticsOnly + blue banner; different `build_id` → yellow warning. |
| **29d-4** | Non-additive RAM: post-Run, evict envelopes that were hydrated **for this query** (envelopes already loaded — e.g. via Snapshot tab — stay loaded so the user's next diff doesn't re-hydrate). N>5 advisory status line with estimated peak RAM. Real per-key streaming (constant RAM regardless of N) deferred to future optimization. |
| **29d-5** | Result row context menu: Inspect this instance / Copy Class.Field / Copy instance address / Copy value sequence. Uses the standard `CopyToClipboardAsync` delegate that the other tabs are wired through. |
| **29d-6** | 11 ViewModel integration tests in [SpcQueryViewModelTests.cs](../ui/UnityDumpUI.Tests/SpcQueryViewModelTests.cs) — selection sync, preflight rejection of bad predicate-at-index-0, cross-session/build auto-mode, killer-use-case end-to-end, eviction contract, clipboard hooks. Total Phase 29c+d coverage: **457/457 unit tests pass**. |

### Live-smoke TODO (user)

Same DLL as before (no changes); only UI work. Live cases to try:

1. **Killer use case literal** — take 4 snapshots while playing
   활俠傳, spend exact amounts between each. On SPC Query tab,
   select all 4 → predicates `[Exact 10160, DeltaExact -250,
   DeltaExact -500, Unchanged]` → Run. Expect 1 match
   (`Mortal.Core.GameStat._value`).
2. **Constant-value finder** — 4+ snapshots from anywhere → all
   predicates `Exact 10160` (or any number you know). Surfaces every
   field that held that value across all snaps. Often more useful
   for "find this address" than CE's first-scan.
3. **Range narrowing** — `Range 9000..11000` on every snap → narrow
   the field universe down to fields that stay in a sensible range
   for the whole session sequence.
4. **Cross-session statics-only** — pick snapshots from different
   game launches (close + relaunch game). StaticsOnly should
   auto-enable + blue banner should appear.
5. **Context menu** — right-click a result row → Inspect / Copy
   variants. Verify clipboard contents.
6. **Logs** at `%LOCALAPPDATA%\Discrete_UEDumper\Logs\ui\snapshot\current.log`
   should record `SpcQuery: snaps=N preds=[...] → M matches in Xms`
   per Run.

### Post-29d research (Phase 29e)

User noted SPC's current design is field-centric. A class-centric
Key/Value pivot would answer "for the Player instance with
name='Bob', show HP across N sessions" — different question, needs
cross-session instance identity matching. Documented as Phase 29e
research stub in plan.md. Not designed in detail yet — waiting for
a concrete real-world need.

### Previous milestone — Phase 29c SQLite cross-session persistence

---

## TL;DR — what's left to pick up (Phase 29c session, 2026-05-29)

**Phase 29c SQLite cross-session persistence SHIPPED** in 8 sub-phases —
DLL untouched, pure UI feature. Per-game `.db` files live under
`%LOCALAPPDATA%\Discrete_UEDumper\snapshots\` by default (override via
Settings dialog). Filename pattern `{exe_basename}_{build_id[0:8]}.db`
so the user can navigate the folder without checksum-decoding.

### What shipped

| Sub-phase | What landed |
|---|---|
| **29c-1** | [SnapshotStore.cs](../ui/UnityDumpUI.Core/Services/SnapshotStore.cs) — `OpenAsync` / `SaveSnapshotAsync` / `ListSnapshotsAsync` / `LoadClassesAsync` / `DeleteSnapshotAsync` / `GetTotalPayloadBytesAsync` / `ShouldVacuumAsync` / `VacuumAsync` / `PruneAsync`. Hybrid schema (per-class gzipped JSON blob), reuses existing `SnapshotClassEntry` source-gen JsonContext for AOT. Migration runner + WAL + FK cascade. 24 unit tests in [SnapshotStoreTests.cs](../ui/UnityDumpUI.Tests/SnapshotStoreTests.cs). |
| **29c-2** | [UserSettings.cs](../ui/UnityDumpUI.Core/Services/UserSettings.cs) + [UserSettingsStore.cs](../ui/UnityDumpUI.Core/Services/UserSettingsStore.cs) — atomic `.tmp` + `File.Move` write, fallback-to-defaults on corrupt/missing/permission-denied, source-gen JsonContext (`UserSettingsJsonContext`). 16 unit tests in [UserSettingsStoreTests.cs](../ui/UnityDumpUI.Tests/UserSettingsStoreTests.cs). |
| **29c-3** | `SnapshotViewModel.OnGameDbContextAsync(buildId, gameModule)` — opens (or switches) per-game DB on every handshake. Lazy `ListSnapshotsAsync` populates `Snapshots` collection with metadata-only envelopes; class blobs hydrate on first diff via `EnsureLoadedAsync`. Wired from `MainWindowViewModel.ConnectAsync` post-handshake. |
| **29c-4** | 💾 per-envelope `SaveSnapshotCommand` + 🗑 `DeleteSnapshotFromDbCommand`. `IsDirty` / `PersistedId` flags on `SnapshotEnvelope`. Auto-save hook in `TakeSnapshotAsync` honours `UserSettings.AutoSaveSnapshots` toggle. `GetUnsavedDirtySnapshots()` API for the close-time warning. |
| **29c-5** | Diff cross-session semantics. Removed the cross-build block at `SnapshotViewModel.cs:655`. New `DiffSessionMode` enum + auto statics-only filter + `CrossSessionBanner` ObservableProperty + `ForceIncludeInstancesCrossSession` toggle that re-runs Diff on change. Same-build / cross-session → blue info banner; different build → yellow warning. |
| **29c-6** | `SnapshotStore.PruneAsync(thresholdBytes, minRetain)` — deletes oldest until under threshold, always keeps `minRetain` (default 1). `ShouldVacuumAsync` heuristic (`freelist_count / page_count > 0.30`). `SnapshotViewModel.AutoPruneAndMaybeVacuumAsync` chained after every Save + on DB open. |
| **29c-7** | UI: blue DB status line (`DB: filename · N saved · X MB / Y GB`) + 📁 Open folder button (`Process.Start("explorer.exe", path)`) + ⚙ Settings… button. [SnapshotSettingsWindow.axaml](../ui/UnityDumpUI/Views/SnapshotSettingsWindow.axaml) modal with directory picker via `IStorageProvider.OpenFolderPickerAsync`, AutoSave checkbox, AutoPrune NumericUpDown (GB). Cross-session banner row above filter bar. Per-side `💾 Save A / Delete A / Save B / Delete B` row below A/B selection. |
| **29c-8** | 15 integration tests in [SnapshotViewModelPersistenceTests.cs](../ui/UnityDumpUI.Tests/SnapshotViewModelPersistenceTests.cs) covering `OnGameDbContextAsync` round-trip, lazy load, save/delete commands, cross-session diff modes, ApplySettingsAsync persistence + reopen-on-dir-change. Total 421/421 tests pass (was 363; +58 from Phase 29c). `SQLitePCL.Batteries_V2.Init()` wired into `Program.Main`. |

### NuGet additions

- `Microsoft.Data.Sqlite.Core` 10.0.0
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3

Both NativeAOT-safe, no EF Core, no reflection. AOT publish size impact:
~1-1.5 MB.

### Live-smoke TODO (user)

Cannot be tested without a live game — handing off to you:

1. **Fresh DB on first connect** — connect to any Unity game; Snapshot tab
   should show "DB: `{exe}_{build_id_prefix}.db` · 0 saved · 0 MB / 200 MB"
   in the blue status line.
2. **Save round-trip** — take a snapshot, click 💾 Save A. Status flips
   from "💾 not saved" to "✓ saved (X MB)". DB file appears under
   `%LOCALAPPDATA%\Discrete_UEDumper\snapshots\`.
3. **Cross-session diff** — take snapshot, close UI, reopen UI, connect
   to SAME game (must be relaunched too, so session_id differs). The
   prior snapshot reappears in the dropdown. Take a fresh snapshot and
   Diff against it — blue banner "Cross-session diff: instance fields
   suppressed (N rows)." should appear. Toggle "Force include instances"
   → diff re-runs with instance rows included.
4. **Cross-build diff** — connect to game A, save snapshot, then connect
   to game B (different build_id), take a snapshot. The list switches DB;
   user can't directly diff across games (each game's DB is separate).
   Within the same game, upgrading the game executable (changes
   build_id) makes the older snapshot show "⚠ stale (different game)"
   in the dropdown.
5. **Auto-prune** — Settings → AutoPrune 0.1 GB. Take several wide
   snapshots until total > 100 MB. Status line should report
   "Auto-pruned N snapshot(s) (freed X MB)".
6. **Settings dialog** — change DB directory via Browse… → click Save.
   UI should reopen DB under new dir + log line in
   `%LOCALAPPDATA%\Discrete_UEDumper\Logs\ui\snapshot\current.log`.
7. **Logs** — every Phase 29c operation writes to the `snapshot` log
   category. Inspect `%LOCALAPPDATA%\Discrete_UEDumper\Logs\ui\snapshot\current.log`
   after testing.

### Known v1 limitations (deferred, not bugs)

- **Cross-session instance fields suppressed** by default (Q3 decision).
  Toggle `Force include instances` exists but produces add/remove
  noise from rebased addresses. Identity matching (singleton via
  static-root walk, MonoBehaviour via GameObject path) is Phase 29d.
- **Settings dialog uses a separate window**, not inline. Keeps the
  Snapshot tab uncluttered for users not actively configuring.
- **No batch delete UI** — user opens the snapshots folder and deletes
  `.db` files manually if they want to nuke a whole game's history
  (per Q5 decision).

### Previous milestones (archived)

Older session hand-offs (Phase 29b ship, Phase 29 polish bundle,
32-bit snapshot OOM mitigation, snapshot false-positive root-cause
fix at build #664, plus pre-Phase-29 reference matter) are in
[status-history.md](status-history.md). The two TL;DRs above —
Phase 29d + Phase 29c — are the current + previous active
hand-offs.

---

## Repo layout (relevant bits)

```
discrete/
├── build.cmd / build.ps1       <- UE5-style wrapper; dist-copy ships scripts/ + README
├── build_number.txt
├── dist/                       <- gitignored; built artifacts land here
├── docs/
│   ├── plan.md                 <- canonical phase ledger
│   ├── pipe-protocol.md        <- DLL ↔ UI contract (current: protocol v1)
│   ├── Archiutecture.md        <- rules / invariants (note: filename typo preserved)
│   ├── address-finder.md       <- Phase 17 design spec
│   ├── lua-invoke.md           <- Phase 19 design spec
│   ├── il2cpp-metadata-crosscheck.md <- Phase 14c upstream-Unity audit
│   ├── test-matrix.md          <- verified games + tooling versions
│   └── STATUS.md               <- this file
├── dll/
│   ├── src/main.cpp            <- DllMain auto-runs Init + StartPipe
│   ├── src/shared/
│   │   ├── ExportAPI.h/.cpp    <- C ABI + every pipe handler
│   │   ├── AobPattern.h/.cpp   <- Phase 18a-3 hand-rolled x64 LDE
│   │   ├── MethodSignatureFormat.h <- Phase 18 humanSuffix builder (header-only)
│   │   ├── MagicStrings.h      <- pipe + command name constants
│   │   ├── Memory.h            <- SEH-protected SafeReadBytes
│   │   ├── PipeServer.h/.cpp   <- threaded named-pipe server
│   │   └── Logger.h            <- per-PID rotated log files
│   ├── src/mono/               <- MonoResolver/Domain/ClassWalker/AddressFinder/MethodFinder/MethodInvoker/...
│   └── src/il2cpp/             <- IL2CPPResolver/Domain/ClassWalker/AddressFinder/MethodFinder/MethodInvoker/...
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 app (AOT). AssemblyName=DiscreteUI. Win-only stack: Avalonia.Win32 + Avalonia.Skia + Avalonia.HarfBuzz.
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── AssemblyBrowserViewModel.cs
│   │   │   ├── LiveInspectionViewModel.cs
│   │   │   ├── AddressLookupViewModel.cs       <- Phase 17c
│   │   │   ├── MethodOverloadsViewModel.cs     <- Phase 18c
│   │   │   ├── MethodCallViewModel.cs          <- Phase 19d
│   │   │   ├── ProcessPickerViewModel.cs
│   │   │   └── ProxyDeployViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml + .cs
│   │   │   ├── ProcessPickerWindow.axaml + .cs
│   │   │   ├── ProxyDeployWindow.axaml + .cs
│   │   │   ├── MethodOverloadsWindow.axaml + .cs <- Phase 18c
│   │   │   └── MethodCallWindow.axaml + .cs    <- Phase 19d
│   │   └── Resources/Strings/en.axaml
│   ├── UnityDumpUI.Core/       <- Protocol + services (AOT-safe)
│   │   └── Services/
│   │       ├── PipeClient.cs / BackendAdapter.cs / ProtocolModels.cs / ProtocolJson.cs
│   │       ├── AobMakerBridge.cs               <- 3-verb pipe client (Nav + CreateAAScript)
│   │       ├── CallArgFactory.cs               <- Phase 19d (public seam over internal JsonContext)
│   │       └── Export/
│   │           ├── UnityTypeMap.cs
│   │           ├── SdkHeaderExporter.cs / CsxExporter.cs / CeXmlExporter.cs
│   │           └── AobAaScriptGenerator.cs     <- Phase 18c
│   └── UnityDumpUI.Tests/       <- xUnit v3
├── tools/                       <- PowerShell pipe helpers + Phase 13 metadata-cli
│   ├── metadata-cli/            <- Phase 13 offline metadata parser
│   ├── Install-CEScripts.ps1    <- glob-driven deploy to <CE>/discrete/
│   ├── Test-CallMethod.ps1      <- Phase 19 smoke
│   ├── Test-FindMethodInstances.ps1 <- Phase 18 smoke
│   ├── Test-AobPattern.ps1      <- Phase 18a-3 smoke
│   ├── Test-WhatIsAt.ps1        <- Phase 17 smoke
│   └── Test-*.ps1
└── scripts/                     <- CE Lua bridge (deployed to <CE>/discrete/ via build.cmd deploy)
    ├── README.md
    ├── unity_dump.lua / unity_dissect.lua / unity_symbols.lua
    ├── unity_addrfind.lua                      <- Phase 17d
    ├── unity_invoke.lua                        <- Phase 19e
    └── utils.lua
```

---

## Known caveats / things to watch (carried forward + Phase 19 additions)

### Build / DLL
- **`-DBACKEND=` cmake cache poisoning**: cmake hates being switched between `Mono` / `IL2CPP` / `Both` in the same build dir. `build.ps1` wipes the per-backend build dir on every configure for this reason.
- **Game must be closed** before rebuilding the DLL — LNK1104 if the DLL is still mapped from a previous injection.
- **Phase 18 AOB decoder bails on unknown opcode** — falls through with literal bytes (no wildcards). The AOB stays valid, just less version-stable. By design — false positives (over-wildcarding) cause symbol ambiguity, which is the failure mode we must avoid. Bailout is reported via `decoderBailed: true, bailedAt: N` in the wire response.
- **IL2CPP `method_get_pointer`** is not exported in Unity 2021.3+. We read `MethodInfo.methodPointer` directly via SEH-protected struct read. Don't reintroduce the export-only path.

### Phase 19 specific
- **`runtime_invoke` non-uniform marshaling** — value types pass `&value`, reference types pass the pointer **directly** in `params[i]`. Matches BepInEx / MelonLoader / Il2CppInterop. Mono's API docs describe a "uniform" `&pointer` convention for ref types — that's wrong for the actual x64 trampoline. Both backends use the non-uniform form. See `MarshalArg` in `IL2CPPMethodInvoker.cpp` / `MonoMethodInvoker.cpp` for the pattern.
- **`signatureMatch` substring trap** — `"Int32"` is a substring of `"UInt32"`. Default to parens-bounded `"(System.Int32)"` for guaranteed uniqueness when calling `callMethod` / `findMethodInstances`. The smoke scripts and `unity_invoke.lua` examples all use the parens form for this reason.
- **GC-root caveat for `callMethod` returns** — a managed pointer returned by `callMethod` is valid for the next call you make right now. A GC that ran between the address read and the next invoke can have moved the object (Mono moving GC; IL2CPP largely non-moving but not guaranteed). Best practice: chain `findInstances` → `callMethod` in one Lua block when correctness matters.
- **PowerShell `ConvertTo-Json` single-element-array unwrap** — when writing a `Test-*.ps1` smoke script that nests an array as a property value, `[pscustomobject]@{ args = @($oneObj) }` flattens the array to a bare object. Splice the args JSON in as raw text (see `tools/Test-CallMethod.ps1` for the pattern).

### Avalonia / UI
- **Avalonia 12 Windows-only build needs all three pieces wired** — `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` packages, `.UseWin32().UseSkia().UseHarfBuzz()` in `Program.cs`. Missing `.UseHarfBuzz()` throws `InvalidOperationException: No text shaping system configured` from `AppBuilder.Setup()` BEFORE any logging starts — process exits with `0xC0000409`, no log file, no window. Missing `.UseSkia()` produces a silent black window.
- **AOT analyzers are strict.** Any new JSON serialization path must go through the source-gen `ProtocolJsonContext`. Reflection-based `JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050. Pattern to copy for in-Core code: the `Str(string)` / `Int(int)` helpers in `BackendAdapter.cs`. For UI ViewModels (which can't reach the internal context), go through `CallArgFactory` (Phase 19d pattern).
- **`LibraryImport` does NOT auto-resolve A/W**. Unlike classic `DllImport` with `CharSet`, every entry point name is taken literally. Use explicit `EntryPoint = "FooW"` for any function that has A/W variants.
- **Avalonia 12 clipboard API churn**: only supported writer is `IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text` item in a `DataTransfer`.
- **Avalonia 12 `avares://` keys off AssemblyName, not RootNamespace.** Renamed the exe? Update `App.axaml`'s `ResourceInclude Source="avares://{AssemblyName}/..."` too.
- **Avalonia 12 `DataGridCheckBoxColumn`** requires entering edit mode (double-click) before the checkbox responds. Use `DataGridTemplateColumn` with an inline `<CheckBox Focusable="False">` for single-click toggles.
- **`CommunityToolkit.Mvvm` source-gen name collision**: a property named `Backend` collides with the generated field name. Use `Backend2`.
- **`CommunityToolkit.Mvvm` and child-collection CanExecute**: a command's CanExecute is NOT auto-re-evaluated when an item inside an `ObservableCollection<T>` raises PropertyChanged. Subscribe to each row's `PropertyChanged` and call `Notify*Changed()` manually. Pattern: `MethodOverloadsViewModel.OnRowPropertyChanged`.

### Misc
- **CJK Windows console** munges em-dashes printed by the script unless we route through `Start-Transcript`. `build.cmd` already does.
- **Antivirus** flags freshly-built `DiscreteUI.exe` (lots of `LibraryImport` native calls + remote thread injection P/Invokes). User has `dist/` excluded; mention this if a fresh dev hits a quarantine error after `build.cmd publish`.
- **`PipeClient` write+read runs uncancellable past commit.** Don't "fix" this back to passing `ct` everywhere — the race it solves (id-mismatch when caller cancels mid-request) is not theoretical, it broke rapid Find Instances clicks.

---

## Quick reference

```powershell
# Full clean build (DLL + UI AOT + tests) -> dist/
.\build.cmd publish clean

# UI only, fast iteration (self-contained single-file in dist\)
.\build.cmd ui

# IL2CPP / Mono DLL only
.\build.cmd il2cpp
.\build.cmd mono

# Tests
.\build.cmd test

# Deploy bridge scripts + DLLs to <CE>\discrete\
.\build.cmd deploy            # post-build, copies from dist/
.\build.cmd deploy-scripts    # live iteration on Lua, copies from scripts/

# Phase 19 smoke (after injecting via Test-Handshake.ps1):
.\tools\Test-CallMethod.ps1                    # Mathf.Abs(-7) -> 7
.\tools\Test-CallMethod.ps1 -Scenario Throw    # Convert.ToByte(-1) -> OverflowException
.\tools\Test-CallMethod.ps1 -Scenario Float    # Mathf.Abs(-3.5) -> 3.5
.\tools\Test-CallMethod.ps1 -Scenario StringConcat
.\tools\Test-CallMethod.ps1 -Scenario StringParse
.\tools\Test-CallMethod.ps1 -Scenario StringThrow
.\tools\Test-CallMethod.ps1 -Scenario Instance -ClassAssembly <asm> -Name <Cls> -Method ToString -InstanceAddress 0x...

# Phase 18 smoke:
.\tools\Test-FindMethodInstances.ps1 -ClassAssembly <asm> -Namespace <ns> -Name <Cls> -Method <m>
.\tools\Test-AobPattern.ps1 -Address <hex>

# Phase 17 smoke:
.\tools\Test-WhatIsAt.ps1 -ProcessId <pid>
```

Pipe protocol contract: [pipe-protocol.md](pipe-protocol.md) (current: protocol v1 — includes `walkArray` / `whatIsAt` / `findMethodInstances` / `getMethodAobPattern` / `callMethod`).
PowerShell smoke tests: `tools/Test-*.ps1` — still useful for one-shot pipe RPCs without launching the UI.
CE-side workflow: [scripts/README.md](../scripts/README.md) — install layout + `dofile` invocations.
Canonical phase ledger: [plan.md](plan.md). All phase numbers, status flags, and acceptance criteria live there.
