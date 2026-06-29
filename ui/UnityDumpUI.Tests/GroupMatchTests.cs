// GroupMatchTests — G1 (group-scan SDR matcher, C# twin of dll/src/shared/GroupMatch.h).
//
// Covers the source-agnostic core that the Snapshot / SPC group surfaces reuse:
//   * the SDR / Kuhn assignment (distinctness, duplicate-value, empty-slot reject)
//   * Assignment() returns DISTINCT representatives (not a shared first match)
//   * the width-scope gate (NumericNoByte excludes 1-byte)
//   * the width-fit guard (70000 cannot match an Int16 field)
//   * every absolute + relative predicate
//   * end-to-end Run() group scenarios

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class GroupMatchTests
{
    // ---- builders ----

    private static GroupMatch.Leaf L(int offset, string type, params double[] nums)
        => new()
        {
            Offset = offset,
            FieldType = type,
            Num = nums.Select(n => (double?)n).ToList(),
            Display = nums.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList(),
        };

    private static GroupMatch.Leaf LDisplay(int offset, string type, params string[] displays)
        => new()
        {
            Offset = offset,
            FieldType = type,
            Display = displays.ToList(),
            Num = displays.Select(GroupMatch.TryParseNumeric).ToList(),
        };

    private static GroupMatch.Slot Exact(double v, GroupMatch.Scope scope = GroupMatch.Scope.NumericNoByte)
        => new() { Scope = scope, Predicate = GroupMatch.Predicate.Exact, Target = v };

    private static List<int>[] Lists(params int[][] rows) => rows.Select(r => r.ToList()).ToArray();

    // ---- SDR core ----

    [Fact]
    public void HasDistinctAssignment_TwoSlotsDistinctLeaves_True()
    {
        // slot0 -> {0}, slot1 -> {1}
        Assert.True(GroupMatch.HasDistinctAssignment(Lists(new[] { 0 }, new[] { 1 }), 2));
    }

    [Fact]
    public void HasDistinctAssignment_TwoSlotsShareOnlyLeaf_False()
    {
        // Both slots can only take leaf 0 — no SDR.
        Assert.False(GroupMatch.HasDistinctAssignment(Lists(new[] { 0 }, new[] { 0 }), 1));
    }

    [Fact]
    public void HasDistinctAssignment_DuplicateValueNeedsTwoLeaves_True()
    {
        // "Str=24, Def=24": both slots match leaves {0,1}; SDR assigns one each.
        Assert.True(GroupMatch.HasDistinctAssignment(Lists(new[] { 0, 1 }, new[] { 0, 1 }), 2));
    }

    [Fact]
    public void HasDistinctAssignment_EmptySlot_False()
    {
        Assert.False(GroupMatch.HasDistinctAssignment(Lists(new[] { 0 }, System.Array.Empty<int>()), 2));
    }

    [Fact]
    public void HasDistinctAssignment_AugmentingPath_True()
    {
        // slot0 -> {0,1}, slot1 -> {0}, slot2 -> {1,2}. A naive greedy that gives
        // slot0=0 then fails slot1 must augment: slot1 takes 0, slot0 takes 1, slot2 takes 2.
        Assert.True(GroupMatch.HasDistinctAssignment(
            Lists(new[] { 0, 1 }, new[] { 0 }, new[] { 1, 2 }), 3));
    }

    [Fact]
    public void Assignment_ReturnsDistinctRepresentatives()
    {
        // Both slots match {0,1}; Assignment must give two DIFFERENT leaves.
        var asg = GroupMatch.Assignment(Lists(new[] { 0, 1 }, new[] { 0, 1 }), 2);
        Assert.NotNull(asg);
        Assert.Equal(2, asg!.Length);
        Assert.NotEqual(asg[0], asg[1]);
        Assert.Contains(asg[0], new[] { 0, 1 });
        Assert.Contains(asg[1], new[] { 0, 1 });
    }

    [Fact]
    public void Assignment_NoSdr_ReturnsNull()
    {
        Assert.Null(GroupMatch.Assignment(Lists(new[] { 0 }, new[] { 0 }), 1));
    }

    // ---- scope gate ----

    [Theory]
    [InlineData("System.Int32", true, true)]
    [InlineData("System.Single", true, true)]
    [InlineData("System.Byte", false, true)]   // NoByte excludes; All includes
    [InlineData("System.SByte", false, true)]
    [InlineData("System.Int16", true, true)]
    [InlineData("System.String", false, false)] // non-numeric never in scope
    [InlineData("MyGame.Faction", false, false)] // enum FQN — not in numeric set
    public void TypeInScope_Matrix(string type, bool noByte, bool all)
    {
        Assert.Equal(noByte, GroupMatch.TypeInScope(type, GroupMatch.Scope.NumericNoByte));
        Assert.Equal(all, GroupMatch.TypeInScope(type, GroupMatch.Scope.NumericAll));
    }

    // ---- width-fit guard ----

    [Fact]
    public void LeafSatisfiesSlot_TargetTooBigForInt16_Rejected()
    {
        // 70000 cannot be an Int16 value, even if a (corrupt) display said so.
        var leaf = L(0, "System.Int16", 70000);
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf, Exact(70000)));
    }

    [Fact]
    public void LeafSatisfiesSlot_FractionalTargetOnIntField_Rejected()
    {
        var leaf = L(0, "System.Int32", 24);
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf, Exact(24.5)));
    }

    [Fact]
    public void LeafSatisfiesSlot_FractionalTargetOnFloatField_Allowed()
    {
        var leaf = L(0, "System.Single", 24.5);
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf, Exact(24.5, GroupMatch.Scope.NumericNoByte)));
    }

    // ---- absolute predicates (evaluate newest = last) ----

    [Fact]
    public void Exact_OnNewestValue()
    {
        var leaf = L(0, "System.Int32", 10, 24); // oldest 10, newest 24
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf, Exact(24)));
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf, Exact(10)));
    }

    [Fact]
    public void BiggerSmallerBetween()
    {
        var leaf = L(0, "System.Int32", 50);
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Bigger, Target = 40 }));
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Bigger, Target = 60 }));
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Smaller, Target = 60 }));
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Between, Target = 40, Target2 = 60 }));
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Between, Target = 10, Target2 = 20 }));
    }

    // ---- relative predicates (first-vs-last; need >= 2) ----

    [Fact]
    public void Relative_SingleSnapshot_NoBaseline_Rejected()
    {
        var leaf = L(0, "System.Int32", 24);
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Increased }));
        Assert.False(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Unchanged }));
    }

    [Fact]
    public void IncreasedDecreasedChangedUnchanged()
    {
        var up = L(0, "System.Int32", 10, 24);
        var down = L(0, "System.Int32", 24, 10);
        var same = L(0, "System.Int32", 24, 24);
        Assert.True(GroupMatch.LeafSatisfiesSlot(up, new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Increased }));
        Assert.True(GroupMatch.LeafSatisfiesSlot(down, new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Decreased }));
        Assert.True(GroupMatch.LeafSatisfiesSlot(up, new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Changed }));
        Assert.True(GroupMatch.LeafSatisfiesSlot(same, new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Unchanged }));
        Assert.False(GroupMatch.LeafSatisfiesSlot(same, new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Changed }));
    }

    [Fact]
    public void Unchanged_TextualEquality_NonNumeric()
    {
        // A non-numeric display that is byte-identical across snapshots reads as Unchanged.
        var leaf = LDisplay(0, "System.Int32", "0x1A2B", "0x1A2B");
        Assert.True(GroupMatch.LeafSatisfiesSlot(leaf,
            new GroupMatch.Slot { Predicate = GroupMatch.Predicate.Unchanged }));
    }

    // ---- end-to-end Run() ----

    [Fact]
    public void Run_ObjectHoldsAllThreeAtDistinctOffsets_Match()
    {
        // Stats object: Str@0x20=24, Def@0x24=12, Dex@0x28=7 (+ noise).
        var leaves = new List<GroupMatch.Leaf>
        {
            L(0x20, "System.Int32", 24),
            L(0x24, "System.Int32", 12),
            L(0x28, "System.Int32", 7),
            L(0x2C, "System.Int32", 999),
        };
        var slots = new List<GroupMatch.Slot> { Exact(24), Exact(12), Exact(7) };
        Assert.True(GroupMatch.Run(leaves, slots, out var perSlot));
        var asg = GroupMatch.Assignment(perSlot, leaves.Count);
        Assert.NotNull(asg);
        // distinct leaves, mapping to the right offsets
        Assert.Equal(0x20, leaves[asg![0]].Offset);
        Assert.Equal(0x24, leaves[asg[1]].Offset);
        Assert.Equal(0x28, leaves[asg[2]].Offset);
    }

    [Fact]
    public void Run_MissingOneValue_NoMatch()
    {
        var leaves = new List<GroupMatch.Leaf>
        {
            L(0x20, "System.Int32", 24),
            L(0x24, "System.Int32", 12),
        };
        var slots = new List<GroupMatch.Slot> { Exact(24), Exact(12), Exact(7) };
        Assert.False(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void Run_DuplicateValueButOnlyOneLeaf_NoMatch()
    {
        // "24 AND 24" but the object has only one field == 24 -> no SDR.
        var leaves = new List<GroupMatch.Leaf>
        {
            L(0x20, "System.Int32", 24),
            L(0x24, "System.Int32", 99),
        };
        var slots = new List<GroupMatch.Slot> { Exact(24), Exact(24) };
        Assert.False(GroupMatch.Run(leaves, slots, out _));
    }

    [Fact]
    public void Run_DuplicateValueTwoLeaves_Match()
    {
        var leaves = new List<GroupMatch.Leaf>
        {
            L(0x20, "System.Int32", 24),
            L(0x24, "System.Int32", 24),
        };
        var slots = new List<GroupMatch.Slot> { Exact(24), Exact(24) };
        Assert.True(GroupMatch.Run(leaves, slots, out var perSlot));
        var asg = GroupMatch.Assignment(perSlot, leaves.Count);
        Assert.NotNull(asg);
        Assert.NotEqual(asg![0], asg[1]); // two distinct fields
    }

    [Fact]
    public void Run_FewerThanTwoSlots_False()
    {
        var leaves = new List<GroupMatch.Leaf> { L(0x20, "System.Int32", 24) };
        Assert.False(GroupMatch.Run(leaves, new List<GroupMatch.Slot> { Exact(24) }, out _));
    }

    [Fact]
    public void Run_ByteFieldExcludedUnderNoByteScope()
    {
        // Both values live only in 1-byte fields; NoByte scope -> no match, All -> match.
        var leaves = new List<GroupMatch.Leaf>
        {
            L(0x10, "System.Byte", 3),
            L(0x11, "System.Byte", 5),
        };
        var noByte = new List<GroupMatch.Slot> { Exact(3), Exact(5) };
        Assert.False(GroupMatch.Run(leaves, noByte, out _));

        var all = new List<GroupMatch.Slot>
        {
            Exact(3, GroupMatch.Scope.NumericAll),
            Exact(5, GroupMatch.Scope.NumericAll),
        };
        Assert.True(GroupMatch.Run(leaves, all, out _));
    }
}
