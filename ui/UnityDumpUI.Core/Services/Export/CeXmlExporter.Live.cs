// CeXmlExporter.Live.cs — walkInstance-driven live CE XML emission.
//
// Split out from CeXmlExporter.cs (2026-05-30). The original live path
// resolved child classes by *name* (getClassInfo) and decoded field bytes
// with the hardcoded UnityTypeMap, so it couldn't:
//   * drill nested classes whose name lookup fails (CharacterData.CharacterInfo),
//   * decode enums (charaKind_ showed a bogus pointer-deref group → 0x65),
//   * decode strings (charaName_ / hasSkill_[i] showed the String object ptr),
//   * drill List<T> element objects (enchantList_ stopped at 8-byte leaves).
//
// This rewrite drives the live export off the same walkInstance /
// walkInstanceFromHeader path the Inspector grid uses, so the generated CE
// table matches the grid exactly. Field classification reuses
// InstanceFieldClassifier; pointer drilldown reads the runtime class pointer
// from the object header (handles nested + generic instantiations); strings
// become CE String entries at the inline UTF-16 buffer; enums become signed
// int leaves. Addressing model = pointer-drilldown + linear offset (each node
// rooted at the concrete absolute address walkInstance reports).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Core.Services.Export;

public static partial class CeXmlExporter
{
    /// <summary>
    /// Emit one walkInstance-decoded field in live mode. Routes by the same
    /// <see cref="InstanceFieldClassifier"/> kind the Inspector uses. Cycle-
    /// detected on concrete address; depth-bounded by DrilldownDepth.
    /// </summary>
    private static async Task EmitWalkedFieldLiveAsync(
        StringBuilder sb, string indent, IdGen ids,
        WalkedField f, string? assemblyName, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited,
        int currentDepth, CancellationToken ct)
    {
        string desc = $"{f.Name} : {f.Type ?? "?"}";
        string? fieldAddr = f.Address;
        bool isPointerValue = !string.IsNullOrEmpty(f.Ptr)
                              && f.Ptr != "0x0000000000000000"
                              && !f.Value.HasValue && f.String is null;
        WalkedFieldKind kind = InstanceFieldClassifier.Classify(f, isPointerValue);
        bool canDrill = currentDepth < options.DrilldownDepth;

        // ---- String — CE String entry at the inline UTF-16 char buffer.
        if (kind == WalkedFieldKind.Leaf && IsManagedString(f.Type))
        {
            if (string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
            {
                EmitLeaf(sb, indent, ids, desc + "  <null>",
                         vartype: "8 Bytes", showAsHex: true,
                         address: fieldAddr ?? "0x0", offsets: null);
                return;
            }
            if (TryParseHexAddress(f.Ptr, out ulong strObj))
            {
                ulong charsAddr = strObj + (ulong)(2 * options.PointerSize + 4);
                string label = f.String is not null ? $"{desc} = \"{f.String}\"" : desc;
                EmitStringEntry(sb, indent, ids, label, $"0x{charsAddr:X16}", f.StringLength);
            }
            else
            {
                EmitLeaf(sb, indent, ids, desc, vartype: "8 Bytes",
                         showAsHex: true, address: f.Ptr!, offsets: null);
            }
            return;
        }

        switch (kind)
        {
            // ---- Scalar leaf (primitive / enum-as-int / bool) at field storage.
            case WalkedFieldKind.Leaf:
            {
                (bool hex, bool signed) = UnityTypeMap.CeDisplay(f.Type);
                string vartype = f.IsEnum ? "4 Bytes" : UnityTypeMap.Resolve(f.Type).CeTypeKeyword;
                EmitLeaf(sb, indent, ids, desc,
                         vartype: vartype,
                         showAsHex: f.IsEnum ? false : hex,
                         showAsSigned: f.IsEnum ? true : signed,
                         address: fieldAddr ?? (f.Ptr ?? "0x0"), offsets: null);
                return;
            }

            // ---- Pointer-to-class — deref'd object root, recurse via header.
            case WalkedFieldKind.PointerToClass:
            {
                if (!canDrill || string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
                {
                    EmitLeaf(sb, indent, ids, desc + "  -> " + (f.Ptr ?? "<null>"),
                             vartype: "8 Bytes", showAsHex: true,
                             address: f.Ptr ?? (fieldAddr ?? "0x0"), offsets: null);
                    return;
                }
                if (!visited.Add(f.Ptr!))
                {
                    EmitLeaf(sb, indent, ids, desc + "  (cycle elided)",
                             vartype: "8 Bytes", showAsHex: true,
                             address: f.Ptr!, offsets: null);
                    return;
                }
                OpenGroup(sb, indent, ids, $"{desc}  -> {f.Ptr}", f.Ptr!,
                          offsets: null, collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                await EmitChildFieldsByHeaderAsync(sb, indent + "  ", ids, f.Ptr!,
                                                   assemblyName, options, live,
                                                   visited, currentDepth + 1, ct)
                    .ConfigureAwait(false);
                CloseGroup(sb, indent);
                visited.Remove(f.Ptr!);
                return;
            }

            // ---- Inline value-type struct — fields at field storage (no deref).
            case WalkedFieldKind.ValueStruct:
            {
                if (!canDrill || string.IsNullOrEmpty(fieldAddr))
                {
                    EmitLeaf(sb, indent, ids, desc + "  (struct)",
                             vartype: "8 Bytes", showAsHex: true,
                             address: fieldAddr ?? "0x0", offsets: null);
                    return;
                }
                OpenGroup(sb, indent, ids, $"{desc}  (struct)", fieldAddr!,
                          offsets: null, collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                if (InstanceFieldClassifier.TryParseClassName(f.Type, out string vns, out string vname))
                {
                    string cycleKey = $"{fieldAddr}!{vns}.{vname}";
                    if (visited.Add(cycleKey))
                    {
                        await EmitChildFieldsByNameAsync(sb, indent + "  ", ids, fieldAddr!,
                                                         assemblyName, vns, vname, options,
                                                         live, visited, currentDepth + 1, ct)
                            .ConfigureAwait(false);
                        visited.Remove(cycleKey);
                    }
                }
                CloseGroup(sb, indent);
                return;
            }

            // ---- Collection (T[] / List<T>) — walkArray + per-slot children.
            case WalkedFieldKind.Collection:
                await EmitCollectionLiveAsync(sb, indent, ids, f, desc, assemblyName,
                                              options, live, visited, currentDepth, ct)
                    .ConfigureAwait(false);
                return;

            default:
                EmitLeaf(sb, indent, ids, desc,
                         vartype: "8 Bytes", showAsHex: true,
                         address: fieldAddr ?? (f.Ptr ?? "0x0"), offsets: null);
                return;
        }
    }

    /// <summary>walkInstance via the object header (resolves nested + generic
    /// classes from the runtime class pointer) and emit its fields.</summary>
    private static async Task EmitChildFieldsByHeaderAsync(
        StringBuilder sb, string indent, IdGen ids, string objAddr,
        string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct)
    {
        WalkInstanceResult res;
        try { res = await live.WalkInstanceFromHeaderAsync(objAddr, ct).ConfigureAwait(false); }
        catch { return; }
        if (res.Fields is not { Count: > 0 } fields) return;
        foreach (WalkedField cf in fields)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>walkInstance via an explicit class name at <paramref name="addr"/>
    /// (for inline value-type structs, which have no header to read).</summary>
    private static async Task EmitChildFieldsByNameAsync(
        StringBuilder sb, string indent, IdGen ids, string addr,
        string? assemblyName, string ns, string name, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited, int currentDepth,
        CancellationToken ct)
    {
        WalkInstanceResult res;
        try
        {
            res = await live.WalkInstanceAsync(addr, assemblyName ?? string.Empty,
                                               ns, name, ct).ConfigureAwait(false);
        }
        catch { return; }
        if (res.Fields is not { Count: > 0 } fields) return;
        foreach (WalkedField cf in fields)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Emit a Collection field as a CE group with one child per slot.
    /// Pointer slots drill into the element object via the header; string slots
    /// become CE String entries; value slots become typed leaves.</summary>
    private static async Task EmitCollectionLiveAsync(
        StringBuilder sb, string indent, IdGen ids, WalkedField f, string desc,
        string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct)
    {
        bool canDrill = currentDepth < options.DrilldownDepth;
        if (!canDrill || options.ArrayLimit <= 0
            || string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
        {
            EmitLeaf(sb, indent, ids, desc, vartype: "8 Bytes", showAsHex: true,
                     address: f.Ptr ?? (f.Address ?? "0x0"), offsets: null);
            return;
        }

        // List<T> stores its backing T[] at +0x10; raw arrays point straight at
        // the array object. walkArray wants the array object address.
        bool isList = f.Type is { Length: > 0 } t
                      && InstanceFieldClassifier.TryParseCollectionType(t, out _, out bool il) && il;
        ulong listObj = ParseHexLoose(f.Ptr!);
        ulong arrayAbs = listObj;
        if (isList)
        {
            try
            {
                ReadMemoryResult itemsMem = await live.ReadMemoryAsync(
                    $"0x{(listObj + 0x10):X16}", 8, ct).ConfigureAwait(false);
                arrayAbs = (string.IsNullOrEmpty(itemsMem.Error) && !string.IsNullOrEmpty(itemsMem.Hex))
                    ? ParseLeQword(itemsMem.Hex!) : 0;
            }
            catch { arrayAbs = 0; }
        }
        if (arrayAbs == 0)
        {
            EmitLeaf(sb, indent, ids, desc + "  <empty>", vartype: "8 Bytes",
                     showAsHex: true, address: f.Ptr!, offsets: null);
            return;
        }
        string arrayAddr = $"0x{arrayAbs:X16}";

        InstanceFieldClassifier.TryParseCollectionType(f.Type ?? string.Empty,
                                                       out string elementType, out _);
        if (string.IsNullOrEmpty(elementType)) elementType = "System.Object";
        (string ns, string name)? elemSpec = SplitElementClass(f.Type);

        WalkArrayResult? walk = null;
        try
        {
            walk = await live.WalkArrayAsync(
                arrayAddr, elementType: elementType, limit: options.ArrayLimit,
                elementSize: null,
                elementAssembly:  elemSpec is null ? null : (assemblyName ?? string.Empty),
                elementNamespace: elemSpec?.ns,
                elementName:      elemSpec?.name, ct).ConfigureAwait(false);
        }
        catch { /* tolerate */ }

        string headerLabel = walk is null
            ? $"{desc}  (walk failed)"
            : $"{desc}  [{walk.Length} of {(walk.ElementClassName ?? elementType)}, showing {walk.Count}]";
        OpenGroup(sb, indent, ids, headerLabel, arrayAddr,
                  offsets: null, collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        if (walk?.Elements is { } elems)
        {
            bool elemIsString = IsManagedString(elementType);
            foreach (WalkedArrayElement elem in elems)
            {
                string slotLabel = $"[{elem.Index}]";
                if (walk.IsPointer && elemIsString)
                {
                    if (string.IsNullOrEmpty(elem.Ptr) || elem.Ptr == "0x0000000000000000")
                    {
                        EmitLeaf(sb, indent + "  ", ids, slotLabel + "  <null>",
                                 vartype: "8 Bytes", showAsHex: true,
                                 address: elem.Address ?? "0x0", offsets: null);
                    }
                    else if (TryParseHexAddress(elem.Ptr, out ulong so))
                    {
                        ulong ca = so + (ulong)(2 * options.PointerSize + 4);
                        string lbl = elem.String is not null ? $"{slotLabel} = \"{elem.String}\"" : slotLabel;
                        EmitStringEntry(sb, indent + "  ", ids, lbl, $"0x{ca:X16}", elem.StringLength);
                    }
                    else
                    {
                        EmitLeaf(sb, indent + "  ", ids, slotLabel,
                                 vartype: "8 Bytes", showAsHex: true,
                                 address: elem.Ptr!, offsets: null);
                    }
                }
                else if (walk.IsPointer)
                {
                    ulong slotPtr = ParseHexLoose(elem.Ptr ?? "0");
                    bool slotDrill = currentDepth + 1 < options.DrilldownDepth;
                    if (slotPtr == 0 || !slotDrill || !visited.Add(elem.Ptr!))
                    {
                        EmitLeaf(sb, indent + "  ", ids, slotLabel,
                                 vartype: "8 Bytes", showAsHex: true,
                                 address: elem.Ptr ?? (elem.Address ?? "0x0"), offsets: null);
                        continue;
                    }
                    OpenGroup(sb, indent + "  ", ids, $"{slotLabel} @ {elem.Ptr}",
                              elem.Ptr!, offsets: null,
                              collapsed: options.CollapsePointerNodes,
                              varType: null, showAsHex: true);
                    await EmitChildFieldsByHeaderAsync(sb, indent + "    ", ids, elem.Ptr!,
                                                       assemblyName, options, live,
                                                       visited, currentDepth + 2, ct)
                        .ConfigureAwait(false);
                    CloseGroup(sb, indent + "  ");
                    visited.Remove(elem.Ptr!);
                }
                else
                {
                    (bool h, bool s) = UnityTypeMap.CeDisplay(elementType);
                    EmitLeaf(sb, indent + "  ", ids, slotLabel,
                             vartype: PrimitiveVartype(elementType),
                             showAsHex: h, showAsSigned: s,
                             address: elem.Address ?? "0x0", offsets: null);
                }
            }
        }
        CloseGroup(sb, indent);
    }

    /// <summary>True for a managed <c>System.String</c> field/element type.</summary>
    private static bool IsManagedString(string? type)
        => string.Equals(type, "System.String", StringComparison.Ordinal);

    /// <summary>Emit a CE "String" leaf pointing at an inline UTF-16 char
    /// buffer. Length is a generous byte cap from the decoded char length.</summary>
    private static void EmitStringEntry(
        StringBuilder sb, string indent, IdGen ids,
        string description, string address, int? charLength)
    {
        int bytes = charLength is int n && n > 0 ? (n + 1) * 2 : 128;
        sb.Append(indent).Append("<CheatEntry>\n");
        sb.Append(indent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(indent).Append("  <Description>\"").Append(XmlEscape(description)).Append("\"</Description>\n");
        sb.Append(indent).Append("  <ShowAsSigned>0</ShowAsSigned>\n");
        sb.Append(indent).Append("  <VariableType>String</VariableType>\n");
        sb.Append(indent).Append("  <Length>").Append(bytes.ToString(CultureInfo.InvariantCulture)).Append("</Length>\n");
        sb.Append(indent).Append("  <Unicode>1</Unicode>\n");
        sb.Append(indent).Append("  <CodePage>0</CodePage>\n");
        sb.Append(indent).Append("  <Address>").Append(XmlEscape(address)).Append("</Address>\n");
        sb.Append(indent).Append("</CheatEntry>\n");
    }
}
