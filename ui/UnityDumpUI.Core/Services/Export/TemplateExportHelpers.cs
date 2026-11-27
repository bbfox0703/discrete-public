// TemplateExportHelpers.cs — shared metadata classification and layout math
// for template-mode CE XML (CeXmlExporter) and CE Structure Dissect (CsxExporter).
//
// Keeps enum underlying-type resolution, inline ValueType boxed-header offset
// normalization, sequential struct stride alignment, and contiguous vs non-array
// collection classification identical across both exporters.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Core.Services.Export;

internal static class TemplateExportHelpers
{
    /// <summary>True when a resolved class is a managed VALUE type — its base is
    /// <c>System.ValueType</c> (struct) or <c>System.Enum</c> (enum).</summary>
    public static bool IsValueTypeClass(GetClassInfoResult info)
        => info.Parent is { Namespace: "System", Name: "ValueType" or "Enum" };

    /// <summary>True when a resolved class is an enum (base <c>System.Enum</c>).</summary>
    public static bool IsEnumClass(GetClassInfoResult info)
        => info.Parent is { Namespace: "System", Name: "Enum" };

    /// <summary>Resolve an enum's underlying integer type from its <c>value__</c>
    /// instance field (or first non-static primitive field). Falls back to
    /// <c>System.Int32</c> when metadata omits it.</summary>
    public static string ResolveEnumUnderlyingType(GetClassInfoResult enumClass)
    {
        if (enumClass.Fields is { Count: > 0 } fields)
        {
            FieldEntry? firstNonStatic = null;
            foreach (FieldEntry f in fields)
            {
                if (f.Static) continue;
                firstNonStatic ??= f;
                if (string.Equals(f.Name, "value__", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(f.Type)
                    && !UnityTypeMap.Resolve(f.Type).IsPointer)
                {
                    return f.Type!;
                }
            }
            if (firstNonStatic is not null
                && !string.IsNullOrEmpty(firstNonStatic.Type)
                && !UnityTypeMap.Resolve(firstNonStatic.Type).IsPointer)
            {
                return firstNonStatic.Type!;
            }
        }
        return "System.Int32";
    }

    /// <summary>True for pointer-sized native integers (<c>System.IntPtr</c> /
    /// <c>System.UIntPtr</c>) whose width tracks <see cref="ExportOptions.PointerSize"/>.</summary>
    public static bool IsNativeIntType(string? type)
        => type is "System.IntPtr" or "System.UIntPtr";

    /// <summary>Normalize pointer size to 4 (x86) or 8 (x64).</summary>
    public static int NormalizePointerSize(int pointerSize)
        => pointerSize == 4 ? 4 : 8;

    /// <summary>CE VariableType for a pointer-sized slot ("4 Bytes" on x86, "8 Bytes" on x64).</summary>
    public static string PointerVartype(int ptr)
        => ptr == 4 ? "4 Bytes" : "8 Bytes";

    /// <summary>True for generic collection / container types that do NOT share
    /// a contiguous SZARRAY / <c>List._items</c> layout (<c>HashSet</c>,
    /// <c>Dictionary</c>, <c>IList</c>, <c>IEnumerable</c>, <c>IReadOnlyList</c>,
    /// <c>Queue</c>, <c>Stack</c>, etc.). Exporters must not fabricate flat array
    /// slots or treat the container pointer as pointing directly to an element.</summary>
    public static bool IsNonArrayCollectionType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        if (InstanceFieldClassifier.TryParseCollectionType(type, out _, out _))
            return false;
        if (UnityTypeMap.IsCollectionType(type))
            return true;

        int lt = type.IndexOf('<');
        string outer = UnityTypeMap.StripArity(lt > 0 ? type.Substring(0, lt) : type);
        return outer is "System.Collections.Generic.Dictionary"
                     or "System.Collections.Generic.IDictionary"
                     or "System.Collections.Generic.IReadOnlyDictionary"
                     or "System.Collections.Generic.Queue"
                     or "System.Collections.Generic.Stack"
                     or "System.Collections.Generic.LinkedList"
                     or "System.Collections.Generic.SortedSet"
                     or "System.Collections.Generic.SortedDictionary"
                     or "System.Collections.Generic.SortedList"
                     or "System.Collections.Generic.HashSet"
                     or "System.Collections.Generic.IList"
                     or "System.Collections.Generic.IEnumerable"
                     or "System.Collections.Generic.IReadOnlyList"
                     or "System.Collections.Generic.ICollection"
                     or "System.Collections.Generic.IReadOnlyCollection";
    }

