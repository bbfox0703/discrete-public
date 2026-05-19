// InspectorFieldOffsetResolver.cs — Phase 21a helper.
//
// Walks an Inspector tree node's parent chain and computes the cumulative
// byte offset of the field's storage from the outer instance base. Used by
// the "List this field across instances" right-click (see
// listFieldValuesAcrossInstances RPC).
//
// Why a helper: the offset stored on each `WalkedFieldNode` is *local*
// (relative to the address the DLL walked from). For a top-level node the
// local offset already IS the cumulative offset (the DLL walked from the
// instance base). For a nested ValueStruct child the DLL was called with
// addr = parent.EffectiveAddress (= parent's storage), so the child's offset
// is relative to the value-struct's start — we have to sum across the
// parent chain to recover the instance-base-relative offset.
//
// Scope (per the feature design): pointer-traversal and collection-element
// fields are NOT supported, because they'd need a per-instance dereference
// at lookup time. If any ancestor in the chain is a pointer- or array-kind
// row, the resolver returns `Supported = false` and a one-line reason — the
// UI surfaces this as a soft error rather than enabling the menu item.
//
// Pure function, no I/O. Lives in Core so the offline unit tests can drive
// it without spinning up an Avalonia VM. The `IInspectorFieldNode` interface
// keeps it decoupled from the concrete `WalkedFieldNode` in the UI project
// (UnityDumpUI.Core can't see UnityDumpUI types).

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Minimal contract a tree node needs to satisfy for the resolver. Mirrors
/// the subset of <c>WalkedFieldNode</c> that the resolver reads — keeps the
/// helper testable from Core without dragging in the UI VM type.
/// </summary>
public interface IInspectorFieldNode
{
    /// <summary>Field offset as the DLL reported it — local to the parent the DLL walked from.</summary>
    int Offset { get; }

    /// <summary>Routing kind. See <see cref="WalkedFieldKind"/>.</summary>
    WalkedFieldKind Kind { get; }

    /// <summary>Parent in the tree, or null at the root.</summary>
    IInspectorFieldNode? ParentNode { get; }
}

/// <summary>
/// Outcome of <see cref="InspectorFieldOffsetResolver.Resolve"/>.
/// </summary>
public readonly struct CumulativeOffsetResult
{
    public CumulativeOffsetResult(bool supported, long offset, string? reason)
    {
        Supported = supported;
        Offset    = offset;
        Reason    = reason;
    }

    /// <summary>True when the chain is value-typed top to bottom.</summary>
    public bool   Supported { get; }

    /// <summary>Cumulative byte offset from the outer instance base. Only meaningful when Supported.</summary>
    public long   Offset    { get; }

    /// <summary>Soft reason when unsupported; null on success.</summary>
    public string? Reason   { get; }

    public static CumulativeOffsetResult Ok(long offset)             => new(true, offset, null);
    public static CumulativeOffsetResult Unsupported(string reason)  => new(false, 0, reason);
}

public static class InspectorFieldOffsetResolver
{
    /// <summary>
    /// Compute the cumulative byte offset for <paramref name="node"/>. Walks
    /// the parent chain summing each ancestor's <see cref="IInspectorFieldNode.Offset"/>
    /// onto the node's own offset. Returns
    /// <see cref="CumulativeOffsetResult.Unsupported(string)"/> as soon as an
    /// ancestor of a non-<see cref="WalkedFieldKind.ValueStruct"/> kind is
    /// reached — the design explicitly does NOT support pointer-traversal
    /// (per-instance deref) or collection-element (per-index resolution).
    /// </summary>
    public static CumulativeOffsetResult Resolve(IInspectorFieldNode? node)
    {
        if (node is null)
            return CumulativeOffsetResult.Unsupported("no node selected");
        if (node.Offset < 0)
            return CumulativeOffsetResult.Unsupported("synthetic row (e.g. array slot) has no field offset");

        long cumulative = node.Offset;
        for (IInspectorFieldNode? p = node.ParentNode; p is not null; p = p.ParentNode)
        {
            switch (p.Kind)
            {
                case WalkedFieldKind.ValueStruct:
                    // OK — value struct's storage IS parent_base + parent_offset.
                    // Sum the parent's local offset and keep climbing.
                    if (p.Offset < 0)
                        return CumulativeOffsetResult.Unsupported(
                            "ancestor row has no field offset (synthetic)");
                    cumulative += p.Offset;
                    break;

                case WalkedFieldKind.PointerToClass:
                case WalkedFieldKind.ArraySlotPointer:
                    return CumulativeOffsetResult.Unsupported(
                        "reverse lookup not supported across a pointer-traversal field");

                case WalkedFieldKind.Collection:
                case WalkedFieldKind.ArraySlotLeaf:
                    return CumulativeOffsetResult.Unsupported(
                        "reverse lookup not supported inside collection elements");

                case WalkedFieldKind.Leaf:
                default:
                    // A Leaf can't have children, so we shouldn't actually
                    // see one as an ancestor — but if we do, treat it as an
                    // unknown route and bail out.
                    return CumulativeOffsetResult.Unsupported(
                        "ancestor kind is not a value struct");
            }
        }
        return CumulativeOffsetResult.Ok(cumulative);
    }
}
