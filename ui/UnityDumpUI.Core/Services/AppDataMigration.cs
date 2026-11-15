// AppDataMigration.cs — one-time move of the UI's user state from
// %LOCALAPPDATA%\Discrete_Dumper\ to %LOCALAPPDATA%\discrete\ (2026-11 rename).
//
// What moves: every top-level entry of the legacy folder EXCEPT Logs\ —
// settings.json, window-state.txt, teleport-hotkeys.txt, bookmarks,
// snapshots\, crash.log, … — each one only when the new folder does not
// already have an entry of that name (the new copy always wins; the legacy one
// is then left where it is and reported). Same parent folder, so every move is
// a rename: cheap enough for Program.Main before any store reads its file.
//
// What does NOT move: Logs\. Old logs are diagnostics, a game still running an
// older DLL may be writing into its per-PID folder, and the new DLL writes to
// discrete\Logs regardless. FileLogger.RunRetentionSweep ages the legacy tree
// out with the same 21-day rule and deletes Discrete_Dumper\ once it is empty.

using System;
using System.Collections.Generic;
using System.IO;

namespace UnityDumpUI.Core.Services;

public static class AppDataMigration
{
    /// <summary>What the last <see cref="Run"/> did, one line per entry, for the
    /// init log (the logger does not exist yet when Program.Main runs this).</summary>
    public static IReadOnlyList<string> LastReport { get; private set; } = [];

    /// <summary>
    /// Move user-state entries from the legacy app-data folder into the
    /// current one. Never throws; per-entry failures are reported and retried
    /// on the next start.
    /// </summary>
    /// <param name="localAppDataOverride">Tests: stands in for %LOCALAPPDATA%.</param>
    public static IReadOnlyList<string> Run(string? localAppDataOverride = null)
    {
        var report = new List<string>();
        try
        {
            string local = localAppDataOverride
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string legacy = Path.Combine(local, Constants.LegacyAppDataFolderName);
            string target = Path.Combine(local, Constants.AppDataFolderName);
            if (!Directory.Exists(legacy)) return LastReport = report;

            Directory.CreateDirectory(target);
            foreach (string src in Directory.GetFileSystemEntries(legacy))
            {
                string name = Path.GetFileName(src);
                if (string.Equals(name, "Logs", StringComparison.OrdinalIgnoreCase)) continue;

                string dst = Path.Combine(target, name);
                if (File.Exists(dst) || Directory.Exists(dst))
                {
                    report.Add($"app-data migration: kept existing {dst}; legacy copy left at {src}");
                    continue;
                }
                try
                {
                    if (Directory.Exists(src)) Directory.Move(src, dst);
                    else File.Move(src, dst);
                    report.Add($"app-data migration: moved {src} -> {dst}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    report.Add($"app-data migration: could not move {src}: {e.Message}");
                }
            }

            // Nothing left (no Logs\ either): drop the empty legacy folder now.
            if (FileLogger.TryDeleteIfEmpty(legacy))
                report.Add($"app-data migration: removed empty {legacy}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Add($"app-data migration failed: {e.GetType().Name}: {e.Message}");
        }
        return LastReport = report;
    }
}
