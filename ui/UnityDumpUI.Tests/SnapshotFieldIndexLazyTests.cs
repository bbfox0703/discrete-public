// SnapshotFieldIndexLazyTests — Phase 29g-2.
//
// Verifies the lazy + releasable FieldIndex contract on SnapshotEnvelope:
//   * a fresh envelope (Classes loaded, index NOT built) reports
//     FieldIndexBuilt=false + an empty FieldIndex, but a non-zero cached
//     FieldValueCount (so the memory estimate / status line work without
//     paying the index cost).
//   * EnsureFieldIndex() builds on demand + is idempotent.
//   * CountFieldValues() (the cheap counting pass) matches the built
//     index size for a collision-free fixture.
//   * ReleaseFieldIndex() drops the index (FieldIndexBuilt=false, empty
//     FieldIndex) while keeping Classes + the cached count, and the index
//     rebuilds afterwards.
//
// This is the lazy/release coverage the Phase 29g plan calls for; the
// Diff/SPC eviction behaviour is exercised in SnapshotViewModelPersistenceTests.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotFieldIndexLazyTests
{
    // ─── Fixture helpers (mirrors SnapshotFieldIndexArrayExpansionTests) ──

    private static SnapshotEnvelope MakeLazyEnv(params SnapshotClassEntry[] classes)
        => new()
        {
            ServerId       = "test",
            Label          = "test",
            CapturedAt     = System.DateTime.UtcNow,
            CapturedAtUtc  = System.DateTime.UtcNow.ToString("o"),
            GameBuildId    = "b1",
            GameSessionId  = "s1",
            TotalClasses   = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes        = classes.ToList(),
            ClassesLoaded  = true,
        };

    private static SnapshotFieldEntry IntField(string name, ulong offset, int value)
        => new()
        {
            Name   = name,
            Type   = "System.Int32",
            Offset = offset,
            Value  = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotClassEntry Klass(
        SnapshotInstanceEntry[]? instances = null, SnapshotFieldEntry[]? statics = null)
        => new()
        {
            Assembly  = "asm",
            Namespace = "Game",
            Name      = "Stat",
            Instances = instances?.ToList(),
            Statics   = statics?.ToList(),
        };

    private static SnapshotEnvelope SampleEnv() => MakeLazyEnv(Klass(
        instances: new[]
        {
            new SnapshotInstanceEntry { Address = "0x1", Fields = { IntField("_hp", 0x10, 100), IntField("_mp", 0x14, 50) } },
            new SnapshotInstanceEntry { Address = "0x2", Fields = { IntField("_hp", 0x10, 200), IntField("_mp", 0x14, 75) } },
        },
        statics: new[] { IntField("s_count", 0x0, 2) }));

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public void FreshEnvelope_IndexNotBuilt_ButCountIsKnown()
    {
        var env = SampleEnv();
        env.RecomputeFieldValueCount();  // what capture / lazy-load does

        Assert.False(env.FieldIndexBuilt);
        Assert.Empty(env.FieldIndex);             // empty read-only view, no build
        Assert.Equal(5, env.FieldValueCount);     // 1 static + 2×2 instance fields
        Assert.True(env.EstimatedMemoryBytes > 0);
    }

    [Fact]
    public void EnsureFieldIndex_BuildsOnDemand_AndIsIdempotent()
    {
        var env = SampleEnv();
        env.RecomputeFieldValueCount();

        var built = env.EnsureFieldIndex();
        Assert.True(env.FieldIndexBuilt);
        Assert.Equal(5, built.Count);
        Assert.Equal("100", env.FieldIndex["asm|Game.Stat|0x1|16|_hp"].ValueDisplay);
        Assert.Equal("2",   env.FieldIndex["asm|Game.Stat|static|0|s_count"].ValueDisplay);

        // Idempotent — same instance, no rebuild.
        Assert.Same(built, env.EnsureFieldIndex());
    }

    [Fact]
    public void CountFieldValues_MatchesBuiltIndexSize()
    {
        var env = SampleEnv();
        int counted = SnapshotEnvelope.CountFieldValues(env.Classes);
        Assert.Equal(env.EnsureFieldIndex().Count, counted);
    }

    [Fact]
    public void ReleaseFieldIndex_DropsIndex_KeepsClassesAndCount_AndRebuilds()
    {
        var env = SampleEnv();
        env.EnsureFieldIndex();
        Assert.True(env.FieldIndexBuilt);

        env.ReleaseFieldIndex();
        Assert.False(env.FieldIndexBuilt);
        Assert.Empty(env.FieldIndex);             // index gone
        Assert.NotEmpty(env.Classes);             // data preserved
        Assert.Equal(5, env.FieldValueCount);     // cached count survives

        // Rebuilds on demand with identical content.
        Assert.Equal(5, env.EnsureFieldIndex().Count);
        Assert.Equal("200", env.FieldIndex["asm|Game.Stat|0x2|16|_hp"].ValueDisplay);
    }

    [Fact]
    public void ReleaseFieldIndex_WhenNotBuilt_IsNoOp()
    {
        var env = SampleEnv();
        env.RecomputeFieldValueCount();
        Assert.False(env.FieldIndexBuilt);

        env.ReleaseFieldIndex();  // must not throw
        Assert.False(env.FieldIndexBuilt);
        Assert.Equal(5, env.FieldValueCount);
    }
}
