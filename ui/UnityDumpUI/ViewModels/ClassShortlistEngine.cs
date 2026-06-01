// ClassShortlistEngine — Phase 29e-3 (Stage 0 of the Class Pivot funnel).
//
// A real Unity game has thousands of classes (活俠傳 Assembly-CSharp =
// 3,647). Asking the user to know the FQN up front is the weak point of
// the 29e-2 manual pivot. Stage 0 scores every class on "is this worth
// pivoting?" so the user can shortlist by stability instead of memory.
//
// For each class C present across the selected snapshots we compute:
//   * Presence        — how many snaps have ≥1 instance of C (must = N
//                        to be a clean pivot candidate; partial presence
//                        means instances appear/vanish between snaps)
//   * InstancesPerSnap — the per-snap live-instance count
//   * Avg / StdDev / CV — CV (= StdDev / max(Avg,1)) is the headline
//                        stability metric. Low CV = the population is
//                        steady across snaps (good — pivot keys line up);
//                        high CV (e.g. a class that's 100 instances in
//                        one snap and 3 in another) is volatile and a
//                        poor pivot target.
//   * FieldCount       — distinct field names (tiny classes have no key
//                        to pivot on)
//   * Primitive / String / Array / Null field ratios — what KIND of
//                        fields C has. High primitive ratio = lots of
//                        numeric fields = good key/value candidates.
//                        High null ratio = noisy. High array ratio = the
//                        class would benefit from array expansion (29e-1b)
//                        / Layer B.
//   * IsSynthetic      — compiler-generated (`<…>`); hidden by default.
//
// The engine returns ALL classes, sorted by the default funnel order
// (Presence desc, CV asc, FieldCount desc, FQN asc). The ViewModel
// applies the show/hide filters so toggles don't require a re-run.
//
// Cost: one pass over Classes[].Instances[].Fields[] per snapshot.
// 活俠傳 = ~15 K instances × ~15 fields × 5 snaps ≈ 1 M field
// observations — well under the "click → see table" budget.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed class ClassShortlistQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }

    /// <summary>Exact-match assembly filter (e.g. "Assembly-CSharp").
    /// Null / whitespace = include every assembly.</summary>
    public string? AssemblyFilter { get; init; }
}

/// <summary>One field of a shortlisted class — name + type + kind flags.
/// Retained by Stage 0 (29i-1) so the class-structure browser can show a
/// class's full field shape without a per-class FQN round-trip + hydrate.
/// First-seen type wins (instances of one class share a layout; chunked
/// entries just re-confirm it).</summary>
public sealed class ClassFieldInfo
{
    public required string Name    { get; init; }
    public required string Type    { get; init; }
    public required bool   IsEnum  { get; init; }
    public required bool   IsArray { get; init; }

    // Display helpers (AOT-safe; no converters).
    public string TypeDisplay  => Type;
    public string FlagsDisplay => IsArray ? "array" : (IsEnum ? "enum" : string.Empty);
}

public sealed class ClassShortlistRow
{
    public required string Assembly { get; init; }
    public required string ClassFqn { get; init; }

    /// <summary>Snaps with ≥1 instance of this class.</summary>
    public required int Presence  { get; init; }
    /// <summary>Total snaps considered (== query.Snapshots.Count).</summary>
    public required int SnapCount { get; init; }

    public required IReadOnlyList<int> InstancesPerSnap { get; init; }
    public required double AvgInstances    { get; init; }
    public required double StdDevInstances { get; init; }
    /// <summary>Coefficient of variation = StdDev / max(Avg, 1). Lower
    /// is more stable. 0 = identical count in every snap.</summary>
    public required double Cv { get; init; }

    public required int    FieldCount          { get; init; }
    public required double PrimitiveFieldRatio { get; init; }
    public required double StringFieldRatio    { get; init; }
    public required double ArrayFieldRatio     { get; init; }
    public required double NullFieldRatio      { get; init; }

    public required bool IsSynthetic { get; init; }

