// SpcQueryEngine — Phase 29d-1.
//
// SPC = "statistical process control" in the loose sense: instead of
// two-snapshot diff, give the user N snapshots + N predicates and find
// every field whose value sequence satisfies the whole constraint chain.
//
// Killer use case (user's example): "find any field that was 10160 in
// session A, 9910 in B, 9410 in C, 9410 in D". The 4-step value
// signature uniquely identifies the 銀兩 GameStat across all
// re-allocations / unrelated noise.
//
// Predicates are per-snapshot. Two flavours:
//   * Absolute   — Exact / Range / NotEqual / Any
//   * Relative   — Unchanged / Increased / Decreased / DeltaExact / DeltaRange
//     (compare vs the immediately preceding snapshot's value; predicate
//     at index 0 cannot be a relative kind.)
//
// "Increased by N" / "Decreased by N" are NOT separate predicates by
// design — DeltaRange [-N, -N] for "decreased by exactly N" /
// DeltaRange [-max, -min] for "decreased by min..max" cover the space.
//
// Algorithm: build candidate key-set as intersection of every selected
// snapshot's FieldIndex.Keys (filtered by Statics / Synthetic / Type /
// Class settings on the anchor), then walk each predicate per key.
// First failing predicate disqualifies the key.
//
// Cross-session caveat: instance-keyed rows from different game launches
// never share addresses, so the intersection of instance keys collapses
// to empty across sessions. The ViewModel auto-enables StaticsOnly when
// it detects mixed session_ids — same philosophy as Phase 29c-5 diff.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityDumpUI.ViewModels;

public enum SpcPredicateKind
{
    /// <summary>No constraint — field always passes this snapshot's slot.</summary>
    Any,

    /// <summary>Value parses as numeric AND equals <c>Operand</c>.</summary>
    Exact,

    /// <summary>Value parses as numeric AND lies in <c>[Operand, Operand2]</c>
    /// (inclusive both ends). Either bound nullable → unbounded on that
    /// side.</summary>
    Range,

    /// <summary>Value parses as numeric AND does NOT equal <c>Operand</c>.</summary>
    NotEqual,

    // ── Relative-to-previous-snapshot predicates ───────────────────────
    // All require the predicate index to be > 0; engine throws otherwise.

    /// <summary>Value equals previous snapshot's value (textual exact
    /// match of ValueDisplay — works for non-numeric fields too).</summary>
    Unchanged,

    /// <summary>Numeric value greater than previous snapshot's value.</summary>
    Increased,

    /// <summary>Numeric value less than previous snapshot's value.</summary>
    Decreased,

    /// <summary>(value − prev_value) equals <c>Operand</c> exactly.</summary>
    DeltaExact,

    /// <summary>(value − prev_value) lies in <c>[Operand, Operand2]</c>.
    /// Same nullable-bound semantics as <see cref="Range"/>.</summary>
    DeltaRange,
}

/// <summary>A single per-snapshot constraint. Position in
/// <see cref="SpcQuery.Predicates"/> aligns with the position of the
/// corresponding snapshot in <see cref="SpcQuery.Snapshots"/>.</summary>
public sealed class SpcPredicate
{
    public required SpcPredicateKind Kind { get; init; }
    public double?                   Operand  { get; init; }
    public double?                   Operand2 { get; init; }

    public static SpcPredicate Any                 => new() { Kind = SpcPredicateKind.Any };
    public static SpcPredicate Exact(double v)     => new() { Kind = SpcPredicateKind.Exact, Operand = v };
    public static SpcPredicate Range(double? min, double? max)
        => new() { Kind = SpcPredicateKind.Range, Operand = min, Operand2 = max };
    public static SpcPredicate NotEqual(double v)  => new() { Kind = SpcPredicateKind.NotEqual, Operand = v };
    public static SpcPredicate Unchanged           => new() { Kind = SpcPredicateKind.Unchanged };
    public static SpcPredicate Increased           => new() { Kind = SpcPredicateKind.Increased };
    public static SpcPredicate Decreased           => new() { Kind = SpcPredicateKind.Decreased };
    public static SpcPredicate DeltaExact(double v)=> new() { Kind = SpcPredicateKind.DeltaExact, Operand = v };
    public static SpcPredicate DeltaRange(double? min, double? max)
        => new() { Kind = SpcPredicateKind.DeltaRange, Operand = min, Operand2 = max };

    public bool IsRelative => Kind is SpcPredicateKind.Unchanged
                                or SpcPredicateKind.Increased
                                or SpcPredicateKind.Decreased
                                or SpcPredicateKind.DeltaExact
                                or SpcPredicateKind.DeltaRange;
}

