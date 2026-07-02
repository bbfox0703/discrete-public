// TeleportHotkeyTests.cs — Task 7. Verifies the TeleportViewModel's global
// hotkey registration wiring against a fake IGlobalHotkeyService (the Win32
// message loop itself needs live verification). Registration is synchronous —
// the dispatcher only appears inside the key-press callback — so toggling
// GlobalHotkeysEnabled drives it deterministically.

using System;
using System.Collections.Generic;
using System.IO;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
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

    [Fact]
    public void Enable_RegistersSevenNumpadCombos()
    {
        var fake = new FakeHotkeyService();
        var vm = NewVm();
        vm.HotkeyService = fake;

        vm.GlobalHotkeysEnabled = true;

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
        Assert.Contains("Unavailable", vm.HotkeyStatus);
    }

    [Fact]
    public void NoService_ReportsUnavailable()
    {
        var vm = NewVm();
        vm.HotkeyService = null;

        vm.GlobalHotkeysEnabled = true;

        Assert.Contains("unavailable", vm.HotkeyStatus);
    }
}
