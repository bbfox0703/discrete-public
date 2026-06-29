// SpcGroupEngineTests — G7 (multiple-values group scan over an N-snapshot SPC
// intersection). Each slot carries its own per-snapshot predicate chain; a
// candidate is an object where N slots each follow their chain at distinct
// fields (a System of Distinct Representatives over the temporal axis).

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SpcGroupEngineTests
{
    private static SnapshotEnvelope MakeEnv(string session, params SnapshotClassEntry[] classes)
        => new()
        {
            ServerId = "t", Label = "t",
            CapturedAt = DateTime.UtcNow, CapturedAtUtc = DateTime.UtcNow.ToString("o"),
            GameBuildId = "b1", GameSessionId = session,
            TotalClasses = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes = classes.ToList(), ClassesLoaded = true,
        };

    private static SnapshotClassEntry MakeClass(string fqn, params SnapshotInstanceEntry[] instances)
    {
        int dot = fqn.LastIndexOf('.');
        return new SnapshotClassEntry
        {
            Assembly  = "asm",
            Namespace = dot >= 0 ? fqn.Substring(0, dot) : string.Empty,
            Name      = dot >= 0 ? fqn.Substring(dot + 1) : fqn,
            Instances = instances.ToList(),
        };
    }

    private static SnapshotInstanceEntry Inst(string addr, params SnapshotFieldEntry[] fields)
        => new() { Address = addr, Fields = fields.ToList() };

    private static SnapshotFieldEntry IntF(string name, int v)
        => new()
        {
            Name = name, Type = "System.Int32",
            Value = JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SpcGroupSlot Slot(GroupMatch.Scope scope, params SpcPredicate[] chain)
        => new() { Scope = scope, Predicates = chain };

    private static SpcGroupSlot Slot(params SpcPredicate[] chain)
        => new() { Scope = GroupMatch.Scope.NumericNoByte, Predicates = chain };

    // ─── 2-snapshot ────────────────────────────────────────────────────

    [Fact]
    public void TwoSnapshot_HpDecreasedMaxHpUnchanged_Match()
    {
        var s1 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 100), IntF("MaxHp", 100))));
        var s2 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 80),  IntF("MaxHp", 100))));
        var r = SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(100), SpcPredicate.Decreased),
                Slot(SpcPredicate.Exact(100), SpcPredicate.Unchanged),
            },
        });
        Assert.Single(r);
        Assert.Equal("Unit", r[0].Class);
        Assert.NotEqual(r[0].Slots[0].Field, r[0].Slots[1].Field);
    }

    [Fact]
    public void TwoSnapshot_SecondSlotChainUnsatisfied_NoMatch()
    {
        var s1 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 100), IntF("MaxHp", 100))));
        var s2 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 80),  IntF("MaxHp", 100))));
        var r = SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(100), SpcPredicate.Decreased),
                Slot(SpcPredicate.Exact(100), SpcPredicate.Increased),  // MaxHp didn't increase
            },
        });
        Assert.Empty(r);
    }

    // ─── 3-snapshot value-signature ────────────────────────────────────

    [Fact]
    public void ThreeSnapshot_ValueSignature_Match()
    {
        // money holds 9410 across all 3; level climbs 5→6→7.
        var s1 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("money", 9410), IntF("level", 5))));
        var s2 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("money", 9410), IntF("level", 6))));
        var s3 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("money", 9410), IntF("level", 7))));
        var r = SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2, s3 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(9410), SpcPredicate.Unchanged, SpcPredicate.Unchanged),
                Slot(SpcPredicate.Exact(5),    SpcPredicate.Increased, SpcPredicate.Increased),
            },
        });
        Assert.Single(r);
        Assert.Equal("Save", r[0].Class);
    }

    [Fact]
    public void DuplicateChain_NeedsTwoFields()
    {
        // Two slots with the SAME chain need two distinct fields holding 9410.
        var s1 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("a", 9410), IntF("b", 9410))));
        var s2 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("a", 9410), IntF("b", 9410))));
        var r = SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(9410), SpcPredicate.Unchanged),
                Slot(SpcPredicate.Exact(9410), SpcPredicate.Unchanged),
            },
        });
        Assert.Single(r);
        Assert.NotEqual(r[0].Slots[0].Field, r[0].Slots[1].Field);

        // Only one field == 9410 → no SDR.
        var t1 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("a", 9410), IntF("b", 1))));
        var t2 = MakeEnv("sess", MakeClass("Game.Save", Inst("0x1", IntF("a", 9410), IntF("b", 1))));
        Assert.Empty(SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { t1, t2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(9410), SpcPredicate.Unchanged),
                Slot(SpcPredicate.Exact(9410), SpcPredicate.Unchanged),
            },
        }));
    }

    // ─── validation ────────────────────────────────────────────────────

    [Fact]
    public void PredicateCountMismatch_Throws()
    {
        var s1 = MakeEnv("sess", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        var s2 = MakeEnv("sess", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Exact(1)),                       // 1 predicate, 2 snapshots
                Slot(SpcPredicate.Exact(1), SpcPredicate.Unchanged),
            },
        }));
    }

    [Fact]
    public void FirstPredicateRelative_Throws()
    {
        var s1 = MakeEnv("sess", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        var s2 = MakeEnv("sess", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[]
            {
                Slot(SpcPredicate.Unchanged, SpcPredicate.Unchanged),  // first can't be relative
                Slot(SpcPredicate.Exact(1), SpcPredicate.Unchanged),
            },
        }));
    }

    [Fact]
    public void FewerThanTwoSlots_Throws()
    {
        var s1 = MakeEnv("sess", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SpcGroupEngine.Run(new SpcGroupQuery
        {
            Snapshots = new[] { s1 },
            Slots = new[] { Slot(SpcPredicate.Exact(1)) },
        }));
    }
}
