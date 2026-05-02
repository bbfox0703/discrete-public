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
        var sb = new StringBuilder(4096);
        var idGen = new IdGen();
        var visited = new HashSet<string>();

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        string rootName  = StructLabel(self, assemblyName);
        OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                  offsets: null, collapsed: false, varType: "8 Bytes", showAsHex: true);

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

    /// <summary>
    /// Single-field "pointer chain" export: emits a CE entry rooted at
    /// <paramref name="baseAddress"/> that drills exactly one field, useful
    /// for "right-click → Export pointer chain" workflows.
    /// </summary>
    public static string RenderField(
        string             baseAddress,
        FieldEntry         field,
        GetClassInfoResult declaringClass,
        string?            assemblyName)
    {
        var sb = new StringBuilder(1024);
        var idGen = new IdGen();
        ManagedTypeShape shape = UnityTypeMap.Resolve(field.Type);

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        string rootName = $"{StructLabel(declaringClass, assemblyName)}.{field.Name}";
        if (shape.IsPointer)
        {
            // Pointer chain: base + [+0xNN] -> Offsets=[0] dereferences, child shows the value.
            OpenGroup(sb, "    ", idGen, rootName, baseAddress,
                      offsets: null, collapsed: false, varType: "8 Bytes", showAsHex: true);
            EmitLeaf(sb, "      ", idGen, $"{field.Name} (deref)",
                     vartype: "8 Bytes", showAsHex: true,
                     address: $"+{field.Offset:X}",
                     offsets: new[] { 0 });
            CloseGroup(sb, "    ");
        }
        else
        {
            EmitLeaf(sb, "    ", idGen, rootName,
                     vartype: shape.CeTypeKeyword, showAsHex: false,
                     address: $"{baseAddress}+{field.Offset:X}",
                     offsets: null);
        }

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
            ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
            string desc = isInherited
                ? $"{f.Name} : {f.Type ?? "?"}  (inherited)"
                : $"{f.Name} : {f.Type ?? "?"}";
            string addr = $"+{f.Offset:X}";

            if (!shape.IsPointer)
            {
                EmitLeaf(sb, indent, ids, desc,
                         vartype: shape.CeTypeKeyword,
                         showAsHex: shape.CeTypeKeyword.EndsWith("Bytes"),
                         address: addr, offsets: null);
                continue;
            }

            // Pointer field — emit a GroupHeader that dereferences via Offsets=[0].
            // Drilldown depth and resolvability decide whether children are emitted.
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
                    cycleKey = null; // already visited; emit placeholder only
                }
            }

            // Open the pointer group.
            OpenGroup(sb, indent, ids, desc, addr,
                      offsets: new[] { 0 },
                      collapsed: options.CollapsePointerNodes,
                      varType: null, showAsHex: true);

            if (child is not null && isCollection && options.ArrayLimit > 0)
            {
                // Array-of-class drilldown: per-element entry at offset
                // i*childSize, each containing the resolved struct's fields.
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
                // Single-instance pointer drilldown.
                await EmitFieldsAsync(sb, indent + "  ", ids, child,
                                      isInherited: false, assemblyName,
                                      options, lookup, visited,
                                      currentDepth + 1, ct).ConfigureAwait(false);
            }

            CloseGroup(sb, indent);
            if (cycleKey is not null) visited.Remove(cycleKey);
        }
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
        if (showAsHex)
            sb.Append(indent).Append("  <ShowAsHex>1</ShowAsHex>\n");
        sb.Append(indent).Append("  <ShowAsSigned>0</ShowAsSigned>\n");
        sb.Append(indent).Append("  <GroupHeader>1</GroupHeader>\n");
        if (collapsed && offsets is not null && address.StartsWith("+"))
            sb.Append(indent).Append("  <Options moHideChildren=\"1\" moDeactivateChildrenAsWell=\"1\"/>\n");
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
        string address, int[]? offsets)
    {
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        if (showAsHex)
            sb.Append(indent).Append("  <ShowAsHex>1</ShowAsHex>\n");
        sb.Append(indent).Append("  <ShowAsSigned>0</ShowAsSigned>\n");
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
