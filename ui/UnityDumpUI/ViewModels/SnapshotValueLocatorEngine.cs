// SnapshotValueLocatorEngine — Phase 29i-4 (value-driven class location).
//
// The inverse of the pivot funnel: the user already knows a VALUE they can
// see in-game (money reads 9410; it dropped from 10160) but NOT which class
// holds it. This engine sweeps the hydrated snapshot blob and returns the
// (class, field[, array element]) candidates that carry that value — so the
// user goes value → class instead of guessing the class up front.
//
// Two modes:
//   * Exact   — a (class, field) is a hit if the target value appears in its
//               value set in >=1 snapshot. Ranked by snaps-hit desc, then by
//               match RARITY (a field where only 1 instance equals 9410 is a
//               far stronger signal than one where 500 instances do).
//   * Delta   — numeric only. A (class, field) is a hit if its value universe
//               MOVED by Delta across two chosen snaps: exists Va in set(A),
//               Vb in set(B) with Vb - Va == Delta (within tolerance). This is
//               Cheat Engine's "changed/decreased value" idea, but on the
//               structured blob — and it works by SET MEMBERSHIP, so it needs
//               no per-instance alignment across snaps (that alignment is what
//               Stage 1 + the Pivot do next, on the confirmed field).
//
// Cross-session capable: it reads stored values, not live memory, so unlike
// Value Search it finds the field even across dead game sessions. The handoff
// address is process-local, so it's only live for the still-attached session.
//
// Cost: one linear pass over the hydrated Classes[].Instances[].Fields[] per
// query (same budget as Stage 0). No Jaccard, no per-instance alignment.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed class ValueLocatorQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }

    /// <summary>Exact-match assembly scope (e.g. "Assembly-CSharp"). Null /
    /// whitespace = sweep every assembly.</summary>
    public string? AssemblyFilter { get; init; }

    // ── Exact mode ─────────────────────────────────────────────────────
    /// <summary>The value to find, as the user typed it. Compared against
    /// both the rendered form ("9410", "\"Money\"") and the raw form
    /// (9410, Money) so the user can type either. Required in exact mode.</summary>
    public string? ExactValue { get; init; }

    /// <summary>Optional ± window for numeric matches (handles float jitter
    /// / off-by-one). When set, numeric fields match if within tolerance.</summary>
    public double? NumericTolerance { get; init; }

    // ── Delta mode (set when all three are present) ─────────────────────
    public int?    SnapA { get; init; }
    public int?    SnapB { get; init; }
    /// <summary>Cross-snap change to find, e.g. -750 for 10160 → 9410.</summary>
    public double? Delta { get; init; }

    // ── Shared options ─────────────────────────────────────────────────
    /// <summary>Also sweep array elements: Layer A primitive element values
    /// and Layer B inner element fields (rendered as <c>arrayField[].inner</c>).</summary>
    public bool IncludeArrayElements { get; init; }

    /// <summary>Only consider numeric-valued observations (skip strings /
    /// pointers). Always on in delta mode.</summary>
    public bool NumericOnly { get; init; }

    /// <summary>Include compiler-generated (<c>&lt;…&gt;</c>) classes. Off by
    /// default — they rarely hold the value the user is hunting.</summary>
    public bool IncludeSynthetic { get; init; }

    /// <summary>Cap on returned hits (ranked). Default 200.</summary>
    public int MaxHits { get; init; } = 200;

    /// <summary>Per-field distinct-value cap in delta mode — bounds memory on
    /// pathological fields. Default 4096.</summary>
    public int DeltaSetCap { get; init; } = 4096;
}

public sealed class ValueLocatorHit
{
    public required string  Assembly       { get; init; }
    public required string  ClassFqn       { get; init; }
    /// <summary>Field name; <c>arrayField[]</c> for a Layer A element value,
    /// <c>arrayField[].inner</c> for a Layer B inner element field.</summary>
    public required string  FieldName      { get; init; }
    public required bool    InArrayElement { get; init; }
    public required int     SnapHitCount   { get; init; }
    public required string  SampleValue    { get; init; }
    public required string? SampleAddress  { get; init; }
    /// <summary># observations equal to the target (exact mode; 0 in delta).</summary>
    public required long    MatchObs       { get; init; }
    /// <summary># observations of this field across the swept snaps.</summary>
    public required long    TotalObs       { get; init; }
    /// <summary>Distinct values of this field (delta mode signal; 0 in exact).</summary>
    public required int     Cardinality    { get; init; }

