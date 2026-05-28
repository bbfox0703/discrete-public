// InstanceFieldClassifier.cs — kind-routing for the Inspector's hierarchical
// field grid. Extracted from LiveInspectionViewModel.cs (2026-05-08) so the
// classifier can be unit-tested offline without spinning up an Avalonia VM.
//
// The classifier maps a wire-protocol `WalkedField` (per pipe-protocol.md
// `walkInstance`) onto a `WalkedFieldKind` that the UI uses to decide:
//   * which row template (Leaf / expandable struct / pointer) to render,
//   * whether the row is expandable at all (Children load on demand for
//     PointerToClass / ValueStruct / Collection),
//   * what address to seed the next walkInstance / walkArray from
//     (`ComputeEffectiveAddress` on the VM side, kept there because it's
//     trivial and only used by the VM).
//
// Routing rules (precedence top→bottom):
//   1. Server-side `IsEnum` ⇒ Leaf, regardless of any other signal. Without
//      this short-circuit, the catch-all 8-byte-pointer decode + the
//      value-type heuristic below would route enums to ValueStruct ("expand"),
//      which fails because enums have no walkable inner fields.
//   2. Type matches `T[]` / `List<T>` ⇒ Collection (or Leaf if the array
//      pointer is null / empty — no point exposing an expand chevron).
//   3. Inline value-type struct (Vector3 / Quaternion / custom struct):
//      preferred signal is the new `IsValueType` flag, with a legacy fallback
//      via UnityTypeMap for older runtime resolvers that didn't bind the
//      type→class bridge.
//   4. Pointer-to-class: a non-null Ptr + a parsable class name.
//   5. Otherwise ⇒ Leaf.
//
// Mirror of pipe-protocol.md §"walkInstance fields[].kind" — keep the rule
// list above in sync with that doc when fields change.

using System;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Routing kind for a single row in the Inspector's hierarchical field grid.
/// </summary>
public enum WalkedFieldKind
{
    /// <summary>Primitive value or unrecognised pointer; no children.</summary>
    Leaf,

    /// <summary>
    /// Pointer to a managed class instance — expand via <c>walkInstance</c>
    /// on the dereferenced address.
    /// </summary>
    PointerToClass,

    /// <summary>
    /// Inline value-type struct (e.g. <c>Vector3</c>, custom struct) — its
    /// fields live at the parent address + offset (no header). Expand via
    /// <c>walkInstance</c> at the field's storage address; the DLL's
    /// <c>walkInstance</c> iterates fields by offset starting there, so it
    /// works for both class instances (with header) and value types
    /// (without).
    /// </summary>
    ValueStruct,

    /// <summary><c>T[]</c> / <c>List&lt;T&gt;</c> — expand via <c>walkArray</c>.</summary>
    Collection,

    /// <summary>Pointer slot inside an expanded array — recursively expandable like PointerToClass.</summary>
    ArraySlotPointer,

    /// <summary>Value-type slot inside an expanded array — primitive leaf for now.</summary>
    ArraySlotLeaf,
}

