// SpcQueryViewModel — Phase 29d-2.
//
// Drives the "SPC Query" tab. Snapshot pool is shared with the Snapshot
// tab (same SnapshotViewModel instance owns the underlying collection
// and per-game SQLite store); this VM just adds the per-snapshot
// predicate row + query-runner UI on top.
//
// Cross-session / cross-build auto-mode mirrors Phase 29c-5 diff
// design: when the selected snapshot set spans multiple game sessions,
// StaticsOnly is auto-enabled (instance addresses don't align cross-
// session); when it spans multiple game builds, a yellow warning
// banner is shown and the user can still proceed (static offsets may
// have shifted).
//
// 29d-4 streaming hydrate isn't here — for N ≤ 5 the VM simply calls
// EnsureLoadedAsync on every selected envelope before running. The
// streaming-mode upgrade lands in 29d-4 with a progress bar.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class SpcQueryViewModel : ObservableObject
{
    private readonly SnapshotViewModel _snapshot;
    private readonly FileLogger        _logger;

    public SpcQueryViewModel(SnapshotViewModel snapshot, FileLogger logger)
    {
        _snapshot = snapshot;
        _logger   = logger;

        // Re-publish the snapshot pool so the SPC tab tracks adds/removes
        // from the Snapshot tab without separate plumbing.
        _snapshot.Snapshots.CollectionChanged += (_, _) => SyncSelectionRows();
        SyncSelectionRows();
    }

    /// <summary>Each row in the snapshot-picker list. Wraps an envelope
    /// with a per-VM Include toggle + per-snapshot predicate.</summary>
    public ObservableCollection<SpcSnapshotRow> SelectionRows { get; } = new();

    /// <summary>Snapshots currently checked in <see cref="SelectionRows"/>,
    /// re-evaluated lazily when Run fires.</summary>
    private List<SpcSnapshotRow> SelectedRows()
        => SelectionRows.Where(r => r.Include).ToList();

    /// <summary>Result rows from the last successful query.</summary>
    public ObservableCollection<SpcMatch> Results { get; } = new();

    /// <summary>Status text under the run button — query outcome,
    /// preflight warnings, or errors.</summary>
    [ObservableProperty] private string _statusMessage =
        "Pick 2+ snapshots, set per-snapshot predicates, press Run.";

    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Banner copy describing cross-session / cross-build mode.
    /// Empty when all selected snapshots share session+build.</summary>
    [ObservableProperty] private string _modeBanner = string.Empty;

    /// <summary>Auto-set during preflight when snapshots span sessions.
    /// User can override only when all share session_id.</summary>
    [ObservableProperty] private bool _staticsOnly;

    [ObservableProperty] private bool _excludeSynthetic = true;
    [ObservableProperty] private bool _numericOnly      = true;

    [ObservableProperty] private string _classFilter = string.Empty;
    [ObservableProperty] private string _typeFilter  = string.Empty;

    [ObservableProperty] private bool _isRunning;

    /// <summary>Cross-tab handoff hook — wired by MainWindow code-behind
    /// the same way SnapshotViewModel.NavigateToInspectorRequested is.</summary>
    public event EventHandler<string>? NavigateToInspectorRequested;

    public void RequestInspectInstance(SpcMatch match)
    {
        if (match.IsStatic) return;
        NavigateToInspectorRequested?.Invoke(this, match.InstanceAddress);
    }

    /// <summary>Sync SelectionRows to mirror the underlying snapshots
    /// pool. Preserves the per-row Predicate + Include flag when an
    /// envelope was already in the list. New rows default to Include=ON
    /// + PredicateKind=Any.</summary>
    private void SyncSelectionRows()
    {
        var snapById = new Dictionary<SnapshotEnvelope, SpcSnapshotRow>();
        foreach (var r in SelectionRows) snapById[r.Snapshot] = r;

        // Walk in capture-order (CapturedAtUtc ascending) so the
        // per-snapshot predicate chain matches the user's mental "snap1
        // → snap2 → snap3" timeline left-to-right.
        var ordered = _snapshot.Snapshots
            .OrderBy(s => s.CapturedAtUtc, StringComparer.Ordinal)
            .ToList();

        SelectionRows.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            var snap = ordered[i];
            if (snapById.TryGetValue(snap, out var existing))
            {
                existing.Index = i;
                SelectionRows.Add(existing);
            }
            else
            {
                SelectionRows.Add(new SpcSnapshotRow
                {
                    Snapshot       = snap,
                    Index          = i,
                    Include        = true,
                    PredicateKind  = SpcPredicateKind.Any,
                });
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count < 2)
        {
            ErrorMessage  = "Pick at least 2 snapshots.";
            StatusMessage = string.Empty;
            return;
        }
        if (selected[0].PredicateKind is
            SpcPredicateKind.Unchanged or SpcPredicateKind.Increased or
            SpcPredicateKind.Decreased or SpcPredicateKind.DeltaExact or
            SpcPredicateKind.DeltaRange)
        {
            ErrorMessage  = "First snapshot's predicate cannot be a relative kind (Unchanged / Increased / Decreased / Δ). Switch to Any or an absolute predicate (Exact / Range / NotEqual).";
            StatusMessage = string.Empty;
            return;
        }

        try
        {
            IsRunning = true;
            UpdateModeBanner(selected);

            // Phase 29d-3: auto-enable StaticsOnly when sessions differ.
            bool anySessionMismatch = selected.Skip(1).Any(r =>
                !string.Equals(r.Snapshot.GameSessionId,
                               selected[0].Snapshot.GameSessionId,
                               StringComparison.Ordinal));
            if (anySessionMismatch) StaticsOnly = true;

            // Hydrate all selected envelopes — 29d-4 will swap this for
            // a streaming path when count > 5.
            foreach (var r in selected)
            {
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);
            }

            var query = new SpcQuery
            {
                Snapshots        = selected.Select(r => r.Snapshot).ToList(),
                Predicates       = selected.Select(r => r.BuildPredicate()).ToList(),
                StaticsOnly      = StaticsOnly,
                ExcludeSynthetic = ExcludeSynthetic,
                NumericOnly      = NumericOnly,
                ClassFilter      = string.IsNullOrWhiteSpace(ClassFilter) ? null : ClassFilter.Trim(),
                TypeFilter       = string.IsNullOrWhiteSpace(TypeFilter)  ? null : TypeFilter.Trim(),
            };

            var sw = Stopwatch.StartNew();
            var matches = SpcQueryEngine.Run(query);
            sw.Stop();

            Results.Clear();
            foreach (var m in matches) Results.Add(m);

            StatusMessage = $"SPC: {matches.Count:N0} match(es) across {selected.Count:N0} snapshot(s) in {sw.ElapsedMilliseconds} ms.";
            _logger.Write(SnapshotStore.LogCategory,
                $"SpcQuery: snaps={selected.Count} preds=[{string.Join(',', selected.Select(r => r.PredicateKind))}] " +
                $"staticsOnly={StaticsOnly} → {matches.Count} matches in {sw.ElapsedMilliseconds}ms");
        }
        catch (ArgumentException ax)
        {
            ErrorMessage = ax.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory,
                $"SpcQuery FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning && _snapshot.Snapshots.Count >= 2;

    /// <summary>Compose <see cref="ModeBanner"/> for the user — explains
    /// what cross-session / cross-build auto-mode just did.</summary>
    private void UpdateModeBanner(IReadOnlyList<SpcSnapshotRow> selected)
    {
        if (selected.Count == 0) { ModeBanner = string.Empty; return; }
        var firstBuild   = selected[0].Snapshot.GameBuildId;
        var firstSession = selected[0].Snapshot.GameSessionId;
        bool buildMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameBuildId, firstBuild, StringComparison.Ordinal));
        bool sessionMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameSessionId, firstSession, StringComparison.Ordinal));

        if (buildMismatch)
        {
            ModeBanner = "⚠ Cross-build SPC query — game build_id differs across the selected snapshots. Static field offsets may have shifted between game versions; result is best-effort only.";
        }
        else if (sessionMismatch)
        {
            ModeBanner = "ℹ Cross-session SPC query — selected snapshots span multiple game sessions. Statics-only has been auto-enabled (instance addresses don't survive game launches).";
        }
        else
        {
            ModeBanner = string.Empty;
        }
    }

    /// <summary>Helper for the per-row "set predicate" UI — picks the
    /// row's editable PredicateKind without typing too much XAML.</summary>
    public static IReadOnlyList<SpcPredicateKind> AllPredicateKinds { get; } =
        Enum.GetValues<SpcPredicateKind>();
}

