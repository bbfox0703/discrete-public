// KeyDiscoveryEngine — Phase 29e-4 (Stage 1 of the Class Pivot funnel).
//
// Once the user has picked a class (Stage 0), they still have to nominate
// WHICH field is the stable business key and WHICH is the value to track.
// On 活俠傳's GameStat that's _propertyID / _type vs _value — but the user
// shouldn't have to know that. Stage 1 scores every field on its
// suitability as a key so the good candidates float to the top.
//
// Per field, four sub-scores in [0,1] (class-pivot.md §4):
//
//   S_stable      avg pairwise Jaccard of the field's distinct-value set
//                 across snapshot pairs. A real key (_propertyID) holds
//                 the same value universe every snap → ~1.0; a volatile
//                 counter (_frameCount) has disjoint sets → ~0.
//
//   S_cardinality distinct values / instance count, averaged over snaps.
//                 1.0 = every instance has a unique value (perfect key);
//                 1/N = the field is a constant.
//
//   TypePrior     int/uint/long = 1.0, string/enum/char = 0.8-0.85,
//                 float/double = 0.20, bool = 0.10, reference = 0.05,
//                 array = 0.0. Keys are almost never floats or bools.
//
//   NamePrior     weak signal from the field name — "id"/"name"/"key"/
//                 "hash"/"type"/"group"/"code" etc. Max of any match.
//
//   Score = 0.40·S_stable + 0.30·S_cardinality + 0.20·TypePrior
//         + 0.10·NamePrior
//
// Then a hard override: address-like fields (System.IntPtr / …Ptr, e.g.
// Unity's m_CachedPtr) are per-instance unique + session-stable, so the raw
// score rates them "perfect keys" — the exact trap this tab exists to avoid,
// since addresses drift across AND within sessions. IsAddressLike zeroes their
// TypePrior and multiplies the composite by AddressKeyPenalty so they sink
// below every real candidate and never win the greedy suggestion.
//
// Plus a greedy compound-key suggestion: start from the top-scored
// field; if it still collides (two instances share its value within a
// snap), add the next-best field that breaks the most collisions, until
// the key is unique in every snap or a 3-field cap is hit. This is the
// answer to the user's "_property=99 collides" problem — the engine
// proposes (_type, _property) or whatever disambiguates.
//
// Cost: one pass to build per-field-per-snap value sets (same budget as
// Stage 0) + a few re-walks for the greedy collision checks.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed class KeyDiscoveryQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    public required string Assembly { get; init; }
    public required string ClassFqn { get; init; }

    /// <summary>Cap on the greedy compound-key length. Default 3 — more
    /// than that and the "key" is really a fingerprint, not a key.</summary>
    public int MaxCompoundKey { get; init; } = 3;
}

public sealed class FieldScore
{
    public required string FieldName    { get; init; }
    public required string FieldType    { get; init; }
    public required bool   IsEnum       { get; init; }
    public required double SStable      { get; init; }
    public required double SCardinality { get; init; }
    public required double TypePrior    { get; init; }
    public required double NamePrior    { get; init; }
    public required double Score        { get; init; }

