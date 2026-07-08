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
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
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
    private readonly List<IGlobalHotkeyRegistration> _hotkeyRegs = new();

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

    /// <summary>Global-hotkey service, injected by MainWindow (Win32 impl). Null
    /// on non-Windows / when unset — the toggle then reports unavailable.</summary>
    public IGlobalHotkeyService? HotkeyService { get; set; }

    public IReadOnlyList<TeleportMarkerRowViewModel> MarkerRows { get; }

    // --- auto-detect (wave A) ---

    /// <summary>Ranked PlayerLocator candidates from the last Auto-detect run.</summary>
    public ObservableCollection<PlayerCandidateRowViewModel> AutoDetectCandidates { get; } = new();

    [ObservableProperty]
    private bool _autoDetecting;

    [ObservableProperty]
    private bool _hasAutoDetectResults;

    // --- detect-by-movement (wave B) ---

    /// <summary>Seconds to keep sampling while the user moves their character.</summary>
    [ObservableProperty]
    private double _movementSampleSeconds = 4.0;

    /// <summary>Countdown / prompt shown during the movement sample.</summary>
    [ObservableProperty]
    private string _movementStatus = string.Empty;

    [ObservableProperty]
    private bool _movementDetecting;

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
    /// <summary>Movement driver of the resolved target (Transform / CharacterController).</summary>
    [ObservableProperty] private string _driverText = string.Empty;

    // --- explicit coordinates ---

    [ObservableProperty] private string _coordinateInput = string.Empty;

    // --- directional ---

    [ObservableProperty] private double _directionalDistance = 5.0;
    [ObservableProperty] private bool   _directionalHorizontal = true;

    // --- undo / status ---

    [ObservableProperty] private bool    _hasUndo;
    [ObservableProperty] private string? _statusMessage;

    // --- global hotkeys ---

    [ObservableProperty] private bool   _globalHotkeysEnabled;
    [ObservableProperty] private string _hotkeyStatus = "Off.";

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        if (_busy) return;
        if (!_backend.IsConnected) { StatusMessage = "Not connected — connect to a running game first."; return; }

        _busy = true;
        AutoDetecting = true;
        try
        {
            StatusMessage = "Auto-detecting player candidates (camera chain / tag / "
                + "CharacterController / common names / player-ish classes)…";
            AutoDetectCandidates.Clear();
            HasAutoDetectResults = false;

            // A fresh locator per run — its engine-assembly cache must not
            // outlive a reconnect to a different game.
            var locator = new PlayerLocator(_backend, _logger);
            PlayerLocateResult r = await locator.LocateAsync().ConfigureAwait(true);
            if (!r.Ok)
            {
                StatusMessage = r.Error ?? "Auto-detect failed.";
                return;
            }

            foreach (PlayerCandidate c in r.Candidates)
                AutoDetectCandidates.Add(new PlayerCandidateRowViewModel(c, this));
            HasAutoDetectResults = AutoDetectCandidates.Count > 0;

            if (r.Candidates.Count == 0)
            {
                StatusMessage = "No candidates found. Try \"Detect by movement\", or paste the "
                    + "player's instance address manually (findInstances / Address Lookup / Scene / Inspector).";
            }
            else if (r.TopConfidence == PlayerConfidence.Low)
            {
                // The camera-was-the-only-hit case — don't pretend we found the player.
                StatusMessage = PlayerLocator.LowConfidenceHint;
            }
            else
            {
                StatusMessage = $"Found {r.Candidates.Count} candidate(s) — best guess is on top. "
                    + "Click Use, then verify with Refresh (position should change as you move in-game).";
            }
        }
        catch (Exception ex) { Fail("Auto-detect", ex); }
        finally { _busy = false; AutoDetecting = false; }
    }

    [RelayCommand]
    private async Task DetectByMovementAsync()
    {
        if (_busy) return;
        if (!_backend.IsConnected) { StatusMessage = "Not connected — connect to a running game first."; return; }

        _busy = true;
        MovementDetecting = true;
        try
        {
            AutoDetectCandidates.Clear();
            HasAutoDetectResults = false;

            var locator = new PlayerLocator(_backend, _logger);

            MovementStatus = "Building the object pool…";
            StatusMessage  = "Detect by movement: get ready to move your character.";
            MovementSample sample = await locator.BeginMovementSampleAsync().ConfigureAwait(true);
            if (!sample.Ok)
            {
                StatusMessage = sample.Error ?? "Movement sampling failed to start.";
                return;
            }
            if (sample.Tracks.Count == 0)
            {
                StatusMessage = "Movement detection found no transforms to sample.";
                return;
            }

            // Countdown while the user moves. The sample already captured start
            // positions; we just wait, then capture the end positions.
            int seconds = Math.Clamp((int)Math.Round(MovementSampleSeconds), 2, 20);
            for (int s = seconds; s > 0; s--)
            {
                string trunc = sample.Truncated
                    ? $" (sampling {sample.Tracks.Count} of {sample.SceneTransformTotal} objects)"
                    : $" (tracking {sample.Tracks.Count} objects)";
                MovementStatus = $"MOVE YOUR CHARACTER NOW — {s}s left{trunc}";
                await Task.Delay(1000).ConfigureAwait(true);
            }
            MovementStatus = "Measuring who moved…";

            PlayerLocateResult r = await locator.FinishMovementSampleAsync(sample).ConfigureAwait(true);
            MovementStatus = string.Empty;
            if (!r.Ok)
            {
                StatusMessage = r.Error ?? "Movement detection failed.";
                return;
            }

            foreach (PlayerCandidate c in r.Candidates)
                AutoDetectCandidates.Add(new PlayerCandidateRowViewModel(c, this));
            HasAutoDetectResults = AutoDetectCandidates.Count > 0;

            StatusMessage = r.Candidates.Count == 0
                ? (sample.Truncated
                    ? $"Nothing moved among the {sample.Tracks.Count} sampled objects. The player may be "
                      + $"outside the sample ({sample.SceneTransformTotal} total) — raise the sample cap, or "
                      + "move a larger distance, then retry."
                    : "Nothing moved. Move your character during the countdown, then retry.")
                : "Top rows are the resolved ROOT of the moving parts (what you teleport); the leaf "
                  + "bones below are the raw movers. Pick the root whose name looks like your character, "
                  + "click Use, then Refresh to confirm.";
        }
        catch (Exception ex) { Fail("Detect by movement", ex); MovementStatus = string.Empty; }
        finally { _busy = false; MovementDetecting = false; }
    }

    internal async Task UseCandidateAsync(PlayerCandidateRowViewModel row)
    {
        // Feed the resolved Transform through the normal Set Target path —
        // Component.get_transform on a Transform returns itself, so this
        // revalidates the pointer for free.
        TargetAddressInput = row.TransformAddress;
        TargetIsGameObject = false;
        await SetTargetAsync().ConfigureAwait(true);
    }

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
            MovementDriver driver = _controller.Driver;
            if (driver.IsCharacterController)
            {
                DriverText = "Driver: CharacterController (teleport disables it during the write)";
                StatusMessage = "Target resolved — CharacterController-driven, so teleports "
                    + "disable the controller during the write (retargeted to its own transform).";
            }
            else
            {
                DriverText = "Driver: Transform (direct write)";
                StatusMessage = "Target resolved (plain Transform write).";
            }
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
    private Task UndoAsync() => RecallLastAsync();

    internal Task RecallLastAsync() =>
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
        DriverText = string.Empty;
    }

    private void Fail(string label, Exception ex)
    {
        StatusMessage = $"{label} error: {ex.Message}";
        _logger.Write("Teleport", $"{label}: {ex}");
    }

    // ------------------------------------------------------------------
    // Global hotkeys (fire while the game window is focused)
    // ------------------------------------------------------------------

    // Numpad virtual-key codes (NumLock must be ON for these to fire).
    private const uint VK_NUMPAD0 = 0x60;
    private const uint VK_NUMPAD1 = 0x61;
    private const uint VK_NUMPAD2 = 0x62;
    private const uint VK_NUMPAD3 = 0x63;

    partial void OnGlobalHotkeysEnabledChanged(bool value)
    {
        if (value) RegisterHotkeys();
        else { UnregisterHotkeys(); HotkeyStatus = "Off."; }
    }

    private void RegisterHotkeys()
    {
        UnregisterHotkeys();
        if (HotkeyService is not { } svc)
        {
            HotkeyStatus = "Global hotkeys are unavailable on this platform.";
            return;
        }

        // Fixed default scheme (numpad — dodges the Ctrl+1..3 skill binds most
        // ARPGs use). Per-action rebinding is a v2 idea. Hotkey callbacks fire
        // on a background thread, so marshal to the UI thread before touching VM
        // state; the actions self-guard on connection / target.
        var bindings = new (uint Mod, uint Vk, string Label, Func<Task> Action)[]
        {
            (HotkeyModifiers.Control, VK_NUMPAD1, "Ctrl+Num1", () => SaveMarkerAsync(0)),
            (HotkeyModifiers.Control, VK_NUMPAD2, "Ctrl+Num2", () => SaveMarkerAsync(1)),
            (HotkeyModifiers.Control, VK_NUMPAD3, "Ctrl+Num3", () => SaveMarkerAsync(2)),
            (HotkeyModifiers.Alt,     VK_NUMPAD1, "Alt+Num1",  () => RecallMarkerAsync(0)),
            (HotkeyModifiers.Alt,     VK_NUMPAD2, "Alt+Num2",  () => RecallMarkerAsync(1)),
            (HotkeyModifiers.Alt,     VK_NUMPAD3, "Alt+Num3",  () => RecallMarkerAsync(2)),
            (HotkeyModifiers.Control, VK_NUMPAD0, "Ctrl+Num0", () => RecallLastAsync()),
        };

        var failed = new List<string>();
        foreach (var b in bindings)
        {
            Func<Task> action = b.Action;
            IGlobalHotkeyRegistration? reg = svc.RegisterSpecific(
                b.Mod, b.Vk, b.Label, () => Dispatcher.UIThread.Post(() => _ = action()));
            if (reg is not null) _hotkeyRegs.Add(reg);
            else                 failed.Add(b.Label);
        }

        if (_hotkeyRegs.Count == 0)
        {
            HotkeyStatus = "No hotkeys could be registered (all combos are in use elsewhere).";
            return;
        }
        string status = "Save 1-3 = Ctrl+Num1/2/3 · Recall 1-3 = Alt+Num1/2/3 · Undo = Ctrl+Num0. NumLock must be ON.";
        if (failed.Count > 0)
            status += $"  Unavailable (in use elsewhere): {string.Join(", ", failed)}.";
        HotkeyStatus = status;
    }

    private void UnregisterHotkeys()
    {
        foreach (IGlobalHotkeyRegistration r in _hotkeyRegs)
        {
            try { r.Dispose(); } catch { /* best-effort unregister */ }
        }
        _hotkeyRegs.Clear();
    }
}

