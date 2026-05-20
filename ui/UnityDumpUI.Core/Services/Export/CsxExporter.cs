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
// Drilldown: for class-typed pointer fields and arrays, RenderAsync
// recursively pulls child class info via IClassInfoLookup and emits the
// nested <Structure> child. Cycle detection via a visited-fullnames set;
// depth bounded by ExportOptions.DrilldownDepth.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        var visited = new HashSet<string>();
        RenderRoot(sb, self, ancestors, assemblyName,
                   ExportOptions.Defaults with { DrilldownDepth = 0 },
                   lookup: null, visited);
        sb.Append("</Structures>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Drilldown render: recursively pulls child class info via
    /// <paramref name="lookup"/> for pointer / array fields. Honors
    /// <see cref="ExportOptions.DrilldownDepth"/> and
    /// <see cref="ExportOptions.ArrayLimit"/>.
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

    private static void RenderRoot(
        StringBuilder sb, GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors, string? assemblyName,
        ExportOptions options, IClassInfoLookup? lookup,
        HashSet<string> visited)
    {
        string structName = StructName(self, assemblyName);
        var (elements, totalSize) = CollectElementsSync(self, ancestors);

        OpenStructure(sb, "  ", structName, totalSize);
        EmitElements(sb, "      ", elements,
                     childRenderer: null, options, lookup, visited);
        CloseStructure(sb, "  ");
    }

    private static async Task RenderRootAsync(
        StringBuilder sb, GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors, string? assemblyName,
        ExportOptions options, IClassInfoLookup? lookup,
        HashSet<string> visited, CancellationToken ct)
    {
        string structName = StructName(self, assemblyName);
        visited.Add(structName);

        var elements = new List<EmittedElement>();
        int totalSize = 0;

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                AddFields(elements, ref totalSize, anc, isInherited: true);
        }
        AddFields(elements, ref totalSize, self, isInherited: false);

        OpenStructure(sb, "  ", structName, totalSize);
        await EmitElementsAsync(sb, "      ", elements,
                                assemblyName, options, lookup, visited,
                                currentDepth: 0, ct).ConfigureAwait(false);
        CloseStructure(sb, "  ");
    }

    // ---------- Element collection ---------------------------------------

    /// <summary>One emitted CSX element + the metadata needed to decide if it
    /// should grow a nested child Structure.</summary>
    private readonly record struct EmittedElement(
        int     Offset,
        string  Vartype,
        int     Bytesize,
        string  Description,
        string  DisplayMethod,
        // Drilldown source: the original field type so we can recurse later.
        string? FieldTypeRaw,
        bool    IsPointer);

    private static (List<EmittedElement> elements, int totalSize) CollectElementsSync(
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors)
    {
        var list = new List<EmittedElement>();
        int total = 0;
        if (ancestors is { Count: > 0 })
            foreach (var anc in ancestors)
                AddFields(list, ref total, anc, isInherited: true);
        AddFields(list, ref total, self, isInherited: false);
        return (list, total);
    }

    private static void AddFields(
        List<EmittedElement> sink, ref int totalSize,
        GetClassInfoResult info, bool isInherited)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        string declaredIn = string.IsNullOrEmpty(info.Namespace)
            ? (info.Name ?? "?")
            : $"{info.Namespace}.{info.Name}";

        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
            string desc = isInherited
                ? $"{f.Name} : {f.Type ?? "?"}  (inherited from {declaredIn})"
                : $"{f.Name} : {f.Type ?? "?"}";

            sink.Add(new EmittedElement(
                Offset: f.Offset,
                Vartype: shape.CeTypeKeyword,
                Bytesize: shape.SizeBytes,
                Description: desc,
                DisplayMethod: DisplayMethodFor(shape.CeTypeKeyword),
                FieldTypeRaw: f.Type,
                IsPointer: shape.IsPointer));
            int end = f.Offset + shape.SizeBytes;
            if (end > totalSize) totalSize = end;
        }
    }

    // ---------- Element emission -----------------------------------------

    /// <summary>
    /// Sync emission, no drilldown. Used by the legacy Render() path and
    /// (internally) when the async path bottoms out.
    /// </summary>
    private static void EmitElements(
        StringBuilder sb, string indent,
        List<EmittedElement> elements,
        System.Action<StringBuilder, string, EmittedElement>? childRenderer,
        ExportOptions options, IClassInfoLookup? lookup,
        HashSet<string> visited)
    {
        foreach (var e in elements)
        {
            EmitElementOpen(sb, indent, e);
            sb.Append("/>\n");
        }
    }

    private static async Task EmitElementsAsync(
        StringBuilder sb, string indent,
        List<EmittedElement> elements,
        string? assemblyName,
        ExportOptions options, IClassInfoLookup? lookup,
        HashSet<string> visited, int currentDepth,
        CancellationToken ct)
    {
        foreach (var e in elements)
        {
            // Decide whether to grow a nested <Structure> child.
            bool shouldDrill =
                lookup is not null
                && currentDepth < options.DrilldownDepth
                && e.IsPointer
                && !string.IsNullOrEmpty(e.FieldTypeRaw);

            if (!shouldDrill)
            {
                EmitElementOpen(sb, indent, e);
                sb.Append("/>\n");
                continue;
            }

            // Resolve target class for drilldown.
            var target = UnityTypeMap.ExtractElementClass(e.FieldTypeRaw);
            if (target is null)
            {
                EmitElementOpen(sb, indent, e);
                sb.Append("/>\n");
                continue;
            }

            // Cycle check on (assembly!ns.name) so the same class doesn't
            // reappear in its own descendants forever.
            string cycleKey = $"{assemblyName ?? "?"}!{target.Value.Namespace}.{target.Value.Name}";
            if (!visited.Add(cycleKey))
            {
                EmitElementOpen(sb, indent, e);
                sb.Append("/>\n");
                continue;
            }

            GetClassInfoResult? child = null;
            try
            {
                child = await lookup!.GetClassInfoAsync(
                    assemblyName ?? string.Empty,
                    target.Value.Namespace, target.Value.Name, ct).ConfigureAwait(false);
            }
            catch { /* tolerate pipe failures — fall through to flat element */ }

            if (child is null || !string.IsNullOrEmpty(child.Error))
            {
                visited.Remove(cycleKey);
                EmitElementOpen(sb, indent, e);
                sb.Append("/>\n");
                continue;
            }

            // Open element with a child Structure inside.
            EmitElementOpen(sb, indent, e);
            sb.Append(">\n");

            string childIndent = indent + "  ";
            string childStructIndent = indent + "  ";
            string childElementsIndent = indent + "      ";

            // Build the child's own element list (no inherited chain — that
            // would explode quickly; user can recurse from a top-level export).
            var childElements = new List<EmittedElement>();
            int childTotal = 0;
            AddFields(childElements, ref childTotal, child, isInherited: false);

            string childName = StructName(child, assemblyName);
            // Collection-typed fields: emit ArrayLimit synthetic entries so
            // the user gets a per-index breakdown instead of a single struct
            // header. Each entry sits at index*elemSize within the child
            // structure (CE will add the dereferenced data pointer).
            bool isCollection = UnityTypeMap.IsCollectionType(e.FieldTypeRaw);
            if (isCollection && options.ArrayLimit > 0)
            {
                // Build per-index elements that wrap the child class. The
                // outer Structure becomes a flat array; each <Element> at
                // offset i*size points to a nested Structure for the element
                // class. We treat the element class as if it had been the
                // child target.
                int elemSize = childTotal > 0 ? childTotal : 8;
                OpenStructure(sb, childStructIndent,
                              $"{childName}[{options.ArrayLimit}]", elemSize * options.ArrayLimit);
                for (int i = 0; i < options.ArrayLimit; ++i)
                {
                    int slotOffset = i * elemSize;
                    var slot = new EmittedElement(
                        Offset: slotOffset,
                        Vartype: "Pointer",
                        Bytesize: 8,
                        Description: $"[{i}] {childName}",
                        DisplayMethod: "hexadecimal",
                        FieldTypeRaw: e.FieldTypeRaw, // recurse into element class on next level
                        IsPointer: true);

                    // Each slot becomes a nested element with the resolved struct.
                    EmitElementOpen(sb, childElementsIndent, slot);
                    sb.Append(">\n");
                    OpenStructure(sb, childElementsIndent + "  ", childName, childTotal);
                    await EmitElementsAsync(sb, childElementsIndent + "      ",
                                            childElements, assemblyName, options,
                                            lookup, visited, currentDepth + 1, ct)
                        .ConfigureAwait(false);
                    CloseStructure(sb, childElementsIndent + "  ");
                    sb.Append(childElementsIndent).Append("</Element>\n");
                }
                CloseStructure(sb, childStructIndent);
            }
            else
            {
                OpenStructure(sb, childStructIndent, childName, childTotal);
                await EmitElementsAsync(sb, childElementsIndent,
                                        childElements, assemblyName, options,
                                        lookup, visited, currentDepth + 1, ct)
                    .ConfigureAwait(false);
                CloseStructure(sb, childStructIndent);
            }

            sb.Append(indent).Append("</Element>\n");
            visited.Remove(cycleKey);
        }
    }

    private static void EmitElementOpen(StringBuilder sb, string indent, EmittedElement e)
    {
        // Both Offset (decimal) and OffsetHex (8-digit hex) must render the
        // same value or CE refuses the dissect file. CloakOffset is a no-op
        // on intact / dev builds; on tampered binaries it shifts every
        // emitted offset by 0-127 bytes so CE reads the wrong field.
        int o = CloakOffset(e.Offset);
        sb.Append(indent)
          .Append("<Element Offset=\"").Append(o.ToString(CultureInfo.InvariantCulture))
          .Append("\" Vartype=\"").Append(XmlEscape(e.Vartype))
          .Append("\" Bytesize=\"").Append(e.Bytesize.ToString(CultureInfo.InvariantCulture))
          .Append("\" OffsetHex=\"").Append(o.ToString("X8", CultureInfo.InvariantCulture))
          .Append("\" Description=\"").Append(XmlEscape(e.Description))
          .Append("\" DisplayMethod=\"").Append(e.DisplayMethod)
          .Append("\"");
    }

    private static int CloakOffset(int offset)
        => offset ^ (TokenAggregator.Combined() & 0x7F);

    // ---------- Helpers ---------------------------------------------------

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
        string full = string.IsNullOrEmpty(ns) ? nm : $"{ns}.{nm}";
        return string.IsNullOrEmpty(assemblyName) ? full : $"{assemblyName}!{full}";
    }

    private static string DisplayMethodFor(string vartype) => vartype switch
    {
        "Float"   => "float",
        "Double"  => "double",
        "Pointer" => "hexadecimal",
        _         => "unsigned integer",
    };

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
