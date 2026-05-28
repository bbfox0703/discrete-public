// SnapshotFieldIndexArrayExpansionTests — Phase 29e-1b.
//
// Verifies that BuildFieldIndex fans out captured array elements as
// pseudo-rows keyed by {fieldName}[{index}], on top of the existing
// "[N elements, captured M]" summary row.
//
// Coverage per docs/class-pivot.md §10:
//   * primitive int[] → N pseudo-rows + 1 summary row
//   * primitive string[] → N pseudo-rows, values quoted
//   * primitive bool[] → "true" / "false" cells
//   * reference array (is_pointer=true, only Ptr filled) → ptr= cells
//   * empty array (Length=0) → summary row only, no pseudo-rows
//   * ArrayError (header was unreadable) → summary row only (no Elements list)
//   * captured < Length (cap hit) → only captured indices expand
//   * static array fields fan out identically with instance="static"
//   * element-level error renders as "(err: ...)"

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotFieldIndexArrayExpansionTests
{
    // ─── Fixture helpers ───────────────────────────────────────────────

    private static SnapshotEnvelope BuildEnv(params SnapshotClassEntry[] classes)
    {
        var env = new SnapshotEnvelope
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
        env.BuildFieldIndex();
        return env;
    }

    private static SnapshotClassEntry MakeClass(
        string assembly, string ns, string name,
        SnapshotInstanceEntry[]? instances = null,
        SnapshotFieldEntry[]? statics = null)
    {
        return new SnapshotClassEntry
        {
            Assembly  = assembly,
            Namespace = ns,
            Name      = name,
            Instances = instances?.ToList(),
            Statics   = statics?.ToList(),
        };
    }

    private static SnapshotInstanceEntry MakeInstance(
        string address, params SnapshotFieldEntry[] fields)
        => new() { Address = address, Fields = fields.ToList() };

    private static SnapshotFieldEntry IntArrayField(
        string name, ulong offset, int length, int captured, params int[] values)
    {
        var fe = new SnapshotFieldEntry
        {
            Name = name, Type = "System.Int32[]", Offset = offset,
            ArrayElements = new SnapshotArrayElements
            {
                Length      = (ulong)length,
                Count       = captured,
                ElementType = "System.Int32",
                ElementSize = 4,
                IsPointer   = false,
                Elements    = new List<SnapshotArrayElement>(),
            },
        };
        for (int i = 0; i < values.Length; i++)
        {
            fe.ArrayElements.Elements.Add(new SnapshotArrayElement
            {
                Index = i,
                Value = JsonDocument.Parse(values[i].ToString(
                    System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
            });
        }
        return fe;
    }

    // ─── Tests ─────────────────────────────────────────────────────────

    [Fact]
    public void PrimitiveIntArray_ExpandsToPerElementRows()
    {
        // _values = [10, 20, 30] → 3 pseudo-rows + 1 summary row.
        var env = BuildEnv(MakeClass("asm", "Game", "Stat",
            instances: new[]
            {
                MakeInstance("0x1", IntArrayField("_values", offset: 0x10, length: 3, captured: 3, 10, 20, 30)),
            }));

        Assert.Equal("10", env.FieldIndex["asm|Game.Stat|0x1|16|_values[0]"].ValueDisplay);
        Assert.Equal("20", env.FieldIndex["asm|Game.Stat|0x1|16|_values[1]"].ValueDisplay);
        Assert.Equal("30", env.FieldIndex["asm|Game.Stat|0x1|16|_values[2]"].ValueDisplay);
        // Summary row preserved
        Assert.Equal("[3 elements, captured 3]",
            env.FieldIndex["asm|Game.Stat|0x1|16|_values"].ValueDisplay);
        // Element type captured from the array header
        Assert.Equal("System.Int32", env.FieldIndex["asm|Game.Stat|0x1|16|_values[0]"].FieldType);
        // Element rows inherit owning instance + offset
        Assert.Equal("0x1", env.FieldIndex["asm|Game.Stat|0x1|16|_values[0]"].InstanceAddress);
        Assert.False(env.FieldIndex["asm|Game.Stat|0x1|16|_values[0]"].IsStatic);
    }

    [Fact]
    public void StringArray_ExpandsWithQuotedValues()
    {
        var fe = new SnapshotFieldEntry
        {
            Name = "_names", Type = "System.String[]", Offset = 0x20,
            ArrayElements = new SnapshotArrayElements
            {
                Length      = 2, Count = 2,
                ElementType = "System.String",
                IsPointer   = true,    // strings are pointers in managed memory
                Elements    = new List<SnapshotArrayElement>
                {
                    new() { Index = 0, String = "Bob" },
                    new() { Index = 1, String = "Alice" },
                },
            },
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Squad",
            instances: new[] { MakeInstance("0x2", fe) }));

        Assert.Equal("\"Bob\"",   env.FieldIndex["asm|Game.Squad|0x2|32|_names[0]"].ValueDisplay);
        Assert.Equal("\"Alice\"", env.FieldIndex["asm|Game.Squad|0x2|32|_names[1]"].ValueDisplay);
    }

    [Fact]
    public void BoolArray_RendersTrueFalse()
    {
        var fe = new SnapshotFieldEntry
        {
            Name = "_flags", Type = "System.Boolean[]", Offset = 0x30,
            ArrayElements = new SnapshotArrayElements
            {
                Length = 2, Count = 2, ElementType = "System.Boolean",
                Elements = new List<SnapshotArrayElement>
                {
                    new() { Index = 0, Value = JsonDocument.Parse("true").RootElement.Clone()  },
                    new() { Index = 1, Value = JsonDocument.Parse("false").RootElement.Clone() },
                },
            },
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Flags",
            instances: new[] { MakeInstance("0x3", fe) }));

        Assert.Equal("true",  env.FieldIndex["asm|Game.Flags|0x3|48|_flags[0]"].ValueDisplay);
        Assert.Equal("false", env.FieldIndex["asm|Game.Flags|0x3|48|_flags[1]"].ValueDisplay);
    }

    [Fact]
    public void ReferenceArray_PtrOnlyElements_RenderAsPtr()
    {
        // Layer A: reference arrays (Item[]) — DLL captures element
        // pointers but not inner fields. Pseudo-rows render ptr= so
        // SPC can at least detect slot-pointer changes within a
        // session. Cross-session ptr= is unreliable (addresses drift)
        // — Layer B will key by inner field instead.
        var fe = new SnapshotFieldEntry
        {
            Name = "_items", Type = "Game.Item[]", Offset = 0x40,
            ArrayElements = new SnapshotArrayElements
            {
                Length = 2, Count = 2, ElementType = "Game.Item",
                IsPointer = true,
                Elements = new List<SnapshotArrayElement>
                {
                    new() { Index = 0, Ptr = "0xdeadbeef" },
                    new() { Index = 1, Ptr = "0xcafef00d" },
                },
            },
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Bag",
            instances: new[] { MakeInstance("0x4", fe) }));

        Assert.Equal("ptr=0xdeadbeef", env.FieldIndex["asm|Game.Bag|0x4|64|_items[0]"].ValueDisplay);
        Assert.Equal("ptr=0xcafef00d", env.FieldIndex["asm|Game.Bag|0x4|64|_items[1]"].ValueDisplay);
        Assert.Equal("Game.Item",       env.FieldIndex["asm|Game.Bag|0x4|64|_items[0]"].FieldType);
    }

    [Fact]
    public void EmptyArray_AddsSummaryRowOnly()
    {
        // Length=0, Count=0 → no pseudo-rows. Summary row carries the
        // "empty" status. SPC will still see one row per array field,
        // but no element rows for empty arrays.
        var fe = new SnapshotFieldEntry
        {
            Name = "_empty", Type = "System.Int32[]", Offset = 0x50,
            ArrayElements = new SnapshotArrayElements
            {
                Length = 0, Count = 0, ElementType = "System.Int32",
                Elements = new List<SnapshotArrayElement>(),
            },
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Foo",
            instances: new[] { MakeInstance("0x5", fe) }));

        Assert.Single(env.FieldIndex);   // only summary, no pseudo-rows
        Assert.Equal("[0 elements, empty]",
            env.FieldIndex["asm|Game.Foo|0x5|80|_empty"].ValueDisplay);
    }

    [Fact]
    public void ArrayError_AddsSummaryRowOnly()
    {
        // The array header was unreadable (false-positive instance,
        // garbage pointer). Summary row carries the arr-err marker;
        // no Elements list to expand.
        var fe = new SnapshotFieldEntry
        {
            Name = "_broken", Type = "System.Int32[]", Offset = 0x60,
            ArrayError = "implausible_length",
            ArrayErrorLength = 4_000_000_000,
            // ArrayElements left null
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Foo",
            instances: new[] { MakeInstance("0x6", fe) }));

        Assert.Single(env.FieldIndex);
        Assert.Contains("arr-err", env.FieldIndex["asm|Game.Foo|0x6|96|_broken"].ValueDisplay);
    }

    [Fact]
    public void CapHit_ExpandsOnlyCapturedIndices()
    {
        // Array has 100 elements but DLL only captured the first 3
        // (instance_cap / array_cap hit). Pseudo-rows for indices 0-2
        // only; summary row reflects total length.
        var fe = IntArrayField("_big", offset: 0x70, length: 100, captured: 3, 1, 2, 3);

        var env = BuildEnv(MakeClass("asm", "Game", "Big",
            instances: new[] { MakeInstance("0x7", fe) }));

        Assert.Contains("asm|Game.Big|0x7|112|_big[0]", env.FieldIndex.Keys);
        Assert.Contains("asm|Game.Big|0x7|112|_big[2]", env.FieldIndex.Keys);
        Assert.DoesNotContain("asm|Game.Big|0x7|112|_big[50]", env.FieldIndex.Keys);
        Assert.Equal("[100 elements, captured 3]",
            env.FieldIndex["asm|Game.Big|0x7|112|_big"].ValueDisplay);
    }

    [Fact]
    public void StaticArrayField_ExpandsWithStaticInstanceKey()
    {
        // Static int[] s_table = [...] — pseudo-rows must carry
        // IsStatic=true and the "static" placeholder in the key.
        var fe = IntArrayField("s_table", offset: 0x80, length: 2, captured: 2, 7, 11);

        var env = BuildEnv(MakeClass("asm", "Game", "Tab",
            statics: new[] { fe }));

        Assert.Equal("7",  env.FieldIndex["asm|Game.Tab|static|128|s_table[0]"].ValueDisplay);
        Assert.Equal("11", env.FieldIndex["asm|Game.Tab|static|128|s_table[1]"].ValueDisplay);
        Assert.True(env.FieldIndex["asm|Game.Tab|static|128|s_table[0]"].IsStatic);
        Assert.Empty(env.FieldIndex["asm|Game.Tab|static|128|s_table[0]"].InstanceAddress);
    }

    [Fact]
    public void ElementLevelError_RendersErrParen()
    {
        // One element's read failed (e.g. SEH during element fetch).
        // Pseudo-row keeps the slot but marks the value as (err: ...).
        var fe = new SnapshotFieldEntry
        {
            Name = "_mixed", Type = "System.Int32[]", Offset = 0x90,
            ArrayElements = new SnapshotArrayElements
            {
                Length = 2, Count = 2, ElementType = "System.Int32",
                Elements = new List<SnapshotArrayElement>
                {
                    new() { Index = 0, Value = JsonDocument.Parse("42").RootElement.Clone() },
                    new() { Index = 1, Error = "read_failed" },
                },
            },
        };

        var env = BuildEnv(MakeClass("asm", "Game", "Foo",
            instances: new[] { MakeInstance("0x9", fe) }));

        Assert.Equal("42",                env.FieldIndex["asm|Game.Foo|0x9|144|_mixed[0]"].ValueDisplay);
        Assert.Equal("(err: read_failed)", env.FieldIndex["asm|Game.Foo|0x9|144|_mixed[1]"].ValueDisplay);
    }

    [Fact]
    public void NonArrayFields_NotAffected()
    {
        // Regression guard: scalar fields keep behaving exactly as
        // before — no spurious pseudo-rows, value display unchanged.
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
