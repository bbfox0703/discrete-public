// SnapshotGroupEngine — G6.
//
// Multiple-values GROUP scan over the captured-snapshot corpus — the
// Orden/GroupMatch reuse target the group-scan plan reserved. Finds objects in
// a snapshot that hold ALL of N values at DISTINCT numeric fields (a System of
// Distinct Representatives), in any order. Two modes:
//
//   Mode A (1 snapshot)  — absolute predicates (Exact/Bigger/Smaller/Between)
//                          over each field's value. A frozen instant.
//   Mode B (2 snapshots) — relative predicates (Changed/Unchanged/Increased/
//                          Decreased) over each field's first-vs-last value, the
//                          motivating "Current HP ↓ + Max HP unchanged" case.
//
// Leaf source: SnapshotEnvelope.FieldIndex (already built for Diff/SPC) grouped
// by (Assembly, ClassFqn, InstanceAddress). Numerics are parsed from
// ValueDisplay exactly like SpcQueryEngine. Pure C# over loaded rows — no DLL
// round-trip, no I/O. The matcher itself is GroupMatch (the C# Orden port).
//
// Emits the shared GroupCandidate / GroupSlotMatch DTOs so the UI binds the same
// master-detail template as the live Value Search group scan.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

/// <summary>One slot of a snapshot group query.</summary>
public sealed class SnapshotGroupSlot
{
    public GroupMatch.Scope     Scope     { get; init; } = GroupMatch.Scope.NumericNoByte;
    public GroupMatch.Predicate Predicate { get; init; } = GroupMatch.Predicate.Exact;
    public double? Target  { get; init; }
    public double? Target2 { get; init; }
}

/// <summary>Inputs to <see cref="SnapshotGroupEngine.Run"/>. Snapshots MUST have
/// their FieldIndex populated (caller calls <c>EnsureFieldIndex</c>). One
/// snapshot = Mode A; two = Mode B (compared first→last).</summary>
public sealed class SnapshotGroupQuery
{
    public required IReadOnlyList<SnapshotEnvelope>  Snapshots { get; init; }
    public required IReadOnlyList<SnapshotGroupSlot> Slots     { get; init; }

    /// <summary>Only match static-field rows. Forced ON when snapshots span
    /// different game sessions (instance addresses don't align cross-session),
    /// mirroring SPC.</summary>
    public bool StaticsOnly { get; init; }

    /// <summary>Skip synthetic (compiler-generated) class rows. Default true.</summary>
    public bool ExcludeSynthetic { get; init; } = true;

    /// <summary>Optional case-insensitive substring filter on ClassFqn.</summary>
    public string? ClassFilter { get; init; }

    /// <summary>Max group candidates returned.</summary>
    public int MaxResults { get; init; } = 500;
}

