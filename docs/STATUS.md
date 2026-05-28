# UnityCEDumper — Session Hand-off

**Branch**: `main` (Phase 29e merged) — **Phase 29e COMPLETE (29e-1 … 29e-4) + 29e-5/6/7/8/9 polish**; build #706 UI / DLL #650. 552/552 tests green. Every flat DataGrid uses the `CustomSortComparer` pattern (arrows work); Inspector tree uses `SortTree`.

### Post-29e fixes (2026-05-28)

* **Game-name typo** — corrected `활俠傳` (leading Korean hangul 활) → `活俠傳`
  (Legend of Mortal) across 17 docs + source files. Byte-level swap, no
  other bytes touched.
* **Case-insensitive autocomplete** — all 6 `AutoCompleteBox`es in the
  Class Pivot + SPC tabs were `FilterMode="ContainsOrdinalCaseSensitive"`
  (Assembly, Class FQN, Key/Value field names, SPC Class/Type). Switched
  to `ContainsOrdinal` so typeahead is case-insensitive. The VM-side
  substring filters (Stage-0 class name, Stage-1 field name, Pivot key
  filter) were already `OrdinalIgnoreCase`; the final data-match equality
  (class FQN / assembly) stays `Ordinal` on purpose — identifiers are
  case-sensitive.
* **Key suggestion no longer proposes pointers** — `KeyDiscoveryEngine`
  rated Unity's `m_CachedPtr` (System.IntPtr, per-instance unique +
  session-stable) a 0.90 "perfect key", the exact address-drift trap the
  Pivot tab exists to avoid. New `IsAddressLike` (IntPtr/UIntPtr or a
  `…Ptr`/`…Pointer` name) zeroes TypePrior + applies `AddressKeyPenalty`
  (×0.10), so addresses sink below every real candidate and are excluded
  from the greedy suggestion. +2 tests (552 total). `System.IntPtr` /
  `UIntPtr` also dropped from the 1.00 TypePrior tier.
* **SPC live-smoke** recorded below (Exact signature / Range narrowing /
  relative Increased·Decreased·Unchanged — all single-session, verified
  on 活俠傳).
* **Phase 29f-1 (Layer B protocol) shipped** — `SnapshotArrayElement`
  gained `Fields` (`List<SnapshotFieldEntry>?`) + `SnapshotElementClassError`
  for reference-array inner-pivot. Source-gen JsonContext already covered
  the recursive type; +3 round-trip / backward-compat tests (555 total).
  No DLL change yet — that's 29f-2.

## TL;DR — what's left to pick up (next session)

Phase 29e is fully shipped + merged. The Class Pivot tab is
feature-complete for v1 (full 3-stage funnel: Stage 0 class shortlist →
Stage 1 key auto-discovery → Stage 2 pivot), and every sortable
DataGrid in the app is now AOT-safe. **Nothing is mid-flight** — pick
from the deferred list below, or live-smoke the funnel + sortable grids.

### Phase 29e waves (all shipped)

* **29e-1**  `PivotEngine` ([PivotEngine.cs](../ui/UnityDumpUI/ViewModels/PivotEngine.cs))
  + 16 tests. Class-centric pivot keyed by field values inside each
  instance — sidesteps address drift cross-session AND within-session.
* **29e-1b** Primitive array index expansion in `BuildFieldIndex`
  ([SnapshotViewModel.cs](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs))
  + 10 tests. Array fields fan out as
  `{addr}|{offset}|{fieldName}[{i}]` pseudo-rows. SPC tab inherits
  per-element tracking for free. Ref-arrays render as `ptr=…`
  (Layer B will key by inner field; needs DLL change).
* **29e-2**  Class Pivot tab — new TabItem at index 9 in
  [MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml).
  [PivotViewModel.cs](../ui/UnityDumpUI/ViewModels/PivotViewModel.cs) +
  18 tests in [PivotViewModelTests.cs](../ui/UnityDumpUI.Tests/PivotViewModelTests.cs).
  Shares envelope pool with Snapshot + SPC tabs; cross-session
  banner reads as a feature, not a warning.
* **29e-2 polish** (live-smoke on 活俠傳) — collision cells now render
  distinct-value set + count + cap (`⟨21: 100, 50, 250,+2⟩`) instead
  of an unreadable wall; Trajectory column wraps + has a ToolTip;
  AutoCompleteBoxes browse-on-focus (`MinimumPrefixLength=0`). +2
  collision tests.
