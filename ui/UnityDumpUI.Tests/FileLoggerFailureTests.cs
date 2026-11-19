// FileLoggerFailureTests — logging is best-effort: an IO failure drops the
// line instead of throwing into the caller, and a later write recovers.

using System;
using System.IO;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class FileLoggerFailureTests : IDisposable
{
    private readonly string _base;

    public FileLoggerFailureTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "DiscreteLoggerFailTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Write_OpenFailure_DropsLine_ThenNextWriteRecovers()
    {
        string root = Path.Combine(_base, "ui");
        var logger = new FileLogger(root);

        // A directory where current.log belongs: the FileStream open fails
        // (startup archiving ignores it because File.Exists is false).
        string current = Path.Combine(root, "cat", Constants.LogCurrentFileName);
        Directory.CreateDirectory(current);

        var first = Record.Exception(() => logger.Write("cat", "first"));
        Assert.Null(first);

        Directory.Delete(current);

        var second = Record.Exception(() => logger.Write("cat", "second"));
        Assert.Null(second);

        logger.Dispose();

        Assert.True(File.Exists(current));
        string text = File.ReadAllText(current);
        Assert.DoesNotContain("first", text);
        Assert.EndsWith(" second\n", text);
    }

    [Fact]
    public void Constructor_And_Write_UnreachableRoot_DoNotThrow()
    {
        // A root below a regular file can never be created.
        string blocker = Path.Combine(_base, "blocker");
        File.WriteAllText(blocker, "x");
        string root = Path.Combine(blocker, "ui");

        FileLogger? logger = null;
        Assert.Null(Record.Exception(() => logger = new FileLogger(root)));
        Assert.Null(Record.Exception(() => logger!.Write("cat", "line")));
        Assert.Null(Record.Exception(() => logger!.Dispose()));
    }
}
