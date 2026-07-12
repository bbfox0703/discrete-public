// BookmarkStore.cs — persistent per-game Inspector bookmarks (UE5CEDumper port).
//
// Pins a live instance you're inspecting (class + last-seen address + a label)
// so you can jump back to it later, persisted per game across sessions. Mirrors
// TeleportHotkeyStore: a small plaintext file under %LOCALAPPDATA%\Discrete_Dumper,
// one bookmark per line, NO JSON / reflection so it's trivially Native-AOT safe
// and loads synchronously. Corrupt / partial lines are skipped; a missing file
// loads as an empty list.
//
// Per-game keying: the file name embeds the game identity (GameBuildId, the same
// key the snapshot DB uses) so two games' bookmarks never collide.
//
// Unity re-anchor note: unlike UE (which re-walks a GWorld chain), Unity has no
// universal root, so a saved Address is only a same-session hint — after a game
// restart the recall path re-anchors the bookmark's stable CLASS via
// findInstances (see LiveInspectionViewModel.RecallBookmarkAsync).

using System;
using System.Collections.Generic;
using System.IO;
using UnityDumpUI.Core;

namespace UnityDumpUI.Core.Services;

/// <summary>One saved Inspector target: a class + a same-session address hint.</summary>
public sealed class InstanceBookmark
{
    public required string Label     { get; set; }
    public required string Assembly  { get; init; }
    public required string Namespace { get; init; }
    public required string Class     { get; init; }
    /// <summary>Last-seen instance address (hex). A same-session hint — re-anchored
    /// from the class on recall after a restart.</summary>
    public required string Address   { get; set; }
    public bool IsGameObject { get; init; }

    public string ClassDisplay =>
        string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
    /// <summary>Label falling back to the class name when the user left it blank.</summary>
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Label) ? ClassDisplay : Label;
}

/// <summary>
/// Loads / saves the per-game bookmark list. One instance serves every game —
/// the game identity is passed per call and selects the backing file.
/// </summary>
public sealed class BookmarkStore
{
    private const string FilePrefix = "bookmarks.";
    private const string FileSuffix = ".txt";
    private const char   Sep        = '|';

    private readonly string _dir;

    public BookmarkStore() : this(DefaultDir()) { }

    /// <summary>Test seam: point the store at an explicit directory.</summary>
    public BookmarkStore(string dir) => _dir = dir;

    /// <summary><c>%LOCALAPPDATA%\Discrete_Dumper</c> — same app-data root as
    /// <see cref="TeleportHotkeyStore"/> / the FileLogger.</summary>
    public static string DefaultDir()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string subRoot = Constants.LogRootSubpath;          // "Discrete_Dumper\Logs"
        int slash = subRoot.IndexOf('\\');
        string appRoot = slash > 0 ? subRoot[..slash] : subRoot;
        return Path.Combine(localAppData, appRoot);
    }

    public string PathFor(string? gameKey) =>
        Path.Combine(_dir, FilePrefix + Sanitize(gameKey) + FileSuffix);

    /// <summary>Load the bookmarks for a game; empty list on absent / unreadable
    /// file (best-effort — a corrupt file must not break the Inspector).</summary>
    public List<InstanceBookmark> Load(string? gameKey)
    {
        try
        {
            string path = PathFor(gameKey);
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : new List<InstanceBookmark>();
        }
        catch
        {
            return new List<InstanceBookmark>();
        }
    }

    /// <summary>Rewrite the whole bookmark set for a game (best-effort; swallowed).</summary>
    public void Save(string? gameKey, IReadOnlyList<InstanceBookmark> bookmarks)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathFor(gameKey), Format(bookmarks));
        }
        catch
        {
            // best-effort persistence
        }
    }

    // ── Pure (re)serialization — unit-tested, no IO ──────────────────────────

    internal static string[] Format(IReadOnlyList<InstanceBookmark> bookmarks)
    {
        var lines = new List<string>(bookmarks.Count + 1)
        {
            "# Discrete Inspector bookmarks (label|assembly|namespace|class|address|isGameObject)",
        };
        foreach (InstanceBookmark b in bookmarks)
        {
            lines.Add(string.Join(Sep,
                Clean(b.Label), Clean(b.Assembly), Clean(b.Namespace),
                Clean(b.Class), Clean(b.Address), b.IsGameObject ? "1" : "0"));
        }
        return lines.ToArray();
    }

    internal static List<InstanceBookmark> Parse(IReadOnlyList<string> lines)
    {
        var list = new List<InstanceBookmark>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] p = line.Split(Sep);
            if (p.Length != 6) continue;
            string assembly = p[1].Trim();
            string cls      = p[3].Trim();
            if (assembly.Length == 0 || cls.Length == 0) continue;   // must be re-inspectable
            list.Add(new InstanceBookmark
            {
                Label        = p[0].Trim(),
                Assembly     = assembly,
                Namespace    = p[2].Trim(),
                Class        = cls,
                Address      = p[4].Trim(),
                IsGameObject = p[5].Trim() == "1",
            });
        }
        return list;
    }

    // Strip the separator + newlines so a field can't break the line format.
    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    // Make a game key safe as a file-name fragment.
    internal static string Sanitize(string? gameKey)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return "default";
        Span<char> buf = stackalloc char[Math.Min(gameKey.Length, 64)];
        int n = 0;
        foreach (char c in gameKey)
        {
            if (n >= buf.Length) break;
            buf[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return n == 0 ? "default" : new string(buf[..n]);
    }
}