    /// <summary>Full field shape (first-seen order). Drives the 29i-1
    /// class-structure browser's preview column + detail panel.</summary>
    public required IReadOnlyList<ClassFieldInfo> Fields { get; init; }

    // ── Display helpers (AOT-safe; no converters) ──────────────────────
    public string PresenceDisplay  => $"{Presence}/{SnapCount}";

    /// <summary>First 10 field names, comma-joined, with a "+N more" tail.
    /// Bound by the shortlist grid's "Fields (preview)" column; full set is
    /// in <see cref="Fields"/> (detail panel + ToolTip).</summary>
    public string FieldPreviewDisplay
    {
        get
        {
            if (Fields.Count == 0) return string.Empty;
            const int kPreview = 10;
            string s = string.Join(", ", Fields.Take(kPreview).Select(f => f.Name));
            if (Fields.Count > kPreview) s += $"  +{Fields.Count - kPreview} more";
            return s;
        }
    }
    public string InstancesDisplay => $"{AvgInstances:0} ± {StdDevInstances:0.0}";
    public string CvDisplay        => Cv.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    public string PrimitivePctDisplay => Pct(PrimitiveFieldRatio);
    public string StringPctDisplay    => Pct(StringFieldRatio);
    public string ArrayPctDisplay     => Pct(ArrayFieldRatio);
    public string NullPctDisplay      => Pct(NullFieldRatio);

