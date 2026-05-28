// PivotEngine — Phase 29e-1.
//
// Class-centric Key/Value pivot. The complement to SpcQueryEngine:
//
//   SPC asks   "for every field-row keyed by {asm|fqn|addr|offset|name},
//               does its value sequence across N snaps match these
//               predicates?"
//
//   Pivot asks "for class C, group every instance by its key field(s)
//               value, and project the value field(s) across N snaps."
//
// Why this matters: SPC keys instance rows by InstanceAddress, which is
// the live managed pointer. Addresses drift cross-session (game relaunch
// re-allocates), and they drift intra-session too — instance walk order
// is unstable when total instance count changes between snaps. So SPC's
// intersection of FieldIndex.Keys collapses for instance-keyed rows on
// any cross-session set, and even within one session a runaway-by-one
// shift in walk order makes "instance #124" mean different objects in
// snap 1 vs snap 2.
//
// Pivot sidesteps both by keying on field VALUES inside each instance.
// The user's killer case: GameStat instances of 活俠傳. Across 5 snaps
// instance #124 might land at addresses 0x1f3a000, 0x1f60a00, 0x1f80a00,
// 0x1fa1000, 0x1fc3000 — but the GameStat row whose _propertyID == 101
// is *the same logical Money stat* every time. Pivot keyed by
// (_itemGroup, _propertyID) → trivially tracks Money's _value
// trajectory across all five snaps, no matter where the instance moves.
//
// Collisions: if two instances happen to share the same key tuple
// within one snap, HasCollision flips and that snap's value cell is
// rendered as "<N: v1, v2, …>" so the caller can see the ambiguity
// rather than picking one silently. AddressPerSnap holds the first
// instance's address (spec §5).
//
// Absent: if a key tuple exists in some snaps but not others, the
// missing snap gets a null cell for both Values and AddressPerSnap.
//
// Performance: O(Σ_snaps Σ_instances_of_C × |Fields|) to build per-snap
// maps + O(|keyUnion|) to outer-join. Field lookups are dict-of-name
// per instance. Field counts are small (~tens), so per-instance work is
// dominated by the format call, not the dict. For 活俠傳's GameStat
// (~100 instances) × 5 snaps × 6 fields: well under 50 ms.
//
// Out of scope for v1:
//   * Static fields. Statics aren't instances; SPC tab already covers
//     the "static field value across N snaps" question. Adding them
//     here would just be a degenerate single-row pivot per class.
//   * Reference array inner-pivot ("inventory[*] keyed by inner
//     element._propertyID"). Needs DLL-side child-field capture per
//     array element — deferred as Layer B (own Phase).
//   * Anomaly auto-surface — separate stretch goal.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

/// <summary>Inputs to <see cref="PivotEngine.Run"/>. Snapshots MUST
/// already have their <c>Classes</c> populated (ClassesLoaded == true)
/// — caller is responsible for hydration via
/// <c>SnapshotViewModel.EnsureLoadedAsync</c> before running the
/// query.</summary>
public sealed class PivotQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }

    /// <summary>Declaring assembly of the class being pivoted (e.g.
    /// <c>"Assembly-CSharp"</c>). Ordinal match.</summary>
    public required string Assembly { get; init; }

    /// <summary>Fully-qualified class name (e.g. <c>"Mortal.Core.GameStat"</c>).
    /// Ordinal match. Caller is responsible for picking the FQN — the
    /// Stage 0 class-shortlist UI (29e-3) will surface candidates.</summary>
    public required string ClassFqn { get; init; }

    /// <summary>Names of the field(s) whose values jointly identify a
    /// logical entity across snapshots. Order matters only for KeyTuple
    /// display — semantics are the same regardless. At least one
    /// required.</summary>
    public required IReadOnlyList<string> KeyFields { get; init; }

    /// <summary>Names of the field(s) whose values are projected across
    /// snapshots (the "what changes" side of the pivot). At least one
    /// required.</summary>
    public required IReadOnlyList<string> ValueFields { get; init; }

    /// <summary>Optional case-insensitive substring filter applied to
    /// the rendered key tuple (parts joined with "', '" for matching).
    /// Null / whitespace = no filter.</summary>
    public string? KeyFilter { get; init; }
}

