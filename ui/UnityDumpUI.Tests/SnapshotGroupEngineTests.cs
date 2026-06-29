// SnapshotGroupEngineTests — G6 (multiple-values group scan over snapshots).
//
// Covers Mode A (single-snapshot absolute) + Mode B (two-snapshot temporal) over
// the FieldIndex leaf source: SDR distinctness, duplicate-value, missing-value,
// the canonical "Current HP decreased + Max HP unchanged" case, scope, and the
// synthetic/static filters.

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotGroupEngineTests
{
    // ─── fixtures ──────────────────────────────────────────────────────
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

    private static SnapshotFieldEntry ByteF(string name, int v)
        => new()
        {
            Name = name, Type = "System.Byte",
            Value = JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotGroupSlot Exact(double v, GroupMatch.Scope scope = GroupMatch.Scope.NumericNoByte)
        => new() { Scope = scope, Predicate = GroupMatch.Predicate.Exact, Target = v };

    private static SnapshotGroupSlot Rel(GroupMatch.Predicate p)
        => new() { Scope = GroupMatch.Scope.NumericNoByte, Predicate = p };

    // ─── Mode A (single snapshot, absolute) ────────────────────────────

    [Fact]
    public void ModeA_ObjectHoldsBothValues_Match()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stats",
            Inst("0x1", IntF("Str", 24), IntF("Def", 12), IntF("Noise", 99))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(24), Exact(12) },
        });
        Assert.Single(r);
        Assert.Equal("Stats", r[0].Class);
        Assert.Equal(2, r[0].Slots.Count);
    }

    [Fact]
    public void ModeA_MissingValue_NoMatch()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stats", Inst("0x1", IntF("Str", 24))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(24), Exact(12) },
        });
        Assert.Empty(r);
    }

    [Fact]
    public void ModeA_DuplicateValueTwoFields_Match_DistinctRepresentatives()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stats",
            Inst("0x1", IntF("Str", 24), IntF("Def", 24))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(24), Exact(24) },
        });
        Assert.Single(r);
        // The SDR assigns each slot a DISTINCT representative field.
        Assert.NotEqual(r[0].Slots[0].Field, r[0].Slots[1].Field);
    }

    [Fact]
    public void ModeA_DuplicateValueOneField_NoMatch()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stats",
            Inst("0x1", IntF("Str", 24), IntF("Def", 99))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(24), Exact(24) },
        });
        Assert.Empty(r);
    }

    [Fact]
    public void ModeA_ByteExcludedUnderNoByteScope()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stats",
            Inst("0x1", ByteF("a", 3), ByteF("b", 5))));
        Assert.Empty(SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(3), Exact(5) },
        }));
        Assert.Single(SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap },
            Slots = new[] { Exact(3, GroupMatch.Scope.NumericAll), Exact(5, GroupMatch.Scope.NumericAll) },
        }));
    }

    [Fact]
    public void ExcludeSynthetic_DropsCompilerGeneratedClass()
    {
        var snap = MakeEnv("s1", MakeClass("Game.<>c__Display",
            Inst("0x1", IntF("a", 1), IntF("b", 2))));
        Assert.Empty(SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(1), Exact(2) },
            ExcludeSynthetic = true,
        }));
    }

    // ─── Mode B (two snapshots, temporal) ──────────────────────────────

    [Fact]
    public void ModeB_DecreasedPlusUnchanged_Match()
    {
        // Current HP fell, Max HP didn't — the motivating case.
        var s1 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 100), IntF("MaxHp", 100))));
        var s2 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 80),  IntF("MaxHp", 100))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[] { Rel(GroupMatch.Predicate.Decreased), Rel(GroupMatch.Predicate.Unchanged) },
        });
        Assert.Single(r);
        Assert.Equal("Unit", r[0].Class);
    }

    [Fact]
    public void ModeB_BothChanged_FailsUnchangedSlot()
    {
        // Level-up: both moved → the Unchanged slot can't be satisfied.
        var s1 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 100), IntF("MaxHp", 100))));
        var s2 = MakeEnv("sess", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 120), IntF("MaxHp", 120))));
        var r = SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { s1, s2 },
            Slots = new[] { Rel(GroupMatch.Predicate.Increased), Rel(GroupMatch.Predicate.Unchanged) },
        });
        Assert.Empty(r);
    }

    [Fact]
    public void ModeA_RelativePredicate_NoBaseline_NoMatch()
    {
        // A single snapshot has no temporal axis → relative predicate can't match.
        var snap = MakeEnv("s1", MakeClass("Game.Unit", Inst("0x1", IntF("Hp", 100), IntF("MaxHp", 100))));
        Assert.Empty(SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap },
            Slots = new[] { Rel(GroupMatch.Predicate.Decreased), Rel(GroupMatch.Predicate.Unchanged) },
        }));
    }

    // ─── validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_FewerThanTwoSlots_Throws()
    {
        var snap = MakeEnv("s1", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { snap }, Slots = new[] { Exact(1) },
        }));
    }

    [Fact]
    public void Run_ThreeSnapshots_Throws()
    {
        var s = MakeEnv("s1", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SnapshotGroupEngine.Run(new SnapshotGroupQuery
        {
            Snapshots = new[] { s, s, s }, Slots = new[] { Exact(1), Exact(2) },
        }));
    }
}
