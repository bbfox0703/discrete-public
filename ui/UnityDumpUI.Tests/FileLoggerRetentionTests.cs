// FileLoggerRetentionTests — rotation + age-based retention (2026-11).
//
// Policy under test (FileLogger.cs header; same as the DLL's Logger.cpp):
//   * current.log is the live file; at startup and at the size cap it is
//     archived to <YYYYMMDD-HHMMSS>.log stamped from its OWN last-write time.
//   * RunRetentionSweep deletes archives older than the window, and DLL
//     per-PID folders that are empty or whose newest file is that old. No
//     count cap — any number of recent folders/archives survives.
//   * The legacy Discrete_Dumper\Logs root is swept the same way and removed
//     once empty.

using System;
using System.IO;
using System.Linq;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class FileLoggerRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _base;     // stands in for %LOCALAPPDATA%
    private readonly string _root;     // <base>\discrete\Logs
    private readonly string _legacy;   // <base>\Discrete_Dumper\Logs

    public FileLoggerRetentionTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "DiscreteRetentionTest_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, Constants.AppDataFolderName, "Logs");
        _legacy = Path.Combine(_base, Constants.LegacyAppDataFolderName, "Logs");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    private static string MakeFile(string path, DateTime lastWriteUtc, string text = "x\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private LogRetentionResult Sweep() =>
        FileLogger.RunRetentionSweep(_root, _legacy, Now, retentionDays: 21);

    // ─── Constants ─────────────────────────────────────────────────────

    [Fact]
    public void Constants_LogRootIsDiscrete()
    {
        // Mirrors MagicStrings.h LogRootSubpath; both sides must agree.
        Assert.Equal(@"discrete\Logs", Constants.LogRootSubpath);
        Assert.Equal("Discrete_Dumper", Constants.LegacyAppDataFolderName);
        Assert.Equal(21, Constants.LogRetentionDays);
        Assert.Equal(8L * 1024 * 1024, Constants.LogMaxFileBytes);
    }

    // ─── Archive naming ────────────────────────────────────────────────

    [Fact]
    public void Archive_IsStampedFromFileOwnWriteTime_NotNow()
    {
        var mtime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
        string cur = MakeFile(Path.Combine(_root, "ui", "view", "current.log"), mtime.ToUniversalTime());

        string? dst = FileLogger.ArchiveByWriteTime(cur);

        Assert.NotNull(dst);
        Assert.Equal("20260304-050607.log", Path.GetFileName(dst));
        Assert.False(File.Exists(cur));
    }

    [Fact]
    public void Archive_SameSecondClash_GetsSuffixInsteadOfOverwriting()
    {
        var mtime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local).ToUniversalTime();
        string dir = Path.Combine(_root, "ui", "view");
        MakeFile(Path.Combine(dir, "20260304-050607.log"), mtime, "first\n");
        string cur = MakeFile(Path.Combine(dir, "current.log"), mtime, "second\n");

        string? dst = FileLogger.ArchiveByWriteTime(cur);

        Assert.Equal("20260304-050607-1.log", Path.GetFileName(dst));
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(dir, "20260304-050607.log")));
    }

    [Fact]
    public void Startup_ArchivesPreviousCurrentLog_AndStartsFresh()
    {
        string uiRoot = Path.Combine(_root, "ui");
        var mtime = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Local);
        MakeFile(Path.Combine(uiRoot, "init", "current.log"), mtime.ToUniversalTime(), "old session\n");

        using (var logger = new FileLogger(uiRoot))
            logger.Write("init", "new session");

        string[] names = Directory.GetFiles(Path.Combine(uiRoot, "init"))
            .Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "20261101-080000.log", "current.log" }, names);
        Assert.Contains("new session", File.ReadAllText(Path.Combine(uiRoot, "init", "current.log")));
        Assert.DoesNotContain("old session", File.ReadAllText(Path.Combine(uiRoot, "init", "current.log")));
    }

    [Fact]
    public void SizeCap_ArchivesMidSession()
    {
        string uiRoot = Path.Combine(_root, "ui");
        using (var logger = new FileLogger(uiRoot, maxFileBytes: 64))
        {
            for (int i = 0; i < 10; i++) logger.Write("view", new string('a', 40));
        }
        string[] files = Directory.GetFiles(Path.Combine(uiRoot, "view"));
        Assert.Contains(files, f => Path.GetFileName(f) == "current.log");
        Assert.True(files.Length > 1, "expected at least one archived chunk");
        Assert.All(files.Where(f => Path.GetFileName(f) != "current.log"),
            f => Assert.Matches(@"^\d{8}-\d{6}(-\d+)?\.log$", Path.GetFileName(f)));
    }

    // ─── Retention ─────────────────────────────────────────────────────

    [Fact]
    public void Sweep_DeletesStalePidFolders_KeepsRecentOnes_NoCountCap()
    {
        // 30 recent folders: a count cap (the old "newest 15") would drop half.
        for (int i = 0; i < 30; i++)
            MakeFile(Path.Combine(_root, (1000 + i).ToString(), "init", "current.log"), Now.AddDays(-1));
        MakeFile(Path.Combine(_root, "900", "init", "current.log"), Now.AddDays(-22));

        var r = Sweep();

        Assert.Equal(1, r.PidFoldersDeleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "900")));
        Assert.Equal(30, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public void Sweep_JudgesFolderByNewestFileInside_NotFolderMtime()
    {
        // Old archive + a fresh current.log (a game still running): folder stays.
        string pid = Path.Combine(_root, "4242");
        MakeFile(Path.Combine(pid, "scan", "20261001-000000.log"), Now.AddDays(-40));
        MakeFile(Path.Combine(pid, "init", "current.log"), Now.AddMinutes(-5));
        Directory.SetLastWriteTimeUtc(pid, Now.AddDays(-60));

        var r = Sweep();

        Assert.Equal(0, r.PidFoldersDeleted);
        Assert.True(File.Exists(Path.Combine(pid, "init", "current.log")));
        // ...but its aged archive goes.
        Assert.False(File.Exists(Path.Combine(pid, "scan", "20261001-000000.log")));
        Assert.Equal(1, r.FilesDeleted);
    }

    [Fact]
    public void Sweep_RemovesEmptyPidFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "5555", "init"));
        var r = Sweep();
        Assert.Equal(1, r.PidFoldersDeleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "5555")));
    }

    [Fact]
    public void Sweep_UiTree_PrunesAgedArchives_NeverCurrentLog_NeverTheFolder()
    {
        string view = Path.Combine(_root, "ui", "view");
        MakeFile(Path.Combine(view, "current.log"), Now.AddDays(-100));   // idle category: still kept
        MakeFile(Path.Combine(view, "20260101-000000.log"), Now.AddDays(-30));
        MakeFile(Path.Combine(view, "20261110-000000.log"), Now.AddDays(-5));

        var r = Sweep();

        Assert.Equal(1, r.FilesDeleted);
        Assert.True(File.Exists(Path.Combine(view, "current.log")));
        Assert.True(File.Exists(Path.Combine(view, "20261110-000000.log")));
        Assert.False(File.Exists(Path.Combine(view, "20260101-000000.log")));
    }

    [Fact]
    public void Sweep_LeavesNonPidFoldersAlone()
    {
        MakeFile(Path.Combine(_root, "notes", "readme.log"), Now.AddDays(-100));
        Sweep();
        Assert.True(File.Exists(Path.Combine(_root, "notes", "readme.log")));
    }

    [Fact]
    public void Sweep_LegacyRoot_AgesOut_AndIsRemovedWhenEmpty()
    {
        MakeFile(Path.Combine(_legacy, "123", "init", "current.log"), Now.AddDays(-30));
        MakeFile(Path.Combine(_legacy, "ui", "view", "current.3.log"), Now.AddDays(-25));

        var r = Sweep();

        Assert.True(r.LegacyRootRemoved);
        Assert.Equal(2, r.PidFoldersDeleted);
        Assert.False(Directory.Exists(_legacy));
        // Its parent app-data folder was empty too (no user state left), so it goes.
        Assert.False(Directory.Exists(Path.GetDirectoryName(_legacy)));
    }

    [Fact]
    public void Sweep_LegacyRoot_KeepsRecentLogs_AndUserState()
    {
        MakeFile(Path.Combine(_legacy, "123", "init", "current.log"), Now.AddDays(-2));
        string settings = MakeFile(Path.Combine(Path.GetDirectoryName(_legacy)!, "settings.json"), Now.AddDays(-200));

        var r = Sweep();

        Assert.False(r.LegacyRootRemoved);
        Assert.True(File.Exists(Path.Combine(_legacy, "123", "init", "current.log")));
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public void Sweep_MissingRoots_IsNoOp()
    {
        var r = FileLogger.RunRetentionSweep(Path.Combine(_base, "nope"), Path.Combine(_base, "nope2"), Now);
        Assert.Equal(default, r);
    }
}
