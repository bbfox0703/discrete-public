// SnapshotFieldIndexStructExpansionTests — Phase 31 (31-1 / 31-3).
//
// Verifies that BuildFieldIndex fans out captured inline value-type struct
// members (Vector3 x/y/z, Color32 r/g/b/a, custom / nested structs) as
// pseudo-rows keyed by {fieldName}.{memberName}, on top of a summary row
// rendered as a "(x, y, z)" tuple by FormatFieldValue.
//
// Coverage:
//   * Vector3 → 3 component rows + 1 summary "(1, 2, 3)" row
//   * component rows carry the ABSOLUTE field offset (parent + member)
//   * Color32 byte members render as ints
//   * nested struct (struct-of-struct) recurses with a dotted path
//   * static struct field fans out with the "static" instance key
//   * a struct field with no captured members → summary only
//   * scalar fields unaffected (regression)

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotFieldIndexStructExpansionTests
{
    private static SnapshotEnvelope BuildEnv(params SnapshotClassEntry[] classes)
    {
        var env = new SnapshotEnvelope
        {
            ServerId = "test", Label = "test",
            CapturedAt = System.DateTime.UtcNow, CapturedAtUtc = System.DateTime.UtcNow.ToString("o"),
            GameBuildId = "b1", GameSessionId = "s1",
            TotalClasses = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes = classes.ToList(), ClassesLoaded = true,
        };
        env.BuildFieldIndex();
        return env;
    }

    private static SnapshotClassEntry MakeClass(
        string assembly, string ns, string name,
        SnapshotInstanceEntry[]? instances = null, SnapshotFieldEntry[]? statics = null)
        => new()
        {
            Assembly = assembly, Namespace = ns, Name = name,
            Instances = instances?.ToList(), Statics = statics?.ToList(),
        };

    private static SnapshotInstanceEntry MakeInstance(string address, params SnapshotFieldEntry[] fields)
        => new() { Address = address, Fields = fields.ToList() };

    private static SnapshotFieldEntry Member(string name, string type, ulong offset, double v)
        => new()
        {
            Name = name, Type = type, Offset = offset,
            Value = JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry Vec3(string name, ulong offset, float x, float y, float z)
        => new()
        {
            Name = name, Type = "UnityEngine.Vector3", Offset = offset, IsValueType = true,
            Fields = new List<SnapshotFieldEntry>
            {
                Member("x", "System.Single", 0, x),
                Member("y", "System.Single", 4, y),
                Member("z", "System.Single", 8, z),
            },
        };

    // ─── Tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Vector3_ExpandsToComponentRowsPlusSummaryTuple()
    {
        var env = BuildEnv(MakeClass("asm", "Game", "Player",
            instances: new[] { MakeInstance("0x1", Vec3("_position", 0x10, 1, 2, 3)) }));

        // Summary row: tuple render.
        Assert.Equal("(1, 2, 3)", env.FieldIndex["asm|Game.Player|0x1|16|_position"].ValueDisplay);

        // Component rows at absolute offsets 16 / 20 / 24.
        Assert.Equal("1", env.FieldIndex["asm|Game.Player|0x1|16|_position.x"].ValueDisplay);
        Assert.Equal("2", env.FieldIndex["asm|Game.Player|0x1|20|_position.y"].ValueDisplay);
        Assert.Equal("3", env.FieldIndex["asm|Game.Player|0x1|24|_position.z"].ValueDisplay);

        var x = env.FieldIndex["asm|Game.Player|0x1|16|_position.x"];
        Assert.Equal("System.Single", x.FieldType);
        Assert.Equal("0x1", x.InstanceAddress);
        Assert.False(x.IsStatic);
        Assert.Equal(16ul, x.FieldOffset);
    }

    [Fact]
    public void Color32_ByteMembersRenderAsInts()
    {
        var color = new SnapshotFieldEntry
        {
            Name = "_tint", Type = "UnityEngine.Color32", Offset = 0x20, IsValueType = true,
            Fields = new List<SnapshotFieldEntry>
            {
                Member("r", "System.Byte", 0, 255),
                Member("g", "System.Byte", 1, 128),
                Member("b", "System.Byte", 2, 0),
                Member("a", "System.Byte", 3, 255),
            },
        };
        var env = BuildEnv(MakeClass("asm", "Game", "Sprite",
            instances: new[] { MakeInstance("0x2", color) }));

        Assert.Equal("(255, 128, 0, 255)", env.FieldIndex["asm|Game.Sprite|0x2|32|_tint"].ValueDisplay);
        Assert.Equal("128", env.FieldIndex["asm|Game.Sprite|0x2|33|_tint.g"].ValueDisplay);
    }

    [Fact]
    public void NestedStruct_RecursesWithDottedPath()
    {
        // _bounds : { center : Vector3, extents : Vector3 } — one extra level.
        var bounds = new SnapshotFieldEntry
        {
            Name = "_bounds", Type = "UnityEngine.Bounds", Offset = 0x30, IsValueType = true,
            Fields = new List<SnapshotFieldEntry>
            {
                new()
                {
                    Name = "center", Type = "UnityEngine.Vector3", Offset = 0, IsValueType = true,
                    Fields = new List<SnapshotFieldEntry>
                    {
                        Member("x", "System.Single", 0, 5),
                        Member("y", "System.Single", 4, 6),
                        Member("z", "System.Single", 8, 7),
                    },
                },
            },
        };
        var env = BuildEnv(MakeClass("asm", "Game", "Box",
            instances: new[] { MakeInstance("0x3", bounds) }));

        // center.x at offset 0x30 + 0 + 0 = 48.
        Assert.Equal("5", env.FieldIndex["asm|Game.Box|0x3|48|_bounds.center.x"].ValueDisplay);
        Assert.Equal("7", env.FieldIndex["asm|Game.Box|0x3|56|_bounds.center.z"].ValueDisplay);
        // The intermediate struct also gets a summary row.
        Assert.Equal("(5, 6, 7)", env.FieldIndex["asm|Game.Box|0x3|48|_bounds.center"].ValueDisplay);
    }

    [Fact]
    public void StaticStructField_ExpandsWithStaticKey()
    {
        var env = BuildEnv(MakeClass("asm", "Game", "Origin",
            statics: new[] { Vec3("s_spawn", 0x40, 0, 1, 0) }));

        Assert.Equal("(0, 1, 0)", env.FieldIndex["asm|Game.Origin|static|64|s_spawn"].ValueDisplay);
        var y = env.FieldIndex["asm|Game.Origin|static|68|s_spawn.y"];
        Assert.Equal("1", y.ValueDisplay);
        Assert.True(y.IsStatic);
        Assert.Empty(y.InstanceAddress);
    }

    [Fact]
    public void StructWithNoCapturedMembers_SummaryOnly()
    {
        // is_value_type but Fields not captured (capture off / Phase B miss).
        var fe = new SnapshotFieldEntry
        {
            Name = "_v", Type = "Game.CustomStruct", Offset = 0x50, IsValueType = true,
        };
        var env = BuildEnv(MakeClass("asm", "Game", "Foo",
            instances: new[] { MakeInstance("0x5", fe) }));

        Assert.Single(env.FieldIndex);
        Assert.Equal("(no value)", env.FieldIndex["asm|Game.Foo|0x5|80|_v"].ValueDisplay);
    }

    [Fact]
    public void ScalarFields_Unaffected()
    {
        var fe = new SnapshotFieldEntry
        {
            Name = "_id", Type = "System.Int32", Offset = 0,
            Value = JsonDocument.Parse("101").RootElement.Clone(),
        };
        var env = BuildEnv(MakeClass("asm", "Game", "Foo",
            instances: new[] { MakeInstance("0xa", fe) }));

        Assert.Single(env.FieldIndex);
        Assert.Equal("101", env.FieldIndex["asm|Game.Foo|0xa|0|_id"].ValueDisplay);
    }
}