    private static string Pct(double r)
        => (r * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

public static class ClassShortlistEngine
{
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte", "System.SByte",
        "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Decimal",
        "System.Char", "System.IntPtr", "System.UIntPtr",
    };

    private sealed class Accumulator
    {
        public required string Assembly;
        public required string ClassFqn;
        public required bool   IsSynthetic;
        public int[]               PerSnap     = Array.Empty<int>();
        // First-seen field shape, ordered. Replaces the old name-only
        // HashSet (29i-1) so the structure browser can render type + kind.
        public Dictionary<string, ClassFieldInfo> Fields = new(StringComparer.Ordinal);
        public List<string>        FieldOrder  = new();
        public long TotalFieldObs, NullObs, PrimitiveObs, StringObs, ArrayObs;
    }

    public static List<ClassShortlistRow> Run(ClassShortlistQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Class shortlist needs at least one snapshot.", nameof(query));

        int snapCount = query.Snapshots.Count;
        string? asmFilter = string.IsNullOrWhiteSpace(query.AssemblyFilter) ? null : query.AssemblyFilter;

        var byKey = new Dictionary<(string Asm, string Fqn), Accumulator>();

        for (int s = 0; s < snapCount; s++)
        {
            var snap = query.Snapshots[s];
            if (snap.Classes is null) continue;

            foreach (var c in snap.Classes)
            {
                if (asmFilter is not null &&
                    !string.Equals(c.Assembly, asmFilter, StringComparison.Ordinal))
                    continue;

                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                var key = (c.Assembly, fqn);
                if (!byKey.TryGetValue(key, out var acc))
                {
                    acc = new Accumulator
                    {
                        Assembly    = c.Assembly,
                        ClassFqn    = fqn,
                        IsSynthetic = SnapshotEnvelope.IsSyntheticClassName(c.Name),
                        PerSnap     = new int[snapCount],
                    };
                    byKey[key] = acc;
                }

                if (c.Instances is null) continue;
                acc.PerSnap[s] += c.Instances.Count;   // chunked entries accumulate

                foreach (var inst in c.Instances)
                {
                    foreach (var f in inst.Fields)
                    {
                        if (!acc.Fields.ContainsKey(f.Name))
                        {
                            acc.Fields[f.Name] = new ClassFieldInfo
                            {
                                Name    = f.Name,
                                Type    = f.Type,
                                IsEnum  = f.IsEnum == true,
                                IsArray = IsArray(f),
                            };
                            acc.FieldOrder.Add(f.Name);
                        }
                        acc.TotalFieldObs++;
                        if (IsNullish(f)) acc.NullObs++;

                        // Mutually-exclusive kind buckets (array > string >
                        // primitive > reference). Ratios sum to ≤ 1; the
                        // remainder is reference / struct fields.
                        if (IsArray(f))           acc.ArrayObs++;
                        else if (IsString(f))     acc.StringObs++;
                        else if (IsPrimitive(f))  acc.PrimitiveObs++;
                    }
                }
            }
        }

        var rows = new List<ClassShortlistRow>(byKey.Count);
        foreach (var acc in byKey.Values)
        {
            int presence = acc.PerSnap.Count(n => n > 0);
            double avg = acc.PerSnap.Average();
            double variance = acc.PerSnap.Select(n => (n - avg) * (n - avg)).Sum() / snapCount;
            double stddev = Math.Sqrt(variance);
            double cv = stddev / Math.Max(avg, 1.0);

            double total = acc.TotalFieldObs > 0 ? acc.TotalFieldObs : 1;
            rows.Add(new ClassShortlistRow
            {
                Assembly            = acc.Assembly,
                ClassFqn            = acc.ClassFqn,
                Presence            = presence,
                SnapCount           = snapCount,
                InstancesPerSnap    = acc.PerSnap,
                AvgInstances        = avg,
                StdDevInstances     = stddev,
                Cv                  = cv,
                FieldCount          = acc.Fields.Count,
                Fields              = acc.FieldOrder.Select(n => acc.Fields[n]).ToList(),
                PrimitiveFieldRatio = acc.PrimitiveObs / total,
                StringFieldRatio    = acc.StringObs    / total,
                ArrayFieldRatio     = acc.ArrayObs     / total,
                NullFieldRatio      = acc.NullObs      / total,
                IsSynthetic         = acc.IsSynthetic,
            });
        }

        // Default funnel order: full-presence + stable + field-rich first.
        rows.Sort((a, b) =>
        {
            int c = b.Presence.CompareTo(a.Presence);       // presence desc
            if (c != 0) return c;
            c = a.Cv.CompareTo(b.Cv);                         // CV asc
            if (c != 0) return c;
            c = b.FieldCount.CompareTo(a.FieldCount);         // field count desc
            if (c != 0) return c;
            return string.CompareOrdinal(a.ClassFqn, b.ClassFqn); // stable tiebreak
        });
        return rows;
    }

    private static bool IsArray(SnapshotFieldEntry f)
        => f.ArrayElements is not null
        || !string.IsNullOrEmpty(f.ArrayError)
        || f.Type.EndsWith("[]", StringComparison.Ordinal);

    private static bool IsString(SnapshotFieldEntry f)
        => string.Equals(f.Type, "System.String", StringComparison.Ordinal);

    private static bool IsPrimitive(SnapshotFieldEntry f)
        => f.IsEnum == true || PrimitiveTypes.Contains(f.Type);

    /// <summary>Field carries no usable value: an error, a missing
    /// payload, or a null pointer. Mirrors the "(no value)" / "(err:)"
    /// tail of FormatFieldValue plus an explicit 0-pointer check.</summary>
    private static bool IsNullish(SnapshotFieldEntry f)
    {
        if (!string.IsNullOrEmpty(f.Error)) return true;
        if (f.Value.HasValue &&
            f.Value.Value.ValueKind != System.Text.Json.JsonValueKind.Null &&
            f.Value.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            return false;
        if (!string.IsNullOrEmpty(f.StringValue)) return false;
        if (f.ArrayElements is { Count: > 0 }) return false;
        if (!string.IsNullOrEmpty(f.Ptr) && !IsZeroPtr(f.Ptr)) return false;
        return true;
    }

    private static bool IsZeroPtr(string ptr)
    {
        // "0x0", "0x0000000000000000", "0" → null reference.
        ReadOnlySpan<char> s = ptr.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        foreach (var ch in s) if (ch != '0') return false;
        return s.Length > 0;
    }
}
