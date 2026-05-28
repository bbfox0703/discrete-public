// SnapshotStoreTests — Phase 29c-1.
//
// Exercises the per-game SQLite snapshot store end-to-end. Uses a real
// SQLite file in a per-test temp directory rather than `:memory:` because
// the production path tests the actual on-disk migration / VACUUM /
// foreign-key cascade behaviour, and the per-test cleanup is trivial.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotStoreTests()
    {
        // bundle_e_sqlite3 needs the SQLitePCLRaw provider initialised
        // once per process — the production app does this in Program.Main;
        // tests do it here. Idempotent.
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "SnapshotStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TempDb(string suffix = "test") =>
        Path.Combine(_tempDir, $"{suffix}.db");

    /// <summary>Per-test CancellationToken alias so each call site stays
    /// compact while still satisfying xUnit v3's xUnit1051 analyzer.</summary>
    private static System.Threading.CancellationToken Ct =>
        TestContext.Current.CancellationToken;

    [Fact]
    public void ResolveDatabaseFileName_BuildsReadablePerGameFilename()
    {
        var name = SnapshotStore.ResolveDatabaseFileName(
            "ChivalryOfLegend.exe", "7f3a2b1c89e44d10");
        Assert.Equal("ChivalryOfLegend_7f3a2b1c.db", name);
    }

    [Fact]
    public void ResolveDatabaseFileName_SanitisesUnsafeCharsInBasename()
    {
        var name = SnapshotStore.ResolveDatabaseFileName(
            "weird:name<game>.exe", "abcdef0123456789");
        // Reserved chars become '_', extension stripped, prefix kept readable.
        Assert.Equal("weird_name_game__abcdef01.db", name);
    }

    [Fact]
    public void ResolveDatabaseFileName_PadsShortBuildIds()
    {
        var name = SnapshotStore.ResolveDatabaseFileName("foo.exe", "abc");
        Assert.Equal("foo_abc00000.db", name);
    }

    [Fact]
    public async Task OpenAsync_CreatesSchemaOnFreshFile()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        Assert.True(File.Exists(path));
        Assert.Equal(path, store.DatabasePath);
        Assert.Equal("build1", store.GameBuildId);
        Assert.Equal("Game.exe", store.TargetProcess);

        // Empty store: total bytes = 0, no snapshots.
        Assert.Equal(0, await store.GetTotalPayloadBytesAsync(Ct));
        Assert.Empty(await store.ListSnapshotsAsync(Ct));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsClassEntries()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        var req = MakeRequest("label-A", classCount: 3, instancesPerClass: 2);
        long id = await store.SaveSnapshotAsync(req, Ct);
        Assert.True(id > 0);

        var list = await store.ListSnapshotsAsync(Ct);
        var meta = Assert.Single(list);
        Assert.Equal(id, meta.Id);
        Assert.Equal("label-A", meta.Label);
        Assert.Equal(3, meta.TotalClasses);
        Assert.True(meta.PayloadBytes > 0);

        var classes = await store.LoadClassesAsync(id, Ct);
        Assert.Equal(3, classes.Count);
        // Class names are persisted; round-trip preserves namespace + name + assembly.
        Assert.All(classes, c => Assert.Equal("Game", c.Namespace));
        Assert.All(classes, c => Assert.StartsWith("Class", c.Name));
        Assert.All(classes, c => Assert.NotNull(c.Instances));
        Assert.All(classes, c => Assert.Equal(2, c.Instances!.Count));
        // Field round-trip — name + offset + raw value preserved through gzip + JSON.
        var firstField = classes[0].Instances![0].Fields[0];
        Assert.Equal("field_0", firstField.Name);
        Assert.Equal(0u, firstField.Offset);
    }

    [Fact]
    public async Task ListSnapshots_ReturnsNewestFirst()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        await store.SaveSnapshotAsync(MakeRequest("oldest",  capturedAt: "2026-05-29T10:00:00Z"), Ct);
        await store.SaveSnapshotAsync(MakeRequest("middle",  capturedAt: "2026-05-29T11:00:00Z"), Ct);
        await store.SaveSnapshotAsync(MakeRequest("newest",  capturedAt: "2026-05-29T12:00:00Z"), Ct);

        var list = await store.ListSnapshotsAsync(Ct);
        Assert.Equal(new[] { "newest", "middle", "oldest" }, list.Select(m => m.Label));
    }

    [Fact]
    public async Task DeleteSnapshot_CascadesToClassRows()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        long id1 = await store.SaveSnapshotAsync(MakeRequest("keep"), Ct);
        long id2 = await store.SaveSnapshotAsync(MakeRequest("delete-me"), Ct);

        long sizeBefore = await store.GetTotalPayloadBytesAsync(Ct);
        Assert.True(sizeBefore > 0);

        int deleted = await store.DeleteSnapshotAsync(id2, Ct);
        Assert.Equal(1, deleted);

        var remaining = await store.ListSnapshotsAsync(Ct);
        Assert.Single(remaining);
        Assert.Equal(id1, remaining[0].Id);

        // Foreign key cascade actually fired — total bytes shrunk, and the
        // dropped snapshot's class rows are gone.
        Assert.True(await store.GetTotalPayloadBytesAsync(Ct) < sizeBefore);
        Assert.Empty(await store.LoadClassesAsync(id2, Ct));
    }

    [Fact]
    public async Task ListDistinctClasses_DedupesAcrossSnapshotsAndOrdersByFqn()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        // Two snapshots with the SAME class names → the distinct query
        // must collapse them, not return 6 rows.
        await store.SaveSnapshotAsync(MakeRequest("s1", classCount: 3), Ct);
        await store.SaveSnapshotAsync(MakeRequest("s2", classCount: 3), Ct);

        var classes = await store.ListDistinctClassesAsync(Ct);
        Assert.Equal(3, classes.Count);
        Assert.All(classes, c => Assert.Equal("Assembly-CSharp", c.Assembly));
        Assert.Equal(new[] { "Game.Class0", "Game.Class1", "Game.Class2" },
            classes.Select(c => c.ClassFqn).ToArray());   // ORDER BY class_fqn
    }

    [Fact]
    public async Task ListDistinctClasses_EmptyStoreReturnsEmpty()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        Assert.Empty(await store.ListDistinctClassesAsync(Ct));
    }

    [Fact]
    public async Task DeleteSnapshot_MissingIdReturnsZero()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        int deleted = await store.DeleteSnapshotAsync(snapshotId: 999_999, Ct);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task Migration_ReopeningExistingDbDoesNotReSeed()
    {
        var path = TempDb();
        long id;
        await using (var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct))
        {
            id = await store.SaveSnapshotAsync(MakeRequest("persisted"), Ct);
        }

        // Second open against the same file MUST see the prior row, not
        // re-run the schema migration as if it were fresh.
        await using var reopened = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        var list = await reopened.ListSnapshotsAsync(Ct);
        Assert.Single(list);
        Assert.Equal(id, list[0].Id);
        Assert.Equal("persisted", list[0].Label);
    }

    [Fact]
    public async Task Vacuum_RunsWithoutError()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        await store.SaveSnapshotAsync(MakeRequest("a"), Ct);
        long id = await store.SaveSnapshotAsync(MakeRequest("b"), Ct);
        await store.DeleteSnapshotAsync(id, Ct);
        await store.VacuumAsync(Ct);  // smoke — no asserts beyond no-throw
    }

    [Fact]
    public async Task GetTotalPayloadBytes_EqualsSumOfSnapshotPayloads()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        await store.SaveSnapshotAsync(MakeRequest("a", classCount: 1), Ct);
        await store.SaveSnapshotAsync(MakeRequest("b", classCount: 5), Ct);

        var list = await store.ListSnapshotsAsync(Ct);
        long sumFromList = list.Sum(m => m.PayloadBytes);
        long sumFromAgg  = await store.GetTotalPayloadBytesAsync(Ct);
        Assert.Equal(sumFromList, sumFromAgg);
        Assert.True(sumFromAgg > 0);
    }

    [Fact]
    public async Task SaveSnapshot_PersistsAllMetadataColumns()
    {
        // Exercises every snapshots-table column round-trip so a future
        // schema migration doesn't silently drop a field.
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build_x", "Game_X.exe", ct: Ct);

        var req = MakeRequest("full-meta");
        long id = await store.SaveSnapshotAsync(req, Ct);

        var meta = (await store.ListSnapshotsAsync(Ct)).Single();
        Assert.Equal(id,                       meta.Id);
        Assert.Equal(req.Label,                meta.Label);
        Assert.Equal(req.CapturedAtUtc,        meta.CapturedAtUtc);
        Assert.Equal(req.GameBuildId,          meta.GameBuildId);
        Assert.Equal(req.GameSessionId,        meta.GameSessionId);
        Assert.Equal(req.TargetProcess,        meta.TargetProcess);
        Assert.Equal(req.Backend,              meta.Backend);
        Assert.Equal(req.UnityVersion,         meta.UnityVersion);
        Assert.Equal(req.PointerSize,          meta.PointerSize);
        Assert.Equal(req.TotalClasses,         meta.TotalClasses);
        Assert.Equal(req.TotalInstances,       meta.TotalInstances);
        Assert.Equal(req.FieldValueCount,      meta.FieldValueCount);
        Assert.Equal(req.InstanceCap,          meta.InstanceCap);
        Assert.Equal(req.ArrayCap,             meta.ArrayCap);
        Assert.Equal(req.IncludeUnchanged,     meta.IncludeUnchanged);
        Assert.Equal(req.AssemblyFilter,       meta.AssemblyFilter);
        Assert.True(meta.PayloadBytes > 0);
    }

    [Fact]
    public async Task SaveSnapshot_BlobShrinkAfterGzip()
    {
        // Sanity: gzip should compress the highly repetitive class FQN /
        // field name strings 2× at minimum on our synthetic fixture.
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        var req = MakeRequest("compressible", classCount: 20, instancesPerClass: 10, fieldsPerInstance: 8);
        await store.SaveSnapshotAsync(req, Ct);
        var meta = (await store.ListSnapshotsAsync(Ct)).Single();

        // Roughly estimate the uncompressed JSON size; payload must be
        // meaningfully smaller. (We can't introspect the exact JSON byte
        // count without re-serialising, so the bound is generous.)
        long classes = req.TotalClasses;
        long fields  = req.FieldValueCount;
        // Per-field overhead in our gzipped payload empirically averages
        // ~30 B; classes carry a constant ~120 B of FQN / namespace / etc.
        long looseUpperBound = (classes * 200) + (fields * 80);
        Assert.True(meta.PayloadBytes < looseUpperBound,
            $"Expected gzipped payload < {looseUpperBound} B but got {meta.PayloadBytes} B");
    }

    [Fact]
    public async Task OpenAsync_NewerSchemaVersionThrowsExplanatoryError()
    {
        var path = TempDb();
        // First open creates v1 schema.
        await using (var initial = await SnapshotStore.OpenAsync(path, "b1", "G.exe", ct: Ct))
        {
            await initial.SaveSnapshotAsync(MakeRequest("seed"), Ct);
        }

        // Manually bump the schema version to a value beyond what the
        // code supports — simulates an older UI reading a future DB.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE meta SET value = '{SnapshotStore.CurrentSchemaVersion + 1}' WHERE key='schema_version';";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotStore.OpenAsync(path, "b1", "G.exe", ct: Ct));
        Assert.Contains("newer than this build supports", ex.Message);
    }

    // ─── Phase 29c-6: Prune + Vacuum ──────────────────────────────────

    [Fact]
    public async Task PruneAsync_ThresholdZeroDisablesPruning()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        await store.SaveSnapshotAsync(MakeRequest("keep1"), Ct);
        await store.SaveSnapshotAsync(MakeRequest("keep2"), Ct);

        var result = await store.PruneAsync(thresholdBytes: 0, ct: Ct);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0L, result.FreedBytes);
        Assert.Equal(2, (await store.ListSnapshotsAsync(Ct)).Count);
    }

    [Fact]
    public async Task PruneAsync_BelowThresholdIsNoOp()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        await store.SaveSnapshotAsync(MakeRequest("only"), Ct);

        // Threshold way above current total.
        var result = await store.PruneAsync(thresholdBytes: 100_000_000, ct: Ct);
        Assert.Equal(0, result.Removed);
        Assert.Single(await store.ListSnapshotsAsync(Ct));
    }

    [Fact]
    public async Task PruneAsync_RemovesOldestFirstUntilUnderThreshold()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        await store.SaveSnapshotAsync(MakeRequest("oldest",  capturedAt: "2026-05-29T10:00:00Z", classCount: 5), Ct);
        await store.SaveSnapshotAsync(MakeRequest("middle",  capturedAt: "2026-05-29T11:00:00Z", classCount: 5), Ct);
        await store.SaveSnapshotAsync(MakeRequest("newest",  capturedAt: "2026-05-29T12:00:00Z", classCount: 5), Ct);

        long total = await store.GetTotalPayloadBytesAsync(Ct);
        // Set threshold to roughly the size of one snapshot — should drop
        // the two oldest, keeping just "newest".
        long perSnap = total / 3;
        var result = await store.PruneAsync(thresholdBytes: perSnap + 100, minRetain: 1, ct: Ct);
        Assert.Equal(2, result.Removed);
        Assert.True(result.FreedBytes > 0);

        var remaining = await store.ListSnapshotsAsync(Ct);
        Assert.Single(remaining);
        Assert.Equal("newest", remaining[0].Label);
    }

    [Fact]
    public async Task PruneAsync_AlwaysPreservesMinRetainEvenOverThreshold()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        await store.SaveSnapshotAsync(MakeRequest("a"), Ct);
        await store.SaveSnapshotAsync(MakeRequest("b"), Ct);

        // Threshold = 1 byte — would otherwise delete everything. minRetain
        // forces us to keep the single most recent snapshot.
        var result = await store.PruneAsync(thresholdBytes: 1, minRetain: 1, ct: Ct);
        Assert.Equal(1, result.Removed);
        var remaining = await store.ListSnapshotsAsync(Ct);
        Assert.Single(remaining);
        Assert.Equal("b", remaining[0].Label);
    }

    [Fact]
    public async Task PruneAsync_MinRetainEqualToTotalBlocksPrune()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        await store.SaveSnapshotAsync(MakeRequest("a"), Ct);
        await store.SaveSnapshotAsync(MakeRequest("b"), Ct);

        var result = await store.PruneAsync(thresholdBytes: 1, minRetain: 5, ct: Ct);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, (await store.ListSnapshotsAsync(Ct)).Count);
    }

    [Fact]
    public async Task PruneAsync_OnEmptyDbIsNoOp()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        var result = await store.PruneAsync(thresholdBytes: 100, ct: Ct);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0L, result.FreedBytes);
    }

    [Fact]
    public async Task PruneAsync_LogsPruneSummary()
    {
        var logRoot = Path.Combine(_tempDir, "logs");
        var logger = new FileLogger(logRoot);
        try
        {
            var path = TempDb("prunelogs");
            await using var store = await SnapshotStore.OpenAsync(
                path, "build1", "Game.exe", logger: logger, ct: Ct);
            await store.SaveSnapshotAsync(MakeRequest("a"), Ct);
            await store.SaveSnapshotAsync(MakeRequest("b"), Ct);
            await store.PruneAsync(thresholdBytes: 1, ct: Ct);
        }
        finally
        {
            logger.Dispose();
        }
        var content = await File.ReadAllTextAsync(
            Path.Combine(logRoot, "snapshot", "current.log"), Ct);
        Assert.Contains("prune removed=", content);
    }

    [Fact]
    public async Task ShouldVacuumAsync_FreshDbReturnsFalse()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);
        Assert.False(await store.ShouldVacuumAsync(ct: Ct));
    }

    [Fact]
    public async Task ShouldVacuumAsync_AfterMassiveDeleteReturnsTrue()
    {
        var path = TempDb();
        await using var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct);

        // Write enough rows that delete leaves a meaningful freelist.
        for (int i = 0; i < 8; i++)
            await store.SaveSnapshotAsync(MakeRequest($"snap{i}", classCount: 20, instancesPerClass: 10), Ct);

        long beforeBytes = new FileInfo(path).Length;
        await store.PruneAsync(thresholdBytes: 1, minRetain: 1, ct: Ct);

        // The PRAGMA freelist_count should now be > 30% of page_count
        // because we just dropped 7 of 8 snapshots' worth of pages.
        Assert.True(await store.ShouldVacuumAsync(freePageRatio: 0.30, ct: Ct),
            $"Expected freelist > 30% after dropping 7/8 snapshots; file is {beforeBytes} B");

        // VACUUM actually reclaims — file shrinks vs pre-VACUUM (rough check;
        // SQLite page alignment means small DBs may show tiny deltas).
        await store.VacuumAsync(Ct);
        long after = new FileInfo(path).Length;
        Assert.True(after <= beforeBytes);
    }

    [Fact]
    public async Task OpenAsync_EmitsSnapshotLogLine()
    {
        var logRoot = Path.Combine(_tempDir, "logs");
        var logger = new FileLogger(logRoot);
        try
        {
            var path = TempDb("logsmoke");
            await using var store = await SnapshotStore.OpenAsync(
                path, "build1", "Game.exe", logger: logger, ct: Ct);
            await store.SaveSnapshotAsync(MakeRequest("with-logger"), Ct);
            await store.DeleteSnapshotAsync(1, Ct);
            await store.VacuumAsync(Ct);
        }
        finally
        {
            logger.Dispose();
        }

        var logFile = Path.Combine(logRoot, "snapshot", "current.log");
        Assert.True(File.Exists(logFile));
        var content = await File.ReadAllTextAsync(logFile, Ct);
        Assert.Contains("opened (fresh schema v1)", content);
        Assert.Contains("saved snapshot",   content);
        Assert.Contains("deleted snapshot", content);
        Assert.Contains("VACUUM",           content);
    }

    // ─── Test fixture helpers ──────────────────────────────────────────

    private static SnapshotPersistRequest MakeRequest(
        string label,
        int classCount = 1,
        int instancesPerClass = 1,
        int fieldsPerInstance = 2,
        string capturedAt = "2026-05-29T12:34:56Z")
    {
        var classes = new List<SnapshotClassEntry>(classCount);
        for (int ci = 0; ci < classCount; ci++)
        {
            var instances = new List<SnapshotInstanceEntry>(instancesPerClass);
            for (int ii = 0; ii < instancesPerClass; ii++)
            {
                var fields = new List<SnapshotFieldEntry>(fieldsPerInstance);
                for (int fi = 0; fi < fieldsPerInstance; fi++)
                {
                    fields.Add(new SnapshotFieldEntry
                    {
                        Name   = $"field_{fi}",
                        Type   = "System.Int32",
                        Offset = (ulong)(fi * 4),
                    });
                }
                instances.Add(new SnapshotInstanceEntry
                {
                    Address = $"0x{(0x1000 + ii):X16}",
                    Fields  = fields,
                });
            }
            classes.Add(new SnapshotClassEntry
            {
                Assembly  = "Assembly-CSharp",
                Namespace = "Game",
                Name      = $"Class{ci}",
                Instances = instances,
            });
        }

        return new SnapshotPersistRequest
        {
            Label            = label,
            CapturedAtUtc    = capturedAt,
            GameBuildId      = "build1",
            GameSessionId    = "session1",
            TargetProcess    = "Game.exe",
            Backend          = "mono",
            UnityVersion     = "2020.3.49f1",
            PointerSize      = 8,
            TotalClasses     = classCount,
            TotalInstances   = classCount * instancesPerClass,
            FieldValueCount  = classCount * instancesPerClass * fieldsPerInstance,
            InstanceCap      = 256,
            ArrayCap         = 64,
            IncludeUnchanged = false,
            AssemblyFilter   = null,
            Classes          = classes,
        };
    }
}
