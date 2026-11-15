// AppDataMigrationTests — 2026-11 move of user state from
// %LOCALAPPDATA%\Discrete_Dumper\ to %LOCALAPPDATA%\discrete\.

using System;
using System.IO;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class AppDataMigrationTests : IDisposable
{
    private readonly string _local;    // stands in for %LOCALAPPDATA%
    private readonly string _legacy;
    private readonly string _target;

    public AppDataMigrationTests()
    {
        _local = Path.Combine(Path.GetTempPath(), "DiscreteMigrationTest_" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_local, Constants.LegacyAppDataFolderName);
        _target = Path.Combine(_local, Constants.AppDataFolderName);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        try { Directory.Delete(_local, recursive: true); } catch { /* best-effort */ }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void NoLegacyFolder_DoesNothing()
    {
        var report = AppDataMigration.Run(_local);
        Assert.Empty(report);
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void MovesUserState_LeavesLogsBehind()
    {
        Write(Path.Combine(_legacy, "settings.json"), "{}");
        Write(Path.Combine(_legacy, "window-state.txt"), "w");
        Write(Path.Combine(_legacy, "snapshots", "game.db"), "db");
        Write(Path.Combine(_legacy, "Logs", "123", "init", "current.log"), "log");

        AppDataMigration.Run(_local);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(_target, "settings.json")));
        Assert.Equal("w", File.ReadAllText(Path.Combine(_target, "window-state.txt")));
        Assert.Equal("db", File.ReadAllText(Path.Combine(_target, "snapshots", "game.db")));
        Assert.False(File.Exists(Path.Combine(_legacy, "settings.json")));
        // Logs stay for the retention sweep to age out.
        Assert.True(File.Exists(Path.Combine(_legacy, "Logs", "123", "init", "current.log")));
        Assert.False(Directory.Exists(Path.Combine(_target, "Logs", "123")));
    }

    [Fact]
    public void ExistingTargetEntry_WinsAndLegacyCopyIsKept()
    {
        Write(Path.Combine(_legacy, "settings.json"), "old");
        Write(Path.Combine(_target, "settings.json"), "new");

        var report = AppDataMigration.Run(_local);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_target, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_legacy, "settings.json")));
        Assert.Contains(report, l => l.Contains("kept existing"));
    }

    [Fact]
    public void LegacyFolderWithoutLogs_IsRemovedOnceEmptied()
    {
        Write(Path.Combine(_legacy, "teleport-hotkeys.txt"), "h");

        AppDataMigration.Run(_local);

        Assert.False(Directory.Exists(_legacy));
        Assert.True(File.Exists(Path.Combine(_target, "teleport-hotkeys.txt")));
    }

    [Fact]
    public void SecondRun_IsIdempotent()
    {
        Write(Path.Combine(_legacy, "settings.json"), "{}");
        AppDataMigration.Run(_local);
        var report = AppDataMigration.Run(_local);
        Assert.Empty(report);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_target, "settings.json")));
    }
}