/// <summary>One pivot result row — a single key tuple and its
/// per-snapshot value trajectory.</summary>
public sealed class PivotRow
{
    /// <summary>The key tuple as a list of formatted strings, one per
    /// entry in <see cref="PivotQuery.KeyFields"/> (same order).</summary>
    public required IReadOnlyList<string> KeyTuple { get; init; }

    /// <summary>Outer dimension = value field index (aligns with
    /// <see cref="PivotQuery.ValueFields"/>); inner dimension = snapshot
    /// index. <c>null</c> cells mean no instance with this key tuple
    /// was present in that snapshot. Collision cells are rendered as
    /// <c>"&lt;N: v1, v2, ...&gt;"</c>.</summary>
    public required IReadOnlyList<IReadOnlyList<string?>> Values { get; init; }

    /// <summary>Per-snapshot instance address for Inspector / CE handoff.
    /// <c>null</c> if absent in that snap. On collision the FIRST
    /// instance's address (spec §5) — consumers should consult
    /// <see cref="HasCollision"/> before trusting a "single instance"
    /// interpretation.</summary>
    public required IReadOnlyList<string?> AddressPerSnap { get; init; }

    /// <summary>True if at least one snapshot had >1 instance sharing
    /// this key tuple. UI surfaces this as a warning chip.</summary>
    public required bool HasCollision { get; init; }
}

public static class PivotEngine
{
    // Unit Separator — invisible delimiter used to join key parts into
    // a single hashable dictionary key. Real field values cannot
    // contain this byte (FormatFieldValue produces printable text only),
    // so it's a safe separator.
    private const char KeyDelim = '\x1F';

    /// <summary>Run the pivot. Returns one row per distinct key tuple
    /// observed across the union of snapshots, in dictionary-iteration
    /// order (no stable sort — callers display this in a sortable
    /// DataGrid).</summary>
    public static List<PivotRow> Run(PivotQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Snapshots.Count == 0)
            throw new ArgumentException("Pivot needs at least one snapshot.", nameof(query));
        if (query.KeyFields.Count == 0)
            throw new ArgumentException("Pivot needs at least one key field.", nameof(query));
        if (query.ValueFields.Count == 0)
            throw new ArgumentException("Pivot needs at least one value field.", nameof(query));
        if (string.IsNullOrEmpty(query.Assembly))
            throw new ArgumentException("Assembly name is required.", nameof(query));
        if (string.IsNullOrEmpty(query.ClassFqn))
            throw new ArgumentException("ClassFqn is required.", nameof(query));

        int snapCount  = query.Snapshots.Count;
        int keyCount   = query.KeyFields.Count;
        int valueCount = query.ValueFields.Count;

        // Per-snap: keyTupleStr → list of (instanceAddress, valuesArray[valueCount]).
        // List<...> handles collisions naturally; HasCollision == any list.Count > 1.
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
                if (!string.Equals(fqn, query.ClassFqn, StringComparison.Ordinal)) continue;
                if (c.Instances is null) continue;