public static class SnapshotGroupEngine
{
    public static List<GroupCandidate> Run(SnapshotGroupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Group scan needs at least one snapshot.", nameof(query));
        if (query.Snapshots.Count > 2)
            throw new ArgumentException("Snapshot group v1 supports 1 (absolute) or 2 (temporal) snapshots.", nameof(query));
        if (query.Slots.Count < 2 || query.Slots.Count > 4)
            throw new ArgumentException("Group scan needs 2..4 slots.", nameof(query));

        var anchorIndex = query.Snapshots[0].EnsureFieldIndex();
        var otherIndex  = query.Snapshots.Count > 1 ? query.Snapshots[1].EnsureFieldIndex() : null;
        string? classFilter = string.IsNullOrWhiteSpace(query.ClassFilter)
            ? null
            : SnapshotEnvelope.NormalizeClassSeparators(query.ClassFilter);

        var slots = query.Slots.Select(s => new GroupMatch.Slot
        {
            Scope = s.Scope, Predicate = s.Predicate, Target = s.Target, Target2 = s.Target2,
        }).ToList();

        // Group numeric fields by object → per-object leaf list.
        var byObject = new Dictionary<(string asm, string fqn, string inst), List<(SnapshotFieldValue fv, GroupMatch.Leaf leaf)>>();

        foreach (var (key, fv) in anchorIndex)
        {
            if (query.StaticsOnly && !fv.IsStatic) continue;
            if (query.ExcludeSynthetic && fv.IsSynthetic) continue;
            if (!GroupMatch.TypeInScope(fv.FieldType, GroupMatch.Scope.NumericAll)) continue;  // numeric only
            if (classFilter is not null &&
                SnapshotEnvelope.NormalizeClassSeparators(fv.ClassFqn)
                    .IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            string[]  displays;
            double?[] nums;
            if (otherIndex is null)
            {
                displays = new[] { fv.ValueDisplay };
                nums     = new[] { GroupMatch.TryParseNumeric(fv.ValueDisplay) };
            }
            else
            {
                // Mode B: the field must exist in BOTH snapshots to compare.
                if (!otherIndex.TryGetValue(key, out var fvB)) continue;
                displays = new[] { fv.ValueDisplay, fvB.ValueDisplay };
                nums     = new[] { GroupMatch.TryParseNumeric(fv.ValueDisplay),
                                   GroupMatch.TryParseNumeric(fvB.ValueDisplay) };
            }

            var leaf = new GroupMatch.Leaf
            {
                Offset    = unchecked((int)fv.FieldOffset),
                FieldType = fv.FieldType,
                Display   = displays,
                Num       = nums,
            };
            var k = (fv.Assembly, fv.ClassFqn, fv.InstanceAddress);
            if (!byObject.TryGetValue(k, out var list))
                byObject[k] = list = new List<(SnapshotFieldValue, GroupMatch.Leaf)>();
            list.Add((fv, leaf));
        }

        var results = new List<GroupCandidate>();
        foreach (var kv in byObject)
        {
            if (results.Count >= query.MaxResults) break;
            var fields = kv.Value;
            var leaves = fields.Select(f => f.leaf).ToList();
            if (!GroupMatch.Run(leaves, slots, out var perSlot)) continue;
            var asg = GroupMatch.Assignment(perSlot, leaves.Count);
            if (asg is null) continue;

            results.Add(BuildCandidate(fields, perSlot, asg, query.Snapshots.Count));
        }

        // Deterministic order: by class then instance.
        results.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Class, b.Class);
            return c != 0 ? c : string.CompareOrdinal(a.InstanceAddr, b.InstanceAddr);
        });
        return results;
    }

    private static GroupCandidate BuildCandidate(
        List<(SnapshotFieldValue fv, GroupMatch.Leaf leaf)> fields,
        List<int>[] perSlot, int[] assignment, int snapCount)
    {
        var first = fields[0].fv;
        var slotMatches = new List<GroupSlotMatch>(perSlot.Length);
        for (int s = 0; s < perSlot.Length; s++)
        {
            var offsets = perSlot[s].Select(li => (long)fields[li].fv.FieldOffset).ToList();
            var sm = new GroupSlotMatch
            {
                SlotIndex      = s,
                MatchedOffsets = offsets,
                Locked         = perSlot[s].Count == 1,
            };
            int rep = (s < assignment.Length) ? assignment[s] : -1;
            if (rep >= 0 && rep < fields.Count)
            {
                var (fv, leaf) = fields[rep];
                // Show the NEWEST value: the last index of the per-snapshot sequence
                // (snapshot B in Mode B). fv is always the snapshot-A row, so sourcing
                // the display from fv.ValueDisplay alone showed the stale snapshot-A
                // value in a Decreased/Unchanged workflow. leaf.Display/Num carry both
                // snapshots (populated above); fall back to the anchor value if absent.
                int last = snapCount - 1;
                string display = (last >= 0 && last < leaf.Display.Count)
                    ? leaf.Display[last] : fv.ValueDisplay;
                double? num = (last >= 0 && last < leaf.Num.Count)
                    ? leaf.Num[last] : GroupMatch.TryParseNumeric(display);
                sm.FieldOffset  = (long)fv.FieldOffset;
                sm.Field        = fv.FieldName;
                sm.FieldType    = fv.FieldType;
                sm.FieldKind    = fv.IsStatic ? "static" : "leaf";
                sm.CurrentValue = MakeValueElement(num, display);
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

    /// <summary>AOT-safe JsonElement from a numeric value (or a string fallback)
    /// via Utf8JsonWriter — no reflection. Matches the live path's
    /// <c>currentValue</c> shape so the UI row renders identically.</summary>
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
