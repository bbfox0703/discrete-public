// ValueSearchViewModel.Group.cs — G5.
//
// Group (multiple-values) mode for the Value Search tab. A Single/Group toggle
// swaps the single-value input for a 2..4 row slot grid; First Scan finds every
// instance holding ALL of the values at distinct numeric fields (a System of
// Distinct Representatives), Next Scan applies a PER-SLOT refine predicate
// (e.g. slot0 decreased + slot1 unchanged) and re-checks the SDR.
//
// Reuses the single-mode plumbing (IsBusy, the bulk lease, _cts, the elapsed
// timer, _backend) — group scan is the same long bulk op, just N values at once.
// AOT-safe: CommunityToolkit.Mvvm source-gen, no reflection.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class ValueSearchViewModel
{
    /// <summary>Single vs Group mode. The XAML overlays two panels on this.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GroupFirstScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GroupNextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GroupEndSessionCommand))]
    private bool _isGroupMode;

    /// <summary>2..4 editable input rows.</summary>
    public ObservableCollection<GroupSlotInput> GroupInputs { get; } = new()
    {
        new GroupSlotInput(),
        new GroupSlotInput(),
    };

    /// <summary>Group candidate rows shown in the grid — the VIEW over
    /// <see cref="_groupAll"/> after the system-assembly filter.</summary>
    public ObservableCollection<GroupCandidateRow> GroupResults { get; } = new();

    /// <summary>Unfiltered group candidates (the store the filter re-derives from).</summary>
    private readonly List<GroupCandidateRow> _groupAll = new();

    /// <summary>Conservative opt-in: hide candidates in engine / framework
    /// assemblies (Unity*/System*/Cinemachine*/…) so the grid shows game-code
    /// objects only. ON by default — a value like 65 otherwise matches thousands
    /// of engine objects. Reuses the single-mode prefix list.</summary>
    [ObservableProperty] private bool _ignoreSystemAssembliesGroup = true;
    partial void OnIgnoreSystemAssembliesGroupChanged(bool value) => RefreshGroupView();

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
        => IsSystemName(r.Assembly) || IsSystemName(r.Namespace) || IsSystemName(r.ClassDisplay);

    [ObservableProperty] private GroupCandidateRow? _selectedGroupRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GroupNextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GroupEndSessionCommand))]
    [NotifyPropertyChangedFor(nameof(HasGroupSession))]
    private string? _groupSessionId;

    public bool HasGroupSession => !string.IsNullOrEmpty(GroupSessionId);

    public bool CanAddSlot    => GroupInputs.Count < 4;
    public bool CanRemoveSlot => GroupInputs.Count > 2;

    [RelayCommand]
    private void AddSlot()
    {
        if (!CanAddSlot) return;
        GroupInputs.Add(new GroupSlotInput());
        OnPropertyChanged(nameof(CanAddSlot));
        OnPropertyChanged(nameof(CanRemoveSlot));
        GroupFirstScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveSlot()
    {
        if (!CanRemoveSlot) return;
        GroupInputs.RemoveAt(GroupInputs.Count - 1);
        OnPropertyChanged(nameof(CanAddSlot));
        OnPropertyChanged(nameof(CanRemoveSlot));
        GroupFirstScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGroupFirstScan))]
    public async Task GroupFirstScanAsync()
    {
        // Validate: 2..4 slots, each a numeric value, scanType exact/range only.
        var slots = new List<GroupSlotRequest>(GroupInputs.Count);
        for (int i = 0; i < GroupInputs.Count; i++)
        {
            var s = GroupInputs[i];
            if (s.ScanType is not ("exact" or "range"))
            {
                ErrorMessage  = $"Slot {i + 1}: First Scan accepts only Exact or Range (prev-value modes are for Next Scan).";
                StatusMessage = "First Scan failed.";
                return;
            }
            if (!double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                ErrorMessage  = $"Slot {i + 1}: '{s.Value}' is not a valid number.";
                StatusMessage = "First Scan failed.";
                return;
            }
            var req = new GroupSlotRequest { DataType = WireDataType(s.DataType), ScanType = s.ScanType, Value = v };
            if (s.ScanType == "range")
            {
                if (!double.TryParse(s.ValueTo, NumberStyles.Float, CultureInfo.InvariantCulture, out double vt))
                {
                    ErrorMessage  = $"Slot {i + 1}: range upper bound '{s.ValueTo}' is not a valid number.";
                    StatusMessage = "First Scan failed.";
                    return;
                }
                req.ValueTo = vt;
            }
            if (double.TryParse(s.Tolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out double tol))
                req.Tolerance = tol;
            slots.Add(req);
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
        _bulkLease = _bulkGate?.TryEnter("Group scan");
        GroupResults.Clear();
        _groupAll.Clear();
        StartElapsedTimer("Group scanning");

        if (HasGroupSession)
            await CloseGroupSessionAsync(ct).ConfigureAwait(true);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _backend.BeginGroupScanAsync(
                slots, limit: Limit, retainCap: MaxCandidates,
                parallel: ParallelArg, ct: ct).ConfigureAwait(true);

            if (string.Equals(result.Error, "cancelled", StringComparison.Ordinal) || result.Cancelled)
            {
                StatusMessage = "Group scan cancelled.";
                return;
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage  = result.Error;
                StatusMessage = "First Scan failed.";
                return;
            }

            GroupSessionId = string.IsNullOrEmpty(result.SessionId) ? null : result.SessionId;
            _groupAll.Clear();
            foreach (var c in result.Candidates)
                _groupAll.Add(new GroupCandidateRow(c));
            RefreshGroupView();

            sw.Stop();
            string trunc = result.Truncated ? " (truncated — refine to narrow)" : string.Empty;
            string filt = GroupResults.Count != _groupAll.Count
                ? $" ({_groupAll.Count - GroupResults.Count} system hidden)" : string.Empty;
            StatusMessage =
                $"{GroupResults.Count} shown{filt} of {result.TotalCandidates} retained; " +
                $"scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms, " +
                $"session={GroupSessionId}.{trunc}";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "First Scan failed.";
            _logger.Write(Constants.LogCategoryView, $"ValueSearch.GroupFirstScan failed: {ex.Message}");
        }
        finally
        {
            StopElapsedTimer();
            IsBusy = false;
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGroupNextScan))]
    public async Task GroupNextScanAsync()
    {
        // Per-slot refine predicate. value/valueTo only required for
        // exact/range/newValue; the prev-value modes need no operand.
        var slots = new List<GroupSlotRequest>(GroupInputs.Count);
        for (int i = 0; i < GroupInputs.Count; i++)
        {
            var s = GroupInputs[i];
            var req = new GroupSlotRequest { ScanType = s.ScanType };
            if (s.ScanType is "exact" or "range" or "newValue")
            {
                if (!double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    ErrorMessage  = $"Slot {i + 1}: '{s.Value}' is not a valid number for {s.ScanType}.";
                    StatusMessage = "Next Scan failed.";
                    return;
                }
                req.Value = v;
                if (s.ScanType == "range")
                {
                    if (!double.TryParse(s.ValueTo, NumberStyles.Float, CultureInfo.InvariantCulture, out double vt))
                    {
                        ErrorMessage  = $"Slot {i + 1}: range upper bound '{s.ValueTo}' is not a valid number.";
                        StatusMessage = "Next Scan failed.";
                        return;
                    }
                    req.ValueTo = vt;
                }
            }
            if (double.TryParse(s.Tolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out double tol))
                req.Tolerance = tol;
            slots.Add(req);
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
        StartElapsedTimer("Group refining");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _backend.RefineGroupScanAsync(GroupSessionId!, slots, 500, ct)
                .ConfigureAwait(true);
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage  = result.Error;
                StatusMessage = "Next Scan failed.";
                return;
            }
            _groupAll.Clear();
            foreach (var c in result.Candidates)
                _groupAll.Add(new GroupCandidateRow(c));
            RefreshGroupView();

            sw.Stop();
            string trunc = result.Truncated ? " (more retained than shown)" : string.Empty;
            StatusMessage =
                $"{result.TotalCandidates} group candidate(s) remaining, removed {result.Removed} this round " +
                $"({sw.ElapsedMilliseconds} ms).{trunc}";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Next Scan failed.";
            _logger.Write(Constants.LogCategoryView, $"ValueSearch.GroupNextScan failed: {ex.Message}");
        }
        finally
        {
            StopElapsedTimer();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGroupEndSession))]
    public async Task GroupEndSessionAsync()
    {
        if (!HasGroupSession) return;
        await CloseGroupSessionAsync().ConfigureAwait(true);
        GroupResults.Clear();
        _groupAll.Clear();
        StatusMessage = "Group session ended.";
        ErrorMessage  = null;
    }

    private async Task CloseGroupSessionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(GroupSessionId)) return;
        try { await _backend.EndGroupScanAsync(GroupSessionId!, ct).ConfigureAwait(true); }
        catch { /* best-effort */ }
        GroupSessionId = null;
    }

    /// <summary>Double-click a candidate → cross-tab to the Inspector at the
    /// instance (reuses the single-mode handoff by synthesizing a candidate).</summary>
    public void ActivateGroup(GroupCandidateRow row)
        => NavigateToInspectorRequested?.Invoke(row.ToInspectorRow());

    // Enabled whenever we're idle in Group mode with >= 2 rows. Per-value
    // validation happens inside the command (it surfaces a clear ErrorMessage) —
    // gating on filled values here would need per-row change subscriptions and
    // left the button stuck disabled until a mode round-trip (the live-smoke bug).
    private bool CanGroupFirstScan()
        => !IsBusy && !BulkBusyElsewhere && IsGroupMode && GroupInputs.Count >= 2;
    private bool CanGroupNextScan()    => !IsBusy && !BulkBusyElsewhere && HasGroupSession;
    private bool CanGroupEndSession()  => !IsBusy && HasGroupSession;

    /// <summary>Map a slot's display dataType to the wire keyword the DLL parses.
    /// The aggregate sentinels become "aggregateNoByte" / "aggregateAll"; concrete
    /// types pass through.</summary>
    private static string WireDataType(string display) => display switch
    {
        AggregateNoByte => "aggregateNoByte",
        AggregateAll    => "aggregateAll",
        _               => display,
    };
}

