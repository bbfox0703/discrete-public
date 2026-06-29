// SnapshotViewModel.Group.cs — G6 UI.
//
// Diff / Group mode for the Snapshot panel. Group mode runs SnapshotGroupEngine
// over the selected snapshot(s): Mode A (snapshot A only — absolute predicates)
// or Mode B (A + B — relative first-vs-last predicates, the "Current HP↓ + Max
// HP unchanged" case). Reuses the shared GroupCandidateRow / GroupSlotMatchRow
// display models + the conservative system-assembly filter.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class SnapshotViewModel
{
    /// <summary>Diff vs Group mode for the snapshot panel.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSnapshotGroupCommand))]
    private bool _isGroupMode;

    public ObservableCollection<SnapshotGroupSlotInput> GroupInputs { get; } = new()
    {
        new SnapshotGroupSlotInput(),
        new SnapshotGroupSlotInput(),
    };

    /// <summary>Grid view (after the system-assembly filter).</summary>
    public ObservableCollection<GroupCandidateRow> GroupResults { get; } = new();
    private readonly List<GroupCandidateRow> _groupAll = new();

    [ObservableProperty] private GroupCandidateRow? _selectedGroupRow;

    [ObservableProperty] private bool _ignoreSystemAssembliesGroup = true;
    partial void OnIgnoreSystemAssembliesGroupChanged(bool value) => RefreshGroupView();

    public bool CanAddGroupSlot    => GroupInputs.Count < 4;
    public bool CanRemoveGroupSlot => GroupInputs.Count > 2;

    [RelayCommand]
    private void AddGroupSlot()
    {
        if (GroupInputs.Count >= 4) return;
        GroupInputs.Add(new SnapshotGroupSlotInput());
        NotifyGroupSlotCount();
    }

    [RelayCommand]
    private void RemoveGroupSlot()
    {
        if (GroupInputs.Count <= 2) return;
        GroupInputs.RemoveAt(GroupInputs.Count - 1);
        NotifyGroupSlotCount();
    }

    private void NotifyGroupSlotCount()
    {
        OnPropertyChanged(nameof(CanAddGroupSlot));
        OnPropertyChanged(nameof(CanRemoveGroupSlot));
        RunSnapshotGroupCommand.NotifyCanExecuteChanged();
    }

    private void RefreshGroupView()
    {
        GroupResults.Clear();
        bool hide = IgnoreSystemAssembliesGroup;
        foreach (var row in _groupAll)
        {
            if (hide && IsSystemGroupRow(row)) continue;
            GroupResults.Add(row);
        }
    }

    private static bool IsSystemGroupRow(GroupCandidateRow r)
        => ValueSearchViewModel.IsSystemName(r.Assembly)
        || ValueSearchViewModel.IsSystemName(r.Namespace)
        || ValueSearchViewModel.IsSystemName(r.ClassDisplay);

    [RelayCommand(CanExecute = nameof(CanRunSnapshotGroup))]
    private async Task RunSnapshotGroupAsync()
    {
        if (SelectedSnapshotA is null)
        {
            ErrorMessage = "Pick snapshot A first.";
            return;
        }

        // Build slots; validate absolute targets up front.
        var slots = new List<SnapshotGroupSlot>(GroupInputs.Count);
        for (int i = 0; i < GroupInputs.Count; i++)
        {
            var s = GroupInputs[i];
            double? t = null, t2 = null;
            if (s.RequiresTarget)
            {
                if (!double.TryParse(s.Target, NumberStyles.Float, CultureInfo.InvariantCulture, out double tv))
                {
                    ErrorMessage = $"Slot {i + 1}: '{s.Target}' is not a valid number.";
                    return;
                }
                t = tv;
                if (s.RequiresTarget2)
                {
                    if (!double.TryParse(s.Target2, NumberStyles.Float, CultureInfo.InvariantCulture, out double t2v))
                    {
                        ErrorMessage = $"Slot {i + 1}: upper bound '{s.Target2}' is not a valid number.";
                        return;
                    }
                    t2 = t2v;
                }
            }
            slots.Add(new SnapshotGroupSlot
            {
                Scope = s.ToScope(), Predicate = s.ToPredicate(), Target = t, Target2 = t2,
            });
        }

        var snaps = new List<SnapshotEnvelope> { SelectedSnapshotA };
        if (SelectedSnapshotB is not null && !ReferenceEquals(SelectedSnapshotB, SelectedSnapshotA))
            snaps.Add(SelectedSnapshotB);

        IsBusy = true; ErrorMessage = null;
        _bulkLease = _bulkGate?.TryEnter("Snapshot group");
        GroupResults.Clear();
        _groupAll.Clear();
        try
        {
            foreach (var sn in snaps)
            {
                await EnsureLoadedAsync(sn).ConfigureAwait(true);
                sn.EnsureFieldIndex();
            }
            bool staticsOnly = snaps.Count > 1
                && !string.Equals(snaps[0].GameSessionId, snaps[1].GameSessionId, StringComparison.Ordinal);

            var query = new SnapshotGroupQuery
            {
                Snapshots        = snaps,
                Slots            = slots,
                ExcludeSynthetic = true,
                StaticsOnly      = staticsOnly,
            };

            var sw = Stopwatch.StartNew();
            var cands = await Task.Run(() => SnapshotGroupEngine.Run(query)).ConfigureAwait(true);
            sw.Stop();

            _groupAll.Clear();
            foreach (var c in cands) _groupAll.Add(new GroupCandidateRow(c));
            RefreshGroupView();

            string mode = snaps.Count > 1 ? "Mode B (2-snapshot temporal)" : "Mode A (single, absolute)";
            string filt = GroupResults.Count != _groupAll.Count
                ? $" ({_groupAll.Count - GroupResults.Count} system hidden)" : string.Empty;
            string staticNote = staticsOnly ? " (statics-only — snapshots span sessions)" : string.Empty;
            StatusMessage = $"Snapshot group [{mode}]: {GroupResults.Count} shown{filt} of {cands.Count} match(es) in {sw.ElapsedMilliseconds} ms{staticNote}.";
        }
        catch (ArgumentException ax)
        {
            ErrorMessage = ax.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"SnapshotGroup FAILED: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    private bool CanRunSnapshotGroup()
        => !IsBusy && !BulkBusyElsewhere && IsGroupMode
           && SelectedSnapshotA is not null && GroupInputs.Count >= 2;
}

/// <summary>One editable snapshot-group input row. Scope + predicate + target(s).</summary>
public sealed partial class SnapshotGroupSlotInput : ObservableObject
{
    public const string ScopeNoByte = "numeric (no 1-byte)";
    public const string ScopeAll    = "numeric (all widths)";

    [ObservableProperty] private string _scope = ScopeNoByte;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresTarget))]
    [NotifyPropertyChangedFor(nameof(RequiresTarget2))]
    private string _predicate = "Exact";

    [ObservableProperty] private string _target  = string.Empty;
    [ObservableProperty] private string _target2 = string.Empty;

    public string[] Scopes { get; } = new[] { ScopeNoByte, ScopeAll };

    // Absolute (Mode A or newest value) + relative (Mode B, first-vs-last).
    public string[] Predicates { get; } = new[]
    {
        "Exact", "Bigger", "Smaller", "Between",
        "Changed", "Unchanged", "Increased", "Decreased",
    };

    public bool RequiresTarget  => Predicate is "Exact" or "Bigger" or "Smaller" or "Between";
    public bool RequiresTarget2 => Predicate == "Between";

    public GroupMatch.Scope ToScope()
        => Scope == ScopeAll ? GroupMatch.Scope.NumericAll : GroupMatch.Scope.NumericNoByte;

    public GroupMatch.Predicate ToPredicate() => Predicate switch
    {
        "Bigger"    => GroupMatch.Predicate.Bigger,
        "Smaller"   => GroupMatch.Predicate.Smaller,
        "Between"   => GroupMatch.Predicate.Between,
        "Changed"   => GroupMatch.Predicate.Changed,
        "Unchanged" => GroupMatch.Predicate.Unchanged,
        "Increased" => GroupMatch.Predicate.Increased,
        "Decreased" => GroupMatch.Predicate.Decreased,
        _           => GroupMatch.Predicate.Exact,
    };
}
