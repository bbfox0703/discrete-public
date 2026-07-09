// TeleportHotkeyTests.cs — Task 7 + rebinding (wave 2). Verifies the
// TeleportViewModel's global-hotkey wiring against a fake IGlobalHotkeyService
// (the Win32 message loop itself needs live verification), plus the rebinding
// flow, the Avalonia Key→Win32 map, and binding persistence. Registration is
// synchronous — the dispatcher only appears inside the key-press callback — so
// toggling GlobalHotkeysEnabled / calling ApplyCapturedKey drives it
// deterministically.

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class TeleportHotkeyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;
    private readonly BackendAdapter _backend;

    public TeleportHotkeyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TpHotkeyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger  = new FileLogger(_tempDir);
        _backend = new BackendAdapter(new PipeClient("Discrete_TpHotkey_" + Guid.NewGuid().ToString("N")));
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private TeleportViewModel NewVm() => new(_backend, _logger);

    private sealed class FakeHotkeyService : IGlobalHotkeyService
    {
        public readonly List<(uint Mod, uint Vk, string Label)> Registered = new();
        public readonly HashSet<string> Unavailable = new();
        public int DisposeCount;

        public IGlobalHotkeyRegistration? RegisterSpecific(uint modifiers, uint vk, string label, Action onPressed)
        {
            if (Unavailable.Contains(label)) return null;
            Registered.Add((modifiers, vk, label));
            return new Reg(label, () => DisposeCount++);
        }

        private sealed class Reg : IGlobalHotkeyRegistration
        {
            private readonly Action _dispose;
            public Reg(string label, Action dispose) { Label = label; _dispose = dispose; }
            public string Label { get; }
            public void Dispose() => _dispose();
        }
    }

    // ── master enable / disable ──────────────────────────────────────────────

    [Fact]
    public void Enable_RegistersSevenDefaultCombos()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;

        vm.GlobalHotkeysEnabled = true;

        // The 7 seeded defaults (Save/Recall 1-3 + Undo); Forward/Back start unbound.
        Assert.Equal(7, fake.Registered.Count);
        List<string> labels = fake.Registered.ConvertAll(r => r.Label);
        Assert.Contains("Ctrl+Num1", labels);
        Assert.Contains("Ctrl+Num3", labels);
        Assert.Contains("Alt+Num1", labels);
        Assert.Contains("Alt+Num3", labels);
        Assert.Contains("Ctrl+Num0", labels);   // undo
        Assert.Contains("NumLock", vm.HotkeyStatus);
    }

    [Fact]
    public void Disable_UnregistersAll()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;

        vm.GlobalHotkeysEnabled = true;
        vm.GlobalHotkeysEnabled = false;

        Assert.Equal(7, fake.DisposeCount);
        Assert.Equal("Off.", vm.HotkeyStatus);
    }

    [Fact]
    public void UnavailableCombo_IsReported_RestStillRegister()
    {
        var fake = new FakeHotkeyService();
        fake.Unavailable.Add("Alt+Num2");
        var vm = NewVm();
        vm.HotkeyService = fake;

        vm.GlobalHotkeysEnabled = true;

        Assert.Equal(6, fake.Registered.Count);
        Assert.Contains("Alt+Num2", vm.HotkeyStatus);
        Assert.Contains("In use elsewhere", vm.HotkeyStatus);
        // The conflicted row is flagged for the ⚠ badge.
        TeleportHotkeyRow recall1 = FindRow(vm, "recall1");
        Assert.True(recall1.Conflicted);
    }

    [Fact]
    public void NoService_ReportsUnavailable()
    {
        var vm = NewVm();
        vm.HotkeyService = null;

        vm.GlobalHotkeysEnabled = true;

        Assert.Contains("unavailable", vm.HotkeyStatus);
    }

    // ── rebinding ────────────────────────────────────────────────────────────

    [Fact]
    public void Rebind_WhileDisabled_UpdatesLabelWithoutRegistering()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;      // present but hotkeys are OFF

        TeleportHotkeyRow save1 = FindRow(vm, "save0");
        save1.BeginCaptureCommand.Execute(null);
        Assert.True(vm.IsCapturingHotkey);

        bool consumed = vm.ApplyCapturedKey(Key.F5, KeyModifiers.Control);

        Assert.True(consumed);
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("Ctrl+F5", save1.Label);
        Assert.Empty(fake.Registered);   // nothing registered while disabled
    }

    [Fact]
    public void Rebind_WhileEnabled_RegistersNewComboImmediately()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;
        vm.GlobalHotkeysEnabled = true;      // 7 registered

        TeleportHotkeyRow forward = FindRow(vm, "forward");   // starts unbound
        forward.BeginCaptureCommand.Execute(null);
        bool consumed = vm.ApplyCapturedKey(Key.F6, KeyModifiers.Control);

        Assert.True(consumed);
        Assert.Equal("Ctrl+F6", forward.Label);
        Assert.Contains(fake.Registered, r => r.Label == "Ctrl+F6" && r.Vk == 0x75);
    }

    [Fact]
    public void ApplyCapturedKey_ModifierOnly_KeepsCapturing()
    {
        var vm = NewVm();
        FindRow(vm, "save0").BeginCaptureCommand.Execute(null);

        bool consumed = vm.ApplyCapturedKey(Key.LeftCtrl, KeyModifiers.Control);

        Assert.True(consumed);            // swallowed so it doesn't leak to the UI
        Assert.True(vm.IsCapturingHotkey);   // but capture continues
    }

    [Fact]
    public void ApplyCapturedKey_Escape_CancelsCapture()
    {
        var vm = NewVm();
        TeleportHotkeyRow row = FindRow(vm, "save0");
        string before = row.Label;
        row.BeginCaptureCommand.Execute(null);

        vm.ApplyCapturedKey(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal(before, row.Label);     // unchanged
    }

    [Fact]
    public void ApplyCapturedKey_NotCapturing_ReturnsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.ApplyCapturedKey(Key.F5, KeyModifiers.Control));
    }

    [Fact]
    public void ClearHotkey_RemovesBindingAndUnregisters()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;
        vm.GlobalHotkeysEnabled = true;

        TeleportHotkeyRow save1 = FindRow(vm, "save0");
        Assert.True(save1.HasBinding);
        int disposedBefore = fake.DisposeCount;

        save1.ClearCommand.Execute(null);

        Assert.False(save1.HasBinding);
        Assert.Equal(string.Empty, save1.Label);
        Assert.Equal(disposedBefore + 1, fake.DisposeCount);   // its registration disposed
    }

    [Fact]
    public void Rebind_ConflictKeepsOldBindingWithoutFlaggingIt()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;
        vm.GlobalHotkeysEnabled = true;

        fake.Unavailable.Add("Ctrl+F9");            // OS says this combo is taken
        TeleportHotkeyRow save1 = FindRow(vm, "save0");
        string old = save1.Label;                   // "Ctrl+Num1"
        save1.BeginCaptureCommand.Execute(null);

        vm.ApplyCapturedKey(Key.F9, KeyModifiers.Control);

        Assert.Equal(old, save1.Label);             // rejected → keep the old combo
        Assert.False(save1.Conflicted);             // the OLD binding still works → no ⚠
        Assert.Contains("already in use", vm.HotkeyStatus);
    }

    [Fact]
    public void Rebind_SameComboAsCurrent_IsNoOpNotConflict()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;
        vm.GlobalHotkeysEnabled = true;             // save0 registered as Ctrl+Num1

        TeleportHotkeyRow save0 = FindRow(vm, "save0");
        save0.BeginCaptureCommand.Execute(null);
        vm.ApplyCapturedKey(Key.NumPad1, KeyModifiers.Control);   // the combo it already holds

        Assert.Equal("Ctrl+Num1", save0.Label);
        Assert.False(save0.Conflicted);
        Assert.DoesNotContain("in use", vm.HotkeyStatus);   // NOT a false conflict
    }

    [Fact]
    public void Disable_ClearsStaleConflictedBadge()
    {
        var fake = new FakeHotkeyService();
        fake.Unavailable.Add("Alt+Num2");
        var vm = NewVm();
        vm.HotkeyService = fake;

        vm.GlobalHotkeysEnabled = true;
        Assert.True(FindRow(vm, "recall1").Conflicted);   // flagged while enabled

        vm.GlobalHotkeysEnabled = false;
        Assert.False(FindRow(vm, "recall1").Conflicted);  // cleared on disable
    }

    [Fact]
    public void ApplyCapturedKey_BareTypingKey_KeepsListening()
    {
        var vm = NewVm();
        TeleportHotkeyRow row = FindRow(vm, "save0");
        string before = row.Label;
        row.BeginCaptureCommand.Execute(null);

        bool consumed = vm.ApplyCapturedKey(Key.G, KeyModifiers.None);   // bare letter

        Assert.True(consumed);
        Assert.True(vm.IsCapturingHotkey);        // rejected → still listening
        Assert.Equal(before, row.Label);          // not bound
        Assert.Contains("modifier", vm.HotkeyStatus);
    }

    [Fact]
    public void ApplyCapturedKey_BareFKey_IsAccepted()
    {
        var vm = NewVm();
        TeleportHotkeyRow row = FindRow(vm, "forward");
        row.BeginCaptureCommand.Execute(null);

        bool consumed = vm.ApplyCapturedKey(Key.F8, KeyModifiers.None);   // bare F-key is safe

        Assert.True(consumed);
        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal("F8", row.Label);
    }

    [Fact]
    public void CancelActiveCapture_StopsCapturing()
    {
        var vm = NewVm();
        FindRow(vm, "save0").BeginCaptureCommand.Execute(null);
        Assert.True(vm.IsCapturingHotkey);

        vm.CancelActiveCapture();

        Assert.False(vm.IsCapturingHotkey);
    }

    // ── persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void LoadHotkeys_MissingFile_SeedsAndPersistsDefaults()
    {
        string path = Path.Combine(_tempDir, "missing-hk.txt");
        var vm = NewVm();
        vm.HotkeyStore = new TeleportHotkeyStore(path);

        vm.LoadHotkeys();

        Assert.True(File.Exists(path));                 // defaults written out
        Assert.True(FindRow(vm, "save0").HasBinding);   // default present
    }

    [Fact]
    public void LoadHotkeys_ExistingEmptyFile_RespectsClearedConfig()
    {
        string path = Path.Combine(_tempDir, "empty-hk.txt");
        File.WriteAllText(path, "# user cleared everything\n");
        var vm = NewVm();
        vm.HotkeyStore = new TeleportHotkeyStore(path);

        vm.LoadHotkeys();

        // An existing file (even with no bindings) is respected — no defaults.
        Assert.False(FindRow(vm, "save0").HasBinding);
    }

    [Fact]
    public void Rebind_PersistsAcrossReload()
    {
        string path = Path.Combine(_tempDir, "roundtrip-hk.txt");
        var vm = NewVm();
        vm.HotkeyStore = new TeleportHotkeyStore(path);
        vm.LoadHotkeys();

        FindRow(vm, "forward").BeginCaptureCommand.Execute(null);
        vm.ApplyCapturedKey(Key.G, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("Ctrl+Shift+G", FindRow(vm, "forward").Label);

        // A fresh VM pointed at the same file reloads the bound combo.
        var vm2 = NewVm();
        vm2.HotkeyStore = new TeleportHotkeyStore(path);
        vm2.LoadHotkeys();
        Assert.Equal("Ctrl+Shift+G", FindRow(vm2, "forward").Label);
    }

    private static TeleportHotkeyRow FindRow(TeleportViewModel vm, string actionId)
    {
        foreach (TeleportHotkeyRow r in vm.HotkeyRows)
            if (r.ActionId == actionId) return r;
        throw new InvalidOperationException($"row {actionId} not found");
    }
}
