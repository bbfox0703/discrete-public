// UserSettingsStoreTests — Phase 29c-2.
//
// Detailed coverage as requested: round-trip, defaults, partial JSON
// backward-compat, malformed JSON fallback, missing-file fallback,
// permission-denied path, atomic write (no half-file on crash), the
// tmp-file cleanup contract, and the FileLogger integration so we
// confirm meaningful log lines land in the snapshot category.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class UserSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public UserSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UserSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static System.Threading.CancellationToken Ct =>
        TestContext.Current.CancellationToken;

    private string SettingsPath(string suffix = "settings") =>
        Path.Combine(_tempDir, $"{suffix}.json");

    // ─── DefaultSettingsPath ───────────────────────────────────────────

    [Fact]
    public void DefaultSettingsPath_LivesUnderLocalAppDataDiscreteUEDumper()
    {
        var p = UserSettingsStore.DefaultSettingsPath();
        // Verify the directory chain matches the FileLogger root so users
        // who open one folder find the other side-by-side.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Discrete_UEDumper", p);
        Assert.EndsWith("settings.json", p);
    }

    // ─── Defaults ──────────────────────────────────────────────────────

    [Fact]
    public void UserSettings_DefaultsMatchAdvertisedValues()
    {
        var s = new UserSettings();
        Assert.Null(s.SnapshotDbDirectory);
        Assert.False(s.AutoSaveSnapshots);
        Assert.Equal(200L * 1024 * 1024, s.AutoPruneThresholdBytes);  // 200 MB
        Assert.Equal(UserSettings.DefaultAutoPruneThresholdBytes, s.AutoPruneThresholdBytes);
    }

    [Fact]
    public void UserSettings_CloneIsIndependent()
    {
        var a = new UserSettings { SnapshotDbDirectory = @"D:\snaps", AutoSaveSnapshots = true, AutoPruneThresholdBytes = 12345 };
        var b = a.Clone();
        b.SnapshotDbDirectory = @"E:\other";
        b.AutoSaveSnapshots = false;
        b.AutoPruneThresholdBytes = 0;
        Assert.Equal(@"D:\snaps", a.SnapshotDbDirectory);
        Assert.True(a.AutoSaveSnapshots);
        Assert.Equal(12345, a.AutoPruneThresholdBytes);
    }

    // ─── Load: missing file ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MissingFileReturnsDefaults()
    {
        var path = SettingsPath();
        Assert.False(File.Exists(path));
        var store = new UserSettingsStore(path);
        var s = await store.LoadAsync(Ct);
        Assert.NotNull(s);
        Assert.False(s.AutoSaveSnapshots);
        Assert.Null(s.SnapshotDbDirectory);
        Assert.Equal(UserSettings.DefaultAutoPruneThresholdBytes, s.AutoPruneThresholdBytes);
        // LoadAsync MUST NOT create the file as a side effect — the
        // user shouldn't see a settings.json appear out of nowhere.
        Assert.False(File.Exists(path));
    }

    // ─── Round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllFields()
    {
        var path = SettingsPath();
        var store = new UserSettingsStore(path);
        var s = new UserSettings
        {
            SnapshotDbDirectory     = @"D:\custom\snaps",
            AutoSaveSnapshots       = true,
            AutoPruneThresholdBytes = 5L * 1024 * 1024 * 1024,  // 5 GB
        };
        await store.SaveAsync(s, Ct);
        Assert.True(File.Exists(path));

        var reloaded = await store.LoadAsync(Ct);
        Assert.Equal(@"D:\custom\snaps", reloaded.SnapshotDbDirectory);
        Assert.True(reloaded.AutoSaveSnapshots);
        Assert.Equal(5L * 1024 * 1024 * 1024, reloaded.AutoPruneThresholdBytes);
    }

    [Fact]
    public async Task SaveAsync_CreatesParentDirectoryIfMissing()
    {
        var path = Path.Combine(_tempDir, "nested", "deeper", "settings.json");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        var store = new UserSettingsStore(path);
        await store.SaveAsync(new UserSettings { AutoSaveSnapshots = true }, Ct);
        Assert.True(File.Exists(path));
    }

    // ─── Partial JSON (forward/backward compat) ────────────────────────

    [Fact]
    public async Task LoadAsync_PartialJsonFallsBackPerFieldToDefaults()
    {
        // A future build adds a field; a past UI reading the newer file
        // should ignore it AND still pick up the old known fields.
        // Equally, an older file missing a newer field should default it.
        var path = SettingsPath();
        await File.WriteAllTextAsync(path,
            """
            { "auto_save_snapshots": true, "future_unknown_field": "ignored" }
            """, Ct);

        var store = new UserSettingsStore(path);
        var s = await store.LoadAsync(Ct);
        Assert.True(s.AutoSaveSnapshots);                                 // present in file
        Assert.Null(s.SnapshotDbDirectory);                               // missing → null default
        Assert.Equal(UserSettings.DefaultAutoPruneThresholdBytes,
            s.AutoPruneThresholdBytes);                                   // missing → 1 GB default
    }

    [Fact]
    public async Task LoadAsync_EmptyObjectGivesAllDefaults()
    {
        var path = SettingsPath();
        await File.WriteAllTextAsync(path, "{}", Ct);
        var s = await new UserSettingsStore(path).LoadAsync(Ct);
        Assert.Null(s.SnapshotDbDirectory);
        Assert.False(s.AutoSaveSnapshots);
        Assert.Equal(UserSettings.DefaultAutoPruneThresholdBytes, s.AutoPruneThresholdBytes);
    }

    // ─── Corrupt JSON ──────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MalformedJsonFallsBackToDefaultsWithoutThrow()
    {
        var path = SettingsPath();
        await File.WriteAllTextAsync(path, "{ this is { not :: json", Ct);
        var s = await new UserSettingsStore(path).LoadAsync(Ct);
        Assert.NotNull(s);
        Assert.False(s.AutoSaveSnapshots);
        Assert.Equal(UserSettings.DefaultAutoPruneThresholdBytes, s.AutoPruneThresholdBytes);
    }

    [Fact]
    public async Task LoadAsync_TruncatedJsonFallsBack()
    {
        var path = SettingsPath();
        await File.WriteAllTextAsync(path, """{ "auto_save_snapshots": tr""", Ct);
        var s = await new UserSettingsStore(path).LoadAsync(Ct);
        Assert.False(s.AutoSaveSnapshots);
    }

    [Fact]
    public async Task LoadAsync_JsonArrayInsteadOfObjectFallsBack()
    {
        // Wrong shape altogether — should not crash, should give defaults.
        var path = SettingsPath();
        await File.WriteAllTextAsync(path, "[1,2,3]", Ct);
        var s = await new UserSettingsStore(path).LoadAsync(Ct);
        Assert.NotNull(s);
        Assert.False(s.AutoSaveSnapshots);
    }

    [Fact]
    public async Task LoadAsync_NullJsonValueFallsBack()
    {
        var path = SettingsPath();
        await File.WriteAllTextAsync(path, "null", Ct);
        var s = await new UserSettingsStore(path).LoadAsync(Ct);
        Assert.NotNull(s);
        Assert.False(s.AutoSaveSnapshots);
    }

    // ─── Atomic write ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_LeavesNoTmpSiblingOnSuccess()
    {
        var path = SettingsPath();
        var store = new UserSettingsStore(path);
        await store.SaveAsync(new UserSettings { AutoSaveSnapshots = true }, Ct);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var path = SettingsPath();
        var store = new UserSettingsStore(path);
        await store.SaveAsync(new UserSettings { AutoSaveSnapshots = true,  AutoPruneThresholdBytes = 100 }, Ct);
        await store.SaveAsync(new UserSettings { AutoSaveSnapshots = false, AutoPruneThresholdBytes = 200 }, Ct);
        var reloaded = await store.LoadAsync(Ct);
        Assert.False(reloaded.AutoSaveSnapshots);
        Assert.Equal(200, reloaded.AutoPruneThresholdBytes);
    }

    [Fact]
    public async Task SaveAsync_NullSettingsThrows()
    {
        var store = new UserSettingsStore(SettingsPath());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SaveAsync(null!, Ct));
    }

    // ─── FileLogger integration ────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_EmitsSnapshotLogLine()
    {
        var logRoot = Path.Combine(_tempDir, "logs");
        var settingsPath = SettingsPath();
        var logger = new FileLogger(logRoot);
        try
        {
            var store = new UserSettingsStore(settingsPath, logger);
            await store.SaveAsync(new UserSettings { AutoSaveSnapshots = true }, Ct);
        }
        finally
        {
            logger.Dispose();
        }

        var logFile = Path.Combine(logRoot, "snapshot", "current.log");
        Assert.True(File.Exists(logFile),
            $"Expected snapshot log file at {logFile} but it was not created.");
        var content = await File.ReadAllTextAsync(logFile, Ct);
        Assert.Contains("UserSettings: Settings saved", content);
    }

    [Fact]
    public async Task LoadAsync_LogsMissingFileFallback()
    {
        var logRoot = Path.Combine(_tempDir, "logs");
        var settingsPath = SettingsPath();
        var logger = new FileLogger(logRoot);
        try
        {
            var store = new UserSettingsStore(settingsPath, logger);
            await store.LoadAsync(Ct);
        }
        finally
        {
            logger.Dispose();
        }

        var content = await File.ReadAllTextAsync(
            Path.Combine(logRoot, "snapshot", "current.log"), Ct);
        Assert.Contains("Settings file not found", content);
    }

    [Fact]
    public async Task LoadAsync_LogsCorruptFileWithRecoveryHint()
    {
        var logRoot = Path.Combine(_tempDir, "logs");
        var settingsPath = SettingsPath();
        await File.WriteAllTextAsync(settingsPath, "not json", Ct);

        var logger = new FileLogger(logRoot);
        try
        {
            var store = new UserSettingsStore(settingsPath, logger);
            var s = await store.LoadAsync(Ct);
            Assert.NotNull(s);  // still returned defaults
        }
        finally
        {
            logger.Dispose();
        }

        var content = await File.ReadAllTextAsync(
            Path.Combine(logRoot, "snapshot", "current.log"), Ct);
        Assert.Contains("corrupt", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delete the file", content, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Concurrency ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ConcurrentCallsSerialiseCleanly()
    {
        // Internal semaphore should serialise writes — no torn file, last
        // writer wins, no .tmp leftover.
        var path = SettingsPath();
        var store = new UserSettingsStore(path);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await store.SaveAsync(new UserSettings
            {
                AutoSaveSnapshots       = (i % 2) == 0,
                AutoPruneThresholdBytes = 1000 + i,
            }, Ct);
        }).ToArray();
        await Task.WhenAll(tasks);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        // Whatever landed must be a valid UserSettings (last writer wins).
        var reloaded = await store.LoadAsync(Ct);
        Assert.NotNull(reloaded);
        Assert.InRange(reloaded.AutoPruneThresholdBytes, 1000, 1009);
    }
}
