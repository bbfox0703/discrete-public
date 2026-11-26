// UnityTypeMap.cs — single source of truth for Unity-managed-type ⇒
// CE-Structure-Dissect / C-header type mapping.
//
// Mirror of docs/Architecture.md §"Export Formats" → §"CSX". Any edit here
// must also be reflected there. The fall-through rule for unmapped types is:
//   * value type (struct, primitive enum)  → treat as opaque bytes
//   * reference type (class / interface / delegate / array / List<T>)
//                                          → 8-byte pointer on x64
// We never invent generic-instantiation layouts at this layer; the caller
// can decide to recurse into a follow-up `getClassInfo` for richer output.

using System;

namespace UnityDumpUI.Core.Services.Export;

/// <summary>
/// Result of resolving a managed type for export. Both CE and C-style outputs
/// reuse this so the mapping table stays authoritative.
/// </summary>
public readonly record struct ManagedTypeShape(
    int     SizeBytes,        // x64 sizeof for value types; 8 for pointers
    bool    IsPointer,        // true ⇒ field stores an 8-byte qword pointer
    string  CeTypeKeyword,    // CE Structure Dissect type keyword
    string  CHeaderType);     // C/C++ header type keyword

public static class UnityTypeMap
{
    /// <summary>
    /// Resolve a Unity managed type name (the format `getClassInfo` returns,
    /// e.g. <c>"System.Single"</c>, <c>"UnityEngine.Vector3"</c>,
    /// <c>"System.Collections.Generic.List`1"</c>) into a structured shape.
    /// Unknown / blank inputs degrade to an opaque pointer of width
    /// <paramref name="pointerSize"/> (8 on x64, 4 on x86).
    /// </summary>
    public static ManagedTypeShape Resolve(string? unityType, int pointerSize = 8)
    {
        int ptr = pointerSize == 4 ? 4 : 8;
        if (string.IsNullOrWhiteSpace(unityType))
            return Pointer("void*", ptr);

        // Trim generic instantiation suffix `<...>` so List`1<int> matches List`1.
        int lt = unityType.IndexOf('<');
        string root = lt > 0 ? unityType.Substring(0, lt) : unityType;

        // Array suffix `[]` → pointer to managed array header.
        if (root.EndsWith("[]", StringComparison.Ordinal))
            return Pointer("void*  /* managed array */", ptr);

        return root switch
        {
            "System.Boolean"           => Value(1, "Byte",     "uint8_t"),
            "System.Byte"              => Value(1, "Byte",     "uint8_t"),
            "System.SByte"             => Value(1, "Byte",     "int8_t"),
            "System.Char"              => Value(2, "2 Bytes",  "uint16_t"),
            "System.Int16"             => Value(2, "2 Bytes",  "int16_t"),
            "System.UInt16"            => Value(2, "2 Bytes",  "uint16_t"),
            "System.Int32"             => Value(4, "4 Bytes",  "int32_t"),
            "System.UInt32"            => Value(4, "4 Bytes",  "uint32_t"),
            "System.Int64"             => Value(8, "8 Bytes",  "int64_t"),
            "System.UInt64"            => Value(8, "8 Bytes",  "uint64_t"),
            "System.Single"            => Value(4, "Float",    "float"),
            "System.Double"            => Value(8, "Double",   "double"),
            "System.IntPtr"            => Value(ptr, ptr == 4 ? "4 Bytes" : "8 Bytes", ptr == 4 ? "int32_t"  : "intptr_t"),
            "System.UIntPtr"           => Value(ptr, ptr == 4 ? "4 Bytes" : "8 Bytes", ptr == 4 ? "uint32_t" : "uintptr_t"),

            "UnityEngine.Vector2"      => Value(8,  "8 Bytes",  "Vector2"),
            "UnityEngine.Vector3"      => Value(12, "12 Bytes", "Vector3"),
            "UnityEngine.Vector4"      => Value(16, "16 Bytes", "Vector4"),
            "UnityEngine.Quaternion"   => Value(16, "16 Bytes", "Quaternion"),
            "UnityEngine.Color"        => Value(16, "16 Bytes", "Color"),
            "UnityEngine.Color32"      => Value(4,  "4 Bytes",  "Color32"),
            "UnityEngine.Rect"         => Value(16, "16 Bytes", "Rect"),
            "UnityEngine.Bounds"       => Value(24, "24 Bytes", "Bounds"),

            "System.String"            => Pointer("void* /* MonoString / Il2CppString */", ptr),
            "System.Collections.Generic.List`1"
                                       => Pointer("void* /* List<T> */", ptr),
            "System.Collections.Generic.Dictionary`2"
                                       => Pointer("void* /* Dictionary<K,V> */", ptr),
            "System.Collections.Generic.HashSet`1"
                                       => Pointer("void* /* HashSet<T> */", ptr),

            // Anything else is *probably* a class / interface / delegate (pointer-sized).
            // Custom value-type structs would need full layout introspection.
            _ => Pointer($"void* /* {root} */", ptr),
        };
    }

    private static ManagedTypeShape Value(int size, string ce, string c)
        => new(size, false, ce, c);
    private static ManagedTypeShape Pointer(string c, int pointerSize = 8)
        => new(pointerSize, true, "Pointer", c);

    // ---------- Drilldown helpers ----------------------------------------

