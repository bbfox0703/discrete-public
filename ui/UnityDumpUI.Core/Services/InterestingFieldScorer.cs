// InterestingFieldScorer.cs — keyword-scores managed field names to surface the
// gameplay-relevant ones (HP / Gold / Damage / Speed …) from a bulk class dump.
// Ported + Unity-adapted from UE5CEDumper's PropertyScoringTable.
//
// Token-EXACT matching via KeywordTokenizer (a keyword hits only when ALL of its
// own tokens are a subset of the field name's tokens) — so "HP" credits GetHP /
// PlayerHP but never the "mp" buried in Component. Engine-agnostic pure logic:
// scores a DumpClassInfoResult (name + type + offset) with no I/O.
//
// UE-only signals from the original are DROPPED (no Unity analogue): GAS
// GameplayAttributeData bonus, CPF_* PropertyFlags gating (SaveGame/
// BlueprintVisible/EditorOnly). The class-location table is rebuilt with Unity
// class-name tokens instead of Pawn/PlayerController/Niagara/HUD.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityDumpUI.Core.Services;

/// <summary>Bucket a scored field falls into (highest-weight keyword hit).</summary>
public enum FieldCategory { Other, Stats, Combat, Resources, Movement, Utility }

/// <summary>One keyword-scored field from the bulk class dump.</summary>
public sealed class InterestingField
{
    public required string Assembly  { get; init; }
    public required string Namespace { get; init; }   // may be empty
    public required string Class     { get; init; }
    public required string Name      { get; init; }
    public required string Type      { get; init; }   // field type string ("" if unknown)
    public required int    Offset    { get; init; }
    public required bool   IsStatic  { get; init; }
    public required FieldCategory Category { get; init; }
    public required bool   IsNumeric { get; init; }
    public required IReadOnlyList<string> MatchedKeywords { get; init; }

    /// <summary>Base keyword+class score, before the Current/Max pair bonus.</summary>
    public int BaseScore { get; init; }
    /// <summary>+2 when this field is part of a Current/Max stat family.</summary>
    public bool HasMaxSibling { get; set; }
    /// <summary>Final score (BaseScore + pair bonus).</summary>
    public int Score { get; set; }

    public string ClassDisplay => string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
    public string OffsetLabel  => $"0x{Offset:X}";
    public string KeywordsDisplay => string.Join(", ", MatchedKeywords);
}

/// <summary>
/// Scores field names against gameplay keyword tables. Static + pure so it is
/// trivially unit-testable and AOT-safe.
/// </summary>
public static class InterestingFieldScorer
{
    /// <summary>Rows below this final score drop from the default view.</summary>
    public const int InterestingThreshold = 4;

    private const int StatPairBonus = 2;

    // ── Keyword tables (property side of the UE port; weights preserved) ──────
    // A keyword hits when all its tokens are a subset of the field-name tokens.
    private sealed record Keyword(string Text, HashSet<string> Tokens, FieldCategory Category);

    private static readonly (FieldCategory Cat, int Weight, string[] Words)[] Table =
    {
        (FieldCategory.Stats, 5, new[]
        {
            "HP", "Hp", "Health", "MP", "Mana", "SP", "Stamina", "Energy",
            "XP", "Exp", "Experience", "Level", "Lv", "Lvl",
            "Dead", "IsDead", "Alive", "IsAlive",
            "Regen", "Regenerate", "Max",
        }),
        (FieldCategory.Combat, 4, new[]
        {
            "Damage", "Dmg", "Defense", "Defence", "Def", "Armor", "Armour",
            "Resistance", "Resist",
            "Crit", "Critical", "CritRate", "CritDamage",
            "Attack", "Atk", "Multiplier",
            "Weapon", "Hit", "HitDamage",
            "Effect", "Target", "Radius", "Ability", "Modifier", "Duration",
        }),
        (FieldCategory.Resources, 4, new[]
        {
            "Gold", "Coin", "Coins", "Money", "Currency", "Cash", "Credit", "Credits",
            "Gem", "Gems", "Diamond", "Diamonds", "Crystal", "Crystals",
            "Token", "Tokens",
            "Ammo", "Material", "Materials", "Resource", "Resources", "Supply", "Supplies",
            "Stack", "Count", "Quantity", "Amount",
            "Item", "Items",
        }),
        (FieldCategory.Movement, 4, new[]
        {
            "Speed", "Velocity", "Walk",
            "Sprint", "Run",
            "Jump", "JumpHeight", "JumpZ",
            "Friction", "Acceleration", "Gravity",
            "Dash", "Climb", "Swim",
        }),
        (FieldCategory.Utility, 3, new[]
        {
            "Save", "Load", "Checkpoint",
            "Quest", "Mission", "Objective",
            "Cheat", "Debug", "GodMode", "NoClip",
            "Score",
            "IsImmortal", "CanBeDamaged", "Invincible", "Invulnerable",
        }),
    };

