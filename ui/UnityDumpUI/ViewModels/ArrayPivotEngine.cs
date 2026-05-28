// ArrayPivotEngine — Phase 29f-3 (Layer B).
//
// The sibling of PivotEngine for REFERENCE-type array elements. Where
// PivotEngine groups whole instances of a class by a key field, this engine
// drills one level deeper: it pivots the ELEMENTS of a reference array field
// (e.g. Mortal.Core.GameStat._statAdditionData : StatConvertCollectionData[])
// by a field INSIDE each element.
//
// Why: keying array elements by index (Layer A) breaks when the game shuffles
// or resizes the array between snaps. Real game arrays carry a stable business
// key inside each element (_propertyID, _itemGroup), so pivoting on that inner
// value tracks the same logical element across snapshots regardless of its
// slot — the same address-drift sidestep PivotEngine does for instances, one
// level down.
//
// Key shape — compound (user decision 2026-05-28):
//   (owner-instance key tuple) + (element inner key tuple)
// The outer key identifies the owning instance (exactly PivotEngine's job);
// the inner key identifies the element within that instance's array. Both
// segments are value-based, so both dodge address drift. OuterKeyFields may
// be empty (degenerate flat mode — elements pooled across all instances, with
// collisions surfaced as usual).
//
// Requires Layer B capture: each SnapshotArrayElement must carry its own
// Fields[] (DLL arrayElementFields=true, Phase 29f-2). Elements without inner
// fields — pre-Layer-B snapshots, primitive arrays, or element_class_error
// slots — are skipped (they can't be keyed by an inner field).
//
// Reuses PivotRow + PivotEngine.FormatCollisionCell so the UI consumes array
// pivots and instance pivots through the same row shape.

using System;
using System.Collections.Generic;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

/// <summary>Inputs to <see cref="ArrayPivotEngine.Run"/>. Snapshots MUST
/// already have their <c>Classes</c> populated (ClassesLoaded == true).</summary>
public sealed class ArrayPivotQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }

    /// <summary>Declaring assembly of the owning class. Ordinal match.</summary>
    public required string Assembly { get; init; }

    /// <summary>Fully-qualified owning class name (e.g.
    /// <c>"Mortal.Core.GameStat"</c>). Ordinal match.</summary>
    public required string ClassFqn { get; init; }

    /// <summary>Name of the reference-array field on the class whose elements
    /// are pivoted (e.g. <c>"_statAdditionData"</c>).</summary>
    public required string ArrayField { get; init; }

    /// <summary>Owner-instance key fields — identify the instance that owns
    /// the array (same role as <see cref="PivotQuery.KeyFields"/>). May be
    /// empty to pool elements across every instance of the class.</summary>
    public IReadOnlyList<string> OuterKeyFields { get; init; } = Array.Empty<string>();

    /// <summary>Element key fields — identify an element within the array by
    /// its own field value(s). At least one required.</summary>
    public required IReadOnlyList<string> InnerKeyFields { get; init; }

    /// <summary>Element value fields — the element values projected across
    /// snapshots. At least one required.</summary>
    public required IReadOnlyList<string> ValueFields { get; init; }

    /// <summary>Optional case-insensitive substring filter on the rendered
    /// key tuple (parts joined with ", "). Null / whitespace = no filter.</summary>
    public string? KeyFilter { get; init; }
}

public static class ArrayPivotEngine
{
    private const char KeyDelim = '\x1F';

