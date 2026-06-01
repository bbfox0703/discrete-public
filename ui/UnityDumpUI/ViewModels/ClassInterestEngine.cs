// ClassInterestEngine — Phase 29i-3 (unsupervised "likely game-state" rank).
//
// Stage 0 ranks classes by population STABILITY (instance-count CV). But
// "stable" != "holds the data I care about": a class that never changes and
// a class that is pure noise both score for the wrong reasons. The signal
// this engine adds is value DRIFT under a stable identity:
//
//   A class is interesting  <=>  it has a good key (stable identity) AND a
//   numeric field whose per-key value CHANGES across snapshots but isn't
//   pure noise. That shape — stable identity + drifting value — is exactly
//   what gameplay state (money / HP / exp / counts) looks like.
//
// Two-pass funnel, cost-gated (never runs the expensive pass on every class):
//   Pass A — Stage 0 (ClassShortlistEngine) picks the top-N candidates.
//   Pass B — per candidate (this engine):
//       * run KeyDiscoveryEngine → best key + best-key score
//       * for each numeric non-key field F, volatility(F) = fraction of
//         multi-snap keys whose F value changed, gated so constants (no
//         change) and fully-unique noise (every observation distinct) don't
//         count.
//   interestingness(C) = bestKeyScore(C) * max_F volatility(F).
//
// This is the most HEURISTIC of the 29i waves — "most likely" is inherently
// fuzzy. When the user has a known value, 29i-4 (Find by value) is the
// precise tool; this is the fallback when they don't.
//
// Cost: per candidate, one KeyDiscovery + one volatility pass over the
// class's instances. With top-N ~40 it's a few hundred ms — button-budget.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed class ClassInterestCandidate
{
    public required string Assembly { get; init; }
    public required string ClassFqn { get; init; }
}

public sealed class ClassInterestQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    /// <summary>The Pass-A shortlist (top-N classes). Pass B runs only on
    /// these — the cost gate.</summary>
    public required IReadOnlyList<ClassInterestCandidate> Candidates { get; init; }
    /// <summary>How many top value fields to surface per class. Default 3.</summary>
    public int TopValueFields { get; init; } = 3;
}

public sealed class ClassInterestRow
{
    public required string Assembly { get; init; }
    public required string ClassFqn { get; init; }
    public required double BestKeyScore   { get; init; }
    public required double MaxVolatility  { get; init; }
    public required double InterestScore  { get; init; }
    public required IReadOnlyList<string> KeyFields      { get; init; }
    public required IReadOnlyList<string> TopValueFieldsList { get; init; }

    /// <summary>Sample trajectory lines (best key + top value field), filled
    /// by the ViewModel after ranking while snapshots are hydrated. Shown in
    /// the preview panel on row selection — kept off the engine so it stays
    /// PivotEngine-free.</summary>
    public List<string> Preview { get; } = new();

    // Display helpers (AOT-safe; no converters).
    public string KeyDisplay      => KeyFields.Count > 0 ? "(" + string.Join(", ", KeyFields) + ")" : "(none)";
    public string ValuesDisplay   => TopValueFieldsList.Count > 0 ? string.Join(", ", TopValueFieldsList) : "—";
    public string InterestDisplay => InterestScore.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    public string VolDisplay      => (MaxVolatility * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    public string KeyScoreDisplay => BestKeyScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

public static class ClassInterestEngine
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "System.Byte", "System.SByte",
        "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Decimal",
    };

    public static List<ClassInterestRow> Run(ClassInterestQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Class interest needs at least one snapshot.", nameof(query));

        var rows = new List<ClassInterestRow>(query.Candidates.Count);
        foreach (var cand in query.Candidates)
        {
            var kd = KeyDiscoveryEngine.Run(new KeyDiscoveryQuery
            {
                Snapshots = query.Snapshots, Assembly = cand.Assembly, ClassFqn = cand.ClassFqn,
            });

            var keyFields = kd.SuggestedKey.Count > 0
                ? kd.SuggestedKey.ToList()
                : (kd.Fields.Count > 0 ? new List<string> { kd.Fields[0].FieldName } : new List<string>());
            double bestKeyScore = kd.Fields.Count > 0 ? kd.Fields[0].Score : 0.0;

            var vol = ComputeVolatility(query.Snapshots, cand.Assembly, cand.ClassFqn, keyFields, kd.Fields);
            double maxVol = vol.Count > 0 ? vol.Values.Max() : 0.0;
            var topValues = vol.OrderByDescending(kv => kv.Value)
                               .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                               .Take(query.TopValueFields)
                               .Where(kv => kv.Value > 0)
                               .Select(kv => kv.Key)
                               .ToList();

            rows.Add(new ClassInterestRow
            {
                Assembly = cand.Assembly, ClassFqn = cand.ClassFqn,
                BestKeyScore = bestKeyScore, MaxVolatility = maxVol,
                InterestScore = bestKeyScore * maxVol,
                KeyFields = keyFields, TopValueFieldsList = topValues,
            });
        }

        rows.Sort(static (a, b) =>
        {
            int c = b.InterestScore.CompareTo(a.InterestScore); if (c != 0) return c;
            c = b.MaxVolatility.CompareTo(a.MaxVolatility);      if (c != 0) return c;
            return string.CompareOrdinal(a.ClassFqn, b.ClassFqn);
        });
        return rows;
    }

