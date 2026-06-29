// SpcGroupEngine — G7.
//
// The N-snapshot generalisation of the snapshot group scan: SPC GROUP. Where
// single SPC asks "which ONE field follows this per-snapshot predicate chain
// across N snapshots", SPC Group asks "which OBJECT has N fields that EACH
// follow their OWN per-snapshot chain, at DISTINCT offsets" — the spatial-AND
// (group SDR) layered on the temporal axis (SPC chains).
//
// Leaf source: the same N-way FieldIndex intersection SpcQueryEngine uses; each
// surviving field carries its value SEQUENCE across the selected snapshots.
// Group the survivors by object, then per slot select the fields whose sequence
// satisfies that slot's predicate chain (SpcQueryEngine.MatchesChain — the exact
// single-SPC semantics, reused), and require a System of Distinct Representatives
// across all slots (GroupMatch).
//
// Pure C# over loaded rows; emits the shared GroupCandidate / GroupSlotMatch DTOs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

/// <summary>One slot of an SPC group query: a width scope + a per-snapshot
/// predicate chain (one <see cref="SpcPredicate"/> per selected snapshot).</summary>
public sealed class SpcGroupSlot
{
    public GroupMatch.Scope Scope { get; init; } = GroupMatch.Scope.NumericNoByte;
    public required IReadOnlyList<SpcPredicate> Predicates { get; init; }
}

public sealed class SpcGroupQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    public required IReadOnlyList<SpcGroupSlot>     Slots     { get; init; }
    public bool    StaticsOnly      { get; init; }
    public bool    ExcludeSynthetic { get; init; } = true;
    public string? ClassFilter      { get; init; }
    public int     MaxResults       { get; init; } = 500;
}

