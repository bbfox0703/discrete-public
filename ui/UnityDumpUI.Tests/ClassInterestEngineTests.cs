// ClassInterestEngineTests — Phase 29i-3.
//
// Covers the unsupervised "likely game-state" ranking:
//   * a stable-key + drifting-value class ranks above a constant class
//   * volatility is 0 for a constant value field, >0 for a drifting one
//   * interestingness = bestKeyScore * maxVolatility
//   * top value fields surface the drifting field
//   * validation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ClassInterestEngineTests
{
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

    private static ClassInterestCandidate Cand(string fqn)
        => new() { Assembly = "asm", ClassFqn = fqn };

    private static ClassInterestRow RowFor(List<ClassInterestRow> rows, string fqn)
        => rows.Single(r => r.ClassFqn == fqn);

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptySnapshots_Throws()
    {
        Assert.Throws<ArgumentException>(() => ClassInterestEngine.Run(new ClassInterestQuery
        {
            Snapshots = Array.Empty<SnapshotEnvelope>(),
            Candidates = new[] { Cand("X") },
        }));
    }

    // ─── Ranking ───────────────────────────────────────────────────────

    [Fact]
    public void Run_DriftingValueClass_RanksAboveConstantClass()
    {
        // Game.Money: _id is a stable unique key, _money drifts per key.
        // Game.Const: _id stable unique key, _const never changes.
        var s1 = MakeEnv(
            MakeClass("Game.Money",
                Inst("0x1", IntF("_id", 1), IntF("_money", 100)),
                Inst("0x2", IntF("_id", 2), IntF("_money", 200))),
            MakeClass("Game.Const",
                Inst("0xC1", IntF("_id", 1), IntF("_const", 9)),
                Inst("0xC2", IntF("_id", 2), IntF("_const", 9))));
        var s2 = MakeEnv(
            MakeClass("Game.Money",
                Inst("0x3", IntF("_id", 1), IntF("_money", 150)),
                Inst("0x4", IntF("_id", 2), IntF("_money", 260))),
            MakeClass("Game.Const",
                Inst("0xC3", IntF("_id", 1), IntF("_const", 9)),
                Inst("0xC4", IntF("_id", 2), IntF("_const", 9))));

        var rows = ClassInterestEngine.Run(new ClassInterestQuery
        {
            Snapshots = new[] { s1, s2 },
            Candidates = new[] { Cand("Game.Money"), Cand("Game.Const") },
        });

        var money = RowFor(rows, "Game.Money");
        var konst = RowFor(rows, "Game.Const");

        Assert.True(money.MaxVolatility > 0.99, $"money should drift, vol={money.MaxVolatility}");
        Assert.Equal(0.0, konst.MaxVolatility, 5);          // constant → no drift
        Assert.True(money.InterestScore > konst.InterestScore);
        Assert.Equal("Game.Money", rows[0].ClassFqn);       // sorted interest desc
        Assert.Contains("_money", money.TopValueFieldsList);
        Assert.Equal(money.BestKeyScore * money.MaxVolatility, money.InterestScore, 6);
    }

    [Fact]
    public void Run_ConstantClass_HasZeroInterest()
    {
        var s1 = MakeEnv(MakeClass("Game.Const",
            Inst("0x1", IntF("_id", 1), IntF("_const", 9)),
            Inst("0x2", IntF("_id", 2), IntF("_const", 9))));
        var s2 = MakeEnv(MakeClass("Game.Const",
            Inst("0x3", IntF("_id", 1), IntF("_const", 9)),
            Inst("0x4", IntF("_id", 2), IntF("_const", 9))));

        var rows = ClassInterestEngine.Run(new ClassInterestQuery
        {
            Snapshots = new[] { s1, s2 }, Candidates = new[] { Cand("Game.Const") },
        });
        var konst = RowFor(rows, "Game.Const");
        Assert.Equal(0.0, konst.MaxVolatility, 5);
        Assert.Equal(0.0, konst.InterestScore, 5);
        Assert.Empty(konst.TopValueFieldsList);   // no drifting field to surface
    }

    [Fact]
    public void Run_PartialDrift_VolatilityIsFraction()
    {
        // Two keys; only key 1's _hp changes across snaps, key 2 is steady.
        // vol(_hp) = 1 of 2 multi-snap keys changed = 0.5.
        var s1 = MakeEnv(MakeClass("Game.Unit",
            Inst("0x1", IntF("_id", 1), IntF("_hp", 100)),
            Inst("0x2", IntF("_id", 2), IntF("_hp", 50))));
        var s2 = MakeEnv(MakeClass("Game.Unit",
            Inst("0x3", IntF("_id", 1), IntF("_hp", 80)),
            Inst("0x4", IntF("_id", 2), IntF("_hp", 50))));

        var rows = ClassInterestEngine.Run(new ClassInterestQuery
        {
            Snapshots = new[] { s1, s2 }, Candidates = new[] { Cand("Game.Unit") },
        });
        Assert.Equal(0.5, RowFor(rows, "Game.Unit").MaxVolatility, 5);
    }
}
