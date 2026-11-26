// CsxExporter.cs — emits a Cheat Engine Structure Dissect XML
// (.csx-importable) describing the layout of a Unity managed class.
//
// Wire shape (CE-canonical, mirrors a "Save structure" round-trip):
//   <Structures>
//     <Structure Name="..." AutoFill="0" AutoCreate="1" DefaultHex="0"
//                AutoDestroy="0" DoNotSaveLocal="0" RLECompression="1"
//                AutoCreateStructsize="...">
//       <Elements>
//         <Element Offset="N" Vartype="..." Bytesize="..."
//                  OffsetHex="00000000" Description="..." DisplayMethod="..."/>
//         <Element ...>
//           <Structure ...>            <!-- nested child for pointer drilldown -->
//             <Elements> ... </Elements>
//           </Structure>
//         </Element>
//       </Elements>
//     </Structure>
//   </Structures>
//
// Both Offset (decimal) AND OffsetHex (8-digit zero-padded uppercase) are
// required by CE's loader — missing either raises EConvertError. Same for
// DisplayMethod. We deliberately omit the <?xml ?> prolog and any
// <!-- comments -->: stock CE output has neither.
//
// Drilldown: for class-typed pointer fields, value structs, enums, and
// contiguous collections (T[] / List<T>), RenderAsync resolves metadata via
// IClassInfoLookup:
//   * System.Enum      → flat inline scalar element using the enum's underlying type
//   * System.ValueType → flattened inline child elements at parentOffset + memberOffset
//                        (never Vartype="Pointer", which CE would dereference)
//   * Reference class  → Vartype="Pointer" element with nested <Structure>
//   * T[] / List<T>    → Pointer → (List: _items @ 2*ptr + _size @ 3*ptr) →
//                        SZARRAY data starting at 4*ptr with element-accurate stride
//   * Non-array collections (HashSet, Dictionary, IList, IEnumerable, ...) →
//                        flat Pointer element (no fabricated array layout)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Core.Services.Export;

public static class CsxExporter
{
    /// <summary>
    /// Single-instance, no-drilldown render. Kept for callers that don't have
    /// an <see cref="IClassInfoLookup"/> handy (tests, simple cases).
    /// </summary>
    public static string Render(
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors = null,
        string? assemblyName = null)
    {
        var sb = new StringBuilder(2048);
        sb.Append("<Structures>\n");
        RenderRootSync(sb, self, ancestors, assemblyName,
                       ExportOptions.Defaults with { DrilldownDepth = 0 });
        sb.Append("</Structures>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Drilldown render: recursively resolves field classes via
    /// <paramref name="lookup"/> for enums, inline value structs, pointer
    /// classes, and contiguous collections (<c>T[]</c> / <c>List&lt;T&gt;</c>).
    /// Honors <see cref="ExportOptions.DrilldownDepth"/>,
    /// <see cref="ExportOptions.ArrayLimit"/>, and
    /// <see cref="ExportOptions.PointerSize"/>.
    /// </summary>
    public static async Task<string> RenderAsync(
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors,
        string? assemblyName,
        ExportOptions options,
        IClassInfoLookup? lookup,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder(4096);
        sb.Append("<Structures>\n");
        var visited = new HashSet<string>();
        await RenderRootAsync(sb, self, ancestors, assemblyName,
                              options, lookup, visited, ct).ConfigureAwait(false);
        sb.Append("</Structures>\n");
        return sb.ToString();
    }

    // ---------- Root structure -------------------------------------------

    private static void RenderRootSync(
        StringBuilder sb, GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors, string? assemblyName,
        ExportOptions options)
    {
        int ptr = TemplateExportHelpers.NormalizePointerSize(options.PointerSize);
        string structName = StructName(self, assemblyName);
        var elements = new List<EmittedElement>();
        int totalSize = 0;

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                CollectFlatFieldsSync(elements, ref totalSize, anc, isInherited: true, ptr);
        }
        CollectFlatFieldsSync(elements, ref totalSize, self, isInherited: false, ptr);

        OpenStructure(sb, "  ", structName, totalSize);
        foreach (EmittedElement e in elements)
        {
            EmitElementOpen(sb, "      ", e);
            sb.Append("/>\n");
        }
        CloseStructure(sb, "  ");
    }

    private static async Task RenderRootAsync(
        StringBuilder sb, GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors, string? assemblyName,
        ExportOptions options, IClassInfoLookup? lookup,
        HashSet<string> visited, CancellationToken ct)
    {
        int ptr = TemplateExportHelpers.NormalizePointerSize(options.PointerSize);
        string structName = StructName(self, assemblyName);
        visited.Add(structName);

        var body = new StringBuilder(2048);
        int totalSize = 0;

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
            {
                int end = await EmitClassFieldsAsync(
                    body, "      ", anc, isInherited: true,
                    baseOffset: 0, offsetAdjust: 0, namePrefix: null,
                    assemblyName, options, lookup, visited,
                    currentDepth: 0, ptr, ct).ConfigureAwait(false);
                if (end > totalSize) totalSize = end;
            }
        }
        int selfEnd = await EmitClassFieldsAsync(
            body, "      ", self, isInherited: false,
            baseOffset: 0, offsetAdjust: 0, namePrefix: null,
            assemblyName, options, lookup, visited,
            currentDepth: 0, ptr, ct).ConfigureAwait(false);
        if (selfEnd > totalSize) totalSize = selfEnd;

        OpenStructure(sb, "  ", structName, totalSize);
        sb.Append(body);
        CloseStructure(sb, "  ");
    }

