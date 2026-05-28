// ClassShortlistEngineTests — Phase 29e-3.
//
// Covers the Stage 0 class-scoring engine:
//   * instance-count aggregation (incl. chunked class entries)
//   * presence (full vs partial)
//   * CV (stable population = low, volatile = high)
//   * field-kind ratios (primitive / string / array / null)
//   * synthetic flag
//   * assembly filter
//   * default sort order (presence desc, CV asc, field count desc)
//   * validation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ClassShortlistEngineTests
{
    // ─── Fixtures ──────────────────────────────────────────────────────

    private static SnapshotEnvelope MakeEnv(params SnapshotClassEntry[] classes)
    {
        return new SnapshotEnvelope
        {
            ServerId       = "t",
            Label          = "t",
            CapturedAt     = DateTime.UtcNow,
            CapturedAtUtc  = DateTime.UtcNow.ToString("o"),
            GameBuildId    = "b1",
            GameSessionId  = "s1",
            TotalClasses   = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes        = classes.ToList(),
            ClassesLoaded  = true,
        };
    }

    private static SnapshotClassEntry MakeClass(
        string fqn, string assembly, params SnapshotInstanceEntry[] instances)
    {
        int dot = fqn.LastIndexOf('.');
        string ns   = dot >= 0 ? fqn.Substring(0, dot) : string.Empty;
        string name = dot >= 0 ? fqn.Substring(dot + 1) : fqn;
        return new SnapshotClassEntry
        {
            Assembly  = assembly,
            Namespace = ns,
            Name      = name,
            Instances = instances.ToList(),
        };
    }

    private static SnapshotInstanceEntry Inst(string addr, params SnapshotFieldEntry[] fields)
        => new() { Address = addr, Fields = fields.ToList() };

    private static SnapshotFieldEntry IntF(string name, int v)
        => new()
        {
            Name = name, Type = "System.Int32",
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .RootElement.Clone(),
        };

    private static SnapshotFieldEntry StrF(string name, string v)
        => new() { Name = name, Type = "System.String", StringValue = v };

    private static SnapshotFieldEntry RefF(string name, string type, string? ptr)
        => new() { Name = name, Type = type, Ptr = ptr };

    private static SnapshotFieldEntry NullF(string name, string type)
        => new() { Name = name, Type = type };   // no value at all

    private static SnapshotFieldEntry ArrF(string name, string elemType, params int[] vals)
    {
        var fe = new SnapshotFieldEntry
        {
            Name = name, Type = elemType + "[]",
            ArrayElements = new SnapshotArrayElements
            {
                Length = (ulong)vals.Length, Count = vals.Length,
                ElementType = elemType, Elements = new List<SnapshotArrayElement>(),
            },
        };
        for (int i = 0; i < vals.Length; i++)
            fe.ArrayElements.Elements.Add(new SnapshotArrayElement
            {
                Index = i,
                Value = JsonDocument.Parse(vals[i].ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    .RootElement.Clone(),
            });
        return fe;
    }

    private static ClassShortlistRow RowFor(List<ClassShortlistRow> rows, string fqn)
        => rows.Single(r => r.ClassFqn == fqn);

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        Assert.Throws<ArgumentException>(() => ClassShortlistEngine.Run(new ClassShortlistQuery
        {
            Snapshots = Array.Empty<SnapshotEnvelope>(),
        }));
    }

    // ─── Instance counts + presence ────────────────────────────────────

    [Fact]
    public void Run_InstanceCounts_PerSnapAndPresence()
    {
        var s1 = MakeEnv(MakeClass("Game.A", "asm",
            Inst("0x1", IntF("v", 1)), Inst("0x2", IntF("v", 2))));
        var s2 = MakeEnv(MakeClass("Game.A", "asm",
            Inst("0x3", IntF("v", 3))));

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1, s2 } });
        var a = RowFor(rows, "Game.A");
        Assert.Equal(new[] { 2, 1 }, a.InstancesPerSnap.ToArray());
        Assert.Equal(2, a.Presence);
        Assert.Equal(2, a.SnapCount);
        Assert.Equal(1.5, a.AvgInstances, 3);
    }

    [Fact]
    public void Run_PartialPresence_CountedCorrectly()
    {
        // Game.B absent in snap2 → presence 1/2, perSnap [3, 0].
        var s1 = MakeEnv(MakeClass("Game.B", "asm",
            Inst("0x1", IntF("v", 1)), Inst("0x2", IntF("v", 2)), Inst("0x3", IntF("v", 3))));
        var s2 = MakeEnv(MakeClass("Game.Other", "asm", Inst("0x9", IntF("v", 9))));

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1, s2 } });
        var b = RowFor(rows, "Game.B");
        Assert.Equal(1, b.Presence);
        Assert.Equal(new[] { 3, 0 }, b.InstancesPerSnap.ToArray());
    }

    [Fact]
    public void Run_ChunkedClassEntries_AggregateInSameSnap()
    {
        // Walker emitted Game.A in two chunks in the same snapshot.
        var s1 = MakeEnv(
            MakeClass("Game.A", "asm", Inst("0x1", IntF("v", 1))),
            MakeClass("Game.A", "asm", Inst("0x2", IntF("v", 2))));
        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1 } });
        var a = RowFor(rows, "Game.A");
        Assert.Equal(new[] { 2 }, a.InstancesPerSnap.ToArray());
    }

    // ─── CV stability ──────────────────────────────────────────────────

    [Fact]
    public void Run_StablePopulation_HasLowCv()
    {
        // 100, 100, 100 → CV 0.
        var snaps = Enumerable.Range(0, 3).Select(_ =>
            MakeEnv(MakeClass("Game.Stable", "asm",
                Enumerable.Range(0, 100).Select(i => Inst($"0x{i}", IntF("v", i))).ToArray()))).ToArray();

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = snaps });
        var stable = RowFor(rows, "Game.Stable");
        Assert.Equal(0.0, stable.Cv, 5);
    }

    [Fact]
    public void Run_VolatilePopulation_HasHighCv()
    {
        // 100 then 4 instances → high CV.
        var s1 = MakeEnv(MakeClass("Game.Volatile", "asm",
            Enumerable.Range(0, 100).Select(i => Inst($"0x{i}", IntF("v", i))).ToArray()));
        var s2 = MakeEnv(MakeClass("Game.Volatile", "asm",
            Enumerable.Range(0, 4).Select(i => Inst($"0xA{i}", IntF("v", i))).ToArray()));

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1, s2 } });
        var vol = RowFor(rows, "Game.Volatile");
        Assert.True(vol.Cv > 0.5, $"expected high CV, got {vol.Cv}");
    }

    // ─── Field ratios ──────────────────────────────────────────────────

    [Fact]
    public void Run_FieldRatios_ClassifyKinds()
    {
        // One instance, 4 fields: 1 int (primitive), 1 string, 1 array,
        // 1 null reference → each ratio 0.25, fieldCount 4.
        var s1 = MakeEnv(MakeClass("Game.Mix", "asm",
            Inst("0x1",
                IntF("n", 5),
                StrF("s", "hi"),
                ArrF("arr", "System.Int32", 1, 2, 3),
                NullF("r", "Game.Other"))));

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1 } });
        var mix = RowFor(rows, "Game.Mix");
        Assert.Equal(4, mix.FieldCount);
        Assert.Equal(0.25, mix.PrimitiveFieldRatio, 3);
        Assert.Equal(0.25, mix.StringFieldRatio, 3);
        Assert.Equal(0.25, mix.ArrayFieldRatio, 3);
        // The null reference field → 1 of 4 observations is nullish.
        Assert.Equal(0.25, mix.NullFieldRatio, 3);
    }

    [Fact]
    public void Run_EnumField_CountsAsPrimitive()
    {
        var enumF = new SnapshotFieldEntry
        {
            Name = "_type", Type = "Mortal.Core.GameStatType", IsEnum = true,
            Value = JsonDocument.Parse("3").RootElement.Clone(),
        };
        var s1 = MakeEnv(MakeClass("Game.E", "asm", Inst("0x1", enumF)));
        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1 } });
        var e = RowFor(rows, "Game.E");
        Assert.Equal(1.0, e.PrimitiveFieldRatio, 3);
    }

    [Fact]
    public void Run_ZeroPointer_CountsAsNull()
    {
        var s1 = MakeEnv(MakeClass("Game.Z", "asm",
            Inst("0x1",
                RefF("live", "Game.Other", "0xdeadbeef"),
                RefF("dead", "Game.Other", "0x0000000000000000"))));
        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1 } });
        var z = RowFor(rows, "Game.Z");
        Assert.Equal(0.5, z.NullFieldRatio, 3);   // 1 of 2 is a null ptr
    }

    // ─── Synthetic flag ────────────────────────────────────────────────

    [Fact]
    public void Run_SyntheticClass_Flagged()
    {
        var s1 = MakeEnv(
            MakeClass("Real.Thing", "asm", Inst("0x1", IntF("v", 1))),
            MakeClass("<>c__DisplayClass1", "asm", Inst("0x2", IntF("v", 2))));
        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1 } });
        Assert.False(RowFor(rows, "Real.Thing").IsSynthetic);
        Assert.True(RowFor(rows, "<>c__DisplayClass1").IsSynthetic);
    }

    // ─── Assembly filter ───────────────────────────────────────────────

    [Fact]
    public void Run_AssemblyFilter_LimitsResults()
    {
        var s1 = MakeEnv(
            MakeClass("Game.A", "Assembly-CSharp", Inst("0x1", IntF("v", 1))),
            MakeClass("Plugin.B", "PluginAsm",      Inst("0x2", IntF("v", 2))));
        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery
        {
            Snapshots = new[] { s1 },
            AssemblyFilter = "Assembly-CSharp",
        });
        Assert.Single(rows);
        Assert.Equal("Game.A", rows[0].ClassFqn);
    }

    // ─── Sort order ────────────────────────────────────────────────────

    [Fact]
    public void Run_DefaultSort_PresenceThenCvThenFieldCount()
    {
        // Three classes:
        //   Full+stable+many-fields  → should rank first
        //   Full+volatile            → middle
        //   Partial-presence         → last (presence < N)
        var stableManyFields = new Func<int, SnapshotInstanceEntry>(i =>
            Inst($"0x{i}", IntF("a", i), IntF("b", i), IntF("c", i)));

        var s1 = MakeEnv(
            MakeClass("Z.Stable", "asm", stableManyFields(0), stableManyFields(1)),
            MakeClass("Z.Volatile", "asm", Inst("0xV0", IntF("v", 0)), Inst("0xV1", IntF("v", 1))),
            MakeClass("Z.Partial", "asm", Inst("0xP0", IntF("v", 0))));
        var s2 = MakeEnv(
            MakeClass("Z.Stable", "asm", stableManyFields(2), stableManyFields(3)),  // count 2 again → CV 0
            MakeClass("Z.Volatile", "asm",
                Inst("0xV2", IntF("v", 2)), Inst("0xV3", IntF("v", 3)),
                Inst("0xV4", IntF("v", 4)), Inst("0xV5", IntF("v", 5)),
                Inst("0xV6", IntF("v", 6))));   // count jumps 2 → 5 = volatile
        // Z.Partial absent in s2.

        var rows = ClassShortlistEngine.Run(new ClassShortlistQuery { Snapshots = new[] { s1, s2 } });

        int iStable   = rows.FindIndex(r => r.ClassFqn == "Z.Stable");
        int iVolatile = rows.FindIndex(r => r.ClassFqn == "Z.Volatile");
        int iPartial  = rows.FindIndex(r => r.ClassFqn == "Z.Partial");

        Assert.True(iStable < iVolatile, "stable (CV 0) should rank above volatile");
        Assert.True(iVolatile < iPartial, "full-presence should rank above partial-presence");
    }
}
