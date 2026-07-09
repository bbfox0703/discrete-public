// TeleportHotkeyStore.cs — Teleport hotkey rebinding (wave 2). Persists the
// user's per-action key bindings to a small plaintext file next to
// window-state.txt under %LOCALAPPDATA%\Discrete_Dumper. Deliberately NO JSON /
// reflection — one `action=mods,vk` line each — so it is trivially Native-AOT
// safe and loads synchronously. Corrupt / partial lines are skipped; a missing
// file loads as an empty set (the VM then seeds its defaults).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityDumpUI.Core;

namespace UnityDumpUI.Services;

/// <summary>One resolved key binding: the Win32 modifier mask + virtual-key
/// code that <c>RegisterHotKey</c> wants. <see cref="Label"/> is derived, never
/// stored.</summary>
public readonly record struct TeleportHotkeyBinding(uint WinMods, uint Vk)
{
    public string Label => HotkeyKeyMap.LabelFor(WinMods, Vk);
}

public sealed class TeleportHotkeyStore
{
    private const string FileName = "teleport-hotkeys.txt";
    private readonly string _path;

    public TeleportHotkeyStore() : this(DefaultPath()) { }

    /// <summary>Test seam: point the store at an explicit path.</summary>
    public TeleportHotkeyStore(string path) => _path = path;

    public string Path => _path;

    /// <summary>True when the config file exists — lets the VM tell "first run
    /// (seed defaults)" from "user cleared every binding (respect the empty set)".</summary>
    public bool Exists
    {
        get { try { return File.Exists(_path); } catch { return false; } }
    }

    /// <summary><c>%LOCALAPPDATA%\Discrete_Dumper\teleport-hotkeys.txt</c> — same
    /// app-data root as <see cref="WindowStateStore"/> and the FileLogger.</summary>
    public static string DefaultPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string subRoot = Constants.LogRootSubpath;          // "Discrete_Dumper\Logs"
        int slash = subRoot.IndexOf('\\');
        string appRoot = slash > 0 ? subRoot[..slash] : subRoot;
        return System.IO.Path.Combine(localAppData, appRoot, FileName);
    }

    /// <summary>Load the saved bindings keyed by action id; empty on absent /
    /// unreadable file (best-effort — a corrupt file must not break startup).</summary>
    public Dictionary<string, TeleportHotkeyBinding> Load()
    {
        try
        {
            return File.Exists(_path) ? Parse(File.ReadAllLines(_path)) : new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Rewrite the whole binding set (best-effort; failures swallowed).</summary>
    public void Save(IReadOnlyDictionary<string, TeleportHotkeyBinding> bindings)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(_path, Format(bindings));
        }
        catch
        {
            // best-effort persistence
        }
    }

    // ── Pure (re)serialization — unit-tested, no IO ──────────────────────────

    internal static string[] Format(IReadOnlyDictionary<string, TeleportHotkeyBinding> bindings)
    {
        var lines = new List<string>(bindings.Count + 1) { "# Discrete teleport hotkeys (action=mods,vk)" };
        foreach (KeyValuePair<string, TeleportHotkeyBinding> kv in bindings)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{kv.Key}={kv.Value.WinMods},{kv.Value.Vk}"));
        }
        return lines.ToArray();
    }

    internal static Dictionary<string, TeleportHotkeyBinding> Parse(IReadOnlyList<string> lines)
    {
        var map = new Dictionary<string, TeleportHotkeyBinding>(StringComparer.Ordinal);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string action = line.Substring(0, eq).Trim();
            string[] parts = line.Substring(eq + 1).Split(',');
            if (action.Length == 0 || parts.Length != 2) continue;
            if (uint.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mods)
                && uint.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint vk)
                && vk != 0)
            {
                map[action] = new TeleportHotkeyBinding(mods, vk);
            }
        }
        return map;
    }
}