    /// <summary>
    /// For a Unity-managed type string, extract the referenced "user" class
    /// (namespace + simple name) we should recurse into during drilldown
    /// export. Strips arrays (<c>T[]</c>), <c>List`1&lt;T&gt;</c>, and any
    /// generic instantiation (using the outer generic for non-list types).
    /// Returns null for primitives, framework types
    /// (System.* / UnityEngine.*), or anything we don't recognize.
    /// </summary>
    public static (string Namespace, string Name)? ExtractElementClass(string? unityType)
    {
        if (string.IsNullOrWhiteSpace(unityType)) return null;
        string t = unityType.Trim();

        // Array suffix: T[] -> T
        while (t.EndsWith("[]", System.StringComparison.Ordinal))
            t = t.Substring(0, t.Length - 2);

        // Generic: pull the inner argument when the outer is List`1, otherwise
        // recurse into the outer (which is the actual storage type).
        int lt = t.IndexOf('<');
        if (lt > 0 && t.EndsWith(">", System.StringComparison.Ordinal))
        {
            string outer = t.Substring(0, lt);
            string inner = t.Substring(lt + 1, t.Length - lt - 2).Trim();
            if (outer == "System.Collections.Generic.List`1" ||
                outer == "System.Collections.Generic.IList`1" ||
                outer == "System.Collections.Generic.IEnumerable`1" ||
                outer == "System.Collections.Generic.HashSet`1" ||
                outer == "System.Collections.Generic.IReadOnlyList`1")
            {
                return ExtractElementClass(inner);
            }
            // Custom generic container (e.g. MainGame.ClassAry<T>) — recurse on inner.
            return ExtractElementClass(inner);
        }

        // Skip framework / value types — UnityTypeMap.Resolve handles those inline.
        if (t.StartsWith("System.",      System.StringComparison.Ordinal)) return null;
        if (t.StartsWith("UnityEngine.", System.StringComparison.Ordinal)) return null;
        if (t.IndexOf('.') < 0)
        {
            // Top-level (no namespace) user type — fine to drill.
            return (string.Empty, t);
        }

        int dot = t.LastIndexOf('.');
        return (t.Substring(0, dot), t.Substring(dot + 1));
    }

    /// <summary>True when the type referred to is a managed reference (8-byte pointer).</summary>
    public static bool IsPointerType(string? unityType)
        => Resolve(unityType).IsPointer;

    /// <summary>
    /// CE display preferences for a leaf field of <paramref name="unityType"/>.
    /// Decides <c>&lt;ShowAsHex&gt;</c> + <c>&lt;ShowAsSigned&gt;</c> per CE
    /// convention so paste-into-CE renders the value correctly without manual
    /// fixup. Signed integers (Int16/32/64, SByte) get ShowAsSigned=1 and
    /// non-hex; unsigned integers and pointer-shaped opaque blobs ("4 Bytes"
    /// for an opaque qword/dword field) get hex; floats/bool/named-vector
    /// types fall through to plain decimal display.
    /// </summary>
    public static (bool ShowAsHex, bool ShowAsSigned) CeDisplay(string? unityType)
    {
        if (string.IsNullOrWhiteSpace(unityType)) return (true, false); // opaque pointer

        // Trim generic suffix and array suffix the same way Resolve does.
        int lt = unityType.IndexOf('<');
        string root = lt > 0 ? unityType.Substring(0, lt) : unityType;
        if (root.EndsWith("[]", StringComparison.Ordinal)) return (true, false);

        return root switch
        {
            "System.SByte"  or
            "System.Int16"  or
            "System.Int32"  or
            "System.Int64"  => (false, true),

            "System.Boolean" or
            "System.Byte"    or
            "System.Char"    or
            "System.UInt16"  or
            "System.UInt32"  or
            "System.UInt64"  => (false, false),

            "System.Single" or
            "System.Double" => (false, false),

            // Pointers and opaque structs (Vectors, Quaternion, Color, etc.):
            // CE shows their qword/byte payload more usefully in hex.
            _ => (true, false),
        };
    }

    /// <summary>True when the type is a collection-shaped pointer (T[], List`1, etc.).</summary>
    public static bool IsCollectionType(string? unityType)
    {
        if (string.IsNullOrWhiteSpace(unityType)) return false;
        if (unityType.EndsWith("[]", System.StringComparison.Ordinal)) return true;
        int lt = unityType.IndexOf('<');
        if (lt < 0) return false;
        // Strip the CLR arity marker so the IL-form "List`1" and the
        // demangled "List" (what the live runtime resolver hands us) both
        // match — otherwise live List<T> fields skip the array-walk branch
        // and get mis-exported as a pointer-to-element.
        string outer = StripArity(unityType.Substring(0, lt));
        return outer.EndsWith("List",          System.StringComparison.Ordinal)
            || outer.EndsWith("IList",         System.StringComparison.Ordinal)
            || outer.EndsWith("IEnumerable",   System.StringComparison.Ordinal)
            || outer.EndsWith("HashSet",       System.StringComparison.Ordinal)
            || outer.EndsWith("IReadOnlyList", System.StringComparison.Ordinal)
            || outer.EndsWith("ClassAry",      System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Strip a trailing CLR generic-arity marker (<c>`1</c>, <c>`2</c>, …) so
    /// the IL-form (<c>List`1</c>) and the demangled form (<c>List</c>)
    /// compare equal. Returns the input unchanged when there's no marker (or
    /// the backtick isn't followed only by digits).
    /// </summary>
    internal static string StripArity(string name)
    {
        int tick = name.LastIndexOf('`');
        if (tick < 0 || tick == name.Length - 1) return name;
        for (int i = tick + 1; i < name.Length; ++i)
            if (!char.IsDigit(name[i])) return name;
        return name.Substring(0, tick);
    }
}
