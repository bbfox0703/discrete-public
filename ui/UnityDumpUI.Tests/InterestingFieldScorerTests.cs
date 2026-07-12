// InterestingFieldScorerTests.cs — keyword scoring, token-exact matching,
// class-location bias, numeric detection, and the Current/Max stat-pair bonus.

using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class InterestingFieldScorerTests
{
    [Theory]
    [InlineData("Health",   FieldCategory.Stats)]
    [InlineData("HP",       FieldCategory.Stats)]
    [InlineData("Gold",     FieldCategory.Resources)]
    [InlineData("MoveSpeed",FieldCategory.Movement)]
    [InlineData("Damage",   FieldCategory.Combat)]
    public void ScoreField_Categorizes(string field, FieldCategory expected)
    {
        (int score, FieldCategory cat, _) = InterestingFieldScorer.ScoreField(field, "Foo");
        Assert.True(score > 0);
        Assert.Equal(expected, cat);
    }

    [Fact]
    public void ScoreField_TokenExact_NoSubstringFalsePositives()
    {
        // "Component" contains "mp" but must NOT hit the "MP" (mana) keyword.
        Assert.Equal(0, InterestingFieldScorer.ScoreField("Component", "Foo").Score);
        // "IsGrounded"/"GridPosition" contain no standalone gameplay tokens.
        Assert.Equal(0, InterestingFieldScorer.ScoreField("IsGrounded", "Foo").Score);
        Assert.Equal(0, InterestingFieldScorer.ScoreField("GridPosition", "Foo").Score);
    }

    [Fact]
    public void ScoreField_MultiKeyword_SumsBuckets()
    {
        // MaxHealth -> {max, health}: both are Stats keywords -> 2 hits * 5 = 10.
        (int score, FieldCategory cat, List<string> matched) =
            InterestingFieldScorer.ScoreField("MaxHealth", "Foo");
        Assert.Equal(10, score);
        Assert.Equal(FieldCategory.Stats, cat);
        Assert.Contains("Health", matched);
        Assert.Contains("Max", matched);
    }

    [Fact]
    public void ScoreField_ClassBonus_Applied()
    {
        // class PlayerStats -> {player, stats}: player+2, stats+2 = +4 on top of Stats 5.
        Assert.Equal(9, InterestingFieldScorer.ScoreField("Health", "PlayerStats").Score);
        // Noise container (Animator) applies a penalty vs a neutral class.
        Assert.True(InterestingFieldScorer.ScoreField("Health", "Animator").Score
                    < InterestingFieldScorer.ScoreField("Health", "Foo").Score);
    }

    [Theory]
    [InlineData("System.Int32",  true)]
    [InlineData("System.Single", true)]
    [InlineData("System.Byte",   true)]
    [InlineData("int",           true)]
    [InlineData("System.String", false)]
    [InlineData("UnityEngine.Vector3", false)]
    [InlineData("System.Int32[]", false)]
    [InlineData("System.Collections.Generic.List<System.Int32>", false)]
    public void IsNumericType_Detects(string type, bool expected)
        => Assert.Equal(expected, InterestingFieldScorer.IsNumericType(type));

    [Theory]
    [InlineData("Health",      "health", false)]
    [InlineData("MaxHealth",   "health", true)]
    [InlineData("CurrentHP",   "hp",     true)]
    [InlineData("BaseAttack",  "attack", true)]
    [InlineData("Max",         "",       true)]   // all-qualifier -> empty stem
    public void NormaliseStatStem_StripsQualifiers(string name, string stem, bool variant)
    {
        (string s, bool v) = InterestingFieldScorer.NormaliseStatStem(name);
        Assert.Equal(stem, s);
        Assert.Equal(variant, v);
    }

    [Fact]
    public void Score_StatPairBonus_CreditsFamilyMembers()
    {
        var dump = new DumpClassInfoResult
        {
            Rows =
            {
                new DumpedClassRow
                {
                    Assembly = "Assembly-CSharp", Namespace = "Game", Name = "Foo",
                    Fields = new List<FieldEntry>
                    {
                        new() { Name = "Health",    Type = "System.Int32", Offset = 16 },
                        new() { Name = "MaxHealth", Type = "System.Int32", Offset = 20 },
                        new() { Name = "Gold",      Type = "System.Int32", Offset = 24 },
                    },
                },
            },
        };

        var scored = InterestingFieldScorer.Score(dump);
        InterestingField health = scored.First(x => x.Name == "Health");
        InterestingField maxHp  = scored.First(x => x.Name == "MaxHealth");
        InterestingField gold   = scored.First(x => x.Name == "Gold");

        Assert.True(health.HasMaxSibling);
        Assert.True(maxHp.HasMaxSibling);
        Assert.False(gold.HasMaxSibling);          // no Max/Current sibling
        Assert.Equal(health.BaseScore + 2, health.Score);   // +2 pair bonus
        Assert.Equal(maxHp.BaseScore + 2, maxHp.Score);
        Assert.Equal(gold.BaseScore, gold.Score);
    }

    [Fact]
    public void Score_DropsBelowThreshold_UnlessRequested()
    {
        var dump = new DumpClassInfoResult
        {
            Rows =
            {
                new DumpedClassRow
                {
                    Assembly = "Assembly-CSharp", Name = "Foo",
                    Fields = new List<FieldEntry>
                    {
                        new() { Name = "Score", Type = "System.Int32", Offset = 8 },  // Utility 3 < 4
                    },
                },
            },
        };
        Assert.Empty(InterestingFieldScorer.Score(dump));
        Assert.Single(InterestingFieldScorer.Score(dump, includeBelowThreshold: true));
    }
}
