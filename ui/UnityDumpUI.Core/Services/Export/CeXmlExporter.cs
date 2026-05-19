// CeXmlExporter.cs — emits a Cheat Engine cheat-table address-list XML
// (.CT-importable) describing a Unity managed class as a tree of
// <CheatEntry> / <CheatEntries>.
//
// Output shape (canonical, matches a CE "Save selected" round-trip):
//
//   <CheatTable>
//     <CheatEntries>
//       <CheatEntry>
//         <ID>0</ID>
//         <Description>"...root..."</Description>
//         <ShowAsHex>1</ShowAsHex>
//         <ShowAsSigned>0</ShowAsSigned>
//         <GroupHeader>1</GroupHeader>
//         <Address>{baseAddress}</Address>
//         <CheatEntries>
//           <CheatEntry> ... +0xNN scalar leaf ... </CheatEntry>
//           <CheatEntry GroupHeader=1 ... +0xNN pointer ... <Offsets><Offset>0</Offset></Offsets>
//             <CheatEntries>... drilldown ...</CheatEntries>
//           </CheatEntry>
//         </CheatEntries>
//       </CheatEntry>
//     </CheatEntries>
//   </CheatTable>
//
// Conventions:
//  - Scalar leaf: <Address>+{offset}</Address>, no <Offsets>.
//  - Pointer / array group: <Address>+{offset}</Address> + <Offsets><Offset>0</Offset></Offsets>
//    so CE dereferences the qword at offset before applying child offsets.
//  - Per-element array entries: <Address>+{i*elemSize}</Address>, no Offsets
//    (parent already dereferenced the data buffer).
//  - <Options moHideChildren="1" moDeactivateChildrenAsWell="1"/> on pointer/
//    array group nodes when ExportOptions.CollapsePointerNodes is true — CE
//    opens those branches collapsed, keeping the table clean for the user.
//
// Drilldown shares the IClassInfoLookup contract with CsxExporter.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services.Export;

public static class CeXmlExporter
{
    /// <summary>
    /// Render a CE table around <paramref name="self"/> rooted at
    /// <paramref name="baseAddress"/>. <paramref name="baseAddress"/> is
    /// emitted verbatim into the root entry's <c>&lt;Address&gt;</c>; CE
    /// accepts hex with or without "0x", a symbol, or an expression.
    /// </summary>
    public static async Task<string> RenderAsync(
        string             baseAddress,
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors,
        string?            assemblyName,
        ExportOptions      options,
        IClassInfoLookup?  lookup,
        CancellationToken  ct = default)
    {
        // Live mode: real instance address + ILiveExportSource hookup.
        // Falls through to template mode if either pre-condition is missing.
        if (options.LiveData
            && lookup is ILiveExportSource live
            && TryParseHexAddress(baseAddress, out ulong baseAbs))
        {
            return await RenderLiveAsync(
                baseAbs, self, ancestors, assemblyName, options, live, ct)
                .ConfigureAwait(false);
        }

        var sb = new StringBuilder(4096);
        var idGen = new IdGen();
        var visited = new HashSet<string>();

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        string rootName  = StructLabel(self, assemblyName);
        OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                  offsets: null, collapsed: options.CollapsePointerNodes,
                  varType: "8 Bytes", showAsHex: true);