    /// <summary>True for engine / BCL namespaces whose reference classes should
    /// not be fanned out during template drilldown.</summary>
    public static bool IsFrameworkOrEngineNamespace(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (type.EndsWith("*", StringComparison.Ordinal)) return true;
        return type.StartsWith("UnityEngine.", StringComparison.Ordinal)
            || type.StartsWith("Unity.", StringComparison.Ordinal)
            || type.StartsWith("System.", StringComparison.Ordinal)
            || type.StartsWith("TMPro.", StringComparison.Ordinal)
            || type.StartsWith("Mono.", StringComparison.Ordinal)
            || type.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    /// <summary>Mono and IL2CPP <c>getClassInfo</c> report ValueType instance field
    /// offsets header-inclusive (boxed convention: starting at <c>2 * ptr</c>, i.e.
    /// <c>0x10</c> on x64 / <c>0x08</c> on x86). When the struct is stored inline
    /// (inside a parent class or a <c>T[]</c> array), there is no object header.
    /// Subtract <c>2 * ptr</c> when all non-static field offsets sit at or above
    /// <c>2 * ptr</c>; return 0 if offsets are already 0-based.</summary>
    public static int ComputeValueTypeHeaderAdjust(GetClassInfoResult info, int ptr)
    {
        if (info.Fields is not { Count: > 0 } fields) return 0;
        int minOff = int.MaxValue;
        foreach (FieldEntry f in fields)
        {
            if (f.Static || f.Offset < 0) continue;
            if (f.Offset < minOff) minOff = f.Offset;
        }
        int objHeader = 2 * ptr;
        return (minOff != int.MaxValue && minOff >= objHeader) ? objHeader : 0;
    }

    /// <summary>Compute the inline byte stride of a <c>System.ValueType</c> struct
    /// (header stripped, padded to natural field alignment capped at <paramref name="ptr"/>)
    /// for contiguous <c>T[]</c> / <c>List&lt;T&gt;</c> element addressing.</summary>
    public static async Task<int> ComputeInlineStructSizeAsync(
        GetClassInfoResult info,
        string? assemblyName,
        int ptr,
        IClassInfoLookup? lookup,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (info.Fields is not { Count: > 0 } fields) return 0;
        int headerAdj = ComputeValueTypeHeaderAdjust(info, ptr);
        int maxEnd = 0;
        int maxAlign = 1;

        foreach (FieldEntry f in fields)
        {
            if (f.Static || f.Offset < 0) continue;
            int relOff = Math.Max(0, f.Offset - headerAdj);
            (int fSize, int fAlign) = await MeasureFieldAsync(
                f.Type, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
            if (fAlign > maxAlign) maxAlign = fAlign;
            int end = relOff + fSize;
            if (end > maxEnd) maxEnd = end;
        }

        if (maxEnd <= 0) return 0;
        int align = Math.Min(Math.Max(maxAlign, 1), ptr);
        return (maxEnd + align - 1) / align * align;
    }

    private static async Task<(int Size, int Align)> MeasureFieldAsync(
        string? fieldType,
        string? assemblyName,
        int ptr,
        IClassInfoLookup? lookup,
        HashSet<string> visited,
        CancellationToken ct)
    {
        ManagedTypeShape shape = UnityTypeMap.Resolve(fieldType);
        if (!shape.IsPointer)
        {
            if (IsNativeIntType(fieldType))
                return (ptr, ptr);
            int sz = shape.SizeBytes;
            int al = sz switch
            {
                <= 1 => 1,
                2    => 2,
                4    => 4,
                8    => Math.Min(8, ptr),
                _    => 4, // Vector3 (12), Vector4/Quaternion/Color/Rect (16), Bounds (24) are float-aligned
            };
            return (sz, al);
        }

        if (lookup is not null
            && !string.IsNullOrEmpty(fieldType)
            && !InstanceFieldClassifier.TryParseCollectionType(fieldType, out _, out _)
            && !IsNonArrayCollectionType(fieldType)
            && !IsFrameworkOrEngineNamespace(fieldType)
            && InstanceFieldClassifier.TryParseClassName(fieldType, out string cns, out string cname))
        {
            string key = $"{assemblyName ?? "?"}!{cns}.{cname}";
            if (visited.Add(key))
            {
                try
                {
                    GetClassInfoResult? sub = null;
                    try
                    {
                        sub = await lookup.GetClassInfoAsync(
                            assemblyName ?? string.Empty, cns, cname, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        sub = null;
                    }

                    if (sub is not null && string.IsNullOrEmpty(sub.Error))
                    {
                        if (IsEnumClass(sub))
                        {
                            string u = ResolveEnumUnderlyingType(sub);
                            int esz = UnityTypeMap.Resolve(u).SizeBytes;
                            return (esz, Math.Min(esz, ptr));
                        }
                        if (IsValueTypeClass(sub))
                        {
                            int ssz = await ComputeInlineStructSizeAsync(
                                sub, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
                            if (ssz > 0) return (ssz, Math.Min(ssz, ptr));
                        }
                    }
                }
                finally
                {
                    visited.Remove(key);
                }
            }
        }

        return (ptr, ptr);
    }

    /// <summary>Safe <see cref="IClassInfoLookup.GetClassInfoAsync"/> wrapper that
    /// tolerates non-cancellation failures and error payloads.</summary>
    public static async Task<GetClassInfoResult?> TryLookupClassAsync(
        IClassInfoLookup lookup,
        string? assemblyName,
        string ns,
        string name,
        CancellationToken ct)
    {
        GetClassInfoResult? result;
        try
        {
            result = await lookup.GetClassInfoAsync(
                assemblyName ?? string.Empty, ns, name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        return (result is not null && string.IsNullOrEmpty(result.Error)) ? result : null;
    }
}
