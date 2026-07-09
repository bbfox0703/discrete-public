// HotkeyKeyMapTests.cs — pure coverage for the Avalonia Key→Win32 map and the
// teleport-hotkey persistence store (both AOT-safe, no IO in the map).

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Input;
using UnityDumpUI.Core.Services;   // HotkeyModifiers
using UnityDumpUI.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class HotkeyKeyMapTests
{
    [Theory]
    [InlineData(Key.F1,  0x70u)]
    [InlineData(Key.F5,  0x74u)]
    [InlineData(Key.F12, 0x7Bu)]
    [InlineData(Key.A,   0x41u)]
    [InlineData(Key.Z,   0x5Au)]
    [InlineData(Key.D0,  0x30u)]
    [InlineData(Key.D9,  0x39u)]
    [InlineData(Key.NumPad0, 0x60u)]
    [InlineData(Key.NumPad3, 0x63u)]
    [InlineData(Key.Left,  0x25u)]
    [InlineData(Key.Delete, 0x2Eu)]
    [InlineData(Key.Space, 0x20u)]
    public void TryVirtualKey_MapsBindableKeys(Key key, uint expectedVk)
    {
        Assert.True(HotkeyKeyMap.TryVirtualKey(key, out uint vk));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Enter)]
    public void TryVirtualKey_RejectsModifierOnlyAndUnsupported(Key key) =>
        Assert.False(HotkeyKeyMap.TryVirtualKey(key, out _));

    [Fact]
    public void TryConvert_BuildsMaskAndLabel()
    {
        Assert.True(HotkeyKeyMap.TryConvert(Key.F5, KeyModifiers.Control, out uint mods, out uint vk, out string label));
        Assert.Equal(HotkeyModifiers.Control, mods);
        Assert.Equal(0x74u, vk);
        Assert.Equal("Ctrl+F5", label);
    }

    [Fact]
    public void TryConvert_CombinesModifiersInStableOrder()
    {
        Assert.True(HotkeyKeyMap.TryConvert(
            Key.G, KeyModifiers.Shift | KeyModifiers.Control, out uint mods, out _, out string label));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, mods);
        Assert.Equal("Ctrl+Shift+G", label);   // Ctrl before Shift regardless of press order
    }

    [Fact]
    public void TryConvert_ModifierOnly_ReturnsFalse() =>
        Assert.False(HotkeyKeyMap.TryConvert(Key.LeftAlt, KeyModifiers.Alt, out _, out _, out _));

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x61u, "Ctrl+Num1")]
    [InlineData(HotkeyModifiers.Alt, 0x71u, "Alt+F2")]
    [InlineData(0u, 0x41u, "A")]
    [InlineData(HotkeyModifiers.Win, 0x27u, "Win+Right")]
    public void LabelFor_Renders(uint mods, uint vk, string expected) =>
        Assert.Equal(expected, HotkeyKeyMap.LabelFor(mods, vk));
}

public class TeleportHotkeyStoreTests : IDisposable
{
    private readonly string _dir;

    public TeleportHotkeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TpHotkeyStore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        string path = Path.Combine(_dir, "hk.txt");
        var store = new TeleportHotkeyStore(path);
        var bindings = new Dictionary<string, TeleportHotkeyBinding>
        {
            ["save0"]   = new(HotkeyModifiers.Control, 0x74),   // Ctrl+F5
            ["forward"] = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x47),  // Alt+Shift+G
        };

        store.Save(bindings);
        Assert.True(store.Exists);
        Dictionary<string, TeleportHotkeyBinding> loaded = store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x74u, loaded["save0"].Vk);
        Assert.Equal(HotkeyModifiers.Control, loaded["save0"].WinMods);
        Assert.Equal("Alt+Shift+G", loaded["forward"].Label);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var store = new TeleportHotkeyStore(Path.Combine(_dir, "nope.txt"));
        Assert.False(store.Exists);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Parse_SkipsMalformedLinesAndZeroVk()
    {
        string[] lines =
        {
            "# comment",
            "",
            "save0=2,116",           // valid → Ctrl (2) + 0x74
            "garbage-no-equals",
            "recall0=notanumber,5",  // bad mods
            "undo=2",                // missing vk field
            "back=2,0",              // zero vk rejected
            "save1=1,113",           // valid → Alt (1) + 0x71
        };

        Dictionary<string, TeleportHotkeyBinding> map = TeleportHotkeyStore.Parse(lines);

        Assert.Equal(2, map.Count);
        Assert.Equal(116u, map["save0"].Vk);
        Assert.Equal(113u, map["save1"].Vk);
        Assert.False(map.ContainsKey("recall0"));
        Assert.False(map.ContainsKey("undo"));
        Assert.False(map.ContainsKey("back"));
    }
}
