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
    bool VerboseDescription    = true)
{
    public static ExportOptions Defaults { get; } = new();
}
