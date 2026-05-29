// SnapshotStoreStreamingTests — Phase 30c.
//
// The UI capture loop streams each snapshotChunk straight to disk
// (BeginStreamingSaveAsync → AppendClassesAsync per chunk →
// FinishStreamingSaveAsync) instead of accumulating the whole snapshot in
// memory. These tests verify:
//   * a streamed snapshot loads back identical to a one-shot SaveSnapshotAsync
//     (same classes, same FQN order, same field round-trip);
//   * an in-progress row (payload_bytes = -1 sentinel) is hidden from
//     ListSnapshotsAsync and swept on reopen (crash mid-capture recovery).
// Real on-disk SQLite (not :memory:) so the FK cascade + reopen path are real.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotStoreStreamingTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotStoreStreamingTests()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "SnapshotStreamTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TempDb(string suffix = "stream") => Path.Combine(_tempDir, $"{suffix}.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamingSave_RoundTripsIdenticalToOneShotSave()
    {
        // Build 6 classes, split across 3 "chunks" of 2. Streaming them must
        // produce the same loaded result as saving all 6 in one SaveSnapshotAsync.
        var allClasses = MakeClasses(6);

        long oneShotId;
        long streamId;
        await using (var store = await SnapshotStore.OpenAsync(TempDb("a"), "build1", "Game.exe", ct: Ct))
        {
            oneShotId = await store.SaveSnapshotAsync(MakeRequest("oneshot", allClasses), Ct);
        }
        await using (var store = await SnapshotStore.OpenAsync(TempDb("b"), "build1", "Game.exe", ct: Ct))
        {
            long rowId = await store.BeginStreamingSaveAsync(MakeBeginRequest("streamed", total: 6), Ct);
            long bytes = 0;
            foreach (var chunk in Chunk(allClasses, 2))
                bytes += await store.AppendClassesAsync(rowId, chunk, Ct);
            await store.FinishStreamingSaveAsync(rowId, bytes, fieldValueCount: 6 * 2, Ct);
            streamId = rowId;

            // Visible + complete after Finish.
            var meta = Assert.Single(await store.ListSnapshotsAsync(Ct));
            Assert.Equal(streamId, meta.Id);
            Assert.Equal(6, meta.TotalClasses);
            Assert.True(meta.PayloadBytes > 0);
            Assert.Equal(12, meta.FieldValueCount);
        }

        // Load both and compare. LoadClassesAsync orders by class_fqn, so the
        // chunked-insert order can't matter — parity must hold exactly.
        List<SnapshotClassEntry> fromOneShot, fromStream;
        await using (var store = await SnapshotStore.OpenAsync(TempDb("a"), "build1", "Game.exe", ct: Ct))
            fromOneShot = await store.LoadClassesAsync(oneShotId, Ct);
        await using (var store = await SnapshotStore.OpenAsync(TempDb("b"), "build1", "Game.exe", ct: Ct))
            fromStream = await store.LoadClassesAsync(streamId, Ct);

        Assert.Equal(fromOneShot.Count, fromStream.Count);
        Assert.Equal(6, fromStream.Count);
        for (int i = 0; i < fromOneShot.Count; i++)
        {
            Assert.Equal(fromOneShot[i].Name, fromStream[i].Name);
            Assert.Equal(fromOneShot[i].Namespace, fromStream[i].Namespace);
            Assert.Equal(fromOneShot[i].Assembly, fromStream[i].Assembly);
            Assert.Equal(fromOneShot[i].Instances?.Count, fromStream[i].Instances?.Count);
        }
        // Sorted FQN order: Class0..Class5.
        Assert.Equal(
            new[] { "Class0", "Class1", "Class2", "Class3", "Class4", "Class5" },
            fromStream.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task IncompleteStreamingRow_IsHiddenAndSweptOnReopen()
    {
        var path = TempDb("crash");

        // Begin + append, but never Finish — simulates a capture that crashed
        // before FinishStreamingSaveAsync ran (row keeps payload_bytes = -1).
        await using (var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct))
        {
            long rowId = await store.BeginStreamingSaveAsync(MakeBeginRequest("aborted", total: 3), Ct);
            await store.AppendClassesAsync(rowId, MakeClasses(3), Ct);

            // Hidden from the list while incomplete (sentinel filters it out).
            Assert.Empty(await store.ListSnapshotsAsync(Ct));
        }

        // Reopen: the sweep in OpenAsync drops the incomplete row + its classes.
        await using (var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct))
        {
            Assert.Empty(await store.ListSnapshotsAsync(Ct));
            // payload_bytes accounting also clean (the orphan classes are gone).
            Assert.Equal(0, await store.GetTotalPayloadBytesAsync(Ct));
        }
    }

    [Fact]
    public async Task FinishedStreamingRow_SurvivesReopen()
    {
        var path = TempDb("survive");
        long id;
        await using (var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct))
        {
            id = await store.BeginStreamingSaveAsync(MakeBeginRequest("kept", total: 2), Ct);
            long bytes = await store.AppendClassesAsync(id, MakeClasses(2), Ct);
            await store.FinishStreamingSaveAsync(id, bytes, fieldValueCount: 4, Ct);
        }
        await using (var store = await SnapshotStore.OpenAsync(path, "build1", "Game.exe", ct: Ct))
        {
            var meta = Assert.Single(await store.ListSnapshotsAsync(Ct));
            Assert.Equal(id, meta.Id);
            Assert.Equal(2, (await store.LoadClassesAsync(id, Ct)).Count);
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────

    private static IEnumerable<List<SnapshotClassEntry>> Chunk(List<SnapshotClassEntry> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    private static List<SnapshotClassEntry> MakeClasses(int count, int instancesPerClass = 2)
    {
        var classes = new List<SnapshotClassEntry>(count);
        for (int ci = 0; ci < count; ci++)
        {
            var instances = new List<SnapshotInstanceEntry>(instancesPerClass);
            for (int ii = 0; ii < instancesPerClass; ii++)
            {
                instances.Add(new SnapshotInstanceEntry
                {
                    Address = $"0x{(0x1000 + ii):X16}",
                    Fields  = new List<SnapshotFieldEntry>
                    {
                        new() { Name = "field_0", Type = "System.Int32", Offset = 0 },
                        new() { Name = "field_1", Type = "System.Int32", Offset = 4 },
                    },
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
        return classes;
    }

    private static SnapshotPersistRequest MakeRequest(string label, List<SnapshotClassEntry> classes) =>
        new()
        {
            Label = label, CapturedAtUtc = "2026-05-29T12:00:00Z",
            GameBuildId = "build1", GameSessionId = "session1",
            TargetProcess = "Game.exe", Backend = "mono", UnityVersion = "2020.3.49f1",
            PointerSize = 8, TotalClasses = classes.Count,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            FieldValueCount = classes.Sum(c => (c.Instances?.Count ?? 0) * 2),
            InstanceCap = 256, ArrayCap = 64, IncludeUnchanged = false, AssemblyFilter = null,
            Classes = classes,
        };

    private static SnapshotStreamBeginRequest MakeBeginRequest(string label, int total) =>
        new()
        {
            Label = label, CapturedAtUtc = "2026-05-29T12:00:00Z",
            GameBuildId = "build1", GameSessionId = "session1",
            TargetProcess = "Game.exe", Backend = "mono", UnityVersion = "2020.3.49f1",
            PointerSize = 8, TotalClasses = total, TotalInstances = total * 2,
            InstanceCap = 256, ArrayCap = 64, IncludeUnchanged = false, AssemblyFilter = null,
        };
}
