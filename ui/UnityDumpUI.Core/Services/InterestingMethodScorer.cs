// InterestingMethodScorer.cs — keyword-scores managed METHOD names to surface
// the gameplay-relevant invoke targets (SetHealth / AddGold / Teleport / Kill /
// God…) from a bulk class dump. Ported + Unity-adapted from UE5CEDumper's
// KeywordScoringTable (the function side). Sibling to InterestingFieldScorer.
//
// Two differences from the field scorer:
//   1. The keyword tables are verb-oriented (Heal/Kill/Teleport/Buy) with an
//      Inventory bucket, an ExplicitCheat pack (NoClip/Fly/God, weight 8), and an
//      opt-in GameplayAction pack (Dash/Interact/Buy/Sell).
//   2. The owning CLASS name tokens are folded into the match domain (UE
//      behavior) so a generic verb gains context from its class — e.g. Add() on
//      GoldManager hits "gold". A keyword hits once even if it appears in both.
//
// UE-only signals dropped: UFunction FunctionFlags (BlueprintCallable / Event /
// pure / ParmsSize). The class-location bias is shared with the field scorer.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityDumpUI.Core.Services;

/// <summary>Bucket a scored method falls into (highest-weight keyword hit).</summary>
public enum MethodCategory { Other, Stats, Inventory, Movement, Combat, Utility, GameplayAction }

/// <summary>One keyword-scored method from the bulk class dump.</summary>
public sealed class InterestingMethod
{
    public required string Assembly  { get; init; }
    public required string Namespace { get; init; }
    public required string Class     { get; init; }
    public required string Name      { get; init; }
    public required bool   IsStatic  { get; init; }
    public required MethodCategory Category { get; init; }
    public required int    Score     { get; init; }
    public required IReadOnlyList<string> MatchedKeywords { get; init; }

    public string ClassDisplay    => string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
    public string KeywordsDisplay => string.Join(", ", MatchedKeywords);
}

/// <summary>Scores method names against gameplay verb tables. Static + pure.</summary>
public static class InterestingMethodScorer
{
    /// <summary>Rows below this final score drop from the default view.</summary>
    public const int InterestingThreshold = 5;

    private sealed record Keyword(string Text, HashSet<string> Tokens, MethodCategory Category, int Weight);

    // Per-(category, weight) keyword sets. ExplicitCheat + GameplayAction are
    // separate packs folded in below.
    private static readonly (MethodCategory Cat, int Weight, string[] Words)[] Table =
    {
        (MethodCategory.Stats, 5, new[]
        {
            "HP", "Health", "MP", "Mana", "SP", "Stamina", "Energy",
            "XP", "Exp", "Experience", "Level", "Score",
            "Damage", "Heal", "Hurt", "Kill", "Revive", "Death",
        }),
        (MethodCategory.Inventory, 5, new[]
        {
            "Gold", "Money", "Coin", "Currency", "Cash", "Credit",
            "Item", "Inventory", "Pickup", "Loot", "Drop", "Equip",
            "Wallet", "Stack",
        }),
        (MethodCategory.Movement, 5, new[]
        {
            "Teleport", "Warp", "TP",
            "Move", "Location",
            "Speed", "Velocity", "Walk", "Sprint", "Jump",
        }),
        (MethodCategory.Combat, 4, new[]
        {
            "Attack", "Fire", "Shoot", "Cast",
            "Ability", "Skill", "Buff", "Debuff",
            "Reload", "Aim", "Block", "Parry",
        }),
        (MethodCategory.Utility, 3, new[]
        {
            "Save", "Load", "Checkpoint",
            "Spawn", "Summon",
            "Timer", "Time", "Clock", "Countdown",
            "Cheat", "Debug", "Console", "Toggle",
        }),
    };

    // Explicit movement cheats — weight 8, categorized as Movement.
    private static readonly string[] ExplicitCheatWords =
        { "NoClip", "Fly", "God", "Ghost", "Invincible", "Invisible" };

    // Opt-in gameplay actions — weight 5 (only scored when requested).
    private static readonly string[] GameplayActionWords =
    {
        "Dash", "Dodge", "Roll", "Slide", "Climb", "Vault", "Glide", "Grapple", "Crouch",
        "Interact", "Use", "Grab", "Activate", "Talk", "Open",
        "Buy", "Sell", "Shop", "Vendor", "Merchant", "Trade", "Purchase", "Store",
    };