    // Category tie-break order (highest bucket wins; ties resolve by this order).
    private static readonly FieldCategory[] CategoryOrder =
    {
        FieldCategory.Stats, FieldCategory.Combat, FieldCategory.Resources,
        FieldCategory.Movement, FieldCategory.Utility,
    };

    private static readonly Dictionary<FieldCategory, int> Weight =
        Table.ToDictionary(t => t.Cat, t => t.Weight);

    private static readonly Keyword[] Keywords = Table
        .SelectMany(t => t.Words.Select(w => new Keyword(w, KeywordTokenizer.TokenizeAsSet(w), t.Cat)))
        .ToArray();

    // Qualifier tokens stripped to find a stat stem (Health vs MaxHealth family).
    private static readonly HashSet<string> StatQualifiers = new(StringComparer.Ordinal)
    {
        "max", "maximum", "min", "minimum", "current", "cur", "base",
        "total", "start", "starting", "initial", "default",
    };

    // ── Class-location bias (Unity class-name tokens; rebuilt from UE) ────────
    // Token-based (not substring) so "Context" never false-hits "text", etc.
    private sealed record ClassRule(string Token, int Bonus, bool Unusual);

    private static readonly ClassRule[] ClassRules =
    {
        // Expected gameplay containers.
        new("character", 3, false), new("player", 2, false), new("hero", 2, false),
        new("pawn", 3, false),      new("enemy", 2, false),  new("monster", 2, false),
        new("boss", 2, false),      new("unit", 2, false),   new("actor", 1, false),
        new("entity", 1, false),
        new("inventory", 3, false), new("equipment", 2, false), new("item", 2, false),
        new("weapon", 2, false),    new("stats", 2, false),  new("stat", 2, false),
        new("status", 2, false),    new("health", 2, false), new("damage", 2, false),
        new("skill", 2, false),     new("ability", 2, false),new("battle", 2, false),
        new("combat", 2, false),    new("save", 2, false),   new("profile", 2, false),
        new("game", 1, false),      new("controller", 1, false),
        // Rendering / audio / UI / FX noise.
        new("animator", -2, false), new("animation", -2, false), new("particle", -2, false),
        new("audio", -2, false),    new("sound", -2, false), new("renderer", -2, false),
        new("shader", -2, false),   new("canvas", -2, false), new("mesh", -1, false),
        new("sprite", -1, false),   new("texture", -1, false), new("widget", -1, false),
        new("tween", -2, false),
        // "Unusual location" — gameplay state hiding where a CE user wouldn't look.
        new("hud", 4, true),        new("cheat", 4, true),   new("debug", 3, true),
    };

    // ── Numeric type detection (last segment vs known numeric type names) ─────
    private static readonly HashSet<string> NumericTypeLeaf = new(StringComparer.OrdinalIgnoreCase)
    {
        "Single", "Double", "Decimal",
        "Int32", "Int64", "Int16", "SByte",
        "UInt32", "UInt64", "UInt16", "Byte",
        "float", "double", "int", "long", "short", "sbyte",
        "uint", "ulong", "ushort", "byte",
    };