    // ---------- Element models & sync fallback ---------------------------

    private readonly record struct EmittedElement(
        int     Offset,
        string  Vartype,
        int     Bytesize,
        string  Description,
        string  DisplayMethod);

    private static void CollectFlatFieldsSync(
        List<EmittedElement> sink, ref int totalSize,
        GetClassInfoResult info, bool isInherited, int ptr)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        string declaredIn = DeclaredInLabel(info);

        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
            bool isNativeInt = TemplateExportHelpers.IsNativeIntType(f.Type);
            string vt = shape.IsPointer
                ? "Pointer"
                : (isNativeInt ? TemplateExportHelpers.PointerVartype(ptr) : shape.CeTypeKeyword);
            int sz = (shape.IsPointer || isNativeInt) ? ptr : shape.SizeBytes;
            string desc = isInherited
                ? $"{f.Name} : {f.Type ?? "?"}  (inherited from {declaredIn})"
                : $"{f.Name} : {f.Type ?? "?"}";

            sink.Add(new EmittedElement(
                Offset: f.Offset,
                Vartype: vt,
                Bytesize: sz,
                Description: desc,
                DisplayMethod: DisplayMethodForType(f.Type, vt)));
            int end = f.Offset + sz;
            if (end > totalSize) totalSize = end;
        }
    }

    // ---------- Async drilldown emission ---------------------------------

    private static async Task<int> EmitClassFieldsAsync(
        StringBuilder sb, string indent,
        GetClassInfoResult info, bool isInherited,
        int baseOffset, int offsetAdjust, string? namePrefix,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, int ptr, CancellationToken ct)
    {
        if (info.Fields is not { Count: > 0 } fields) return baseOffset;
        string declaredIn = DeclaredInLabel(info);
        int maxEnd = baseOffset;

        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            int fieldOffset = baseOffset + Math.Max(0, f.Offset - offsetAdjust);
            string fieldName = string.IsNullOrEmpty(namePrefix) ? f.Name : namePrefix + f.Name;
            string desc = isInherited
                ? $"{fieldName} : {f.Type ?? "?"}  (inherited from {declaredIn})"
                : $"{fieldName} : {f.Type ?? "?"}";

            ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);

            // 1. Known primitive / non-pointer scalar.
            if (!shape.IsPointer)
            {
                bool isNativeInt = TemplateExportHelpers.IsNativeIntType(f.Type);
                string vt = isNativeInt ? TemplateExportHelpers.PointerVartype(ptr) : shape.CeTypeKeyword;
                int sz = isNativeInt ? ptr : shape.SizeBytes;
                EmitFlat(sb, indent, fieldOffset, vt, sz, desc, DisplayMethodForType(f.Type, vt));
                if (fieldOffset + sz > maxEnd) maxEnd = fieldOffset + sz;
                continue;
            }

            // 2. Contiguous collection: T[] or List<T>.
            if (!string.IsNullOrEmpty(f.Type)
                && InstanceFieldClassifier.TryParseCollectionType(f.Type, out string elementType, out bool isList))
            {
                if (fieldOffset + ptr > maxEnd) maxEnd = fieldOffset + ptr;
                if (currentDepth < options.DrilldownDepth && options.ArrayLimit > 0)
                {
                    await EmitCollectionElementAsync(
                        sb, indent, fieldOffset, desc, elementType, isList,
                        assemblyName, options, lookup, visited,
                        currentDepth, ptr, ct).ConfigureAwait(false);
                }
                else
                {
                    EmitFlat(sb, indent, fieldOffset, "Pointer", ptr, desc, "hexadecimal");
                }
                continue;
            }

            // 3. Non-array collection (HashSet, Dictionary, IList, IEnumerable, ...):
            //    flat Pointer element — never fabricate an array layout.
            if (TemplateExportHelpers.IsNonArrayCollectionType(f.Type))
            {
                EmitFlat(sb, indent, fieldOffset, "Pointer", ptr, desc, "hexadecimal");
                if (fieldOffset + ptr > maxEnd) maxEnd = fieldOffset + ptr;
                continue;
            }

            // 4. Ambiguous game type: resolve via lookup even at depth cap so
            //    enums and ValueType structs never emit Vartype="Pointer".
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
                EmitFlat(sb, indent, fieldOffset, es.CeTypeKeyword, es.SizeBytes, desc,
                         DisplayMethodForType(underlying, es.CeTypeKeyword));
                if (fieldOffset + es.SizeBytes > maxEnd) maxEnd = fieldOffset + es.SizeBytes;
                continue;
            }

            if (child is not null && TemplateExportHelpers.IsValueTypeClass(child))
            {
                int headerAdj = TemplateExportHelpers.ComputeValueTypeHeaderAdjust(child, ptr);
                if (currentDepth < options.DrilldownDepth && cycleKey is not null && visited.Add(cycleKey))
                {
                    int inlineEnd;
                    try
                    {
                        inlineEnd = await EmitClassFieldsAsync(
                            sb, indent, child, isInherited: false,
                            baseOffset: fieldOffset, offsetAdjust: headerAdj,
                            namePrefix: fieldName + ".",
                            assemblyName, options, lookup, visited,
                            currentDepth + 1, ptr, ct).ConfigureAwait(false);
                    }
                    finally { visited.Remove(cycleKey); }

                    if (inlineEnd > fieldOffset)
                    {
                        if (inlineEnd > maxEnd) maxEnd = inlineEnd;
                    }
                    else
                    {
                        // Empty value struct: emit 1-byte inline placeholder (never Pointer).
                        EmitFlat(sb, indent, fieldOffset, "Array of byte", 1, desc, "hexadecimal");
                        if (fieldOffset + 1 > maxEnd) maxEnd = fieldOffset + 1;
                    }
                }
                else
                {
                    int sz = await TemplateExportHelpers.ComputeInlineStructSizeAsync(
                        child, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
                    if (sz <= 0) sz = ptr;
                    EmitFlat(sb, indent, fieldOffset, "Array of byte", sz, desc, "hexadecimal");
                    if (fieldOffset + sz > maxEnd) maxEnd = fieldOffset + sz;
                }
                continue;
            }

            // 5. Reference class (resolved or unresolved).
            if (fieldOffset + ptr > maxEnd) maxEnd = fieldOffset + ptr;
            if (child is not null
                && currentDepth < options.DrilldownDepth
                && cycleKey is not null
                && visited.Add(cycleKey))
            {
                try
                {
                    var elem = new EmittedElement(fieldOffset, "Pointer", ptr, desc, "hexadecimal");
                    EmitElementOpen(sb, indent, elem);
                    sb.Append(">\n");

                    string childStructIndent = indent + "  ";
                    string childElemsIndent  = indent + "      ";
                    var childBody = new StringBuilder(512);
                    int childTotal = await EmitClassFieldsAsync(
                        childBody, childElemsIndent, child, isInherited: false,
                        baseOffset: 0, offsetAdjust: 0, namePrefix: null,
                        assemblyName, options, lookup, visited,
                        currentDepth + 1, ptr, ct).ConfigureAwait(false);

                    OpenStructure(sb, childStructIndent, StructName(child, assemblyName), childTotal);
                    sb.Append(childBody);
                    CloseStructure(sb, childStructIndent);
                    sb.Append(indent).Append("</Element>\n");
                }
                finally { visited.Remove(cycleKey); }
            }
            else
            {
                EmitFlat(sb, indent, fieldOffset, "Pointer", ptr, desc, "hexadecimal");
            }
        }

        return maxEnd;
    }

    private static async Task EmitCollectionElementAsync(
        StringBuilder sb, string indent, int fieldOffset, string desc,
        string elementType, bool isList,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, int ptr, CancellationToken ct)
    {
        int itemsOff = 2 * ptr; // List<T>._items: +0x10 (x64) / +0x08 (x86)
        int sizeOff  = 3 * ptr; // List<T>._size:  +0x18 (x64) / +0x0C (x86)
        int dataOff  = 4 * ptr; // SZARRAY data:   +0x20 (x64) / +0x10 (x86)

        var outer = new EmittedElement(fieldOffset, "Pointer", ptr, desc, "hexadecimal");
        EmitElementOpen(sb, indent, outer);
        sb.Append(">\n");

        string arrStructIndent = indent + "  ";
        if (isList)
        {
            string listStructIndent = indent + "  ";
            string listElemsIndent  = indent + "      ";
            OpenStructure(sb, listStructIndent, $"List<{elementType}>", sizeOff + 4);

            var itemsElem = new EmittedElement(
                itemsOff, "Pointer", ptr, $"_items : {elementType}[]", "hexadecimal");
            EmitElementOpen(sb, listElemsIndent, itemsElem);
            sb.Append(">\n");

            arrStructIndent = listElemsIndent + "  ";
            await EmitSzArrayStructureAsync(
                sb, arrStructIndent, elementType, dataOff,
                assemblyName, options, lookup, visited,
                currentDepth, ptr, ct).ConfigureAwait(false);

            sb.Append(listElemsIndent).Append("</Element>\n");
            EmitFlat(sb, listElemsIndent, sizeOff, "4 Bytes", 4, "_size : System.Int32", "signed integer");
            CloseStructure(sb, listStructIndent);
        }
        else
        {
            await EmitSzArrayStructureAsync(
                sb, arrStructIndent, elementType, dataOff,
                assemblyName, options, lookup, visited,
                currentDepth, ptr, ct).ConfigureAwait(false);
        }

        sb.Append(indent).Append("</Element>\n");
    }

    private static async Task EmitSzArrayStructureAsync(
        StringBuilder sb, string structIndent, string elementType, int dataOff,
        string? assemblyName, ExportOptions options,
        IClassInfoLookup? lookup, HashSet<string> visited,
        int currentDepth, int ptr, CancellationToken ct)
    {
        string elemsIndent = structIndent + "    ";
        var body = new StringBuilder(1024);
        int totalSize = dataOff;
        string arrLabel = $"{elementType}[{options.ArrayLimit}]";

        ManagedTypeShape elemShape = UnityTypeMap.Resolve(elementType);
        if (!elemShape.IsPointer)
        {
            bool isNativeInt = TemplateExportHelpers.IsNativeIntType(elementType);
            int stride = isNativeInt ? ptr : elemShape.SizeBytes;
            string vt  = isNativeInt ? TemplateExportHelpers.PointerVartype(ptr) : elemShape.CeTypeKeyword;
            string dm  = DisplayMethodForType(elementType, vt);
            for (int i = 0; i < options.ArrayLimit; ++i)
            {
                int off = dataOff + i * stride;
                EmitFlat(body, elemsIndent, off, vt, stride, $"[{i}] {elementType}", dm);
                totalSize = off + stride;
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
                arrLabel = $"{StructName(elemClass, assemblyName)}[{options.ArrayLimit}]";
                string underlying = TemplateExportHelpers.ResolveEnumUnderlyingType(elemClass);
                ManagedTypeShape es = UnityTypeMap.Resolve(underlying);
                string dm = DisplayMethodForType(underlying, es.CeTypeKeyword);
                for (int i = 0; i < options.ArrayLimit; ++i)
                {
                    int off = dataOff + i * es.SizeBytes;
                    EmitFlat(body, elemsIndent, off, es.CeTypeKeyword, es.SizeBytes,
                             $"[{i}] {StructName(elemClass, assemblyName)}", dm);
                    totalSize = off + es.SizeBytes;
                }
            }
            else if (elemClass is not null && TemplateExportHelpers.IsValueTypeClass(elemClass))
            {
                string elemName = StructName(elemClass, assemblyName);
                arrLabel = $"{elemName}[{options.ArrayLimit}]";
                int headerAdj = TemplateExportHelpers.ComputeValueTypeHeaderAdjust(elemClass, ptr);
                int elemSize  = await TemplateExportHelpers.ComputeInlineStructSizeAsync(
                    elemClass, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
                if (elemSize > 0 && cycleKey is not null && visited.Add(cycleKey))
                {
                    try
                    {
                        for (int i = 0; i < options.ArrayLimit; ++i)
                        {
                            int slotOff = dataOff + i * elemSize;
                            int slotEnd = await EmitClassFieldsAsync(
                                body, elemsIndent, elemClass, isInherited: false,
                                baseOffset: slotOff, offsetAdjust: headerAdj,
                                namePrefix: $"[{i}].",
                                assemblyName, options, lookup, visited,
                                currentDepth + 1, ptr, ct).ConfigureAwait(false);
                            totalSize = Math.Max(totalSize, Math.Max(slotOff + elemSize, slotEnd));
                        }
                    }
                    finally { visited.Remove(cycleKey); }
                }
            }
            else if (elemClass is not null && cycleKey is not null && visited.Add(cycleKey))
            {
                // Reference-type element slots: pointer stride at dataOff + i*ptr,
                // each Pointer element dereferences into the element's <Structure>.
                string childName = StructName(elemClass, assemblyName);
                arrLabel = $"{childName}[{options.ArrayLimit}]";
                try
                {
                    for (int i = 0; i < options.ArrayLimit; ++i)
                    {
                        int slotOff = dataOff + i * ptr;
                        var slot = new EmittedElement(slotOff, "Pointer", ptr, $"[{i}] {childName}", "hexadecimal");
                        EmitElementOpen(body, elemsIndent, slot);
                        body.Append(">\n");

                        var childBody = new StringBuilder(512);
                        int childTotal = await EmitClassFieldsAsync(
                            childBody, elemsIndent + "      ", elemClass, isInherited: false,
                            baseOffset: 0, offsetAdjust: 0, namePrefix: null,
                            assemblyName, options, lookup, visited,
                            currentDepth + 1, ptr, ct).ConfigureAwait(false);

                        OpenStructure(body, elemsIndent + "  ", childName, childTotal);
                        body.Append(childBody);
                        CloseStructure(body, elemsIndent + "  ");
                        body.Append(elemsIndent).Append("</Element>\n");
                        totalSize = slotOff + ptr;
                    }
                }
                finally { visited.Remove(cycleKey); }
            }
            else
            {
                for (int i = 0; i < options.ArrayLimit; ++i)
                {
                    int off = dataOff + i * ptr;
                    EmitFlat(body, elemsIndent, off, "Pointer", ptr, $"[{i}] {elementType}", "hexadecimal");
                    totalSize = off + ptr;
                }
            }
        }

        OpenStructure(sb, structIndent, arrLabel, totalSize);
        sb.Append(body);
        CloseStructure(sb, structIndent);
    }

    // ---------- Emission primitives --------------------------------------

    private static void EmitFlat(
        StringBuilder sb, string indent, int offset,
        string vartype, int bytesize, string description, string displayMethod)
    {
        EmitElementOpen(sb, indent, new EmittedElement(offset, vartype, bytesize, description, displayMethod));
        sb.Append("/>\n");
    }

    private static void EmitElementOpen(StringBuilder sb, string indent, EmittedElement e)
    {
        // Both Offset (decimal) and OffsetHex (8-digit hex) must render the
        // same value or CE refuses the dissect file.
        int o = e.Offset;
        sb.Append(indent)
          .Append("<Element Offset=\"").Append(o.ToString(CultureInfo.InvariantCulture))
          .Append("\" Vartype=\"").Append(XmlEscape(e.Vartype))
          .Append("\" Bytesize=\"").Append(e.Bytesize.ToString(CultureInfo.InvariantCulture))
          .Append("\" OffsetHex=\"").Append(o.ToString("X8", CultureInfo.InvariantCulture))
          .Append("\" Description=\"").Append(XmlEscape(e.Description))
          .Append("\" DisplayMethod=\"").Append(e.DisplayMethod)
          .Append("\"");
    }

    // ---------- Helpers ---------------------------------------------------

    private static string DeclaredInLabel(GetClassInfoResult info)
        => string.IsNullOrEmpty(info.Namespace)
            ? (info.Name ?? "?")
            : $"{info.Namespace}.{info.Name}";

    private static void OpenStructure(StringBuilder sb, string indent,
                                      string name, int autoCreateStructsize)
    {
        sb.Append(indent)
          .Append("<Structure Name=\"").Append(XmlEscape(name))
          .Append("\" AutoFill=\"0\" AutoCreate=\"1\" DefaultHex=\"0\" AutoDestroy=\"0\" DoNotSaveLocal=\"0\" RLECompression=\"1\" AutoCreateStructsize=\"")
          .Append(autoCreateStructsize.ToString(CultureInfo.InvariantCulture))
          .Append("\">\n")
          .Append(indent).Append("  <Elements>\n");
    }

    private static void CloseStructure(StringBuilder sb, string indent)
    {
        sb.Append(indent).Append("  </Elements>\n");
        sb.Append(indent).Append("</Structure>\n");
    }

    private static string StructName(GetClassInfoResult info, string? assemblyName)
    {
        string ns = info.Namespace ?? string.Empty;
        string nm = info.Name      ?? "Unknown";
        // Same demangle rationale as CeXmlExporter.StructLabel — the
        // Dissect Data view shows this as a human-readable structure
        // title, so the friendly form is much more useful than the raw
        // "<Foo>d__N" runtime name. CSX intentionally omits XML comments
        // (see file header — matches CE's own export shape), so no
        // name-map block is prepended; the raw form survives in the
        // wire metadata but not in the .csx file itself.
        string friendly = CompilerNameDemangler.DemangleClassPath(nm);
        string full = string.IsNullOrEmpty(ns) ? friendly : $"{ns}.{friendly}";
        return string.IsNullOrEmpty(assemblyName) ? full : $"{assemblyName}!{full}";
    }

    private static string DisplayMethodForType(string? unityType, string vartype)
    {
        if (vartype == "Float") return "float";
        if (vartype == "Double") return "double";
        if (vartype is "Pointer" or "Array of byte") return "hexadecimal";
        return unityType switch
        {
            "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64" => "signed integer",
            _ => "unsigned integer",
        };
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
}