/// <summary>One row in the snapshot-picker / predicate-editor grid.</summary>
public sealed partial class SpcSnapshotRow : ObservableObject
{
    public required SnapshotEnvelope Snapshot      { get; init; }
    [ObservableProperty] private int  _index;
    [ObservableProperty] private bool _include;
    [ObservableProperty] private SpcPredicateKind _predicateKind;
    [ObservableProperty] private string _operandText  = string.Empty;
    [ObservableProperty] private string _operand2Text = string.Empty;

    /// <summary>Compose the actual <see cref="SpcPredicate"/> for the
    /// engine. Parses the operand text per predicate kind; invalid
    /// numeric input becomes <c>null</c> (engine treats as missing).</summary>
    public SpcPredicate BuildPredicate()
    {
        double? op  = ParseOperand(OperandText);
        double? op2 = ParseOperand(Operand2Text);
        return new SpcPredicate
        {
            Kind     = PredicateKind,
            Operand  = op,
            Operand2 = op2,
        };
    }

    private static double? ParseOperand(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>UI label for the snapshot — mirrors
    /// <see cref="SnapshotEnvelope.DisplayName"/> but adds the index so
    /// the user sees the predicate-chain order at a glance.</summary>
    public string DisplayLabel => $"[{Index}] {Snapshot.DisplayName}";
}