/// <summary>Inputs to <see cref="SpcQueryEngine.Run"/>. Snapshots MUST
/// already have their FieldIndex populated — caller is responsible for
/// calling <c>SnapshotViewModel.EnsureLoadedAsync</c> on each before
/// running the query (or going through the streaming path the VM
/// provides).</summary>
public sealed class SpcQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    public required IReadOnlyList<SpcPredicate>     Predicates { get; init; }

    /// <summary>Only match static-field rows. ON automatically when
    /// snapshots span multiple game sessions (instance addresses don't
    /// align cross-session). Default false here; ViewModel may force
    /// true.</summary>
    public bool StaticsOnly { get; init; }

    /// <summary>Skip rows whose class name marks it synthetic
    /// (<c>&lt;Module&gt;</c>, <c>&lt;&gt;c</c>, etc.) — same heuristic
    /// the diff DataGrid uses. Default true.</summary>
    public bool ExcludeSynthetic { get; init; } = true;

    /// <summary>Require all snapshot values to parse as numeric. Default
    /// true since predicates like Increased / DeltaExact are numeric.
    /// When false, predicates like Exact will still TRY numeric parse;
    /// falling back to <see cref="SnapshotFieldValue.ValueDisplay"/>
    /// string equality.</summary>
    public bool NumericOnly { get; init; } = true;

    /// <summary>Optional substring filter on <c>ClassFqn</c>
    /// (case-insensitive ordinal).</summary>
    public string? ClassFilter { get; init; }

    /// <summary>Optional substring filter on <c>FieldType</c>
    /// (case-insensitive ordinal). e.g. "Int32" / "Single".</summary>
    public string? TypeFilter { get; init; }
}

/// <summary>One matching row emitted by <see cref="SpcQueryEngine.Run"/>.
/// Carries the per-snapshot value sequence + numeric-parsed mirrors for
/// downstream UI display (DataGrid columns one-per-snapshot + a delta
/// summary).</summary>
public sealed class SpcMatch
{
    public required string Key             { get; init; }   // FieldIndex key
    public required string Assembly        { get; init; }
    public required string ClassFqn        { get; init; }
    public required string FieldName       { get; init; }
    public required string FieldType       { get; init; }
    public required string InstanceAddress { get; init; }   // empty when IsStatic
    public required bool   IsStatic        { get; init; }
    public required ulong  FieldOffset     { get; init; }

    /// <summary>Per-snapshot raw display string (length == snap count).</summary>
    public required IReadOnlyList<string>  ValueDisplays { get; init; }

    /// <summary>Per-snapshot numeric parse result, <c>null</c> when the
    /// value didn't parse as a finite double. Same length as
    /// <see cref="ValueDisplays"/>.</summary>
    public required IReadOnlyList<double?> NumericValues { get; init; }
}

