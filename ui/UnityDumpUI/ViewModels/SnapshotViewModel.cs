// SnapshotViewModel.cs — Phase 29b.
//
// In-memory snapshot store + diff workflow built on the Phase 29a DLL
// RPCs (snapshotBegin / snapshotChunk / snapshotEnd). User clicks
// "Take Snapshot" → VM fires Begin (server runs the wide heap walk +
// per-class decode under one outer attach guard) → loops Chunk until
// done → builds a local SnapshotEnvelope. Multiple snapshots persist
// in `Snapshots` for diff comparison.
//
// Diff strategy: build a flat `(classFqn, instanceAddress, fieldOffset)
// → value` map per snapshot at chunk-collection time. Diff is then a
// dictionary join: rows present in both with different values =
// "changed", in A only = "removed", in B only = "added". Within a
// single game session instance addresses are stable so this works for
// every field. Cross-session diff (29c) will need address-independent
// keys for instance fields.
//
// UI filter strategy C (per user decision 2026-05-26): no DLL-side
// filtering of synthetic types; UI ships a "Hide synthetic types"
// checkbox default ON that filters by name pattern (everything
// starting with '<', covering `<Module>`, `<>f__AnonymousType*`,
// `<>c`, `<Start>d__N`, etc).
//
// AOT-safe: CommunityToolkit.Mvvm source-gen + zero reflection.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class SnapshotViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _cts;
    private DispatcherTimer?         _elapsedTimer;
    private Stopwatch?               _elapsedStopwatch;

    /// <summary>Process working-set sampled at <see cref="TakeSnapshotAsync"/>
    /// entry. The elapsed-timer ticks compute Δ against this so the user
    /// sees how much memory the current capture is costing in real time —
    /// useful guard rail now that the default caps are 512 instances /
    /// 256 array elements (5–10× the earlier defaults).</summary>
    private long _captureStartWorkingSet;

    /// <summary>Defaults for the non-32-bit case (modern x64 games).
    /// Kept as the "safe high water mark" so <see cref="OnGameConnected"/>
    /// can restore them when the user reconnects to an x64 game after
    /// having been clamped on an earlier x86 connection.</summary>
    private const int kDefaultInstanceCapX64 = 512;
    private const int kDefaultArrayCapX64    = 256;

    /// <summary>Clamped defaults for 32-bit Mono games. Chosen 2026-05-27
    /// after the build #646 활俠傳 CTD: the DLL caches the whole
    /// snapshot's JSON payload inside the game process, so on a target
    /// with a 4 GB user-space ceiling the post-bump 512/256 defaults
    /// pushed snap #2 past OOM. 256/64 leaves comfortable headroom
    /// (smaller cache + smaller per-class metadata). User can still
    /// push higher manually via the NumericUpDowns if they accept the
    /// risk — and the DLL also has a 3 GB pre-check refusal as a hard
    /// backstop.</summary>
    private const int kDefaultInstanceCapX86 = 256;
    private const int kDefaultArrayCapX86    = 64;

    public SnapshotViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
        // Re-fire the rollup banner whenever the list mutates (Take /
        // Drop / ClearAll). DisplayName for each envelope is per-row
        // and doesn't need this — only the cumulative count + memory
        // estimate does.
        Snapshots.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(TotalSnapshotsMemoryBanner));
    }

    /// <summary>Snapshots the user has taken in this session. List
    /// order = chronological. Most users only ever hold 2 — before and
    /// after an in-game action — but multi-snapshot diff (any A → any B)
    /// works for free.</summary>
    public ObservableCollection<SnapshotEnvelope> Snapshots { get; } = new();

    /// <summary>Full set of rows produced by the last <see cref="Diff"/>
    /// — the canonical, unfiltered result. <see cref="FilteredDiffRows"/>
    /// is the projection the UI actually binds.</summary>
    public ObservableCollection<SnapshotDiffRow> DiffRows { get; } = new();

    /// <summary>Materialized projection of <see cref="DiffRows"/> under the
    /// current filter set — what the diff DataGrid binds. Rebuilt by
    /// <see cref="RebuildFilteredRows"/> on any filter property change
    /// (single O(N) pass rather than per-render predicate evaluation as
    /// the lazy IEnumerable variant did pre-noise-filter).</summary>
    public ObservableCollection<SnapshotDiffRow> FilteredDiffRows { get; } = new();

    /// <summary>Per-field-name occurrence counts in the current diff,
    /// sorted desc. Drives the right-side stats panel — fields appearing
    /// across many rows are often noise (e.g. <c>m_CachedPtr</c>,
    /// <c>&lt;&gt;1__state</c>), and the user can one-click exclude them.
    /// Rebuilt only on <see cref="Diff"/> (not on every filter toggle —
    /// the panel reflects the underlying diff, not the filter result).</summary>
    public ObservableCollection<FieldNameStat> FieldNameStats { get; } = new();

    /// <summary>Field names the user has explicitly excluded via the stats
    /// panel. Bound to a chip-list in the UI; <see cref="HideNoisyFields"/>
    /// must be ON for this set to take effect.</summary>
    public ObservableCollection<string> ExcludedFieldNames { get; } = new();

    // -----------------------------------------------------------------------
    // Capture options ("全抓" defaults; user can opt in to narrowing).
    // -----------------------------------------------------------------------

    /// <summary>Optional comma-separated assembly filter. Empty → all
    /// visible assemblies (the design rule from Phase 29 plan: user
    /// shouldn't have to know where data lives).</summary>
    [ObservableProperty] private string _scopeAssembliesText = string.Empty;

    /// <summary>Per-class instance cap (server-side hard limit 1024).
    /// Raised from 100 → 512 on 2026-05-26 user feedback: 활俠傳 player
    /// hit a GameStat instance past position 100 in the heap-walk order
    /// that diff therefore missed entirely. 512 covers the typical
    /// per-class population on the live targets while leaving headroom
    /// before OOM territory. Memory cost is reported in the status line
    /// during capture so the user can dial it back if needed.</summary>
    [ObservableProperty] private int _instanceCap = 512;

    /// <summary>Per-field array element cap (server-side hard limit 256).
    /// Raised from 50 → 256 same session — at the hard cap by default,
    /// since the server already truncates beyond 256 and the user has
    /// no reason to artificially under-cap.</summary>
    [ObservableProperty] private int _arrayCap    = 256;

    /// <summary>When unchecked (default), the server skips
    /// mscorlib / System.* / Microsoft.* / netstandard / Mono.* /
    /// UnityEngine.* / Unity.* / Fungus assemblies on 全抓 mode.
    /// Per Phase 29a-v3 live-smoke on 活俠傳: capturing all 15K
    /// classes including system noise made the snapshot too heavy
    /// (game freeze + drown user data in metadata). Checking this
    /// box passes <c>includeSystemAssemblies: true</c> to keep
    /// everything (useful for diagnostic / advanced workflows).</summary>
    [ObservableProperty] private bool _includeSystemAssemblies;

    /// <summary>Optional label the user can attach to a snapshot before
    /// taking it. Surfaced in the Snapshots list + diff status line.</summary>
    [ObservableProperty] private string _pendingLabel = string.Empty;

    /// <summary>When set, the next <see cref="Diff"/> walks the joined
    /// field index and emits <c>unchanged</c> rows in addition to
    /// changed/added/removed. Default OFF — unchanged-row capture can
    /// add 10–100× more rows on a wide diff (107K instances × ~30
    /// fields ⇒ millions of "still equal" rows), so it's opt-in. The
    /// payoff: lets the user filter by value range across the whole
    /// captured state, not just what differed between A and B.</summary>
    [ObservableProperty] private bool _includeUnchangedRows;

    // ----- Game-process memory + bitness (Phase 29-polish 2026-05-27) -----

    /// <summary>Game-process pointer size, populated from the handshake.
    /// <c>4</c> = 32-bit Mono target (where DLL-side JSON cache risks
    /// 4 GB OOM on snap #2); <c>8</c> = x64 (no such risk); <c>0</c> =
    /// not yet connected / unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTarget32Bit))]
    [NotifyPropertyChangedFor(nameof(GameMemoryBanner))]
    private int _gamePointerSize;

    /// <summary>Last game-process WorkingSet sample (bytes), updated on
    /// every handshake + every snapshotBegin response.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameMemoryBanner))]
    private ulong _gameWorkingSet;

    /// <summary>Last game-process PrivateUsage sample (bytes). On x86
    /// targets this is the metric that matters — physical mapping
    /// (WorkingSet) can stay low while committed virtual climbs and
    /// triggers OOM.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameMemoryBanner))]
    private ulong _gamePrivateUsage;

    /// <summary>True when the connected game is 32-bit. Drives the
    /// auto-clamped defaults + the "32-bit safety banner" in the
    /// snapshot tab.</summary>
    public bool IsTarget32Bit => GamePointerSize == 4;

    /// <summary>Human-friendly status string shown above the capture
    /// controls: "Game: 32-bit Mono, 1.42 GB / 1.78 GB committed
    /// (approaching 4 GB ceiling — snapshot caps auto-clamped)".
    /// Empty when not connected.</summary>
    public string GameMemoryBanner
    {
        get
        {
            if (GamePointerSize == 0) return string.Empty;
            string bits = GamePointerSize == 4 ? "32-bit" : "64-bit";
            string ws   = GameWorkingSet   > 0 ? FormatMemory((long)GameWorkingSet)   : "?";
            string pu   = GamePrivateUsage > 0 ? FormatMemory((long)GamePrivateUsage) : "?";
            string suffix = string.Empty;
            if (GamePointerSize == 4 && GamePrivateUsage > 0)
            {
                // 3 GB is the DLL's pre-check refusal threshold; warn earlier.
                const ulong kWarnAt = 2UL * 1024 * 1024 * 1024;       // 2 GB
                const ulong kDanger = 3UL * 1024 * 1024 * 1024;       // 3 GB (DLL refuses)
                if (GamePrivateUsage >= kDanger)
                    suffix = " ⛔ over 3 GB — DLL will refuse next snapshot";
                else if (GamePrivateUsage >= kWarnAt)
                    suffix = " ⚠ approaching 4 GB ceiling — consider lowering caps further";
                else
                    suffix = " — snapshot caps auto-clamped for safety";
            }
            return $"Game: {bits} Mono — WorkingSet {ws}, committed {pu}{suffix}";
        }
    }

    /// <summary>Hook fired from <c>MainWindowViewModel</c> after every
    /// successful handshake. Updates the bitness + memory display and,
    /// on 32-bit targets, clamps <see cref="InstanceCap"/> /
    /// <see cref="ArrayCap"/> downward to the safe defaults (256 / 64)
    /// if they're currently above. Never clamps upward — user manually-
    /// chosen lower caps stay respected.</summary>
    public void OnGameConnected(int pointerSize, ulong workingSet, ulong privateUsage)
    {
        GamePointerSize  = pointerSize;
        GameWorkingSet   = workingSet;
        GamePrivateUsage = privateUsage;

        if (pointerSize == 4)
        {
            if (InstanceCap > kDefaultInstanceCapX86) InstanceCap = kDefaultInstanceCapX86;
            if (ArrayCap    > kDefaultArrayCapX86)    ArrayCap    = kDefaultArrayCapX86;
        }
        // On x64 we don't auto-raise. If the user manually picked low caps
        // on a previous x86 session and reconnects to x64, they probably
        // still want their pick — the high defaults are a fresh-install
        // thing, not a "snap back to wide-open" behaviour.
    }

    // -----------------------------------------------------------------------
    // Selection state.
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotACommand))]
    private SnapshotEnvelope? _selectedSnapshotA;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotBCommand))]
    private SnapshotEnvelope? _selectedSnapshotB;

    // -----------------------------------------------------------------------
    // Diff filter options (Strategy C — UI-side filtering only). All
    // filter properties below funnel through OnPropertyChanged into
    // RebuildFilteredRows so the materialized FilteredDiffRows stays in
    // sync without per-render predicate work.
    // -----------------------------------------------------------------------

    /// <summary>Default ON. Filters out classes whose names start with
    /// '&lt;' (synthetic compiler-generated types like
    /// <c>&lt;Module&gt;</c>, <c>&lt;&gt;f__AnonymousType*</c>,
    /// <c>&lt;&gt;c</c>, <c>&lt;Start&gt;d__N</c>).</summary>
    [ObservableProperty] private bool _hideSyntheticTypes = true;

    /// <summary>Show "changed" rows (value differs between A and B).
    /// Default OFF as of 2026-05-27 — user feedback: the value-range +
    /// unchanged + added/removed workflows are now the primary entry
    /// points (find-by-value, not just see-what-changed), so the
    /// raw changed list more often gets in the way than helps.
    /// Re-checking it stays a one-click switch.</summary>
    [ObservableProperty] private bool _showChanged;

    /// <summary>Show "added" rows (in B but not A). Default OFF.</summary>
    [ObservableProperty] private bool _showAdded;

    /// <summary>Show "removed" rows (in A but not B). Default OFF.</summary>
    [ObservableProperty] private bool _showRemoved;

    /// <summary>Show "unchanged" rows (key present in both with equal
    /// value). Default ON — when the user opts in to capturing them
    /// they almost always want them visible too. Has no effect unless
    /// <see cref="IncludeUnchangedRows"/> was set when <see cref="Diff"/>
    /// last ran.</summary>
    [ObservableProperty] private bool _showUnchanged = true;

    /// <summary>Class-name substring filter (case-insensitive).</summary>
    [ObservableProperty] private string _classFilterText = string.Empty;

    /// <summary>Field-name substring filter (case-insensitive). Symmetric
    /// to <see cref="ClassFilterText"/>; both must match if both set.</summary>
    [ObservableProperty] private string _fieldFilterText = string.Empty;

    /// <summary>Lower bound (inclusive) for the numeric value range filter.
    /// <c>null</c> = no lower bound. Applies on the "either side in range"
    /// semantics: a row passes if Old OR New parses as numeric and falls
    /// in <c>[Min, Max]</c>. Non-numeric rows are filtered out entirely
    /// once any range bound is set.
    /// Typed as <c>decimal?</c> to match Avalonia 12 <c>NumericUpDown.Value</c>
    /// — the value-range check casts to <c>double</c> internally.</summary>
    [ObservableProperty] private decimal? _valueRangeMin;

    /// <summary>Upper bound (inclusive) for the numeric value range filter.
    /// <c>null</c> = no upper bound.</summary>
    [ObservableProperty] private decimal? _valueRangeMax;

    /// <summary>Hide rows where both sides are pointer-formatted
    /// (<c>ptr=0x…</c>). These are usually re-allocation noise — the
    /// pointer changes but the referenced content's diff would show
    /// up elsewhere if it actually differs.</summary>
    [ObservableProperty] private bool _hidePointerChanges;

    /// <summary>Hide string rows whose longer side exceeds
    /// <see cref="LargeStringThreshold"/> characters. Targets
    /// <c>System.Exception._stackTrace</c> / <c>_remoteStackTraceString</c>
    /// blobs that drown out signal.</summary>
    [ObservableProperty] private bool _hideLargeStrings = true;

    /// <summary>Character threshold above which a string row is
    /// considered "large". 256 catches typical stack-trace strings on
    /// both Mono and CoreCLR shapes.</summary>
    [ObservableProperty] private int _largeStringThreshold = 256;

    /// <summary>Honour <see cref="ExcludedFieldNames"/> when set. Default
    /// ON — once the user one-clicks a noisy field name from the stats
    /// panel they expect it to disappear immediately.</summary>
    [ObservableProperty] private bool _hideNoisyFields = true;

    // CommunityToolkit source-gen OnXxxChanged hooks — each filter
    // property triggers a single materialized rebuild. Cheaper than
    // per-render predicate evaluation as filter count grows.
    partial void OnHideSyntheticTypesChanged(bool value)  => RebuildFilteredRows();
    partial void OnShowChangedChanged(bool value)         => RebuildFilteredRows();
    partial void OnShowAddedChanged(bool value)           => RebuildFilteredRows();
    partial void OnShowRemovedChanged(bool value)         => RebuildFilteredRows();
    partial void OnShowUnchangedChanged(bool value)       => RebuildFilteredRows();
    partial void OnClassFilterTextChanged(string value)   => RebuildFilteredRows();
    partial void OnFieldFilterTextChanged(string value)   => RebuildFilteredRows();
    partial void OnValueRangeMinChanged(decimal? value)   => RebuildFilteredRows();
    partial void OnValueRangeMaxChanged(decimal? value)   => RebuildFilteredRows();
    partial void OnHidePointerChangesChanged(bool value)  => RebuildFilteredRows();
    partial void OnHideLargeStringsChanged(bool value)    => RebuildFilteredRows();
    partial void OnLargeStringThresholdChanged(int value) => RebuildFilteredRows();
    partial void OnHideNoisyFieldsChanged(bool value)     => RebuildFilteredRows();

    // -----------------------------------------------------------------------
    // Status / progress.
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotACommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotBCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Press Take Snapshot to capture the game's current managed-state. Two snapshots needed for a diff.";
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Activation handoff — the view's "Inspect this instance"
    /// context-menu item raises this through the VM so MainWindowViewModel
    /// can drive the cross-tab navigation (same pattern as the other
    /// tabs: Address Lookup, Symbol Finder, Value Search).</summary>
    public event Action<SnapshotDiffRow>? NavigateToInspectorRequested;
    public event Action<SnapshotDiffRow>? NavigateToAddressLookupRequested;

    public void NavigateToInspector(SnapshotDiffRow row)
        => NavigateToInspectorRequested?.Invoke(row);

    public void NavigateToAddressLookup(SnapshotDiffRow row)
        => NavigateToAddressLookupRequested?.Invoke(row);

    // -----------------------------------------------------------------------
    // Commands.
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanTakeSnapshot))]
    public async Task TakeSnapshotAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
        _captureStartWorkingSet = GetWorkingSet();
        // Phase 1 — heap walk + per-class decode + GC drain on the
        // server side. The elapsed-time DispatcherTimer updates the
        // visible status every 200ms so the user can see we're alive +
        // the current process memory + delta-from-start so they can
        // dial back the caps if the snapshot is heading toward OOM.
        // This phase dominates on big targets (60-90s on 활俠傳 with
        // 全抓 scope after exclusions); the wide heap walk is the
        // unavoidable cost.
        StartElapsedTimer("Capturing (heap walk + decode)");

        var sw = Stopwatch.StartNew();
        try
        {
            // Parse scope. Comma- or whitespace-separated → list. Empty → null (all).
            IReadOnlyList<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(ScopeAssembliesText))
            {
                var parts = ScopeAssembliesText
                    .Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) scope = parts;
            }

            int cap = InstanceCap > 0 ? InstanceCap : 512;
            int arr = ArrayCap    > 0 ? ArrayCap    : 256;

            var begin = await _backend.SnapshotBeginAsync(
                scope, cap, arr, IncludeSystemAssemblies, ct).ConfigureAwait(true);
            StopElapsedTimer();  // Phase 1 done; manual status from here.

            // Refresh game-process memory + bitness from the response,
            // even on the error path — the DLL's 32-bit OOM refusal
            // includes those fields so the banner reflects current state
            // (and the auto-clamp re-fires if the user reconnected
            // between snapshots).
            if (begin.GamePointerSize is int ps && ps > 0)
            {
                OnGameConnected(
                    ps,
                    begin.GameWorkingSet  ?? GameWorkingSet,
                    begin.GamePrivateUsage ?? GamePrivateUsage);
            }
            else if (begin.GameWorkingSet is { } gws)
            {
                GameWorkingSet = gws;
                if (begin.GamePrivateUsage is { } gpu) GamePrivateUsage = gpu;
            }

            if (!string.IsNullOrEmpty(begin.Error))
            {
                ErrorMessage  = begin.Error;
                StatusMessage = "snapshotBegin failed.";
                return;
            }

            // Phase 2 — stream pre-built class payloads in chunks. Each
            // snapshotChunk RPC is a pure JSON slice server-side (Phase
            // 29a-v2 design), so latency is dominated by IPC throughput,
            // not server work. Chunk size 100 (was 32) cuts round-trip
            // count by ~3x — on 활俠傳's 3.6K-class workload that's 37
            // round-trips instead of 114. Server caps at 200.
            const int kChunkSize = 100;
            int totalClasses = begin.TotalClasses;
            int expectedChunks = totalClasses > 0
                ? (totalClasses + kChunkSize - 1) / kChunkSize
                : 0;
            double beginElapsedSec = sw.Elapsed.TotalSeconds;

            var classes = new List<SnapshotClassEntry>(totalClasses);
            int cursor = 0;
            int chunkCount = 0;
            var chunkPhaseSw = Stopwatch.StartNew();
            while (true)
            {
                var chunk = await _backend.SnapshotChunkAsync(begin.SnapshotId, cursor, kChunkSize, ct)
                    .ConfigureAwait(true);
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    ErrorMessage  = chunk.Error;
                    StatusMessage = "snapshotChunk failed.";
                    break;
                }
                classes.AddRange(chunk.Classes);
                chunkCount++;
                // Live progress for the streaming phase. Updates every
                // chunk so the user sees the counter move — fixes the
                // "looks frozen" perception when total snapshot is 100s+.
                long wsNow = GetWorkingSet();
                string gameMem = GameWorkingSet > 0
                    ? $" — game {FormatMemory((long)GameWorkingSet)}"
                    : string.Empty;
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Streaming chunk {0}/{1} ({2:N0} of {3:N0} classes)… capture {4:F1}s + stream {5:F1}s — UI {6} (+{7}){8}",
                    chunkCount, expectedChunks,
                    classes.Count, totalClasses,
                    beginElapsedSec, chunkPhaseSw.Elapsed.TotalSeconds,
                    FormatMemory(wsNow), FormatMemory(wsNow - _captureStartWorkingSet),
                    gameMem);
                if (chunk.Done) break;
                cursor = chunk.NextCursor;
            }

            // Always release the server-side session even if we errored mid-chunk.
            try { await _backend.SnapshotEndAsync(begin.SnapshotId, ct).ConfigureAwait(true); }
            catch { /* best effort */ }

            // Build local envelope + flat lookup index for diff.
            var env = new SnapshotEnvelope
            {
                ServerId       = begin.SnapshotId,
                Label          = string.IsNullOrWhiteSpace(PendingLabel)
                                    ? string.Empty
                                    : PendingLabel.Trim(),
                CapturedAt     = DateTime.Now,
                CapturedAtUtc  = begin.CapturedAt,
                GameBuildId    = begin.GameBuildId,
                GameSessionId  = begin.GameSessionId,
                TotalClasses   = begin.TotalClasses,
                TotalInstances = begin.TotalInstances,
                Classes        = classes,
            };
            env.BuildFieldIndex();

            Snapshots.Add(env);
            // Auto-pick: if this is the first snapshot, set A; if it's the
            // second (and no B yet), set B; otherwise leave selections
            // alone so the user keeps control.
            if (SelectedSnapshotA is null)      SelectedSnapshotA = env;
            else if (SelectedSnapshotB is null) SelectedSnapshotB = env;

            PendingLabel = string.Empty;
            sw.Stop();
            string label = string.IsNullOrEmpty(env.Label) ? "(no label)" : $"\"{env.Label}\"";
            string excluded = begin.ExcludedSystemAssemblies > 0
                ? $", excluded {begin.ExcludedSystemAssemblies} system asm"
                : string.Empty;
            string cacheNote = begin.DecodeStats is { } ds && (ds.ClassInfoCacheHits + ds.ClassInfoCacheMisses) > 0
                ? $", ClassInfo cache {ds.ClassInfoCacheHits:N0}/{ds.ClassInfoCacheHits + ds.ClassInfoCacheMisses:N0}"
                : string.Empty;
            long wsEnd = GetWorkingSet();
            string memNote = wsEnd > 0
                ? $", UI {FormatMemory(wsEnd)} (+{FormatMemory(wsEnd - _captureStartWorkingSet)})"
                : string.Empty;
            string gameMemNote = GameWorkingSet > 0
                ? $", game {FormatMemory((long)GameWorkingSet)} ws / {FormatMemory((long)GamePrivateUsage)} committed"
                : string.Empty;
            StatusMessage = $"Snapshot #{Snapshots.Count} {label} — {env.TotalClasses:N0} classes, {env.TotalInstances:N0} instances, {env.FieldIndex.Count:N0} field-values (captured in {sw.Elapsed.TotalSeconds:F1}s across {chunkCount} chunks{excluded}{cacheNote}{memNote}{gameMemNote}).";
            _logger.Write(Constants.LogCategoryView,
                $"Snapshot: id={begin.SnapshotId} classes={env.TotalClasses} instances={env.TotalInstances} fieldValues={env.FieldIndex.Count} elapsed={sw.Elapsed.TotalSeconds:F1}s excluded={begin.ExcludedSystemAssemblies} workingSet={wsEnd} workingSetDelta={wsEnd - _captureStartWorkingSet} gameWorkingSet={GameWorkingSet} gamePrivateUsage={GamePrivateUsage}");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Snapshot failed.";
            _logger.Write(Constants.LogCategoryView, $"Snapshot.Take failed: {ex.Message}");
        }
        finally
        {
            StopElapsedTimer();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDiff))]
    public void Diff()
    {
        ErrorMessage = null;
        var a = SelectedSnapshotA;
        var b = SelectedSnapshotB;
        if (a is null || b is null) return;
        if (ReferenceEquals(a, b))
        {
            ErrorMessage  = "Pick two different snapshots to diff.";
            StatusMessage = "Diff failed.";
            return;
        }
        if (!string.Equals(a.GameBuildId, b.GameBuildId, StringComparison.Ordinal))
        {
            ErrorMessage  = "Snapshots are from different game builds — field offsets may not align. Cross-build diff is a Phase 29c feature.";
            StatusMessage = "Diff blocked.";
            return;
        }

        var sw = Stopwatch.StartNew();
        DiffRows.Clear();
        int changed = 0, added = 0, removed = 0, unchanged = 0;
        bool includeUnchanged = IncludeUnchangedRows;

        // Walk A → B keys to find changed + removed (+ optionally unchanged);
        // second pass walks B → A to catch the added.
        foreach (var (key, va) in a.FieldIndex)
        {
            if (b.FieldIndex.TryGetValue(key, out var vb))
            {
                if (!string.Equals(va.ValueDisplay, vb.ValueDisplay, StringComparison.Ordinal))
                {
                    DiffRows.Add(SnapshotDiffRow.Changed(va, vb));
                    changed++;
                }
                else if (includeUnchanged)
                {
                    DiffRows.Add(SnapshotDiffRow.Unchanged(va, vb));
                    unchanged++;
                }
            }
            else
            {
                DiffRows.Add(SnapshotDiffRow.Removed(va));
                removed++;
            }
        }
        foreach (var (key, vb) in b.FieldIndex)
        {
            if (!a.FieldIndex.ContainsKey(key))
            {
                DiffRows.Add(SnapshotDiffRow.Added(vb));
                added++;
            }
        }

        sw.Stop();
        RecomputeFieldStats();
        RebuildFilteredRows();
        string unchangedNote = includeUnchanged
            ? $", {unchanged:N0} unchanged"
            : string.Empty;
        StatusMessage = $"Diff {a.ShortLabel} → {b.ShortLabel}: {changed:N0} changed, {added:N0} added, {removed:N0} removed{unchangedNote} (in {sw.ElapsedMilliseconds} ms). Showing {FilteredDiffRows.Count:N0} after filters.";
    }

    /// <summary>Drop the snapshot currently selected as A. Status line
    /// reports the freed estimate so the user sees memory drop.</summary>
    [RelayCommand(CanExecute = nameof(CanDropSnapshotA))]
    public void DropSnapshotA() => DropOne(SelectedSnapshotA, "A");

    /// <summary>Drop the snapshot currently selected as B. Independent
    /// from <see cref="DropSnapshotA"/> so the user doesn't have to
    /// swap A↔B to drop the B side.</summary>
    [RelayCommand(CanExecute = nameof(CanDropSnapshotB))]
    public void DropSnapshotB() => DropOne(SelectedSnapshotB, "B");

    private void DropOne(SnapshotEnvelope? env, string slotLabel)
    {
        if (env is null) return;
        long freed = env.EstimatedMemoryBytes;
        Snapshots.Remove(env);
        // If the dropped envelope was also referenced by the other slot,
        // clear that selection too — ComboBox.SelectedItem would otherwise
        // dangle on a no-longer-in-source item.
        if (ReferenceEquals(SelectedSnapshotA, env)) SelectedSnapshotA = null;
        if (ReferenceEquals(SelectedSnapshotB, env)) SelectedSnapshotB = null;
        // Diff result targeted this envelope — clear it.
        DiffRows.Clear();
        FieldNameStats.Clear();
        RebuildFilteredRows();
        OnPropertyChanged(nameof(TotalSnapshotsMemoryBanner));
        StatusMessage = $"Snapshot {slotLabel} dropped (~{FormatMemory(freed)} freed). {Snapshots.Count:N0} remaining.";
    }

    [RelayCommand]
    public void ClearAll()
    {
        long freed = TotalSnapshotsMemoryBytes;
        int count = Snapshots.Count;
        Snapshots.Clear();
        SelectedSnapshotA = null;
        SelectedSnapshotB = null;
        DiffRows.Clear();
        FieldNameStats.Clear();
        ExcludedFieldNames.Clear();
        RebuildFilteredRows();
        OnPropertyChanged(nameof(TotalSnapshotsMemoryBanner));
        StatusMessage = count > 0
            ? $"All {count:N0} snapshots cleared (~{FormatMemory(freed)} freed)."
            : "Snapshot list already empty.";
        ErrorMessage  = null;
    }

    /// <summary>Sum of <see cref="SnapshotEnvelope.EstimatedMemoryBytes"/>
    /// over every snapshot in the list. UI surfaces this so the user
    /// sees the cumulative cost as snapshots accumulate.</summary>
    public long TotalSnapshotsMemoryBytes
    {
        get
        {
            long sum = 0;
            foreach (var s in Snapshots) sum += s.EstimatedMemoryBytes;
            return sum;
        }
    }

    /// <summary>Header line shown above the A/B selectors, e.g.
    /// "3 snapshots — ~4.2 GB total (UI memory)". Empty when no
    /// snapshots yet so the row collapses.</summary>
    public string TotalSnapshotsMemoryBanner
        => Snapshots.Count == 0
            ? string.Empty
            : $"{Snapshots.Count:N0} snapshot{(Snapshots.Count == 1 ? string.Empty : "s")} held — ~{FormatMemory(TotalSnapshotsMemoryBytes)} total UI memory";

    /// <summary>Toggle a field name in / out of <see cref="ExcludedFieldNames"/>
    /// from the stats panel. The list is small (typically top-20) so the
    /// O(N) scan is fine.</summary>
    [RelayCommand]
    public void ToggleFieldNameExclusion(string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return;
        int idx = -1;
        for (int i = 0; i < ExcludedFieldNames.Count; i++)
        {
            if (string.Equals(ExcludedFieldNames[i], fieldName, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx >= 0) ExcludedFieldNames.RemoveAt(idx);
        else          ExcludedFieldNames.Add(fieldName);

        // Flip the FieldNameStat.IsExcluded indicator for live UI feedback.
        foreach (var s in FieldNameStats)
        {
            if (string.Equals(s.FieldName, fieldName, StringComparison.Ordinal))
                s.IsExcluded = idx < 0;
        }
        RebuildFilteredRows();
    }

    /// <summary>Drop every exclusion the user has accumulated. Wired to
    /// a "Clear exclusions" button under the stats panel.</summary>
    [RelayCommand]
    public void ClearFieldExclusions()
    {
        if (ExcludedFieldNames.Count == 0) return;
        ExcludedFieldNames.Clear();
        foreach (var s in FieldNameStats) s.IsExcluded = false;
        RebuildFilteredRows();
    }

    /// <summary>Apply every filter against <see cref="DiffRows"/> and
    /// rebuild <see cref="FilteredDiffRows"/> in place. Called by every
    /// filter property's <c>OnXxxChanged</c> hook + by <see cref="Diff"/>
    /// when the underlying row set itself changes. Single O(N) pass.</summary>
    private void RebuildFilteredRows()
    {
        var clsFilter   = ClassFilterText;
        var fieldFilter = FieldFilterText;
        bool hasClsFilter   = !string.IsNullOrWhiteSpace(clsFilter);
        bool hasFieldFilter = !string.IsNullOrWhiteSpace(fieldFilter);
        bool useRange  = ValueRangeMin.HasValue || ValueRangeMax.HasValue;
        double rangeMin = ValueRangeMin.HasValue ? (double)ValueRangeMin.Value : double.NegativeInfinity;
        double rangeMax = ValueRangeMax.HasValue ? (double)ValueRangeMax.Value : double.PositiveInfinity;
        int largeStrLimit = HideLargeStrings ? Math.Max(0, LargeStringThreshold) : int.MaxValue;

        // Snapshot the exclusion set into a HashSet for O(1) lookups in
        // the inner loop. Ordinal — field names are case-sensitive.
        HashSet<string>? excluded = null;
        if (HideNoisyFields && ExcludedFieldNames.Count > 0)
            excluded = new HashSet<string>(ExcludedFieldNames, StringComparer.Ordinal);

        FilteredDiffRows.Clear();
        foreach (var r in DiffRows)
        {
            if (HideSyntheticTypes && r.IsSynthetic) continue;

            // Three independent change-kind toggles (Phase-29-polish refactor —
            // replaces the prior ChangedOnly + ShowAdded + ShowRemoved triad
            // whose interaction had dead-code branches).
            if (r.ChangeKind == "changed"   && !ShowChanged)   continue;
            if (r.ChangeKind == "added"     && !ShowAdded)     continue;
            if (r.ChangeKind == "removed"   && !ShowRemoved)   continue;
            if (r.ChangeKind == "unchanged" && !ShowUnchanged) continue;

            if (hasClsFilter &&
                r.ClassDisplay.IndexOf(clsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (hasFieldFilter &&
                r.Field.IndexOf(fieldFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (excluded is not null && excluded.Contains(r.Field)) continue;

            if (HidePointerChanges && r.Kind == DiffRowKind.Pointer) continue;

            if (r.Kind == DiffRowKind.String && r.MaxValueLength > largeStrLimit) continue;

            if (useRange)
            {
                if (r.Kind != DiffRowKind.Numeric) continue;
                bool oldIn = r.NumericOld is double o && o >= rangeMin && o <= rangeMax;
                bool newIn = r.NumericNew is double n && n >= rangeMin && n <= rangeMax;
                if (!oldIn && !newIn) continue;
            }

            FilteredDiffRows.Add(r);
        }
    }

    /// <summary>Build the per-field-name occurrence histogram from the
    /// current <see cref="DiffRows"/>, sorted descending by count, top 30.
    /// Cheap O(N) — typical N is ~1k–2k changed rows.</summary>
    private void RecomputeFieldStats()
    {
        FieldNameStats.Clear();
        if (DiffRows.Count == 0) return;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in DiffRows)
        {
            if (string.IsNullOrEmpty(r.Field)) continue;
            counts.TryGetValue(r.Field, out int c);
            counts[r.Field] = c + 1;
        }

        var excludedSet = ExcludedFieldNames.Count > 0
            ? new HashSet<string>(ExcludedFieldNames, StringComparer.Ordinal)
            : null;

        const int kTopN = 30;
        foreach (var kvp in counts
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                            .Take(kTopN))
        {
            FieldNameStats.Add(new FieldNameStat
            {
                FieldName  = kvp.Key,
                Count      = kvp.Value,
                IsExcluded = excludedSet?.Contains(kvp.Key) ?? false,
            });
        }
    }

    private bool CanTakeSnapshot()    => !IsBusy;
    private bool CanDiff()            => !IsBusy && SelectedSnapshotA is not null && SelectedSnapshotB is not null;
    private bool CanDropSnapshotA()   => !IsBusy && SelectedSnapshotA is not null;
    private bool CanDropSnapshotB()   => !IsBusy && SelectedSnapshotB is not null;

    // -----------------------------------------------------------------------
    // Elapsed-time dispatcher timer (same pattern as ValueSearchViewModel —
    // request/response pipe can't stream progress, so just surface wall-clock
    // every 200ms so the user knows we haven't frozen).
    // -----------------------------------------------------------------------

    private void StartElapsedTimer(string verb)
    {
        _elapsedStopwatch = Stopwatch.StartNew();
        StatusMessage = $"{verb}… 0.0 s";
        _elapsedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tag = verb;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        if (_elapsedTimer is not null) _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedStopwatch = null;
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        if (_elapsedStopwatch is null) return;
        if (sender is DispatcherTimer t && t.Tag is string verb)
        {
            long ws = GetWorkingSet();
            string memNote = ws > 0
                ? $" — process {FormatMemory(ws)} (+{FormatMemory(ws - _captureStartWorkingSet)})"
                : string.Empty;
            StatusMessage = $"{verb}… {_elapsedStopwatch.Elapsed.TotalSeconds:F1} s{memNote}";
        }
    }

    /// <summary>Current process working-set in bytes, or 0 on failure
    /// (the rare path where Process.GetCurrentProcess throws — e.g.
    /// sandboxed test host). Caller treats 0 as "unknown" and skips
    /// the memory annotation rather than displaying garbage.</summary>
    private static long GetWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Human-friendly bytes formatter. Switches to GB at 1 GB
    /// so the snapshot status doesn't read "1843 MB" once we're past
    /// the gigabyte threshold. Signed input handled — negative deltas
    /// (memory dropped after GC) show as e.g. "-120 MB".</summary>
    private static string FormatMemory(long bytes)
    {
        if (bytes == 0) return "0 MB";
        long abs = Math.Abs(bytes);
        string sign = bytes < 0 ? "-" : string.Empty;
        if (abs >= 1024L * 1024 * 1024)
        {
            double gb = abs / (1024.0 * 1024.0 * 1024.0);
            return $"{sign}{gb.ToString("0.00", CultureInfo.InvariantCulture)} GB";
        }
        double mb = abs / (1024.0 * 1024.0);
        return $"{sign}{mb.ToString("0", CultureInfo.InvariantCulture)} MB";
    }
}

// ===========================================================================
// Local model — one captured snapshot held in memory for the session.
// ===========================================================================

public sealed partial class SnapshotEnvelope : ObservableObject
{
    public string   ServerId       { get; init; } = string.Empty;
    public string   Label          { get; set; }  = string.Empty;
    public DateTime CapturedAt     { get; init; }
    public string   CapturedAtUtc  { get; init; } = string.Empty;
    public string   GameBuildId    { get; init; } = string.Empty;
    public string   GameSessionId  { get; init; } = string.Empty;
    public int      TotalClasses   { get; init; }
    public int      TotalInstances { get; init; }
    public List<SnapshotClassEntry> Classes { get; init; } = new();

    /// <summary>Phase 29b — flipped by MainWindowViewModel when the
    /// current handshake's gameBuildId doesn't match this snapshot's
    /// gameBuildId (user reconnected to a different game). Visible in
    /// the snapshot dropdown via <see cref="DisplayName"/>.</summary>
    [ObservableProperty]
    private bool _isStale;

    /// <summary>Flat lookup: <c>"asm|ns|class|instance|offset"</c> →
    /// per-field value snapshot. Built once at capture time, reused on
    /// every Diff. Pre-computing this trades ~10 MB of RAM per snapshot
    /// for sub-second diff latency.</summary>
    public Dictionary<string, SnapshotFieldValue> FieldIndex { get; private set; }
        = new();

    /// <summary>Short label for status messages / dropdown display.
    /// Falls back to "#N (capture time)" if no user label was set.</summary>
    public string ShortLabel
        => string.IsNullOrEmpty(Label)
            ? CapturedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : $"\"{Label}\"";

    /// <summary>Heuristic UI-side memory cost of this envelope, in bytes.
    /// Empirical ratio from build #646 live-smoke on 활俠傳 / Mortal:
    /// 1.13M field-values consumed ~1.62 GB of UI WorkingSet delta →
    /// ~1500 bytes per FieldIndex entry once you account for the
    /// dictionary's bucket overhead, the SnapshotFieldValue object
    /// header, the per-field strings (assembly, class fqn, instance
    /// address, field name, type, value display), and the parallel
    /// Classes list. The estimate is rough — within 2× — but plenty
    /// good for "is this snapshot small or huge?" UX. Re-computed on
    /// demand from FieldIndex.Count so it doesn't bloat the envelope.</summary>
    public long EstimatedMemoryBytes
        => (long)FieldIndex.Count * 1500L;

    public string DisplayName
    {
        get
        {
            string body = string.IsNullOrEmpty(Label)
                ? $"{CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst / ~{FormatMb(EstimatedMemoryBytes)}"
                : $"{Label} — {CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst / ~{FormatMb(EstimatedMemoryBytes)}";
            return IsStale ? $"⚠ stale (different game) — {body}" : body;
        }
    }

    /// <summary>Format bytes for the dropdown line — MB unless huge,
    /// then GB. Shorter than the per-status line's FormatMemory so
    /// dropdowns stay readable.</summary>
    private static string FormatMb(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        if (bytes >= 1024L * 1024 * 1024)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
        double mb = bytes / (1024.0 * 1024.0);
        return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(DisplayName));

    /// <summary>Walk classes / statics / instance fields and stash each
    /// observed value in <see cref="FieldIndex"/>. Key shape:
    /// <c>"{assembly}|{ns}.{name}|{instance|static}|{offset}|{fieldName}"</c>.
    /// Field name is included in the key to handle the (rare) case where
    /// two fields share an offset (won't happen in normal C#, but our
    /// false-positive instances can show offset=0 for multiple
    /// anonymous-type fields — and we want each row diffable).</summary>
    public void BuildFieldIndex()
    {
        var map = new Dictionary<string, SnapshotFieldValue>(StringComparer.Ordinal);
        foreach (var c in Classes)
        {
            string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            bool synthetic = IsSyntheticClassName(c.Name);

            // Static fields ----------------------------------------------------
            if (c.Statics is { Count: > 0 })
            {
                foreach (var f in c.Statics)
                {
                    string key = $"{c.Assembly}|{fqn}|static|{f.Offset}|{f.Name}";
                    map[key] = new SnapshotFieldValue
                    {
                        Assembly       = c.Assembly,
                        ClassFqn       = fqn,
                        ClassName      = c.Name,
                        Namespace      = c.Namespace,
                        InstanceAddress= string.Empty,
                        IsStatic       = true,
                        FieldName      = f.Name,
                        FieldType      = f.Type,
                        FieldOffset    = f.Offset,
                        ValueDisplay   = FormatFieldValue(f),
                        IsSynthetic    = synthetic,
                    };
                }
            }

            // Instance fields --------------------------------------------------
            if (c.Instances is { Count: > 0 })
            {
                foreach (var inst in c.Instances)
                {
                    foreach (var f in inst.Fields)
                    {
                        string key = $"{c.Assembly}|{fqn}|{inst.Address}|{f.Offset}|{f.Name}";
                        map[key] = new SnapshotFieldValue
                        {
                            Assembly       = c.Assembly,
                            ClassFqn       = fqn,
                            ClassName      = c.Name,
                            Namespace      = c.Namespace,
                            InstanceAddress= inst.Address,
                            IsStatic       = false,
                            FieldName      = f.Name,
                            FieldType      = f.Type,
                            FieldOffset    = f.Offset,
                            ValueDisplay   = FormatFieldValue(f),
                            IsSynthetic    = synthetic,
                        };
                    }
                }
            }
        }
        FieldIndex = map;
    }

    /// <summary>Detection rule for compiler-generated / synthetic types
    /// per Strategy C. Conservative match — only names starting with
    /// '&lt;' are flagged. Includes <c>&lt;Module&gt;</c>,
    /// <c>&lt;&gt;f__AnonymousType*</c>, <c>&lt;&gt;c</c>,
    /// <c>&lt;Start&gt;d__N</c>, and the various async/iterator state
    /// machines the C# compiler emits. Avoids flagging legitimate
    /// generic types like <c>Dictionary`2</c> (those have backticks
    /// but no leading '&lt;').</summary>
    private static bool IsSyntheticClassName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name[0] == '<') return true;
        // Nested under a synthetic parent? Slash-form nested names like
        // "AdventsDrawer/<>c" → flag if any segment starts with '<'.
        foreach (var seg in name.Split('/'))
        {
            if (seg.Length > 0 && seg[0] == '<') return true;
        }
        return false;
    }

    /// <summary>Stringify a field's stored value into a stable
    /// comparison key + human-readable display. Falls through the
    /// JsonElement → string ladder for primitives, hex for pointers,
    /// quoted form for strings, and explicit error markers for the
    /// unreadable / open-generic-array / implausible-length cases so
    /// noise stays clearly distinguishable from real changes.</summary>
    private static string FormatFieldValue(SnapshotFieldEntry f)
    {
        if (!string.IsNullOrEmpty(f.Error))
        {
            // Phase 29a-v4 round 4: Mono static fields capture metadata
            // only — value reads removed to eliminate mono_class_vtable
            // calls that wedge the game's main thread post-RPC. Give
            // the row a friendly label instead of the raw error code.
            if (f.Error == "static_value_skipped_mono") return "(static, value not captured on Mono)";
            return $"(err: {f.Error})";
        }
        if (f.Value.HasValue && f.Value.Value.ValueKind != JsonValueKind.Null
                              && f.Value.Value.ValueKind != JsonValueKind.Undefined)
        {
            return f.Value.Value.ValueKind switch
            {
                JsonValueKind.String => f.Value.Value.GetString() ?? "(null)",
                JsonValueKind.Number => f.Value.Value.GetRawText(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                _ => f.Value.Value.GetRawText(),
            };
        }
        if (!string.IsNullOrEmpty(f.StringValue))
            return $"\"{f.StringValue}\"";
        if (!string.IsNullOrEmpty(f.Ptr))
            return $"ptr={f.Ptr}";
        if (!string.IsNullOrEmpty(f.ArrayError))
        {
            string len = f.ArrayErrorLength.HasValue
                ? $", len={f.ArrayErrorLength.Value}"
                : string.Empty;
            return $"(arr-err: {f.ArrayError}{len})";
        }
        if (f.ArrayElements is { Count: > 0 } arr)
            return $"[{arr.Length} elements, captured {arr.Count}]";
        if (f.ArrayElements is { Count: 0 } arr0)
            return $"[{arr0.Length} elements, empty]";
        return "(no value)";
    }
}

/// <summary>One row in the right-side stats panel — a field name + how
/// many diff rows it occurs on. The <see cref="IsExcluded"/> flag is
/// bound to a chip / checkbox so live toggling reflects the user's
/// current exclusion set.</summary>
public sealed partial class FieldNameStat : ObservableObject
{
    public required string FieldName { get; init; }
    public required int    Count     { get; init; }

    [ObservableProperty] private bool _isExcluded;
}

public sealed class SnapshotFieldValue
{
    public string Assembly        { get; init; } = string.Empty;
    public string ClassFqn        { get; init; } = string.Empty;
    public string ClassName       { get; init; } = string.Empty;
    public string Namespace       { get; init; } = string.Empty;
    public string InstanceAddress { get; init; } = string.Empty;
    public bool   IsStatic        { get; init; }
    public string FieldName       { get; init; } = string.Empty;
    public string FieldType       { get; init; } = string.Empty;
    public ulong  FieldOffset     { get; init; }
    public string ValueDisplay    { get; init; } = string.Empty;
    public bool   IsSynthetic     { get; init; }
}

// ===========================================================================
// Diff row — surfaces in the result DataGrid.
// ===========================================================================

/// <summary>Coarse classifier for a diff row's value shape — computed once
/// at row construction so the noise-reduction filters key off it in O(1).
/// "Pointer" rows are the re-allocation noise the user flagged: both sides
/// formatted as <c>ptr=0x...</c>, no semantic content change visible.
/// "String" carries <see cref="SnapshotDiffRow.MaxValueLength"/> so the
/// "hide large strings" filter (stack traces etc.) can apply a dynamic
/// threshold without re-classifying.</summary>
public enum DiffRowKind
{
    Other,
    Numeric,
    Pointer,
    String,
    Error,
}

public sealed class SnapshotDiffRow
{
    public required string Assembly        { get; init; }
    public required string ClassDisplay    { get; init; }   // "MainGame.PLY_WORK"
    public required string Field           { get; init; }
    public required string FieldType       { get; init; }
    public required string InstanceLabel   { get; init; }   // "0x..." or "static"
    public required string InstanceAddress { get; init; }   // raw for handoff (empty for static)
    public required ulong  FieldOffset     { get; init; }
    public required string OldValue        { get; init; }
    public required string NewValue        { get; init; }
    public required string Delta           { get; init; }
    public required string ChangeKind      { get; init; }   // "changed" | "added" | "removed"
    public required bool   IsSynthetic     { get; init; }

    /// <summary>Shape of the value(s) in this row. Drives the pointer-mask,
    /// value-range, and large-string filters. See <see cref="DiffRowKind"/>.</summary>
    public required DiffRowKind Kind        { get; init; }

    /// <summary>Old value parsed as <c>double</c> when <see cref="Kind"/>
    /// is <see cref="DiffRowKind.Numeric"/>; <c>null</c> for "added" or
    /// non-numeric rows. Used by the value-range filter.</summary>
    public required double? NumericOld     { get; init; }

    /// <summary>New value parsed as <c>double</c> when <see cref="Kind"/>
    /// is <see cref="DiffRowKind.Numeric"/>; <c>null</c> for "removed" or
    /// non-numeric rows.</summary>
    public required double? NumericNew     { get; init; }

    /// <summary>max(Old.Length, New.Length). Used only for the
    /// "hide large strings" filter so the threshold stays dynamic.</summary>
    public required int    MaxValueLength  { get; init; }

    public string OffsetLabel  => $"0x{FieldOffset:X}";

    public static SnapshotDiffRow Changed(SnapshotFieldValue a, SnapshotFieldValue b)
    {
        var (kind, oldNum, newNum) = ClassifyValues(a.ValueDisplay, b.ValueDisplay);
        return new()
        {
            Assembly        = a.Assembly,
            ClassDisplay    = a.ClassFqn,
            Field           = a.FieldName,
            FieldType       = a.FieldType,
            InstanceLabel   = a.IsStatic ? "static" : a.InstanceAddress,
            InstanceAddress = a.InstanceAddress,
            FieldOffset     = a.FieldOffset,
            OldValue        = a.ValueDisplay,
            NewValue        = b.ValueDisplay,
            Delta           = ComputeDelta(oldNum, newNum),
            ChangeKind      = "changed",
            IsSynthetic     = a.IsSynthetic || b.IsSynthetic,
            Kind            = kind,
            NumericOld      = oldNum,
            NumericNew      = newNum,
            MaxValueLength  = Math.Max(a.ValueDisplay.Length, b.ValueDisplay.Length),
        };
    }

    public static SnapshotDiffRow Added(SnapshotFieldValue b)
    {
        var (kind, _, newNum) = ClassifyValues(string.Empty, b.ValueDisplay);
        return new()
        {
            Assembly        = b.Assembly,
            ClassDisplay    = b.ClassFqn,
            Field           = b.FieldName,
            FieldType       = b.FieldType,
            InstanceLabel   = b.IsStatic ? "static" : b.InstanceAddress,
            InstanceAddress = b.InstanceAddress,
            FieldOffset     = b.FieldOffset,
            OldValue        = string.Empty,
            NewValue        = b.ValueDisplay,
            Delta           = "+",
            ChangeKind      = "added",
            IsSynthetic     = b.IsSynthetic,
            Kind            = kind,
            NumericOld      = null,
            NumericNew      = newNum,
            MaxValueLength  = b.ValueDisplay.Length,
        };
    }

    /// <summary>"Unchanged" row — value identical between A and B but the
    /// user opted in to capturing them via <c>IncludeUnchangedRows</c>.
    /// Carries the same numeric/length metadata as <see cref="Changed"/>
    /// so the value-range and large-string filters apply uniformly.</summary>
    public static SnapshotDiffRow Unchanged(SnapshotFieldValue a, SnapshotFieldValue b)
    {
        var (kind, oldNum, newNum) = ClassifyValues(a.ValueDisplay, b.ValueDisplay);
        return new()
        {
            Assembly        = a.Assembly,
            ClassDisplay    = a.ClassFqn,
            Field           = a.FieldName,
            FieldType       = a.FieldType,
            InstanceLabel   = a.IsStatic ? "static" : a.InstanceAddress,
            InstanceAddress = a.InstanceAddress,
            FieldOffset     = a.FieldOffset,
            OldValue        = a.ValueDisplay,
            NewValue        = b.ValueDisplay,
            Delta           = "=",
            ChangeKind      = "unchanged",
            IsSynthetic     = a.IsSynthetic || b.IsSynthetic,
            Kind            = kind,
            NumericOld      = oldNum,
            NumericNew      = newNum,
            MaxValueLength  = Math.Max(a.ValueDisplay.Length, b.ValueDisplay.Length),
        };
    }

    public static SnapshotDiffRow Removed(SnapshotFieldValue a)
    {
        var (kind, oldNum, _) = ClassifyValues(a.ValueDisplay, string.Empty);
        return new()
        {
            Assembly        = a.Assembly,
            ClassDisplay    = a.ClassFqn,
            Field           = a.FieldName,
            FieldType       = a.FieldType,
            InstanceLabel   = a.IsStatic ? "static" : a.InstanceAddress,
            InstanceAddress = a.InstanceAddress,
            FieldOffset     = a.FieldOffset,
            OldValue        = a.ValueDisplay,
            NewValue        = string.Empty,
            Delta           = "-",
            ChangeKind      = "removed",
            IsSynthetic     = a.IsSynthetic,
            Kind            = kind,
            NumericOld      = oldNum,
            NumericNew      = null,
            MaxValueLength  = a.ValueDisplay.Length,
        };
    }

    /// <summary>Classify Old/New value pair into a <see cref="DiffRowKind"/>
    /// + extract numeric values when applicable. One side may be empty
    /// (added: Old empty / removed: New empty); classification then keys
    /// off the populated side only. Side-classify order: error → pointer
    /// → numeric → quoted string → other.</summary>
    private static (DiffRowKind kind, double? oldNum, double? newNum)
        ClassifyValues(string oldVal, string newVal)
    {
        var oSide = ClassifySide(oldVal);
        var nSide = ClassifySide(newVal);

        // Any error marker on either side dominates.
        if (oSide.IsError || nSide.IsError)
            return (DiffRowKind.Error, null, null);

        // Pointer-only diff — both populated sides are ptr= (or one side
        // is empty for added/removed).
        bool oPtrOk = oSide.IsPointer || oldVal.Length == 0;
        bool nPtrOk = nSide.IsPointer || newVal.Length == 0;
        if (oPtrOk && nPtrOk && (oSide.IsPointer || nSide.IsPointer))
            return (DiffRowKind.Pointer, null, null);

        // Numeric — both populated sides parse as double (empty side ok).
        bool oNumOk = oSide.Numeric.HasValue || oldVal.Length == 0;
        bool nNumOk = nSide.Numeric.HasValue || newVal.Length == 0;
        if (oNumOk && nNumOk && (oSide.Numeric.HasValue || nSide.Numeric.HasValue))
            return (DiffRowKind.Numeric, oSide.Numeric, nSide.Numeric);

        // Quoted string on either side → String row.
        if (oSide.IsString || nSide.IsString)
            return (DiffRowKind.String, null, null);

        return (DiffRowKind.Other, null, null);
    }

    private readonly record struct SideClassification(
        bool IsError, bool IsPointer, bool IsString, double? Numeric);

    private static SideClassification ClassifySide(string s)
    {
        if (string.IsNullOrEmpty(s))
            return new SideClassification(false, false, false, null);
        if (s.StartsWith("(err:", StringComparison.Ordinal) ||
            s.StartsWith("(arr-err:", StringComparison.Ordinal))
            return new SideClassification(true, false, false, null);
        if (s.StartsWith("ptr=", StringComparison.Ordinal))
            return new SideClassification(false, true, false, null);
        if (s[0] == '"')
            return new SideClassification(false, false, true, null);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return new SideClassification(false, false, false, d);
        return new SideClassification(false, false, false, null);
    }

    /// <summary>Best-effort numeric delta using the pre-classified numeric
    /// values. Returns sign-prefixed decimal for numeric pairs, "+" /
    /// "-" sentinels for added / removed, "…" otherwise.</summary>
    private static string ComputeDelta(double? oldNum, double? newNum)
    {
        if (oldNum.HasValue && newNum.HasValue)
        {
            double d = newNum.Value - oldNum.Value;
            return d switch
            {
                > 0 => $"+{d.ToString("0.######", CultureInfo.InvariantCulture)}",
                < 0 =>  d.ToString("0.######",  CultureInfo.InvariantCulture),
                _   => "0",
            };
        }
        return "…";
    }
}