    /// <summary>Run the array-element pivot. Returns one <see cref="PivotRow"/>
    /// per distinct (outer key ⊕ inner key) tuple observed across the union of
    /// snapshots, in dictionary-iteration order (caller sorts in the grid).
    /// <see cref="PivotRow.KeyTuple"/> is the outer key parts followed by the
    /// inner key parts. <see cref="PivotRow.AddressPerSnap"/> is the element
    /// object's address (falling back to the owning instance's address when
    /// the element didn't carry one).</summary>
    public static List<PivotRow> Run(ArrayPivotQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Array pivot needs at least one snapshot.", nameof(query));
        if (string.IsNullOrEmpty(query.Assembly))
            throw new ArgumentException("Assembly name is required.", nameof(query));
        if (string.IsNullOrEmpty(query.ClassFqn))
            throw new ArgumentException("ClassFqn is required.", nameof(query));
        if (string.IsNullOrEmpty(query.ArrayField))
            throw new ArgumentException("ArrayField is required.", nameof(query));
        if (query.InnerKeyFields.Count == 0)
            throw new ArgumentException("Array pivot needs at least one inner key field.", nameof(query));
        if (query.ValueFields.Count == 0)
            throw new ArgumentException("Array pivot needs at least one value field.", nameof(query));

        int snapCount  = query.Snapshots.Count;
        int outerCount = query.OuterKeyFields.Count;
        int innerCount = query.InnerKeyFields.Count;
        int valueCount = query.ValueFields.Count;

        var perSnap = new Dictionary<string, List<(string Addr, string?[] Vals)>>[snapCount];

        for (int s = 0; s < snapCount; s++)
        {
            var dict = new Dictionary<string, List<(string Addr, string?[] Vals)>>(StringComparer.Ordinal);
            perSnap[s] = dict;

            var snap = query.Snapshots[s];
            if (snap.Classes is null) continue;

            foreach (var c in snap.Classes)
            {
                if (!string.Equals(c.Assembly, query.Assembly, StringComparison.Ordinal)) continue;
                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                if (!SnapshotEnvelope.ClassFqnEquals(fqn, query.ClassFqn)) continue;
                if (c.Instances is null) continue;

                foreach (var inst in c.Instances)
                {
                    // Owner field lookup → outer key parts.
                    var ownerMap = BuildFieldMap(inst.Fields);
                    var outerParts = new string[outerCount];
                    for (int k = 0; k < outerCount; k++)
                    {
                        outerParts[k] = ownerMap.TryGetValue(query.OuterKeyFields[k], out var of)
                            ? SnapshotEnvelope.FormatFieldValue(of)
                            : "(missing)";
                    }

                    // Locate the reference array field; skip the instance if it
                    // has no captured elements.
                    if (!ownerMap.TryGetValue(query.ArrayField, out var arrField)) continue;
                    var elements = arrField.ArrayElements?.Elements;
                    if (elements is null || elements.Count == 0) continue;

                    foreach (var el in elements)
                    {
                        // Layer B only: elements without inner fields (pre-Layer-B
                        // snapshot, primitive element, or element_class_error)
                        // can't be keyed by an inner field.
                        if (el.Fields is null || el.Fields.Count == 0) continue;

                        var elemMap = BuildFieldMap(el.Fields);

                        int keyLen = outerCount + innerCount;
                        var keyParts = new string[keyLen];
                        for (int k = 0; k < outerCount; k++) keyParts[k] = outerParts[k];
                        for (int k = 0; k < innerCount; k++)
                        {
                            keyParts[outerCount + k] = elemMap.TryGetValue(query.InnerKeyFields[k], out var ik)
                                ? SnapshotEnvelope.FormatFieldValue(ik)
                                : "(missing)";
                        }
                        string keyTupleStr = string.Join(KeyDelim, keyParts);

                        var valParts = new string?[valueCount];
                        for (int v = 0; v < valueCount; v++)
                        {
                            valParts[v] = elemMap.TryGetValue(query.ValueFields[v], out var vf)
                                ? SnapshotEnvelope.FormatFieldValue(vf)
                                : null;
                        }

                        // Element object's own address for Inspector handoff;
                        // fall back to the owning instance when absent.
                        string addr = !string.IsNullOrEmpty(el.Address) ? el.Address! : inst.Address;

                        if (!dict.TryGetValue(keyTupleStr, out var list))
                        {
                            list = new List<(string Addr, string?[] Vals)>(1);
                            dict[keyTupleStr] = list;
                        }
                        list.Add((addr, valParts));
                    }
                }
            }
        }

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 0; s < snapCount; s++)
            foreach (var k in perSnap[s].Keys) allKeys.Add(k);

        string? keyFilter = string.IsNullOrWhiteSpace(query.KeyFilter) ? null : query.KeyFilter;

        var results = new List<PivotRow>(allKeys.Count);
        foreach (var keyTupleStr in allKeys)
        {
            string[] keyTuple = keyTupleStr.Split(KeyDelim);

            if (keyFilter is not null)
            {
                string rendered = string.Join(", ", keyTuple);
                if (rendered.IndexOf(keyFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            bool hasCollision = false;
            var addressPerSnap = new string?[snapCount];
            var values = new string?[valueCount][];
            for (int v = 0; v < valueCount; v++) values[v] = new string?[snapCount];

            for (int s = 0; s < snapCount; s++)
            {
                if (!perSnap[s].TryGetValue(keyTupleStr, out var list))
                {
                    addressPerSnap[s] = null;
                    for (int v = 0; v < valueCount; v++) values[v][s] = null;
                    continue;
                }

                if (list.Count == 1)
                {
                    var (addr, vals) = list[0];
                    addressPerSnap[s] = addr;
                    for (int v = 0; v < valueCount; v++) values[v][s] = vals[v];
                }
                else
                {
                    hasCollision = true;
                    addressPerSnap[s] = list[0].Addr;
                    for (int v = 0; v < valueCount; v++)
                        values[v][s] = PivotEngine.FormatCollisionCell(list, v);
                }
            }

            var valuesReadonly = new IReadOnlyList<string?>[valueCount];
            for (int v = 0; v < valueCount; v++) valuesReadonly[v] = values[v];

            results.Add(new PivotRow
            {
                KeyTuple       = keyTuple,
                Values         = valuesReadonly,
                AddressPerSnap = addressPerSnap,
                HasCollision   = hasCollision,
            });
        }

        return results;
    }

    private static Dictionary<string, SnapshotFieldEntry> BuildFieldMap(
        IReadOnlyList<SnapshotFieldEntry> fields)
    {
        var map = new Dictionary<string, SnapshotFieldEntry>(fields.Count, StringComparer.Ordinal);
        foreach (var f in fields) map.TryAdd(f.Name, f);   // first wins on dup names
        return map;
    }
}
