using System.Collections.Generic;

namespace UnityDumpUI.Core.Services;

/// <summary>Phase 29g-1 — per-snapshot string interning.
/// <para/>
/// A freshly deserialized snapshot holds millions of <see cref="SnapshotFieldEntry"/>
/// objects, each with its own distinct <c>string</c> instances for the field
/// Name / Type / declaring-type (From) and the owning class's Assembly /
/// Namespace / Name — even though the distinct <em>values</em> number only a
/// few thousand (<c>"System.Int32"</c>, <c>"MainGame/PLY_WORK"</c>, <c>"_hp"</c>…
/// repeated across every instance). Phase 29g-0b measured ~5.2 GB of
/// <see cref="string"/> across ~55M objects pre-29g-2; the bulk that survives
/// the FieldIndex removal is these duplicated metadata strings in the Classes
/// tree.
/// <para/>
/// <see cref="Intern"/> walks the tree once after deserialize and collapses each
/// distinct metadata value to a single shared instance using a <em>local</em>
/// <c>Dictionary&lt;string,string&gt;</c> pool — NOT <see cref="string.Intern"/>,
/// which pins strings for the process lifetime and never frees. The pool is
/// discarded when the walk returns; only the deduplicated references survive.
/// <para/>
/// Inherently-unique strings — instance <see cref="SnapshotInstanceEntry.Address"/>,
/// per-field <see cref="SnapshotFieldEntry.Ptr"/> / <see cref="SnapshotFieldEntry.Address"/>,
/// captured <see cref="SnapshotFieldEntry.StringValue"/> / element string values —
/// are deliberately left alone: they barely dedup, so pooling them would only
/// bloat the pool. Interned strings serialize identically, so the DB save
/// round-trip ([SnapshotStore.SaveSnapshotAsync]) is unaffected.
/// </summary>
public static class SnapshotInterner
{
    /// <summary>Dedup the metadata strings across an entire snapshot's class
    /// list in place. Idempotent and cheap to re-run (a second pass just finds
    /// every string already pooled).</summary>
    public static void Intern(IReadOnlyList<SnapshotClassEntry> classes)
    {
        if (classes is null || classes.Count == 0) return;
        var pool = new Dictionary<string, string>(System.StringComparer.Ordinal);

        foreach (var c in classes)
        {
            c.Assembly  = P(pool, c.Assembly);
            c.Namespace = P(pool, c.Namespace);
            c.Name      = P(pool, c.Name);
            if (c.Parent is { } parent)
            {
                parent.Namespace = P(pool, parent.Namespace);
                parent.Name      = P(pool, parent.Name);
            }
            if (c.Statics is { } statics)
                foreach (var f in statics) InternField(pool, f);
            if (c.Instances is { } instances)
                foreach (var inst in instances)
                    foreach (var f in inst.Fields) InternField(pool, f);
        }
    }

    private static void InternField(Dictionary<string, string> pool, SnapshotFieldEntry f)
    {
        f.Name = P(pool, f.Name);
        f.Type = P(pool, f.Type);
        if (f.From is { } from) f.From = P(pool, from);
        if (f.ArrayElements is { } arr)
        {
            if (arr.ElementType is { } et) arr.ElementType = P(pool, et);
            // Layer B (Phase 29f): reference-array element inner fields nest one
            // level — dedup their Name/Type/From against the same pool.
            foreach (var el in arr.Elements)
                if (el.Fields is { } elFields)
                    foreach (var ef in elFields) InternField(pool, ef);
        }
    }

    /// <summary>Pool lookup: return the shared instance for <paramref name="s"/>,
    /// adding it on first sight. Null / empty pass through (<c>string.Empty</c>
    /// is already a shared singleton).</summary>
    private static string P(Dictionary<string, string> pool, string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (pool.TryGetValue(s, out var existing)) return existing;
        pool[s] = s;
        return s;
    }
}