    // Display helpers (AOT-safe; no converters).
    public string ScoreDisplay  => Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public string StablePctDisplay => Pct(SStable);
    public string CardPctDisplay   => Pct(SCardinality);
    public string TypePriorDisplay => TypePrior.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public string NamePriorDisplay => NamePrior.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string Pct(double r)
        => (r * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

public sealed class KeyDiscoveryResult
{
    /// <summary>All fields, sorted by composite Score descending.</summary>
    public required IReadOnlyList<FieldScore> Fields { get; init; }
    /// <summary>Greedy compound-key proposal (1+ field names).</summary>
    public required IReadOnlyList<string> SuggestedKey { get; init; }
    /// <summary>Human-readable note on the suggestion quality.</summary>
    public required string SuggestedKeyReason { get; init; }
}

public static class KeyDiscoveryEngine
{
    public static KeyDiscoveryResult Run(KeyDiscoveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Key discovery needs at least one snapshot.", nameof(query));
        if (string.IsNullOrEmpty(query.Assembly))
            throw new ArgumentException("Assembly is required.", nameof(query));
        if (string.IsNullOrEmpty(query.ClassFqn))
            throw new ArgumentException("ClassFqn is required.", nameof(query));

        int snapCount = query.Snapshots.Count;

        // fieldName → per-snap distinct value sets.
        var valueSets = new Dictionary<string, List<HashSet<string>>>(StringComparer.Ordinal);
        // fieldName → (type, isEnum) sampled from first observation.
        var fieldMeta = new Dictionary<string, (string Type, bool IsEnum)>(StringComparer.Ordinal);
        var instanceCount = new int[snapCount];

        for (int s = 0; s < snapCount; s++)
        {
            foreach (var inst in InstancesOf(query.Snapshots[s], query.Assembly, query.ClassFqn))
            {
                instanceCount[s]++;
                foreach (var f in inst.Fields)
                {
                    if (!valueSets.TryGetValue(f.Name, out var perSnap))
                    {
                        perSnap = new List<HashSet<string>>(snapCount);
                        for (int k = 0; k < snapCount; k++) perSnap.Add(new HashSet<string>(StringComparer.Ordinal));
                        valueSets[f.Name] = perSnap;
                        fieldMeta[f.Name] = (f.Type, f.IsEnum == true);
                    }
                    perSnap[s].Add(SnapshotEnvelope.FormatFieldValue(f));
                }
            }
        }

        var scores = new List<FieldScore>(valueSets.Count);
        foreach (var (name, perSnap) in valueSets)
        {
            var (type, isEnum) = fieldMeta[name];
            double sStable = AvgPairwiseJaccard(perSnap);

            double cardSum = 0; int snapsWithInst = 0;
            for (int s = 0; s < snapCount; s++)
            {
                if (instanceCount[s] == 0) continue;
                cardSum += (double)perSnap[s].Count / instanceCount[s];
                snapsWithInst++;
            }
            double sCard = snapsWithInst > 0 ? cardSum / snapsWithInst : 0;

            bool addrLike = IsAddressLike(name, type, isEnum);
            double tp = addrLike ? 0.0 : TypePrior(type, isEnum);
            double np = NamePrior(name);
            double score = 0.40 * sStable + 0.30 * sCard + 0.20 * tp + 0.10 * np;
            // Raw addresses / native pointers (System.IntPtr, Unity's m_CachedPtr,
            // any …Ptr) are per-instance unique + session-stable, so the statistical
            // scores rate them as perfect keys — the exact trap this tab exists to
            // avoid, since addresses drift both across AND within sessions. Sink
            // them so they never win the suggestion or sit at the top of the picker.
            if (addrLike) score *= AddressKeyPenalty;

            scores.Add(new FieldScore
            {
                FieldName = name, FieldType = type, IsEnum = isEnum,
                SStable = sStable, SCardinality = sCard, TypePrior = tp, NamePrior = np,
                Score = score,
            });
        }

        scores.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.CompareOrdinal(a.FieldName, b.FieldName);
        });

        var (suggestedKey, reason) = SuggestKey(query, scores);

        return new KeyDiscoveryResult
        {
            Fields             = scores,
            SuggestedKey       = suggestedKey,
            SuggestedKeyReason = reason,
        };
    }

    // ── Greedy compound key ────────────────────────────────────────────

    private static (IReadOnlyList<string> Key, string Reason) SuggestKey(
        KeyDiscoveryQuery query, List<FieldScore> scores)
    {
        if (scores.Count == 0)
            return (Array.Empty<string>(), "No fields to key on.");

        // Only consider fields that make plausible keys (TypePrior > 0 so
        // arrays/null-only fields don't get nominated).
        var candidates = scores.Where(f => f.TypePrior > 0).ToList();
        if (candidates.Count == 0)
            candidates = scores;   // degenerate — fall back to all

        var chosen = new List<string> { candidates[0].FieldName };
        long best = CountCollisions(query, chosen);
        if (best == 0)
            return (chosen, $"'{chosen[0]}' is unique in every snapshot.");

        while (chosen.Count < query.MaxCompoundKey)
        {
            string? bestAdd = null;
            long bestAddColl = best;
            foreach (var f in candidates)
            {
                if (chosen.Contains(f.FieldName)) continue;
                var trial = new List<string>(chosen) { f.FieldName };
                long coll = CountCollisions(query, trial);
                if (coll < bestAddColl)
                {
                    bestAddColl = coll;
                    bestAdd = f.FieldName;
                }
            }
            if (bestAdd is null) break;   // no field reduces collisions
            chosen.Add(bestAdd);
            best = bestAddColl;
            if (best == 0) break;
        }

        string keyStr = string.Join(", ", chosen);
        return best == 0
            ? (chosen, $"({keyStr}) is unique in every snapshot.")
            : (chosen, $"({keyStr}) is the lowest-collision combination — {best} instance(s) still share a key. Add another field or accept the collision.");
    }