    /// <summary>Per numeric non-key field F: fraction of keys present in >=2
    /// snapshots whose F value changed across those snaps. Gated so a field
    /// only counts if its global distinct-value count is in [2, totalObs) —
    /// excludes constants (no signal) and fully-unique noise / ids (every
    /// observation distinct).</summary>
    private static Dictionary<string, double> ComputeVolatility(
        IReadOnlyList<SnapshotEnvelope> snaps, string asm, string fqn,
        IReadOnlyList<string> keyFields, IReadOnlyList<FieldScore> fields)
    {
        var keySet = new HashSet<string>(keyFields, StringComparer.Ordinal);
        // Numeric primitives only — enums are categories (not trending
        // values) and bools/strings/pointers aren't game-state quantities.
        var numericFields = fields
            .Where(f => !keySet.Contains(f.FieldName) && !f.IsEnum && NumericTypes.Contains(f.FieldType))
            .Select(f => f.FieldName)
            .ToList();
        if (numericFields.Count == 0 || keyFields.Count == 0)
            return new Dictionary<string, double>();

        // keyTuple -> snaps it appears in
        var keyPresence = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        // (field, keyTuple) -> distinct values across snaps
        var fieldVals = new Dictionary<(string F, string K), HashSet<string>>();
        // field -> global distinct values + total observations (noise gate)
        var globalDistinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var totalObs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var nf in numericFields)
        {
            globalDistinct[nf] = new HashSet<string>(StringComparer.Ordinal);
            totalObs[nf] = 0;
        }

        for (int s = 0; s < snaps.Count; s++)
        {
            foreach (var inst in InstancesOf(snaps[s], asm, fqn))
            {
                var map = new Dictionary<string, SnapshotFieldEntry>(inst.Fields.Count, StringComparer.Ordinal);
                foreach (var f in inst.Fields) map.TryAdd(f.Name, f);

                string kt = BuildKeyTuple(map, keyFields);
                if (!keyPresence.TryGetValue(kt, out var pres)) { pres = new HashSet<int>(); keyPresence[kt] = pres; }
                pres.Add(s);

                foreach (var nf in numericFields)
                {
                    if (!map.TryGetValue(nf, out var fe)) continue;
                    string v = SnapshotEnvelope.FormatFieldValue(fe);
                    var fk = (nf, kt);
                    if (!fieldVals.TryGetValue(fk, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); fieldVals[fk] = set; }
                    set.Add(v);
                    globalDistinct[nf].Add(v);
                    totalObs[nf]++;
                }
            }
        }

        var multiSnapKeys = keyPresence.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var nf in numericFields)
        {
            // Constant gate: a field with <2 distinct values never drifts, so
            // it can't be game-state. (We deliberately do NOT gate on "all
            // values distinct" — money is globally distinct yet exactly what
            // we want; per-key CHANGE is the real signal. Genuine random noise
            // also surfaces here — the preview panel is how the user tells the
            // two apart. 1-A is heuristic by design; 1-B is the precise tool.)
            int gd = globalDistinct[nf].Count;
            if (gd < 2 || totalObs[nf] == 0) { result[nf] = 0.0; continue; }

            int changed = 0, total = 0;
            foreach (var kt in multiSnapKeys)
            {
                if (!fieldVals.TryGetValue((nf, kt), out var set)) continue;
                total++;
                if (set.Count > 1) changed++;
            }
            result[nf] = total > 0 ? (double)changed / total : 0.0;
        }
        return result;
    }

    private static IEnumerable<SnapshotInstanceEntry> InstancesOf(SnapshotEnvelope snap, string asm, string fqn)
    {
        if (snap.Classes is null) yield break;
        foreach (var c in snap.Classes)
        {
            if (!string.Equals(c.Assembly, asm, StringComparison.Ordinal)) continue;
            string cfqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            if (!SnapshotEnvelope.ClassFqnEquals(cfqn, fqn)) continue;
            if (c.Instances is null) continue;
            foreach (var inst in c.Instances) yield return inst;
        }
    }

    private static string BuildKeyTuple(Dictionary<string, SnapshotFieldEntry> map, IReadOnlyList<string> keyFields)
    {
        var parts = new string[keyFields.Count];
        for (int i = 0; i < keyFields.Count; i++)
            parts[i] = map.TryGetValue(keyFields[i], out var f)
                ? SnapshotEnvelope.FormatFieldValue(f)
                : "(missing)";
        return string.Join('\x1F', parts);
    }
}
