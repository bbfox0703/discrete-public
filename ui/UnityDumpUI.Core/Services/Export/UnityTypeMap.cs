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
    /// Unknown / blank inputs degrade to an opaque 8-byte pointer.
    /// </summary>
    public static ManagedTypeShape Resolve(string? unityType)
    {
        if (string.IsNullOrWhiteSpace(unityType))
            return Pointer("void*");

        // Trim generic instantiation suffix `<...>` so List`1<int> matches List`1.
        int lt = unityType.IndexOf('<');
        string root = lt > 0 ? unityType.Substring(0, lt) : unityType;

        // Array suffix `[]` → pointer to managed array header.
        if (root.EndsWith("[]", StringComparison.Ordinal))
            return Pointer("void*  /* managed array */");

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
            "System.IntPtr"            => Value(8, "8 Bytes",  "intptr_t"),
            "System.UIntPtr"           => Value(8, "8 Bytes",  "uintptr_t"),

            "UnityEngine.Vector2"      => Value(8,  "8 Bytes",  "Vector2"),
            "UnityEngine.Vector3"      => Value(12, "12 Bytes", "Vector3"),
            "UnityEngine.Vector4"      => Value(16, "16 Bytes", "Vector4"),
            "UnityEngine.Quaternion"   => Value(16, "16 Bytes", "Quaternion"),
            "UnityEngine.Color"        => Value(16, "16 Bytes", "Color"),
            "UnityEngine.Color32"      => Value(4,  "4 Bytes",  "Color32"),
            "UnityEngine.Rect"         => Value(16, "16 Bytes", "Rect"),
            "UnityEngine.Bounds"       => Value(24, "24 Bytes", "Bounds"),

            "System.String"            => Pointer("void* /* MonoString / Il2CppString */"),
            "System.Collections.Generic.List`1"
                                       => Pointer("void* /* List<T> */"),
            "System.Collections.Generic.Dictionary`2"
                                       => Pointer("void* /* Dictionary<K,V> */"),
            "System.Collections.Generic.HashSet`1"
                                       => Pointer("void* /* HashSet<T> */"),

            // Anything else is *probably* a class / interface / delegate (8 byte pointer).
            // Custom value-type structs would need full layout introspection (Phase 10
            // follow-up). For now we still emit a pointer-shaped entry but tag the type
            // name so the user can fix it up in CE / their header.
            _ => Pointer($"void* /* {root} */"),
        };
    }

    private static ManagedTypeShape Value(int size, string ce, string c)
        => new(size, false, ce, c);
    private static ManagedTypeShape Pointer(string c)
        => new(8, true, "Pointer", c);
}
