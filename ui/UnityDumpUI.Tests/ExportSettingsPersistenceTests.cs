// ExportSettingsPersistenceTests — guards the CE-export-knob persistence
// contracts the MainWindowViewModel debounced-save relies on (added with the
// UE5-style collapse/flatten port). The confirmed review finding was that the
// export save must NOT clobber the snapshot keys the Snapshot tab owns, and an
// OLDER settings.json missing the new keys must NOT flip true-default toggles
// off. These tests lock those data-model contracts (the VM wiring routes
// through the single live UserSettings; here we verify the building blocks).

using System;
using System.IO;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ExportSettingsPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public ExportSettingsPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExportSettingsPersistenceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private AssemblyBrowserViewModel BuildBrowser()
    {
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + Guid.NewGuid().ToString("N")));
        return new AssemblyBrowserViewModel(backend, logger);
    }

    private UserSettingsStore Store(string file)
        => new UserSettingsStore(Path.Combine(_tempDir, file));

    // ---- Source-gen round-trip (AOT path) -------------------------------

    [Fact]
    public async Task SaveLoad_RoundTripsExportAndSnapshotKeys()
    {
        var store = Store("rt.json");
        var s = new UserSettings
        {
            SnapshotDbDirectory       = @"D:\snaps",
            AutoSaveSnapshots         = true,
            AutoPruneThresholdBytes   = 12345,
            ExportDrilldownDepth      = 3,
            ExportArrayLimit          = 64,
            ExportCollapsePointerNodes = false,
            ExportUseLiveData         = false,
            ExportVerboseDescription  = true,
            ExportFlattenValueStructs = true,
            ExportFlattenLeafRecords  = true,
            ExportCollapseLeafPointers = true,
            ExportCollapseChain       = true,
            ExportRecordColors        = true,
            ExportRecordColorEven     = "0080FF",
            ExportRecordColorOdd      = "FF8000",
            ExportCeXmlBaseAddress    = "0xDEADBEEF",
        };
        await store.SaveAsync(s, TestContext.Current.CancellationToken);
        UserSettings r = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Snapshot keys intact.
        Assert.Equal(@"D:\snaps", r.SnapshotDbDirectory);
        Assert.True(r.AutoSaveSnapshots);
        Assert.Equal(12345, r.AutoPruneThresholdBytes);
        // Export keys intact through the source-gen serializer (AOT-safe path).
        Assert.Equal(3, r.ExportDrilldownDepth);
        Assert.Equal(64, r.ExportArrayLimit);
        Assert.False(r.ExportCollapsePointerNodes);
        Assert.False(r.ExportUseLiveData);
        Assert.True(r.ExportVerboseDescription);
        Assert.True(r.ExportFlattenValueStructs);
        Assert.True(r.ExportFlattenLeafRecords);
        Assert.True(r.ExportCollapseLeafPointers);
        Assert.True(r.ExportCollapseChain);
        Assert.True(r.ExportRecordColors);
        Assert.Equal("0080FF", r.ExportRecordColorEven);
        Assert.Equal("FF8000", r.ExportRecordColorOdd);
        Assert.Equal("0xDEADBEEF", r.ExportCeXmlBaseAddress);
    }

    // ---- Older settings.json (no export keys) ---------------------------

    [Fact]
    public async Task OldSettingsFile_MissingExportKeys_LeavesTrueDefaultsIntact()
    {
        // A pre-feature settings.json holds only snapshot keys.
        string path = Path.Combine(_tempDir, "old.json");
        await File.WriteAllTextAsync(path, """
            { "snapshot_db_directory": "D:\\old", "auto_save_snapshots": true }
            """, TestContext.Current.CancellationToken);
        var store = new UserSettingsStore(path);
        UserSettings loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Nullable true-default keys deserialise to null (absent), NOT false/0.
        Assert.Null(loaded.ExportCollapsePointerNodes);
        Assert.Null(loaded.ExportUseLiveData);
        Assert.Null(loaded.ExportDrilldownDepth);
        Assert.Null(loaded.ExportArrayLimit);
        Assert.Null(loaded.ExportRecordColorEven);

        // Applying to the Browser leaves its true/non-zero VM defaults intact.
        var browser = BuildBrowser();
        Assert.True(browser.CollapsePointerNodes);     // default true
        Assert.True(browser.UseLiveData);              // default true
        Assert.Equal(1, browser.DrilldownDepth);       // default 1
        Assert.Equal(16, browser.ArrayLimit);          // default 16
        browser.ApplyExportSettings(loaded);
        Assert.True(browser.CollapsePointerNodes);     // NOT flipped off
        Assert.True(browser.UseLiveData);
        Assert.Equal(1, browser.DrilldownDepth);
        Assert.Equal(16, browser.ArrayLimit);
        Assert.Equal("0080FF", browser.RecordColorEven); // default preserved
    }

    // ---- Clone copies both groups (dialog clone+save safety) ------------

    [Fact]
    public void Clone_CopiesExportAndSnapshotKeys()
    {
        var s = new UserSettings
        {
            SnapshotDbDirectory   = @"D:\x",
            ExportFlattenValueStructs = true,
            ExportRecordColors    = true,
            ExportRecordColorEven = "ABCDEF",
            ExportDrilldownDepth  = 5,
        };
        UserSettings c = s.Clone();
        Assert.Equal(@"D:\x", c.SnapshotDbDirectory);
        Assert.True(c.ExportFlattenValueStructs);
        Assert.True(c.ExportRecordColors);
        Assert.Equal("ABCDEF", c.ExportRecordColorEven);
        Assert.Equal(5, c.ExportDrilldownDepth);
    }

    // ---- WriteExportSettings preserves snapshot keys (finding #1) -------

    [Fact]
    public void WriteExportSettings_PreservesSnapshotKeys()
    {
        // The crux of the confirmed bug: writing export knobs into a settings
        // object must NOT touch the snapshot keys the Snapshot tab owns.
        var s = new UserSettings
        {
            SnapshotDbDirectory     = @"D:\snaps",
            AutoSaveSnapshots       = true,
            AutoPruneThresholdBytes = 999,
        };
        var browser = BuildBrowser();
        browser.FlattenValueStructs = true;
        browser.RecordColors        = true;
        browser.DrilldownDepth      = 4;

        browser.WriteExportSettings(s);

        // Snapshot keys untouched.
        Assert.Equal(@"D:\snaps", s.SnapshotDbDirectory);
        Assert.True(s.AutoSaveSnapshots);
        Assert.Equal(999, s.AutoPruneThresholdBytes);
        // Export keys written.
        Assert.True(s.ExportFlattenValueStructs);
        Assert.True(s.ExportRecordColors);
        Assert.Equal(4, s.ExportDrilldownDepth);
    }

    [Fact]
    public void ApplyThenWrite_RoundTripsThroughBrowser()
    {
        var s = new UserSettings
        {
            ExportDescShowOffset      = true,
            ExportFlattenLeafRecords  = true,
            ExportCollapseChain       = true,
            ExportRecordColors        = true,
            ExportRecordColorEven     = "112233",
            ExportRecordColorOdd      = "445566",
            ExportDrilldownDepth      = 2,
            ExportArrayLimit          = 32,
            ExportCollapsePointerNodes = false,
        };
        var browser = BuildBrowser();
        browser.ApplyExportSettings(s);

        Assert.True(browser.DescShowOffset);
        Assert.True(browser.FlattenLeafRecords);
        Assert.True(browser.CollapseChain);
        Assert.True(browser.RecordColors);
        Assert.Equal("112233", browser.RecordColorEven);
        Assert.Equal("445566", browser.RecordColorOdd);
        Assert.Equal(2, browser.DrilldownDepth);
        Assert.Equal(32, browser.ArrayLimit);
        Assert.False(browser.CollapsePointerNodes);

        var s2 = new UserSettings();
        browser.WriteExportSettings(s2);
        Assert.True(s2.ExportDescShowOffset);
        Assert.True(s2.ExportFlattenLeafRecords);
        Assert.True(s2.ExportCollapseChain);
        Assert.Equal("112233", s2.ExportRecordColorEven);
        Assert.Equal(2, s2.ExportDrilldownDepth);
        Assert.False(s2.ExportCollapsePointerNodes);
    }
}