/// <summary>One editable group-input row. DataType + per-slot predicate + value(s).</summary>
public sealed partial class GroupSlotInput : ObservableObject
{
    // Default to the aggregate "any numeric width (no 1-byte)" so the user need
    // not know whether the game stores the value as int / float / double — the
    // DLL scans every fitting width for this slot.
    [ObservableProperty] private string _dataType = ValueSearchViewModel.AggregateNoByte;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresValueInput))]
    [NotifyPropertyChangedFor(nameof(RequiresValue2Input))]
    private string _scanType = "exact";

    [ObservableProperty] private string _value     = string.Empty;
    [ObservableProperty] private string _valueTo   = string.Empty;
    [ObservableProperty] private string _tolerance = "0.5";

    public string[] DataTypes { get; } = new[]
    {
        ValueSearchViewModel.AggregateNoByte,
        ValueSearchViewModel.AggregateAll,
        "int8", "uint8", "int16", "uint16", "int32", "uint32",
        "int64", "uint64", "float", "double",
    };

    // First Scan accepts exact/range; Next Scan adds the prev-value modes. The
    // full set is offered; GroupFirstScan validates exact/range.
    public string[] ScanTypes { get; } = new[]
    {
        "exact", "range", "newValue", "changed", "unchanged", "increased", "decreased",
    };

    public bool RequiresValueInput  => ScanType is "exact" or "range" or "newValue";
    public bool RequiresValue2Input => ScanType == "range";
}

