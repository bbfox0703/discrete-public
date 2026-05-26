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

    public SnapshotViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
    }

    /// <summary>Snapshots the user has taken in this session. List
    /// order = chronological. Most users only ever hold 2 — before and
    /// after an in-game action — but multi-snapshot diff (any A → any B)
    /// works for free.</summary>
    public ObservableCollection<SnapshotEnvelope> Snapshots { get; } = new();

    /// <summary>Rows currently shown in the diff DataGrid. Rebuilt on
    /// every Diff press + every filter toggle.</summary>
    public ObservableCollection<SnapshotDiffRow> DiffRows { get; } = new();

    // -----------------------------------------------------------------------
    // Capture options ("全抓" defaults; user can opt in to narrowing).
    // -----------------------------------------------------------------------

    /// <summary>Optional comma-separated assembly filter. Empty → all
    /// visible assemblies (the design rule from Phase 29 plan: user
    /// shouldn't have to know where data lives).</summary>
    [ObservableProperty] private string _scopeAssembliesText = string.Empty;

    [ObservableProperty] private int _instanceCap = 100;
    [ObservableProperty] private int _arrayCap    = 50;

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

    // -----------------------------------------------------------------------
    // Selection state.
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotCommand))]
    private SnapshotEnvelope? _selectedSnapshotA;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    private SnapshotEnvelope? _selectedSnapshotB;

    // -----------------------------------------------------------------------
    // Diff filter options (Strategy C — UI-side filtering only).
    // -----------------------------------------------------------------------

    /// <summary>Default ON. Filters out classes whose names start with
    /// '&lt;' (synthetic compiler-generated types like
    /// <c>&lt;Module&gt;</c>, <c>&lt;&gt;f__AnonymousType*</c>,
    /// <c>&lt;&gt;c</c>, <c>&lt;Start&gt;d__N</c>) and rows where the
    /// field's "from" hint indicates open-generic context.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDiffRows))]
    private bool _hideSyntheticTypes = true;

    /// <summary>Default ON. Shows only rows where value actually differs
    /// between snapshots — added/removed rows alone (no value change)
    /// stay hidden unless <see cref="ShowAdded"/> / <see cref="ShowRemoved"/>
    /// is also on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDiffRows))]
    private bool _changedOnly = true;

    /// <summary>Show "added" rows (in B but not A). Off by default —
    /// most diffs are about value changes, not net new fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDiffRows))]
    private bool _showAdded;

    /// <summary>Show "removed" rows (in A but not B). Same default as
    /// <see cref="ShowAdded"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDiffRows))]
    private bool _showRemoved;

    /// <summary>Class-name regex / substring filter on the diff result.
    /// Lets the user narrow to a single class quickly after a wide diff.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDiffRows))]
    private string _classFilterText = string.Empty;

    /// <summary>Live-projection over <see cref="DiffRows"/>. The view
    /// binds to this; the underlying <c>DiffRows</c> stays canonical.</summary>
    public IEnumerable<SnapshotDiffRow> FilteredDiffRows
    {
        get
        {
            var clsFilter = ClassFilterText;
            bool hasClsFilter = !string.IsNullOrWhiteSpace(clsFilter);
            foreach (var r in DiffRows)
            {
                if (HideSyntheticTypes && r.IsSynthetic) continue;
                if (ChangedOnly && r.ChangeKind != "changed" && !ShowAdded && !ShowRemoved) continue;
                if (r.ChangeKind == "added"   && !ShowAdded   && !(r.ChangeKind == "changed")) continue;
                if (r.ChangeKind == "removed" && !ShowRemoved && !(r.ChangeKind == "changed")) continue;
                if (hasClsFilter &&
                    r.ClassDisplay.IndexOf(clsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                yield return r;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Status / progress.
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropSnapshotCommand))]
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
        // Phase 1 — heap walk + per-class decode + GC drain on the
        // server side. The elapsed-time DispatcherTimer updates the
        // visible status every 200ms so the user can see we're alive.
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

            int cap = InstanceCap > 0 ? InstanceCap : 100;
            int arr = ArrayCap    > 0 ? ArrayCap    : 50;

            var begin = await _backend.SnapshotBeginAsync(
                scope, cap, arr, IncludeSystemAssemblies, ct).ConfigureAwait(true);
            StopElapsedTimer();  // Phase 1 done; manual status from here.
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
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Streaming chunk {0}/{1} ({2:N0} of {3:N0} classes)… capture {4:F1}s + stream {5:F1}s",
                    chunkCount, expectedChunks,
                    classes.Count, totalClasses,
                    beginElapsedSec, chunkPhaseSw.Elapsed.TotalSeconds);
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
            StatusMessage = $"Snapshot #{Snapshots.Count} {label} — {env.TotalClasses:N0} classes, {env.TotalInstances:N0} instances, {env.FieldIndex.Count:N0} field-values (captured in {sw.Elapsed.TotalSeconds:F1}s across {chunkCount} chunks{excluded}{cacheNote}).";
            _logger.Write(Constants.LogCategoryView,
                $"Snapshot: id={begin.SnapshotId} classes={env.TotalClasses} instances={env.TotalInstances} fieldValues={env.FieldIndex.Count} elapsed={sw.Elapsed.TotalSeconds:F1}s excluded={begin.ExcludedSystemAssemblies}");
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
        int changed = 0, added = 0, removed = 0;

        // Walk A → B keys to find changed + removed; second pass walks B → A
        // to catch the added.
        foreach (var (key, va) in a.FieldIndex)
        {
            if (b.FieldIndex.TryGetValue(key, out var vb))
            {
                if (!string.Equals(va.ValueDisplay, vb.ValueDisplay, StringComparison.Ordinal))
                {
                    DiffRows.Add(SnapshotDiffRow.Changed(va, vb));
                    changed++;
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
        OnPropertyChanged(nameof(FilteredDiffRows));
        StatusMessage = $"Diff {a.ShortLabel} → {b.ShortLabel}: {changed:N0} changed, {added:N0} added, {removed:N0} removed (in {sw.ElapsedMilliseconds} ms).";
    }

    [RelayCommand(CanExecute = nameof(CanDropSnapshot))]
    public void DropSnapshot()
    {
        if (SelectedSnapshotA is { } a)
        {
            Snapshots.Remove(a);
            SelectedSnapshotA = null;
            DiffRows.Clear();
            OnPropertyChanged(nameof(FilteredDiffRows));
            StatusMessage = "Snapshot dropped.";
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        Snapshots.Clear();
        SelectedSnapshotA = null;
        SelectedSnapshotB = null;
        DiffRows.Clear();
        OnPropertyChanged(nameof(FilteredDiffRows));
        StatusMessage = "All snapshots cleared.";
        ErrorMessage  = null;
    }

    private bool CanTakeSnapshot()    => !IsBusy;
    private bool CanDiff()            => !IsBusy && SelectedSnapshotA is not null && SelectedSnapshotB is not null;
    private bool CanDropSnapshot()    => !IsBusy && SelectedSnapshotA is not null;

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
            StatusMessage = $"{verb}… {_elapsedStopwatch.Elapsed.TotalSeconds:F1} s";
        }
    }
}

// ===========================================================================
// Local model — one captured snapshot held in memory for the session.
// ===========================================================================

public sealed class SnapshotEnvelope
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

    public string DisplayName
        => string.IsNullOrEmpty(Label)
            ? $"{CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst"
            : $"{Label} — {CapturedAt:HH:mm:ss} — {TotalClasses:N0} cls / {TotalInstances:N0} inst";

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
            return $"(err: {f.Error})";
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

    public string OffsetLabel  => $"0x{FieldOffset:X}";

    public static SnapshotDiffRow Changed(SnapshotFieldValue a, SnapshotFieldValue b)
        => new()
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
            Delta           = ComputeDelta(a.ValueDisplay, b.ValueDisplay),
            ChangeKind      = "changed",
            IsSynthetic     = a.IsSynthetic || b.IsSynthetic,
        };

    public static SnapshotDiffRow Added(SnapshotFieldValue b)
        => new()
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
        };

    public static SnapshotDiffRow Removed(SnapshotFieldValue a)
        => new()
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
        };

    /// <summary>Best-effort numeric delta. Tries to parse both sides as
    /// double; if both parse, emits <c>{newer-older}</c> formatted with
    /// sign. Falls back to a sentinel for non-numeric changes ("…").</summary>
    private static string ComputeDelta(string oldVal, string newVal)
    {
        if (double.TryParse(oldVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(newVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            double d = b - a;
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
