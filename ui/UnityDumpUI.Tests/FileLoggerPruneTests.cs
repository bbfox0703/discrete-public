// FileLoggerPruneTests — count-based per-PID log retention.
//
// PruneOldDllPidDirectories keeps only the most-recent N numeric PID
// directories (by last-write time) under the log root and deletes the rest.
// The `ui` directory is never a candidate. (Changed 2026-05-29 from age-based
// to count-based: keeping logs longer than the last N sessions serves no
// purpose — see Constants.LogKeepRecentPidDirs.)

using System;
using System.IO;
using System.Linq;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class FileLoggerPruneTests : IDisposable
{
    private readonly string _root;

    public FileLoggerPruneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DiscretePruneTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Create a numeric-PID dir with a deterministic last-write time. Base time
    // is fixed (no Date.Now) so ordering is reproducible: higher index = newer.
    private string MakePidDir(int pid, int ageRank)
    {
        string dir = Path.Combine(_root, pid.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "current.log"), $"pid {pid}\n");
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(dir, baseTime.AddMinutes(ageRank));
        return dir;
    }

    [Fact]
    public void KeepsNewestN_DeletesOlder()
    {
        // 20 PID dirs, ageRank 0 (oldest) .. 19 (newest).
        for (int i = 0; i < 20; i++) MakePidDir(1000 + i, ageRank: i);

        int deleted = FileLogger.PruneOldDllPidDirectories(keepRecent: 15, logRootOverride: _root);

        Assert.Equal(5, deleted);
        var remaining = Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(15, remaining.Count);
        // The 5 oldest (1000..1004) are gone; the 15 newest (1005..1019) stay.
        Assert.DoesNotContain("1004", remaining);
        Assert.Contains("1005", remaining);
        Assert.Contains("1019", remaining);
    }

    [Fact]
    public void UnderLimit_DeletesNothing()
    {
        for (int i = 0; i < 10; i++) MakePidDir(2000 + i, ageRank: i);

        int deleted = FileLogger.PruneOldDllPidDirectories(keepRecent: 15, logRootOverride: _root);

        Assert.Equal(0, deleted);
        Assert.Equal(10, Directory.EnumerateDirectories(_root).Count());
    }

    [Fact]
    public void UiDirectory_IsNeverPruned()
    {
        // `ui` plus more PID dirs than the keep limit — ui must survive and
        // must not count toward the limit.
        Directory.CreateDirectory(Path.Combine(_root, "ui"));
        File.WriteAllText(Path.Combine(_root, "ui", "current.log"), "ui\n");
        for (int i = 0; i < 18; i++) MakePidDir(3000 + i, ageRank: i);

        int deleted = FileLogger.PruneOldDllPidDirectories(keepRecent: 15, logRootOverride: _root);

        Assert.Equal(3, deleted);                                   // 18 - 15
        Assert.True(Directory.Exists(Path.Combine(_root, "ui")));   // preserved
    }
}