/// <summary>Master row: one matched instance + its per-slot detail.</summary>
public sealed class GroupCandidateRow
{
    public GroupCandidateRow(GroupCandidate c)
    {
        InstanceAddr = c.InstanceAddr;
        Assembly     = c.Assembly  ?? string.Empty;
        Namespace    = c.Namespace ?? string.Empty;
        Class        = c.Class     ?? string.Empty;
        ClassDisplay = string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
        Slots        = c.Slots.Select(s => new GroupSlotMatchRow(s)).ToList();
        Summary      = string.Join("   •   ", Slots.Select(s => s.Summary));
        AllLocked    = Slots.Count > 0 && Slots.All(s => s.Locked);
        LockSummary  = AllLocked
            ? $"🔒 {ClassDisplay} — " + string.Join(", ", Slots.Select(s => $"{s.FieldOrSlot}@{s.OffsetLabel}"))
            : string.Empty;
    }

    public string InstanceAddr { get; }
    public string Assembly     { get; }
    public string Namespace    { get; }
    public string Class        { get; }
    public string ClassDisplay { get; }
    public IReadOnlyList<GroupSlotMatchRow> Slots { get; }
    public string Summary      { get; }
    public bool   AllLocked    { get; }
    public string LockSummary  { get; }

    /// <summary>Synthesize a single-mode row for the Inspector handoff (instance
    /// + the first slot's representative field).</summary>
    public ValueSearchRow ToInspectorRow()
    {
        var first = Slots.Count > 0 ? Slots[0] : null;
        return new ValueSearchRow(new ValueScanCandidate
        {
            InstanceAddr = InstanceAddr,
            Assembly     = Assembly,
            Namespace    = Namespace,
            Class        = Class,
            Address      = first?.Address ?? InstanceAddr,
            Field        = first?.Field,
            FieldType    = first?.FieldType,
            FieldOffset  = first?.FieldOffset,
        });
    }
}

/// <summary>Detail row: one slot's match within a candidate.</summary>
public sealed class GroupSlotMatchRow
{
    public GroupSlotMatchRow(GroupSlotMatch m)
    {
        SlotIndex    = m.SlotIndex;
        Field        = m.Field        ?? string.Empty;
        FieldType    = m.FieldType    ?? string.Empty;
        Address      = m.Address      ?? string.Empty;
        FieldOffset  = m.FieldOffset;
        OffsetLabel  = m.FieldOffset.HasValue ? $"0x{m.FieldOffset.Value:X}" : string.Empty;
        CurrentValue = m.CurrentValue?.ToString() ?? string.Empty;
        Locked       = m.Locked;
        MatchedCount = m.MatchedOffsets?.Count ?? 0;
        FieldOrSlot  = string.IsNullOrEmpty(Field) ? $"slot {SlotIndex}" : Field;
        LockLabel    = Locked ? "🔒 locked" : $"{MatchedCount} candidate offsets";
        Summary      = $"#{SlotIndex} {FieldOrSlot}"
                       + (OffsetLabel.Length > 0 ? $"@{OffsetLabel}" : string.Empty)
                       + $"={CurrentValue}";
    }

    public int     SlotIndex    { get; }
    public string  Field        { get; }
    public string  FieldOrSlot  { get; }
    public string  FieldType    { get; }
    public string  Address      { get; }
    public long?   FieldOffset  { get; }
    public string  OffsetLabel  { get; }
    public string  CurrentValue { get; }
    public bool    Locked       { get; }
    public int     MatchedCount { get; }
    public string  LockLabel    { get; }
    public string  Summary      { get; }
}