                foreach (var inst in c.Instances)
                {
                    // Build name→field lookup once per instance. Linear scan would
                    // be ~equivalent for typical 10-30 field counts but the dict
                    // avoids repeating the scan for each KeyField + ValueField.
                    var fieldMap = new Dictionary<string, SnapshotFieldEntry>(
                        inst.Fields.Count, StringComparer.Ordinal);
                    foreach (var f in inst.Fields)
                    {
                        // First wins on duplicate names — same convention
                        // BuildFieldIndex uses for offset+name keys.
                        fieldMap.TryAdd(f.Name, f);
                    }

                    // Render key tuple parts. Missing field → sentinel; this
                    // lets the user see "(missing)" rows rather than silently
                    // dropping instances that lack the key field.
                    var keyParts = new string[keyCount];
                    for (int k = 0; k < keyCount; k++)
                    {
                        keyParts[k] = fieldMap.TryGetValue(query.KeyFields[k], out var kf)
                            ? SnapshotEnvelope.FormatFieldValue(kf)
                            : "(missing)";
                    }
                    string keyTupleStr = string.Join(KeyDelim, keyParts);

                    // Render value parts. Missing here → null so absent
                    // semantics match (and the UI grid can distinguish from
                    // "present but null").
                    var valParts = new string?[valueCount];
                    for (int v = 0; v < valueCount; v++)
                    {
                        valParts[v] = fieldMap.TryGetValue(query.ValueFields[v], out var vf)
                            ? SnapshotEnvelope.FormatFieldValue(vf)
                            : null;
                    }

                    if (!dict.TryGetValue(keyTupleStr, out var list))
                    {
                        list = new List<(string Addr, string?[] Vals)>(1);
                        dict[keyTupleStr] = list;
                    }
                    list.Add((inst.Address, valParts));
                }
            }
        }

        // Union of key tuples across all snaps.
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 0; s < snapCount; s++)
        {
            foreach (var k in perSnap[s].Keys) allKeys.Add(k);
        }

        string? keyFilter = string.IsNullOrWhiteSpace(query.KeyFilter) ? null : query.KeyFilter;

        var results = new List<PivotRow>(allKeys.Count);
        foreach (var keyTupleStr in allKeys)
        {
            string[] keyTuple = keyTupleStr.Split(KeyDelim);

            // Apply substring filter on the human-readable join.
            if (keyFilter is not null)
            {
                string rendered = string.Join(", ", keyTuple);
                if (rendered.IndexOf(keyFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            bool hasCollision = false;
            var addressPerSnap = new string?[snapCount];
            // values[valueFieldIdx][snapIdx]
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
                    addressPerSnap[s] = list[0].Addr;   // spec §5: first address
                    for (int v = 0; v < valueCount; v++)
                        values[v][s] = FormatCollisionCell(list, v);
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

    /// <summary>Max distinct values shown inside a collision cell before
    /// the "+N more" suffix kicks in. A 21-way collision dumped in full
    /// is unreadable and overflows the grid cell; the distinct set with
    /// a count tells the user what they actually need — "how many
    /// instances collided, and do they even disagree".</summary>
    private const int kCollisionDistinctCap = 6;

    /// <summary>Render a collided snapshot slot compactly: the instance
    /// count plus the DISTINCT set of values (first-seen order, capped).
    /// <c>⟨21: 100⟩</c> means 21 instances all hold 100 (the key just
    /// isn't unique — but the value agrees); <c>⟨21: 100, 50, 250,+3⟩</c>
    /// means 21 instances with 6+ distinct values (the user needs a
    /// more discriminating key). The leading <c>⟨…⟩</c> brackets make
    /// collision cells visually distinct from clean single-instance
    /// rows at a glance.</summary>
    internal static string FormatCollisionCell(
        List<(string Addr, string?[] Vals)> list, int valueIdx)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in list)
        {
            string val = t.Vals[valueIdx] ?? "(null)";
            if (seen.Add(val)) distinct.Add(val);
        }
        IEnumerable<string> shown = distinct.Count > kCollisionDistinctCap
            ? distinct.Take(kCollisionDistinctCap)
            : distinct;
        string suffix = distinct.Count > kCollisionDistinctCap
            ? $",+{distinct.Count - kCollisionDistinctCap}"
            : string.Empty;
        return $"⟨{list.Count}: {string.Join(", ", shown)}{suffix}⟩";
    }
}