public static class SpcGroupEngine
{
    public static List<GroupCandidate> Run(SpcGroupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        int n = query.Snapshots.Count;
        if (n == 0) throw new ArgumentException("SPC group needs at least one snapshot.", nameof(query));
        if (query.Slots.Count < 2 || query.Slots.Count > 4)
            throw new ArgumentException("SPC group needs 2..4 slots.", nameof(query));
        foreach (var slot in query.Slots)
        {
            if (slot.Predicates.Count != n)
                throw new ArgumentException(
                    $"Each slot's predicate count ({slot.Predicates.Count}) must match the snapshot count ({n}).",
                    nameof(query));
            if (slot.Predicates[0].IsRelative)
                throw new ArgumentException(
                    "A slot's first predicate cannot be relative (no previous snapshot to compare against).",
                    nameof(query));
        }

        // Hydrate + anchor on the smallest FieldIndex (fewest TryGetValue probes).
        var indexes = query.Snapshots.Select(s => s.EnsureFieldIndex()).ToList();
        int anchorIdx = 0;
        for (int i = 1; i < indexes.Count; i++)
            if (indexes[i].Count < indexes[anchorIdx].Count) anchorIdx = i;
        var anchor = indexes[anchorIdx];

        string? classFilter = string.IsNullOrWhiteSpace(query.ClassFilter)
            ? null
            : SnapshotEnvelope.NormalizeClassSeparators(query.ClassFilter);

        // Per surviving field (present in ALL snapshots): its anchor row + the
        // value/numeric sequence, bucketed by object.
        var byObject = new Dictionary<(string asm, string fqn, string inst), List<FieldSeq>>();

        foreach (var (key, anchorVal) in anchor)
        {
            if (query.StaticsOnly && !anchorVal.IsStatic) continue;
            if (query.ExcludeSynthetic && anchorVal.IsSynthetic) continue;
            if (!GroupMatch.TypeInScope(anchorVal.FieldType, GroupMatch.Scope.NumericAll)) continue;
            if (classFilter is not null &&
                SnapshotEnvelope.NormalizeClassSeparators(anchorVal.ClassFqn)
                    .IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var values = new string[n];
            var nums   = new double?[n];
            bool inAll = true;
            for (int i = 0; i < n; i++)
            {
                SnapshotFieldValue fv;
                if (i == anchorIdx) fv = anchorVal;
                else if (!indexes[i].TryGetValue(key, out var found)) { inAll = false; break; }
                else fv = found;
                values[i] = fv.ValueDisplay;
                nums[i]   = GroupMatch.TryParseNumeric(fv.ValueDisplay);
            }
            if (!inAll) continue;

            var k = (anchorVal.Assembly, anchorVal.ClassFqn, anchorVal.InstanceAddress);
            if (!byObject.TryGetValue(k, out var list))
                byObject[k] = list = new List<FieldSeq>();
            list.Add(new FieldSeq(anchorVal, values, nums));
        }

        var results = new List<GroupCandidate>();
        foreach (var kv in byObject)
        {
            if (results.Count >= query.MaxResults) break;
            var fields = kv.Value;

            // Per-slot membership: fields whose sequence satisfies the slot's
            // chain AND whose width is in the slot's scope.
            var perSlot = new List<int>[query.Slots.Count];
            bool anyEmpty = false;
            for (int s = 0; s < query.Slots.Count; s++)
            {
                var slot = query.Slots[s];
                var hits = new List<int>();
                for (int fi = 0; fi < fields.Count; fi++)
                {
                    var f = fields[fi];
                    if (!GroupMatch.TypeInScope(f.Anchor.FieldType, slot.Scope)) continue;
                    if (SpcQueryEngine.MatchesChain(slot.Predicates, f.Values, f.Nums))
                        hits.Add(fi);
                }
                perSlot[s] = hits;
                if (hits.Count == 0) { anyEmpty = true; break; }
            }
            if (anyEmpty) continue;
            if (!GroupMatch.HasDistinctAssignment(perSlot, fields.Count)) continue;
            var asg = GroupMatch.Assignment(perSlot, fields.Count);
            if (asg is null) continue;

            results.Add(BuildCandidate(fields, perSlot, asg, n));
        }

        results.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Class, b.Class);
            return c != 0 ? c : string.CompareOrdinal(a.InstanceAddr, b.InstanceAddr);
        });
        return results;
    }

    private readonly struct FieldSeq
    {
        public FieldSeq(SnapshotFieldValue anchor, string[] values, double?[] nums)
        { Anchor = anchor; Values = values; Nums = nums; }
        public SnapshotFieldValue Anchor { get; }
        public string[]  Values { get; }
        public double?[] Nums   { get; }
    }

    private static GroupCandidate BuildCandidate(
        List<FieldSeq> fields, List<int>[] perSlot, int[] assignment, int n)
    {
        var first = fields[0].Anchor;
        var slotMatches = new List<GroupSlotMatch>(perSlot.Length);
        for (int s = 0; s < perSlot.Length; s++)
        {
            var sm = new GroupSlotMatch
            {
                SlotIndex      = s,
                MatchedOffsets = perSlot[s].Select(fi => (long)fields[fi].Anchor.FieldOffset).ToList(),
                Locked         = perSlot[s].Count == 1,
            };
            int rep = (s < assignment.Length) ? assignment[s] : -1;
            if (rep >= 0 && rep < fields.Count)
            {
                var f = fields[rep];
                string newest = f.Values[n - 1];      // last selected snapshot
                double? num   = f.Nums[n - 1];
                sm.FieldOffset  = (long)f.Anchor.FieldOffset;
                sm.Field        = f.Anchor.FieldName;
                sm.FieldType    = f.Anchor.FieldType;
                sm.FieldKind    = f.Anchor.IsStatic ? "static" : "leaf";
                sm.CurrentValue = MakeValueElement(num, newest);
            }
            slotMatches.Add(sm);
        }

        return new GroupCandidate
        {
            InstanceAddr = string.IsNullOrEmpty(first.InstanceAddress) ? "(static)" : first.InstanceAddress,
            Assembly     = first.Assembly,
            Namespace    = first.Namespace,
            Class        = first.ClassName,
            Slots        = slotMatches,
        };
    }

    private static JsonElement MakeValueElement(double? num, string display)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            if (num.HasValue && double.IsFinite(num.Value)) w.WriteNumberValue(num.Value);
            else w.WriteStringValue(display ?? string.Empty);
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }
}
