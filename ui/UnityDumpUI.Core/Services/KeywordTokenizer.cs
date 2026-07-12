// KeywordTokenizer.cs — split a CamelCase / snake_case / kebab identifier into
// whole lowercased word tokens, so keyword matching is token-exact instead of
// substring. Ported from UE5CEDumper (engine-agnostic pure string util).
//
// Substring matching is a false-positive trap: "Component" contains "mp",
// "Spawn" contains "sp", "grid"/"guid"/"valid" contain "id". Tokenizing first
// means a keyword like "id" only matches when it appears as a STANDALONE token
// (userId -> {user, id}) and not buried inside another word (grid -> {grid}).
// This is the prerequisite for any name-keyword scorer and fixes the latent
// naive-.Contains bug in KeyDiscoveryEngine.NamePrior.
//
// Split rules (applied in order):
//   1. Underscores are separators (consumed):  m_Player_C -> {m, player, c}
//   2. Hyphens are separators (consumed):      snake-case -> {snake, case}
//   3. lower->upper splits before the upper:   AddMoney   -> {add, money}
//   4. run-of-uppers then lower splits before the last upper:
//      HUDWidget -> {hud, widget}, XMLParser -> {xml, parser}
//   5. digits stay attached to their neighbour (no digit-boundary split):
//      Player100Health -> {player100health}  (use underscores for boundaries)
// All tokens are returned lowercase so downstream comparison is a dict lookup.

using System;
using System.Collections.Generic;
using System.Text;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Tokenizes identifiers into whole lowercased words for token-exact keyword
/// matching. See file header for the split rules.
/// </summary>
public static class KeywordTokenizer
{
    /// <summary>
    /// Tokenize <paramref name="identifier"/> into lowercased word tokens.
    /// Empty / null input returns an empty array (never null).
    /// </summary>
    public static string[] Tokenize(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return Array.Empty<string>();

        var tokens = new List<string>(8);
        var current = new StringBuilder(identifier.Length);

        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];

            // Rules 1+2: separators -> finalize current, drop the char.
            if (c == '_' || c == '-')
            {
                FlushIfNonEmpty(tokens, current);
                continue;
            }

            // Rule 3: lower-to-upper. Split BEFORE the upper.
            if (current.Length > 0
                && char.IsLower(current[^1])
                && char.IsUpper(c))
            {
                FlushIfNonEmpty(tokens, current);
                current.Append(c);
                continue;
            }

            // Rule 4: run-of-uppers followed by a lower. The builder holds e.g.
            // "HUDW" and we now see 'i' (lower) -> split BEFORE the last upper
            // (W): emit "HUD", start a new token "Wi".
            if (current.Length >= 2
                && char.IsUpper(current[^1])
                && char.IsUpper(current[^2])
                && char.IsLower(c))
            {
                char lastUpper = current[^1];
                current.Length--;                  // drop the last upper from current
                FlushIfNonEmpty(tokens, current);  // emit "HUD"
                current.Append(lastUpper);         // start new with W
                current.Append(c);                 // ...append the lower
                continue;
            }

            current.Append(c);
        }

        FlushIfNonEmpty(tokens, current);
        return tokens.ToArray();
    }

    /// <summary>
    /// Tokenize into an ordinal <see cref="HashSet{T}"/> for O(1) keyword
    /// lookup (repeats collapse). Empty / null input returns an empty set.
    /// </summary>
    public static HashSet<string> TokenizeAsSet(string? identifier)
    {
        string[] arr = Tokenize(identifier);
        return arr.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(arr, StringComparer.Ordinal);
    }

    private static void FlushIfNonEmpty(List<string> tokens, StringBuilder buf)
    {
        if (buf.Length == 0) return;
        // Lowercase on emit so downstream comparisons are case-blind by
        // construction (cheaper than lowercasing at each comparison).
        for (int i = 0; i < buf.Length; i++)
            buf[i] = char.ToLowerInvariant(buf[i]);
        tokens.Add(buf.ToString());
        buf.Clear();
    }
}
