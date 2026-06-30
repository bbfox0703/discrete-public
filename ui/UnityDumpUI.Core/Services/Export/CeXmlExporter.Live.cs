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
        string desc = FieldDesc(f.Name, f.Type, options, f.Offset);
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
                try
                {
                    // Collapse paths need the deref'd object's fields BEFORE deciding
                    // how to emit, so walk first when either is enabled. Otherwise fall
                    // straight through to the unchanged nested-group path.
                    if (options.CollapseLeafPointers || options.CollapseChain)
                    {
                        List<WalkedField>? kids = await WalkObjectByHeaderAsync(
                            f.Ptr!, assemblyName, options, live, f.Type, ct).ConfigureAwait(false);
                        if (kids is { Count: > 0 })
                        {
                            // Collapse pointer → single scalar leaf into one CE record:
                            // <Address>+ptrOff</Address> <Offsets>[fieldOff]</Offsets>.
                            if (options.CollapseLeafPointers && kids.Count == 1
                                && IsFlattenableScalarLive(kids[0]))
                            {
                                EmitCollapsedLeafPointer(sb, indent, ids, f, kids[0], options, rel);
                                return;
                            }
                            // Collapse a single-child pointer spine into one multi-level
                            // <Offsets> entry.
                            if (options.CollapseChain && kids.Count == 1 && IsFoldablePointerChild(kids[0]))
                            {
                                await EmitCollapsedChainAsync(sb, indent, ids, f, kids, rel,
                                                              assemblyName, options, live, visited,
                                                              currentDepth, ct).ConfigureAwait(false);
                                return;
                            }
                            // Didn't qualify — emit the normal nested group from the
                            // already-walked children (no second walk).
                            OpenGroup(sb, indent, ids, desc, rel,
                                      offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                                      varType: null, showAsHex: true);
                            foreach (WalkedField cf in kids)
                                await EmitWalkedFieldLiveAsync(sb, indent + "  ", ids, cf, assemblyName,
                                                               options, live, visited, currentDepth + 1, ct)
                                    .ConfigureAwait(false);
                            CloseGroup(sb, indent);
                            return;
                        }
                        // Walk failed → fall through to the default path (which retries
                        // and degrades to an empty group, same as before).
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
                }
                finally { visited.Remove(f.Ptr!); }
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

                // Flatten path: walk the struct first so we can decide whether all
                // its members are flatten-eligible. Only entered when a flatten flag
                // is on; otherwise the default nested-group path below runs unchanged.
                if ((options.FlattenValueStructs || options.FlattenLeafRecords)
                    && InstanceFieldClassifier.TryParseClassName(f.Type, out string fvns, out string fvname))
                {
                    string fcycle = $"{f.Address}!{fvns}.{fvname}";
                    if (visited.Add(fcycle))
                    {
                        try
                        {
                            List<WalkedField>? members = await WalkStructByNameAsync(
                                f.Address!, assemblyName, fvns, fvname, options, live, ct)
                                .ConfigureAwait(false);
                            if (members is { Count: > 0 })
                            {
                                bool flattenScalars = options.FlattenValueStructs
                                    && members.TrueForAll(IsFlattenableScalarLive);
                                bool flattenRecords = options.FlattenLeafRecords
                                    && members.TrueForAll(m => IsFlattenableLeafLive(m, allowStrings: true));
                                if (flattenScalars || flattenRecords)
                                {
                                    EmitFlattenedStructLive(sb, indent, ids, f, members, options, rel);
                                    return;   // finally removes fcycle
                                }
                                // Not flattenable — emit the normal nested group from the
                                // already-walked members (equivalent to EmitChildFieldsByName).
                                OpenGroup(sb, indent, ids, desc, rel,
                                          offsets: null, collapsed: options.CollapsePointerNodes,
                                          varType: null, showAsHex: true);
                                foreach (WalkedField cf in members)
                                    await EmitWalkedFieldLiveAsync(sb, indent + "  ", ids, cf, assemblyName,
                                                                   options, live, visited, currentDepth + 1, ct)
                                        .ConfigureAwait(false);
                                CloseGroup(sb, indent);
                                return;
                            }
                        }
                        catch { /* fall through to default path */ }
                        finally { visited.Remove(fcycle); }
                    }
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
        List<WalkedField>? resolved = await WalkObjectByHeaderAsync(
            objAddr, assemblyName, options, live, fallbackType, ct).ConfigureAwait(false);
        if (resolved is null) return;
        foreach (WalkedField cf in resolved)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Walk an object via its header (IL2CPP) with the Mono name-based
    /// fallback, returning its fields — or null when nothing resolves. The
    /// walk-only half of <see cref="EmitChildFieldsByHeaderAsync"/>, factored
    /// out so the collapse paths can inspect the deref'd object's members before
    /// choosing how to emit. Pure resolution: no emission, no cycle bookkeeping.</summary>
    private static async Task<List<WalkedField>?> WalkObjectByHeaderAsync(
        string objAddr, string? assemblyName, ExportOptions options, ILiveExportSource live,
        string? fallbackType, CancellationToken ct)
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

        return res?.Fields is { Count: > 0 } resolved ? resolved : null;
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
        List<WalkedField>? fields = await WalkStructByNameAsync(
            addr, assemblyName, ns, name, options, live, ct).ConfigureAwait(false);
        if (fields is null) return;
        foreach (WalkedField cf in fields)
            await EmitWalkedFieldLiveAsync(sb, indent, ids, cf, assemblyName,
                                           options, live, visited, currentDepth, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Walk an inline value-type struct by explicit class name across
    /// the assembly candidate list (the walk-only half of
    /// <see cref="EmitChildFieldsByNameAsync"/>). Returns its members or null.
    /// Used by the flatten path to inspect members before emitting.</summary>
    private static async Task<List<WalkedField>?> WalkStructByNameAsync(
        string addr, string? assemblyName, string ns, string name, ExportOptions options,
        ILiveExportSource live, CancellationToken ct)
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
        return res?.Fields is { Count: > 0 } fields ? fields : null;
    }

    // ---------- Flatten / collapse (UE5CEDumper-style) --------------------

    /// <summary>True for a struct member that is a primitive SCALAR leaf —
    /// safe to promote to a sibling leaf when flattening. Keys off the DLL's
    /// own inline decode: an enum (int) or a field the runtime decoded into
    /// <see cref="WalkedField.Value"/>. A member that is itself a value struct,
    /// pointer-to-class, collection, dictionary, or managed string is NOT a
    /// scalar leaf (it would lose structure if flattened), so it disqualifies
    /// its parent from FlattenValueStructs. This is what keeps a Vector3 that's
    /// a member of a larger struct nested (it flattens at its own level) rather
    /// than collapsed into one opaque leaf.</summary>
    private static bool IsFlattenableScalarLive(WalkedField c)
    {
        if (c is null) return false;
        if (c.IsEnum) return true;
        if (c.Dict || c.Fields is { Count: > 0 }) return false;
        if (IsManagedString(c.Type)) return false;
        if (!string.IsNullOrEmpty(c.Type)
            && InstanceFieldClassifier.TryParseCollectionType(c.Type!, out _, out _)) return false;
        // The DLL decoded an inline primitive value (Int32 / Single / Boolean …).
        // Value structs come back with no Value (just Address); pointers come
        // back as Ptr. So Value.HasValue is the precise "scalar primitive" gate.
        return c.Value.HasValue;
    }

    /// <summary>Superset gate for FlattenLeafRecords: a scalar leaf, OR (when
    /// <paramref name="allowStrings"/>) a managed String member (emitted as a
    /// 2-level deref leaf at the combined offset).</summary>
    private static bool IsFlattenableLeafLive(WalkedField c, bool allowStrings)
        => (allowStrings && IsManagedString(c.Type)) || IsFlattenableScalarLive(c);

    /// <summary>True when a deref'd object's single child is itself a drillable
    /// pointer-to-class (so a single-child spine can be folded). Excludes
    /// strings, value structs, collections, dictionaries, engine / framework
    /// reference types, and null pointers.</summary>
    private static bool IsFoldablePointerChild(WalkedField c)
    {
        if (c is null || string.IsNullOrEmpty(c.Ptr) || c.Ptr == "0x0000000000000000") return false;
        if (c.IsValueType || c.Dict || c.Fields is { Count: > 0 }) return false;
        if (IsManagedString(c.Type)) return false;
        if (IsEngineInternalType(c.Type) || IsFrameworkReferenceType(c.Type)) return false;
        if (!string.IsNullOrEmpty(c.Type)
            && InstanceFieldClassifier.TryParseCollectionType(c.Type!, out _, out _)) return false;
        return InstanceFieldClassifier.TryParseClassName(c.Type, out _, out _);
    }

    /// <summary>Description for a flattened struct member: <c>parent ▸ child</c>,
    /// with the child's type (verbose) and the COMBINED offset (DescShowOffset)
    /// appended.</summary>
    private static string FlattenSegLabel(WalkedField parent, WalkedField child,
                                          ExportOptions options, int combinedOffset)
    {
        string s = $"{parent.Name} ▸ {child.Name}";
        if (options.VerboseDescription && !string.IsNullOrEmpty(child.Type))
            s += $" : {child.Type}";
        if (options.DescShowOffset)
            s += $" (+{combinedOffset:X})";
        return s;
    }

    /// <summary>Emit a value struct as flattened sibling leaves at combined
    /// offsets (<c>parentOffset + memberOffset</c>), one CE entry per member.
    /// Scalars become typed leaves; enums signed-int leaves; (records mode)
    /// String members the canonical 2-level deref leaf.</summary>
    private static void EmitFlattenedStructLive(
        StringBuilder sb, string indent, IdGen ids,
        WalkedField parent, List<WalkedField> members, ExportOptions options, string baseRel)
    {
        foreach (WalkedField child in members)
        {
            // Address is the struct's own base (relative OR absolute) + the
            // member offset; the label shows the in-object combined offset.
            int combined = parent.Offset + child.Offset;
            string rel    = OffsetAddr(baseRel, child.Offset);
            string label  = FlattenSegLabel(parent, child, options, combined);

            if (IsManagedString(child.Type))
            {
                if (string.IsNullOrEmpty(child.Ptr) || child.Ptr == "0x0000000000000000")
                {
                    EmitLeaf(sb, indent, ids, options.VerboseDescription ? label + "  <null>" : label,
                             vartype: PointerVartype(options), showAsHex: true, address: rel, offsets: null);
                }
                else
                {
                    string slabel = options.VerboseDescription && child.String is not null
                        ? $"{label} = \"{child.String}\"" : label;
                    EmitStringDerefGroup(sb, indent, ids, slabel, rel, child.StringLength, options.PointerSize);
                }
            }
            else if (child.IsEnum)
            {
                EmitLeaf(sb, indent, ids, label, vartype: "4 Bytes",
                         showAsHex: false, showAsSigned: true, address: rel, offsets: null);
            }
            else
            {
                (bool hex, bool signed) = UnityTypeMap.CeDisplay(child.Type);
                EmitLeaf(sb, indent, ids, label,
                         vartype: UnityTypeMap.Resolve(child.Type).CeTypeKeyword,
                         showAsHex: hex, showAsSigned: signed, address: rel, offsets: null);
            }
        }
    }

    /// <summary>Emit a pointer-to-single-scalar as ONE collapsed CE record:
    /// <c>&lt;Address&gt;+ptrOff&lt;/Address&gt; &lt;Offsets&gt;[fieldOff]&lt;/Offsets&gt;</c>
    /// — CE dereferences the pointer at <c>parent + ptrOff</c> then reads the
    /// scalar at <c>+ fieldOff</c>, no group wrapper.</summary>
    private static void EmitCollapsedLeafPointer(
        StringBuilder sb, string indent, IdGen ids,
        WalkedField ptr, WalkedField child, ExportOptions options, string baseRel)
    {
        string label = $"{ptr.Name} ▸ {child.Name}";
        if (options.VerboseDescription && !string.IsNullOrEmpty(child.Type))
            label += $" : {child.Type}";
        if (options.DescShowOffset)
            label += $" (+{ptr.Offset:X}→+{child.Offset:X})";

        // The pointer field's own address (relative in-place, or the absolute
        // standalone-copy root) — CE dereferences it, then +child.Offset.
        string rel = baseRel;
        var offsets = new[] { child.Offset };
        if (child.IsEnum)
        {
            EmitLeaf(sb, indent, ids, label, vartype: "4 Bytes",
                     showAsHex: false, showAsSigned: true, address: rel, offsets: offsets);
        }
        else
        {
            (bool hex, bool signed) = UnityTypeMap.CeDisplay(child.Type);
            EmitLeaf(sb, indent, ids, label,
                     vartype: UnityTypeMap.Resolve(child.Type).CeTypeKeyword,
                     showAsHex: hex, showAsSigned: signed, address: rel, offsets: offsets);
        }
    }

    /// <summary>Fold a single-child pointer spine into ONE CE multi-level
    /// pointer entry. <paramref name="firstChildren"/> are the already-walked
    /// fields of the object the field points at. We keep descending while each
    /// object has exactly one foldable pointer child, accumulating the deref
    /// offsets, then emit a group whose <c>&lt;Offsets&gt;</c> chain resolves to
    /// the final object's base; its members are emitted relative beneath.
    ///
    /// CE offsets are applied innermost-last, so the chain is
    /// <c>[0, …reverse(chainOffsets)]</c>: the leading 0 is the final deref to
    /// the last object's base, then the intermediate field offsets in reverse
    /// depth order.</summary>
    private static async Task EmitCollapsedChainAsync(
        StringBuilder sb, string indent, IdGen ids,
        WalkedField f, List<WalkedField> firstChildren, string baseRel,
        string? assemblyName, ExportOptions options, ILiveExportSource live,
        HashSet<string> visited, int currentDepth, CancellationToken ct)
    {
        const int kMaxFold = 16;
        var chainOffsets = new List<int>();
        var labels       = new List<string> { LabelSeg(f, options) };
        var added        = new List<string>();   // intermediate ptrs added to visited
        List<WalkedField> curChildren = firstChildren;

        try
        {
            while (curChildren.Count == 1 && chainOffsets.Count < kMaxFold)
            {
                WalkedField only = curChildren[0];
                if (!IsFoldablePointerChild(only)) break;
                if (!visited.Add(only.Ptr!)) break;   // cycle — stop folding here
                List<WalkedField>? next = await WalkObjectByHeaderAsync(
                    only.Ptr!, assemblyName, options, live, only.Type, ct).ConfigureAwait(false);
                if (next is not { Count: > 0 })
                {
                    visited.Remove(only.Ptr!);   // couldn't descend — don't fold this hop
                    break;
                }
                added.Add(only.Ptr!);
                chainOffsets.Add(only.Offset);
                labels.Add(LabelSeg(only, options));
                curChildren = next;
            }

            // [0, …reverse(chainOffsets)]
            var offsets = new int[chainOffsets.Count + 1];
            offsets[0] = 0;
            for (int k = 0; k < chainOffsets.Count; k++)
                offsets[k + 1] = chainOffsets[chainOffsets.Count - 1 - k];

            OpenGroup(sb, indent, ids, string.Join(" ▸ ", labels), baseRel,
                      offsets: offsets, collapsed: options.CollapsePointerNodes,
                      varType: null, showAsHex: true);
            foreach (WalkedField cf in curChildren)
                await EmitWalkedFieldLiveAsync(sb, indent + "  ", ids, cf, assemblyName,
                                               options, live, visited, currentDepth + 1, ct)
                    .ConfigureAwait(false);
            CloseGroup(sb, indent);
        }
        finally
        {
            foreach (string p in added) visited.Remove(p);
        }
    }

    /// <summary>One segment label for a folded chain — bare field name plus an
    /// optional <c>(+offset)</c>. No type suffix on chain crumbs (matches the
    /// UE5 fold, which never decorates folded crumbs with the type).</summary>
    private static string LabelSeg(WalkedField f, ExportOptions options)
        => options.DescShowOffset ? $"{f.Name} (+{f.Offset:X})" : f.Name;

    /// <summary>Add a byte delta to a CE base address string, preserving its
    /// form: a relative <c>+{hex}</c> stays relative (<c>+{hex+delta}</c>); an
    /// absolute <c>0x…</c> stays absolute. Used to address a flattened struct
    /// member relative to its struct's base, which is itself either relative
    /// (in-place export) or absolute (a standalone "Copy CE field" root /
    /// dictionary value).</summary>
    private static string OffsetAddr(string baseAddr, int delta)
    {
        if (delta == 0) return baseAddr;
        if (baseAddr.StartsWith("+", StringComparison.Ordinal)
            && int.TryParse(baseAddr.AsSpan(1), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out int relBase))
            return "+" + (relBase + delta).ToString("X", CultureInfo.InvariantCulture);
        if (baseAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(baseAddr.AsSpan(2), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out ulong absBase))
            return "0x" + (absBase + (ulong)(long)delta).ToString("X16", CultureInfo.InvariantCulture);
        return baseAddr;   // unrecognised form — leave as-is
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
                // Alternating record colour by element index (null when the
                // feature is off → no <Color>, CE keeps its theme).
                string? elemColor = RecordColorFor(options, elem.Index);

                if (walk.IsPointer && elemIsString)
                {
                    if (string.IsNullOrEmpty(elem.Ptr) || elem.Ptr == "0x0000000000000000")
                    {
                        EmitLeaf(sb, elemIndent, ids, options.VerboseDescription ? slotLabel + "  <null>" : slotLabel,
                                 vartype: PointerVartype(options), showAsHex: true, address: slotRel, offsets: null,
                                 color: elemColor);
                    }
                    else
                    {
                        string lbl = options.VerboseDescription && elem.String is not null
                            ? $"{slotLabel} = \"{elem.String}\"" : slotLabel;
                        EmitStringDerefGroup(sb, elemIndent, ids, lbl, slotRel, elem.StringLength, ptr, color: elemColor);
                    }
                }
                else if (walk.IsPointer)
                {
                    ulong slotPtr = ParseHexLoose(elem.Ptr ?? "0");
                    if (slotPtr == 0 || !slotDrill || elemIsFrameworkRef || !visited.Add(elem.Ptr!))
                    {
                        EmitLeaf(sb, elemIndent, ids, slotLabel,
                                 vartype: PointerVartype(options), showAsHex: true, address: slotRel, offsets: null,
                                 color: elemColor);
                        continue;
                    }
                    OpenGroup(sb, elemIndent, ids, slotLabel, slotRel,
                              offsets: new[] { 0 }, collapsed: options.CollapsePointerNodes,
                              varType: null, showAsHex: true, color: elemColor);
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
                                  varType: null, showAsHex: true, color: elemColor);
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
                                 showAsHex: h, showAsSigned: s, address: slotRel, offsets: null,
                                 color: elemColor);
                    }
                }
                else
                {
                    (bool h, bool s) = UnityTypeMap.CeDisplay(elementType);
                    EmitLeaf(sb, elemIndent, ids, slotLabel,
                             vartype: PrimitiveVartype(elementType),
                             showAsHex: h, showAsSigned: s, address: slotRel, offsets: null,
                             color: elemColor);
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
        string description, string rel, int? charLength, int pointerSize,
        string? color = null)
    {
        int ptr = pointerSize == 4 ? 4 : 8;
        int lenOff   = 2 * ptr;          // int32 length
        int charsOff = 2 * ptr + 4;      // first UTF-16 char
        int len = charLength is int n && n >= 0 ? n : 64;

        OpenGroup(sb, indent, ids, description, rel,
                  offsets: new[] { 0 }, collapsed: true,
                  varType: null, showAsHex: true, color: color);

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
