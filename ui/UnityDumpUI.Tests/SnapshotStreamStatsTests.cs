// SnapshotStreamStatsTests — Phase 30b. With IL2CPP game-side streaming decode,
// the final decode stats + truncation + post-stream game memory ship on the LAST
// (done=true) chunk instead of the snapshotBegin response. These tests verify the
// SnapshotChunkResult model parses those fields, and that a non-final / non-
// streaming chunk leaves them at their backward-compatible defaults (null/false)
// so the UI falls back to the Begin values for a non-streaming (Mono / legacy) DLL.
using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotStreamStatsTests
{
    private static SnapshotChunkResult ParseChunk(string json) =>
        JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SnapshotChunkResult)!;

    // The last chunk of a streaming capture carries the finalized stats.
    [Fact]
    public void LastChunk_CarriesFinalizedStreamStats()
    {
        const string json = """
        {
          "ok": true,
          "snapshotId": "7",
          "cursor": 200,
          "nextCursor": 220,
          "done": true,
          "classes": [],
          "decodeStats": {
            "decodedClasses": 1500,
            "decodedInstances": 50000,
            "decodedArrays": 1200,
            "classInfoCacheHits": 90000,
            "classInfoCacheMisses": 1500,
            "decodedElementSets": 0,
            "elementBudgetHit": false
          },
          "truncatedByMemory": false,
          "truncatedByCount": true,
          "gameWorkingSet": 7340032000,
          "gamePrivateUsage": 6900000000,
          "gamePointerSize": 8
        }
        """;

        var chunk = ParseChunk(json);

        Assert.True(chunk.Done);
        Assert.NotNull(chunk.DecodeStats);
        Assert.Equal(50000, chunk.DecodeStats!.DecodedInstances);
        Assert.Equal(90000, chunk.DecodeStats.ClassInfoCacheHits);
        Assert.Equal(1500, chunk.DecodeStats.ClassInfoCacheMisses);
        Assert.False(chunk.TruncatedByMemory);
        Assert.True(chunk.TruncatedByCount);
        Assert.Equal(7340032000ul, chunk.GameWorkingSet);
        Assert.Equal(6900000000ul, chunk.GamePrivateUsage);
        Assert.Equal(8, chunk.GamePointerSize);
    }

    // A non-final chunk (mid-stream) omits the stats; they must default so the UI
    // doesn't mistake an in-progress chunk for the finalized totals.
    [Fact]
    public void MidChunk_LeavesStreamStatsAtDefaults()
    {
        const string json = """
        {
          "ok": true,
          "snapshotId": "7",
          "cursor": 0,
          "nextCursor": 100,
          "done": false,
          "classes": []
        }
        """;

        var chunk = ParseChunk(json);

        Assert.False(chunk.Done);
        Assert.Null(chunk.DecodeStats);
        Assert.False(chunk.TruncatedByMemory);
        Assert.False(chunk.TruncatedByCount);
        Assert.Null(chunk.GameWorkingSet);
        Assert.Null(chunk.GamePrivateUsage);
        Assert.Null(chunk.GamePointerSize);
    }
}
