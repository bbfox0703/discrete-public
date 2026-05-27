// PivotViewModel — Phase 29e-2.
//
// Drives the "Class Pivot" tab. Shares the Snapshot tab's envelope
// pool — exactly like SpcQueryViewModel — so the user takes / saves /
// loads snapshots once and three tabs (Snapshot diff / SPC / Pivot)
// all see the same set.
//
// What this tab answers that SPC can't: "for class C, group instances
// across N snapshots by a stable business key (e.g. _propertyID), and
// show how a value field (e.g. _value) trajectory looks for each key
// tuple". Sidesteps both cross-session and intra-session address
// drift — see docs/class-pivot.md for the full design.
//
// Stage 0 (class shortlist with stability metrics) + Stage 1 (auto-
// key discovery) are NOT in this VM yet — 29e-3 and 29e-4 respectively.
// v1 is "manual class + manual K + manual V": the user types the FQN
// + comma-separated field names; AutoCompleteBox suggestions come
// from KnownClasses / KnownFieldNames sourced from currently-hydrated
// snapshots.
//
// Cross-session / cross-build banner is repurposed from SPC's
// "instance fields suppressed" wording — pivot is *built for* cross-
// session, so the same situation reads as "Cross-session pivot —
// keying by (k1, k2) sidesteps address drift" instead of a warning.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class PivotViewModel : ObservableObject
{
    private readonly SnapshotViewModel _snapshot;
    private readonly FileLogger        _logger;

    public PivotViewModel(SnapshotViewModel snapshot, FileLogger logger)
    {
        _snapshot = snapshot;
        _logger   = logger;

        _snapshot.Snapshots.CollectionChanged += (_, _) =>
        {
            SyncSelectionRows();
            RunCommand.NotifyCanExecuteChanged();
        };
        SyncSelectionRows();
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Class FQNs seen across currently-hydrated snapshots.
    /// Populated by <see cref="RefreshKnownClassesAndFields"/> after
    /// every successful Run (so the autocomplete grows with each
    /// hydration). Empty until at least one snapshot is hydrated.</summary>
    public ObservableCollection<string> KnownClasses { get; } = new();

    /// <summary>Field names observed on instances of the currently-typed
    /// <see cref="ClassFqn"/>. Refreshed in two places — after Run (full
    /// re-scan) and on every ClassFqn / Snapshots change (best-effort
    /// scan of already-hydrated snaps). Drives the Key + Value field
    /// AutoCompleteBoxes.</summary>
    public ObservableCollection<string> KnownFieldNames { get; } = new();

    /// <summary>Snapshot-picker rows. Mirrors <see cref="SpcQueryViewModel.SelectionRows"/>
    /// shape but without per-snap predicates — pivot doesn't have them.</summary>
    public ObservableCollection<PivotSnapshotRow> SelectionRows { get; } = new();

    private List<PivotSnapshotRow> SelectedRows()
        => SelectionRows.Where(r => r.Include).ToList();

    /// <summary>Result rows from the last successful pivot. Each row
    /// carries a pre-rendered <see cref="PivotResultRow.TrajectoryDisplay"/>
    /// string the DataGrid shows directly — Avalonia 12 AOT bans
    /// reflection-based Binding paths, so we avoid building dynamic
    /// per-snap columns at runtime. The trajectory string keeps the
    /// per-snap "v0 → v1 → …" feel SPC users already know, and works
    /// for any (snap count, value field count) pair.</summary>
    public ObservableCollection<PivotResultRow> Results { get; } = new();

    [ObservableProperty] private string _statusMessage =
        "Type a class FQN, list key + value field name(s), pick 1+ snapshots, press Run.";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _modeBanner   = string.Empty;
    [ObservableProperty] private bool   _isRunning;

    /// <summary>FQN of the class whose instances to pivot — typed by
    /// the user, suggestions from <see cref="KnownClasses"/>.</summary>
    [ObservableProperty] private string _classFqn = string.Empty;

    /// <summary>Comma-separated key field names. Empty → preflight
    /// error. Compound key = multiple names.</summary>
    [ObservableProperty] private string _keyFieldsText = string.Empty;

    /// <summary>Comma-separated value field names. Empty → preflight
    /// error.</summary>
    [ObservableProperty] private string _valueFieldsText = string.Empty;

    /// <summary>Optional substring filter on the rendered key tuple
    /// (parts joined with ", "). Case-insensitive.</summary>
    [ObservableProperty] private string _keyFilter = string.Empty;

    partial void OnClassFqnChanged(string value) => RefreshKnownFieldNamesForClass();

    /// <summary>NavigateToInspector handoff — wired by MainWindow to
    /// the cross-tab navigation handler that switches to Inspector
    /// and seeds the address. Mirrors SpcQuery + Snapshot tab.</summary>
    public event EventHandler<string>? NavigateToInspectorRequested;

    public void RequestInspectInstance(PivotResultRow row, int snapIdx = -1)
    {
        if (row is null) return;
        string? addr = null;
        if (snapIdx >= 0 && snapIdx < row.AddressPerSnap.Count)
        {
            addr = row.AddressPerSnap[snapIdx];
        }
        if (string.IsNullOrEmpty(addr))
        {
            // Fall back to the latest snap that has an address.
            for (int i = row.AddressPerSnap.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(row.AddressPerSnap[i])) { addr = row.AddressPerSnap[i]; break; }
            }
        }
        if (string.IsNullOrEmpty(addr)) return;
        NavigateToInspectorRequested?.Invoke(this, addr!);
    }

    /// <summary>Clipboard hook installed by MainWindow — same pattern
    /// as SpcQuery. Receives a single string to copy.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [RelayCommand]
    public async Task CopyKeyTupleAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        await CopyToClipboardAsync(row.KeyTupleDisplay).ConfigureAwait(true);
        StatusMessage = $"Copied key tuple: {row.KeyTupleDisplay}";
    }

    [RelayCommand]
    public async Task CopyValueSequenceAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        // For multi-value-field rows, flatten all value-field sequences
        // into one user-visible string. Per-field readability beats raw
        // tab-delimited dumps here — the user just wants "what trajectory
        // does this row show".
        var sb = new System.Text.StringBuilder();
        for (int v = 0; v < row.PerValueFieldSequences.Count; v++)
        {
            if (v > 0) sb.Append('\n');
            sb.Append(row.PerValueFieldSequences[v].FieldName).Append(": ");
            sb.Append(string.Join(" → ", row.PerValueFieldSequences[v].Values.Select(s => s ?? "—")));
        }
        await CopyToClipboardAsync(sb.ToString()).ConfigureAwait(true);
        StatusMessage = "Copied value sequence.";
    }

    [RelayCommand]
    public async Task CopyAddressLatestAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        for (int i = row.AddressPerSnap.Count - 1; i >= 0; i--)
        {
            var addr = row.AddressPerSnap[i];
            if (!string.IsNullOrEmpty(addr))
            {
                await CopyToClipboardAsync(addr!).ConfigureAwait(true);
                StatusMessage = $"Copied instance address: {addr}";
                return;
            }
        }
        StatusMessage = "No instance address available for this row.";
    }

    /// <summary>Sync SelectionRows to the snapshot pool. Preserves the
    /// per-row Include toggle for rows that survive the resync.
    /// Capture-order sorted so the per-snap columns map left-to-right
    /// to the user's chronological mental model.</summary>
    private void SyncSelectionRows()
    {
        var prevById = new Dictionary<SnapshotEnvelope, PivotSnapshotRow>();
        foreach (var r in SelectionRows) prevById[r.Snapshot] = r;

        var ordered = _snapshot.Snapshots
            .OrderBy(s => s.CapturedAtUtc, StringComparer.Ordinal)
            .ToList();

        SelectionRows.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            var snap = ordered[i];
            if (prevById.TryGetValue(snap, out var prev))
            {
                prev.Index = i;
                SelectionRows.Add(prev);
            }
            else
            {
                SelectionRows.Add(new PivotSnapshotRow
                {
                    Snapshot = snap, Index = i, Include = true,
                });
            }
        }
    }

    private const int kLargeNAdvisory = 5;

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot.";
            StatusMessage = string.Empty;
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassFqn))
        {
            ErrorMessage = "Class FQN is required (e.g. 'Mortal.Core.GameStat').";
            return;
        }
        var keyFields = SplitFieldList(KeyFieldsText);
        if (keyFields.Count == 0)
        {
            ErrorMessage = "List at least one key field name (e.g. '_propertyID' or '_itemGroup, _propertyID').";
            return;
        }
        var valueFields = SplitFieldList(ValueFieldsText);
        if (valueFields.Count == 0)
        {
            ErrorMessage = "List at least one value field name (e.g. '_value' or '_hp, _mp').";
            return;
        }

        try
        {
            IsRunning = true;
            UpdateModeBanner(selected, keyFields);

            // Track which envelopes were already hydrated BEFORE this run
            // so we evict only the ones we ourselves loaded (same policy
            // as SpcQueryViewModel — keeps user-loaded snaps hot for
            // re-diff while not bloating RAM with query-only hydrates).
            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);

            if (selected.Count > kLargeNAdvisory)
            {
                long estPerSnap = selected[0].Snapshot.EstimatedMemoryBytes;
                if (estPerSnap == 0)
                    estPerSnap = selected[0].Snapshot.PersistedBytes * 50;
                long total = estPerSnap * selected.Count;
                StatusMessage = $"Hydrating {selected.Count:N0} snapshots (~{FormatMb(total)} estimated peak RAM).";
            }

            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            // Resolve assembly from any hydrated snapshot that has the
            // class. Required for PivotEngine — same FQN can exist in
            // multiple assemblies (game DLLs + plugins); we pick the
            // first assembly that contains the class in the earliest
            // selected snap.
            string? assembly = ResolveAssemblyForClass(selected[0].Snapshot, ClassFqn)
                            ?? selected.Select(r => ResolveAssemblyForClass(r.Snapshot, ClassFqn))
                                       .FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (string.IsNullOrEmpty(assembly))
            {
                ErrorMessage = $"Class '{ClassFqn}' not found in any selected snapshot. Check the FQN (it must include the namespace; case-sensitive).";
                return;
            }

            var query = new PivotQuery
            {
                Snapshots   = selected.Select(r => r.Snapshot).ToList(),
                Assembly    = assembly!,
                ClassFqn    = ClassFqn.Trim(),
                KeyFields   = keyFields,
                ValueFields = valueFields,
                KeyFilter   = string.IsNullOrWhiteSpace(KeyFilter) ? null : KeyFilter.Trim(),
            };

            var sw = Stopwatch.StartNew();
            var matches = PivotEngine.Run(query);
            sw.Stop();

            // Build PivotResultRow per match — pre-render the trajectory
            // as a multi-line "fieldName: v0 → v1 → …" string. The
            // DataGrid's single Trajectory column binds directly to it
            // via CompiledBinding (AOT-safe). For the single-value-field
            // case the row reads like one SPC line; multi-value rows
            // get one line per value field stacked.
            int snapCount  = selected.Count;
            int valueCount = valueFields.Count;
            Results.Clear();
            foreach (var m in matches)
            {
                var perField = new List<PivotValueFieldSequence>(valueCount);
                var sb = new System.Text.StringBuilder();
                for (int v = 0; v < valueCount; v++)
                {
                    perField.Add(new PivotValueFieldSequence
                    {
                        FieldName = valueFields[v],
                        Values    = m.Values[v],
                    });
                    if (v > 0) sb.Append('\n');
                    if (valueCount > 1) sb.Append(valueFields[v]).Append(": ");
                    for (int s = 0; s < snapCount; s++)
                    {
                        if (s > 0) sb.Append(" → ");
                        sb.Append(m.Values[v][s] ?? "—");
                    }
                }
                Results.Add(new PivotResultRow
                {
                    KeyTupleDisplay        = string.Join(", ", m.KeyTuple),
                    KeyTuple               = m.KeyTuple,
                    TrajectoryDisplay      = sb.ToString(),
                    AddressPerSnap         = m.AddressPerSnap,
                    HasCollision           = m.HasCollision,
                    PerValueFieldSequences = perField,
                });
            }

            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }
            string evictionNote = evicted > 0 ? $" — evicted {evicted:N0} hydrated-for-query envelope(s)" : string.Empty;

            int collisionCount = matches.Count(m => m.HasCollision);
            string collisionNote = collisionCount > 0 ? $", {collisionCount} with collisions" : string.Empty;
            StatusMessage = $"Pivot: {matches.Count:N0} row(s){collisionNote} across {snapCount:N0} snapshot(s) in {sw.ElapsedMilliseconds} ms{evictionNote}.";
            _logger.Write(SnapshotStore.LogCategory,
                $"Pivot: class={ClassFqn} keys=[{string.Join(',', keyFields)}] vals=[{string.Join(',', valueFields)}] " +
                $"snaps={snapCount} → {matches.Count} rows ({collisionCount} collisions) in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (ArgumentException ax)
        {
            ErrorMessage = ax.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"Pivot FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning && _snapshot.Snapshots.Count >= 1;

    private static List<string> SplitFieldList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .ToList();
    }

    private static string? ResolveAssemblyForClass(SnapshotEnvelope env, string fqn)
    {
        if (!env.ClassesLoaded || env.Classes is null) return null;
        foreach (var c in env.Classes)
        {
            string cfqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            if (string.Equals(cfqn, fqn, StringComparison.Ordinal))
                return c.Assembly;
        }
        return null;
    }

    /// <summary>Walk every hydrated snapshot and refresh both the
    /// class autocomplete + the field-name autocomplete for the
    /// currently-typed class. Cheap pass over already-loaded data.</summary>
    private void RefreshKnownClassesAndFields()
    {
        var classSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var env in _snapshot.Snapshots)
        {
            if (!env.ClassesLoaded || env.Classes is null) continue;
            foreach (var c in env.Classes)
            {
                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                classSet.Add(fqn);
            }
        }
        KnownClasses.Clear();
        foreach (var c in classSet.OrderBy(s => s, StringComparer.Ordinal))
            KnownClasses.Add(c);
        RefreshKnownFieldNamesForClass();
    }

    private void RefreshKnownFieldNamesForClass()
    {
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ClassFqn))
        {
            foreach (var env in _snapshot.Snapshots)
            {
                if (!env.ClassesLoaded || env.Classes is null) continue;
                foreach (var c in env.Classes)
                {
                    string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                    if (!string.Equals(fqn, ClassFqn, StringComparison.Ordinal)) continue;
                    if (c.Instances is not null)
                    {
                        foreach (var inst in c.Instances)
                            foreach (var f in inst.Fields)
                                if (!string.IsNullOrEmpty(f.Name)) nameSet.Add(f.Name);
                    }
                }
            }
        }
        KnownFieldNames.Clear();
        foreach (var n in nameSet.OrderBy(s => s, StringComparer.Ordinal))
            KnownFieldNames.Add(n);
    }

    /// <summary>Compose the pivot-specific banner. Pivot is built for
    /// cross-session so the same "different session_id" situation
    /// that triggers an SPC warning is here a feature — the banner
    /// confirms what the user signed up for.</summary>
    private void UpdateModeBanner(IReadOnlyList<PivotSnapshotRow> selected, IReadOnlyList<string> keyFields)
    {
        if (selected.Count == 0) { ModeBanner = string.Empty; return; }
        var firstBuild   = selected[0].Snapshot.GameBuildId;
        var firstSession = selected[0].Snapshot.GameSessionId;
        bool buildMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameBuildId, firstBuild, StringComparison.Ordinal));
        bool sessionMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameSessionId, firstSession, StringComparison.Ordinal));

        string keyDisplay = "(" + string.Join(", ", keyFields) + ")";
        if (buildMismatch)
        {
            ModeBanner = $"⚠ Cross-build pivot — game build_id differs across the selected snapshots. Key fields {keyDisplay} are verified at run-time but field offsets may have shifted between game versions; missing keys render as '(missing)'.";
        }
        else if (sessionMismatch)
        {
            ModeBanner = $"ℹ Cross-session pivot — keying by {keyDisplay} sidesteps address drift across game sessions. This is exactly what the Class Pivot tab is built for.";
        }
        else
        {
            ModeBanner = string.Empty;
        }
    }

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
}