    /// <summary>Fraction of this field's observations that matched the target.
    /// Lower = rarer = stronger signal. 1.0 when unknown.</summary>
    public double MatchRarity => TotalObs > 0 ? (double)MatchObs / TotalObs : 1.0;

    /// <summary>Composite: more snaps hit + rarer match ranks higher.</summary>
    public double Score => SnapHitCount + (1.0 - MatchRarity);

    // Display helpers (AOT-safe; no converters).
    public string SnapHitDisplay => SnapHitCount.ToString(CultureInfo.InvariantCulture);
    public string ArrayDisplay   => InArrayElement ? "yes" : string.Empty;
    public string RarityDisplay  => TotalObs > 0
        ? (MatchRarity * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%"
        : "—";
    public string ScoreDisplay   => Score.ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed class ValueLocatorResult
{
    public required IReadOnlyList<ValueLocatorHit> Hits    { get; init; }
    public required string                         Summary { get; init; }
}

public static class SnapshotValueLocatorEngine
{
    public static ValueLocatorResult Run(ValueLocatorQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Value locator needs at least one snapshot.", nameof(query));

        bool deltaMode = query.Delta.HasValue && query.SnapA.HasValue && query.SnapB.HasValue;
        return deltaMode ? RunDelta(query) : RunExact(query);
    }

    // ── Exact mode ─────────────────────────────────────────────────────

    private sealed class ExactAcc
    {
        public string?       SampleAddress;
        public string        SampleValue = string.Empty;
        public long          MatchObs, TotalObs;
        public HashSet<int>  Snaps = new();
    }

    private static ValueLocatorResult RunExact(ValueLocatorQuery query)
    {
        if (string.IsNullOrEmpty(query.ExactValue))
            throw new ArgumentException("Exact mode needs a value to find.", nameof(query));

        string?  asm    = string.IsNullOrWhiteSpace(query.AssemblyFilter) ? null : query.AssemblyFilter;
        bool     hasTgt = double.TryParse(query.ExactValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double tgtNum);
        double?  tol    = query.NumericTolerance;

        var acc = new Dictionary<(string Asm, string Fqn, string Field, bool InArr), ExactAcc>();

        for (int s = 0; s < query.Snapshots.Count; s++)
        {
            var snap = query.Snapshots[s];
            if (snap.Classes is null) continue;
            foreach (var c in snap.Classes)
            {
                if (asm is not null && !string.Equals(c.Assembly, asm, StringComparison.Ordinal)) continue;
                if (!query.IncludeSynthetic && SnapshotEnvelope.IsSyntheticClassName(c.Name)) continue;
                if (c.Instances is null) continue;
                string fqn = Fqn(c);

                foreach (var inst in c.Instances)
                    foreach (var o in Observations(inst, query.IncludeArrayElements))
                    {
                        if (query.NumericOnly && o.Num is null) continue;

                        var key = (c.Assembly, fqn, o.Field, o.InArr);
                        if (!acc.TryGetValue(key, out var a)) { a = new ExactAcc(); acc[key] = a; }
                        a.TotalObs++;

                        bool match;
                        if (tol.HasValue && hasTgt && o.Num is double dn)
                            match = Math.Abs(dn - tgtNum) <= tol.Value;
                        else
                            match = string.Equals(o.Rendered, query.ExactValue, StringComparison.Ordinal)
                                 || string.Equals(o.Raw,      query.ExactValue, StringComparison.Ordinal);

                        if (!match) continue;
                        a.MatchObs++;
                        a.Snaps.Add(s);
                        if (a.SampleAddress is null) { a.SampleAddress = o.Addr; a.SampleValue = o.Rendered; }
                    }
            }
        }

        var hits = new List<ValueLocatorHit>();
        foreach (var (key, a) in acc)
        {
            if (a.Snaps.Count == 0) continue;
            hits.Add(new ValueLocatorHit
            {
                Assembly = key.Asm, ClassFqn = key.Fqn, FieldName = key.Field,
                InArrayElement = key.InArr,
                SnapHitCount = a.Snaps.Count, SampleValue = a.SampleValue,
                SampleAddress = a.SampleAddress,
                MatchObs = a.MatchObs, TotalObs = a.TotalObs, Cardinality = 0,
            });
        }

        hits.Sort(static (a, b) =>
        {
            int c = b.SnapHitCount.CompareTo(a.SnapHitCount); if (c != 0) return c;
            c = a.MatchRarity.CompareTo(b.MatchRarity);       if (c != 0) return c;  // rarer first
            c = string.CompareOrdinal(a.ClassFqn, b.ClassFqn); if (c != 0) return c;
            return string.CompareOrdinal(a.FieldName, b.FieldName);
        });

        int total = hits.Count;
        if (hits.Count > query.MaxHits) hits = hits.Take(query.MaxHits).ToList();
        string capNote = total > hits.Count ? $" (showing top {hits.Count} of {total})" : string.Empty;
        return new ValueLocatorResult
        {
            Hits    = hits,
            Summary = $"Exact '{query.ExactValue}': {total} candidate field(s){capNote}.",
        };
    }

    // ── Delta mode ─────────────────────────────────────────────────────

    private sealed class DeltaAcc
    {
        public HashSet<double> A = new();
        public HashSet<double> B = new();
        public string?         SampleAddress;   // from snap B
        public bool            ACapped, BCapped;
    }

    private static ValueLocatorResult RunDelta(ValueLocatorQuery query)
    {
        int n = query.Snapshots.Count;
        int ia = query.SnapA!.Value, ib = query.SnapB!.Value;
        if (ia < 0 || ia >= n || ib < 0 || ib >= n)
            throw new ArgumentException("Delta mode snapshot indices out of range.", nameof(query));
        if (ia == ib)
            throw new ArgumentException("Delta mode needs two DIFFERENT snapshots.", nameof(query));

        double delta = query.Delta!.Value;
        double tol   = query.NumericTolerance ?? 1e-6;   // ints exact; floats need a hair
        string? asm  = string.IsNullOrWhiteSpace(query.AssemblyFilter) ? null : query.AssemblyFilter;

        var acc = new Dictionary<(string Asm, string Fqn, string Field, bool InArr), DeltaAcc>();

        CollectDeltaSide(query, query.Snapshots[ia], asm, acc, takeA: true);
        CollectDeltaSide(query, query.Snapshots[ib], asm, acc, takeA: false);

        var hits = new List<ValueLocatorHit>();
        foreach (var (key, a) in acc)
        {
            if (a.A.Count == 0 || a.B.Count == 0) continue;
            // exists Va in A with (Va + delta) in B (within tol)?
            double? matchedVa = null;
            foreach (var va in a.A)
            {
                double want = va + delta;
                foreach (var vb in a.B)
                    if (Math.Abs(vb - want) <= tol) { matchedVa = va; break; }
                if (matchedVa.HasValue) break;
            }
            if (!matchedVa.HasValue) continue;

            int card = Math.Max(a.A.Count, a.B.Count);
            hits.Add(new ValueLocatorHit
            {
                Assembly = key.Asm, ClassFqn = key.Fqn, FieldName = key.Field,
                InArrayElement = key.InArr,
                SnapHitCount = 2,
                SampleValue = $"{Fmt(matchedVa.Value)} → {Fmt(matchedVa.Value + delta)}",
                SampleAddress = a.SampleAddress,
                MatchObs = 0, TotalObs = 0, Cardinality = card,
            });
        }

        // Fewer distinct values whose set moved by Δ = stronger signal.
        hits.Sort(static (a, b) =>
        {
            int c = a.Cardinality.CompareTo(b.Cardinality); if (c != 0) return c;
            c = string.CompareOrdinal(a.ClassFqn, b.ClassFqn); if (c != 0) return c;
            return string.CompareOrdinal(a.FieldName, b.FieldName);
        });

        int total = hits.Count;
        if (hits.Count > query.MaxHits) hits = hits.Take(query.MaxHits).ToList();
        string capNote = total > hits.Count ? $" (showing top {hits.Count} of {total})" : string.Empty;
        return new ValueLocatorResult
        {
            Hits    = hits,
            Summary = $"Delta {Fmt(delta)} (snap {ia} → {ib}): {total} candidate field(s){capNote}.",
        };
    }

    private static void CollectDeltaSide(
        ValueLocatorQuery query, SnapshotEnvelope snap, string? asm,
        Dictionary<(string, string, string, bool), DeltaAcc> acc, bool takeA)
    {
        if (snap.Classes is null) return;
        foreach (var c in snap.Classes)
        {
            if (asm is not null && !string.Equals(c.Assembly, asm, StringComparison.Ordinal)) continue;
            if (!query.IncludeSynthetic && SnapshotEnvelope.IsSyntheticClassName(c.Name)) continue;
            if (c.Instances is null) continue;
            string fqn = Fqn(c);

            foreach (var inst in c.Instances)
                foreach (var o in Observations(inst, query.IncludeArrayElements))
                {
                    if (o.Num is not double dn) continue;   // delta = numeric only
                    var key = (c.Assembly, fqn, o.Field, o.InArr);
                    if (!acc.TryGetValue(key, out var a)) { a = new DeltaAcc(); acc[key] = a; }
                    if (takeA)
                    {
                        if (a.A.Count < query.DeltaSetCap) a.A.Add(dn); else a.ACapped = true;
                    }
                    else
                    {
                        if (a.B.Count < query.DeltaSetCap) a.B.Add(dn); else a.BCapped = true;
                        a.SampleAddress ??= o.Addr;
                    }
                }
        }
    }

    // ── Observation source ─────────────────────────────────────────────

    private readonly struct Obs
    {
        public readonly string  Field;
        public readonly bool    InArr;
        public readonly string  Rendered;
        public readonly string  Raw;
        public readonly double? Num;
        public readonly string? Addr;
        public Obs(string field, bool inArr, string rendered, string raw, double? num, string? addr)
        { Field = field; InArr = inArr; Rendered = rendered; Raw = raw; Num = num; Addr = addr; }
    }

    private static IEnumerable<Obs> Observations(SnapshotInstanceEntry inst, bool includeArrays)
    {
        foreach (var f in inst.Fields)
        {
            var (r, raw, num) = RenderField(f);
            yield return new Obs(f.Name, false, r, raw, num, inst.Address);

            if (!includeArrays || f.ArrayElements is not { } ae) continue;
            foreach (var el in ae.Elements)
            {
                if (el.Fields is { Count: > 0 } inner)
                {
                    foreach (var ef in inner)
                    {
                        var (r2, raw2, num2) = RenderField(ef);
                        yield return new Obs($"{f.Name}[].{ef.Name}", true, r2, raw2, num2, inst.Address);
                    }
                }
                else
                {
                    var (r3, raw3, num3) = RenderElement(el);
                    yield return new Obs($"{f.Name}[]", true, r3, raw3, num3, inst.Address);
                }
            }
        }
    }

    private static (string Rendered, string Raw, double? Num) RenderField(SnapshotFieldEntry f)
    {
        string rendered = SnapshotEnvelope.FormatFieldValue(f);
        if (f.Value is { } fv && fv.ValueKind == JsonValueKind.Number && fv.TryGetDouble(out double d))
            return (rendered, fv.GetRawText(), d);
        if (f.Value is { } fs && fs.ValueKind == JsonValueKind.String)
            return (rendered, fs.GetString() ?? rendered, null);
        if (!string.IsNullOrEmpty(f.StringValue)) return (rendered, f.StringValue, null);
        if (!string.IsNullOrEmpty(f.Ptr))         return (rendered, f.Ptr, null);
        return (rendered, rendered, null);
    }

    private static (string Rendered, string Raw, double? Num) RenderElement(SnapshotArrayElement el)
    {
        if (!string.IsNullOrEmpty(el.Error)) { string e = $"(err: {el.Error})"; return (e, e, null); }
        if (el.Value is { } ev && ev.ValueKind == JsonValueKind.Number && ev.TryGetDouble(out double d))
            return (ev.GetRawText(), ev.GetRawText(), d);
        if (el.Value is { } es && es.ValueKind == JsonValueKind.String)
        { string sv = es.GetString() ?? "(null)"; return (sv, sv, null); }
        if (!string.IsNullOrEmpty(el.String)) return ($"\"{el.String}\"", el.String, null);
        if (!string.IsNullOrEmpty(el.Ptr))    return ($"ptr={el.Ptr}", el.Ptr, null);
        return ("(no value)", "(no value)", null);
    }

    private static string Fqn(SnapshotClassEntry c)
        => string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";

    private static string Fmt(double d)
        => d.ToString("0.######", CultureInfo.InvariantCulture);
}