/// <summary>
/// Pure functions that classify a <see cref="WalkedField"/> into a
/// <see cref="WalkedFieldKind"/>. No I/O, no VM state; safe to call from
/// unit tests.
/// </summary>
public static class InstanceFieldClassifier
{
    /// <summary>
    /// Classify an instance-level <see cref="WalkedField"/> into a routing
    /// kind. <paramref name="isPointerValue"/> mirrors the legacy
    /// <c>FormatDisplay</c> output and is true when the field's wire payload
    /// is decoded as an 8-byte pointer (i.e. <see cref="WalkedField.Ptr"/>
    /// populated and the runtime didn't supply an inline primitive).
    /// </summary>
    public static WalkedFieldKind Classify(WalkedField f, bool isPointerValue)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));

        // Rule 1 — server-side enum classification trumps everything else.
        // Enums come back with the underlying integer already decoded into
        // Value, so the row is a Leaf showing that int.
        if (f.IsEnum) return WalkedFieldKind.Leaf;

        // Rule 2 — Collection. `T[]` / `List<T>` is distinguishable from
        // Type alone, no wire payload inspection needed.
        if (!string.IsNullOrEmpty(f.Type) &&
            TryParseCollectionType(f.Type, out _, out _))
        {
            // Empty pointer = empty array, no expansion possible.
            if (string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
                return WalkedFieldKind.Leaf;
            return WalkedFieldKind.Collection;
        }

        // A primitive that the DLL already decoded inline (`value` or
        // `string`) is a Leaf even when the runtime says IsValueType=true
        // (every primitive is technically a value type). Without this
        // short-circuit, server-side value-type flags would route Int32s /
        // floats to ValueStruct.
        bool primitiveDecoded = f.Value.HasValue || f.String is not null;

        // A System.* value type (IntPtr / UIntPtr, the numeric primitives,
        // Boolean, Char, Decimal, …) is a scalar with no walkable managed
        // fields. It must stay a Leaf even when its value wasn't inline-decoded
        // — e.g. IntPtr comes back as a Ptr (not Value/String), and a field at
        // a garbage address fails to decode entirely. Without this guard the
        // IsValueType signal alone routed those to ValueStruct, giving an
        // "(struct — expand)" chevron whose walkInstance then returns
        // "class not resolved". Mirrors the System.* exclusion TryParseClassName
        // already applies to the legacy path below.
        bool scalarSystemType = !string.IsNullOrEmpty(f.Type)
            && f.Type.StartsWith("System.", StringComparison.Ordinal);

        // Rule 3 — inline value-type struct. The field's storage IS the
        // struct — no pointer to deref — and walkInstance can iterate its
        // fields by offset starting at the field's own address.
        bool serverStruct = f.IsValueType && !primitiveDecoded && !scalarSystemType;
        bool legacyStruct = !primitiveDecoded
            && !string.IsNullOrEmpty(f.Type)
            && !UnityTypeMap.IsPointerType(f.Type)
            && TryParseClassName(f.Type, out _, out _);
        if ((serverStruct || legacyStruct) && !string.IsNullOrEmpty(f.Address))
        {
            return WalkedFieldKind.ValueStruct;
        }

        // Rule 4 — pointer-to-class.
        if (isPointerValue
            && !string.IsNullOrEmpty(f.Ptr)
            && f.Ptr != "0x0000000000000000"
            && TryParseClassName(f.Type, out _, out _))
        {
            return WalkedFieldKind.PointerToClass;
        }

        // Rule 5 — fallback.
        return WalkedFieldKind.Leaf;
    }

    /// <summary>
    /// Parses <c>T[]</c> and <c>System.Collections.Generic.List`1&lt;T&gt;</c>
    /// wrappers. Returns true on a match and fills <paramref name="elementType"/>
    /// (the inner <c>T</c>); <paramref name="isList"/> distinguishes List from
    /// raw array (caller may want to walk via <c>List._items.array</c> instead
    /// of the slot directly).
    /// </summary>
    public static bool TryParseCollectionType(string typeName, out string elementType, out bool isList)
    {
        isList = false;
        elementType = string.Empty;
        if (typeName is null) return false;
        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            elementType = typeName.Substring(0, typeName.Length - 2);
            return true;
        }
        const string listPrefix = "System.Collections.Generic.List`1<";
        if (typeName.StartsWith(listPrefix, StringComparison.Ordinal)
            && typeName.EndsWith(">", StringComparison.Ordinal))
        {
            elementType = typeName.Substring(listPrefix.Length, typeName.Length - listPrefix.Length - 1).Trim();
            isList = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Split a managed full-name into (namespace, name) for downstream
    /// <c>FindClass(ns, name)</c> calls. Returns false for primitives
    /// (<c>System.*</c>) and arrays (<c>T[]</c>) — those have no class to
    /// walk. Generic-instantiation suffixes are stripped before splitting at
    /// the dot, so <c>MainGame.ClassAry&lt;MainGame.CARRYITEM&gt;</c> resolves
    /// to <c>(MainGame, ClassAry)</c> rather than splitting at the dot inside
    /// the generic argument.
    /// </summary>
    public static bool TryParseClassName(string? type, out string ns, out string name)
    {
        ns = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(type)) return false;
        if (type.StartsWith("System.", StringComparison.Ordinal)) return false;
        if (type.EndsWith("[]", StringComparison.Ordinal)) return false;

        int lt = type.IndexOf('<');
        string outer = lt > 0 ? type.Substring(0, lt) : type;

        int dot = outer.LastIndexOf('.');
        if (dot < 0)
        {
            name = outer;
            return true;
        }
        ns = outer.Substring(0, dot);
        name = outer.Substring(dot + 1);
        return true;
    }
}