        // Inherited fields first, then self's declared fields.
        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                await EmitFieldsAsync(sb, "      ", idGen, anc, isInherited: true,
                                      assemblyName, options, lookup, visited,
                                      currentDepth: 0, ct).ConfigureAwait(false);
        }
        await EmitFieldsAsync(sb, "      ", idGen, self, isInherited: false,
                              assemblyName, options, lookup, visited,
                              currentDepth: 0, ct).ConfigureAwait(false);

        CloseGroup(sb, "    ");

        sb.Append("  </CheatEntries>\n");
        sb.Append("</CheatTable>\n");
        return sb.ToString();
    }

    // ---------- Live-data mode -------------------------------------------

    /// <summary>
    /// Live render: every entry uses an absolute address, no <c>+offset</c>
    /// chains, no <c>&lt;Offsets&gt;</c> dereferences. For pointer fields the
    /// exporter <c>readMemory</c>s the qword and recurses into the pointed-to
    /// object; for collection fields it <c>walkArray</c>s and emits one
    /// concrete-rooted child per slot. Caller is responsible for keeping
    /// the game running while the export executes — every node on the wire
    /// becomes one or two pipe RPCs.
    /// </summary>
    private static async Task<string> RenderLiveAsync(
        ulong              baseAbs,
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors,
        string?            assemblyName,
        ExportOptions      options,
        ILiveExportSource  live,
        CancellationToken  ct)
    {
        var sb = new StringBuilder(8192);
        var idGen = new IdGen();
        var visited = new HashSet<string>();

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        string rootName  = $"{StructLabel(self, assemblyName)} @ 0x{baseAbs:X16}";
        OpenGroup(sb, "    ", idGen, rootName, $"0x{baseAbs:X16}",
                  offsets: null, collapsed: options.CollapsePointerNodes,
                  varType: "8 Bytes", showAsHex: true);

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                await EmitFieldsLiveAsync(sb, "      ", idGen, anc, baseAbs,
                                          isInherited: true, assemblyName, options,
                                          live, visited, currentDepth: 0, ct)
                    .ConfigureAwait(false);
        }
        await EmitFieldsLiveAsync(sb, "      ", idGen, self, baseAbs,
                                  isInherited: false, assemblyName, options,
                                  live, visited, currentDepth: 0, ct)
            .ConfigureAwait(false);

        CloseGroup(sb, "    ");
        sb.Append("  </CheatEntries>\n");
        sb.Append("</CheatTable>\n");
        return sb.ToString();
    }

    private static async Task EmitFieldsLiveAsync(
        StringBuilder sb, string indent, IdGen ids,
        GetClassInfoResult info, ulong parentAbs,
        bool isInherited, string? assemblyName, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            await EmitOneFieldLiveAsync(sb, indent, ids, f, parentAbs,
                                        isInherited, assemblyName, options,
                                        live, visited, currentDepth, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task EmitOneFieldLiveAsync(
        StringBuilder sb, string indent, IdGen ids,
        FieldEntry f, ulong parentAbs,
        bool isInherited, string? assemblyName, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
        ulong fieldAbs = parentAbs + (ulong)(uint)f.Offset;
        string fieldAddr = $"0x{fieldAbs:X16}";
        string desc = isInherited
            ? $"{f.Name} : {f.Type ?? "?"}  (inherited)"
            : $"{f.Name} : {f.Type ?? "?"}";

        // Scalar leaf — emit at absolute address, no chain.
        if (!shape.IsPointer)
        {
            (bool hex, bool signed) = UnityTypeMap.CeDisplay(f.Type);
            EmitLeaf(sb, indent, ids, desc,
                     vartype: shape.CeTypeKeyword,
                     showAsHex: hex, showAsSigned: signed,
                     address: fieldAddr, offsets: null);
            return;
        }

        // Pointer field — read the qword to get the deref'd value.
        ReadMemoryResult mem;
        try
        {
            mem = await live.ReadMemoryAsync(fieldAddr, 8, ct).ConfigureAwait(false);
        }
        catch
        {
            // Pipe error — fall back to template-style group with deref offsets.
            OpenGroup(sb, indent, ids, desc + "  (read failed)", fieldAddr,
                      offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                      varType: null, showAsHex: true);
            CloseGroup(sb, indent);
            return;
        }
        if (!string.IsNullOrEmpty(mem.Error) || string.IsNullOrEmpty(mem.Hex))
        {
            EmitLeaf(sb, indent, ids, desc + "  <unreadable>",
                     vartype: "8 Bytes", showAsHex: true,
                     address: fieldAddr, offsets: null);
            return;
        }
        ulong derefAbs = ParseLeQword(mem.Hex!);
        if (derefAbs == 0)
        {
            EmitLeaf(sb, indent, ids, desc + "  <null>",
                     vartype: "8 Bytes", showAsHex: true,
                     address: fieldAddr, offsets: null);
            return;
        }

        bool isCollection = UnityTypeMap.IsCollectionType(f.Type);
        bool canDrill = currentDepth < options.DrilldownDepth;

        // Collection field: walkArray and emit per-slot entries with real
        // absolute addresses. List<T> needs an extra +0x10 dereference to
        // reach the inner T[] header.
        if (isCollection && canDrill && options.ArrayLimit > 0)
        {
            ulong arrayAbs = derefAbs;
            bool isList = f.Type is { Length: > 0 } t
                          && t.StartsWith("System.Collections.Generic.List`1<", StringComparison.Ordinal);
            if (isList)
            {
                try
                {
                    ReadMemoryResult itemsMem = await live.ReadMemoryAsync(
                        $"0x{(derefAbs + 0x10):X16}", 8, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(itemsMem.Error) && !string.IsNullOrEmpty(itemsMem.Hex))
                        arrayAbs = ParseLeQword(itemsMem.Hex!);
                    else
                        arrayAbs = 0;
                }
                catch { arrayAbs = 0; }
            }

            if (arrayAbs == 0)
            {
                EmitLeaf(sb, indent, ids, desc + "  <empty>",
                         vartype: "8 Bytes", showAsHex: true,
                         address: fieldAddr, offsets: null);
                return;
            }

            // Resolve element class hint from the field type.
            // SplitElementClass returns null for primitive (System.*) element
            // types because the DLL primitive table covers them — but we
            // still need the bare element name (e.g. "System.Byte" for
            // System.Byte[]) to feed into PrimitiveVartype below. Compute
            // both: a runtime-class spec (may be null) and a type-string
            // hint that's always the unwrapped element type.
            (string ns, string name)? elemSpec = SplitElementClass(f.Type);
            string elemType = StripCollectionSuffix(f.Type)
                              ?? (elemSpec is { } es
                                  ? (string.IsNullOrEmpty(es.ns) ? es.name : $"{es.ns}.{es.name}")
                                  : (f.Type ?? string.Empty));

            WalkArrayResult? walk = null;
            try
            {
                walk = await live.WalkArrayAsync(
                    $"0x{arrayAbs:X16}",
                    elementType:      elemType,
                    limit:            options.ArrayLimit,
                    elementSize:      null,
                    elementAssembly:  elemSpec is null ? null : (assemblyName ?? string.Empty),
                    elementNamespace: elemSpec?.ns,
                    elementName:      elemSpec?.name,
                    ct).ConfigureAwait(false);
            }
            catch { /* tolerate */ }

            string headerLabel = walk is null
                ? $"{desc}  (walk failed)"
                : $"{desc}  [{walk.Length} of {(walk.ElementClassName ?? elemType)}, showing {walk.Count}]";
            OpenGroup(sb, indent, ids, headerLabel, $"0x{arrayAbs:X16}",
                      offsets: null, collapsed: options.CollapsePointerNodes,
                      varType: null, showAsHex: true);

            if (walk?.Elements is { } elems)
            {
                // For ref-type slots, recurse into each pointed-to object.
                // For value-type slots, lay out the element class fields at
                // slot.Address. Cycle-detect on element class name so an
                // array of self doesn't unroll forever.
                GetClassInfoResult? elemClass = null;
                string? cycleKey = null;
                if (canDrill && elemSpec is { } e0)
                {
                    cycleKey = $"{assemblyName ?? "?"}!{e0.ns}.{e0.name}";
                    if (visited.Add(cycleKey))
                    {
                        try
                        {
                            elemClass = await live.GetClassInfoAsync(
                                assemblyName ?? string.Empty, e0.ns, e0.name, ct)
                                .ConfigureAwait(false);
                        }
                        catch { /* tolerate */ }
                    }
                    else
                    {
                        cycleKey = null;
                    }
                }

                foreach (WalkedArrayElement elem in elems)
                {
                    if (walk.IsPointer)
                    {
                        ulong slotPtr = ParseHexLoose(elem.Ptr ?? "0");
                        if (slotPtr == 0 || elemClass is null)
                        {
                            EmitLeaf(sb, indent + "  ", ids,
                                     $"[{elem.Index}]",
                                     vartype: "8 Bytes", showAsHex: true,
                                     address: elem.Address ?? "?", offsets: null);
                            continue;
                        }
                        OpenGroup(sb, indent + "  ", ids,
                                  $"[{elem.Index}] @ {elem.Ptr}",
                                  $"0x{slotPtr:X16}",
                                  offsets: null,
                                  collapsed: options.CollapsePointerNodes,
                                  varType: null, showAsHex: true);
                        await EmitFieldsLiveAsync(sb, indent + "    ", ids,
                                                  elemClass, slotPtr,
                                                  isInherited: false, assemblyName,
                                                  options, live, visited,
                                                  currentDepth + 1, ct)
                            .ConfigureAwait(false);
                        CloseGroup(sb, indent + "  ");
                    }
                    else
                    {
                        ulong slotAbs = ParseHexLoose(elem.Address ?? "0");
                        if (elemClass is null || slotAbs == 0)
                        {
                            (bool h, bool s) = UnityTypeMap.CeDisplay(elemType);
                            EmitLeaf(sb, indent + "  ", ids,
                                     $"[{elem.Index}]",
                                     vartype: PrimitiveVartype(elemType),
                                     showAsHex: h, showAsSigned: s,
                                     address: elem.Address ?? "?", offsets: null);
                            continue;
                        }
                        OpenGroup(sb, indent + "  ", ids,
                                  $"[{elem.Index}]",
                                  $"0x{slotAbs:X16}",
                                  offsets: null,
                                  collapsed: options.CollapsePointerNodes,
                                  varType: null, showAsHex: true);
                        await EmitFieldsLiveAsync(sb, indent + "    ", ids,
                                                  elemClass, slotAbs,
                                                  isInherited: false, assemblyName,
                                                  options, live, visited,
                                                  currentDepth + 1, ct)
                            .ConfigureAwait(false);
                        CloseGroup(sb, indent + "  ");
                    }
                }

                if (cycleKey is not null) visited.Remove(cycleKey);
            }
            CloseGroup(sb, indent);
            return;
        }

        // Plain reference field — drill into the pointed-to object if the
        // class resolves and we still have depth budget.
        (string ns, string name)? target = canDrill
            ? UnityTypeMap.ExtractElementClass(f.Type)
            : null;

        GetClassInfoResult? child = null;
        string? cycleKey2 = null;
        if (target is not null)
        {
            cycleKey2 = $"{assemblyName ?? "?"}!{target.Value.ns}.{target.Value.name}";
            if (visited.Add(cycleKey2))
            {
                try
                {
                    child = await live.GetClassInfoAsync(
                        assemblyName ?? string.Empty,
                        target.Value.ns, target.Value.name, ct)
                        .ConfigureAwait(false);
                }
                catch { /* tolerate */ }
                if (child is null || !string.IsNullOrEmpty(child.Error))
                {
                    visited.Remove(cycleKey2);
                    child = null;
                }
            }
            else
            {
                cycleKey2 = null;
            }
        }

        OpenGroup(sb, indent, ids, $"{desc}  -> 0x{derefAbs:X16}",
                  $"0x{derefAbs:X16}",
                  offsets: null,
                  collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);
        if (child is not null)
        {
            await EmitFieldsLiveAsync(sb, indent + "  ", ids, child, derefAbs,
                                      isInherited: false, assemblyName,
                                      options, live, visited,
                                      currentDepth + 1, ct).ConfigureAwait(false);
        }
        CloseGroup(sb, indent);
        if (cycleKey2 is not null) visited.Remove(cycleKey2);
    }

    // ---------- Hex / type helpers ---------------------------------------

    /// <summary>Try to parse an address argument as a real hex pointer (0x... or bare hex).</summary>
    private static bool TryParseHexAddress(string? input, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ulong.TryParse(s, NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out value);
    }

    private static ulong ParseHexLoose(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        string t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
        return ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : 0;
    }

    /// <summary>Reorder an 8-byte little-endian hex blob into a host-order qword.</summary>
    private static ulong ParseLeQword(string leHex)
    {
        if (leHex.Length < 16) return 0;
        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < 8; ++i)
        {
            if (!byte.TryParse(leHex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out bytes[i]))
                return 0;
        }
        ulong v = 0;
        for (int i = 7; i >= 0; --i) v = (v << 8) | bytes[i];
        return v;
    }

    /// <summary>
    /// Like <see cref="UnityTypeMap.ExtractElementClass"/> but also returns
    /// primitive-class hints (System.Int32 etc.) so the live walker can emit
    /// per-slot scalar leaves without losing the type label.
    /// </summary>
    /// <summary>
    /// Strip <c>[]</c> / <c>List`1&lt;…&gt;</c> wrapping from a managed type
    /// string and return the bare element type (e.g. <c>System.Byte[]</c> →
    /// <c>System.Byte</c>). Returns null when the input isn't recognisably
    /// a collection so callers can short-circuit.
    /// </summary>
    private static string? StripCollectionSuffix(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        string t = type;
        bool changed = false;
        while (t.EndsWith("[]", StringComparison.Ordinal))
        {
            t = t.Substring(0, t.Length - 2);
            changed = true;
        }
        const string listPrefix = "System.Collections.Generic.List`1<";
        if (t.StartsWith(listPrefix, StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
        {
            t = t.Substring(listPrefix.Length, t.Length - listPrefix.Length - 1).Trim();
            changed = true;
        }
        return changed ? t : null;
    }

    private static (string ns, string name)? SplitElementClass(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        string t = type;
        while (t.EndsWith("[]", StringComparison.Ordinal))
            t = t.Substring(0, t.Length - 2);
        const string listPrefix = "System.Collections.Generic.List`1<";
        if (t.StartsWith(listPrefix, StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
            t = t.Substring(listPrefix.Length, t.Length - listPrefix.Length - 1).Trim();
        if (t.StartsWith("System.", StringComparison.Ordinal)) return null; // primitives — let DLL table handle
        if (t.IndexOf('.') < 0) return (string.Empty, t);
        int dot = t.LastIndexOf('.');
        return (t.Substring(0, dot), t.Substring(dot + 1));
    }

    private static string PrimitiveVartype(string? type)
    {
        ManagedTypeShape s = UnityTypeMap.Resolve(type);
        return s.CeTypeKeyword;
    }

    /// <summary>
    /// Single-field "pointer chain" export with optional drilldown — emits a
    /// CE table containing one root entry rooted at <paramref name="baseAddress"/>
    /// for the given field. Honors <paramref name="options"/> the same way the
    /// full export does, so a depth=1 ArrayLimit=8 export of a class-pointer
    /// field will resolve to the same nested structure shown in the full
    /// table. Useful for "right-click on field → Copy CE field" workflows
    /// where the user only wants one record pasted into CE.
    /// </summary>
    public static Task<string> RenderFieldAsync(
        string             baseAddress,
        FieldEntry         field,
        GetClassInfoResult declaringClass,
        string?            assemblyName,
        ExportOptions      options,
        IClassInfoLookup?  lookup,
        CancellationToken  ct = default)
        => RenderFieldsAsync(baseAddress, new[] { field }, declaringClass,
                             assemblyName, options, lookup, ct);

    /// <summary>
    /// Multi-field variant of <see cref="RenderFieldAsync"/> — emits one
    /// <c>&lt;CheatEntry&gt;</c> per supplied field, all wrapped in a single
    /// declaring-class GroupHeader rooted at <paramref name="baseAddress"/>.
    /// Drives the Fields-tab "Copy CE field" right-click when the user has
    /// multi-selected rows (Ctrl/Shift-click); single-element input matches
    /// the single-field shape byte-for-byte so the legacy path is unaffected.
    /// </summary>
    public static async Task<string> RenderFieldsAsync(
        string                     baseAddress,
        IReadOnlyList<FieldEntry>  fields,
        GetClassInfoResult         declaringClass,
        string?                    assemblyName,
        ExportOptions              options,
        IClassInfoLookup?          lookup,
        CancellationToken          ct = default)
    {
        if (fields is null || fields.Count == 0)
            throw new ArgumentException("at least one field required", nameof(fields));

        var sb = new StringBuilder(1024);
        var idGen = new IdGen();
        var visited = new HashSet<string>();

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        // Single-row preserves the historical "Class.field" label so existing
        // exports and the unit-test snapshot don't churn. Multi-row uses the
        // bare class label because the entries underneath have different
        // names — putting any one of them in the header would mislead.
        string rootName = fields.Count == 1
            ? $"{StructLabel(declaringClass, assemblyName)}.{fields[0].Name}"
            : StructLabel(declaringClass, assemblyName);
        // Wrap in a base-address group so each field's relative +0xNN address
        // resolves correctly without polluting the user's existing CE table.
        OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                  offsets: null, collapsed: false, varType: "8 Bytes", showAsHex: true);
        foreach (FieldEntry field in fields)
        {
            await EmitOneFieldAsync(sb, "      ", idGen, field,
                                    isInherited: false, assemblyName,
                                    options, lookup, visited,
                                    currentDepth: 0, ct).ConfigureAwait(false);
        }
        CloseGroup(sb, "    ");

        sb.Append("  </CheatEntries>\n");
        sb.Append("</CheatTable>\n");
        return sb.ToString();
    }

    // ---------- Field emission -------------------------------------------

    private static async Task EmitFieldsAsync(
        StringBuilder sb, string indent, IdGen ids,
        GetClassInfoResult info, bool isInherited,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            await EmitOneFieldAsync(sb, indent, ids, f, isInherited,
                                    assemblyName, options, lookup, visited,
                                    currentDepth, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per-field emit. Shared between <see cref="EmitFieldsAsync"/> (which
    /// loops a class's fields) and <see cref="RenderFieldAsync"/> (which
    /// emits exactly one). Handles scalar leaves, pointer GroupHeaders with
    /// <c>Offsets=[0]</c> dereference, optional drilldown into the resolved
    /// element class, and per-index expansion for collection-typed fields.
    /// </summary>
    private static async Task EmitOneFieldAsync(
        StringBuilder sb, string indent, IdGen ids,
        FieldEntry f, bool isInherited,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
        string desc = isInherited
            ? $"{f.Name} : {f.Type ?? "?"}  (inherited)"
            : $"{f.Name} : {f.Type ?? "?"}";
        string addr = $"+{f.Offset:X}";

        if (!shape.IsPointer)
        {
            (bool hex, bool signed) = UnityTypeMap.CeDisplay(f.Type);
            EmitLeaf(sb, indent, ids, desc,
                     vartype: shape.CeTypeKeyword,
                     showAsHex: hex, showAsSigned: signed,
                     address: addr, offsets: null);
            return;
        }

        // Pointer field — GroupHeader with Offsets=[0] dereference.
        bool isCollection = UnityTypeMap.IsCollectionType(f.Type);
        bool canDrill = lookup is not null
                        && currentDepth < options.DrilldownDepth
                        && !string.IsNullOrEmpty(f.Type);

        (string ns, string name)? target = canDrill
            ? UnityTypeMap.ExtractElementClass(f.Type)
            : null;

        GetClassInfoResult? child = null;
        string? cycleKey = null;
        if (target is not null)
        {
            cycleKey = $"{assemblyName ?? "?"}!{target.Value.ns}.{target.Value.name}";
            if (visited.Add(cycleKey))
            {
                try
                {
                    child = await lookup!.GetClassInfoAsync(
                        assemblyName ?? string.Empty,
                        target.Value.ns, target.Value.name, ct).ConfigureAwait(false);
                }
                catch { /* tolerate */ }
                if (child is null || !string.IsNullOrEmpty(child.Error))
                {
                    visited.Remove(cycleKey);
                    child = null;
                }
            }
            else
            {
                cycleKey = null;
            }
        }

        OpenGroup(sb, indent, ids, desc, addr,
                  offsets: new[] { 0 },
                  collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        if (child is not null && isCollection && options.ArrayLimit > 0)
        {
            int elemSize = ComputeStructSize(child);
            if (elemSize <= 0) elemSize = 8;
            for (int i = 0; i < options.ArrayLimit; ++i)
            {
                int elemOffset = i * elemSize;
                OpenGroup(sb, indent + "  ", ids,
                          $"[{i}]", $"+{elemOffset:X}",
                          offsets: null,
                          collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                await EmitFieldsAsync(sb, indent + "    ", ids, child,
                                      isInherited: false, assemblyName,
                                      options, lookup, visited,
                                      currentDepth + 1, ct).ConfigureAwait(false);
                CloseGroup(sb, indent + "  ");
            }
        }
        else if (child is not null)
        {
            await EmitFieldsAsync(sb, indent + "  ", ids, child,
                                  isInherited: false, assemblyName,
                                  options, lookup, visited,
                                  currentDepth + 1, ct).ConfigureAwait(false);
        }

        CloseGroup(sb, indent);
        if (cycleKey is not null) visited.Remove(cycleKey);
    }

    // ---------- Emission primitives --------------------------------------

    private static void OpenGroup(
        StringBuilder sb, string indent, IdGen ids,
        string description, string address,
        int[]? offsets, bool collapsed, string? varType, bool showAsHex)
    {
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        // <Options moHideChildren=...> goes on every collapsed group node
        // regardless of addressing mode — CE keys off the GroupHeader, not
        // off the Offsets chain. Previous version restricted this to
        // relative+offset entries and silently dropped it in live mode.
        if (collapsed)
            sb.Append(indent).Append("  <Options moHideChildren=\"1\" moDeactivateChildrenAsWell=\"1\"/>\n");
        if (showAsHex)
            sb.Append(indent).Append("  <ShowAsHex>1</ShowAsHex>\n");
        sb.Append(indent).Append("  <ShowAsSigned>0</ShowAsSigned>\n");
        sb.Append(indent).Append("  <GroupHeader>1</GroupHeader>\n");
        if (varType is not null)
            sb.Append(indent).Append("  <VariableType>").Append(varType).Append("</VariableType>\n");
        sb.Append(indent).Append("  <Address>").Append(XmlEscape(address)).Append("</Address>\n");
        EmitOffsets(sb, indent + "  ", offsets);
        sb.Append(indent).Append("  <CheatEntries>\n");
    }

    private static void CloseGroup(StringBuilder sb, string indent)
    {
        sb.Append(indent).Append("  </CheatEntries>\n");
        sb.Append(indent).Append("</CheatEntry>\n");
    }

    private static void EmitLeaf(
        StringBuilder sb, string indent, IdGen ids,
        string description, string vartype, bool showAsHex,
        string address, int[]? offsets, bool showAsSigned = false)
    {
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        if (showAsHex)
            sb.Append(indent).Append("  <ShowAsHex>1</ShowAsHex>\n");
        sb.Append(indent).Append("  <ShowAsSigned>").Append(showAsSigned ? '1' : '0').Append("</ShowAsSigned>\n");
        sb.Append(indent).Append("  <VariableType>").Append(vartype).Append("</VariableType>\n");
        sb.Append(indent).Append("  <Address>").Append(XmlEscape(address)).Append("</Address>\n");
        EmitOffsets(sb, indent + "  ", offsets);
        sb.Append(indent).Append("</CheatEntry>\n");
    }

    private static void EmitOffsets(StringBuilder sb, string indent, int[]? offsets)
    {
        if (offsets is null || offsets.Length == 0) return;
        sb.Append(indent).Append("<Offsets>\n");
        foreach (int off in offsets)
            sb.Append(indent).Append("  <Offset>").Append(off.ToString("X", CultureInfo.InvariantCulture)).Append("</Offset>\n");
        sb.Append(indent).Append("</Offsets>\n");
    }

    // ---------- Helpers ---------------------------------------------------

    /// <summary>Total bytes covered by the largest non-static field (offset+size).</summary>
    private static int ComputeStructSize(GetClassInfoResult info)
    {
        if (info.Fields is null) return 0;
        int max = 0;
        foreach (FieldEntry f in info.Fields)
        {
            if (f.Static) continue;
            int end = f.Offset + UnityTypeMap.Resolve(f.Type).SizeBytes;
            if (end > max) max = end;
        }
        return max;
    }

    private static string StructLabel(GetClassInfoResult info, string? assemblyName)
    {
        string ns = info.Namespace ?? string.Empty;
        string nm = info.Name      ?? "Unknown";
        string full = string.IsNullOrEmpty(ns) ? nm : $"{ns}.{nm}";
        return string.IsNullOrEmpty(assemblyName) ? full : $"{assemblyName}!{full}";
    }

    private static string XmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '<'  => "&lt;",
                '>'  => "&gt;",
                '&'  => "&amp;",
                '"'  => "&quot;",
                '\'' => "&apos;",
                _    => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private sealed class IdGen
    {
        private int _n;
        public int Next() => _n++;
    }
}