* **29e-3**  Stage 0 class shortlist —
  [ClassShortlistEngine.cs](../ui/UnityDumpUI/ViewModels/ClassShortlistEngine.cs)
  (12 tests) + a collapsible "Class shortlist" Expander in the Class
  Pivot tab (+4 ViewModel tests). Scores every class by stability
  (instance-count CV), field-type ratios (primitive/string/array),
  null ratio, presence. Default view = full-presence + non-synthetic
  + FieldCount≥2, sorted Presence↓ / CV↑ / FieldCount↓. Assembly
  filter + toggles. Double-click a row → fills Class FQN → Stage 2.
* **29e-4**  Stage 1 field key auto-discovery —
  [KeyDiscoveryEngine.cs](../ui/UnityDumpUI/ViewModels/KeyDiscoveryEngine.cs)
  (10 tests) + a "Suggest key/value fields" Expander (+5 ViewModel
  tests). Per field: S_stable (pairwise Jaccard of value sets),
  S_cardinality, TypePrior, NamePrior → composite Score. Greedy
  compound-key suggestion solves the "_property collides" problem by
  proposing (_type, _property). UI: Score fields + filter + suggested
  key + Apply; scored DataGrid (sortable), double-click → add as key,
  right-click → add as value. The filterable field picker requested
  during 29e-2 smoke.
