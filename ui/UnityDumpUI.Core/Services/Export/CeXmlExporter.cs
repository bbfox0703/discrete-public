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
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Core.Services.Export;

public static partial class CeXmlExporter
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
        CancellationToken  ct = default,
        IProgress<int>?    progress = null)
    {
        ct.ThrowIfCancellationRequested();

        // Live mode: real instance address + ILiveExportSource hookup.
        // Falls through to template mode if either pre-condition is missing.
        if (options.LiveData
            && lookup is ILiveExportSource live
            && TryParseHexAddress(baseAddress, out ulong baseAbs))
        {
            return await RenderLiveAsync(
                baseAbs, self, ancestors, assemblyName, options, live, ct, progress)
                .ConfigureAwait(false);
        }

        var sb = new StringBuilder(4096);
        var idGen = new IdGen(progress);
        var visited = new HashSet<string>();
        var nameMap = new ExportNameMap();

        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        int ptr = TemplateExportHelpers.NormalizePointerSize(options.PointerSize);
        string layoutTag = ptr == 4 ? "x86 layout" : "x64 layout";
        string rootName  = $"{StructLabel(self, assemblyName, nameMap)} ({layoutTag})";
        OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                  offsets: null, collapsed: options.CollapsePointerNodes,
                  varType: PointerVartype(options), showAsHex: true);

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
        return BuildOutput(sb, nameMap);
    }

    // Prepend the XML prolog and (if non-empty) the demangle name-map
    // comment. CE skips XML comments on load, so the map is editor-only.
    private static string BuildOutput(StringBuilder body, ExportNameMap nameMap)
    {
        string comment = nameMap.ToXmlComment();
        if (comment.Length == 0)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + body.ToString();
        }
        var prelude = new StringBuilder(comment.Length + 64);
        prelude.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        prelude.Append(comment);
        prelude.Append(body);
        return prelude.ToString();
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
        CancellationToken  ct,
        IProgress<int>?    progress = null)
    {
        var sb = new StringBuilder(8192);
        var idGen = new IdGen(progress);
        var visited = new HashSet<string>();
        var nameMap = new ExportNameMap();

        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        // walkInstance walks the FULL inheritance chain and returns
        // pre-classified, pre-decoded fields (enum / string / value-type flags
        // + real addresses), so `ancestors` is unused in live mode — the named
        // root walk already includes inherited members. This is the same path
        // the Inspector grid uses, so the CE table matches what the user sees.
        // Done BEFORE OpenGroup so the DLL-reported pointer size can correct
        // `options` for the whole emit (see below).
        WalkInstanceResult root;
        try
        {
            ct.ThrowIfCancellationRequested();
            root = await live.WalkInstanceAsync(
                $"0x{baseAbs:X16}", assemblyName ?? string.Empty,
                self.Namespace, self.Name ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            root = new WalkInstanceResult();
        }

        // Prefer the DLL's authoritative pointer size over the caller-supplied
        // one (the UI's handshake-cached GamePointerSize can be 0/stale at
        // export time → x64 String offsets + a broken List._items deref on a
        // 32-bit target). 4 or 8 only; ignore 0 / garbage.
        if (root.PointerSize == 4 || root.PointerSize == 8)
            options = options with { PointerSize = root.PointerSize };

        string rootName  = $"{StructLabel(self, assemblyName, nameMap)} @ 0x{baseAbs:X16}";
        OpenGroup(sb, "    ", idGen, rootName, $"0x{baseAbs:X16}",
                  offsets: null, collapsed: options.CollapsePointerNodes,
                  varType: PointerVartype(options), showAsHex: true);

        if (root.Fields is { Count: > 0 } rootFields)
        {
            foreach (WalkedField wf in rootFields)
                await EmitWalkedFieldLiveAsync(sb, "      ", idGen, wf, assemblyName,
                                               options, live, visited,
                                               currentDepth: 0, ct).ConfigureAwait(false);
        }

        CloseGroup(sb, "    ");
        sb.Append("  </CheatEntries>\n");
        sb.Append("</CheatTable>\n");
        return BuildOutput(sb, nameMap);
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

    /// <summary>Reorder a little-endian hex blob (<paramref name="byteCount"/> bytes:
    /// 4 on x86 or 8 on x64) into a host-order pointer value. Returns 0 on truncated
    /// or invalid hex input.</summary>
    private static ulong ParseLePointer(string leHex, int byteCount)
    {
        if (byteCount is < 1 or > 8) return 0;
        int hexLen = byteCount * 2;
        if (leHex.Length < hexLen) return 0;
        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < byteCount; ++i)
        {
            if (!byte.TryParse(leHex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out bytes[i]))
                return 0;
        }
        ulong v = 0;
        for (int i = byteCount - 1; i >= 0; --i) v = (v << 8) | bytes[i];
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
    // List<T> reaches the exporter in two wire forms: the IL-form
    // "System.Collections.Generic.List`1<T>" (offline metadata) and the
    // demangled "System.Collections.Generic.List<T>" (live runtime
    // resolver). Strip whichever is present and return the inner element
    // type so live List<T> fields drill per-slot instead of being mistaken
    // for a pointer-to-element.
    private static bool TryStripListGeneric(string t, out string inner)
    {
        inner = string.Empty;
        if (!t.EndsWith(">", StringComparison.Ordinal)) return false;
        int lt = t.IndexOf('<');
        if (lt <= 0) return false;
        string outer = t.Substring(0, lt);
        if (outer != "System.Collections.Generic.List`1" &&
            outer != "System.Collections.Generic.List")
            return false;
        inner = t.Substring(lt + 1, t.Length - lt - 2).Trim();
        return true;
    }

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
        if (TryStripListGeneric(t, out string inner))
        {
            t = inner;
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
        if (TryStripListGeneric(t, out string inner))
            t = inner;
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

    /// <summary>CE VariableType for a raw pointer-sized slot (IntPtr / object
    /// reference / List/array handle): "4 Bytes" on x86, "8 Bytes" on x64.
    /// A managed reference is sizeof(void*), so a 32-bit target MUST emit
    /// "4 Bytes" or CE reads 8 and the next field overlaps.</summary>
    private static string PointerVartype(ExportOptions options)
        => options.PointerSize == 4 ? "4 Bytes" : "8 Bytes";

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
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder(1024);
        var idGen = new IdGen();
        var visited = new HashSet<string>();
        var nameMap = new ExportNameMap();

        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        // Single-row preserves the historical "Class.field" label so existing
        // exports and the unit-test snapshot don't churn. Multi-row uses the
        // bare class label because the entries underneath have different
        // names — putting any one of them in the header would mislead.
        string rootName = fields.Count == 1
            ? $"{StructLabel(declaringClass, assemblyName, nameMap)}.{fields[0].Name}"
            : StructLabel(declaringClass, assemblyName, nameMap);
        // Wrap in a base-address group so each field's relative +0xNN address
        // resolves correctly without polluting the user's existing CE table.
        OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                  offsets: null, collapsed: false, varType: PointerVartype(options), showAsHex: true);
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
        return BuildOutput(sb, nameMap);
    }

    // ---------- Field emission -------------------------------------------

    private static async Task EmitFieldsAsync(
        StringBuilder sb, string indent, IdGen ids,
        GetClassInfoResult info, bool isInherited,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, CancellationToken ct, int offsetAdjust = 0)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            ct.ThrowIfCancellationRequested();
            await EmitOneFieldAsync(sb, indent, ids, f, isInherited,
                                    assemblyName, options, lookup, visited,
                                    currentDepth, ct, offsetAdjust).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per-field template emit. Shared between <see cref="EmitFieldsAsync"/>
    /// and <see cref="RenderFieldsAsync"/>. Resolves unknown game types via
    /// <paramref name="lookup"/> to distinguish <c>System.Enum</c> (inline
    /// scalar leaf), <c>System.ValueType</c> (inline non-deref group), and
    /// reference classes (<c>Offsets=[0]</c> deref group). Contiguous
    /// collections (<c>T[]</c> and <c>List&lt;T&gt;</c>) emit proper
    /// SZARRAY (<c>4*ptr</c>) and <c>_items</c> (<c>2*ptr</c>) pointer chains.
    /// </summary>
    private static async Task EmitOneFieldAsync(
        StringBuilder sb, string indent, IdGen ids,
        FieldEntry f, bool isInherited,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, CancellationToken ct, int offsetAdjust = 0)
    {
        int ptr = TemplateExportHelpers.NormalizePointerSize(options.PointerSize);
        int fieldOffset = Math.Max(0, f.Offset - offsetAdjust);
        ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
        string desc = FieldDesc(f.Name, f.Type, options, options.DescShowOffset ? fieldOffset : null);
        if (isInherited && options.VerboseDescription) desc += "  (inherited)";
        string addr = RelAddr(fieldOffset);

        if (!shape.IsPointer)
        {
            (bool hex, bool signed) = UnityTypeMap.CeDisplay(f.Type);
            string vartype = TemplateExportHelpers.IsNativeIntType(f.Type)
                ? PointerVartype(options)
                : shape.CeTypeKeyword;
            EmitLeaf(sb, indent, ids, desc,
                     vartype: vartype,
                     showAsHex: hex, showAsSigned: signed,
                     address: addr, offsets: null);
            return;
        }

        // Contiguous collection: T[] or List<T> (known SZARRAY / _items layout).
        if (!string.IsNullOrEmpty(f.Type)
            && InstanceFieldClassifier.TryParseCollectionType(f.Type, out string elementType, out bool isList))
        {
            await EmitTemplateCollectionAsync(
                sb, indent, ids, desc, addr, elementType, isList, ptr,
                assemblyName, options, lookup, visited, currentDepth, ct).ConfigureAwait(false);
            return;
        }

        // Non-array collections (HashSet, IList, IEnumerable, Dictionary, ...)
        // do NOT have a flat SZARRAY layout — emit a watchable pointer leaf
        // instead of fabricating array slots or treating the container as T.
        if (TemplateExportHelpers.IsNonArrayCollectionType(f.Type))
        {
            EmitLeaf(sb, indent, ids, desc,
                     vartype: PointerVartype(options),
                     showAsHex: true, showAsSigned: false,
                     address: addr, offsets: null);
            return;
        }

        // Managed System.String: expand into length/chars deref group when
        // depth allows; at depth 0 keep a single-entry group for compact
        // single/multi-field copy parity.
        if (IsManagedString(f.Type))
        {
            if (currentDepth < options.DrilldownDepth)
            {
                EmitStringDerefGroup(sb, indent, ids, desc, addr, charLength: null, ptr,
                                     minChars: options.MinStringChars);
            }
            else
            {
                OpenGroup(sb, indent, ids, desc, addr,
                          offsets: new[] { 0 },
                          collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                CloseGroup(sb, indent);
            }
            return;
        }

        // Ambiguous game type: resolve via lookup even at depth cap so enums
        // and custom ValueType structs are never mis-emitted as deref pointers.
        GetClassInfoResult? child = null;
        string? cycleKey = null;
        if (lookup is not null
            && !string.IsNullOrEmpty(f.Type)
            && !TemplateExportHelpers.IsFrameworkOrEngineNamespace(f.Type)
            && InstanceFieldClassifier.TryParseClassName(f.Type, out string cns, out string cname))
        {
            string key = $"{assemblyName ?? "?"}!{cns}.{cname}";
            if (!visited.Contains(key))
            {
                child = await TemplateExportHelpers.TryLookupClassAsync(
                    lookup, assemblyName, cns, cname, ct).ConfigureAwait(false);
                if (child is not null) cycleKey = key;
            }
        }

        if (child is not null && TemplateExportHelpers.IsEnumClass(child))
        {
            string underlying = TemplateExportHelpers.ResolveEnumUnderlyingType(child);
            ManagedTypeShape es = UnityTypeMap.Resolve(underlying);
            (bool hex, bool signed) = UnityTypeMap.CeDisplay(underlying);
            EmitLeaf(sb, indent, ids, desc,
                     vartype: es.CeTypeKeyword,
                     showAsHex: hex, showAsSigned: signed,
                     address: addr, offsets: null);
            return;
        }

        if (child is not null && TemplateExportHelpers.IsValueTypeClass(child))
        {
            if (currentDepth < options.DrilldownDepth && cycleKey is not null && visited.Add(cycleKey))
            {
                try
                {
                    int headerAdj = TemplateExportHelpers.ComputeValueTypeHeaderAdjust(child, ptr);
                    OpenGroup(sb, indent, ids, desc, addr,
                              offsets: null,
                              collapsed: options.CollapsePointerNodes,
                              varType: null, showAsHex: true);
                    await EmitFieldsAsync(sb, indent + "  ", ids, child,
                                          isInherited: false, assemblyName,
                                          options, lookup, visited,
                                          currentDepth + 1, ct, offsetAdjust: headerAdj).ConfigureAwait(false);
                    CloseGroup(sb, indent);
                }
                finally { visited.Remove(cycleKey); }
            }
            else
            {
                // Depth-capped inline struct: emit an inline leaf (NO Offsets=[0]).
                EmitLeaf(sb, indent, ids, desc,
                         vartype: PointerVartype(options),
                         showAsHex: true, showAsSigned: false,
                         address: addr, offsets: null);
            }
            return;
        }

        // Reference class (resolved or unresolved) — pointer deref group.
        OpenGroup(sb, indent, ids, desc, addr,
                  offsets: new[] { 0 },
                  collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        if (child is not null
            && currentDepth < options.DrilldownDepth
            && cycleKey is not null
            && visited.Add(cycleKey))
        {
            try
            {
                await EmitFieldsAsync(sb, indent + "  ", ids, child,
                                      isInherited: false, assemblyName,
                                      options, lookup, visited,
                                      currentDepth + 1, ct, offsetAdjust: 0).ConfigureAwait(false);
            }
            finally { visited.Remove(cycleKey); }
        }

        CloseGroup(sb, indent);
    }

    private static async Task EmitTemplateCollectionAsync(
        StringBuilder sb, string indent, IdGen ids,
        string desc, string addr, string elementType, bool isList, int ptr,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        int itemsOff = 2 * ptr; // List<T>._items: +0x10 (x64) / +0x08 (x86)
        int dataOff  = 4 * ptr; // SZARRAY data:   +0x20 (x64) / +0x10 (x86)

        OpenGroup(sb, indent, ids, desc, addr,
                  offsets: new[] { 0 },
                  collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        if (options.ArrayLimit > 0 && currentDepth < options.DrilldownDepth)
        {
            string elemIndent = indent + "  ";
            if (isList)
            {
                OpenGroup(sb, elemIndent, ids,
                          FieldDesc("_items", elementType + "[]", options),
                          RelAddr(itemsOff),
                          offsets: new[] { 0 },
                          collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                elemIndent += "  ";
            }

            ManagedTypeShape elemShape = UnityTypeMap.Resolve(elementType);
            if (!elemShape.IsPointer)
            {
                int stride = TemplateExportHelpers.IsNativeIntType(elementType) ? ptr : elemShape.SizeBytes;
                string vt  = TemplateExportHelpers.IsNativeIntType(elementType)
                    ? PointerVartype(options)
                    : elemShape.CeTypeKeyword;
                (bool hex, bool signed) = UnityTypeMap.CeDisplay(elementType);
                for (int i = 0; i < options.ArrayLimit; ++i)
                {
                    EmitLeaf(sb, elemIndent, ids, $"[{i}]",
                             vartype: vt, showAsHex: hex, showAsSigned: signed,
                             address: RelAddr(dataOff + i * stride), offsets: null,
                             color: RecordColorFor(options, i));
                }
            }
            else if (IsManagedString(elementType))
            {
                for (int i = 0; i < options.ArrayLimit; ++i)
                {
                    EmitStringDerefGroup(sb, elemIndent, ids, $"[{i}]",
                                         RelAddr(dataOff + i * ptr), charLength: null, ptr,
                                         minChars: options.MinStringChars,
                                         color: RecordColorFor(options, i));
                }
            }
            else
            {
                GetClassInfoResult? elemClass = null;
                string? cycleKey = null;
                if (lookup is not null
                    && !TemplateExportHelpers.IsFrameworkOrEngineNamespace(elementType)
                    && InstanceFieldClassifier.TryParseClassName(elementType, out string ens, out string ename))
                {
                    string key = $"{assemblyName ?? "?"}!{ens}.{ename}";
                    if (!visited.Contains(key))
                    {
                        elemClass = await TemplateExportHelpers.TryLookupClassAsync(
                            lookup, assemblyName, ens, ename, ct).ConfigureAwait(false);
                        if (elemClass is not null) cycleKey = key;
                    }
                }

                if (elemClass is not null && TemplateExportHelpers.IsEnumClass(elemClass))
                {
                    string underlying = TemplateExportHelpers.ResolveEnumUnderlyingType(elemClass);
                    ManagedTypeShape es = UnityTypeMap.Resolve(underlying);
                    (bool hex, bool signed) = UnityTypeMap.CeDisplay(underlying);
                    for (int i = 0; i < options.ArrayLimit; ++i)
                    {
                        EmitLeaf(sb, elemIndent, ids, $"[{i}]",
                                 vartype: es.CeTypeKeyword, showAsHex: hex, showAsSigned: signed,
                                 address: RelAddr(dataOff + i * es.SizeBytes), offsets: null,
                                 color: RecordColorFor(options, i));
                    }
                }
                else if (elemClass is not null && TemplateExportHelpers.IsValueTypeClass(elemClass))
                {
                    int headerAdj = TemplateExportHelpers.ComputeValueTypeHeaderAdjust(elemClass, ptr);
                    int elemSize  = await TemplateExportHelpers.ComputeInlineStructSizeAsync(
                        elemClass, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
                    if (elemSize > 0 && cycleKey is not null && visited.Add(cycleKey))
                    {
                        try
                        {
                            for (int i = 0; i < options.ArrayLimit; ++i)
                            {
                                ct.ThrowIfCancellationRequested();
                                OpenGroup(sb, elemIndent, ids, $"[{i}]",
                                          RelAddr(dataOff + i * elemSize),
                                          offsets: null,
                                          collapsed: options.CollapsePointerNodes,
                                          varType: null, showAsHex: true,
                                          color: RecordColorFor(options, i));
                                await EmitFieldsAsync(sb, elemIndent + "  ", ids, elemClass,
                                                      isInherited: false, assemblyName,
                                                      options, lookup, visited,
                                                      currentDepth + 1, ct, offsetAdjust: headerAdj).ConfigureAwait(false);
                                CloseGroup(sb, elemIndent);
                            }
                        }
                        finally { visited.Remove(cycleKey); }
                    }
                }
                else if (elemClass is not null && cycleKey is not null && visited.Add(cycleKey))
                {
                    // Reference-type element slots: pointer stride at dataOff + i*ptr,
                    // each slot dereferenced with Offsets=[0].
                    try
                    {
                        for (int i = 0; i < options.ArrayLimit; ++i)
                        {
                            ct.ThrowIfCancellationRequested();
                            OpenGroup(sb, elemIndent, ids, $"[{i}]",
                                      RelAddr(dataOff + i * ptr),
                                      offsets: new[] { 0 },
                                      collapsed: options.CollapsePointerNodes,
                                      varType: null, showAsHex: true,
                                      color: RecordColorFor(options, i));
                            await EmitFieldsAsync(sb, elemIndent + "  ", ids, elemClass,
                                                  isInherited: false, assemblyName,
                                                  options, lookup, visited,
                                                  currentDepth + 1, ct, offsetAdjust: 0).ConfigureAwait(false);
                            CloseGroup(sb, elemIndent);
                        }
                    }
                    finally { visited.Remove(cycleKey); }
                }
                else
                {
                    // Unresolved / framework ref / cycle: emit pointer-strided leaves.
                    for (int i = 0; i < options.ArrayLimit; ++i)
                    {
                        EmitLeaf(sb, elemIndent, ids, $"[{i}]",
                                 vartype: PointerVartype(options), showAsHex: true, showAsSigned: false,
                                 address: RelAddr(dataOff + i * ptr), offsets: null,
                                 color: RecordColorFor(options, i));
                    }
                }
            }

            if (isList) CloseGroup(sb, indent + "  ");
        }

        CloseGroup(sb, indent);
    }

    // ---------- Emission primitives --------------------------------------

    private static void OpenGroup(
        StringBuilder sb, string indent, IdGen ids,
        string description, string address,
        int[]? offsets, bool collapsed, string? varType, bool showAsHex,
        string? color = null)
    {
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        if (color is not null)
            sb.Append(indent).Append("  <Color>").Append(color).Append("</Color>\n");
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
        string address, int[]? offsets, bool showAsSigned = false,
        string? color = null)
    {
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        if (color is not null)
            sb.Append(indent).Append("  <Color>").Append(color).Append("</Color>\n");
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

    private static string StructLabel(GetClassInfoResult info, string? assemblyName,
                                      ExportNameMap? nameMap = null)
    {
        string ns = info.Namespace ?? string.Empty;
        string nm = info.Name      ?? "Unknown";
        // Demangle compiler-generated nested-class paths so the CE address
        // list shows "Outer / Async [async 41]" instead of the raw
        // "Outer/<Async>d__41". When a NameMap is provided, register the
        // pair so the export prepends a comment block letting the reader
        // recover the canonical runtime name.
        string friendly = nameMap is null
            ? CompilerNameDemangler.DemangleClassPath(nm)
            : nameMap.Register(nm);
        string full = string.IsNullOrEmpty(ns) ? friendly : $"{ns}.{friendly}";
        return string.IsNullOrEmpty(assemblyName) ? full : $"{assemblyName}!{full}";
    }

    /// <summary>
    /// CE <c>&lt;Description&gt;</c> label for a single field. Verbose mode
    /// (default) yields the self-documenting <c>"name : Type"</c>; compact mode
    /// (<see cref="ExportOptions.VerboseDescription"/> = false) yields the bare
    /// field <c>name</c> so the CE table stays terse. Callers append value /
    /// state suffixes (= "...", &lt;null&gt;, [N of T]) only in verbose mode.
    /// </summary>
    private static string FieldDesc(string name, string? type, ExportOptions options,
                                    int? offset = null)
    {
        string desc = options.VerboseDescription ? $"{name} : {type ?? "?"}" : name;
        // DescShowOffset appends the field's offset-within-parent so the user
        // can cross-check the CE address against the dumper grid. Shown as the
        // real offset (not the cloaked one); the live exporter emits raw
        // offsets too, so the two always agree on a clean build.
        if (options.DescShowOffset && offset is int off)
            desc += $" (+{off:X})";
        return desc;
    }

    /// <summary>CE <c>&lt;Color&gt;</c> (BBGGRR) for the alternating record-colour
    /// feature: even indices use <see cref="ExportOptions.RecordColorEven"/>,
    /// odd use <see cref="ExportOptions.RecordColorOdd"/>. Null (→ no colour
    /// element, CE keeps its theme) when the feature is off or that parity is
    /// unset / malformed.</summary>
    private static string? RecordColorFor(ExportOptions options, int index)
    {
        if (!options.RecordColors) return null;
        return RgbToCeColor((index & 1) == 0 ? options.RecordColorEven : options.RecordColorOdd);
    }

    /// <summary>Convert a UI RGB hex string ("RRGGBB", optional '#') to a CE
    /// COLORREF hex ("BBGGRR"), or null when empty / malformed. CE stores a
    /// Win32 COLORREF (0x00BBGGRR), so the byte order is reversed from RGB:
    /// "0080FF" (azure) → "FF8000".</summary>
    private static string? RgbToCeColor(string? rgb)
    {
        if (string.IsNullOrWhiteSpace(rgb)) return null;
        string s = rgb.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return null;
        return (s.Substring(4, 2) + s.Substring(2, 2) + s.Substring(0, 2)).ToUpperInvariant();
    }

    private static string XmlEscape(string s) => TemplateExportHelpers.XmlEscape(s);

    private sealed class IdGen
    {
        private int _n;
        private readonly IProgress<int>? _progress;
        private int _sinceReport;
        public IdGen(IProgress<int>? progress = null) => _progress = progress;
        // Each emitted CE entry takes an id, so _n tracks rows produced. Report
        // it (throttled) so a long live export can show "exporting… N entries".
        public int Next()
        {
            int id = _n++;
            if (_progress is not null && ++_sinceReport >= 256)
            {
                _sinceReport = 0;
                _progress.Report(_n);
            }
            return id;
        }
    }
}
