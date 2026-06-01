// SnapshotValueLocatorEngineTests — Phase 29i-4.
//
// Covers value-driven class location:
//   * exact match finds the right (class, field)
//   * rarer-match field ranks above a field where every instance equals it
//   * delta mode finds a field whose value set moved by Δ; ignores others
//   * Layer A primitive array element + Layer B inner element hits
//   * numeric tolerance
//   * cross-session (mixed session_id) — found regardless
//   * synthetic classes excluded by default
//   * validation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotValueLocatorEngineTests
{
    // ─── Fixtures ──────────────────────────────────────────────────────

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
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry FloatF(string name, double v)
        => new()
        {
            Name = name, Type = "System.Single",
            Value = JsonDocument.Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

    private static SnapshotFieldEntry StrF(string name, string v)
        => new() { Name = name, Type = "System.String", StringValue = v };

    private static SnapshotFieldEntry IntArrF(string name, params int[] vals)
    {
        var fe = new SnapshotFieldEntry
        {
            Name = name, Type = "System.Int32[]",
            ArrayElements = new SnapshotArrayElements
            {
                Length = (ulong)vals.Length, Count = vals.Length,
                ElementType = "System.Int32", Elements = new List<SnapshotArrayElement>(),
            },
        };
        for (int i = 0; i < vals.Length; i++)
            fe.ArrayElements.Elements.Add(new SnapshotArrayElement
            {
                Index = i,
                Value = JsonDocument.Parse(vals[i].ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
            });
        return fe;
    }

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

    private static ValueLocatorHit? Find(ValueLocatorResult r, string fqn, string field)
        => r.Hits.FirstOrDefault(h => h.ClassFqn == fqn && h.FieldName == field);

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        Assert.Throws<ArgumentException>(() => SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = Array.Empty<SnapshotEnvelope>(), ExactValue = "1",
        }));
    }

    [Fact]
    public void Run_ExactModeWithoutValue_Throws()
    {
        var snap = MakeEnv("s1", MakeClass("Game.A", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap },   // no ExactValue, no delta
        }));
    }

    // ─── Exact mode ────────────────────────────────────────────────────

    [Fact]
    public void Exact_FindsFieldCarryingValue()
    {
        var snap = MakeEnv("s1", MakeClass("Mortal.GameStat",
            Inst("0x1", IntF("_propertyID", 101), IntF("_value", 9410)),
            Inst("0x2", IntF("_propertyID", 201), IntF("_value", 1200))));
        var r = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410",
        });
        var hit = Find(r, "Mortal.GameStat", "_value");
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.SnapHitCount);
        Assert.Equal("0x1", hit.SampleAddress);
    }

    [Fact]
    public void Exact_RarerMatch_RanksAboveUbiquitousMatch()
    {
        // _money: 1 of 3 instances == 9410 (rare → strong). _cap: all 3 ==
        // 9410 (ubiquitous → weak). Rarer field must rank first.
        var snap = MakeEnv("s1", MakeClass("Game.Stat",
            Inst("0x1", IntF("_money", 9410), IntF("_cap", 9410)),
            Inst("0x2", IntF("_money", 10),   IntF("_cap", 9410)),
            Inst("0x3", IntF("_money", 20),   IntF("_cap", 9410))));
        var r = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410",
        });
        Assert.Equal("_money", r.Hits[0].FieldName);
        Assert.True(Find(r, "Game.Stat", "_money")!.MatchRarity <
                    Find(r, "Game.Stat", "_cap")!.MatchRarity);
    }

    [Fact]
    public void Exact_StringValue_MatchesRawOrRendered()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Item",
            Inst("0x1", StrF("_name", "Money"))));
        var rawHit = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "Money",
        });
        Assert.NotNull(Find(rawHit, "Game.Item", "_name"));
        var quotedHit = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "\"Money\"",
        });
        Assert.NotNull(Find(quotedHit, "Game.Item", "_name"));
    }

    [Fact]
    public void Exact_NumericTolerance_MatchesNearbyFloat()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stat", Inst("0x1", FloatF("_hp", 9410.3))));

        var noTol = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410",
        });
        Assert.Null(Find(noTol, "Game.Stat", "_hp"));   // 9410 != 9410.3

        var withTol = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410", NumericTolerance = 0.5,
        });
        Assert.NotNull(Find(withTol, "Game.Stat", "_hp"));
    }

    [Fact]
    public void Exact_CrossSession_FoundRegardless()
    {
        // Different session ids + different addresses — engine keys on values.
        var s1 = MakeEnv("sessA", MakeClass("Game.Stat", Inst("0x1000", IntF("_money", 9410))));
        var s2 = MakeEnv("sessB", MakeClass("Game.Stat", Inst("0xFFFF", IntF("_money", 9410))));
        var r = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { s1, s2 }, ExactValue = "9410",
        });
        Assert.Equal(2, Find(r, "Game.Stat", "_money")!.SnapHitCount);
    }

    [Fact]
    public void Exact_SyntheticClass_ExcludedByDefault_IncludedOnToggle()
    {
        var snap = MakeEnv("s1",
            MakeClass("Game.Real", Inst("0x1", IntF("v", 9410))),
            MakeClass("<>c__Hidden", Inst("0x2", IntF("v", 9410))));

        var def = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410",
        });
        Assert.NotNull(Find(def, "Game.Real", "v"));
        Assert.Null(Find(def, "<>c__Hidden", "v"));

        var inc = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410", IncludeSynthetic = true,
        });
        Assert.NotNull(Find(inc, "<>c__Hidden", "v"));
    }

    // ─── Array elements ────────────────────────────────────────────────

    [Fact]
    public void Exact_LayerAPrimitiveArrayElement_Hit()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Bag",
            Inst("0x1", IntArrF("_inv", 1, 2, 9410))));

        var off = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410",   // arrays off → no element sweep
        });
        Assert.Null(Find(off, "Game.Bag", "_inv[]"));

        var on = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410", IncludeArrayElements = true,
        });
        var hit = Find(on, "Game.Bag", "_inv[]");
        Assert.NotNull(hit);
        Assert.True(hit!.InArrayElement);
    }

    [Fact]
    public void Exact_LayerBInnerElementField_Hit()
    {
        var snap = MakeEnv("s1", MakeClass("Mortal.GameStat",
            Inst("0x1", RefArrayF("_arr",
                new[] { IntF("_propertyID", 101), IntF("_amount", 9410) },
                new[] { IntF("_propertyID", 102), IntF("_amount", 7) }))));
        var r = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap }, ExactValue = "9410", IncludeArrayElements = true,
        });
        var hit = Find(r, "Mortal.GameStat", "_arr[]._amount");
        Assert.NotNull(hit);
        Assert.True(hit!.InArrayElement);
    }

    // ─── Delta mode ────────────────────────────────────────────────────

    [Fact]
    public void Delta_FindsFieldWhoseSetMovedByDelta_IgnoresOthers()
    {
        // _money set moves 10160 → 9410 (Δ -750). _frame set 5 → 9 doesn't.
        var s1 = MakeEnv("s1", MakeClass("Game.Stat",
            Inst("a1", IntF("_money", 10160), IntF("_frame", 5)),
            Inst("a2", IntF("_money", 1200),  IntF("_frame", 5))));
        var s2 = MakeEnv("s1", MakeClass("Game.Stat",
            Inst("b1", IntF("_money", 9410),  IntF("_frame", 9)),
            Inst("b2", IntF("_money", 1200),  IntF("_frame", 9))));

        var r = SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { s1, s2 }, SnapA = 0, SnapB = 1, Delta = -750,
        });
        var money = Find(r, "Game.Stat", "_money");
        Assert.NotNull(money);
        Assert.Equal("10160 → 9410", money!.SampleValue);
        Assert.Null(Find(r, "Game.Stat", "_frame"));
    }

    [Fact]
    public void Delta_SameSnapIndices_Throws()
    {
        var snap = MakeEnv("s1", MakeClass("Game.Stat", Inst("0x1", IntF("v", 1))));
        Assert.Throws<ArgumentException>(() => SnapshotValueLocatorEngine.Run(new ValueLocatorQuery
        {
            Snapshots = new[] { snap, snap }, SnapA = 0, SnapB = 0, Delta = 1,
        }));
    }
}
