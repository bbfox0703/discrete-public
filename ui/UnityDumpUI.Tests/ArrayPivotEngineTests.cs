// ArrayPivotEngineTests — Phase 29f-3 (Layer B).
//
// Mirrors PivotEngineTests for the reference-array element pivot:
//   * Input validation throws
//   * Inner key tracks an element's value across snaps regardless of slot
//     (the whole point — index-independent)
//   * Compound (outer instance key, inner element key)
//   * Collision when two elements share the inner key in one snap
//   * Absent cells when the inner key is missing in some snaps
//   * Cross-session works (engine is address-agnostic)
//   * Elements without inner fields (pre-Layer-B / primitive /
//     element_class_error) are skipped → honest empty
//   * KeyFilter substring
//   * Missing inner-key field → "(missing)" sentinel
//   * AddressPerSnap uses the element object's address
//   * Multiple value fields render in parallel

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ArrayPivotEngineTests
{
    // ─── Fixtures ──────────────────────────────────────────────────────

    private static SnapshotEnvelope MakeEnv(string label, string sessionId,
        params SnapshotClassEntry[] classes)
        => new()
        {
            ServerId = label, Label = label,
            CapturedAt = DateTime.UtcNow, CapturedAtUtc = DateTime.UtcNow.ToString("o"),
            GameBuildId = "b1", GameSessionId = sessionId,
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

    private static SnapshotFieldEntry Field(string name, string type, object? value)
    {
        var fe = new SnapshotFieldEntry { Name = name, Type = type };
        switch (value)
        {
            case null: break;
            case int i:    fe.Value = Num(i); break;
            case long l:   fe.Value = Num(l); break;
            case string s: fe.StringValue = s; break;
            default: throw new ArgumentException($"Unhandled fixture value: {value.GetType()}");
        }
        return fe;
    }

    private static JsonElement Num(object n) =>
        JsonDocument.Parse(Convert.ToString(n, System.Globalization.CultureInfo.InvariantCulture)!)
                    .RootElement.Clone();

    /// <summary>A reference-array field whose elements carry inner Fields (Layer B).</summary>
    private static SnapshotFieldEntry RefArray(string name, string elementType, params SnapshotArrayElement[] els)
        => new()
        {
            Name = name, Type = elementType + "[]",
            ArrayElements = new SnapshotArrayElements
            {
                Length = (ulong)els.Length, Count = els.Length,
                ElementType = elementType, ElementSize = 8, IsPointer = true,
                Elements = els.ToList(),
            },
        };

    private static SnapshotArrayElement Elem(string addr, params SnapshotFieldEntry[] fields)
        => new() { Address = addr, Ptr = addr, Fields = fields.ToList() };

    /// <summary>An element with no inner fields — pre-Layer-B / primitive slot.</summary>
    private static SnapshotArrayElement ElemNoFields(string addr)
        => new() { Address = addr, Ptr = addr };

    private static SnapshotArrayElement ElemClassError(string err)
        => new() { SnapshotElementClassError = err };

    private static List<PivotRow> RunOn(
        IReadOnlyList<string> innerKeys, IReadOnlyList<string> valueFields,
        IReadOnlyList<string>? outerKeys = null, string? keyFilter = null,
        params SnapshotEnvelope[] snaps)
        => ArrayPivotEngine.Run(new ArrayPivotQuery
        {
            Snapshots      = snaps,
            Assembly       = "asm",
            ClassFqn       = "Game.Stat",
            ArrayField     = "_data",
            OuterKeyFields = outerKeys ?? Array.Empty<string>(),
            InnerKeyFields = innerKeys,
            ValueFields    = valueFields,
            KeyFilter      = keyFilter,
        });

    private static PivotRow Row(List<PivotRow> rows, params string[] keyTuple)
        => rows.Single(r => r.KeyTuple.SequenceEqual(keyTuple));

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => RunOn(new[] { "id" }, new[] { "v" }));
        Assert.Contains("at least one snapshot", ex.Message);
    }

    [Fact]
    public void Run_NoInnerKeyFields_Throws()
    {
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat", Inst("0x1")));
        var ex = Assert.Throws<ArgumentException>(() =>
            RunOn(Array.Empty<string>(), new[] { "v" }, snaps: snap));
        Assert.Contains("inner key", ex.Message);
    }

    [Fact]
    public void Run_NoValueFields_Throws()
    {
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat", Inst("0x1")));
        var ex = Assert.Throws<ArgumentException>(() =>
            RunOn(new[] { "id" }, Array.Empty<string>(), snaps: snap));
        Assert.Contains("value field", ex.Message);
    }

    // ─── Slot-independent inner-key tracking ───────────────────────────

    [Fact]
    public void InnerKey_TracksElementValue_RegardlessOfSlotOrder()
    {
        // Snap 1: [propertyID=101 amount=10, propertyID=202 amount=20]
        // Snap 2 REORDERS the slots: [propertyID=202 amount=25, propertyID=101 amount=15]
        // Keyed by the inner _propertyID, each element is tracked across the
        // shuffle — Layer A's index keying would have reported a false change.
        var s1 = MakeEnv("s1", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10)),
                Elem("0xB", Field("_propertyID", "System.Int32", 202), Field("_amount", "System.Int32", 20))))));
        var s2 = MakeEnv("s2", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xC", Field("_propertyID", "System.Int32", 202), Field("_amount", "System.Int32", 25)),
                Elem("0xD", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 15))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: new[] { s1, s2 });

        var r101 = Row(rows, "101");
        Assert.Equal(new string?[] { "10", "15" }, r101.Values[0]);
        var r202 = Row(rows, "202");
        Assert.Equal(new string?[] { "20", "25" }, r202.Values[0]);
        Assert.All(rows, r => Assert.False(r.HasCollision));
    }

    // ─── Compound outer + inner key ────────────────────────────────────

    [Fact]
    public void CompoundKey_OuterInstanceKeySeparatesSameInnerKey()
    {
        // Two GameStat instances both have an element with _propertyID=101.
        // Outer key _statId disambiguates the owners → two distinct rows.
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1",
                Field("_statId", "System.Int32", 1),
                RefArray("_data", "Game.Add",
                    Elem("0xA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10)))),
            Inst("0xI2",
                Field("_statId", "System.Int32", 2),
                RefArray("_data", "Game.Add",
                    Elem("0xB", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 99))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" },
                         outerKeys: new[] { "_statId" }, snaps: snap);

        Assert.Equal(2, rows.Count);
        Assert.Equal("10", Row(rows, "1", "101").Values[0][0]);
        Assert.Equal("99", Row(rows, "2", "101").Values[0][0]);
    }

    // ─── Collision ─────────────────────────────────────────────────────

    [Fact]
    public void Collision_TwoElementsShareInnerKeyInOneSnap_Flags()
    {
        // No outer key + two elements with the same _propertyID in one
        // instance's array → the inner key is not unique → collision.
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10)),
                Elem("0xB", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 20))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: snap);
        var r = Assert.Single(rows);
        Assert.True(r.HasCollision);
        Assert.Contains("⟨2:", r.Values[0][0]);   // collision cell: 2 instances
    }

    // ─── Absent cells ──────────────────────────────────────────────────

    [Fact]
    public void AbsentCell_WhenInnerKeyMissingInSnap()
    {
        var s1 = MakeEnv("s1", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10))))));
        var s2 = MakeEnv("s2", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xB", Field("_propertyID", "System.Int32", 202), Field("_amount", "System.Int32", 20))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: new[] { s1, s2 });

        Assert.Equal(new string?[] { "10", null }, Row(rows, "101").Values[0]);
        Assert.Equal(new string?[] { null, "20" }, Row(rows, "202").Values[0]);
    }

    // ─── Cross-session (address-agnostic) ──────────────────────────────

    [Fact]
    public void CrossSession_PivotsByInnerValue_NotAddress()
    {
        // Different sessionId + different element/instance addresses; keyed by
        // inner value, the element still lines up across the two sessions.
        var s1 = MakeEnv("s1", "sessA", MakeClass("Game.Stat",
            Inst("0x1000", RefArray("_data", "Game.Add",
                Elem("0xAAAA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10))))));
        var s2 = MakeEnv("s2", "sessB", MakeClass("Game.Stat",
            Inst("0x9000", RefArray("_data", "Game.Add",
                Elem("0xBBBB", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 15))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: new[] { s1, s2 });
        Assert.Equal(new string?[] { "10", "15" }, Row(rows, "101").Values[0]);
    }

    // ─── Elements without inner fields are skipped ─────────────────────

    [Fact]
    public void ElementsWithoutInnerFields_AreSkipped()
    {
        // A pre-Layer-B / primitive element (no Fields) and an
        // element_class_error slot must not produce rows; only the captured
        // element does.
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                ElemNoFields("0xA"),
                ElemClassError("object_get_class_failed"),
                Elem("0xC", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: snap);
        var r = Assert.Single(rows);
        Assert.Equal(new[] { "101" }, r.KeyTuple.ToArray());
    }

    [Fact]
    public void NoCapturedElements_ReturnsEmpty()
    {
        // Old snapshot: array present but elements carry no inner fields →
        // Layer B honestly shows nothing rather than fabricating rows.
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                ElemNoFields("0xA"), ElemNoFields("0xB")))));
        Assert.Empty(RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: snap));
    }

    // ─── Filters / sentinels / address / multi-value ───────────────────

    [Fact]
    public void KeyFilter_Substring_RestrictsRows()
    {
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10)),
                Elem("0xB", Field("_propertyID", "System.Int32", 202), Field("_amount", "System.Int32", 20))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, keyFilter: "202", snaps: snap);
        var r = Assert.Single(rows);
        Assert.Equal(new[] { "202" }, r.KeyTuple.ToArray());
    }

    [Fact]
    public void MissingInnerKeyField_RendersMissingSentinel()
    {
        // Element captured (Layer B) but the chosen inner key field isn't
        // among its captured fields — e.g. elementFieldCap clipped it.
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA", Field("_amount", "System.Int32", 10))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: snap);
        var r = Assert.Single(rows);
        Assert.Equal(new[] { "(missing)" }, r.KeyTuple.ToArray());
    }

    [Fact]
    public void AddressPerSnap_UsesElementObjectAddress()
    {
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xELEM", Field("_propertyID", "System.Int32", 101), Field("_amount", "System.Int32", 10))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount" }, snaps: snap);
        Assert.Equal("0xELEM", Row(rows, "101").AddressPerSnap[0]);
    }

    [Fact]
    public void MultipleValueFields_RenderInParallel()
    {
        var snap = MakeEnv("s", "sess1", MakeClass("Game.Stat",
            Inst("0xI1", RefArray("_data", "Game.Add",
                Elem("0xA",
                    Field("_propertyID", "System.Int32", 101),
                    Field("_amount", "System.Int32", 10),
                    Field("_bonus",  "System.Int32", 5))))));

        var rows = RunOn(new[] { "_propertyID" }, new[] { "_amount", "_bonus" }, snaps: snap);
        var r = Row(rows, "101");
        Assert.Equal("10", r.Values[0][0]);
        Assert.Equal("5",  r.Values[1][0]);
    }
}