public static class SpcQueryEngine
{
    /// <summary>Run the SPC query and return every match. Streams O(N×K)
    /// where N = snapshot count and K = anchor snap's FieldIndex.Count
    /// after pre-filtering. Pure CPU + heap walks — no I/O.</summary>
    public static List<SpcMatch> Run(SpcQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Query needs at least one snapshot.", nameof(query));
        if (query.Predicates.Count != query.Snapshots.Count)
            throw new ArgumentException(
                $"Predicate count ({query.Predicates.Count}) must match snapshot count ({query.Snapshots.Count}).",
                nameof(query));
        if (query.Predicates[0].IsRelative)
            throw new ArgumentException(
                "First predicate cannot be relative (Unchanged/Increased/Decreased/Delta*) — there is no previous snapshot to compare against.",
                nameof(query));

        // Anchor on the smallest FieldIndex so the candidate-key walk
        // does fewer Dictionary.TryGetValue calls overall.
        int anchorIdx = 0;
        int anchorSize = query.Snapshots[0].FieldIndex.Count;
        for (int i = 1; i < query.Snapshots.Count; i++)
        {
            int sz = query.Snapshots[i].FieldIndex.Count;
            if (sz < anchorSize) { anchorIdx = i; anchorSize = sz; }
        }

        var anchor = query.Snapshots[anchorIdx];
        string? classFilter = string.IsNullOrWhiteSpace(query.ClassFilter) ? null : query.ClassFilter;
        string? typeFilter  = string.IsNullOrWhiteSpace(query.TypeFilter)  ? null : query.TypeFilter;

        var results = new List<SpcMatch>();
        var values  = new string[query.Snapshots.Count];
        var numeric = new double?[query.Snapshots.Count];

        foreach (var (key, anchorVal) in anchor.FieldIndex)
        {
            // ── Per-key pre-filter ─────────────────────────────────────
            if (query.StaticsOnly && !anchorVal.IsStatic) continue;
            if (query.ExcludeSynthetic && anchorVal.IsSynthetic) continue;
            if (classFilter is not null &&
                anchorVal.ClassFqn.IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (typeFilter is not null &&
                anchorVal.FieldType.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            // Gather per-snapshot value + numeric. The anchor's value is
            // already in hand; every other snap must contain the same
            // key (intersection requirement).
            bool keyPresentInAll = true;
            SnapshotFieldValue? canonicalForDisplay = anchorVal;
            for (int i = 0; i < query.Snapshots.Count; i++)
            {
                SnapshotFieldValue v;
                if (i == anchorIdx)
                {
                    v = anchorVal;
                }
                else if (!query.Snapshots[i].FieldIndex.TryGetValue(key, out var found))
                {
                    keyPresentInAll = false;
                    break;
                }
                else
                {
                    v = found;
                    canonicalForDisplay ??= v;
                }
                values[i]  = v.ValueDisplay;
                numeric[i] = TryParseNumeric(v.ValueDisplay);
            }
            if (!keyPresentInAll) continue;

            if (query.NumericOnly)
            {
                bool allNumeric = true;
                for (int i = 0; i < numeric.Length; i++)
                {
                    if (!numeric[i].HasValue) { allNumeric = false; break; }
                }
                if (!allNumeric) continue;
            }

            // ── Predicate walk ─────────────────────────────────────────
            bool match = true;
            for (int i = 0; i < query.Predicates.Count; i++)
            {
                var pred = query.Predicates[i];
                double? prevNum  = i > 0 ? numeric[i - 1] : null;
                string?  prevText = i > 0 ? values[i - 1]  : null;
                if (!Evaluate(pred, values[i], numeric[i], prevText, prevNum))
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;

            results.Add(new SpcMatch
            {
                Key             = key,
                Assembly        = canonicalForDisplay!.Assembly,
                ClassFqn        = canonicalForDisplay.ClassFqn,
                FieldName       = canonicalForDisplay.FieldName,
                FieldType       = canonicalForDisplay.FieldType,
                InstanceAddress = canonicalForDisplay.InstanceAddress,
                IsStatic        = canonicalForDisplay.IsStatic,
                FieldOffset     = canonicalForDisplay.FieldOffset,
                ValueDisplays   = values.ToArray(),    // snapshot copy
                NumericValues   = numeric.ToArray(),
            });
        }

        return results;
    }

    /// <summary>Evaluate one predicate against one snapshot slot.</summary>
    private static bool Evaluate(
        SpcPredicate pred, string value, double? numeric,
        string? prevValue, double? prevNumeric)
    {
        switch (pred.Kind)
        {
            case SpcPredicateKind.Any:
                return true;

            case SpcPredicateKind.Exact:
                return numeric.HasValue && pred.Operand.HasValue
                       && numeric.Value == pred.Operand.Value;

            case SpcPredicateKind.NotEqual:
                return numeric.HasValue && pred.Operand.HasValue
                       && numeric.Value != pred.Operand.Value;

            case SpcPredicateKind.Range:
                if (!numeric.HasValue) return false;
                if (pred.Operand.HasValue  && numeric.Value < pred.Operand.Value)  return false;
                if (pred.Operand2.HasValue && numeric.Value > pred.Operand2.Value) return false;
                return true;

            case SpcPredicateKind.Unchanged:
                // Textual equality covers non-numeric fields too — string,
                // pointer, error rows where the value DIDN'T change still
                // get caught even though they aren't parseable as double.
                return prevValue is not null
                       && string.Equals(value, prevValue, StringComparison.Ordinal);

            case SpcPredicateKind.Increased:
                return numeric.HasValue && prevNumeric.HasValue
                       && numeric.Value > prevNumeric.Value;

            case SpcPredicateKind.Decreased:
                return numeric.HasValue && prevNumeric.HasValue
                       && numeric.Value < prevNumeric.Value;

            case SpcPredicateKind.DeltaExact:
            {
                if (!numeric.HasValue || !prevNumeric.HasValue || !pred.Operand.HasValue)
                    return false;
                double d = numeric.Value - prevNumeric.Value;
                return d == pred.Operand.Value;
            }

            case SpcPredicateKind.DeltaRange:
            {
                if (!numeric.HasValue || !prevNumeric.HasValue) return false;
                double d = numeric.Value - prevNumeric.Value;
                if (pred.Operand.HasValue  && d < pred.Operand.Value)  return false;
                if (pred.Operand2.HasValue && d > pred.Operand2.Value) return false;
                return true;
            }
        }
        return false;
    }

    /// <summary>Mirror of <c>SnapshotViewModel.ClassifySide</c>'s numeric
    /// branch — local copy so the engine stays free of ViewModel
    /// internals. Returns null for empty / error / pointer / quoted
    /// string displays.</summary>
    private static double? TryParseNumeric(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s[0] == '"') return null;                                  // quoted string
        if (s.StartsWith("(err:",     StringComparison.Ordinal)) return null;
        if (s.StartsWith("(arr-err:", StringComparison.Ordinal)) return null;
        if (s.StartsWith("ptr=",      StringComparison.Ordinal)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : (double?)null;
    }
}