/// <summary>One auto-detect candidate row (immutable display strings + a Use
/// button that delegates back to the parent <see cref="TeleportViewModel"/>).</summary>
public sealed partial class PlayerCandidateRowViewModel : ObservableObject
{
    private readonly TeleportViewModel _parent;

    public PlayerCandidateRowViewModel(PlayerCandidate candidate, TeleportViewModel parent)
    {
        _parent          = parent;
        TransformAddress = candidate.TransformAddress;
        Score            = candidate.Score.ToString(CultureInfo.InvariantCulture);
        Name             = string.IsNullOrEmpty(candidate.Name) ? "<no name>" : candidate.Name;
        ClassName        = candidate.ClassName ?? string.Empty;
        Confidence       = candidate.Confidence switch
        {
            PlayerConfidence.High   => "High",
            PlayerConfidence.Medium => "Medium",
            _                       => "Low",
        };
        // Movement runs report displacement; static runs report camera distance.
        Distance = candidate.MovedDistance is { } moved
            ? "moved " + moved.ToString("0.0", CultureInfo.InvariantCulture) + " m"
            : candidate.DistanceToCamera is { } d
                ? d.ToString("0.0", CultureInfo.InvariantCulture) + " m"
                : "—";
        Sources = string.Join(" + ", candidate.Sources);
    }

    public string TransformAddress { get; }
    public string Score            { get; }
    public string Name             { get; }
    public string ClassName        { get; }
    public string Confidence       { get; }
    public string Distance         { get; }
    public string Sources          { get; }

    [RelayCommand] private Task Use() => _parent.UseCandidateAsync(this);
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
