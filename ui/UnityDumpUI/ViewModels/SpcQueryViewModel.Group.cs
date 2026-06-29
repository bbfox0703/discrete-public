// SpcQueryViewModel.Group.cs — G7 UI.
//
// Single / Group mode for the SPC Query panel. SPC Group is the N-snapshot
// generalisation: each slot follows its OWN per-snapshot predicate chain, and a
// candidate is an object where N slots each follow their chain at DISTINCT
// fields (SpcGroupEngine).
//
// To avoid a full slot×snapshot predicate matrix, each slot is expressed
// COMPACTLY: an absolute "first" predicate for snapshot 0 (Any/Exact/Range/
// NotEqual) + a relative "rest" predicate applied to snapshots 1..N-1
// (Any/Unchanged/Increased/Decreased). That builds the chain
// [first, rest, rest, …] — enough for the value-signature cases ("9410 then
// unchanged", "5 then increasing"). Reuses the shared GroupCandidateRow display
// + the conservative system-assembly filter, and the existing SelectionRows
// snapshot picker.

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

public sealed partial class SpcQueryViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunGroupCommand))]
    private bool _isGroupMode;

    public ObservableCollection<SpcGroupSlotInput> GroupInputs { get; } = new()
    {
        new SpcGroupSlotInput(),
        new SpcGroupSlotInput(),
    };

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
        GroupInputs.Add(new SpcGroupSlotInput());
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
        RunGroupCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(CanRunGroup))]
    private async Task RunGroupAsync()
    {
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Tick at least one snapshot to query.";
            return;
        }
        int n = selected.Count;

        // Build each slot's chain: [first] + (n-1)×[rest].
        var slots = new List<SpcGroupSlot>(GroupInputs.Count);
        for (int i = 0; i < GroupInputs.Count; i++)
        {
            var gi = GroupInputs[i];
            SpcPredicate first;
            try { first = gi.BuildFirst(); }
            catch (ArgumentException ax) { ErrorMessage = $"Slot {i + 1}: {ax.Message}"; return; }
            var chain = new List<SpcPredicate>(n) { first };
            for (int k = 1; k < n; k++) chain.Add(gi.BuildRest());
            slots.Add(new SpcGroupSlot { Scope = gi.ToScope(), Predicates = chain });
        }

        IsRunning = true; ErrorMessage = string.Empty;
        GroupResults.Clear();
        _groupAll.Clear();
        try
        {
            bool anySessionMismatch = selected.Skip(1).Any(r =>
                !string.Equals(r.Snapshot.GameSessionId, selected[0].Snapshot.GameSessionId, StringComparison.Ordinal));
            if (anySessionMismatch) StaticsOnly = true;

            foreach (var r in selected)
            {
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);
                r.Snapshot.EnsureFieldIndex();
            }

            var query = new SpcGroupQuery
            {
                Snapshots        = selected.Select(r => r.Snapshot).ToList(),
                Slots            = slots,
                StaticsOnly      = StaticsOnly,
                ExcludeSynthetic = ExcludeSynthetic,
                ClassFilter      = string.IsNullOrWhiteSpace(ClassFilter) ? null : ClassFilter.Trim(),
            };

            var sw = Stopwatch.StartNew();
            var cands = await Task.Run(() => SpcGroupEngine.Run(query)).ConfigureAwait(true);
            sw.Stop();

            _groupAll.Clear();
            foreach (var c in cands) _groupAll.Add(new GroupCandidateRow(c));
            RefreshGroupView();

            string filt = GroupResults.Count != _groupAll.Count
                ? $" ({_groupAll.Count - GroupResults.Count} system hidden)" : string.Empty;
            StatusMessage = $"SPC group: {GroupResults.Count} shown{filt} of {cands.Count} match(es) across {n} snapshot(s) in {sw.ElapsedMilliseconds} ms.";
        }
        catch (ArgumentException ax)
        {
            ErrorMessage = ax.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"SpcGroup FAILED: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRunGroup()
        => !IsRunning && IsGroupMode && _snapshot.Snapshots.Count >= 1;
}

/// <summary>One editable SPC-group slot: a width scope + a compact predicate
/// chain (absolute first predicate + relative rest predicate).</summary>
public sealed partial class SpcGroupSlotInput : ObservableObject
{
    public const string ScopeNoByte = "numeric (no 1-byte)";
    public const string ScopeAll    = "numeric (all widths)";

    [ObservableProperty] private string _scope = ScopeNoByte;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresOperand))]
    [NotifyPropertyChangedFor(nameof(RequiresOperand2))]
    private string _firstKind = "Exact";

    [ObservableProperty] private string _operand  = string.Empty;
    [ObservableProperty] private string _operand2 = string.Empty;
    [ObservableProperty] private string _restKind = "Unchanged";

    public string[] Scopes     { get; } = new[] { ScopeNoByte, ScopeAll };
    public string[] FirstKinds { get; } = new[] { "Any", "Exact", "Range", "NotEqual" };
    public string[] RestKinds  { get; } = new[] { "Any", "Unchanged", "Increased", "Decreased" };

    public bool RequiresOperand  => FirstKind is "Exact" or "Range" or "NotEqual";
    public bool RequiresOperand2 => FirstKind == "Range";

    public GroupMatch.Scope ToScope()
        => Scope == ScopeAll ? GroupMatch.Scope.NumericAll : GroupMatch.Scope.NumericNoByte;

    /// <summary>Absolute predicate for snapshot 0. Throws ArgumentException on a
    /// missing operand so the run surfaces a clear per-slot message.</summary>
    public SpcPredicate BuildFirst()
    {
        double? op  = double.TryParse(Operand,  NumberStyles.Float, CultureInfo.InvariantCulture, out var v)  ? v  : (double?)null;
        double? op2 = double.TryParse(Operand2, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2) ? v2 : (double?)null;
        return FirstKind switch
        {
            "Exact"    => op.HasValue  ? SpcPredicate.Exact(op.Value)    : throw new ArgumentException("Exact needs a value."),
            "NotEqual" => op.HasValue  ? SpcPredicate.NotEqual(op.Value) : throw new ArgumentException("NotEqual needs a value."),
            "Range"    => (op.HasValue || op2.HasValue) ? SpcPredicate.Range(op, op2) : throw new ArgumentException("Range needs at least one bound."),
            _          => SpcPredicate.Any,
        };
    }

    /// <summary>Relative predicate applied to every snapshot after the first.</summary>
    public SpcPredicate BuildRest() => RestKind switch
    {
        "Unchanged" => SpcPredicate.Unchanged,
        "Increased" => SpcPredicate.Increased,
        "Decreased" => SpcPredicate.Decreased,
        _           => SpcPredicate.Any,
    };
}
