// KeyDiscoveryEngineTests — Phase 29e-4.
//
// Covers Stage 1 field key auto-discovery:
//   * stable-unique field tops the chart
//   * cardinality: unique-per-instance vs constant
//   * TypePrior ordering (int > float > bool > reference)
//   * NamePrior (id / name / group lift)
//   * composite Score ordering
//   * greedy: single key when already unique
//   * greedy: compound key when single field collides
//   * suggested-key reason text
//   * validation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class KeyDiscoveryEngineTests
{
    // ─── Fixtures ──────────────────────────────────────────────────────

    private static SnapshotEnvelope MakeEnv(params SnapshotClassEntry[] classes)
        => new()
        {
            ServerId = "t", Label = "t",
            CapturedAt = DateTime.UtcNow, CapturedAtUtc = DateTime.UtcNow.ToString("o"),
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
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry FloatF(string name, double v)
        => new()
        {
            Name = name, Type = "System.Single",
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry BoolF(string name, bool v)
        => new()
        {
            Name = name, Type = "System.Boolean",
            Value = JsonDocument.Parse(v ? "true" : "false").RootElement.Clone(),
        };

    private static SnapshotFieldEntry PtrF(string name, long v)
        => new()
        {
            Name = name, Type = "System.IntPtr",
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static KeyDiscoveryResult RunOn(params SnapshotEnvelope[] snaps)
        => KeyDiscoveryEngine.Run(new KeyDiscoveryQuery
        {
            Snapshots = snaps, Assembly = "asm", ClassFqn = "Game.Stat",
        });

    // 29i-2: a reference-array field whose elements carry inner Fields
    // (Layer B capture). Each element is one SnapshotFieldEntry[].
    private static SnapshotFieldEntry RefArrayF(string name, params SnapshotFieldEntry[][] elements)
    {
        var fe = new SnapshotFieldEntry
        {
            Name = name, Type = "Game.Elem[]",
            ArrayElements = new SnapshotArrayElements
            {
                Length = (ulong)elements.Length, Count = elements.Length,
                IsPointer = true, ElementType = "Game.Elem",
                Elements = new List<SnapshotArrayElement>(),
            },
        };
        for (int i = 0; i < elements.Length; i++)
            fe.ArrayElements.Elements.Add(new SnapshotArrayElement
            {
                Index = i, Fields = elements[i].ToList(),
            });
        return fe;
    }

    private static KeyDiscoveryResult RunElementOn(string arrayField, params SnapshotEnvelope[] snaps)
        => KeyDiscoveryEngine.Run(new KeyDiscoveryQuery
        {
            Snapshots = snaps, Assembly = "asm", ClassFqn = "Game.Stat",
            ArrayField = arrayField,
        });

    private static FieldScore ScoreOf(KeyDiscoveryResult r, string field)
        => r.Fields.Single(f => f.FieldName == field);

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeyDiscoveryEngine.Run(new KeyDiscoveryQuery
        {
            Snapshots = Array.Empty<SnapshotEnvelope>(), Assembly = "asm", ClassFqn = "X",
        }));
    }

    // ─── Cardinality ───────────────────────────────────────────────────

    [Fact]
    public void Score_UniquePerInstanceField_HasHighCardinality()
    {
        // _id unique per instance → cardinality 1.0. _const same for all
        // → cardinality 1/3.
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_id", 1), IntF("_const", 9)),
            Inst("0x2", IntF("_id", 2), IntF("_const", 9)),
            Inst("0x3", IntF("_id", 3), IntF("_const", 9))));
        var r = RunOn(snap);
        Assert.Equal(1.0, ScoreOf(r, "_id").SCardinality, 3);
        Assert.Equal(1.0 / 3, ScoreOf(r, "_const").SCardinality, 3);
    }

    // ─── Stability ─────────────────────────────────────────────────────

    [Fact]
    public void Score_StableValueSet_HasHighStability()
    {
        // _propertyID holds {101,201} both snaps → Jaccard 1.0.
        // _frame holds disjoint sets → Jaccard 0.
        var s1 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_propertyID", 101), IntF("_frame", 10)),
            Inst("0x2", IntF("_propertyID", 201), IntF("_frame", 11))));
        var s2 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x3", IntF("_propertyID", 101), IntF("_frame", 90)),
            Inst("0x4", IntF("_propertyID", 201), IntF("_frame", 91))));
        var r = RunOn(s1, s2);
        Assert.Equal(1.0, ScoreOf(r, "_propertyID").SStable, 3);
        Assert.Equal(0.0, ScoreOf(r, "_frame").SStable, 3);
    }

    // ─── Type prior ────────────────────────────────────────────────────

    [Fact]
    public void Score_TypePrior_IntBeatsFloatBeatsBool()
    {
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("i", 1), FloatF("f", 1.5), BoolF("b", true))));
        var r = RunOn(snap);
        Assert.Equal(1.00, ScoreOf(r, "i").TypePrior, 3);
        Assert.Equal(0.20, ScoreOf(r, "f").TypePrior, 3);
        Assert.Equal(0.10, ScoreOf(r, "b").TypePrior, 3);
    }

    [Fact]
    public void Score_EnumTypePrior_IsHigh()
    {
        var enumF = new SnapshotFieldEntry
        {
            Name = "_type", Type = "Game.StatType", IsEnum = true,
            Value = JsonDocument.Parse("3").RootElement.Clone(),
        };
        var snap = MakeEnv(MakeClass("Game.Stat", Inst("0x1", enumF)));
        var r = RunOn(snap);
        Assert.Equal(0.85, ScoreOf(r, "_type").TypePrior, 3);
    }

    // ─── Name prior ────────────────────────────────────────────────────

    [Fact]
    public void Score_NamePrior_IdNameGroupGetLift()
    {
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_propertyID", 1), IntF("_itemGroup", 2), IntF("plain", 3))));
        var r = RunOn(snap);
        Assert.Equal(0.30, ScoreOf(r, "_propertyID").NamePrior, 3);  // "id"
        Assert.Equal(0.15, ScoreOf(r, "_itemGroup").NamePrior, 3);   // "group"
        Assert.Equal(0.0,  ScoreOf(r, "plain").NamePrior, 3);
    }

    // ─── Composite ordering ────────────────────────────────────────────

    [Fact]
    public void Score_StableUniqueIdField_TopsTheChart()
    {
        // _propertyID: stable, int, name lift. _value: changes (low
        // stability), int. _flag: bool. The id field should rank #1.
        var s1 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_propertyID", 101), IntF("_value", 10160), BoolF("_flag", true)),
            Inst("0x2", IntF("_propertyID", 201), IntF("_value", 1200),  BoolF("_flag", false))));
        var s2 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x3", IntF("_propertyID", 101), IntF("_value", 9910),  BoolF("_flag", true)),
            Inst("0x4", IntF("_propertyID", 201), IntF("_value", 1180),  BoolF("_flag", false))));
        var r = RunOn(s1, s2);
        Assert.Equal("_propertyID", r.Fields[0].FieldName);
    }

    // ─── Greedy suggested key ──────────────────────────────────────────

    [Fact]
    public void Suggest_UniqueSingleField_IsSuggestedAlone()
    {
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_id", 1), IntF("v", 10)),
            Inst("0x2", IntF("_id", 2), IntF("v", 20)),
            Inst("0x3", IntF("_id", 3), IntF("v", 30))));
        var r = RunOn(snap);
        Assert.Equal(new[] { "_id" }, r.SuggestedKey.ToArray());
        Assert.Contains("unique in every snapshot", r.SuggestedKeyReason);
    }

    [Fact]
    public void Suggest_CollidingSingleField_ProducesCompoundKey()
    {
        // Neither _itemGroup nor _property is unique on its own (each
        // value appears twice), but (_itemGroup, _property) is unique.
        // No single-field unique escape hatch here, so greedy must
        // build the compound key. (A unique-per-instance field would
        // correctly win as a lone key — that's covered separately.)
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_itemGroup", 1), IntF("_property", 101)),
            Inst("0x2", IntF("_itemGroup", 2), IntF("_property", 101)),
            Inst("0x3", IntF("_itemGroup", 1), IntF("_property", 102)),
            Inst("0x4", IntF("_itemGroup", 2), IntF("_property", 102))));
        var r = RunOn(snap);
        Assert.True(r.SuggestedKey.Count >= 2, "expected a compound key");
        Assert.Contains("_itemGroup", r.SuggestedKey);
        Assert.Contains("_property", r.SuggestedKey);
        Assert.Contains("unique in every snapshot", r.SuggestedKeyReason);
    }

    // ─── Address / native-pointer demotion ─────────────────────────────

    [Fact]
    public void Score_PointerField_IsDemoted_AndNotSuggestedAsKey()
    {
        // m_CachedPtr (System.IntPtr) is unique per instance + session-stable
        // — perfect cardinality, perfect stability — so the raw score would
        // crown it. _type collides (two instances share each value), so
        // without the address override the pointer outranks the real key.
        // The override must zero the pointer's TypePrior, sink its composite,
        // and keep it out of the suggested key.
        var s1 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", PtrF("m_CachedPtr", 0x1000), IntF("_type", 1), IntF("_value", 100)),
            Inst("0x2", PtrF("m_CachedPtr", 0x2000), IntF("_type", 1), IntF("_value", 200)),
            Inst("0x3", PtrF("m_CachedPtr", 0x3000), IntF("_type", 2), IntF("_value", 300)),
            Inst("0x4", PtrF("m_CachedPtr", 0x4000), IntF("_type", 2), IntF("_value", 400))));
        var s2 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", PtrF("m_CachedPtr", 0x1000), IntF("_type", 1), IntF("_value", 110)),
            Inst("0x2", PtrF("m_CachedPtr", 0x2000), IntF("_type", 1), IntF("_value", 210)),
            Inst("0x3", PtrF("m_CachedPtr", 0x3000), IntF("_type", 2), IntF("_value", 310)),
            Inst("0x4", PtrF("m_CachedPtr", 0x4000), IntF("_type", 2), IntF("_value", 410))));
        var r = RunOn(s1, s2);

        var ptr = ScoreOf(r, "m_CachedPtr");
        Assert.Equal(0.0, ptr.TypePrior, 3);
        Assert.True(ptr.Score < 0.15, $"pointer should be penalized, was {ptr.Score}");
        Assert.DoesNotContain("m_CachedPtr", r.SuggestedKey);
        Assert.True(ScoreOf(r, "_type").Score > ptr.Score,
            "a real key must outrank the demoted pointer");
    }

    [Fact]
    public void Score_NameEndingInPtr_IsTreatedAsAddress()
    {
        // Even when the reported type isn't IntPtr, a …Ptr name is a native
        // pointer by Unity convention → never a key.
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("nativePtr", 111), IntF("_id", 1)),
            Inst("0x2", IntF("nativePtr", 222), IntF("_id", 2))));
        var r = RunOn(snap);
        Assert.Equal(0.0, ScoreOf(r, "nativePtr").TypePrior, 3);
        Assert.DoesNotContain("nativePtr", r.SuggestedKey);
        Assert.Equal(new[] { "_id" }, r.SuggestedKey.ToArray());
    }

    [Fact]
    public void Suggest_UnresolvableCollision_ReportsRemaining()
    {
        // Two instances are identical on every field → no key set can
        // separate them. Reason should report the remaining collision.
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("a", 1), IntF("b", 2)),
            Inst("0x2", IntF("a", 1), IntF("b", 2))));
        var r = RunOn(snap);
        Assert.Contains("still share a key", r.SuggestedKeyReason);
    }

    // ─── 29i-2: element mode (array inner-field scoring) ────────────────

    [Fact]
    public void ElementMode_ScoresInnerFields_NotInstanceOrArrayField()
    {
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_ownerId", 1),
                RefArrayF("_arr",
                    new[] { IntF("_propertyID", 101), IntF("_amount", 5) },
                    new[] { IntF("_propertyID", 102), IntF("_amount", 7) }))));
        var r = RunElementOn("_arr", snap);

        Assert.Contains(r.Fields, f => f.FieldName == "_propertyID");
        Assert.Contains(r.Fields, f => f.FieldName == "_amount");
        // The owning instance's own field and the array field itself are
        // NOT element fields — element mode must not surface them.
        Assert.DoesNotContain(r.Fields, f => f.FieldName == "_ownerId");
        Assert.DoesNotContain(r.Fields, f => f.FieldName == "_arr");
    }

    [Fact]
    public void ElementMode_StableUniqueInnerKey_TopsAndIsSuggested()
    {
        // _propertyID = {101,102} every snap (stable + unique within the
        // 2-element set); _amount drifts. Element key should win.
        var s1 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", RefArrayF("_arr",
                new[] { IntF("_propertyID", 101), IntF("_amount", 5) },
                new[] { IntF("_propertyID", 102), IntF("_amount", 7) }))));
        var s2 = MakeEnv(MakeClass("Game.Stat",
            Inst("0x9", RefArrayF("_arr",
                new[] { IntF("_propertyID", 101), IntF("_amount", 50) },
                new[] { IntF("_propertyID", 102), IntF("_amount", 70) }))));
        var r = RunElementOn("_arr", s1, s2);

        Assert.Equal("_propertyID", r.Fields[0].FieldName);
        Assert.Equal(1.0, ScoreOf(r, "_propertyID").SStable, 3);
        Assert.Equal(1.0, ScoreOf(r, "_propertyID").SCardinality, 3);
        Assert.Equal(new[] { "_propertyID" }, r.SuggestedKey.ToArray());
    }

    [Fact]
    public void ElementMode_NoCapturedInnerFields_YieldsNoScores()
    {
        // Array present but elements have no inner Fields (Layer A only /
        // Layer B not enabled) → nothing to score.
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", RefArrayF("_arr",
                System.Array.Empty<SnapshotFieldEntry>(),
                System.Array.Empty<SnapshotFieldEntry>()))));
        var r = RunElementOn("_arr", snap);
        Assert.Empty(r.Fields);
    }

    [Fact]
    public void InstanceMode_Unchanged_WhenArrayFieldNull()
    {
        // Regression: with ArrayField null the engine scores instance
        // fields exactly as before (the array field collapses to a single
        // reference field, not its elements).
        var snap = MakeEnv(MakeClass("Game.Stat",
            Inst("0x1", IntF("_ownerId", 1),
                RefArrayF("_arr", new[] { IntF("_propertyID", 101) }))));
        var r = RunOn(snap);
        Assert.Contains(r.Fields, f => f.FieldName == "_ownerId");
        Assert.Contains(r.Fields, f => f.FieldName == "_arr");
        Assert.DoesNotContain(r.Fields, f => f.FieldName == "_propertyID");
    }
}