* **29e-5 polish** (live-smoke #2 on 活俠傳) — three recurring
  DataGrid papercuts, fixed on all three Pivot grids: (1) **AOT-safe
  sort** — Avalonia's built-in header sort is reflection-based and
  no-ops under AOT; replaced with a manual `Sorting`-event handler +
  typed ViewModel comparers. (2) **Auto-width + h-scroll** — text
  columns moved off star-sizing so long FQNs / field names aren't
  truncated and a horizontal scrollbar appears. (3) **class-name
  keyword filter** added to Stage 0. +4 ViewModel tests. Verified
  live: key=`_type` correctly walked the 銀兩 money trajectory; the
  full funnel (Analyze → Score → pivot) works end-to-end. **Audit
  flagged 7 legacy grids with the same two bugs — see Avalonia
  caveats; not yet migrated.**

* **29e-6 legacy-grid migration** — applied the 29e-5 AOT-safe sort +
  Auto-width pattern to the 6 pre-29e grids that were still on the
  broken built-in sort (Fields, Methods, Symbol Finder, Value Search,
  Snapshot diff, Snapshot field-stats). Comparers centralised in
  [LegacyGridSort.cs](../ui/UnityDumpUI/ViewModels/LegacyGridSort.cs)
  (+9 tests) and called from thin code-behind `Sorting` handlers.
  Inspector tree grid got width-only treatment here (tree-order is
  load-bearing); its tree-aware sort followed in 29e-8.
* **29e-7 DB-side class enumeration** — `SnapshotStore.ListDistinctClassesAsync`
  (`SELECT DISTINCT assembly, class_fqn` — metadata only, no blob)
  seeds the Class Pivot tab's class + assembly autocomplete on connect,
  before any hydrate. Solves the chicken-and-egg where the Class FQN
  box was empty until the user ran something. Wired via
  `MainWindowViewModel.InitSnapshotDbAndPivotAsync` (runs right after
  the per-game DB opens). `RefreshKnownClassesAndFields` now unions the
  DB-seeded list with hydrated-snap classes. +4 tests. (Field-name
  autocomplete still needs a hydrate — field names live in the gzipped
  blob, not the metadata columns.)
* **29e-8 Inspector tree-aware sort** —
  [LiveInspectionViewModel.SortTree](../ui/UnityDumpUI/ViewModels/LiveInspectionViewModel.cs)
  + [InspectorSortTreeTests.cs](../ui/UnityDumpUI.Tests/InspectorSortTreeTests.cs)
  (+6 tests). The last grid on the broken built-in sort. Sorts each
  sibling group recursively (roots + each node's `Children`) and
  re-flattens `Fields` in DFS order respecting expand state, so
  children stay grouped under their parent and collapsed subtrees keep
  their sorted order for the next expand.
* **29e-9 sort consolidation** — discovered the codebase already had an
  AOT-safe sort (`DataGridColumn.CustomSortComparer` +
  [DataGridSortComparers.cs](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs)
  + `WireSortComparers`, docs in aot-pitfalls.md §4.5) that the 29e-5/6
  manual `Sorting`-event detour had duplicated + regressed (3 grids lost
  their arrow glyph). Folded all flat grids back onto `CustomSortComparer`
  (added a `Double` comparer factory for the CV/ratio/score columns),
  deleted `LegacyGridSort.cs` + the Pivot `Sort*` methods. The Inspector
  tree keeps `SortTree` (a flat comparer can't sort a tree). Arrows
  restored; one mechanism for every flat grid.

**550/550 green** (was 462 before 29e). No DLL change. Full design +
live-smoke findings at [class-pivot.md](class-pivot.md) §13.

### Live-smoke status (29e-2)

✅ **Verified on 活俠傳** (2026-05-28): `Mortal.Core.GameStat`,
key=`_type`, value=`_value` → key `3` walks
`10160 → 9910 → 10160 → 10060`, the real 銀兩 spend trajectory across
4 captures / 3 sessions. The cross-session pivot works where SPC
can't (SPC cross-session on Mono is empty — Mono statics carry no
values; see §13).

**Two constraints to remember** (documented in [class-pivot.md](class-pivot.md) §13):
- SPC cross-session on Mono finds nothing → use Class Pivot instead.
- Instance "Inspect" only resolves addresses from the **live** game
  session, so take the final capture in the session you're still
  attached to. Trajectory across older snapshots stays valid; only
  the address handoff needs the live process.

### Live-smoke TODO (29e-3 + 29e-4 — full funnel)

Stages 0/1 were unit-tested but not live-smoked. End-to-end on 活俠傳:
take 3-4 snapshots, open Class Pivot tab.
1. Expand **Class shortlist (Stage 0)**, (optionally type
   `Assembly-CSharp`), press **Analyze classes**. Expect
   `Mortal.Core.GameStat` near the top (full presence, low CV, high
   primitive %). Sort by CV / Prim% / Null% to sanity-check scoring.
   Double-click it → Class FQN box fills.
2. Expand **Suggest key/value fields (Stage 1)**, press **Score
   fields**. Expect `_property`/`_type`/`_propertyID`-ish fields near
   the top; the **Suggested key** label should propose a key (likely
   a compound like `(_type, _property)` since `_property` alone
   collides). Click **Apply suggested key**, then double-click
   `_value` … actually right-click `_value` → "Add to Value fields".
3. Press **Run Pivot**. Confirm the money row trajectory
   (`10160 → 9910 → 10160 → 10060`) appears with the auto-discovered
   key — no manual field typing needed.

### Deferred (post-29e-core)

* **Field-name autocomplete pre-hydrate** — class + assembly names
  now seed from the DB (29e-7), but the Key/Value field-name boxes
  still need a hydrate (field names are inside the gzipped blob, not
  the metadata columns). Cheap enumeration isn't possible without
  reading blobs; a future option is persisting a per-class field-name
  list as a metadata column at save time.
* **Layer B**: reference-array inner pivot (key array elements by an
  inner field) — needs DLL-side per-element child-field capture.
  **Now planned in detail as Phase 29f** (plan.md + class-pivot.md §7):
  `SnapshotArrayElement.Fields`, bitness-adaptive capture (x64 blanket /
  x86 targeted-with-banner), compound (owner-key, element-inner-key)
  pivot. ~4.5 days, 5 waves. Ready to start.
* **Anomaly auto-surface** (stretch): auto-tag "dropped to 0 at snap
  X", "monotonic decrease", etc.

DB schema untouched. Existing `.db` files keep working — every
29e wave is in-memory only.

## Previous TL;DR — Phase 29d live-smoke polish (head `82647cd` / build #682)

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
| 2 | Exact-signature finder — per-snap `Exact` values | ✅ Verified 2026-05-28 (single session, 4 snaps) — `[Exact 10160, Exact 9910, Exact 10860, Exact 10860]` → 1 match `Mortal.Core.GameStat._value` (System.Int32), trajectory `10160 → 9910 → 10860 → 10860`. Confirms absolute Exact predicate end-to-end |
| 3 | Range narrowing — per-snap `Range min..max` | ✅ Verified 2026-05-28 (single session) — `[10000-20000, 8000-10000, 7000-12000, 10000-15000]` → `GameStat._value` + 2× `AkCommonUserSettings/SpatialAudioSettings.m_MaxPathLength` (constant 10000.0). Range predicate + per-snap operand-2 work |
| 3b | Relative predicates — `Increased` / `Decreased` / `Unchanged` (after a leading absolute) | ✅ Verified 2026-05-28 (single session) — `[Any, Decreased, Increased, Unchanged]` → `GameStat._value` + `Mortal.Core.GameSave` fields (CurrentYear/CurrentMonth/TimeTick). Confirms value up/down/unchanged deltas |
| 4 | Cross-session statics-only auto-mode + banner | Not yet tried live (needs game relaunch between snaps) |
| 5 | Cross-build diff banner | Not yet tried live (needs game version change) |
| 6 | Context menu — right-click result row → Inspect / Copy variants | Not yet tried live |
| 7 | Logs at `…\Logs\ui\snapshot\current.log` show `SpcQuery: snaps=N preds=[...] → M matches in Xms` | Not yet inspected |
| 8 | Auto-prune at 200 MB threshold across many wide snaps | Not yet tried live |
| 9 | Settings dialog DB-directory change → live DB switch | Not yet tried live |

### Next priorities

* Continue live-smoke on the items above; surface any further UX papercuts.
* **Phase 29e Class-centric Key/Value pivot — designed, ready to start.**
  Full spec at [class-pivot.md](class-pivot.md); plan.md 29e stub
  expanded to a 5-wave breakdown (29e-1 PivotEngine, 29e-1b primitive
  array index expansion, 29e-2 Pivot tab, 29e-3 Stage 0 class
  shortlisting, 29e-4 Stage 1 key auto-discovery). Layer B
  (reference-array inner pivot, requires DLL change) deferred to a
  follow-up Phase. Existing `.db` files keep working — `BuildFieldIndex`
  is in-memory only, payload_gzip stores raw `SnapshotClassEntry[]`
  with `SnapshotArrayElements` intact, so 29e ships on every saved
  snapshot without migration or manual delete.
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
   活俠傳, spend exact amounts between each. On SPC Query tab,
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
- **Avalonia 12 DataGrid built-in sort is reflection-based → no-op under Native AOT.** `CanUserSortColumns="True"` clicks resolve `SortMemberPath` via `ClrPropertyInfo` (IL2026/IL3050), which silently does nothing in an AOT publish. **The established fix (use this — don't reinvent it) is `DataGridColumn.CustomSortComparer`**: typed factories in [DataGridSortComparers.cs](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs) (`Ordinal`/`Number`/`Double`/`Hex`/`Bool`) + the `DataGrid.WireSortComparers(dict)` extension, wired per grid at `Loaded` in `MainWindow.WireSortComparersForAllGrids()`. Each column needs `SortMemberPath="<PropertyName>"` + `CanUserSort="True"`; the dict maps that path → a row-property selector. This keeps the **native arrow glyph + direction toggle** and is AOT-safe. Full rationale in [aot-pitfalls.md §4.5](aot-pitfalls.md). **Every flat sortable grid uses this** (Fields, Methods, Symbol Finder, Value Search, Snapshot diff, Snapshot field-stats, Pivot shortlist/field-score/results — 29e-9). **The one exception is the Inspector `FieldsDataGrid`** — it's an expand/collapse tree, so a flat comparer would tear children from their parent; it uses the tree-aware [LiveInspectionViewModel.SortTree](../ui/UnityDumpUI/ViewModels/LiveInspectionViewModel.cs) via the `Sorting` event instead (the only grid on that path; no arrow glyph there). *History: 29e-5/6/8 briefly used a parallel `Sorting`-event + manual-sort mechanism; 29e-9 consolidated all flat grids back onto `CustomSortComparer` and deleted that detour (`LegacyGridSort.cs`).*
- **Avalonia 12 DataGrid star-sized columns never overflow → no horizontal scrollbar + truncation.** All-`Width="*"`/`2*` columns sum to exactly the viewport, so long text (class FQNs, field names) clips with no way to scroll. Fix: give text columns `Width="Auto"` + `MinWidth` (numeric/fixed columns stay pixel-width) so the column set can exceed the pane width and the DataGrid shows its horizontal scrollbar. **Fixed** on Pivot grids (29e-5) + Fields/Methods/Symbol Finder/Inspector star columns (29e-6). Value Search + Snapshot diff already used fixed pixel widths (they overflow + scroll already). **Avoid `Width="Auto"` on the Snapshot diff grid** specifically — it can hold ~740K rows and Auto would measure them all; it stays fixed-width.
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
