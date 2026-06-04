// CeXmlExporter.Live.cs — walkInstance-driven live CE XML emission.
//
// Addressing model (relative, UE5CEDumper-style) — 2026-05-30 rewrite:
//   The ENTIRE table carries exactly ONE absolute address: the root group's.
//   Every descendant is addressed RELATIVE to its parent's resolved address:
//     * scalar / enum / primitive  → <Address>+{offset}</Address>  (no Offsets)
//     * pointer field (class)      → group <Address>+{offset}</Address>
//                                     <Offsets><Offset>0</Offset></Offsets>
//                                     (CE dereferences *(parent+offset); the
//                                     children then offset from the target)
//     * inline value-type struct   → group <Address>+{offset}</Address>, NO
//                                     Offsets (struct lives inline at parent+off)
//     * System.String              → 2-level deref group: +{offset} Offsets=[0]
//                                     → _stringLength (4 Bytes @ +2*ptr) +
//                                       _firstChar (String @ +2*ptr+4)
//     * List<T> / T[]              → +{offset} Offsets=[0] (deref List/array obj);
//                                     List adds an inner _items group at +2*ptr
//                                     Offsets=[0] (→ inner T[]); elements offset
//                                     from the array data start (4*ptr + i*size).
//   Because nothing below the root is absolute, the user can swap the root
//   address for a CE symbol (e.g. an AOB-registered base) and the whole table
//   re-resolves for ANY instance of that class.
//
// Layout is still DISCOVERED live via walkInstance / walkInstanceFromHeader /
// walkArray (the same path the Inspector grid uses, so enums/strings/nested +
// generic classes all resolve), but only the RELATIVE offsets are emitted —
// the absolute addresses the walk returns are used solely to recurse, never
// written into the table.

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
    /// Emit one walkInstance-decoded field in live mode, addressed RELATIVE to
    /// its containing object (<c>+{f.Offset}</c>). Routes by the same
    /// <see cref="InstanceFieldClassifier"/> kind the Inspector uses. Cycle-
    /// detected on the concrete deref'd address; pointer/struct drilldown is
    /// bounded by <see cref="ExportOptions.DrilldownDepth"/>.
    /// </summary>
    private static async Task EmitWalkedFieldLiveAsync(
        StringBuilder sb, string indent, IdGen ids,
        WalkedField f, string? assemblyName, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited,
        int currentDepth, CancellationToken ct,
        string? addrOverride = null)
    {
        string desc = FieldDesc(f.Name, f.Type, options);
        // Normally a field is addressed RELATIVE to its parent (+offset). When
        // addrOverride is set (the "Copy CE field" standalone root), this node
        // uses that ABSOLUTE address instead so the entry stands on its own;
        // its drilldown children remain relative beneath it.
        string rel  = addrOverride ?? RelAddr(f.Offset);
        bool isPointerValue = !string.IsNullOrEmpty(f.Ptr)
                              && f.Ptr != "0x0000000000000000"
                              && !f.Value.HasValue && f.String is null;
        WalkedFieldKind kind = InstanceFieldClassifier.Classify(f, isPointerValue);
        bool canDrill = currentDepth < options.DrilldownDepth;
        // Never recurse into Unity DOTS / native-backed / pointer types. They
        // are native-memory internals (SystemState*, UnsafeList<T>*,
        // NativeQueue<T>, EntityQuery, BlobAssetReference, JobHandle …), not
        // gameplay state. Recursing fans the walk out across the entire ECS
        // system graph — a storm of walkInstance + native-pointer derefs on the
        // Mono worker thread that CTDs the game (Core Keeper) — and produces
        // useless CE XML. Emit them as a watchable pointer leaf instead. Has no
        // effect on non-DOTS games (no such types).
        bool engineInternal = IsEngineInternalType(f.Type);

        // ---- String — always deref to the inline UTF-16 buffer (terminal, so
        //      not gated by depth). Emits the canonical _stringLength/_firstChar
        //      pair so CE shows both the length and the text.
        if (kind == WalkedFieldKind.Leaf && IsManagedString(f.Type))
        {
            string slabel = options.VerboseDescription && f.String is not null
                ? $"{desc} = \"{f.String}\"" : desc;
            if (string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
            {
                // Null string handle — emit a plain pointer leaf so the slot is
                // still watchable, but no deref (would read garbage).
                EmitLeaf(sb, indent, ids, options.VerboseDescription ? slabel + "  <null>" : slabel,
                         vartype: PointerVartype(options), showAsHex: true, address: rel, offsets: null);
                return;
            }
            EmitStringDerefGroup(sb, indent, ids, slabel, rel, f.StringLength, options.PointerSize);
            return;
        }

        switch (kind)
        {
            // ---- Scalar leaf (primitive / enum-as-int / bool) at +offset.
            case WalkedFieldKind.Leaf:
            {
                (bool hex, bool signed) = UnityTypeMap.CeDisplay(f.Type);
                // IntPtr / UIntPtr are pointer-sized: UnityTypeMap hardcodes
                // them to "8 Bytes", but on x86 they're 4. Emit the arch-correct
                // width so m_CachedPtr et al. don't overlap the next field.
                bool isNativeInt = string.Equals(f.Type, "System.IntPtr", StringComparison.Ordinal)
                                || string.Equals(f.Type, "System.UIntPtr", StringComparison.Ordinal);
                string vartype = f.IsEnum     ? "4 Bytes"
                               : isNativeInt  ? PointerVartype(options)
                               :                UnityTypeMap.Resolve(f.Type).CeTypeKeyword;
                EmitLeaf(sb, indent, ids, desc,
                         vartype: vartype,
                         showAsHex: f.IsEnum ? false : hex,
                         showAsSigned: f.IsEnum ? true : signed,
                         address: rel, offsets: null);
                return;
            }

            // ---- Pointer-to-class — group +offset Offsets=[0]; recurse into
            //      the deref'd object via its header.
            case WalkedFieldKind.PointerToClass:
            {
                // Don't recurse into engine / framework REFERENCE objects
                // (UnityEngine.Transform → scene graph, GameObject → components,
                // UnityEvent → invocation lists, BCL internals). They explode
                // the export into hundreds of thousands of irrelevant rows and
                // aren't gameplay state — emit a watchable pointer leaf instead.
                // Game-namespace types (Assembly-CSharp / Pug.* / …) still
                // recurse; value structs (UnityEngine.Vector3) are unaffected
                // (different case → still expand to x/y/z).
                if (!canDrill || engineInternal || IsFrameworkReferenceType(f.Type)
                    || string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
                {
                    EmitLeaf(sb, indent, ids, desc, vartype: PointerVartype(options),
                             showAsHex: true, address: rel, offsets: null);
                    return;
                }
                if (!visited.Add(f.Ptr!))
                {
                    EmitLeaf(sb, indent, ids, options.VerboseDescription ? desc + "  (cycle elided)" : desc,
                             vartype: PointerVartype(options), showAsHex: true, address: rel, offsets: null);
                    return;
                }
                OpenGroup(sb, indent, ids, desc, rel,
                          offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                await EmitChildFieldsByHeaderAsync(sb, indent + "  ", ids, f.Ptr!,
                                                   assemblyName, options, live,
                                                   visited, currentDepth + 1, ct,
                                                   fallbackType: f.Type)
                    .ConfigureAwait(false);
                CloseGroup(sb, indent);
                visited.Remove(f.Ptr!);
                return;
            }

            // ---- Inline value-type struct — group +offset, NO Offsets (the
            //      struct storage IS at parent+offset). Walk it by name at its
            //      absolute storage address; children offset from the struct base.
            case WalkedFieldKind.ValueStruct:
            {
                // A framework value struct that ISN'T a small math/geometry/color
                // keeper (e.g. AsyncOperationHandle<T>, native handles) is opaque
                // engine bookkeeping — emit a leaf instead of fanning out its
                // internals (analysis: AsyncOperationHandle<Texture2D> alone was
                // ~20k rows / 1972 nodes). Game value structs + Vector3/Color/…
                // still expand.
                if (!canDrill || engineInternal || IsOpaqueFrameworkValueStruct(f.Type)
                    || string.IsNullOrEmpty(f.Address))
                {
                    EmitLeaf(sb, indent, ids, desc, vartype: PointerVartype(options),
                             showAsHex: true, address: rel, offsets: null);
                    return;
                }
                OpenGroup(sb, indent, ids, desc, rel,
                          offsets: null, collapsed: options.CollapsePointerNodes,
                          varType: null, showAsHex: true);
                if (InstanceFieldClassifier.TryParseClassName(f.Type, out string vns, out string vname))
                {
                    string cycleKey = $"{f.Address}!{vns}.{vname}";
                    if (visited.Add(cycleKey))
                    {
                        await EmitChildFieldsByNameAsync(sb, indent + "  ", ids, f.Address!,
                                                         assemblyName, vns, vname, options,
                                                         live, visited, currentDepth + 1, ct)
                            .ConfigureAwait(false);
                        visited.Remove(cycleKey);
                    }
                }
                CloseGroup(sb, indent);
                return;
            }

            // ---- Collection (T[] / List<T>) — relative deref chain + per-slot.
            case WalkedFieldKind.Collection:
                await EmitCollectionLiveAsync(sb, indent, ids, f, desc, rel, assemblyName,
                                              options, live, visited, currentDepth, ct)
                    .ConfigureAwait(false);
                return;

            // ---- Dictionary<K,V> — relative deref chain (dict object → entries
            //      array) with each key→value pair emitted at its computed slot.
            case WalkedFieldKind.Dictionary:
                await EmitDictionaryLiveAsync(sb, indent, ids, f, desc, rel, assemblyName,
                                              options, live, visited, currentDepth, ct)
                    .ConfigureAwait(false);
                return;

            default:
                EmitLeaf(sb, indent, ids, desc,
                         vartype: PointerVartype(options), showAsHex: true, address: rel, offsets: null);
                return;
        }
    }

    /// <summary>True for Unity DOTS / native-backed / pointer types we must
    /// NOT recurse into during live export — they're native-memory internals,
    /// not gameplay state, and walking them explodes the walk across the ECS
    /// graph (CTD risk on the Mono worker thread) for no useful output.
    /// Covers pointer types (e.g. <c>SystemState*</c>, <c>UnsafeList&lt;T&gt;*</c>)
    /// and the Unity.Entities / Unity.Collections / Unity.Jobs / Unity.Burst
    /// namespaces (Entity, EntityQuery, EntityArchetype, BlobAssetReference,
    /// NativeQueue, AllocatorHelper, JobHandle, …). Leaves UnityEngine /
    /// Unity.Mathematics math structs (Vector3, float3, Quaternion) walkable.</summary>
    private static bool IsEngineInternalType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (type.EndsWith("*", StringComparison.Ordinal)) return true;   // unmanaged pointer
        return type.StartsWith("Unity.Entities.", StringComparison.Ordinal)
            || type.StartsWith("Unity.Collections.", StringComparison.Ordinal)
            || type.StartsWith("Unity.Jobs.", StringComparison.Ordinal)
            || type.StartsWith("Unity.Burst.", StringComparison.Ordinal);
    }

    /// <summary>True for engine / framework REFERENCE types — emitted as a
    /// pointer leaf instead of recursed, to keep live export to gameplay data.
    /// Recursing into UnityEngine objects (Transform / GameObject / Component /
    /// MonoBehaviour / UnityEvent / Material …) walks the whole scene + engine
    /// graph; BCL reference internals (System.* dictionaries, delegates) are
    /// equally useless. Applied ONLY on the pointer-to-class path, so value
    /// structs (UnityEngine.Vector3, Unity.Mathematics.float3) still expand.
    /// Game types (no framework namespace prefix) are still recursed. The list
    /// is conservative-broad on purpose — a cheat table wants game state, not
    /// engine objects; a specific engine field can still be drilled manually
    /// from its emitted pointer address.</summary>
    private static bool IsFrameworkReferenceType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        return type.StartsWith("UnityEngine.", StringComparison.Ordinal)
            || type.StartsWith("Unity.", StringComparison.Ordinal)
            || type.StartsWith("System.", StringComparison.Ordinal)
            || type.StartsWith("TMPro.", StringComparison.Ordinal)
            || type.StartsWith("Mono.", StringComparison.Ordinal)
            || type.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    /// <summary>Framework VALUE structs we still expand — small math / geometry /
    /// color data a cheat table actually wants (x/y/z, rgba, …). Any OTHER
    /// framework value struct is opaque engine bookkeeping (e.g.
    /// <c>AsyncOperationHandle&lt;T&gt;</c>, native handles) and is leaf'd.</summary>
    private static readonly HashSet<string> s_expandableFrameworkValueStructs = new(StringComparer.Ordinal)
    {
        "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4",
        "UnityEngine.Vector2Int", "UnityEngine.Vector3Int",
        "UnityEngine.Quaternion", "UnityEngine.Color", "UnityEngine.Color32",
        "UnityEngine.Rect", "UnityEngine.RectInt", "UnityEngine.Bounds", "UnityEngine.BoundsInt",
        "UnityEngine.Matrix4x4", "UnityEngine.Ray", "UnityEngine.Ray2D", "UnityEngine.Plane",
        "Unity.Mathematics.float2", "Unity.Mathematics.float3", "Unity.Mathematics.float4",
        "Unity.Mathematics.int2", "Unity.Mathematics.int3", "Unity.Mathematics.int4",
        "Unity.Mathematics.quaternion",
        "Unity.Mathematics.float2x2", "Unity.Mathematics.float3x3", "Unity.Mathematics.float4x4",
    };

    /// <summary>True for a framework value struct we should NOT expand (leaf it).
    /// Game value structs (no framework prefix) always expand; the keep-set is
    /// the allowlist for framework ones.</summary>
    private static bool IsOpaqueFrameworkValueStruct(string? type)
        => IsFrameworkReferenceType(type) && !s_expandableFrameworkValueStructs.Contains(type!);

    /// <summary>walkInstance via the object header (resolves nested + generic
    /// classes from the runtime class pointer) and emit its fields. Each child
    /// is addressed relative to this object via its own <c>Offset</c>.
    ///
    /// The header path is IL2CPP-only — the Mono backend's walkInstance handler
    /// requires assembly+name and ignores the header address. So when the
    /// header walk yields nothing AND the caller knows the declared type
    /// (<paramref name="fallbackType"/>), retry with the name-based walk
    /// (mirrors the live Inspector's LoadPointerChildrenAsync fallback).
    /// Without this, pointer-to-class / array-element drilldown on Mono
    /// exported an empty group (活俠傳 StatConvertCollectionData[0] had no
    /// children).</summary>
    private static async Task EmitChildFieldsByHeaderAsync(
        StringBuilder sb, string indent, IdGen ids, string objAddr,
        string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct,
        string? fallbackType = null)
    {
        WalkInstanceResult? res = null;
        try
        {
            WalkInstanceResult attempt =
                await live.WalkInstanceFromHeaderAsync(objAddr, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(attempt.Error) && attempt.Fields is { Count: > 0 })
                res = attempt;
        }
        catch { /* fall through to name path */ }

        // Mono (or header-resolve miss): retry by declared type name across the
        // parent assembly + well-known fallbacks, same as the Inspector.
        if (res is null && InstanceFieldClassifier.TryParseClassName(fallbackType, out string ns, out string name))
        {
            bool isGeneric = fallbackType is { Length: > 0 } t && t.IndexOf('<') > 0;
            var nameVariants = new List<string> { name };
            if (isGeneric) { nameVariants.Add(name + "`1"); nameVariants.Add(name + "`2"); nameVariants.Add(name + "`3"); }
            foreach (string asm in BuildExportAssemblyCandidates(assemblyName, ns))
            {
                foreach (string nv in nameVariants)
                {
                    try
                    {
                        WalkInstanceResult attempt = await live
                            .WalkInstanceAsync(objAddr, asm, ns, nv, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(attempt.Error) && attempt.Fields is { Count: > 0 })
                        { res = attempt; break; }
                    }
                    catch { /* try next */ }
                }
                if (res is not null) break;
            }
        }

        if (res?.Fields is not { Count: > 0 } resolved) return;
        foreach (WalkedField cf in resolved)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Assembly search order for the name-based walk fallback —
    /// mirrors LiveInspectionViewModel.BuildAssemblyCandidates: the owning
    /// assembly first, then well-known Unity / System images by namespace.</summary>
    private static string[] BuildExportAssemblyCandidates(string? primaryAssembly, string ns)
    {
        var list = new List<string>(4);
        if (!string.IsNullOrEmpty(primaryAssembly)) list.Add(primaryAssembly!);
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal))
        {
            if (!list.Contains("UnityEngine.CoreModule")) list.Add("UnityEngine.CoreModule");
            if (!list.Contains("UnityEngine"))            list.Add("UnityEngine");
        }
        else if (ns.StartsWith("System", StringComparison.Ordinal))
        {
            if (!list.Contains("mscorlib"))       list.Add("mscorlib");
            if (!list.Contains("System.Runtime")) list.Add("System.Runtime");
        }
        if (list.Count == 0) list.Add(string.Empty);
        return list.ToArray();
    }

    /// <summary>walkInstance via an explicit class name at <paramref name="addr"/>
    /// (for inline value-type structs, which have no header to read).
    ///
    /// walkInstance searches ONLY the named image, so a struct that lives in a
    /// DIFFERENT assembly than its containing class — most importantly
    /// UnityEngine.Vector3 / Quaternion / Color (in UnityEngine.CoreModule) used
    /// as fields of an Assembly-CSharp class — won't resolve under the parent's
    /// assembly. Mirror the live Inspector's LoadPointerChildrenAsync: try the
    /// parent assembly first, then well-known fallbacks by namespace, and the
    /// generic-arity name variants. Without this `now_position` (a Vector3 field)
    /// exported as an empty group — i.e. didn't expand to x/y/z.</summary>
    private static async Task EmitChildFieldsByNameAsync(
        StringBuilder sb, string indent, IdGen ids, string addr,
        string? assemblyName, string ns, string name, ExportOptions options,
        ILiveExportSource live, HashSet<string> visited, int currentDepth,
        CancellationToken ct)
    {
        bool isGeneric = name.IndexOf('`') >= 0 || name.IndexOf('<') >= 0;
        var nameVariants = new List<string> { name };
        if (isGeneric) { nameVariants.Add(name + "`1"); nameVariants.Add(name + "`2"); nameVariants.Add(name + "`3"); }

        WalkInstanceResult? res = null;
        foreach (string asm in BuildExportAssemblyCandidates(assemblyName, ns))
        {
            foreach (string nv in nameVariants)
            {
                try
                {
                    WalkInstanceResult attempt = await live
                        .WalkInstanceAsync(addr, asm, ns, nv, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(attempt.Error) && attempt.Fields is { Count: > 0 })
                    { res = attempt; break; }
                }
                catch { /* try next */ }
            }
            if (res is not null) break;
        }
        if (res?.Fields is not { Count: > 0 } fields) return;
        foreach (WalkedField cf in fields)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Emit a Collection field as a relative-addressed CE group. The field
    /// pointer is dereferenced (Offsets=[0]); for List&lt;T&gt; an inner
    /// <c>_items</c> group dereferences the backing array; per-slot children
    /// offset from the array data start. Pointer slots drill into the element
    /// object via its header; string slots become String deref groups; value
    /// slots become typed leaves.
    /// </summary>
    /// <summary>
    /// Emit a <c>Dictionary&lt;K,V&gt;</c> as a relative deref chain. The DLL
    /// already decoded the live key→value pairs into <see cref="WalkedField.Fields"/>
    /// and supplied the chain offsets (entries-field offset, SZARRAY data start,
    /// Entry stride). We rebuild the CE addressing:
    /// <code>
    ///   dict field +rel               Offsets=[0]   (deref → dict object)
    ///     entries  +DictEntriesOffset  Offsets=[0]   (deref → Entry[] object)
    ///       [key] +(DictDataOffset + index*stride + valueOffset)   (per pair)
    /// </code>
    /// Each pair's VALUE is handed back to <see cref="EmitWalkedFieldLiveAsync"/>
    /// via <c>addrOverride</c>, so scalars / strings / pointer-to-class / nested
    /// dictionaries / value structs / byte[] all reuse the existing per-kind
    /// emit. Relative throughout, so swapping the root symbol re-targets the
    /// whole table (stable as long as the dict isn't rehashed).
    /// </summary>
    private static async Task EmitDictionaryLiveAsync(
        StringBuilder sb, string indent, IdGen ids, WalkedField f, string desc,
        string rel, string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct)
    {
        bool canDrill = currentDepth < options.DrilldownDepth;
        List<WalkedField>? pairs = f.Fields;
        // No pairs (empty / dict_error / depth cap) or missing chain metadata
        // (older DLL) → emit a watchable pointer leaf instead of a broken chain.
        if (!canDrill || pairs is not { Count: > 0 }
            || f.DictEntriesOffset <= 0 || f.DictStride <= 0)
        {
            EmitLeaf(sb, indent, ids, desc, vartype: PointerVartype(options),
                     showAsHex: true, address: rel, offsets: null);
            return;
        }

        string headerLabel = options.VerboseDescription
            ? $"{desc}  [dict, {f.DictCount} entries{(f.DictTruncated ? "+" : string.Empty)}]"
            : desc;

        // Group 1: deref the dict field pointer → dict object.
        OpenGroup(sb, indent, ids, headerLabel, rel,
                  offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        // Group 2: deref the `entries` field → Entry[] array object.
        string entriesIndent = indent + "  ";
        OpenGroup(sb, entriesIndent, ids, FieldDesc("entries", "Entry[]", options),
                  RelAddr(f.DictEntriesOffset),
                  offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        string pairIndent = entriesIndent + "  ";
        foreach (WalkedField pair in pairs)
        {
            int idx = pair.Index ?? 0;
            // Value address relative to the Entry[] object:
            //   data_offset + index*stride + value-member-offset (pair.Offset).
            string pairRel = RelAddr(f.DictDataOffset + idx * f.DictStride + pair.Offset);
            // +2 depth for the two derefs consumed above (matches the List path).
            await EmitWalkedFieldLiveAsync(sb, pairIndent, ids, pair, assemblyName,
                                           options, live, visited, currentDepth + 2, ct,
                                           addrOverride: pairRel)
                .ConfigureAwait(false);
        }

        CloseGroup(sb, entriesIndent);
        CloseGroup(sb, indent);
    }

    private static async Task EmitCollectionLiveAsync(
        StringBuilder sb, string indent, IdGen ids, WalkedField f, string desc,
        string rel, string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct)
    {
        bool canDrill = currentDepth < options.DrilldownDepth;
        if (!canDrill || options.ArrayLimit <= 0
            || string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
        {
            EmitLeaf(sb, indent, ids, desc, vartype: PointerVartype(options), showAsHex: true,
                     address: rel, offsets: null);
            return;
        }

        int ptr = options.PointerSize == 4 ? 4 : 8;
        int itemsOff = 2 * ptr;        // List<T>._items managed-field offset
        int dataOff  = 4 * ptr;        // SZARRAY data start (overridden below
                                       // from the walk's data_address when the
                                       // DLL reports it — exact, immune to a
                                       // mis-detected pointer size).

        bool isList = f.Type is { Length: > 0 } t
                      && InstanceFieldClassifier.TryParseCollectionType(t, out _, out bool il) && il;

        // Resolve the backing T[] object address (List<T> needs an extra deref
        // through _items; raw arrays point straight at the array object).
        ulong fieldPtr = ParseHexLoose(f.Ptr!);
        ulong arrayAbs = fieldPtr;
        if (isList)
        {
            try
            {
                ReadMemoryResult itemsMem = await live.ReadMemoryAsync(
                    $"0x{(fieldPtr + (ulong)itemsOff):X16}", 8, ct).ConfigureAwait(false);
                arrayAbs = (string.IsNullOrEmpty(itemsMem.Error) && !string.IsNullOrEmpty(itemsMem.Hex))
                    ? ParseLeQword(itemsMem.Hex!) : 0;
            }
            catch { arrayAbs = 0; }
        }
        if (arrayAbs == 0)
        {
            EmitLeaf(sb, indent, ids, options.VerboseDescription ? desc + "  <empty>" : desc,
                     vartype: PointerVartype(options), showAsHex: true, address: rel, offsets: null);
            return;
        }

        InstanceFieldClassifier.TryParseCollectionType(f.Type ?? string.Empty,
                                                       out string elementType, out _);
        if (string.IsNullOrEmpty(elementType)) elementType = "System.Object";
        (string ns, string name)? elemSpec = SplitElementClass(f.Type);

        WalkArrayResult? walk = null;
        try
        {
            walk = await live.WalkArrayAsync(
                $"0x{arrayAbs:X16}", elementType: elementType, limit: options.ArrayLimit,
                elementSize: null,
                elementAssembly:  elemSpec is null ? null : (assemblyName ?? string.Empty),
                elementNamespace: elemSpec?.ns,
                elementName:      elemSpec?.name, ct).ConfigureAwait(false);
        }
        catch { /* tolerate */ }

        // Prefer the DLL-reported data start (data_address − array object) for
        // the element base offset — it's the exact SZARRAY vector offset for
        // the target's arch, so x86 (data at +0x10) and x64 (+0x20) are both
        // correct without relying on options.PointerSize being right.
        if (walk?.DataAddress is { Length: > 0 } da
            && TryParseHexAddress(da, out ulong dataAbs) && dataAbs > arrayAbs)
        {
            ulong delta = dataAbs - arrayAbs;
            if (delta <= 0x40) dataOff = (int)delta;   // sanity-bound the header
        }

        string headerLabel = !options.VerboseDescription
            ? desc
            : walk is null
                ? $"{desc}  (walk failed)"
                : $"{desc}  [{walk.Length} of {(walk.ElementClassName ?? elementType)}, showing {walk.Count}]";

        // Outer group: deref the field pointer (List object or array object).
        OpenGroup(sb, indent, ids, headerLabel, rel,
                  offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                  varType: null, showAsHex: true);

        // For List<T>, add an inner _items group that derefs the backing T[];
        // element children then sit under it. For raw arrays the outer group
        // already points at the array object, so elements are direct children.
        string elemIndent = indent + "  ";
        bool innerGroup = isList;
        if (innerGroup)
        {
            OpenGroup(sb, elemIndent, ids, FieldDesc("_items", elementType + "[]", options), RelAddr(itemsOff),
                      offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                      varType: null, showAsHex: true);
            elemIndent += "  ";
        }

        if (walk?.Elements is { } elems)
        {
            int stride = walk.ElementSize > 0 ? walk.ElementSize : ptr;
            bool elemIsString = IsManagedString(elementType);
            bool slotDrill = (currentDepth + 1) < options.DrilldownDepth;
            // Element type is an engine/framework REFERENCE (UnityEngine.*,
            // Unity.* DOTS, System.* internals): do NOT recurse into each
            // element object — emit a watchable pointer leaf per slot instead.
            // This is the dominant Copy-CE-XML bloat source: List<SpriteRenderer>,
            // SpriteRenderer[], List<Texture2D/GameObject/Renderer/Transform> …
            // each fanned out full component subtrees per element (~100k+ rows in
            // the 706k-line sample). Collections of GAME types (List<EquipmentSlot>)
            // and value-struct elements (Color[], Vector3[]) are unaffected.
            bool elemIsFrameworkRef = IsEngineInternalType(elementType)
                                      || IsFrameworkReferenceType(elementType);
            foreach (WalkedArrayElement elem in elems)
            {
                string slotLabel = $"[{elem.Index}]";
                string slotRel = RelAddr(dataOff + elem.Index * stride);

                if (walk.IsPointer && elemIsString)
                {
                    if (string.IsNullOrEmpty(elem.Ptr) || elem.Ptr == "0x0000000000000000")
                    {
                        EmitLeaf(sb, elemIndent, ids, options.VerboseDescription ? slotLabel + "  <null>" : slotLabel,
                                 vartype: PointerVartype(options), showAsHex: true, address: slotRel, offsets: null);
                    }
                    else
                    {
                        string lbl = options.VerboseDescription && elem.String is not null
                            ? $"{slotLabel} = \"{elem.String}\"" : slotLabel;
                        EmitStringDerefGroup(sb, elemIndent, ids, lbl, slotRel, elem.StringLength, ptr);
                    }
                }
                else if (walk.IsPointer)
                {
                    ulong slotPtr = ParseHexLoose(elem.Ptr ?? "0");
                    if (slotPtr == 0 || !slotDrill || elemIsFrameworkRef || !visited.Add(elem.Ptr!))
                    {
                        EmitLeaf(sb, elemIndent, ids, slotLabel,
                                 vartype: PointerVartype(options), showAsHex: true, address: slotRel, offsets: null);
                        continue;
                    }
                    OpenGroup(sb, elemIndent, ids, slotLabel, slotRel,
                              offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                              varType: null, showAsHex: true);
                    await EmitChildFieldsByHeaderAsync(sb, elemIndent + "  ", ids, elem.Ptr!,
                                                       assemblyName, options, live,
                                                       visited, currentDepth + 2, ct,
                                                       fallbackType: elementType)
                        .ConfigureAwait(false);
                    CloseGroup(sb, elemIndent);
                    visited.Remove(elem.Ptr!);
                }
                else if (slotDrill && !string.IsNullOrEmpty(elem.Address)
                         && !IsEngineInternalType(elementType)
                         && !IsOpaqueFrameworkValueStruct(elementType)
                         && InstanceFieldClassifier.TryParseClassName(elementType, out string ens, out string ename))
                {
                    // Value-type STRUCT element (PlayerData[], Vector3[], a custom
                    // struct[]) — expand its members like an inline ValueStruct
                    // field, walked by name at the slot's absolute address.
                    // Primitive / enum elements fail TryParseClassName and fall
                    // through to the typed leaf below. (Without this, party
                    // (PlayerData[]) exported [0],[1],… with no fields.)
                    string ecycle = $"{elem.Address}!{ens}.{ename}";
                    if (visited.Add(ecycle))
                    {
                        OpenGroup(sb, elemIndent, ids, slotLabel, slotRel,
                                  offsets: null, collapsed: options.CollapsePointerNodes,
                                  varType: null, showAsHex: true);
                        await EmitChildFieldsByNameAsync(sb, elemIndent + "  ", ids, elem.Address!,
                                                         assemblyName, ens, ename, options, live,
                                                         visited, currentDepth + 2, ct)
                            .ConfigureAwait(false);
                        CloseGroup(sb, elemIndent);
                        visited.Remove(ecycle);
                    }
                    else
                    {
                        (bool h, bool s) = UnityTypeMap.CeDisplay(elementType);
                        EmitLeaf(sb, elemIndent, ids, slotLabel,
                                 vartype: PrimitiveVartype(elementType),
                                 showAsHex: h, showAsSigned: s, address: slotRel, offsets: null);
                    }
                }
                else
                {
                    (bool h, bool s) = UnityTypeMap.CeDisplay(elementType);
                    EmitLeaf(sb, elemIndent, ids, slotLabel,
                             vartype: PrimitiveVartype(elementType),
                             showAsHex: h, showAsSigned: s, address: slotRel, offsets: null);
                }
            }
        }

        if (innerGroup) CloseGroup(sb, indent + "  ");
        CloseGroup(sb, indent);
    }

    /// <summary>
    /// "Copy CE field" — render one or more selected Inspector field nodes, each
    /// as a STANDALONE subtree rooted at the field's own absolute storage
    /// address, with the same value decode + relative drilldown the in-place
    /// export uses (depth-bounded by <paramref name="options"/>). Multi-select
    /// produces one top-level entry per node under a single CheatTable. Unlike
    /// the old flat-leaf behaviour, a pointer / string / collection field
    /// expands properly here too.
    ///
    /// Each tuple is (field, absoluteAddress): the reconstructed
    /// <see cref="WalkedField"/> as the Inspector saw it, plus the absolute
    /// address its top entry should carry (the field's own storage for scalars
    /// / value structs / strings, or the deref'd object for array slots —
    /// whatever the caller computed as the node's effective root).
    /// </summary>
    /// <param name="rootAsOffset">When true, each node's TOP entry is addressed
    /// as a relative offset (<c>+{field.Offset:X}</c>) instead of its absolute
    /// address, so the table can be pasted directly UNDER the field's parent
    /// struct in CE and re-resolve there (e.g. a <c>member</c> field at +28 under
    /// its <c>team</c> parent). Children stay relative beneath it either way.</param>
    public static async Task<string> RenderFieldNodesLiveAsync(
        IReadOnlyList<(WalkedField field, string absoluteAddress)> nodes,
        string? assemblyName, ExportOptions options, ILiveExportSource live,
        CancellationToken ct = default, IProgress<int>? progress = null,
        bool rootAsOffset = false)
    {
        var sb = new StringBuilder(2048);
        var ids = new IdGen(progress);
        var nameMap = new ExportNameMap();
        sb.Append("<CheatTable>\n");
        sb.Append("  <CheatEntries>\n");

        // NOTE: do NOT probe pointer size by header-walking a node's root here.
        // Unlike the full RenderLiveAsync path (whose root IS an object header),
        // a "Copy CE field" root is the field's own STORAGE address — for a
        // pointer / List / array field that storage holds an object pointer, so
        // WalkInstanceFromHeader read it AS a klass and the DLL dereferenced a
        // managed-object pointer as a class → CTD (the haluCircuitList crash).
        // The caller's options.PointerSize already comes from the authoritative
        // per-process backend detection (GamePointerSize), so there is nothing to
        // correct — use it directly.

        foreach ((WalkedField field, string absoluteAddress) in nodes)
        {
            var visited = new HashSet<string>();
            string root = rootAsOffset ? RelAddr(field.Offset) : absoluteAddress;
            await EmitWalkedFieldLiveAsync(sb, "    ", ids, field, assemblyName,
                                           options, live, visited,
                                           currentDepth: 0, ct,
                                           addrOverride: root)
                .ConfigureAwait(false);
        }

        sb.Append("  </CheatEntries>\n");
        sb.Append("</CheatTable>\n");
        return BuildOutput(sb, nameMap);
    }

    // ---------- Relative-addressing helpers -------------------------------

    /// <summary>Format a managed field offset as a CE relative address
    /// (<c>+{hex}</c>). Negative offsets shouldn't occur for managed layouts;
    /// guarded to <c>+0</c> just in case.</summary>
    private static string RelAddr(int offset)
        => offset >= 0
            ? "+" + offset.ToString("X", CultureInfo.InvariantCulture)
            : "+0";

    /// <summary>True for a managed <c>System.String</c> field/element type.</summary>
    private static bool IsManagedString(string? type)
        => string.Equals(type, "System.String", StringComparison.Ordinal);

    /// <summary>
    /// Emit a managed-String value as a 2-level relative deref group, matching
    /// the canonical CE ".NET string dissect" shape:
    ///   group <Address>{rel}</Address> Offsets=[0]   (deref String object)
    ///     length (int32)  @ +2*ptr
    ///     chars  (String) @ +2*ptr+4   (Unicode, zero-terminated)
    /// <paramref name="rel"/> is the relative address of the String reference
    /// within its parent. Pointer size selects the header width (x64 → chars at
    /// +0x14, x86 → +0xC).
    ///
    /// The two child rows use NEUTRAL labels ("length (int32)" / "chars
    /// (UTF-16)") rather than concrete field names like <c>m_stringLength</c> /
    /// <c>_firstChar</c>: those are read from the String class's metadata and
    /// vary per game / runtime (m_length, m_firstChar, _stringLength, …). We
    /// don't resolve them here, so emitting a specific name would imply a
    /// metadata-sourced symbol we never read. The offsets are layout-fixed and
    /// correct regardless of what the game calls the fields.
    /// </summary>
    private static void EmitStringDerefGroup(
        StringBuilder sb, string indent, IdGen ids,
        string description, string rel, int? charLength, int pointerSize)
    {
        int ptr = pointerSize == 4 ? 4 : 8;
        int lenOff   = 2 * ptr;          // int32 length
        int charsOff = 2 * ptr + 4;      // first UTF-16 char
        int len = charLength is int n && n >= 0 ? n : 64;

        OpenGroup(sb, indent, ids, description, rel,
                  offsets: new[] { 0 }, collapsed: true,
                  varType: null, showAsHex: true);

        string childIndent = indent + "  ";
        EmitLeaf(sb, childIndent, ids, "length (int32)",
                 vartype: "4 Bytes", showAsHex: false, showAsSigned: false,
                 address: RelAddr(lenOff), offsets: null);

        // chars — CE String type. Length is in BYTES; UTF-16 → len*2, plus the
        // trailing NUL pair. Zero-terminate so CE stops at the NUL even when
        // the decoded length was unavailable (len fallback = 64).
        sb.Append(childIndent).Append("<CheatEntry>\n");
        sb.Append(childIndent).Append("  <ID>").Append(ids.Next()).Append("</ID>\n");
        sb.Append(childIndent).Append("  <Description>\"chars (UTF-16)\"</Description>\n");
        sb.Append(childIndent).Append("  <ShowAsSigned>0</ShowAsSigned>\n");
        sb.Append(childIndent).Append("  <VariableType>String</VariableType>\n");
        sb.Append(childIndent).Append("  <Length>").Append(((len + 1) * 2).ToString(CultureInfo.InvariantCulture)).Append("</Length>\n");
        sb.Append(childIndent).Append("  <Unicode>1</Unicode>\n");
        sb.Append(childIndent).Append("  <CodePage>0</CodePage>\n");
        sb.Append(childIndent).Append("  <ZeroTerminate>1</ZeroTerminate>\n");
        sb.Append(childIndent).Append("  <Address>").Append(RelAddr(charsOff)).Append("</Address>\n");
        sb.Append(childIndent).Append("</CheatEntry>\n");

        CloseGroup(sb, indent);
    }
}