    /// <summary>
    /// Score every field in a bulk class dump and return the ranked list. Fields
    /// scoring below <see cref="InterestingThreshold"/> are dropped unless
    /// <paramref name="includeBelowThreshold"/> is set.
    /// </summary>
    public static IReadOnlyList<InterestingField> Score(
        DumpClassInfoResult? dump, bool includeBelowThreshold = false)
    {
        var all = new List<InterestingField>();
        if (dump?.Rows is null) return all;

        foreach (DumpedClassRow row in dump.Rows)
        {
            if (row.Fields is null || row.Fields.Count == 0) continue;
            string cls = row.Name;
            string ns  = row.Namespace ?? string.Empty;
            foreach (var f in row.Fields)
            {
                (int baseScore, FieldCategory cat, List<string> hits) = ScoreField(f.Name, cls);
                if (baseScore <= 0) continue;
                all.Add(new InterestingField
                {
                    Assembly  = row.Assembly,
                    Namespace = ns,
                    Class     = cls,
                    Name      = f.Name,
                    Type      = f.Type ?? string.Empty,
                    Offset    = f.Offset,
                    IsStatic  = f.Static,
                    Category  = cat,
                    IsNumeric = IsNumericType(f.Type),
                    MatchedKeywords = hits,
                    BaseScore = baseScore,
                    Score     = baseScore,
                });
            }
        }

        ApplyStatPairBonuses(all);

        IEnumerable<InterestingField> kept = includeBelowThreshold
            ? all
            : all.Where(x => x.Score >= InterestingThreshold);
        return kept
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ClassDisplay, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Score a single field name against the keyword + class tables. Returns the
    /// base score (before pair bonus), the winning category, and the matched
    /// keyword strings. Internal for direct unit testing.
    /// </summary>
    internal static (int Score, FieldCategory Category, List<string> Matched)
        ScoreField(string fieldName, string className)
    {
        HashSet<string> nameTokens = KeywordTokenizer.TokenizeAsSet(fieldName);
        var bucket = new Dictionary<FieldCategory, int>();
        var matched = new List<string>();

        foreach (Keyword kw in Keywords)
        {
            if (kw.Tokens.Count == 0) continue;
            if (kw.Tokens.IsSubsetOf(nameTokens))
            {
                bucket[kw.Category] = bucket.GetValueOrDefault(kw.Category) + 1;
                matched.Add(kw.Text);
            }
        }

        int keywordSum = 0;
        int bestScore = -1;
        FieldCategory best = FieldCategory.Other;
        foreach (FieldCategory c in CategoryOrder)
        {
            int hits = bucket.GetValueOrDefault(c);
            if (hits == 0) continue;
            int catScore = hits * Weight[c];
            keywordSum += catScore;
            if (catScore > bestScore) { bestScore = catScore; best = c; }
        }

        if (keywordSum == 0) return (0, FieldCategory.Other, matched);

        int classBonus = ClassBonus(className);
        int score = keywordSum + classBonus;
        return (score, best, matched);
    }

    private static int ClassBonus(string className)
    {
        HashSet<string> classTokens = KeywordTokenizer.TokenizeAsSet(className);
        int bonus = 0;
        foreach (ClassRule r in ClassRules)
            if (classTokens.Contains(r.Token)) bonus += r.Bonus;
        return bonus;
    }

    /// <summary>True when the field type resolves to a numeric primitive.</summary>
    public static bool IsNumericType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        string t = type;
        int lt = t.IndexOf('<');            // strip any generic/array suffix noise
        if (lt >= 0) t = t[..lt];
        if (t.EndsWith("[]", StringComparison.Ordinal)) return false;
        int dot = t.LastIndexOf('.');
        string leaf = dot >= 0 ? t[(dot + 1)..] : t;
        return NumericTypeLeaf.Contains(leaf.Trim());
    }

    // ── Current/Max stat-pair bonus (post-pass; needs sibling context) ───────
    private static void ApplyStatPairBonuses(List<InterestingField> fields)
    {
        foreach (var classGroup in fields.GroupBy(f => (f.Assembly, f.Namespace, f.Class)))
        {
            // stem -> members; a member records whether it was a variant.
            var byStem = new Dictionary<string, List<(InterestingField Field, bool Variant)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in classGroup)
            {
                (string stem, bool variant) = NormaliseStatStem(f.Name);
                if (stem.Length == 0) continue;
                if (!byStem.TryGetValue(stem, out var list))
                    byStem[stem] = list = new();
                list.Add((f, variant));
            }
            foreach (var list in byStem.Values)
            {
                if (list.Count < 2) continue;
                if (!list.Any(m => m.Variant)) continue;   // need a real Max/Current/… variant
                foreach (var (field, _) in list)
                {
                    field.HasMaxSibling = true;
                    field.Score += StatPairBonus;
                }
            }
        }
    }

    /// <summary>
    /// Strip stat qualifier tokens (max/current/base/…) from both ends of the
    /// tokenized name and join the rest with '.'. <c>Variant</c> is true when any
    /// qualifier was stripped. Health -> ("health", false); MaxHealth ->
    /// ("health", true); CurrentHP -> ("hp", true). All-qualifier -> ("", ...).
    /// Internal for testing.
    /// </summary>
    internal static (string Stem, bool Variant) NormaliseStatStem(string name)
    {
        string[] tokens = KeywordTokenizer.Tokenize(name);
        int start = 0, end = tokens.Length;
        bool variant = false;
        while (start < end && StatQualifiers.Contains(tokens[start])) { start++; variant = true; }
        while (end > start && StatQualifiers.Contains(tokens[end - 1])) { end--; variant = true; }
        if (start >= end) return (string.Empty, variant);
        return (string.Join('.', tokens[start..end]), variant);
    }
}