    /// <summary>Collisions = Σ_snaps (instanceCount − distinctKeyTuples).
    /// 0 means the key set is unique in every snapshot.</summary>
    private static long CountCollisions(KeyDiscoveryQuery query, List<string> keyFields)
    {
        long collisions = 0;
        foreach (var snap in query.Snapshots)
        {
            int instCount = 0;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inst in InstancesOf(snap, query.Assembly, query.ClassFqn))
            {
                instCount++;
                distinct.Add(BuildKeyTuple(inst, keyFields));
            }
            collisions += instCount - distinct.Count;
        }
        return collisions;
    }

    private static string BuildKeyTuple(SnapshotInstanceEntry inst, List<string> keyFields)
    {
        var fieldMap = new Dictionary<string, SnapshotFieldEntry>(inst.Fields.Count, StringComparer.Ordinal);
        foreach (var f in inst.Fields) fieldMap.TryAdd(f.Name, f);

        var parts = new string[keyFields.Count];
        for (int i = 0; i < keyFields.Count; i++)
            parts[i] = fieldMap.TryGetValue(keyFields[i], out var f)
                ? SnapshotEnvelope.FormatFieldValue(f)
                : "(missing)";
        return string.Join('\x1F', parts);
    }

    private static IEnumerable<SnapshotInstanceEntry> InstancesOf(
        SnapshotEnvelope snap, string assembly, string fqn)
    {
        if (snap.Classes is null) yield break;
        foreach (var c in snap.Classes)
        {
            if (!string.Equals(c.Assembly, assembly, StringComparison.Ordinal)) continue;
            string cfqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            if (!SnapshotEnvelope.ClassFqnEquals(cfqn, fqn)) continue;
            if (c.Instances is null) continue;
            foreach (var inst in c.Instances) yield return inst;
        }
    }

    // ── Sub-scores ─────────────────────────────────────────────────────

    private static double AvgPairwiseJaccard(List<HashSet<string>> sets)
    {
        // Drop snaps with no observations so an absent snap doesn't drag
        // the score; a field present in only one snap is "trivially
        // stable" (1.0) since there's nothing to disagree with.
        var nonEmpty = sets.Where(s => s.Count > 0).ToList();
        if (nonEmpty.Count < 2) return 1.0;

        double sum = 0; int pairs = 0;
        for (int i = 0; i < nonEmpty.Count; i++)
            for (int j = i + 1; j < nonEmpty.Count; j++)
            {
                sum += Jaccard(nonEmpty[i], nonEmpty[j]);
                pairs++;
            }
        return pairs == 0 ? 0 : sum / pairs;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        int inter = 0;
        foreach (var x in a) if (b.Contains(x)) inter++;
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    private static double TypePrior(string type, bool isEnum)
    {
        if (isEnum) return 0.85;
        if (type.EndsWith("[]", StringComparison.Ordinal)) return 0.0;
        return type switch
        {
            "System.Int16" or "System.UInt16" or
            "System.Int32" or "System.UInt32" or
            "System.Int64" or "System.UInt64" or
            "System.Byte"  or "System.SByte"  => 1.00,
            "System.String"                      => 0.85,
            "System.Char"                        => 0.80,
            "System.Single" or "System.Double" or "System.Decimal" => 0.20,
            "System.Boolean"                     => 0.10,
            _                                    => 0.05,   // reference / struct
        };
    }

    /// <summary>Composite-score multiplier for address-like fields — they
    /// can never be a stable cross-session key, so sink them well below any
    /// legitimate candidate while still showing them in the picker.</summary>
    private const double AddressKeyPenalty = 0.10;

    /// <summary>A raw address / native pointer is the one thing class-pivot
    /// must never key on: the whole reason this tab exists is that addresses
    /// drift across (and within) sessions while business field values don't.
    /// Unity's <c>m_CachedPtr</c> is <see cref="System.IntPtr"/>, per-instance
    /// unique and session-stable, so the statistical scores would rate it a
    /// "perfect key" — we override that with domain knowledge here.</summary>
    private static bool IsAddressLike(string name, string type, bool isEnum)
    {
        if (isEnum) return false;
        if (type is "System.IntPtr" or "System.UIntPtr") return true;
        string n = name.ToLowerInvariant();
        return n.EndsWith("ptr", StringComparison.Ordinal)
            || n.EndsWith("pointer", StringComparison.Ordinal);
    }

    private static double NamePrior(string name)
    {
        string n = name.ToLowerInvariant();
        double max = 0;
        void Bump(string sub, double w)
        {
            if (n.Contains(sub, StringComparison.Ordinal) && w > max) max = w;
        }
        Bump("id", 0.30);
        Bump("name", 0.20);
        Bump("key", 0.20);
        Bump("hash", 0.20);
        Bump("type", 0.15);
        Bump("kind", 0.15);
        Bump("group", 0.15);
        Bump("category", 0.15);
        Bump("code", 0.10);
        return max > 1.0 ? 1.0 : max;
    }
}
