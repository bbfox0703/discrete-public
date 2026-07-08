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
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class TeleportViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private readonly TeleportController _controller;
    private bool _busy;
    private TeleportPose? _lastPose;

    // Active registrations + persisted bindings, keyed by action id ("save0",
    // "recall2", "undo", "forward", "back").
    private readonly Dictionary<string, IGlobalHotkeyRegistration> _hotkeyRegs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeleportHotkeyBinding> _bindings       = new(StringComparer.Ordinal);
    private TeleportHotkeyRow? _capturingRow;

    public TeleportViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend    = backend;
        _logger     = logger;
        _controller = new TeleportController(new TeleportService(backend, logger: logger));

        var rows = new List<TeleportMarkerRowViewModel>(TeleportMarkerStore.SlotCount);
        for (int i = 0; i < TeleportMarkerStore.SlotCount; i++)
            rows.Add(new TeleportMarkerRowViewModel(i, this));
        MarkerRows = rows;

        BuildHotkeyRows();
        SeedDefaultBindings();
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

    /// <summary>Per-action rebindable hotkey rows (Save/Recall/Undo/Forward/Back).</summary>
    public ObservableCollection<TeleportHotkeyRow> HotkeyRows { get; } = new();

    /// <summary>Binding persistence, injected by MainWindow. Null in tests /
    /// before wiring → default bindings live only in memory.</summary>
    public TeleportHotkeyStore? HotkeyStore { get; set; }

    /// <summary>True while a row is waiting for a key combo — the view's tunnel
    /// KeyDown handler routes the next press to <see cref="ApplyCapturedKey"/>.</summary>
    public bool IsCapturingHotkey => _capturingRow is not null;

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
                : "Top row is the resolved character root (Use it, then Refresh). If the character "
                  + "teleports oddly — camera doesn't follow, moves too fast — Use the next row down "
                  + "(the model root) or its parent instead; games nest several controllers.";
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
            _logger.Write("Teleport", $"SetTarget driver={driver.Label}: {_controller.LastDriverNote ?? "(none)"}");
            if (driver.IsCharacterController)
            {
                DriverText = "Driver: CharacterController (teleport disables it during the write)";
                StatusMessage = "Target resolved — CharacterController-driven, so teleports "
                    + "disable the controller during the write (retargeted to its own transform).";
            }
            else
            {
                DriverText = "Driver: Transform (direct write)";
                StatusMessage = "Target resolved (plain Transform write). "
                    + "If the character orbits / won't move, it may be physics-driven "
                    + "(CharacterController not found on it or its ancestors).";
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
    // Global hotkeys (fire while the game window is focused) — rebindable
    // ------------------------------------------------------------------

    // Bindable actions. Ids are digit-suffixed for markers ("save0".."save2",
    // "recall0".."recall2") plus "undo" / "forward" / "back".
    private static readonly (string Id, string Name, string Hint)[] HotkeyActionDefs =
    {
        ("save0",   "Save marker 1",    "Store the current pose in slot 1."),
        ("save1",   "Save marker 2",    "Store the current pose in slot 2."),
        ("save2",   "Save marker 3",    "Store the current pose in slot 3."),
        ("recall0", "Recall marker 1",  "Teleport to slot 1 (auto-saves undo)."),
        ("recall1", "Recall marker 2",  "Teleport to slot 2 (auto-saves undo)."),
        ("recall2", "Recall marker 3",  "Teleport to slot 3 (auto-saves undo)."),
        ("undo",    "Undo last jump",   "Return to the pre-jump pose."),
        ("forward", "Teleport forward", "Move along facing by the directional distance."),
        ("back",    "Teleport back",    "Move opposite facing by the directional distance."),
    };

    private void BuildHotkeyRows()
    {
        foreach ((string id, string name, string hint) in HotkeyActionDefs)
            HotkeyRows.Add(new TeleportHotkeyRow(this, id, name, hint));
    }

    // First-run defaults: the pre-rebinding numpad scheme (Ctrl/Alt+Num, which
    // dodges the Ctrl+1..3 skill binds most ARPGs use). Forward/back start
    // unbound. A persisted config overrides these via LoadHotkeys().
    private void SeedDefaultBindings()
    {
        Bind("save0",   HotkeyModifiers.Control, 0x61);   // Ctrl+Num1
        Bind("save1",   HotkeyModifiers.Control, 0x62);   // Ctrl+Num2
        Bind("save2",   HotkeyModifiers.Control, 0x63);   // Ctrl+Num3
        Bind("recall0", HotkeyModifiers.Alt,     0x61);   // Alt+Num1
        Bind("recall1", HotkeyModifiers.Alt,     0x62);   // Alt+Num2
        Bind("recall2", HotkeyModifiers.Alt,     0x63);   // Alt+Num3
        Bind("undo",    HotkeyModifiers.Control, 0x60);   // Ctrl+Num0

        void Bind(string id, uint mod, uint vk)
        {
            var b = new TeleportHotkeyBinding(mod, vk);
            _bindings[id] = b;
            SetRowLabel(id, b.Label);
        }
    }

    /// <summary>Load persisted bindings (called by MainWindow once the store is
    /// injected). An existing file — even an empty one — is respected verbatim
    /// (the user may have cleared everything); only a MISSING file keeps the
    /// seeded defaults and writes them out.</summary>
    public void LoadHotkeys()
    {
        if (HotkeyStore is not { } store) return;
        if (store.Exists)
        {
            _bindings.Clear();
            foreach (KeyValuePair<string, TeleportHotkeyBinding> kv in store.Load())
                if (IsKnownAction(kv.Key)) _bindings[kv.Key] = kv.Value;
            foreach (TeleportHotkeyRow row in HotkeyRows)
                row.Label = _bindings.TryGetValue(row.ActionId, out TeleportHotkeyBinding b) ? b.Label : string.Empty;
        }
        else
        {
            store.Save(_bindings);   // persist the first-run defaults
        }
        if (GlobalHotkeysEnabled) RegisterAllHotkeys();
    }

    private static bool IsKnownAction(string id)
    {
        foreach ((string aid, _, _) in HotkeyActionDefs)
            if (aid == id) return true;
        return false;
    }

    private void SetRowLabel(string actionId, string label)
    {
        foreach (TeleportHotkeyRow row in HotkeyRows)
            if (row.ActionId == actionId) { row.Label = label; return; }
    }

    partial void OnGlobalHotkeysEnabledChanged(bool value)
    {
        if (value) { RegisterAllHotkeys(); return; }
        UnregisterAllHotkeys();
        // No registrations left → no combo can be "in use", so drop any stale ⚠
        // badges (they'd otherwise imply a broken binding while the feature is off).
        foreach (TeleportHotkeyRow row in HotkeyRows) row.Conflicted = false;
        HotkeyStatus = "Off.";
    }

    // Register every bound row. Hotkey callbacks fire on a BACKGROUND thread, so
    // marshal to the UI thread before touching VM state; the actions self-guard
    // on connection / target.
    private void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        if (HotkeyService is null)
        {
            HotkeyStatus = "Global hotkeys are unavailable on this platform.";
            return;
        }

        var conflicts = new List<string>();
        foreach (TeleportHotkeyRow row in HotkeyRows)
        {
            if (!_bindings.TryGetValue(row.ActionId, out TeleportHotkeyBinding b)) continue;
            bool ok = TryRegister(row.ActionId, b);
            row.Conflicted = !ok;
            if (!ok) conflicts.Add(b.Label);
        }
        UpdateHotkeyStatus(conflicts);
    }

    private void UnregisterAllHotkeys()
    {
        foreach (IGlobalHotkeyRegistration r in _hotkeyRegs.Values)
        {
            try { r.Dispose(); } catch { /* best-effort unregister */ }
        }
        _hotkeyRegs.Clear();
    }

    // Register one action's hotkey. The prior registration for the same action
    // is kept UNTIL the new one succeeds, so a rebind that hits an OS-taken combo
    // doesn't strand the working binding. False = combo already claimed elsewhere
    // / no service (in which case any stale registration IS dropped).
    private bool TryRegister(string actionId, TeleportHotkeyBinding b)
    {
        if (HotkeyService is not { } svc)
        {
            if (_hotkeyRegs.Remove(actionId, out IGlobalHotkeyRegistration? stale))
                try { stale.Dispose(); } catch { /* best-effort */ }
            return false;
        }
        IGlobalHotkeyRegistration? reg = svc.RegisterSpecific(
            b.WinMods, b.Vk, b.Label, () => Dispatcher.UIThread.Post(() => OnHotkeyPressed(actionId)));
        if (reg is null) return false;   // combo taken → leave the old binding live
        if (_hotkeyRegs.Remove(actionId, out IGlobalHotkeyRegistration? old))
            try { old.Dispose(); } catch { /* best-effort */ }
        _hotkeyRegs[actionId] = reg;
        return true;
    }

    private void UpdateHotkeyStatus(IReadOnlyList<string> conflicts)
    {
        string status = $"On — {_hotkeyRegs.Count} of {_bindings.Count} bound hotkey(s) active.";
        if (conflicts.Count > 0)
            status += $" In use elsewhere: {string.Join(", ", conflicts)}.";
        if (AnyNumpadBinding())
            status += " (Numpad combos need NumLock ON.)";
        HotkeyStatus = status;
    }

    private bool AnyNumpadBinding()
    {
        foreach (TeleportHotkeyBinding b in _bindings.Values)
            if (b.Vk >= 0x60 && b.Vk <= 0x69) return true;
        return false;
    }

    // --- rebinding (driven by the view's tunnel KeyDown capture) ---

    /// <summary>Begin (or cancel) capturing a key combo for a row. The view's
    /// tunnel KeyDown handler routes the next press to <see cref="ApplyCapturedKey"/>.
    /// Called by the row's Set button.</summary>
    internal void BeginCaptureHotkey(TeleportHotkeyRow row)
    {
        if (ReferenceEquals(_capturingRow, row)) { CancelCapture(); HotkeyStatus = "Rebind cancelled."; return; }
        if (_capturingRow is not null) _capturingRow.IsCapturing = false;
        _capturingRow = row;
        row.IsCapturing = true;
        OnPropertyChanged(nameof(IsCapturingHotkey));
        HotkeyStatus = $"Press a key or combo for '{row.DisplayName}' (Esc to cancel)…";
    }

    /// <summary>Remove a row's binding (unregister + persist). Called by the
    /// row's Clear button.</summary>
    internal void ClearHotkey(TeleportHotkeyRow row)
    {
        if (ReferenceEquals(_capturingRow, row)) CancelCapture();
        if (_hotkeyRegs.Remove(row.ActionId, out IGlobalHotkeyRegistration? reg))
        {
            try { reg.Dispose(); } catch { /* best-effort */ }
        }
        _bindings.Remove(row.ActionId);
        row.Label = string.Empty;
        row.Conflicted = false;
        HotkeyStore?.Save(_bindings);
        if (GlobalHotkeysEnabled) UpdateHotkeyStatus(Array.Empty<string>());
        else HotkeyStatus = $"Cleared {row.DisplayName}.";
    }

    private void CancelCapture()
    {
        if (_capturingRow is not null) _capturingRow.IsCapturing = false;
        _capturingRow = null;
        OnPropertyChanged(nameof(IsCapturingHotkey));
    }

    /// <summary>
    /// Handle a key press captured while a row is in capture mode. Returns true
    /// when the press was consumed (bound, cancelled, or a rejected combo),
    /// false when not capturing (let the event pass through). A modifier-only
    /// press keeps capturing (returns true, no bind) so "hold Ctrl then press F7"
    /// works.
    /// </summary>
    public bool ApplyCapturedKey(Key key, KeyModifiers mods)
    {
        if (_capturingRow is not { } row) return false;
        if (key == Key.Escape) { CancelCapture(); HotkeyStatus = "Rebind cancelled."; return true; }

        // Modifier-only press → keep listening (don't end capture).
        if (!HotkeyKeyMap.TryConvert(key, mods, out uint winMods, out uint vk, out string label))
            return true;

        // A bare (modifier-less) TYPING key would be claimed OS-wide and stop the
        // user typing that character anywhere — keep listening for a safer combo.
        // Bare F-keys / navigation keys are exempt (games rarely use them and they
        // don't block typing).
        if (winMods == 0 && RequiresModifier(vk))
        {
            HotkeyStatus = $"'{label}' needs a modifier (Ctrl/Alt/Shift) — or use an F-key. Still listening…";
            return true;
        }

        var binding = new TeleportHotkeyBinding(winMods, vk);
        _capturingRow = null;
        row.IsCapturing = false;
        OnPropertyChanged(nameof(IsCapturingHotkey));

        // Re-binding to the combo this action ALREADY holds is a no-op — skip
        // re-registration (RegisterHotKey would reject our own still-live
        // registration as a duplicate and falsely cry "conflict").
        if (_bindings.TryGetValue(row.ActionId, out TeleportHotkeyBinding cur) && cur == binding)
        {
            row.Label = label;
            HotkeyStatus = $"{row.DisplayName} is already {label}.";
            return true;
        }

        // If hotkeys are live, claim the combo NOW so a conflict is caught
        // immediately.
        if (GlobalHotkeysEnabled && HotkeyService is not null && !TryRegister(row.ActionId, binding))
        {
            // The attempted combo is taken (another app, or another teleport
            // action). The OLD binding is untouched and still live, so leave the
            // row showing it — do NOT flag the working binding as broken.
            row.Conflicted = false;
            HotkeyStatus = $"'{label}' is already in use (another app, or another teleport action) — pick a different combo.";
            return true;
        }

        _bindings[row.ActionId] = binding;
        row.Label = label;
        row.Conflicted = false;
        HotkeyStore?.Save(_bindings);
        HotkeyStatus = GlobalHotkeysEnabled
            ? $"Bound {row.DisplayName} → {label}."
            : $"Bound {row.DisplayName} → {label} (enable hotkeys to activate).";
        return true;
    }

    // True for a "typing" virtual-key whose modifier-less global registration
    // would swallow that character system-wide (letters, digits, numpad digits /
    // operators, space, the -/= OEM keys). F-keys and navigation keys are safe bare.
    private static bool RequiresModifier(uint vk) =>
        (vk >= 0x30 && vk <= 0x39) ||   // 0-9
        (vk >= 0x41 && vk <= 0x5A) ||   // A-Z
        (vk >= 0x60 && vk <= 0x69) ||   // Num0-9
        (vk >= 0x6A && vk <= 0x6F) ||   // Num * + - . /
        vk == 0x20 || vk == 0xBB || vk == 0xBD;   // Space, =, -

    /// <summary>Cancel any in-progress hotkey capture. The view calls this on
    /// window-deactivate / click-away so an armed capture can't hijack an
    /// unrelated keystroke (e.g. a movement key pressed after alt-tabbing to the
    /// game).</summary>
    public void CancelActiveCapture()
    {
        if (_capturingRow is null) return;
        CancelCapture();
        HotkeyStatus = "Rebind cancelled.";
    }

    // Dispatch a fired hotkey to its teleport action (already on the UI thread).
    // Each action self-guards on connection / target via RunActionAsync.
    private void OnHotkeyPressed(string actionId)
    {
        switch (actionId)
        {
            case "save0":   _ = SaveMarkerAsync(0);      break;
            case "save1":   _ = SaveMarkerAsync(1);      break;
            case "save2":   _ = SaveMarkerAsync(2);      break;
            case "recall0": _ = RecallMarkerAsync(0);    break;
            case "recall1": _ = RecallMarkerAsync(1);    break;
            case "recall2": _ = RecallMarkerAsync(2);    break;
            case "undo":    _ = RecallLastAsync();       break;
            case "forward": _ = TeleportForwardAsync();  break;
            case "back":    _ = TeleportBackwardAsync(); break;
        }
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

/// <summary>One rebindable teleport-hotkey row: the action name, its current
/// combo, and capture/conflict state. Set / Clear delegate back to the parent
/// <see cref="TeleportViewModel"/> (same pattern as the marker rows).</summary>
public sealed partial class TeleportHotkeyRow : ObservableObject
{
    private readonly TeleportViewModel _parent;

    public TeleportHotkeyRow(TeleportViewModel parent, string actionId, string displayName, string hint)
    {
        _parent     = parent;
        ActionId    = actionId;
        DisplayName = displayName;
        Hint        = hint;
    }

    public string ActionId    { get; }
    public string DisplayName { get; }
    public string Hint        { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel), nameof(HasBinding))]
    private string _label = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel), nameof(CaptureButtonText))]
    private bool _isCapturing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private bool _conflicted;

    public bool HasBinding => !string.IsNullOrEmpty(Label);

    /// <summary>What the combo cell shows: the prompt while capturing, the combo
    /// (with a ⚠ when it couldn't be claimed), or an em dash when unbound.</summary>
    public string DisplayLabel =>
        IsCapturing ? "Press keys…"
        : HasBinding ? (Conflicted ? $"{Label} ⚠" : Label)
        : "—";

    /// <summary>Capture-button text — flips to "Cancel" mid-capture.</summary>
    public string CaptureButtonText => IsCapturing ? "Cancel" : "Set";

    [RelayCommand] private void BeginCapture() => _parent.BeginCaptureHotkey(this);
    [RelayCommand] private void Clear()        => _parent.ClearHotkey(this);
}
