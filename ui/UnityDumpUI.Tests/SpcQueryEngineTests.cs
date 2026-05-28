// SpcQueryEngineTests — Phase 29d-1.
//
// Detailed coverage as requested:
//   * Every predicate kind (absolute + relative)
//   * Per-snap predicate alignment (mismatched count throws)
//   * Cross-session: instance-keyed rows drop when StaticsOnly set
//   * Statics-only / synthetic / type / class filters
//   * NumericOnly: textual rows pass when off, drop when on
//   * Mixed predicate chain — the user's "10160 / 9910 / 9410 / 9410"
//     killer use case literally encoded
//   * Edge: empty snapshots / single snapshot / Range with one bound
//   * First-predicate-relative rejection
//   * Anchor picking — smallest FieldIndex chosen so candidate walk
//     is bounded by the narrowest snapshot

using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SpcQueryEngineTests
{
    // ─── Helpers ───────────────────────────────────────────────────────

    private static SnapshotEnvelope MakeEnv(
        string buildId, string sessionId, string label,
        (string fqn, string fieldName, string value, bool isStatic, string fieldType)[] rows,
        bool synthetic = false)
    {
        var env = new SnapshotEnvelope
        {
            ServerId       = label,
            Label          = label,
            CapturedAt     = System.DateTime.UtcNow,
            CapturedAtUtc  = System.DateTime.UtcNow.ToString("o"),
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = rows.Length,
            TotalInstances = rows.Length,
            Classes        = new List<SnapshotClassEntry>(),
            ClassesLoaded  = true,
        };
        // Hand-build FieldIndex (skip BuildFieldIndex to keep test fixtures predictable).
        var idx = new Dictionary<string, SnapshotFieldValue>();
        foreach (var (fqn, fieldName, value, isStatic, fieldType) in rows)
        {
            string instance = isStatic ? string.Empty : "0xCAFE0000";
            string key = $"asm|{fqn}|{(isStatic ? "static" : instance)}|0|{fieldName}";
            idx[key] = new SnapshotFieldValue
            {
                Assembly        = "asm",
                ClassFqn        = fqn,
                ClassName       = fqn,
                Namespace       = string.Empty,
                InstanceAddress = instance,
                IsStatic        = isStatic,
                FieldName       = fieldName,
                FieldType       = fieldType,
                FieldOffset     = 0,
                ValueDisplay    = value,
                IsSynthetic     = synthetic,
            };
        }
        InjectFieldIndex(env, idx);
        return env;
    }

    private static void InjectFieldIndex(SnapshotEnvelope env, Dictionary<string, SnapshotFieldValue> idx)
    {
        // Phase 29g-2: FieldIndex is now lazy (no public setter). Use the
        // dedicated test seam to inject a controlled fixture dict directly.
        env.SetFieldIndexForTest(idx);
    }

    // ─── Predicate-count alignment ─────────────────────────────────────

    [Fact]
    public void Run_PredicateCountMustMatchSnapshotCount()
    {
        var snap = MakeEnv("b1", "s1", "A", new[] { ("Foo", "x", "1", true, "System.Int32") });
        var ex = Assert.Throws<System.ArgumentException>(() => SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap, snap },
            Predicates = new[] { SpcPredicate.Any },
        }));
        Assert.Contains("Predicate count", ex.Message);
    }

    [Fact]
    public void Run_FirstPredicateCannotBeRelative()
    {
        var snap = MakeEnv("b1", "s1", "A", new[] { ("Foo", "x", "1", true, "System.Int32") });
        Assert.Throws<System.ArgumentException>(() => SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Increased },
        }));
    }

    [Fact]
    public void Run_EmptySnapshotsThrows()
    {
        Assert.Throws<System.ArgumentException>(() => SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = System.Array.Empty<SnapshotEnvelope>(),
            Predicates = System.Array.Empty<SpcPredicate>(),
        }));
    }

    // ─── Predicate semantics ───────────────────────────────────────────

    [Fact]
    public void Predicate_AnyMatchesEveryRow()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "x", "10160", true, "System.Int32"),
            ("Foo", "y", "9999",  true, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Any },
        });
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Predicate_ExactMatchesOneRow()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "x", "10160", true, "System.Int32"),
            ("Foo", "y", "9999",  true, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Exact(10160) },
        });
        Assert.Single(matches);
        Assert.Equal("x", matches[0].FieldName);
    }

    [Fact]
    public void Predicate_RangeBoundedBothSides()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "below",  "50",  true, "System.Int32"),
            ("Foo", "in1",    "100", true, "System.Int32"),
            ("Foo", "in2",    "999", true, "System.Int32"),
            ("Foo", "above",  "1500", true, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Range(100, 1000) },
        });
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.FieldName == "in1");
        Assert.Contains(matches, m => m.FieldName == "in2");
    }

    [Fact]
    public void Predicate_RangeBoundedOneSideOnly()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "low",  "50",   true, "System.Int32"),
            ("Foo", "mid",  "500",  true, "System.Int32"),
            ("Foo", "high", "9999", true, "System.Int32"),
        });
        var minOnly = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Range(100, null) },
        });
        Assert.Equal(2, minOnly.Count);
        Assert.DoesNotContain(minOnly, m => m.FieldName == "low");

        var maxOnly = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Range(null, 1000) },
        });
        Assert.Equal(2, maxOnly.Count);
        Assert.DoesNotContain(maxOnly, m => m.FieldName == "high");
    }

    [Fact]
    public void Predicate_NotEqualExcludesMatchingValue()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "x", "10160", true, "System.Int32"),
            ("Foo", "y", "9999",  true, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.NotEqual(10160) },
        });
        Assert.Single(matches);
        Assert.Equal("y", matches[0].FieldName);
    }

    [Fact]
    public void Predicate_UnchangedHoldsEvenForNonNumericFields()
    {
        // "Unchanged" uses textual equality so it works on string /
        // pointer / error rows that don't parse as double.
        var a = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "name", "\"Bob\"", true, "System.String"),
        });
        var b = MakeEnv("b1", "s1", "B", new[]
        {
            ("Foo", "name", "\"Bob\"", true, "System.String"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { a, b },
            Predicates  = new[] { SpcPredicate.Any, SpcPredicate.Unchanged },
            NumericOnly = false,
        });
        Assert.Single(matches);
    }

    [Fact]
    public void Predicate_IncreasedRequiresPrevValue()
    {
        var a = MakeEnv("b1", "s1", "A", new[] { ("Foo", "x", "100", true, "System.Int32") });
        var b = MakeEnv("b1", "s1", "B", new[] { ("Foo", "x", "200", true, "System.Int32") });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b },
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.Increased },
        });
        Assert.Single(matches);
    }

    [Fact]
    public void Predicate_DecreasedSkipsEqualOrIncreased()
    {
        var a = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "drop",   "100", true, "System.Int32"),
            ("Foo", "same",   "50",  true, "System.Int32"),
            ("Foo", "growth", "10",  true, "System.Int32"),
        });
        var b = MakeEnv("b1", "s1", "B", new[]
        {
            ("Foo", "drop",   "75",  true, "System.Int32"),  // ↓
            ("Foo", "same",   "50",  true, "System.Int32"),  // =
            ("Foo", "growth", "20",  true, "System.Int32"),  // ↑
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b },
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.Decreased },
        });
        Assert.Single(matches);
        Assert.Equal("drop", matches[0].FieldName);
    }

    [Fact]
    public void Predicate_DeltaExactMatchesOnlyExactDifference()
    {
        var a = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "x", "10160", true, "System.Int32"),
            ("Foo", "y", "5000",  true, "System.Int32"),
        });
        var b = MakeEnv("b1", "s1", "B", new[]
        {
            ("Foo", "x", "9910",  true, "System.Int32"),  // Δ -250 ✓
            ("Foo", "y", "5050",  true, "System.Int32"),  // Δ +50  ✗
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b },
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.DeltaExact(-250) },
        });
        Assert.Single(matches);
        Assert.Equal("x", matches[0].FieldName);
    }

    [Fact]
    public void Predicate_DeltaRangeMatchesWindow()
    {
        var a = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "tiny", "100", true, "System.Int32"),
            ("Foo", "mid",  "100", true, "System.Int32"),
            ("Foo", "huge", "100", true, "System.Int32"),
        });
        var b = MakeEnv("b1", "s1", "B", new[]
        {
            ("Foo", "tiny", "98",  true, "System.Int32"),  // Δ -2
            ("Foo", "mid",  "50",  true, "System.Int32"),  // Δ -50 ✓
            ("Foo", "huge", "1",   true, "System.Int32"),  // Δ -99
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b },
            // "Decreased by 10..70"
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.DeltaRange(-70, -10) },
        });
        Assert.Single(matches);
        Assert.Equal("mid", matches[0].FieldName);
    }

    // ─── The killer SPC use case ───────────────────────────────────────

    [Fact]
    public void KillerUseCase_FourStepValueSignatureLocatesTargetField()
    {
        // User's verbatim example: identify the 銀兩 GameStat across 4
        // sessions by its value sequence 10160 → 9910 → 9410 → 9410
        // (bought 250, bought 500, slept-no-change).

        (string, string, string, bool, string)[] noiseRows =
        {
            // Plausible-looking other fields that fail at least one step.
            ("Mortal.Player",       "level",   "12",    true, "System.Int32"),
            ("Mortal.Player",       "xp",      "9999",  true, "System.Int32"),
            ("Mortal.Inventory",    "slots",   "30",    true, "System.Int32"),
            ("Mortal.Core.GameStat","_value",  "10160", true, "System.Int32"),  // target
        };
        var sA = MakeEnv("b1", "s1", "before-quest", noiseRows);
        var sB = MakeEnv("b1", "s1", "after-shop1", new[]
        {
            ("Mortal.Player",       "level",   "12",    true, "System.Int32"),
            ("Mortal.Player",       "xp",      "10500", true, "System.Int32"),  // grew
            ("Mortal.Inventory",    "slots",   "30",    true, "System.Int32"),
            ("Mortal.Core.GameStat","_value",  "9910",  true, "System.Int32"),  // ↓ 250
        });
        var sC = MakeEnv("b1", "s1", "after-shop2", new[]
        {
            ("Mortal.Player",       "level",   "12",    true, "System.Int32"),
            ("Mortal.Player",       "xp",      "11000", true, "System.Int32"),
            ("Mortal.Inventory",    "slots",   "30",    true, "System.Int32"),
            ("Mortal.Core.GameStat","_value",  "9410",  true, "System.Int32"),  // ↓ 500
        });
        var sD = MakeEnv("b1", "s1", "after-sleep", new[]
        {
            ("Mortal.Player",       "level",   "13",    true, "System.Int32"),  // levelled up
            ("Mortal.Player",       "xp",      "0",     true, "System.Int32"),
            ("Mortal.Inventory",    "slots",   "30",    true, "System.Int32"),
            ("Mortal.Core.GameStat","_value",  "9410",  true, "System.Int32"),  // unchanged
        });

        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { sA, sB, sC, sD },
            Predicates = new[]
            {
                SpcPredicate.Exact(10160),
                SpcPredicate.DeltaExact(-250),
                SpcPredicate.DeltaExact(-500),
                SpcPredicate.Unchanged,
            },
            StaticsOnly = true,
        });
        Assert.Single(matches);
        Assert.Equal("Mortal.Core.GameStat", matches[0].ClassFqn);
        Assert.Equal("_value", matches[0].FieldName);
        Assert.Equal(new[] { "10160", "9910", "9410", "9410" },
            matches[0].ValueDisplays);
    }

    // ─── Filters ───────────────────────────────────────────────────────

    [Fact]
    public void StaticsOnly_DropsInstanceRows()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "kStat",  "1", true,  "System.Int32"),
            ("Foo", "instX",  "1", false, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { snap },
            Predicates  = new[] { SpcPredicate.Any },
            StaticsOnly = true,
        });
        Assert.Single(matches);
        Assert.True(matches[0].IsStatic);
    }

    [Fact]
    public void ExcludeSynthetic_DropsSyntheticRows()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("<>c", "kept", "1", true, "System.Int32"),
        }, synthetic: true);
        var matchesDefault = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Any },
        });
        Assert.Empty(matchesDefault);

        var matchesOff = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots        = new[] { snap },
            Predicates       = new[] { SpcPredicate.Any },
            ExcludeSynthetic = false,
        });
        Assert.Single(matchesOff);
    }

    [Fact]
    public void TypeFilter_NarrowsByFieldTypeSubstring()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "i32",  "1",   true, "System.Int32"),
            ("Foo", "f32",  "1.5", true, "System.Single"),
            ("Foo", "i64",  "1",   true, "System.Int64"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { snap },
            Predicates = new[] { SpcPredicate.Any },
            TypeFilter = "Int",
        });
        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Contains("Int", m.FieldType));
    }

    [Fact]
    public void ClassFilter_NarrowsByFqnSubstring()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Mortal.Core.GameStat", "_value", "1", true, "System.Int32"),
            ("Mortal.Player",        "level",  "1", true, "System.Int32"),
            ("Unity.Scene",          "name",   "1", true, "System.Int32"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { snap },
            Predicates  = new[] { SpcPredicate.Any },
            ClassFilter = "Mortal",
        });
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void NumericOnly_True_ExcludesTextualRows()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "n", "100",     true, "System.Int32"),
            ("Foo", "s", "\"text\"", true, "System.String"),
        });
        var matchesNumeric = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { snap },
            Predicates  = new[] { SpcPredicate.Any },
            NumericOnly = true,
        });
        Assert.Single(matchesNumeric);
        Assert.Equal("n", matchesNumeric[0].FieldName);
    }

    [Fact]
    public void NumericOnly_False_KeepsTextualRows()
    {
        var snap = MakeEnv("b1", "s1", "A", new[]
        {
            ("Foo", "n", "100",     true, "System.Int32"),
            ("Foo", "s", "\"text\"", true, "System.String"),
        });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { snap },
            Predicates  = new[] { SpcPredicate.Any },
            NumericOnly = false,
        });
        Assert.Equal(2, matches.Count);
    }

    // ─── Intersection / cross-session ──────────────────────────────────

    [Fact]
    public void CrossSession_InstanceKeysCollapseUnlessStaticsOnly()
    {
        // Same class, same fieldName, but different instance addresses
        // → cross-session run produces different key shapes for the
        // instance row. Without StaticsOnly, the intersection drops it
        // entirely. Static row stays since its key path doesn't carry
        // the changing instance addr.

        // Hand-build envelopes to control the instance addresses
        // independently (the MakeEnv helper hardcodes 0xCAFE0000).
        var idxA = new Dictionary<string, SnapshotFieldValue>();
        var idxB = new Dictionary<string, SnapshotFieldValue>();
        var statKey = "asm|Foo|static|0|kStatic";
        var instKeyA = "asm|Foo|0x1000|0|inst";
        var instKeyB = "asm|Foo|0x2000|0|inst";  // different addr in session B
        idxA[statKey]  = MakeFv(true,  string.Empty,  "Foo", "kStatic", "10");
        idxA[instKeyA] = MakeFv(false, "0x1000",      "Foo", "inst",    "20");
        idxB[statKey]  = MakeFv(true,  string.Empty,  "Foo", "kStatic", "10");
        idxB[instKeyB] = MakeFv(false, "0x2000",      "Foo", "inst",    "21");
        var a = NewEnv("b1", "sX", "A", idxA);
        var b = NewEnv("b1", "sY", "B", idxB);

        // Without StaticsOnly: only the static key matches across both.
        // Instance key drops because intersection is empty.
        var loose = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b },
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.Any },
        });
        Assert.Single(loose);
        Assert.True(loose[0].IsStatic);

        // With StaticsOnly: same result, but explicitly stated.
        var strict = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots   = new[] { a, b },
            Predicates  = new[] { SpcPredicate.Any, SpcPredicate.Any },
            StaticsOnly = true,
        });
        Assert.Single(strict);
        Assert.True(strict[0].IsStatic);
    }

    private static SnapshotFieldValue MakeFv(
        bool isStatic, string addr, string fqn, string fieldName, string value)
        => new()
        {
            Assembly        = "asm",
            ClassFqn        = fqn,
            ClassName       = fqn,
            Namespace       = string.Empty,
            InstanceAddress = addr,
            IsStatic        = isStatic,
            FieldName       = fieldName,
            FieldType       = "System.Int32",
            FieldOffset     = 0,
            ValueDisplay    = value,
        };

    private static SnapshotEnvelope NewEnv(
        string buildId, string sessionId, string label,
        Dictionary<string, SnapshotFieldValue> idx)
    {
        var env = new SnapshotEnvelope
        {
            ServerId       = label,
            Label          = label,
            CapturedAt     = System.DateTime.UtcNow,
            CapturedAtUtc  = System.DateTime.UtcNow.ToString("o"),
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = idx.Count,
            TotalInstances = idx.Count,
            Classes        = new List<SnapshotClassEntry>(),
            ClassesLoaded  = true,
        };
        InjectFieldIndex(env, idx);
        return env;
    }

    // ─── Result shape ──────────────────────────────────────────────────

    [Fact]
    public void Match_CarriesPerSnapshotValueSequenceInOrder()
    {
        var a = MakeEnv("b1", "s1", "A", new[] { ("Foo", "x", "100", true, "System.Int32") });
        var b = MakeEnv("b1", "s1", "B", new[] { ("Foo", "x", "200", true, "System.Int32") });
        var c = MakeEnv("b1", "s1", "C", new[] { ("Foo", "x", "300", true, "System.Int32") });
        var matches = SpcQueryEngine.Run(new SpcQuery
        {
            Snapshots  = new[] { a, b, c },
            Predicates = new[] { SpcPredicate.Any, SpcPredicate.Increased, SpcPredicate.Increased },
        });
        var m = Assert.Single(matches);
        Assert.Equal(new[] { "100", "200", "300" }, m.ValueDisplays);
        Assert.Equal(new double?[] { 100.0, 200.0, 300.0 }, m.NumericValues);
    }
}