    private static readonly MethodCategory[] CategoryOrder =
    {
        MethodCategory.Stats, MethodCategory.Inventory, MethodCategory.Movement,
        MethodCategory.Combat, MethodCategory.Utility, MethodCategory.GameplayAction,
    };

    private static readonly Keyword[] Keywords = BuildKeywords();

    private static Keyword[] BuildKeywords()
    {
        var list = new List<Keyword>();
        foreach (var (cat, weight, words) in Table)
            foreach (string w in words)
                list.Add(new Keyword(w, KeywordTokenizer.TokenizeAsSet(w), cat, weight));
        foreach (string w in ExplicitCheatWords)          // Movement, weight 8
            list.Add(new Keyword(w, KeywordTokenizer.TokenizeAsSet(w), MethodCategory.Movement, 8));
        foreach (string w in GameplayActionWords)         // GameplayAction, weight 5
            list.Add(new Keyword(w, KeywordTokenizer.TokenizeAsSet(w), MethodCategory.GameplayAction, 5));
        return list.ToArray();
    }

    /// <summary>
    /// Score every method in a bulk class dump and return the ranked list.
    /// Overloads collapse to one row per (class, name). Rows below
    /// <see cref="InterestingThreshold"/> are dropped unless requested.
    /// </summary>
    public static IReadOnlyList<InterestingMethod> Score(
        DumpClassInfoResult? dump, bool includeGameplayActions = false, bool includeBelowThreshold = false)
    {
        var all = new List<InterestingMethod>();
        if (dump?.Rows is null) return all;

        var seen = new HashSet<(string, string, string, string)>();
        foreach (DumpedClassRow row in dump.Rows)
        {
            if (row.Methods is null || row.Methods.Count == 0) continue;
            string cls = row.Name;
            string ns  = row.Namespace ?? string.Empty;
            foreach (MethodEntry m in row.Methods)
            {
                if (!seen.Add((row.Assembly, ns, cls, m.Name))) continue;   // collapse overloads
                (int score, MethodCategory cat, List<string> hits) =
                    ScoreMethod(m.Name, cls, includeGameplayActions);
                if (score <= 0) continue;
                all.Add(new InterestingMethod
                {
                    Assembly = row.Assembly, Namespace = ns, Class = cls, Name = m.Name,
                    IsStatic = m.Static, Category = cat, Score = score, MatchedKeywords = hits,
                });
            }
        }

        IEnumerable<InterestingMethod> kept = includeBelowThreshold
            ? all
            : all.Where(x => x.Score >= InterestingThreshold);
        return kept
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ClassDisplay, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Score a single method name against the keyword + class tables, folding the
    /// class-name tokens into the match domain. Returns the score, winning
    /// category, and matched keyword strings. Internal for direct unit testing.
    /// </summary>
    internal static (int Score, MethodCategory Category, List<string> Matched)
        ScoreMethod(string methodName, string className, bool includeGameplayActions)
    {
        // Match domain = method-name tokens UNION class-name tokens (UE folds the
        // class in). A keyword hits once even if present in both.
        HashSet<string> tokens = KeywordTokenizer.TokenizeAsSet(methodName);
        foreach (string t in KeywordTokenizer.Tokenize(className)) tokens.Add(t);

        var bucket = new Dictionary<MethodCategory, int>();
        var matched = new List<string>();
        foreach (Keyword kw in Keywords)
        {
            if (kw.Category == MethodCategory.GameplayAction && !includeGameplayActions) continue;
            if (kw.Tokens.Count == 0) continue;
            if (kw.Tokens.IsSubsetOf(tokens))
            {
                bucket[kw.Category] = bucket.GetValueOrDefault(kw.Category) + kw.Weight;
                matched.Add(kw.Text);
            }
        }

        int keywordSum = 0, best = -1;
        MethodCategory bestCat = MethodCategory.Other;
        foreach (MethodCategory c in CategoryOrder)
        {
            int s = bucket.GetValueOrDefault(c);
            if (s == 0) continue;
            keywordSum += s;
            if (s > best) { best = s; bestCat = c; }
        }
        if (keywordSum == 0) return (0, MethodCategory.Other, matched);

        int score = keywordSum + InterestingFieldScorer.ClassBonus(className);
        return (score, bestCat, matched);
    }
}
