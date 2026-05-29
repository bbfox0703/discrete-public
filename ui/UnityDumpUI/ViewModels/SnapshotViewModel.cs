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
using System.Collections.Specialized;
using System.ComponentModel;
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
    /// having been clamped on an earlier x86 connection.
    /// 2026-05-29: lowered 512 → 256 after the Utawarerumono live-smoke —
    /// a 22K-class / 665K-instance game decoded 174K instances in ~11 min and
    /// pushed the game to 10 GB + the UI to 14.7 GB. Per-class 256 trims the
    /// high-population classes; the DLL-side instanceDecodeBudget (50K) is the
    /// hard total bound, and the ascending-population plan sort keeps the
    /// low-population game-state classes when truncation kicks in.</summary>
    private const int kDefaultInstanceCapX64 = 256;
    private const int kDefaultArrayCapX64    = 256;

    /// <summary>Clamped defaults for 32-bit Mono games. Chosen 2026-05-27
    /// after the build #646 活俠傳 CTD: the DLL caches the whole
    /// snapshot's JSON payload inside the game process, so on a target
    /// with a 4 GB user-space ceiling the post-bump 512/256 defaults
    /// pushed snap #2 past OOM. 256/64 leaves comfortable headroom
    /// (smaller cache + smaller per-class metadata). User can still
    /// push higher manually via the NumericUpDowns if they accept the
    /// risk — and the DLL also has a 3 GB pre-check refusal as a hard
    /// backstop.</summary>
    private const int kDefaultInstanceCapX86 = 256;
    private const int kDefaultArrayCapX86    = 64;

    // ── Phase 29c persistence layer ────────────────────────────────────
    private readonly UserSettingsStore?  _settingsStore;
    private          UserSettings        _settings;
    private          SnapshotStore?      _store;
    private          string?             _currentGameModule;

    /// <summary>Active per-game SQLite DB, or <c>null</c> when not yet
    /// connected to a game (no handshake). Switched on every successful
    /// connect via <see cref="OnGameDbContextAsync"/>.</summary>
    public SnapshotStore? Store => _store;

    /// <summary>The user-settings instance shared with the Settings dialog.
    /// Mutate through <see cref="ApplySettingsAsync"/> so the change
    /// reaches disk + downstream consumers atomically.</summary>
    public UserSettings Settings => _settings;

    public SnapshotViewModel(
        BackendAdapter backend,
        FileLogger logger,
        UserSettingsStore? settingsStore = null,
        UserSettings? initialSettings = null)
    {
        _backend       = backend;
        _logger        = logger;
        _settingsStore = settingsStore;
        _settings      = initialSettings ?? new UserSettings();
        // Re-fire the rollup banner whenever the list mutates (Take /
        // Drop / ClearAll). DisplayName for each envelope is per-row
        // and doesn't need this — only the cumulative count + memory
        // estimate does.
        Snapshots.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(TotalSnapshotsMemoryBanner));
    }

    /// <summary>Phase 29c-3: open (or switch to) the per-game DB keyed
    /// by <paramref name="gameBuildId"/>. Reloads <see cref="Snapshots"/>
    /// from disk metadata only (lazy load — class blobs stay on disk
    /// until first diff selection touches them). Called by
    /// <c>MainWindowViewModel.ConnectAsync</c> after a successful
    /// handshake. Idempotent — if the same build_id is already open the
    /// call is a no-op.
    /// <para/>
    /// Caller is expected to have flagged in-memory unsaved snapshots
    /// for the user (via <see cref="GetUnsavedDirtySnapshots"/>) BEFORE
    /// invoking this — switching DB drops the in-memory list.</summary>
    public async Task OnGameDbContextAsync(
        string gameBuildId, string gameModule, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameBuildId))
        {
            _logger.Write(SnapshotStore.LogCategory,
                "OnGameDbContextAsync: empty buildId — leaving snapshot store closed");
            return;
        }
        if (_store is not null && string.Equals(_store.GameBuildId, gameBuildId, StringComparison.Ordinal))
        {
            // Same game build → keep the existing store + in-memory list.
            return;
        }

        // Switching context: tear down any prior store + clear in-memory
        // list (caller is responsible for warning the user about unsaved
        // snapshots first).
        if (_store is not null)
        {
            try { await _store.DisposeAsync().ConfigureAwait(true); } catch { /* best effort */ }
            _store = null;
        }
        Snapshots.Clear();
        SelectedSnapshotA = null;
        SelectedSnapshotB = null;
        DiffRows.Clear();
        FilteredDiffRows.ReplaceAll(null);

        try
        {
            string dir = ResolveSnapshotDbDirectory();
            string fileName = SnapshotStore.ResolveDatabaseFileName(gameModule, gameBuildId);
            string dbPath = System.IO.Path.Combine(dir, fileName);
            _store = await SnapshotStore.OpenAsync(
                dbPath, gameBuildId, gameModule,
                logger: _logger, ct: ct).ConfigureAwait(true);
            _currentGameModule = gameModule;

            // Lazy-load metadata only — class blobs decompress on
            // first diff selection (see EnsureLoadedAsync).
            var rows = await _store.ListSnapshotsAsync(ct).ConfigureAwait(true);
            foreach (var meta in rows)
            {
                Snapshots.Add(BuildEnvelopeFromMeta(meta));
            }
            RecomputeStaleFlags(gameBuildId, currentSessionId: null);

            // Phase 29c-6: auto-prune on open so cross-session bloat
            // accumulated while the UI was closed gets reclaimed.
            await AutoPruneAndMaybeVacuumAsync(ct).ConfigureAwait(true);

            await RefreshDbStatusAsync(ct).ConfigureAwait(true);
            StatusMessage = $"Snapshot DB: {fileName} — {Snapshots.Count:N0} saved snapshot(s), {FormatMemory(_dbTotalBytes)}.";
            _logger.Write(SnapshotStore.LogCategory,
                $"opened per-game DB {fileName} for buildId={gameBuildId} ({Snapshots.Count} saved)");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to open snapshot DB: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory,
                $"OnGameDbContextAsync FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Phase 29e-7 — distinct (assembly, class FQN) pairs from
    /// the open per-game DB's metadata, with no blob hydration. Returns
    /// empty when no store is open. Soft-fails to empty on error. The
    /// Class Pivot tab uses this to seed its class / assembly
    /// autocomplete before the user analyses anything.</summary>
    public async Task<IReadOnlyList<PersistedClassRef>> GetPersistedClassesAsync(
        CancellationToken ct = default)
    {
        if (_store is null) return System.Array.Empty<PersistedClassRef>();
        try
        {
            return await _store.ListDistinctClassesAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Write(SnapshotStore.LogCategory,
                $"GetPersistedClassesAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            return System.Array.Empty<PersistedClassRef>();
        }
    }

    /// <summary>Called by MainWindowViewModel once UserSettingsStore
    /// finishes loading from disk. Hot-swaps the in-memory settings so
    /// later TakeSnapshot / OnGameDbContext calls honour the disk values.</summary>
    public void ApplyLoadedSettings(UserSettings loaded)
    {
        if (loaded is null) return;
        _settings = loaded;
        OnPropertyChanged(nameof(DbStatusLine));
    }

    /// <summary>One-line summary of the active per-game SQLite store.
    /// Bound to the Snapshot tab status line. Empty when no DB is open
    /// (UI hides the row in that case).</summary>
    public string DbStatusLine
    {
        get
        {
            if (_store is null) return string.Empty;
            int count = Snapshots.Count(s => s.PersistedId is not null);
            string filename = System.IO.Path.GetFileName(_store.DatabasePath);
            string threshold = _settings.AutoPruneThresholdBytes > 0
                ? $" / {FormatMemory(_settings.AutoPruneThresholdBytes)}"
                : string.Empty;
            return $"DB: {filename}  ·  {count:N0} saved  ·  {FormatMemory(_dbTotalBytes)}{threshold}";
        }
    }
    private long _dbTotalBytes;

    /// <summary>Recompute <see cref="DbStatusLine"/> with a fresh SUM
    /// query. Called after every save / delete / prune so the line
    /// reflects current on-disk usage.</summary>
    public async Task RefreshDbStatusAsync(CancellationToken ct = default)
    {
        if (_store is null)
        {
            _dbTotalBytes = 0;
        }
        else
        {
            _dbTotalBytes = await _store.GetTotalPayloadBytesAsync(ct).ConfigureAwait(true);
        }
        OnPropertyChanged(nameof(DbStatusLine));
    }

    /// <summary>Path to the folder containing the active DB, or the
    /// resolved default directory when no DB is open. Used by the "📁
    /// Open folder" button. The folder is created lazily on first
    /// access so the open-button never fails for a missing dir.</summary>
    public string SnapshotDbFolder
    {
        get
        {
            string dir = _store is not null
                ? System.IO.Path.GetDirectoryName(_store.DatabasePath) ?? ResolveSnapshotDbDirectory()
                : ResolveSnapshotDbDirectory();
            try { System.IO.Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            return dir;
        }
    }

    /// <summary>Persist + apply a new UserSettings instance. Triggers a
    /// re-open of the snapshot store if <c>SnapshotDbDirectory</c>
    /// changed; otherwise just kicks an auto-prune so the threshold
    /// update takes immediate effect.</summary>
    public async Task ApplySettingsAsync(UserSettings updated, CancellationToken ct = default)
    {
        if (updated is null) return;
        bool dirChanged = !string.Equals(
            _settings.SnapshotDbDirectory ?? string.Empty,
            updated.SnapshotDbDirectory   ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        _settings = updated;
        if (_settingsStore is not null)
        {
            try { await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(true); }
            catch (Exception ex)
            {
                ErrorMessage = $"Settings save failed: {ex.Message}";
                _logger.Write(SnapshotStore.LogCategory,
                    $"ApplySettingsAsync save FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (dirChanged && _store is not null)
        {
            // Reopen against the same buildId/module under the new dir.
            string buildId = _store.GameBuildId;
            string module  = _currentGameModule ?? "unknown";
            await _store.DisposeAsync().ConfigureAwait(true);
            _store = null;
            await OnGameDbContextAsync(buildId, module, ct).ConfigureAwait(true);
        }
        else
        {
            await AutoPruneAndMaybeVacuumAsync(ct).ConfigureAwait(true);
        }
        OnPropertyChanged(nameof(DbStatusLine));
    }

    private string ResolveSnapshotDbDirectory()
    {
        string? overridePath = _settings.SnapshotDbDirectory;
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Same parent root as the logger so user finds them together.
        string sub = Constants.LogRootSubpath;
        int slash = sub.IndexOf('\\');
        string appRoot = slash > 0 ? sub[..slash] : sub;
        return System.IO.Path.Combine(local, appRoot, "snapshots");
    }

    private async Task AutoPruneAndMaybeVacuumAsync(CancellationToken ct)
    {
        if (_store is null) return;
        var result = await _store.PruneAsync(_settings.AutoPruneThresholdBytes, ct: ct).ConfigureAwait(true);
        if (result.Removed > 0)
        {
            // Drop pruned snapshots from the in-memory list too.
            var alive = await _store.ListSnapshotsAsync(ct).ConfigureAwait(true);
            var aliveIds = new HashSet<long>(alive.Select(m => m.Id));
            for (int i = Snapshots.Count - 1; i >= 0; i--)
            {
                var env = Snapshots[i];
                if (env.PersistedId is long pid && !aliveIds.Contains(pid))
                {
                    if (ReferenceEquals(SelectedSnapshotA, env)) SelectedSnapshotA = null;
                    if (ReferenceEquals(SelectedSnapshotB, env)) SelectedSnapshotB = null;
                    Snapshots.RemoveAt(i);
                }
            }
            StatusMessage = $"Auto-pruned {result.Removed:N0} snapshot(s) (freed {FormatMemory(result.FreedBytes)}) to stay under {FormatMemory(_settings.AutoPruneThresholdBytes)}.";

            if (await _store.ShouldVacuumAsync(ct: ct).ConfigureAwait(true))
            {
                await _store.VacuumAsync(ct).ConfigureAwait(true);
            }
        }
    }

    private static SnapshotEnvelope BuildEnvelopeFromMeta(PersistedSnapshotMeta meta)
    {
        // Lazy envelope — Classes / FieldIndex stay empty until the
        // user picks this snapshot for diff. EnsureLoadedAsync hydrates.
        DateTime captured;
        if (!DateTime.TryParse(meta.CapturedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out captured))
        {
            captured = DateTime.UtcNow;
        }
        return new SnapshotEnvelope
        {
            ServerId       = string.Empty,
            Label          = meta.Label ?? string.Empty,
            CapturedAt     = captured.ToLocalTime(),
            CapturedAtUtc  = meta.CapturedAtUtc,
            GameBuildId    = meta.GameBuildId,
            GameSessionId  = meta.GameSessionId,
            TotalClasses   = meta.TotalClasses,
            TotalInstances = meta.TotalInstances,
            Classes        = new List<SnapshotClassEntry>(),  // lazy
            PersistedId    = meta.Id,
            PersistedBytes = meta.PayloadBytes,
            IsDirty        = false,
            ClassesLoaded  = false,
        };
    }

    /// <summary>Hydrate an envelope's <see cref="SnapshotEnvelope.Classes"/>
    /// + <see cref="SnapshotEnvelope.FieldIndex"/> from disk on demand.
    /// No-op if already loaded or if this envelope isn't persisted.</summary>
    public async Task EnsureLoadedAsync(SnapshotEnvelope env, CancellationToken ct = default)
    {
        if (env.ClassesLoaded) return;
        if (_store is null || env.PersistedId is not long id) return;
        var classes = await _store.LoadClassesAsync(id, ct).ConfigureAwait(true);
        env.Classes.AddRange(classes);
        // Phase 29g-1: dedup metadata strings on the lazily-loaded tree too
        // (each blob deserializes its own copies of "System.Int32" etc.).
        SnapshotInterner.Intern(env.Classes);
        // Phase 29g-2: don't eager-build the FieldIndex on load — only Diff /
        // SPC / address-lookup need it, and they call EnsureFieldIndex on
        // demand. Pivot/ArrayPivot read Classes directly. Just refresh the
        // cheap cached count for the memory estimate + status line.
        env.RecomputeFieldValueCount();
        env.ClassesLoaded = true;
        _logger.Write(SnapshotStore.LogCategory,
            $"lazy-loaded snapshot id={id} → {env.FieldValueCount:N0} field-values (index lazy)");
    }

    /// <summary>Flip <see cref="SnapshotEnvelope.IsStale"/> on every
    /// envelope whose <c>GameBuildId</c> differs from the active game.
    /// Same-build envelopes from prior sessions stay NOT stale (cross-
    /// session diff still works for statics — see Phase 29c-5).</summary>
    private void RecomputeStaleFlags(string activeBuildId, string? currentSessionId)
    {
        foreach (var s in Snapshots)
        {
            s.IsStale = !string.Equals(s.GameBuildId, activeBuildId, StringComparison.Ordinal);
        }
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
    /// via a SINGLE Reset CollectionChanged event (see
    /// <see cref="BulkObservableCollection{T}.ReplaceAll"/>). Prior
    /// implementation used the standard <see cref="ObservableCollection{T}"/>
    /// with Clear + per-row Add — that fired 740K+ Add events on the
    /// "show all added rows" toggle and the DataGrid re-layout each
    /// time, hanging the UI for minutes (2026-05-28 user feedback).</summary>
    public BulkObservableCollection<SnapshotDiffRow> FilteredDiffRows { get; } = new();

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
    /// shouldn't have to know where data lives). On connect, defaults to
    /// "Assembly-CSharp" when that assembly exists (the usual gameplay
    /// assembly) so the common case doesn't capture the whole heap.</summary>
    [ObservableProperty] private string _scopeAssembliesText = string.Empty;

    /// <summary>Assembly names reported by the game — drives the Scope
    /// picker so the user can append an assembly without typing it.</summary>
    public ObservableCollection<string> KnownScopeAssemblies { get; } = new();

    /// <summary>Picker selection — appending a chosen assembly to
    /// <see cref="ScopeAssembliesText"/> (comma-separated, deduped), then
    /// resetting so the same item can be re-picked.</summary>
    [ObservableProperty] private string? _selectedScopeAssembly;

    partial void OnSelectedScopeAssemblyChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var parts = ScopeAssembliesText
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!parts.Any(p => string.Equals(p, value, StringComparison.Ordinal)))
        {
            parts.Add(value);
            ScopeAssembliesText = string.Join(", ", parts);
        }
        SelectedScopeAssembly = null;   // reset for re-pick
    }

    /// <summary>Fetch the game's assemblies (post-connect) to populate the
    /// Scope picker and default the scope to "Assembly-CSharp" when present
    /// and the user hasn't set one. Soft-fails — a getAssemblies error must
    /// not block the connect flow.</summary>
    public async Task LoadScopeAssembliesAsync(System.Threading.CancellationToken ct = default)
    {
        try
        {
            var res = await _backend.GetAssembliesAsync(ct).ConfigureAwait(true);
            var names = res.Assemblies
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            KnownScopeAssemblies.Clear();
            foreach (var n in names) KnownScopeAssemblies.Add(n);

            if (string.IsNullOrWhiteSpace(ScopeAssembliesText) &&
                names.Any(n => string.Equals(n, "Assembly-CSharp", StringComparison.Ordinal)))
            {
                ScopeAssembliesText = "Assembly-CSharp";
            }
        }
        catch (Exception ex)
        {
            _logger.Write(SnapshotStore.LogCategory,
                $"LoadScopeAssembliesAsync soft-fail: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Per-class instance cap (server-side hard limit 1024).
    /// Raised from 100 → 512 on 2026-05-26 user feedback: 活俠傳 player
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

    /// <summary>Phase 29f (Layer B) — capture each reference-type array
    /// element's own fields one level deep, so the Class Pivot tab can
    /// inner-pivot array elements by an inner field. OFF by default
    /// (multiplies capture size). On x64 it captures every reference array
    /// (blanket); on x86 it is disabled unless
    /// <see cref="ArrayElementTargetsText"/> nominates targets (OOM
    /// safety — the DLL refuses 32-bit blanket capture).</summary>
    [ObservableProperty] private bool _captureArrayElementFields;

    /// <summary>Per-element inner-field cap (server hard-cap 64). Only used
    /// when <see cref="CaptureArrayElementFields"/> is on.</summary>
    [ObservableProperty] private int _elementFieldCap = 16;

    /// <summary>Optional comma/semicolon-separated inner-pivot targets —
    /// <c>"Namespace.Class"</c> (all its reference arrays) or
    /// <c>"Namespace.Class.fieldName"</c>. Required to enable Layer B
    /// capture on 32-bit targets; on x64 leave blank for blanket capture.</summary>
    [ObservableProperty] private string _arrayElementTargetsText = string.Empty;

    /// <summary>Capture static-field VALUES (IL2CPP). Default OFF — static
    /// metadata (name/type/offset) is always captured, but the values are
    /// skipped unless opted in. Mono always captures static metadata only
    /// (the vtable-freeze fix), so this toggle has no effect on Mono.
    /// Note: statics are a small fraction of snapshot memory — the bulk is
    /// instance fields — so this is a modest lever, not the main one.</summary>
    [ObservableProperty] private bool _captureStaticFieldValues;

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

    /// <summary>Backend reported by the handshake ("Mono" / "IL2CPP").
    /// Drives the bitness banner so it doesn't mislabel an IL2CPP game as
    /// "Mono". Empty until connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameMemoryBanner))]
    private string _gameBackend = string.Empty;

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
            string be = string.IsNullOrEmpty(GameBackend) ? "Unity" : GameBackend;
            return $"Game: {bits} {be} — WorkingSet {ws}, committed {pu}{suffix}";
        }
    }

    /// <summary>Hook fired from <c>MainWindowViewModel</c> after every
    /// successful handshake. Updates the bitness + memory display and,
    /// on 32-bit targets, clamps <see cref="InstanceCap"/> /
    /// <see cref="ArrayCap"/> downward to the safe defaults (256 / 64)
    /// if they're currently above. Never clamps upward — user manually-
    /// chosen lower caps stay respected.</summary>
    public void OnGameConnected(int pointerSize, ulong workingSet, ulong privateUsage,
                                string backend = "")
    {
        GamePointerSize  = pointerSize;
        GameWorkingSet   = workingSet;
        GamePrivateUsage = privateUsage;
        if (!string.IsNullOrEmpty(backend)) GameBackend = backend;

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

    // ── "Changed" trichotomy (Phase 29-polish, 2026-05-29) ─────────────
    // The single ShowChanged toggle was split into three independent
    // sub-toggles so the user can express direction-based filters
    // naturally as set unions:
    //
    //   • "value ≥ old"     → ShowUnchanged + ShowIncreased
    //   • "value ≤ old"     → ShowUnchanged + ShowDecreased
    //   • "any direction"   → ShowIncreased + ShowDecreased
    //   • "non-numeric Δ"   → ShowOtherChanged (string / pointer / error)
    //
    // Combined with the numeric ValueRangeMin/Max filter this answers
    // the canonical CE-style "I don't know the exact value but it's
    // somewhere in 0–1000 and it just went down" workflow.
    //
    // Defaults preserve the previous "ShowChanged = true" behaviour —
    // all three sub-kinds default ON so first-time users see the same
    // unfiltered changed result as before.

    /// <summary>Show numeric rows where <c>new &gt; old</c>. Default ON.
    /// Combined with <see cref="ShowUnchanged"/> expresses the ≥ filter
    /// (value grew or stayed the same).</summary>
    [ObservableProperty] private bool _showIncreased = true;

    /// <summary>Show numeric rows where <c>new &lt; old</c>. Default ON.
    /// Combined with <see cref="ShowUnchanged"/> expresses the ≤ filter
    /// (value shrank or stayed the same).</summary>
    [ObservableProperty] private bool _showDecreased = true;

    /// <summary>Show "changed" rows that aren't numerically comparable —
    /// string content changes, pointer-address changes, error rows, or
    /// numeric rows whose Old or New side failed to parse. Default ON
    /// so legacy "Changed = true" behaviour is preserved out-of-box.</summary>
    [ObservableProperty] private bool _showOtherChanged = true;

    /// <summary>Show "added" rows (in B but not A). Default OFF.</summary>
    [ObservableProperty] private bool _showAdded;

    /// <summary>Show "removed" rows (in A but not B). Default OFF.</summary>
    [ObservableProperty] private bool _showRemoved;

    /// <summary>Show "unchanged" rows (key present in both with equal
    /// value). Default OFF as of 2026-05-28 — opting in to
    /// <see cref="IncludeUnchangedRows"/> at capture time is the
    /// "I want to see them" signal; turning this on then is the
    /// second step. With unchanged-row counts often 10–100× the
    /// changed-row count, default-ON drowned the diff result.</summary>
    [ObservableProperty] private bool _showUnchanged;

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
    /// <para/>
    /// This is the <b>committed</b> value — the actual filter input. UI
    /// binds the NumericUpDowns to <see cref="PendingValueRangeMin"/> /
    /// <see cref="PendingValueRangeMax"/> and an Apply button copies
    /// pending → committed. Without that indirection every spinner click
    /// fired a 500ms RebuildFilteredRows over 800K+ DiffRows and the UI
    /// looked frozen (2026-05-28 user feedback).
    /// Typed as <c>decimal?</c> to match Avalonia 12 <c>NumericUpDown.Value</c>
    /// — the value-range check casts to <c>double</c> internally.</summary>
    [ObservableProperty] private decimal? _valueRangeMin;

    /// <summary>Upper bound (inclusive) for the numeric value range filter.
    /// <c>null</c> = no upper bound. See <see cref="ValueRangeMin"/> for
    /// the pending/committed split rationale.</summary>
    [ObservableProperty] private decimal? _valueRangeMax;

    /// <summary>Pending lower bound bound to the Min NumericUpDown. Not
    /// applied to the filter until <see cref="ApplyValueRangeFilter"/>
    /// runs.</summary>
    [ObservableProperty] private decimal? _pendingValueRangeMin;

    /// <summary>Pending upper bound bound to the Max NumericUpDown.</summary>
    [ObservableProperty] private decimal? _pendingValueRangeMax;

    /// <summary>Commit pending Min/Max into the live filter. Triggers
    /// a single RebuildFilteredRows pass via the existing
    /// <c>OnValueRangeMin/MaxChanged</c> hooks.</summary>
    [RelayCommand]
    public void ApplyValueRangeFilter()
    {
        ValueRangeMin = PendingValueRangeMin;
        ValueRangeMax = PendingValueRangeMax;
    }

    /// <summary>Null both pending + committed bounds and rebuild — the
    /// only safe way to escape "I typed a value, now I can't unset it"
    /// (NumericUpDown can't go back to empty via spinner clicks).</summary>
    [RelayCommand]
    public void ClearValueRangeFilter()
    {
        PendingValueRangeMin = null;
        PendingValueRangeMax = null;
        ValueRangeMin = null;
        ValueRangeMax = null;
    }

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
    partial void OnShowIncreasedChanged(bool value)       => RebuildFilteredRows();
    partial void OnShowDecreasedChanged(bool value)       => RebuildFilteredRows();
    partial void OnShowOtherChangedChanged(bool value)    => RebuildFilteredRows();
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

    /// <summary>Live tally of the current filter pass — separated from
    /// <see cref="StatusMessage"/> because the breakdown has to refresh
    /// on every filter-property change, but the Diff line (which records
    /// "A → B: changed/added/removed in N ms") shouldn't be overwritten
    /// when the user just toggles a checkbox. Wired to a separate
    /// TextBlock in the snapshot tab so each line owns its own
    /// reactivity. Empty until the first <see cref="RebuildFilteredRows"/>
    /// call after a diff produces rows.</summary>
    [ObservableProperty] private string _filterResultLine = string.Empty;

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
        // This phase dominates on big targets (60-90s on 活俠傳 with
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

            // Phase 29f (Layer B) — element inner-field capture opt-in.
            string[]? elemTargets = null;
            if (!string.IsNullOrWhiteSpace(ArrayElementTargetsText))
            {
                var parts = ArrayElementTargetsText
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0) elemTargets = parts;
            }

            var begin = await _backend.SnapshotBeginAsync(
                scope, cap, arr, IncludeSystemAssemblies,
                CaptureArrayElementFields,
                CaptureArrayElementFields ? ElementFieldCap : (int?)null,
                elemTargets,
                CaptureStaticFieldValues,
                ct).ConfigureAwait(true);

            // The DLL echoes the EFFECTIVE capture state after its x86
            // blanket gate. If the user asked for Layer B capture but the
            // server disabled it (32-bit, no targets), say so plainly.
            if (CaptureArrayElementFields && !begin.ArrayElementFields && string.IsNullOrEmpty(begin.Error))
            {
                StatusMessage = "Note: array element-field capture was disabled by the DLL " +
                    "(32-bit target needs explicit targets — set \"Array element targets\" to a " +
                    "class.field, e.g. Mortal.Core.GameStat._statAdditionData).";
            }
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
            // count by ~3x — on 活俠傳's 3.6K-class workload that's 37
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

            // Phase 29g-1: dedup the millions of duplicated metadata strings
            // (field Name/Type/From, class Assembly/Namespace/Name, ElementType)
            // to one shared instance each before they're retained in the envelope.
            SnapshotInterner.Intern(classes);

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
            // Phase 29g-2: don't build the FieldIndex at capture — it was the
            // #2 resident cost (12.2M SnapshotFieldValue + 432 MB dict arrays,
            // all snapshots). Diff/SPC build it on demand via EnsureFieldIndex;
            // here we only cache the cheap field-value count for the status
            // line + memory estimate.
            env.RecomputeFieldValueCount();

            // Phase 29c-4: brand-new envelope is in-memory only until
            // user clicks 💾 (or AutoSaveSnapshots is ON, below).
            env.IsDirty       = true;
            env.ClassesLoaded = true;
            Snapshots.Add(env);
            // Auto-pick: if this is the first snapshot, set A; if it's the
            // second (and no B yet), set B; otherwise leave selections
            // alone so the user keeps control.
            if (SelectedSnapshotA is null)      SelectedSnapshotA = env;
            else if (SelectedSnapshotB is null) SelectedSnapshotB = env;

            // Phase 29c-4: auto-save on capture if the user opted in via
            // Settings dialog. Failure is non-fatal — envelope stays dirty.
            if (_settings.AutoSaveSnapshots && _store is not null)
            {
                try { await SaveSnapshotAsync(env).ConfigureAwait(true); }
                catch (Exception saveEx)
                {
                    _logger.Write(SnapshotStore.LogCategory,
                        $"auto-save FAILED for fresh capture: {saveEx.GetType().Name}: {saveEx.Message}");
                }
            }

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
            // Partial-capture warning. The DLL stops decoding when it hits the
            // memory watchdog or the total-instance budget; with the ascending-
            // population plan sort the dropped classes are the high-population
            // ones (low-population game-state classes are kept).
            string truncNote = string.Empty;
            if (begin.TruncatedByMemory)
                truncNote = "  ⚠ PARTIAL: memory budget hit — high-population classes dropped. " +
                            "Narrow the assembly scope, or raise memHeadroomMB / restart the game for more.";
            else if (begin.TruncatedByCount)
                truncNote = "  ⚠ PARTIAL: instance budget reached — high-population classes dropped. " +
                            "Narrow the assembly scope to capture specific classes fully.";
            StatusMessage = $"Snapshot #{Snapshots.Count} {label} — {env.TotalClasses:N0} classes, {env.TotalInstances:N0} instances, {env.FieldValueCount:N0} field-values (captured in {sw.Elapsed.TotalSeconds:F1}s across {chunkCount} chunks{excluded}{cacheNote}{memNote}{gameMemNote}).{truncNote}";
            _logger.Write(Constants.LogCategoryView,
                $"Snapshot: id={begin.SnapshotId} classes={env.TotalClasses} instances={env.TotalInstances} fieldValues={env.FieldValueCount} elapsed={sw.Elapsed.TotalSeconds:F1}s excluded={begin.ExcludedSystemAssemblies} workingSet={wsEnd} workingSetDelta={wsEnd - _captureStartWorkingSet} gameWorkingSet={GameWorkingSet} gamePrivateUsage={GamePrivateUsage}");

            // Phase 29g-2b: a capture churns large transient LOH buffers (the
            // per-chunk pipe-read JSON byte[]/char[]). 29g-2 cut the rest of
            // capture's allocation enough that natural gen2 GCs fire rarely,
            // so those collectable buffers linger — live-smoke saw ~4.5 GB of
            // dead Byte[]/Char[] still resident (14.8 GB private) that a GC
            // reclaimed (post-GC gcdump showed them gone). A plain Forced
            // compacting collect frees the objects but Workstation GC may keep
            // the committed segments mapped (process stays large); GCCollectionMode
            // .Aggressive additionally DECOMMITS / returns memory to the OS.
            // Off the UI thread (fire-and-forget) so the collect doesn't freeze
            // the window; logs before/after so we can confirm it ran + its effect.
            long gcBeforeManaged = GC.GetTotalMemory(forceFullCollection: false);
            long gcBeforeWs      = GetWorkingSet();
            _ = Task.Run(() =>
            {
                GC.Collect(GC.MaxGeneration, System.GCCollectionMode.Aggressive, blocking: true);
                long afterManaged = GC.GetTotalMemory(forceFullCollection: false);
                _logger.Write(SnapshotStore.LogCategory,
                    $"post-capture GC (Aggressive): managed {gcBeforeManaged:N0} -> {afterManaged:N0} B, " +
                    $"WS {gcBeforeWs:N0} -> {GetWorkingSet():N0} B");
            });
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

    /// <summary>Phase 29c-5: classify a Diff pair into Live (same
    /// session), CrossSession (same build, different session id), or
    /// CrossBuild (different game-build hash entirely). Drives the
    /// statics-only auto-filter + banner copy.</summary>
    public enum DiffSessionMode { Live, CrossSession, CrossBuild }

    /// <summary>Banner text published after each Diff call describing the
    /// session relationship between the two snapshots. Empty when Live.
    /// The UI binds a colored stripe above the diff DataGrid to this.</summary>
    [ObservableProperty] private string _crossSessionBanner = string.Empty;

    /// <summary>Diff-time toggle: when ON, instance-keyed rows participate
    /// in cross-session diff even though instance addresses are not stable
    /// across game launches (produces noisy add/remove pairs). Default OFF.</summary>
    [ObservableProperty] private bool _forceIncludeInstancesCrossSession;
    partial void OnForceIncludeInstancesCrossSessionChanged(bool value)
    {
        // Re-run diff so the user sees the change without an extra click.
        if (SelectedSnapshotA is not null && SelectedSnapshotB is not null)
            _ = DiffAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDiff))]
    public async Task DiffAsync()
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

        // Phase 29c-3: lazy-load class blobs on demand. Phase 29g-2: the
        // FieldIndex is no longer built at load — build it here (only for the
        // active A/B pair) via EnsureFieldIndex. EvictUnusedEnvelopes below
        // releases every other snapshot's index.
        Dictionary<string, SnapshotFieldValue> ai, bi;
        try
        {
            await EnsureLoadedAsync(a).ConfigureAwait(true);
            await EnsureLoadedAsync(b).ConfigureAwait(true);
            ai = a.EnsureFieldIndex();
            bi = b.EnsureFieldIndex();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load snapshot data: {ex.Message}";
            return;
        }

        // Phase 29c-5: pick session mode + apply statics-only auto-filter.
        var sessionMode = ClassifySessionMode(a, b);
        bool staticsOnly = sessionMode != DiffSessionMode.Live
                           && !ForceIncludeInstancesCrossSession;

        var sw = Stopwatch.StartNew();
        DiffRows.Clear();
        int changed = 0, added = 0, removed = 0, unchanged = 0, suppressed = 0;
        bool includeUnchanged = IncludeUnchangedRows;

        // Walk A → B keys to find changed + removed (+ optionally unchanged);
        // second pass walks B → A to catch the added.
        foreach (var (key, va) in ai)
        {
            if (staticsOnly && !va.IsStatic) { suppressed++; continue; }
            if (bi.TryGetValue(key, out var vb))
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
        foreach (var (key, vb) in bi)
        {
            if (staticsOnly && !vb.IsStatic) { suppressed++; continue; }
            if (!ai.ContainsKey(key))
            {
                DiffRows.Add(SnapshotDiffRow.Added(vb));
                added++;
            }
        }

        sw.Stop();
        RecomputeFieldStats();
        RebuildFilteredRows();
        UpdateCrossSessionBanner(sessionMode, staticsOnly, suppressed);

        // Phase 29c-eviction: Diff is the natural "done with the old
        // pair" moment. Unload every persisted envelope that isn't the
        // current A or B so the user can keep 10+ saved snapshots
        // without paying their FieldIndex cost in memory all the time.
        int evicted = EvictUnusedEnvelopes();

        // When "Include unchanged" is OFF the diff only carries changed/
        // added/removed rows, so a value that stayed the same (e.g. purse
        // you never spent between snaps) won't appear even with the
        // "Unchanged" filter checked — flag that so the user knows to re-run
        // with it on, or use SPC/Value Search for a static-looking value.
        string unchangedNote = includeUnchanged
            ? $", {unchanged:N0} unchanged"
            : ", unchanged NOT captured — tick \"Include unchanged\" + re-Diff to compare values that didn't change";
        string suppressedNote = suppressed > 0
            ? $" ({suppressed:N0} instance rows suppressed)"
            : string.Empty;
        string evictionNote = evicted > 0
            ? $" — evicted {evicted:N0} stale envelope(s) from RAM"
            : string.Empty;
        StatusMessage = $"Diff {a.ShortLabel} → {b.ShortLabel}: {changed:N0} changed, {added:N0} added, {removed:N0} removed{unchangedNote}{suppressedNote} (in {sw.ElapsedMilliseconds} ms){evictionNote}.";
    }

    /// <summary>Phase 29c-eviction + 29g-2: free memory from every envelope
    /// that isn't the active diff A/B pair. Persisted envelopes are fully
    /// unloaded (Classes + FieldIndex dropped; <c>EnsureLoadedAsync</c>
    /// re-hydrates on demand). Dirty/in-memory-only envelopes are NEVER
    /// unloaded — that would lose the user's unsaved snapshot — but their
    /// FieldIndex is a rebuildable cache, so it's released while
    /// <see cref="SnapshotEnvelope.Classes"/> stays put (Phase 29g-2: the
    /// FieldIndex was the #2 resident cost, ~2.5-3 GB across snapshots).
    /// Returns the number of fully-evicted (unloaded) envelopes for the diff
    /// status line.</summary>
    private int EvictUnusedEnvelopes()
    {
        int evicted = 0, released = 0;
        long freedEstimate = 0;
        foreach (var env in Snapshots)
        {
            if (!env.ClassesLoaded) continue;
            if (ReferenceEquals(env, SelectedSnapshotA)) continue;
            if (ReferenceEquals(env, SelectedSnapshotB)) continue;
            if (env.PersistedId is not null)
            {
                freedEstimate += env.EstimatedMemoryBytes;
                env.Unload();
                evicted++;
            }
            else if (env.FieldIndexBuilt)
            {
                // Dirty (unsaved) → can't drop Classes, but the FieldIndex is
                // rebuildable. Release it; the unsaved data is preserved.
                env.ReleaseFieldIndex();
                released++;
            }
        }
        if (evicted > 0 || released > 0)
        {
            _logger.Write(SnapshotStore.LogCategory,
                $"auto-eviction: unloaded {evicted} envelope(s) (~{freedEstimate:N0} B est), released {released} dirty FieldIndex(es)");
            OnPropertyChanged(nameof(TotalSnapshotsMemoryBanner));
        }
        return evicted;
    }

    /// <summary>Sync wrapper retained for any caller already binding to
    /// the parameterless Diff command. New code should await
    /// <see cref="DiffAsync"/> directly to surface lazy-load errors.</summary>
    public void Diff() => _ = DiffAsync();

    private static DiffSessionMode ClassifySessionMode(SnapshotEnvelope a, SnapshotEnvelope b)
    {
        if (!string.Equals(a.GameBuildId, b.GameBuildId, StringComparison.Ordinal))
            return DiffSessionMode.CrossBuild;
        if (!string.Equals(a.GameSessionId, b.GameSessionId, StringComparison.Ordinal))
            return DiffSessionMode.CrossSession;
        return DiffSessionMode.Live;
    }

    private void UpdateCrossSessionBanner(DiffSessionMode mode, bool staticsOnly, int suppressed)
    {
        CrossSessionBanner = mode switch
        {
            DiffSessionMode.Live => string.Empty,
            DiffSessionMode.CrossSession when staticsOnly =>
                $"ℹ Cross-session diff — {suppressed:N0} instance-field rows suppressed because addresses change every game launch. " +
                "Static fields diff cleanly. Toggle \"Force include instances\" to override (will produce add/remove noise).",
            DiffSessionMode.CrossSession =>
                "ℹ Cross-session diff — instance fields included (Force-include is ON). Expect add/remove noise from rebased addresses.",
            DiffSessionMode.CrossBuild when staticsOnly =>
                $"⚠ Cross-build diff — game build_id differs. Static field offsets may have shifted between versions; {suppressed:N0} instance rows suppressed.",
            DiffSessionMode.CrossBuild =>
                "⚠ Cross-build diff — instance fields included AND offsets may have shifted between game versions. Result is best-effort only.",
            _ => string.Empty,
        };
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

    // ── Phase 29c-4: persistence commands ──────────────────────────────

    /// <summary>Snapshots present in the in-memory list that have NOT
    /// been persisted to disk yet. Caller (UI / app-close handler)
    /// surfaces these to the user before destructive actions like
    /// closing the app or switching games.</summary>
    public IReadOnlyList<SnapshotEnvelope> GetUnsavedDirtySnapshots()
    {
        var list = new List<SnapshotEnvelope>();
        foreach (var s in Snapshots)
            if (s.IsDirty) list.Add(s);
        return list;
    }

    /// <summary>Persist a single envelope. Idempotent — if already saved
    /// (<see cref="SnapshotEnvelope.PersistedId"/> != null), no-op.
    /// Triggers prune + vacuum heuristic after success.</summary>
    [RelayCommand]
    public async Task SaveSnapshotAsync(SnapshotEnvelope env)
    {
        if (env is null) return;
        if (env.PersistedId is not null) return;
        if (_store is null)
        {
            ErrorMessage = "Snapshot DB is not open — connect to a game first.";
            return;
        }
        try
        {
            int fieldValueCount = env.ClassesLoaded ? env.FieldValueCount : 0;
            var req = new SnapshotPersistRequest
            {
                Label             = string.IsNullOrEmpty(env.Label) ? null : env.Label,
                CapturedAtUtc     = string.IsNullOrEmpty(env.CapturedAtUtc)
                                        ? env.CapturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                                        : env.CapturedAtUtc,
                GameBuildId       = env.GameBuildId,
                GameSessionId     = env.GameSessionId,
                TargetProcess     = _currentGameModule ?? string.Empty,
                Backend           = _store.GameBuildId == env.GameBuildId ? GuessBackend() : "unknown",
                UnityVersion      = null,
                PointerSize       = null,
                TotalClasses      = env.TotalClasses,
                TotalInstances    = env.TotalInstances,
                FieldValueCount   = fieldValueCount,
                InstanceCap       = InstanceCap,
                ArrayCap          = ArrayCap,
                IncludeUnchanged  = IncludeUnchangedRows,
                AssemblyFilter    = string.IsNullOrWhiteSpace(ScopeAssembliesText) ? null : ScopeAssembliesText.Trim(),
                Classes           = env.Classes,
            };
            // Large snapshots (100K+ instances) take seconds to gzip + insert.
            // Poll the save task and, once it crosses 2 s, show a live elapsed
            // status so the UI doesn't look hung. Stays on the UI thread
            // (ConfigureAwait(true)) so the status updates marshal correctly.
            var saveSw   = Stopwatch.StartNew();
            // gzip + SQLite insert are CPU/IO-bound and run synchronously
            // (Microsoft.Data.Sqlite's async is sync under the hood), so run
            // them on a background thread — otherwise they'd block the UI
            // thread and the elapsed ticker below could never render. The
            // store serialises its own access via an internal gate.
            var saveTask = Task.Run(() => _store.SaveSnapshotAsync(req));
            bool announced = false;
            while (!saveTask.IsCompleted)
            {
                var finished = await Task.WhenAny(saveTask, Task.Delay(500)).ConfigureAwait(true);
                if (finished == saveTask) break;
                if (saveSw.Elapsed.TotalSeconds >= 2)
                {
                    announced = true;
                    StatusMessage =
                        $"Saving snapshot {env.ShortLabel} to DB… {saveSw.Elapsed.TotalSeconds:0.0}s "
                        + $"({env.TotalClasses:N0} classes / {env.TotalInstances:N0} instances)";
                }
            }
            long id = await saveTask.ConfigureAwait(true);
            if (announced)
                StatusMessage = $"Saved snapshot {env.ShortLabel} to DB in {saveSw.Elapsed.TotalSeconds:0.0}s (id={id}).";
            env.PersistedId    = id;
            env.PersistedBytes = await _store.GetTotalPayloadBytesAsync().ConfigureAwait(true)
                                 - (await SumOtherPersistedBytesAsync(id).ConfigureAwait(true));
            env.IsDirty        = false;
            StatusMessage = $"Saved snapshot {env.ShortLabel} to DB (id={id}).";
            _logger.Write(SnapshotStore.LogCategory,
                $"SaveSnapshotAsync ok id={id} label=\"{env.Label}\"");

            await AutoPruneAndMaybeVacuumAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshDbStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Save failed: {ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory,
                $"SaveSnapshotAsync FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Sum of payload_bytes for every snapshot EXCEPT the given
    /// id — used to back out the freshly-saved envelope's own bytes
    /// when computing its PersistedBytes display value.</summary>
    private async Task<long> SumOtherPersistedBytesAsync(long excludeId)
    {
        if (_store is null) return 0;
        long total = 0;
        var list = await _store.ListSnapshotsAsync().ConfigureAwait(true);
        foreach (var m in list)
            if (m.Id != excludeId) total += m.PayloadBytes;
        return total;
    }

    /// <summary>Remove a snapshot from BOTH the in-memory list AND disk.
    /// Distinct from <see cref="DropSnapshotA"/>/<see cref="DropSnapshotB"/>
    /// which only free in-memory state.</summary>
    [RelayCommand]
    public async Task DeleteSnapshotFromDbAsync(SnapshotEnvelope env)
    {
        if (env is null) return;
        if (env.PersistedId is long id && _store is not null)
        {
            await _store.DeleteSnapshotAsync(id).ConfigureAwait(true);
            env.PersistedId    = null;
            env.PersistedBytes = 0;
            await RefreshDbStatusAsync().ConfigureAwait(true);
        }
        DropOne(env, "DB-delete");
    }

    /// <summary>Best-effort backend tag — Mono module names contain "mono",
    /// IL2CPP uses "GameAssembly". Falls back to <c>"unknown"</c>.</summary>
    private string GuessBackend()
    {
        var m = (_currentGameModule ?? string.Empty).ToLowerInvariant();
        if (m.Contains("mono")) return "mono";
        if (m.Contains("gameassembly") || m.Contains("il2cpp")) return "il2cpp";
        return "unknown";
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

    /// <summary>Filter-pass kind counts from the last
    /// <see cref="RebuildFilteredRows"/>. Used to surface a
    /// "(X changed, Y added, Z removed, W unchanged)" breakdown in
    /// the status line so the user can tell at a glance which
    /// bucket their value-range / class / field filter narrowed to.
    /// Particularly important for the re-allocation pattern: when
    /// the game GC moves an instance between snapshots the field
    /// value diff shows up as add+remove pair, NOT as changed —
    /// users hunting "where did my HP/money go?" often miss the
    /// pair entirely if the breakdown isn't visible.</summary>
    private int _filteredIncreasedCount;
    private int _filteredDecreasedCount;
    private int _filteredOtherChangedCount;
    private int _filteredAddedCount;
    private int _filteredRemovedCount;
    private int _filteredUnchangedCount;

    /// <summary>Apply every filter against <see cref="DiffRows"/> and
    /// rebuild <see cref="FilteredDiffRows"/> in place. Called by every
    /// filter property's <c>OnXxxChanged</c> hook + by <see cref="Diff"/>
    /// when the underlying row set itself changes. Single O(N) pass.</summary>
    private void RebuildFilteredRows()
    {
        // Class filter is separator-insensitive: fold '/' → '.' on both sides
        // so the user can type CE's "MainGame.PLY_WORK" or our "MainGame/PLY_WORK".
        var clsFilter   = SnapshotEnvelope.NormalizeClassSeparators(ClassFilterText ?? string.Empty);
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

        // Build into a local List first, then ReplaceAll in one Reset
        // notification so the DataGrid doesn't re-layout per row. Avoids
        // the 740K-Add-events hang on "show all added" toggle.
        var staged = new List<SnapshotDiffRow>(DiffRows.Count);
        _filteredIncreasedCount    = 0;
        _filteredDecreasedCount    = 0;
        _filteredOtherChangedCount = 0;
        _filteredAddedCount        = 0;
        _filteredRemovedCount      = 0;
        _filteredUnchangedCount    = 0;
        // Classification cache so the inner-loop dispatch sees the same
        // sub-kind that's later used for counter increment + value-range
        // gating below. Avoids re-reading NumericOld/NumericNew twice.
        DiffRowKindSubclass sub = DiffRowKindSubclass.None;
        foreach (var r in DiffRows)
        {
            if (HideSyntheticTypes && r.IsSynthetic) continue;

            // ChangeKind dispatch (Phase 29-polish 2026-05-29): the
            // single "changed" lane is partitioned into three sub-kinds
            // — Increased / Decreased / OtherChanged — so the user can
            // express direction filters as set unions with Unchanged.
            sub = DiffRowKindSubclass.None;
            switch (r.ChangeKind)
            {
                case "changed":
                    sub = ClassifyChangedDirection(r);
                    bool keep = sub switch
                    {
                        DiffRowKindSubclass.Increased    => ShowIncreased,
                        DiffRowKindSubclass.Decreased    => ShowDecreased,
                        DiffRowKindSubclass.OtherChanged => ShowOtherChanged,
                        _ => false,
                    };
                    if (!keep) continue;
                    break;
                case "added":     if (!ShowAdded)     continue; break;
                case "removed":   if (!ShowRemoved)   continue; break;
                case "unchanged": if (!ShowUnchanged) continue; break;
            }

            if (hasClsFilter &&
                SnapshotEnvelope.NormalizeClassSeparators(r.ClassDisplay)
                    .IndexOf(clsFilter, StringComparison.OrdinalIgnoreCase) < 0)
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

            staged.Add(r);
            switch (r.ChangeKind)
            {
                case "changed":
                    switch (sub)
                    {
                        case DiffRowKindSubclass.Increased:    _filteredIncreasedCount++;    break;
                        case DiffRowKindSubclass.Decreased:    _filteredDecreasedCount++;    break;
                        case DiffRowKindSubclass.OtherChanged: _filteredOtherChangedCount++; break;
                    }
                    break;
                case "added":     _filteredAddedCount++;     break;
                case "removed":   _filteredRemovedCount++;   break;
                case "unchanged": _filteredUnchangedCount++; break;
            }
        }
        FilteredDiffRows.ReplaceAll(staged);

        // Live-publish the breakdown so the UI shows current numbers
        // regardless of whether this rebuild was triggered by Diff or
        // by a filter-property toggle. Empty source = empty line so
        // the TextBlock collapses cleanly.
        FilterResultLine = DiffRows.Count == 0
            ? string.Empty
            : $"Filter: {FilteredDiffRows.Count:N0} of {DiffRows.Count:N0} rows ({FormatFilteredKindBreakdown()})";
    }

    /// <summary>Classify a "changed" row into a numeric-direction
    /// subclass. Numeric rows with both sides parseable are split into
    /// Increased / Decreased; everything else (strings, pointers, error
    /// rows, numeric rows with one unparseable side, and the edge case
    /// where old == new but ChangeKind still says "changed") falls into
    /// OtherChanged so it's still reachable via a single checkbox.</summary>
    private static DiffRowKindSubclass ClassifyChangedDirection(SnapshotDiffRow r)
    {
        if (r.Kind == DiffRowKind.Numeric &&
            r.NumericOld is double oldN &&
            r.NumericNew is double newN)
        {
            if (newN > oldN) return DiffRowKindSubclass.Increased;
            if (newN < oldN) return DiffRowKindSubclass.Decreased;
        }
        return DiffRowKindSubclass.OtherChanged;
    }

    /// <summary>Format the per-kind counts. Three "changed" sub-counts
    /// (↑ increased / ↓ decreased / Δ other) replace the prior single
    /// "changed" term so the status line reflects the new UI partition.
    /// Zero-valued sub-counts are dropped to keep the line readable on
    /// diffs where only one direction has data.</summary>
    private string FormatFilteredKindBreakdown()
    {
        if (FilteredDiffRows.Count == 0) return "0 rows";

        var parts = new List<string>(6);
        if (_filteredIncreasedCount    > 0) parts.Add($"{_filteredIncreasedCount:N0} ↑");
        if (_filteredDecreasedCount    > 0) parts.Add($"{_filteredDecreasedCount:N0} ↓");
        if (_filteredOtherChangedCount > 0) parts.Add($"{_filteredOtherChangedCount:N0} Δ");
        if (_filteredAddedCount        > 0) parts.Add($"{_filteredAddedCount:N0} added");
        if (_filteredRemovedCount      > 0) parts.Add($"{_filteredRemovedCount:N0} removed");
        if (_filteredUnchangedCount    > 0) parts.Add($"{_filteredUnchangedCount:N0} unchanged");
        return parts.Count == 0 ? "0 rows" : string.Join(", ", parts);
    }

    // ----- Instance lookup diagnostic (Phase 29-polish, 2026-05-28) -----
    //
    // User-driven diagnostic: given a known instance address (e.g. the
    // CE-pinned "銀兩 GameStat at 0x2986ACC0" case that wasn't surfacing in
    // diff results despite the address being stable across snapshots),
    // dump every FieldIndex entry from both selected snapshots that
    // touches that address. Answers "was the instance captured at all?
    // what value was recorded for each field? does the diff key match
    // between A and B?" — without re-snapshotting the game.
    //
    // Pure UI-side: walks the already-built FieldIndex dictionaries.
    // No DLL round-trip, no game pressure.

    /// <summary>Address the user typed into the diagnostic input. Accepted
    /// in any of "0x000000002986ACC0", "0x2986ACC0", "2986ACC0", lower or
    /// upper case. Normalised to the canonical 16-digit upper form before
    /// matching.</summary>
    [ObservableProperty] private string _debugLookupAddress = string.Empty;

    /// <summary>Class name pattern for the class-listing diagnostic. Substring
    /// match against <c>"{ns}.{name}"</c>, case-insensitive. E.g.
    /// "Mortal.Core.GameStat" or just "GameStat" both match the 銀兩 class.
    /// Used to answer "did the snapshot actually capture every instance
    /// of this class, or only a subset?" — typing a class name then
    /// "List instances" dumps every captured address in A and B sorted
    /// numerically, so a missing address (e.g. 0x2962ACC0 that the live
    /// Inspector shows but the snapshot didn't see) is obvious.</summary>
    [ObservableProperty] private string _debugLookupClass = string.Empty;

    /// <summary>Result of the last <see cref="DebugLookupInstance"/>.
    /// Multi-line text bound to a read-only TextBox so the user can
    /// copy / inspect freely. Empty until first lookup runs.</summary>
    [ObservableProperty] private string _debugLookupOutput = string.Empty;

    /// <summary>Walk FieldIndex of A and B, dump everything matching the
    /// normalised address. Emits one block per snapshot followed by a
    /// per-field diff section so the user can see at a glance whether
    /// the address was missed by one snapshot, captured with different
    /// values, or captured with identical values (= unchanged, default-
    /// filtered out).</summary>
    [RelayCommand]
    public void DebugLookupInstance()
    {
        var sb = new System.Text.StringBuilder();
        string normalised = NormaliseAddressForLookup(DebugLookupAddress);
        if (string.IsNullOrEmpty(normalised))
        {
            DebugLookupOutput = "Enter an address — accepted: 0x000000002986ACC0, 0x2986ACC0, or 2986ACC0 (case-insensitive).";
            return;
        }

        sb.Append("Lookup '").Append(normalised).AppendLine("' across selected snapshots.");
        sb.Append("A = ").Append(SelectedSnapshotA?.ShortLabel ?? "(not set)").AppendLine();
        sb.Append("B = ").Append(SelectedSnapshotB?.ShortLabel ?? "(not set)").AppendLine();
        sb.AppendLine();

        var aRows = CollectMatchingRows(SelectedSnapshotA, normalised);
        var bRows = CollectMatchingRows(SelectedSnapshotB, normalised);

        DumpSnapshotSection(sb, "Snapshot A", aRows);
        sb.AppendLine();
        DumpSnapshotSection(sb, "Snapshot B", bRows);
        sb.AppendLine();

        // Pair by full key — same {asm|class|addr|offset|name} → same logical field.
        var keyUnion = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in aRows) keyUnion.Add(r.key);
        foreach (var r in bRows) keyUnion.Add(r.key);

        var aMap = aRows.ToDictionary(r => r.key, r => r.value, StringComparer.Ordinal);
        var bMap = bRows.ToDictionary(r => r.key, r => r.value, StringComparer.Ordinal);

        sb.Append("=== Per-field diff at this address (").Append(keyUnion.Count).AppendLine(" keys) ===");
        int changedN = 0, addedN = 0, removedN = 0, unchangedN = 0;
        foreach (var key in keyUnion.OrderBy(k => k, StringComparer.Ordinal))
        {
            bool inA = aMap.TryGetValue(key, out var va);
            bool inB = bMap.TryGetValue(key, out var vb);
            string label = ExtractFieldLabel(key);
            if (inA && inB)
            {
                if (string.Equals(va!.ValueDisplay, vb!.ValueDisplay, StringComparison.Ordinal))
                {
                    sb.Append("  [=] ").Append(label).Append(" = ").AppendLine(va.ValueDisplay);
                    unchangedN++;
                }
                else
                {
                    sb.Append("  [~] ").Append(label).Append(" : ")
                      .Append(va.ValueDisplay).Append(" → ").AppendLine(vb!.ValueDisplay);
                    changedN++;
                }
            }
            else if (inB)
            {
                sb.Append("  [+] ").Append(label).Append(" = ").AppendLine(bMap[key].ValueDisplay);
                addedN++;
            }
            else
            {
                sb.Append("  [-] ").Append(label).Append(" = ").AppendLine(aMap[key].ValueDisplay);
                removedN++;
            }
        }
        sb.AppendLine();
        sb.Append("Summary: ")
          .Append(changedN).Append(" changed, ")
          .Append(addedN).Append(" added (B-only), ")
          .Append(removedN).Append(" removed (A-only), ")
          .Append(unchangedN).AppendLine(" unchanged.");

        if (aRows.Count == 0 && bRows.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ No rows matched in either snapshot. Possible causes:");
            sb.AppendLine("  - Address typed isn't in the captured set (instance cap, scope filter)");
            sb.AppendLine("  - Address typed differs from the snapshot's stored format");
            sb.AppendLine("  - The instance's class wasn't in the capture scope");
        }
        DebugLookupOutput = sb.ToString();
    }

    /// <summary>Class-listing diagnostic — walks each snapshot's Classes
    /// list for entries whose FQN matches the user's substring, then
    /// dumps every captured instance address + a small set of "spotlight"
    /// field values (string, int32, enum where present) per row.
    /// Reveals (a) whether the class was captured at all (b) how many
    /// instances each snapshot saw (c) the address-sorted set so a
    /// missing address compared to the live game stands out
    /// instantly.</summary>
    [RelayCommand]
    public void DebugLookupClassInstances()
    {
        var sb = new System.Text.StringBuilder();
        string pattern = (DebugLookupClass ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(pattern))
        {
            DebugLookupOutput = "Enter a class-name substring (e.g. \"Mortal.Core.GameStat\" or \"GameStat\").";
            return;
        }
        sb.Append("Class lookup '").Append(pattern).AppendLine("' across selected snapshots.");
        sb.Append("A = ").Append(SelectedSnapshotA?.ShortLabel ?? "(not set)").AppendLine();
        sb.Append("B = ").Append(SelectedSnapshotB?.ShortLabel ?? "(not set)").AppendLine();
        sb.AppendLine();

        DumpClassSection(sb, "Snapshot A", SelectedSnapshotA, pattern);
        sb.AppendLine();
        DumpClassSection(sb, "Snapshot B", SelectedSnapshotB, pattern);

        DebugLookupOutput = sb.ToString();
    }

    private static void DumpClassSection(System.Text.StringBuilder sb, string label,
        SnapshotEnvelope? env, string pattern)
    {
        sb.Append("=== ").Append(label).AppendLine(" ===");
        if (env is null) { sb.AppendLine("  (not selected)"); return; }

        var matches = env.Classes
            .Where(c => MatchClassName(c, pattern))
            .ToList();
        if (matches.Count == 0)
        {
            sb.AppendLine("  (no class matched the pattern)");
            return;
        }

        foreach (var c in matches)
        {
            string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            int instCount = c.Instances?.Count ?? 0;
            int staticCount = c.Statics?.Count ?? 0;
            sb.Append("  ").Append(c.Assembly).Append(" | ").Append(fqn)
              .Append("  — ").Append(instCount).Append(" instance(s), ")
              .Append(staticCount).AppendLine(" static(s)");
            if (!string.IsNullOrEmpty(c.Error))
                sb.Append("    error: ").AppendLine(c.Error);
            if (c.Instances is { Count: > 0 } insts)
            {
                // Sort numerically so missing addresses are obvious next
                // to a live Inspector listing (which is also sorted).
                var sorted = insts
                    .Select(i => new { Inst = i, Numeric = ParseAddrToUlong(i.Address) })
                    .OrderBy(x => x.Numeric)
                    .ToList();
                int idx = 0;
                foreach (var row in sorted)
                {
                    sb.Append("    #").Append(idx).Append("  ").Append(row.Inst.Address);
                    // Spotlight fields: anything that looks like a name or value.
                    string spotlight = ExtractSpotlightFields(row.Inst);
                    if (!string.IsNullOrEmpty(spotlight))
                        sb.Append("  ").Append(spotlight);
                    sb.AppendLine();
                    idx++;
                }
            }
        }
    }

    private static bool MatchClassName(SnapshotClassEntry c, string pattern)
    {
        string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
        return fqn.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static ulong ParseAddrToUlong(string addr)
    {
        if (string.IsNullOrEmpty(addr)) return 0;
        string s = addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addr.Substring(2) : addr;
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : 0;
    }

    /// <summary>Pull a few human-readable values from an instance's
    /// captured fields — the first string-typed value and the first
    /// integer-typed value found, formatted as "_name=銀兩 _value=9910"-
    /// style. Helps the user spot the money row in a list of 152
    /// GameStat addresses.</summary>
    private static string ExtractSpotlightFields(SnapshotInstanceEntry inst)
    {
        if (inst.Fields is null || inst.Fields.Count == 0) return string.Empty;
        var bits = new List<string>();
        // Prefer a string field (typically _name) and an int32 field
        // (typically _value) — capped at 3 hits to keep the line short.
        int hits = 0;
        foreach (var f in inst.Fields)
        {
            if (hits >= 3) break;
            if (string.IsNullOrEmpty(f.Name)) continue;
            if (!string.IsNullOrEmpty(f.StringValue))
            {
                bits.Add($"{f.Name}=\"{f.StringValue}\"");
                hits++;
                continue;
            }
            if (f.Value.HasValue &&
                (f.Value.Value.ValueKind == JsonValueKind.Number ||
                 f.Value.Value.ValueKind == JsonValueKind.String))
            {
                bits.Add($"{f.Name}={f.Value.Value.GetRawText().Trim('"')}");
                hits++;
            }
        }
        return bits.Count == 0 ? string.Empty : string.Join(" ", bits);
    }

    private static string NormaliseAddressForLookup(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        // strip any thousands-separator chrome the user might have typed
        s = s.Replace("_", string.Empty).Replace(" ", string.Empty);
        if (s.Length == 0) return string.Empty;
        if (!ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v))
            return string.Empty;
        // Canonical form matches the DLL's formatHex output: "0x%016llX".
        return "0x" + v.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static List<(string key, SnapshotFieldValue value)> CollectMatchingRows(
        SnapshotEnvelope? env, string normalisedAddress)
    {
        var hits = new List<(string, SnapshotFieldValue)>();
        if (env is null || !env.ClassesLoaded) return hits;
        // Phase 29g-2: build the index on demand (the debug lookup runs after
        // the user has selected/diffed A/B, so Classes are loaded).
        foreach (var kvp in env.EnsureFieldIndex())
        {
            // Key shape: "{asm}|{fqn}|{instAddrOrStatic}|{offset}|{fieldName}".
            // Just substring-match the address piece — cheap, unambiguous
            // because the 16-digit hex won't accidentally appear in
            // assembly/class/field strings.
            if (kvp.Key.IndexOf(normalisedAddress, StringComparison.Ordinal) >= 0)
                hits.Add((kvp.Key, kvp.Value));
        }
        return hits;
    }

    private static void DumpSnapshotSection(System.Text.StringBuilder sb, string label,
        List<(string key, SnapshotFieldValue value)> rows)
    {
        sb.Append("=== ").Append(label).Append(" — ").Append(rows.Count).AppendLine(" rows ===");
        if (rows.Count == 0)
        {
            sb.AppendLine("  (no field-index entries match this address)");
            return;
        }
        foreach (var (key, value) in rows.OrderBy(r => r.value.FieldOffset))
        {
            sb.Append("  +0x").Append(value.FieldOffset.ToString("X", CultureInfo.InvariantCulture))
              .Append("  ").Append(value.FieldName)
              .Append(" (").Append(value.FieldType).Append(")")
              .Append(" = ").AppendLine(value.ValueDisplay);
        }
    }

    private static string ExtractFieldLabel(string key)
    {
        // "{asm}|{fqn}|{instAddrOrStatic}|{offset}|{fieldName}" → "+0xN fieldName"
        int last = key.LastIndexOf('|');
        if (last < 0) return key;
        int prev = key.LastIndexOf('|', last - 1);
        if (prev < 0) return key.Substring(last + 1);
        string offsetStr = key.Substring(prev + 1, last - prev - 1);
        string name      = key.Substring(last + 1);
        // FieldOffset is stored as a ulong — printed in decimal here. Re-render as hex.
        if (ulong.TryParse(offsetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong off))
            return $"+0x{off:X} {name}";
        return $"+{offsetStr} {name}";
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

        // Bumped 30 → 40 (2026-05-28 user ask) — the stats panel is
        // tall enough on a typical 1080p+ display to show ~40 rows
        // before needing the scrollbar, and the long tail of noisy
        // field names tends to keep adding signal past 30.
        const int kTopN = 40;
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

    // ── Phase 29c persistence flags ────────────────────────────────────

    /// <summary>SQLite row id this envelope was last persisted as, or
    /// <c>null</c> if it lives only in memory. Surfaced in the per-row
    /// "💾 Save" button (label flips to "✓ Saved" once set).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(PersistenceStatus))]
    private long? _persistedId;

    /// <summary>Bytes occupied on disk for this snapshot (sum of its
    /// class-blob payload_bytes). 0 for in-memory-only envelopes.</summary>
    public long PersistedBytes { get; set; }

    /// <summary>Mirrors <see cref="PersistedId"/> for binding clarity:
    /// <c>true</c> = in-memory only, the user could lose it on app close.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty != value)
            {
                _isDirty = value;
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(PersistenceStatus));
            }
        }
    }
    private bool _isDirty;

    /// <summary>Lazy-load flag: <c>false</c> when this envelope was
    /// constructed from <c>ListSnapshotsAsync</c> metadata only and
    /// its <see cref="Classes"/> / <see cref="FieldIndex"/> haven't
    /// been hydrated from disk yet. Diff path calls
    /// <c>SnapshotViewModel.EnsureLoadedAsync</c> before reading these.</summary>
    public bool ClassesLoaded { get; set; } = true;

    /// <summary>UI-friendly label for the save/load state: "💾 not saved",
    /// "✓ saved (12 MB)", "📁 loaded (12 MB)".</summary>
    public string PersistenceStatus
    {
        get
        {
            if (PersistedId is null) return "💾 not saved";
            string size = PersistedBytes > 0 ? $" ({FormatMb(PersistedBytes)})" : string.Empty;
            return ClassesLoaded ? $"✓ saved{size}" : $"📁 saved{size} (load on diff)";
        }
    }

    /// <summary>Phase 29b — flipped by MainWindowViewModel when the
    /// current handshake's gameBuildId doesn't match this snapshot's
    /// gameBuildId (user reconnected to a different game). Visible in
    /// the snapshot dropdown via <see cref="DisplayName"/>.</summary>
    [ObservableProperty]
    private bool _isStale;

    /// <summary>Phase 29g-2 — backing store for the lazily-built FieldIndex.
    /// <c>null</c> until the first content consumer (Diff / SPC /
    /// address-lookup / field-stats) calls <see cref="EnsureFieldIndex"/>;
    /// released back to <c>null</c> by <see cref="ReleaseFieldIndex"/> /
    /// <see cref="Unload"/> for any snapshot that isn't the active diff A/B
    /// pair. **Not built at capture or lazy-load** — Phase 29g-0b
    /// (dotnet-dump) measured 12.2M <see cref="SnapshotFieldValue"/> = 1.17 GB
    /// + 432 MB of dictionary bucket arrays + the per-field composite-key and
    /// ValueDisplay strings, all three snapshots resident, as the #2
    /// structural cost. <see cref="Classes"/> stays loaded so the index
    /// rebuilds without a disk reload.</summary>
    private Dictionary<string, SnapshotFieldValue>? _fieldIndex;

    /// <summary>Cached field-value count, set whenever the index is built
    /// (exact, = map.Count) or at capture / lazy-load via
    /// <see cref="CountFieldValues"/> (approximate). Survives
    /// <see cref="ReleaseFieldIndex"/> so <see cref="EstimatedMemoryBytes"/>
    /// + the status lines stay meaningful after the index is dropped.</summary>
    private int _fieldValueCount;

    private static readonly Dictionary<string, SnapshotFieldValue> EmptyFieldIndex
        = new(StringComparer.Ordinal);

    /// <summary>Flat lookup: <c>"asm|ns|class|instance|offset|field"</c> →
    /// per-field value snapshot. **Read-only view — empty when not built.**
    /// Content consumers (Diff / SPC / address-lookup / field-stats) MUST
    /// call <see cref="EnsureFieldIndex"/> first; the empty fallback avoids a
    /// null-deref if a site is missed (yields no rows rather than crashing).</summary>
    public IReadOnlyDictionary<string, SnapshotFieldValue> FieldIndex
        => _fieldIndex ?? EmptyFieldIndex;

    /// <summary><c>true</c> when the FieldIndex is currently built + resident.</summary>
    public bool FieldIndexBuilt => _fieldIndex is not null;

    /// <summary>Field-value count — cheap, survives <see cref="ReleaseFieldIndex"/>.
    /// Drives <see cref="EstimatedMemoryBytes"/> + the capture status line.</summary>
    public int FieldValueCount => _fieldValueCount;

    /// <summary>Build the FieldIndex on demand (no-op if already built) and
    /// return it. Requires <see cref="Classes"/> to be loaded — call
    /// <c>SnapshotViewModel.EnsureLoadedAsync</c> first for lazy envelopes.</summary>
    public Dictionary<string, SnapshotFieldValue> EnsureFieldIndex()
    {
        if (_fieldIndex is null) BuildFieldIndex();
        return _fieldIndex!;
    }

    /// <summary>Phase 29g-2 — drop the built FieldIndex (its composite-key +
    /// ValueDisplay strings, <see cref="SnapshotFieldValue"/> objects, and
    /// dictionary bucket array) but KEEP <see cref="Classes"/> so it can be
    /// rebuilt on demand without a disk reload. The cached
    /// <see cref="FieldValueCount"/> survives. No-op if not built. Safe on
    /// dirty/in-memory-only envelopes — this loses a rebuildable cache, not
    /// data.</summary>
    public void ReleaseFieldIndex() => _fieldIndex = null;

    /// <summary>Test seam — inject a prebuilt FieldIndex without walking
    /// <see cref="Classes"/> (fixtures build a custom dict directly).
    /// Production builds via <see cref="EnsureFieldIndex"/>.</summary>
    public void SetFieldIndexForTest(Dictionary<string, SnapshotFieldValue> map)
    {
        _fieldIndex     = map;
        _fieldValueCount = map.Count;
    }

    /// <summary>Phase 29c-eviction: clear the in-memory class + field
    /// index, returning this envelope to the metadata-only state it had
    /// after <c>BuildEnvelopeFromMeta</c>. Caller MUST have a persisted
    /// id so the data can be reloaded later via
    /// <c>SnapshotViewModel.EnsureLoadedAsync</c>. No-op if already
    /// unloaded or never loaded.</summary>
    public void Unload()
    {
        if (!ClassesLoaded) return;
        if (PersistedId is null) return;  // can't reload — refuse
        Classes.Clear();
        _fieldIndex   = null;
        ClassesLoaded = false;
        OnPropertyChanged(nameof(PersistenceStatus));
    }

    /// <summary>Short label for status messages / dropdown display.
    /// Falls back to "#N (capture time)" if no user label was set.</summary>
    public string ShortLabel
        => string.IsNullOrEmpty(Label)
            ? CapturedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : $"\"{Label}\"";

    /// <summary>Heuristic UI-side memory cost of this envelope, in bytes.
    /// Empirical ratio from build #646 live-smoke on 活俠傳 / Mortal:
    /// 1.13M field-values consumed ~1.62 GB of UI WorkingSet delta →
    /// ~1500 bytes per field-value once you account for the dictionary's
    /// bucket overhead, the SnapshotFieldValue object header, the per-field
    /// strings (assembly, class fqn, instance address, field name, type,
    /// value display), and the parallel Classes list. The estimate is rough
    /// — within 2× — but plenty good for "is this snapshot small or huge?"
    /// UX. Computed from the cached <see cref="FieldValueCount"/> (Phase
    /// 29g-2) so it survives <see cref="ReleaseFieldIndex"/> and doesn't
    /// force-build the index. Note: post-29g-2 the FieldIndex is usually NOT
    /// resident (built only for the active diff/SPC pair), so this is now an
    /// upper bound on the snapshot's actual footprint — conservative, which
    /// is the safe direction for the RAM advisories that read it.</summary>
    public long EstimatedMemoryBytes
        => (long)_fieldValueCount * 1500L;

    public string DisplayName
    {
        get
        {
            string body = string.IsNullOrEmpty(Label)
                ? $"{CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst / ~{FormatMb(EstimatedMemoryBytes)}"
                : $"{Label} — {CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst / ~{FormatMb(EstimatedMemoryBytes)}";
            // Phase 29c-4 polish: prefix a "● unsaved" badge when the
            // envelope is in-memory only, so the snapshot dropdown shows
            // at a glance which entries would be lost on app close.
            // Stale flag (different game build) takes precedence — both
            // prefixes can coexist but stale is the bigger warning.
            if (IsDirty) body = $"● unsaved · {body}";
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
    /// anonymous-type fields — and we want each row diffable).
    /// <para/>
    /// Phase 29e-1b: array fields fan out — for any field whose
    /// <see cref="SnapshotFieldEntry.ArrayElements"/> has captured
    /// elements, we add N pseudo-rows keyed by <c>"…|{fieldName}[{i}]"</c>
    /// in addition to the existing summary row. SPC's per-snap value
    /// trajectory + the upcoming Pivot tab both see element-level
    /// changes that previously collapsed to a single
    /// <c>"[N elements, captured M]"</c> opaque cell. Layer A — index
    /// is positional; arrays that get reordered between snaps look
    /// like all elements changed even when the contents stayed the
    /// same. Reference-array inner-pivot (key by element field) is
    /// Layer B and needs DLL changes — deferred.</summary>
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
                    ExpandArrayElements(map, c.Assembly, fqn, c.Name, c.Namespace,
                                        instanceAddress: string.Empty, isStatic: true, f, synthetic);
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
                        ExpandArrayElements(map, c.Assembly, fqn, c.Name, c.Namespace,
                                            instanceAddress: inst.Address, isStatic: false, f, synthetic);
                    }
                }
            }
        }
        _fieldIndex      = map;
        _fieldValueCount = map.Count;
    }

    /// <summary>Phase 29g-2 — refresh the cached <see cref="FieldValueCount"/>
    /// from the current <see cref="Classes"/>, used at capture + lazy-load
    /// (before any FieldIndex is built) so the status line +
    /// <see cref="EstimatedMemoryBytes"/> work without paying the index cost.</summary>
    public void RecomputeFieldValueCount() => _fieldValueCount = CountFieldValues(Classes);

    /// <summary>Cheap field-value count (statics + instance fields + expanded
    /// array elements) WITHOUT building the FieldIndex dictionary or
    /// allocating any keys / SnapshotFieldValue objects. Mirrors
    /// <see cref="BuildFieldIndex"/>'s row emission so the count tracks the
    /// index size (modulo the rare offset+name key collision, which only
    /// shrinks the real dict — fine for a memory estimate).</summary>
    public static int CountFieldValues(IReadOnlyList<SnapshotClassEntry> classes)
    {
        int n = 0;
        foreach (var c in classes)
        {
            if (c.Statics is { Count: > 0 } statics)
                foreach (var f in statics) n += 1 + ArrayElemCount(f);
            if (c.Instances is { Count: > 0 } insts)
                foreach (var inst in insts)
                    foreach (var f in inst.Fields) n += 1 + ArrayElemCount(f);
        }
        return n;

        static int ArrayElemCount(SnapshotFieldEntry f)
            => f.ArrayElements is { Count: > 0 } arr ? arr.Elements.Count : 0;
    }

    /// <summary>Phase 29e-1b — fan out captured array elements as
    /// pseudo-rows keyed by <c>"{fieldName}[{index}]"</c>. Uses the
    /// element's own Index field rather than list position so that
    /// any future sparse-capture mode (skip-null elements etc.) maps
    /// 1:1. Element type comes from the array header
    /// (<see cref="SnapshotArrayElements.ElementType"/>); FieldOffset
    /// is inherited from the parent (the array slot offset). The
    /// element's actual managed address goes unused at this layer —
    /// Inspector jumps still target the array's owning instance, then
    /// the element-by-index walk happens at display time.</summary>
    private static void ExpandArrayElements(
        Dictionary<string, SnapshotFieldValue> map,
        string assembly, string fqn, string className, string ns,
        string instanceAddress, bool isStatic,
        SnapshotFieldEntry f, bool synthetic)
    {
        if (f.ArrayElements is not { Count: > 0 } arr) return;
        string instanceKey = isStatic ? "static" : instanceAddress;
        string elemType = string.IsNullOrEmpty(arr.ElementType) ? "?" : arr.ElementType!;
        foreach (var el in arr.Elements)
        {
            string elFieldName = $"{f.Name}[{el.Index}]";
            string key = $"{assembly}|{fqn}|{instanceKey}|{f.Offset}|{elFieldName}";
            map[key] = new SnapshotFieldValue
            {
                Assembly       = assembly,
                ClassFqn       = fqn,
                ClassName      = className,
                Namespace      = ns,
                InstanceAddress= isStatic ? string.Empty : instanceAddress,
                IsStatic       = isStatic,
                FieldName      = elFieldName,
                FieldType      = elemType,
                FieldOffset    = f.Offset,
                ValueDisplay   = FormatArrayElement(el),
                IsSynthetic    = synthetic,
            };
        }
    }

    /// <summary>Render one captured array element. Mirrors
    /// <see cref="FormatFieldValue"/>'s ladder for the element-level
    /// surface area: Value JsonElement → primitives, String → quoted,
    /// Ptr → <c>ptr=…</c>, Error → <c>(err: …)</c>. Reference-array
    /// elements arrive with Ptr filled but no inner fields (DLL doesn't
    /// chase children one extra level) — the ptr= row still tells
    /// users "this slot's reference changed".</summary>
    private static string FormatArrayElement(SnapshotArrayElement el)
    {
        if (!string.IsNullOrEmpty(el.Error)) return $"(err: {el.Error})";
        if (el.Value.HasValue && el.Value.Value.ValueKind != JsonValueKind.Null
                              && el.Value.Value.ValueKind != JsonValueKind.Undefined)
        {
            return el.Value.Value.ValueKind switch
            {
                JsonValueKind.String => el.Value.Value.GetString() ?? "(null)",
                JsonValueKind.Number => el.Value.Value.GetRawText(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                _ => el.Value.Value.GetRawText(),
            };
        }
        if (!string.IsNullOrEmpty(el.String)) return $"\"{el.String}\"";
        if (!string.IsNullOrEmpty(el.Ptr))    return $"ptr={el.Ptr}";
        return "(no value)";
    }

    /// <summary>Detection rule for compiler-generated / synthetic types
    /// per Strategy C. Conservative match — only names starting with
    /// '&lt;' are flagged. Includes <c>&lt;Module&gt;</c>,
    /// <c>&lt;&gt;f__AnonymousType*</c>, <c>&lt;&gt;c</c>,
    /// <c>&lt;Start&gt;d__N</c>, and the various async/iterator state
    /// machines the C# compiler emits. Avoids flagging legitimate
    /// generic types like <c>Dictionary`2</c> (those have backticks
    /// but no leading '&lt;').</summary>
    /// <summary>Normalize a class FQN's nested-type separator so the two
    /// conventions compare equal: this tool surfaces nested types as
    /// <c>Outer/Inner</c> (slash, for unambiguous runtime round-trip via
    /// FindClass), while CE / .NET-style tooling shows <c>Outer.Inner</c>
    /// (dot). Folding '/' → '.' lets a user paste either form into a class
    /// filter / FQN box and have it match.</summary>
    internal static string NormalizeClassSeparators(string fqn)
        => string.IsNullOrEmpty(fqn) ? fqn : fqn.Replace('/', '.');

    /// <summary>Separator-insensitive FQN equality (see
    /// <see cref="NormalizeClassSeparators"/>).</summary>
    internal static bool ClassFqnEquals(string a, string b)
        => string.Equals(NormalizeClassSeparators(a), NormalizeClassSeparators(b), StringComparison.Ordinal);

    internal static bool IsSyntheticClassName(string name)
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
    internal static string FormatFieldValue(SnapshotFieldEntry f)
    {
        if (!string.IsNullOrEmpty(f.Error))
        {
            // Phase 29a-v4 round 4: Mono static fields capture metadata
            // only — value reads removed to eliminate mono_class_vtable
            // calls that wedge the game's main thread post-RPC. Give
            // the row a friendly label instead of the raw error code.
            if (f.Error == "static_value_skipped_mono") return "(static, value not captured on Mono)";
            if (f.Error == "static_value_not_captured") return "(static, value capture disabled)";
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

/// <summary><see cref="ObservableCollection{T}"/> that supports bulk
/// replacement with a SINGLE <see cref="NotifyCollectionChangedAction.Reset"/>
/// event instead of N separate <c>Add</c> events. Critical for the
/// diff DataGrid — toggling "show added" causes ~740K rows to enter
/// the filtered set; per-row Add notifications hang the UI for
/// minutes (Avalonia DataGrid re-layouts on each event), while a
/// single Reset is fast.
/// <para/>
/// <c>ReplaceAll</c> internally swaps the underlying <c>Items</c> list
/// and emits Count + Item[] + Reset notifications in the order
/// .NET's reference implementation does — bindings that listen to
/// any of the three stay correct.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Drop every current item and stage the given sequence as
    /// the new content. Fires one Reset event (and Count + Item[]
    /// PropertyChanged), regardless of how many items were added or
    /// removed. Pass <c>null</c> or an empty sequence to clear.</summary>
    public void ReplaceAll(IEnumerable<T>? items)
    {
        CheckReentrancy();
        Items.Clear();
        if (items is not null)
        {
            foreach (var item in items) Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
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

/// <summary>Sub-classification of a row whose top-level ChangeKind is
/// "changed". Drives the three-way Increased/Decreased/OtherChanged
/// checkbox dispatch in <see cref="SnapshotViewModel.RebuildFilteredRows"/>
/// + the matching counter increment.</summary>
public enum DiffRowKindSubclass
{
    None,           // not a "changed" row (added/removed/unchanged)
    Increased,      // numeric, new > old
    Decreased,      // numeric, new < old
    OtherChanged,   // non-numeric change, or numeric with unparseable side
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