/// <summary>One row in the snapshot-picker grid. Mirrors
/// <see cref="SpcSnapshotRow"/> but without the per-snap predicate
/// fields — pivot doesn't have them.</summary>
public sealed partial class PivotSnapshotRow : ObservableObject
{
    public required SnapshotEnvelope Snapshot { get; init; }
    [ObservableProperty] private int  _index;
    [ObservableProperty] private bool _include;

    public string DisplayLabel => $"[{Index}] {Snapshot.DisplayName}";
}

/// <summary>One result row. Wraps a <see cref="PivotRow"/> with a
/// flat Cells[] array (so a DataGrid column at index
/// <c>v*snapCount + s</c> binds directly to the value-field /
/// snapshot pair).</summary>
public sealed class PivotResultRow
{
    public required string                          KeyTupleDisplay        { get; init; }
    public required IReadOnlyList<string>           KeyTuple               { get; init; }
    /// <summary>Pre-rendered "fieldName: v0 → v1 → …" string (multi-line
    /// when multiple value fields). Bound directly by the result
    /// DataGrid's Trajectory column — avoids Avalonia 12 AOT's
    /// reflection-Binding ban on dynamic column paths.</summary>
    public required string                          TrajectoryDisplay      { get; init; }
    public required IReadOnlyList<string?>          AddressPerSnap         { get; init; }
    public required bool                            HasCollision           { get; init; }
    public required IReadOnlyList<PivotValueFieldSequence> PerValueFieldSequences { get; init; }

    /// <summary>UI helper: a single chip-like cell that renders the
    /// collision flag — used by the DataGrid's first column. Empty
    /// string when no collision so cells look identical to non-
    /// collision rows.</summary>
    public string CollisionMarker => HasCollision ? "⚠" : string.Empty;
}

/// <summary>One value field's per-snap trajectory, kept alongside the
/// flat Cells[] for the Copy-value-sequence context-menu action.</summary>
public sealed class PivotValueFieldSequence
{
    public required string                FieldName { get; init; }
    public required IReadOnlyList<string?> Values   { get; init; }
}
