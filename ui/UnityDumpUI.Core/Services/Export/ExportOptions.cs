// ExportOptions.cs — user-tunable export knobs.
//
// Defaults match the most useful "I want a CE table" cadence: nest one
// pointer level (so the user sees the immediate sub-struct), 16 elements
// per array (enough for diagnosis without flooding CE's tree), and collapse
// pointer / array group headers in CE XML so the tree opens visually clean.

namespace UnityDumpUI.Core.Services.Export;

public sealed record ExportOptions(
    int  DrilldownDepth        = 1,
    int  ArrayLimit            = 16,
    bool CollapsePointerNodes  = true,
    bool LiveData              = false,
    // Game-process pointer size (8 = x64, 4 = x86/WOW64). Only consulted by
    // the live-data export for the managed-String inline char buffer offset
    // (header is 2*ptr, chars follow the int32 length → x64 +0x14, x86 +0xC).
    int  PointerSize           = 8,
    // CE <Description> verbosity. true (default): "name : Type [= value]" —
    // the full self-documenting form. false: bare field name only, for a
    // compact CE table. Only affects the description text, never the
    // addresses / offsets / variable types emitted into the entry.
    bool VerboseDescription    = true,

    // ---- Live-export collapse / flatten knobs (ported from UE5CEDumper) ----
    // All default OFF so existing exports / unit-test snapshots are unchanged.
    // They only take effect in the live walkInstance-driven exporter
    // (CeXmlExporter.Live), which is where the field tree actually exists;
    // the template exporter never expands value structs, so there is nothing
    // to flatten there. DescShowOffset is the one exception — it only decorates
    // the <Description> text, so it applies to both modes.

    // Append " (+0xNN)" (the field's offset within its parent) to every CE
    // entry's <Description>. Pairs with VerboseDescription independently.
    bool DescShowOffset        = false,

    // Flatten an inline value-type struct whose members are ALL primitive
    // scalars (Vector3 → "pos ▸ x" / "▸ y" / "▸ z" sibling leaves at the
    // combined offset) instead of a nested group. A struct member that is
    // itself a struct / pointer / collection disqualifies the parent (it stays
    // nested, and the inner struct flattens at its own level).
    bool FlattenValueStructs   = false,

    // Superset of FlattenValueStructs: also flatten structs whose members
    // include managed String fields (a save-data "record" struct). String
    // members emit the canonical 2-level deref leaf at the combined offset.
    bool FlattenLeafRecords    = false,

    // When a pointer-to-class dereferences to an object with exactly ONE
    // primitive scalar field, collapse it into a single CE record
    // (<Address>+ptrOff</Address> <Offsets><Offset>fieldOff</Offset></Offsets>)
    // instead of a group wrapping one child.
    bool CollapseLeafPointers  = false,

    // Fold a spine of single-child pointer dereferences (A → B → C, each object
    // having exactly one pointer field) into ONE CE multi-level-pointer entry
    // with a combined <Offsets> chain, instead of nested groups.
    bool CollapseChain         = false,

    // Tint alternating container-element rows ([0],[2],… vs [1],[3],…) with a
    // CE <Color>. Even/odd colours are RRGGBB hex strings (or null = CE theme);
    // converted to CE's COLORREF (BBGGRR) byte order at emit time.
    bool RecordColors          = false,
    string? RecordColorEven    = "0080FF",
    string? RecordColorOdd     = null,

    // ---- Feature 1: minimum CE String buffer length (opt-in) --------------
    // When EnforceMinStringLength is on, every emitted CE String <Length> is
    // floored to MinStringLength CHARACTERS: a live string shorter than the
    // floor still reserves the floor's worth of UTF-16 buffer (so a different
    // save's longer string in the same field renders fully), while a string at
    // or above the floor is left exactly as decoded. Off by default so existing
    // string <Length> output is byte-identical. Threaded through the single
    // EmitStringDerefGroup choke point, so it covers instance-field, flattened
    // struct member, and array-element string surfaces alike.
    bool EnforceMinStringLength = false,
    int  MinStringLength        = 32,

    // ---- Feature 2: fill empty array slots (opt-in) -----------------------
    // Assume every array slot holds data even when the current slot is a null
    // pointer, or the whole collection is empty / unallocated (the game hasn't
    // loaded that record yet — different save files populate different slots).
    // Null pointer-element slots and empty/unallocated collections are filled
    // from the element class's metadata layout (GetClassInfoAsync — no live
    // object needed) so the CE table carries the full per-element structure,
    // ready to resolve once any instance is written into the slot. String slots
    // reserve the MAX length observed across the array (floored by
    // MinStringLength when EnforceMinStringLength is on). POPULATED slots keep
    // their own live walk — only null / empty slots are templated. Never
    // overrides the framework/engine reference skip (List<Transform> etc. stay
    // watchable leaves). Off by default → the collection emit path is unchanged.
    bool FillEmptyArraySlots    = false)
{
    public static ExportOptions Defaults { get; } = new();

    /// <summary>Effective minimum CE String character length to floor emitted
    /// <c>&lt;Length&gt;</c> to — <see cref="MinStringLength"/> when
    /// <see cref="EnforceMinStringLength"/> is on (clamped non-negative), else 0
    /// (no floor).</summary>
    public int MinStringChars
        => EnforceMinStringLength ? System.Math.Max(0, MinStringLength) : 0;
}
