// PivotStructComponentTests — Phase 31 (31-4).
//
// Verifies that the pivot + key-discovery engines treat inline value-type
// struct components (e.g. _position.x) as first-class fields:
//   * PivotEngine renders a whole struct value field as a "(x, y, z)" tuple
//   * PivotEngine values a single component via a dotted path (_position.y)
//   * PivotEngine keys by a dotted component path
//   * KeyDiscoveryEngine flattens components into its scored field set

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class PivotStructComponentTests
{
    private static SnapshotEnvelope MakeEnv(params SnapshotClassEntry[] classes)
        => new()
        {
            ServerId = "t", Label = "t",
            CapturedAt = System.DateTime.UtcNow, CapturedAtUtc = System.DateTime.UtcNow.ToString("o"),
            GameBuildId = "b1", GameSessionId = "s1",
            TotalClasses = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes = classes.ToList(), ClassesLoaded = true,
        };

    private static SnapshotClassEntry MakeClass(string fqn, params SnapshotInstanceEntry[] instances)
    {
        int dot = fqn.LastIndexOf('.');
        return new SnapshotClassEntry
        {
            Assembly = "asm",
            Namespace = dot >= 0 ? fqn.Substring(0, dot) : string.Empty,
            Name = dot >= 0 ? fqn.Substring(dot + 1) : fqn,
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

    private static SnapshotFieldEntry FloatM(string name, ulong offset, float v)
        => new()
        {
            Name = name, Type = "System.Single", Offset = offset,
            Value = JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry Vec3(string name, float x, float y, float z)
        => new()
        {
            Name = name, Type = "UnityEngine.Vector3", IsValueType = true,
            Fields = new List<SnapshotFieldEntry> { FloatM("x", 0, x), FloatM("y", 4, y), FloatM("z", 8, z) },
        };

    private static PivotRow Row(List<PivotRow> rows, string key)
        => rows.Single(r => string.Join(", ", r.KeyTuple) == key);

    // ─── PivotEngine ───────────────────────────────────────────────────

    [Fact]
    public void Pivot_WholeStructValueField_RendersTupleTrajectory()
    {
        var s1 = MakeEnv(MakeClass("Game.Player", Inst("0x1", IntF("_id", 1), Vec3("_position", 10, 20, 30))));
        var s2 = MakeEnv(MakeClass("Game.Player", Inst("0x2", IntF("_id", 1), Vec3("_position", 10, 25, 30))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots = new[] { s1, s2 }, Assembly = "asm", ClassFqn = "Game.Player",
            KeyFields = new List<string> { "_id" }, ValueFields = new List<string> { "_position" },
        });

        var row = Row(rows, "1");
        Assert.Equal("(10, 20, 30)", row.Values[0][0]);
        Assert.Equal("(10, 25, 30)", row.Values[0][1]);
    }

    [Fact]
    public void Pivot_DottedComponentValue_TracksSingleAxis()
    {
        var s1 = MakeEnv(MakeClass("Game.Player", Inst("0x1", IntF("_id", 1), Vec3("_position", 10, 20, 30))));
        var s2 = MakeEnv(MakeClass("Game.Player", Inst("0x2", IntF("_id", 1), Vec3("_position", 10, 25, 30))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots = new[] { s1, s2 }, Assembly = "asm", ClassFqn = "Game.Player",
            KeyFields = new List<string> { "_id" }, ValueFields = new List<string> { "_position.y" },
        });

        var row = Row(rows, "1");
        Assert.Equal("20", row.Values[0][0]);
        Assert.Equal("25", row.Values[0][1]);
    }

    [Fact]
    public void Pivot_DottedComponentKey_GroupsByAxisValue()
    {
        // Two instances at x=10 and x=99 → keying by _position.x yields two rows.
        var snap = MakeEnv(MakeClass("Game.Player",
            Inst("0x1", IntF("_hp", 100), Vec3("_position", 10, 0, 0)),
            Inst("0x2", IntF("_hp", 50),  Vec3("_position", 99, 0, 0))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots = new[] { snap }, Assembly = "asm", ClassFqn = "Game.Player",
            KeyFields = new List<string> { "_position.x" }, ValueFields = new List<string> { "_hp" },
        });

        Assert.Equal("100", Row(rows, "10").Values[0][0]);
        Assert.Equal("50",  Row(rows, "99").Values[0][0]);
    }

    [Fact]
    public void Pivot_MissingComponent_RendersMissing()
    {
        var snap = MakeEnv(MakeClass("Game.Player", Inst("0x1", IntF("_id", 1))));  // no _position
        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots = new[] { snap }, Assembly = "asm", ClassFqn = "Game.Player",
            KeyFields = new List<string> { "_position.x" }, ValueFields = new List<string> { "_id" },
        });
        Assert.Equal("(missing)", string.Join(", ", rows.Single().KeyTuple));
    }

    // ─── KeyDiscoveryEngine ────────────────────────────────────────────

    [Fact]
    public void KeyDiscovery_FlattensStructComponents()
    {
        var snap = MakeEnv(MakeClass("Game.Player",
            Inst("0x1", IntF("_id", 1), Vec3("_position", 1, 2, 3)),
            Inst("0x2", IntF("_id", 2), Vec3("_position", 4, 5, 6))));

        var r = KeyDiscoveryEngine.Run(new KeyDiscoveryQuery
        {
            Snapshots = new[] { snap }, Assembly = "asm", ClassFqn = "Game.Player",
        });

        var names = r.Fields.Select(f => f.FieldName).ToHashSet();
        Assert.Contains("_position", names);     // struct summary
        Assert.Contains("_position.x", names);    // component
        Assert.Contains("_position.z", names);
        // The component is a float → scored with the float TypePrior.
        Assert.Equal(0.20, r.Fields.Single(f => f.FieldName == "_position.x").TypePrior, 3);
    }
}
