// InterestingMethodScorerTests.cs — method-name keyword scoring, the explicit
// cheat pack, the opt-in gameplay-action pack, class-token folding, and the
// overload-collapsing bulk pass.

using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class InterestingMethodScorerTests
{
    [Theory]
    [InlineData("SetHealth",  MethodCategory.Stats)]
    [InlineData("AddGold",    MethodCategory.Inventory)]
    [InlineData("TeleportTo", MethodCategory.Movement)]
    [InlineData("FireWeapon", MethodCategory.Combat)]
    [InlineData("SaveGame",   MethodCategory.Utility)]
    public void ScoreMethod_Categorizes(string method, MethodCategory expected)
    {
        (int score, MethodCategory cat, _) = InterestingMethodScorer.ScoreMethod(method, "Foo", false);
        Assert.True(score > 0);
        Assert.Equal(expected, cat);
    }

    [Fact]
    public void ScoreMethod_ExplicitCheat_WeightedHeavier()
    {
        // "EnableFly" -> Fly is an explicit cheat (weight 8), categorized Movement.
        (int score, MethodCategory cat, List<string> matched) =
            InterestingMethodScorer.ScoreMethod("EnableFly", "Foo", false);
        Assert.Equal(MethodCategory.Movement, cat);
        Assert.Equal(8, score);                 // no class bonus on "Foo"
        Assert.Contains("Fly", matched);
    }

    [Fact]
    public void ScoreMethod_HP_ScoresOnce_NotDoubleFromHpAlias()
    {
        // "HP" and "Hp" both tokenize to {hp}; the Stats table must not list both,
        // or "SetHP" would score 10 (2×5) and duplicate the keyword in the display.
        (int score, MethodCategory cat, List<string> matched) =
            InterestingMethodScorer.ScoreMethod("SetHP", "Foo", false);
        Assert.Equal(5, score);                 // single Stats hit, no class bonus on "Foo"
        Assert.Equal(MethodCategory.Stats, cat);
        Assert.Single(matched);
        Assert.Equal("HP", matched[0]);
        Assert.DoesNotContain("Hp", matched);
    }

    [Fact]
    public void ScoreMethod_FoldsClassTokens()
    {
        // "Add" alone is not a keyword; folding the class "GoldManager" makes it
        // hit the Inventory "Gold" keyword.
        (int score, MethodCategory cat, _) = InterestingMethodScorer.ScoreMethod("Add", "GoldManager", false);
        Assert.Equal(MethodCategory.Inventory, cat);
        Assert.True(score >= 5);
        // A boring method with no gameplay tokens anywhere scores 0.
        Assert.Equal(0, InterestingMethodScorer.ScoreMethod("GetComponent", "Foo", false).Score);
    }

    [Fact]
    public void ScoreMethod_GameplayActions_OptIn()
    {
        Assert.Equal(0, InterestingMethodScorer.ScoreMethod("Dash", "Foo", includeGameplayActions: false).Score);
        (int score, MethodCategory cat, _) = InterestingMethodScorer.ScoreMethod("Dash", "Foo", includeGameplayActions: true);
        Assert.Equal(MethodCategory.GameplayAction, cat);
        Assert.True(score > 0);
    }

    [Fact]
    public void Score_CollapsesOverloads_AndDropsBelowThreshold()
    {
        var dump = new DumpClassInfoResult
        {
            Rows =
            {
                new DumpedClassRow
                {
                    Assembly = "Assembly-CSharp", Namespace = "Game", Name = "Player",
                    Methods = new List<MethodEntry>
                    {
                        new() { Name = "SetHealth" },   // Stats + class Player bonus
                        new() { Name = "SetHealth" },   // overload — collapses
                        new() { Name = "AddGold" },     // Inventory + class bonus
                        new() { Name = "Update" },       // no keyword -> dropped
                    },
                },
            },
        };

        var scored = InterestingMethodScorer.Score(dump);
        Assert.Equal(2, scored.Count);                       // overload collapsed, Update dropped
        Assert.Single(scored, m => m.Name == "SetHealth");
        Assert.Contains(scored, m => m.Name == "AddGold");
        Assert.DoesNotContain(scored, m => m.Name == "Update");
    }
}
