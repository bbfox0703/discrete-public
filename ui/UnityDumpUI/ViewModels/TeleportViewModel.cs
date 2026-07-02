// TeleportViewModel.cs — Task 6 (Teleport wave 1). Thin Avalonia wrapper over
// the Core TeleportController: target selection, live pose display, 3 marker
// slots + one-way undo, explicit-coordinate + directional teleport, and
// copy-coordinates to the clipboard.
//
// Unity has no universal player chain, so the TARGET is user-picked: paste a
// managed instance address (from findInstances / Address Lookup / Inspector /
// Scene — every one of those already has a "copy address" affordance) into the
// target box and Set Target resolves its Transform. All engine calls run on
// the Unity main thread inside the Core service.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class TeleportViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private readonly TeleportController _controller;
    private bool _busy;
    private TeleportPose? _lastPose;

    public TeleportViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend    = backend;
        _logger     = logger;
        _controller = new TeleportController(new TeleportService(backend));

        var rows = new List<TeleportMarkerRowViewModel>(TeleportMarkerStore.SlotCount);
        for (int i = 0; i < TeleportMarkerStore.SlotCount; i++)
            rows.Add(new TeleportMarkerRowViewModel(i, this));
        MarkerRows = rows;
    }

    /// <summary>Clipboard writer injected by MainWindow (Avalonia TopLevel.Clipboard).</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    public IReadOnlyList<TeleportMarkerRowViewModel> MarkerRows { get; }

    // --- target ---

    /// <summary>Managed instance address (hex, 0x-optional) of the player object.</summary>
    [ObservableProperty]
    private string _targetAddressInput = string.Empty;

    /// <summary>True when the target is a GameObject (Scene handoff); false for a
    /// Component / MonoBehaviour / Transform instance (the findInstances case).</summary>
    [ObservableProperty]
    private bool _targetIsGameObject;

    [ObservableProperty]
    private bool _hasTarget;

    // --- live pose ---

    [ObservableProperty] private string _positionText = "—";   // em dash
    [ObservableProperty] private string _rotationText = "—";

    // --- explicit coordinates ---

    [ObservableProperty] private string _coordinateInput = string.Empty;

    // --- directional ---

    [ObservableProperty] private double _directionalDistance = 5.0;
    [ObservableProperty] private bool   _directionalHorizontal = true;

    // --- undo / status ---

    [ObservableProperty] private bool    _hasUndo;
    [ObservableProperty] private string? _statusMessage;

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task SetTargetAsync()
    {
        if (_busy) return;
        if (!_backend.IsConnected) { StatusMessage = "Not connected — connect to a running game first."; return; }
        string addr = (TargetAddressInput ?? string.Empty).Trim();
        if (addr.Length == 0) { StatusMessage = "Enter a target instance address."; return; }

        _busy = true;
        try
        {
            string? err = await _controller.SetTargetAsync(addr, TargetIsGameObject).ConfigureAwait(true);
            if (err is not null)
            {
                HasTarget = false;
                ClearPose();
                StatusMessage = $"Set target failed: {err}";
                return;
            }
            HasTarget = _controller.HasTarget;
            StatusMessage = "Target resolved.";
            await RefreshPoseInternalAsync().ConfigureAwait(true);
            RefreshMarkerRows();
        }
        catch (Exception ex) { Fail("Set target", ex); }
        finally { _busy = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (!Guard()) return;
        _busy = true;
        try { await RefreshPoseInternalAsync().ConfigureAwait(true); RefreshMarkerRows(); }
        catch (Exception ex) { Fail("Refresh", ex); }
        finally { _busy = false; }
    }

    [RelayCommand]
    private Task UndoAsync() =>
        RunActionAsync(() => _controller.RecallLastAsync(), "Undo");

    [RelayCommand]
    private Task TeleportToCoordsAsync()
    {
        ParsedCoordinates parsed = TeleportCoordinateParser.Parse(CoordinateInput);
        if (!parsed.Ok) { StatusMessage = $"Bad coordinates: {parsed.Error}"; return Task.CompletedTask; }
        (double x, double y, double z)? euler =
            parsed.HasRotation ? (parsed.EulerX, parsed.EulerY, parsed.EulerZ) : null;
        return RunActionAsync(
            () => _controller.TeleportToCoordsAsync(parsed.X, parsed.Y, parsed.Z, euler),
            "Teleport to coordinates");
    }

    [RelayCommand]
    private Task TeleportForwardAsync() =>
        RunActionAsync(() => _controller.TeleportDirectionalAsync(DirectionalDistance, DirectionalHorizontal),
                       "Move forward");

    [RelayCommand]
    private Task TeleportBackwardAsync() =>
        RunActionAsync(() => _controller.TeleportDirectionalAsync(-DirectionalDistance, DirectionalHorizontal),
                       "Move back");

    [RelayCommand]
    private Task CopyCoordsAsync()
    {
        if (CopyToClipboardAsync is not { } copy || _lastPose is not { } p)
        {
            StatusMessage = "No pose to copy — set a target and refresh first.";
            return Task.CompletedTask;
        }
        string text = p.HasRotation
            ? TeleportCoordinateParser.Format(p.X, p.Y, p.Z, p.EulerX, p.EulerY, p.EulerZ)
            : TeleportCoordinateParser.Format(p.X, p.Y, p.Z);
        StatusMessage = "Copied coordinates to clipboard.";
        return copy(text);
    }

    // ------------------------------------------------------------------
    // Called by the marker rows
    // ------------------------------------------------------------------

    internal Task SaveMarkerAsync(int slot) =>
        RunActionAsync(() => _controller.SaveMarkerAsync(slot), $"Save slot {slot + 1}");

    internal Task RecallMarkerAsync(int slot) =>
        RunActionAsync(() => _controller.RecallMarkerAsync(slot), $"Recall slot {slot + 1}");

    internal void ClearMarker(int slot)
    {
        _controller.ClearMarker(slot);
        RefreshMarkerRows();
        StatusMessage = $"Cleared slot {slot + 1}.";
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    // Shared runner for teleport actions: connect/target guard + re-entrancy,
    // then re-read the pose + markers so the display reflects the outcome.
    private async Task RunActionAsync(Func<Task<string?>> action, string label)
    {
        if (_busy) return;
        if (!Guard()) return;
        _busy = true;
        try
        {
            string? err = await action().ConfigureAwait(true);
            StatusMessage = err is null ? $"{label}: OK." : $"{label} failed: {err}";
            await RefreshPoseInternalAsync().ConfigureAwait(true);
            RefreshMarkerRows();
        }
        catch (Exception ex) { Fail(label, ex); }
        finally { _busy = false; }
    }

    private bool Guard()
    {
        if (!_backend.IsConnected) { StatusMessage = "Not connected — connect to a running game first."; return false; }
        if (!HasTarget)            { StatusMessage = "Set a target first."; return false; }
        return true;
    }

    private async Task RefreshPoseInternalAsync()
    {
        TeleportPose pose = await _controller.ReadPoseAsync().ConfigureAwait(true);
        if (!pose.Ok)
        {
            ClearPose();
            StatusMessage = $"Read pose failed: {pose.Error}";
            return;
        }
        _lastPose = pose;
        PositionText = TeleportCoordinateParser.Format(pose.X, pose.Y, pose.Z);
        RotationText = pose.HasRotation
            ? TeleportCoordinateParser.Format(pose.EulerX, pose.EulerY, pose.EulerZ)
            : "(rotation unavailable)";
    }

    private void RefreshMarkerRows()
    {
        for (int i = 0; i < MarkerRows.Count; i++)
            MarkerRows[i].Update(_controller.Markers.Get(i));
        HasUndo = _controller.Markers.Last.Valid;
    }

    private void ClearPose()
    {
        _lastPose = null;
        PositionText = "—";
        RotationText = "—";
    }

    private void Fail(string label, Exception ex)
    {
        StatusMessage = $"{label} error: {ex.Message}";
        _logger.Write("Teleport", $"{label}: {ex}");
    }
}

/// <summary>One marker slot row: display + Save / Recall / Clear buttons that
/// delegate back to the parent <see cref="TeleportViewModel"/>.</summary>
public sealed partial class TeleportMarkerRowViewModel : ObservableObject
{
    private readonly TeleportViewModel _parent;

    public TeleportMarkerRowViewModel(int slot, TeleportViewModel parent)
    {
        Slot      = slot;
        _parent   = parent;
        SlotLabel = $"Slot {slot + 1}";
    }

    public int    Slot      { get; }
    public string SlotLabel { get; }

    [ObservableProperty] private string _display = "empty";
    [ObservableProperty] private bool   _hasMarker;

    [RelayCommand] private Task Save()   => _parent.SaveMarkerAsync(Slot);
    [RelayCommand] private Task Recall() => _parent.RecallMarkerAsync(Slot);
    [RelayCommand] private void Clear()  => _parent.ClearMarker(Slot);

    public void Update(TeleportMarker m)
    {
        HasMarker = m.Valid;
        Display   = m.Valid ? TeleportCoordinateParser.Format(m.X, m.Y, m.Z) : "empty";
    }
}
