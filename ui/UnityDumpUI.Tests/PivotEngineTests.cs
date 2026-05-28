// PivotEngineTests — Phase 29e-1.
//
// Covers the engine surface laid out in docs/class-pivot.md §5 + §10:
//   * Input validation throws
//   * Single-key / single-value happy path (the user's 活俠傳 GameStat case
//     in miniature)
//   * Compound key (_itemGroup, _propertyID)
//   * Collision flag + value rendering
//   * Absent cells when key tuple missing in some snaps
//   * Cross-session works (engine is address-agnostic)
//   * Assembly + FQN filter (instances in other classes ignored)
//   * KeyFilter substring filter
//   * Address per snap populated for singleton rows
//   * Address per snap = first instance address on collision
//   * Multiple value fields render in parallel
//   * Chunked snapshot — multiple SnapshotClassEntry with same FQN merge
//   * Missing key field renders "(missing)" sentinel

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class PivotEngineTests
{
    // ─── Fixture helpers ───────────────────────────────────────────────

    // Note: defaults BEFORE a `params` array are a C# footgun — the
    // compiler will NOT auto-skip the optional when binding positional
    // args, so the next positional collides with the default-having
    // parameter. We use overload pairs instead: the simple form uses
    // canonical defaults, and the explicit form lets specific tests
    // override sessionId / assembly.

    private static SnapshotEnvelope MakeEnv(
        string label,
        params SnapshotClassEntry[] classes)
        => MakeEnvWithSession(label, "s1", "b1", classes);

    private static SnapshotEnvelope MakeEnvWithSession(
        string label, string sessionId, string buildId,
        params SnapshotClassEntry[] classes)
    {
        return new SnapshotEnvelope
        {
            ServerId       = label,
            Label          = label,
            CapturedAt     = DateTime.UtcNow,
            CapturedAtUtc  = DateTime.UtcNow.ToString("o"),
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes        = classes.ToList(),
            ClassesLoaded  = true,
        };
    }

    private static SnapshotClassEntry MakeClass(
        string fqn,
        params SnapshotInstanceEntry[] instances)
        => MakeClassInAsm(fqn, "asm", instances);

    private static SnapshotClassEntry MakeClassInAsm(
        string fqn, string assembly,
        params SnapshotInstanceEntry[] instances)
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

    private static SnapshotInstanceEntry MakeInstance(
        string address,
        params (string name, string type, object? value)[] fields)
    {
        var inst = new SnapshotInstanceEntry { Address = address };
        foreach (var (name, type, value) in fields)
        {
            inst.Fields.Add(MakeField(name, type, value));
        }
        return inst;
    }

    /// <summary>Builds a SnapshotFieldEntry shaped to mirror what
    /// FormatFieldValue expects. Numbers → JsonElement; strings →
    /// StringValue; null → "(no value)".</summary>
    private static SnapshotFieldEntry MakeField(string name, string type, object? value)
    {
        var fe = new SnapshotFieldEntry { Name = name, Type = type };
        switch (value)
        {
            case null:
                break; // → "(no value)"
            case int i:
                fe.Value = JsonDocument.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                       .RootElement.Clone();
                break;
            case long l:
                fe.Value = JsonDocument.Parse(l.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                       .RootElement.Clone();
                break;
            case double d:
                fe.Value = JsonDocument.Parse(d.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                       .RootElement.Clone();
                break;
            case bool b:
                fe.Value = JsonDocument.Parse(b ? "true" : "false").RootElement.Clone();
                break;
            case string s:
                fe.StringValue = s;
                break;
            default:
                throw new ArgumentException($"Unhandled fixture value type: {value.GetType()}");
        }
        return fe;
    }

    // ─── Input validation ──────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PivotEngine.Run(new PivotQuery
        {
            Snapshots   = Array.Empty<SnapshotEnvelope>(),
            Assembly    = "asm",
            ClassFqn    = "Foo",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        }));
        Assert.Contains("at least one snapshot", ex.Message);
    }

    [Fact]
    public void Run_NoKeyFields_Throws()
    {
        var snap = MakeEnv("A");
        Assert.Throws<ArgumentException>(() => PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Foo",
            KeyFields   = Array.Empty<string>(),
            ValueFields = new[] { "v" },
        }));
    }

    [Fact]
    public void Run_NoValueFields_Throws()
    {
        var snap = MakeEnv("A");
        Assert.Throws<ArgumentException>(() => PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Foo",
            KeyFields   = new[] { "id" },
            ValueFields = Array.Empty<string>(),
        }));
    }

    [Fact]
    public void Run_EmptyAssembly_Throws()
    {
        var snap = MakeEnv("A");
        Assert.Throws<ArgumentException>(() => PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = string.Empty,
            ClassFqn    = "Foo",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        }));
    }

    // ─── Happy path: single key, single value, all snaps present ───────

    [Fact]
    public void Run_SingleKeySingleValue_TracksMoneyAcrossDriftingAddresses()
    {
        // 活俠傳-shaped: same logical GameStat (_propertyID=101) lives at
        // different addresses across snapshots. Pivot keyed by _propertyID
        // must track it through the address drift.
        var snap1 = MakeEnvWithSession("snap1", "S1", "b1",
            MakeClass("Mortal.Core.GameStat",
                MakeInstance("0x1f3a000", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32", 10160)),
                MakeInstance("0x1f3a020", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 1200))));
        var snap2 = MakeEnvWithSession("snap2", "S1", "b1",
            MakeClass("Mortal.Core.GameStat",
                MakeInstance("0x1f60a00", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 1180)),
                MakeInstance("0x1f60a20", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32", 9910))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2 },
            Assembly    = "asm",
            ClassFqn    = "Mortal.Core.GameStat",
            KeyFields   = new[] { "_propertyID" },
            ValueFields = new[] { "_value" },
        });

        Assert.Equal(2, rows.Count);
        var money = rows.Single(r => r.KeyTuple[0] == "101");
        Assert.False(money.HasCollision);
        Assert.Equal(new string?[] { "10160", "9910" }, money.Values[0].ToArray());
        Assert.Equal(new string?[] { "0x1f3a000", "0x1f60a20" }, money.AddressPerSnap.ToArray());
    }

    // ─── Compound key ──────────────────────────────────────────────────

    [Fact]
    public void Run_CompoundKey_TwoFieldsJointlyIdentify()
    {
        // (_itemGroup, _propertyID) — same _propertyID could collide across
        // groups, so the compound key disambiguates Money(Currency, 101) vs
        // Sword(Weapon, 101).
        var snap1 = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0xa1", ("_itemGroup", "string", "Currency"), ("_propertyID", "System.Int32", 101), ("_count", "System.Int32", 10160)),
                MakeInstance("0xa2", ("_itemGroup", "string", "Weapon"),   ("_propertyID", "System.Int32", 101), ("_count", "System.Int32",     1))));
        var snap2 = MakeEnv("snap2",
            MakeClass("Game.Item",
                MakeInstance("0xb1", ("_itemGroup", "string", "Currency"), ("_propertyID", "System.Int32", 101), ("_count", "System.Int32",  9410)),
                MakeInstance("0xb2", ("_itemGroup", "string", "Weapon"),   ("_propertyID", "System.Int32", 101), ("_count", "System.Int32",     2))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2 },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "_itemGroup", "_propertyID" },
            ValueFields = new[] { "_count" },
        });

        Assert.Equal(2, rows.Count);
        var currency = rows.Single(r => r.KeyTuple.SequenceEqual(new[] { "\"Currency\"", "101" }));
        var weapon   = rows.Single(r => r.KeyTuple.SequenceEqual(new[] { "\"Weapon\"",   "101" }));
        Assert.Equal(new string?[] { "10160", "9410" }, currency.Values[0].ToArray());
        Assert.Equal(new string?[] {     "1",    "2" }, weapon.Values[0].ToArray());
    }

    // ─── Collision ─────────────────────────────────────────────────────

    [Fact]
    public void Run_KeyCollision_FlagsRowAndRendersAllValues()
    {
        // Two instances with _propertyID=42 in snap2 → collision.
        var snap1 = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0x1", ("_propertyID", "System.Int32", 42), ("_count", "System.Int32", 5))));
        var snap2 = MakeEnv("snap2",
            MakeClass("Game.Item",
                MakeInstance("0x2", ("_propertyID", "System.Int32", 42), ("_count", "System.Int32", 7)),
                MakeInstance("0x3", ("_propertyID", "System.Int32", 42), ("_count", "System.Int32", 9))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2 },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "_propertyID" },
            ValueFields = new[] { "_count" },
        });

        var row = Assert.Single(rows);
        Assert.True(row.HasCollision);
        Assert.Equal("5", row.Values[0][0]);
        // Collision cell: instance count + distinct value set.
        Assert.Equal("⟨2: 7, 9⟩", row.Values[0][1]);
        Assert.Equal("0x1", row.AddressPerSnap[0]);
        Assert.Equal("0x2", row.AddressPerSnap[1]);  // spec §5: first address on collision
    }

    [Fact]
    public void Run_CollisionAllSameValue_RendersSingleValueWithCount()
    {
        // 5 instances share key=7 and ALL have _count=100. The cell
        // should collapse to "⟨5: 100⟩" — the key isn't unique but the
        // value agrees, so there's nothing to disambiguate value-wise.
        var insts = Enumerable.Range(0, 5)
            .Select(i => MakeInstance($"0x{i}", ("k", "System.Int32", 7), ("v", "System.Int32", 100)))
            .ToArray();
        var snap = MakeEnv("snap1", MakeClass("Game.Item", insts));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "k" },
            ValueFields = new[] { "v" },
        });

        var row = Assert.Single(rows);
        Assert.True(row.HasCollision);
        Assert.Equal("⟨5: 100⟩", row.Values[0][0]);
    }

    [Fact]
    public void Run_CollisionManyDistinct_CapsWithMoreSuffix()
    {
        // 10 instances, 10 distinct values → cell shows the first 6
        // distinct + "+4" suffix so it doesn't overflow the grid.
        var insts = Enumerable.Range(0, 10)
            .Select(i => MakeInstance($"0x{i}", ("k", "System.Int32", 7), ("v", "System.Int32", i * 10)))
            .ToArray();
        var snap = MakeEnv("snap1", MakeClass("Game.Item", insts));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "k" },
            ValueFields = new[] { "v" },
        });

        var row = Assert.Single(rows);
        Assert.True(row.HasCollision);
        // First 6 distinct (0,10,20,30,40,50) then "+4".
        Assert.Equal("⟨10: 0, 10, 20, 30, 40, 50,+4⟩", row.Values[0][0]);
    }

    // ─── Absent cells ──────────────────────────────────────────────────

    [Fact]
    public void Run_KeyAbsentInSomeSnap_NullCells()
    {
        // _propertyID=101 appears in snap1+snap3 but not snap2 — that
        // instance is missing mid-game (e.g. dropped item).
        var snap1 = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0x1", ("_propertyID", "System.Int32", 101), ("_count", "System.Int32", 5))));
        var snap2 = MakeEnv("snap2",
            MakeClass("Game.Item",
                MakeInstance("0x2", ("_propertyID", "System.Int32", 202), ("_count", "System.Int32", 1))));
        var snap3 = MakeEnv("snap3",
            MakeClass("Game.Item",
                MakeInstance("0x3", ("_propertyID", "System.Int32", 101), ("_count", "System.Int32", 7))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2, snap3 },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "_propertyID" },
            ValueFields = new[] { "_count" },
        });

        var id101 = rows.Single(r => r.KeyTuple[0] == "101");
        Assert.False(id101.HasCollision);
        Assert.Equal(new string?[] { "5", null, "7" }, id101.Values[0].ToArray());
        Assert.Equal(new string?[] { "0x1", null, "0x3" }, id101.AddressPerSnap.ToArray());
    }

    // ─── Cross-session — engine doesn't care about session_id ──────────

    [Fact]
    public void Run_CrossSession_WorksUnchanged()
    {
        // Different session_id → addresses don't align — but pivot is
        // address-agnostic, so the key tuple still matches.
        var snap1 = MakeEnvWithSession("snap1", "S_OLD", "b1",
            MakeClass("Game.Player",
                MakeInstance("0x1", ("name", "string", "Bob"), ("hp", "System.Int32", 100))));
        var snap2 = MakeEnvWithSession("snap2", "S_NEW", "b1",
            MakeClass("Game.Player",
                MakeInstance("0xff",("name", "string", "Bob"), ("hp", "System.Int32", 80))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2 },
            Assembly    = "asm",
            ClassFqn    = "Game.Player",
            KeyFields   = new[] { "name" },
            ValueFields = new[] { "hp"   },
        });

        var bob = Assert.Single(rows);
        Assert.Equal(new[] { "\"Bob\"" }, bob.KeyTuple);
        Assert.Equal(new string?[] { "100", "80" }, bob.Values[0].ToArray());
    }

    // ─── Filtering ─────────────────────────────────────────────────────

    [Fact]
    public void Run_AssemblyMismatch_IgnoresInstances()
    {
        var snap = MakeEnv("snap1",
            MakeClassInAsm("Game.Item", "asm-OTHER",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 99))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        });
        Assert.Empty(rows);
    }

    [Fact]
    public void Run_FqnMismatch_IgnoresInstances()
    {
        var snap = MakeEnv("snap1",
            MakeClass("Game.OtherItem",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 99))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        });
        Assert.Empty(rows);
    }

    [Fact]
    public void Run_KeyFilter_SubstringMatchIsCaseInsensitive()
    {
        var snap = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0x1", ("group", "string", "Currency"), ("v", "System.Int32", 1)),
                MakeInstance("0x2", ("group", "string", "Weapon"),   ("v", "System.Int32", 2)),
                MakeInstance("0x3", ("group", "string", "Armor"),    ("v", "System.Int32", 3))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "group" },
            ValueFields = new[] { "v"     },
            KeyFilter   = "WEAP",  // case-insensitive substring
        });

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "\"Weapon\"" }, row.KeyTuple);
    }

    // ─── Missing key field ─────────────────────────────────────────────

    [Fact]
    public void Run_MissingKeyField_RendersAsMissingSentinel()
    {
        // Instance lacks the "id" field entirely → key tuple gets the
        // "(missing)" sentinel rather than silent drop. Two such
        // instances collapse to one row keyed by "(missing)".
        var snap = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0x1", ("v", "System.Int32", 10)),
                MakeInstance("0x2", ("v", "System.Int32", 20))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        });

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "(missing)" }, row.KeyTuple);
        Assert.True(row.HasCollision);  // two instances collapsed to the (missing) bucket
    }

    // ─── Multiple value fields ─────────────────────────────────────────

    [Fact]
    public void Run_MultipleValueFields_ParallelTrajectories()
    {
        var snap1 = MakeEnv("snap1",
            MakeClass("Game.Player",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("hp", "System.Int32", 100), ("mp", "System.Int32", 30))));
        var snap2 = MakeEnv("snap2",
            MakeClass("Game.Player",
                MakeInstance("0x2", ("id", "System.Int32", 1), ("hp", "System.Int32",  80), ("mp", "System.Int32", 50))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap1, snap2 },
            Assembly    = "asm",
            ClassFqn    = "Game.Player",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "hp", "mp" },
        });

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Values.Count);  // 2 value fields
        Assert.Equal(new string?[] { "100", "80" }, row.Values[0].ToArray());  // hp
        Assert.Equal(new string?[] {  "30", "50" }, row.Values[1].ToArray());  // mp
    }

    // ─── Chunked class entries (walker emits multiple SnapshotClassEntry) ──

    [Fact]
    public void Run_ChunkedClassEntries_MergeInstances()
    {
        // Walker chunked the class across two SnapshotClassEntry — engine
        // must union their Instances rather than picking one.
        var snap = MakeEnv("snap1",
            MakeClass("Game.Item",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 11))),
            MakeClass("Game.Item",
                MakeInstance("0x2", ("id", "System.Int32", 2), ("v", "System.Int32", 22))));

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        });

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.KeyTuple[0] == "1" && r.Values[0][0] == "11");
        Assert.Contains(rows, r => r.KeyTuple[0] == "2" && r.Values[0][0] == "22");
    }

    // ─── Edge: instance has duplicate field name (defensive) ──────────

    [Fact]
    public void Run_DuplicateFieldName_FirstWins()
    {
        // Pathological defensive case — TryAdd discards the second entry.
        var inst = new SnapshotInstanceEntry { Address = "0x1" };
        inst.Fields.Add(MakeField("id", "System.Int32", 1));
        inst.Fields.Add(MakeField("v",  "System.Int32", 100));
        inst.Fields.Add(MakeField("v",  "System.Int32", 999));   // duplicate

        var cls = new SnapshotClassEntry { Assembly = "asm", Namespace = "Game", Name = "Item" };
        cls.Instances = new List<SnapshotInstanceEntry> { inst };
        var snap = MakeEnv("snap1", classes: new[] { cls });

        var rows = PivotEngine.Run(new PivotQuery
        {
            Snapshots   = new[] { snap },
            Assembly    = "asm",
            ClassFqn    = "Game.Item",
            KeyFields   = new[] { "id" },
            ValueFields = new[] { "v"  },
        });

        var row = Assert.Single(rows);
        Assert.Equal("100", row.Values[0][0]);   // first v wins, not 999
    }
}
