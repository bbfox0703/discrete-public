// InspectorFieldOffsetResolverTests.cs — Phase 21a offline coverage.
//
// Pins the cumulative-offset resolver contract used by the "List this field
// across instances" right-click. The resolver walks the parent chain summing
// offsets across value-struct ancestors, bailing as soon as any non-
// ValueStruct ancestor (pointer, collection, array slot) appears — those
// would need a per-instance dereference or per-row index resolution, which
// the bulk listFieldValuesAcrossInstances RPC can't model.

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class InspectorFieldOffsetResolverTests
{
    private sealed class FakeNode : IInspectorFieldNode
    {
        public FakeNode(int offset, WalkedFieldKind kind, IInspectorFieldNode? parent = null)
        {
            Offset     = offset;
            Kind       = kind;
            ParentNode = parent;
        }
        public int                  Offset     { get; }
        public WalkedFieldKind      Kind       { get; }
        public IInspectorFieldNode? ParentNode { get; }
    }

    [Fact]
    public void Resolve_TopLevelLeaf_ReturnsItsOwnOffset()
    {
        // A primitive field at offset 0x20 with no parent — top-level row.
        // Cumulative offset is just the field's own offset.
        var node = new FakeNode(0x20, WalkedFieldKind.Leaf);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(node);

        Assert.True(r.Supported);
        Assert.Equal(0x20, r.Offset);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Resolve_NestedValueStructLeaf_SumsOffsetsAcrossChain()
    {
        // class A { Vector3 pos; }  pos at +0x18 in A
        // Vector3 { float x; float y; float z; }  y at +0x4 in Vector3
        // Cumulative offset of A.pos.y = 0x18 + 0x4 = 0x1C.
        var pos = new FakeNode(0x18, WalkedFieldKind.ValueStruct);
        var y   = new FakeNode(0x4,  WalkedFieldKind.Leaf, parent: pos);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(y);

        Assert.True(r.Supported);
        Assert.Equal(0x1C, r.Offset);
    }

    [Fact]
    public void Resolve_NestedValueStruct_DepthTwo_SumsAll()
    {
        // class Outer { Wrapper w; }                w at +0x10
        // struct Wrapper { Inner i; }               i at +0x8 in Wrapper
        // struct Inner { int counter; }             counter at +0x4 in Inner
        // Cumulative offset = 0x10 + 0x8 + 0x4 = 0x1C.
        var w       = new FakeNode(0x10, WalkedFieldKind.ValueStruct);
        var i       = new FakeNode(0x8,  WalkedFieldKind.ValueStruct, parent: w);
        var counter = new FakeNode(0x4,  WalkedFieldKind.Leaf,        parent: i);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(counter);

        Assert.True(r.Supported);
        Assert.Equal(0x1C, r.Offset);
    }

    [Fact]
    public void Resolve_ValueStructItself_AsSelectedRow_ReturnsItsOffset()
    {
        // The user can select the value-struct row itself (without expanding
        // it). Its own offset relative to instance base is the cumulative
        // answer; field type is e.g. UnityEngine.Vector3 and the DLL decoder
        // will fall through to the catch-all pointer-qword display, which is
        // still useful (8 bytes of struct data) — but more importantly the
        // resolver must NOT bail here.
        var pos = new FakeNode(0x18, WalkedFieldKind.ValueStruct);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(pos);

        Assert.True(r.Supported);
        Assert.Equal(0x18, r.Offset);
    }

    [Fact]
    public void Resolve_BailsOnPointerToClassAncestor()
    {
        // class A { Inventory inv; }   inv is a reference — kind = PointerToClass.
        // class Inventory { int chrNo; }
        // Selecting `chrNo` inside the expanded inv subtree must NOT compute a
        // cumulative offset from A's base — each A instance's inv points to a
        // different Inventory heap object.
        var inv   = new FakeNode(0x10, WalkedFieldKind.PointerToClass);
        var chrNo = new FakeNode(0x8,  WalkedFieldKind.Leaf, parent: inv);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(chrNo);

        Assert.False(r.Supported);
        Assert.NotNull(r.Reason);
        Assert.Contains("pointer", r.Reason!);
    }

    [Fact]
    public void Resolve_BailsOnCollectionAncestor()
    {
        // class A { List<int> ids; }  — ids is a Collection; per-row index
        // resolution makes the offset non-constant across the array.
        var ids = new FakeNode(0x10, WalkedFieldKind.Collection);
        var slot = new FakeNode(0x4, WalkedFieldKind.Leaf, parent: ids);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(slot);

        Assert.False(r.Supported);
        Assert.NotNull(r.Reason);
        Assert.Contains("collection", r.Reason!);
    }

    [Fact]
    public void Resolve_BailsOnArraySlotAncestor()
    {
        // Selecting a value-type field inside an expanded array slot has the
        // same "different offset per index" problem as a Collection ancestor.
        var slot  = new FakeNode(0x20, WalkedFieldKind.ArraySlotLeaf);
        var inner = new FakeNode(0x0,  WalkedFieldKind.Leaf, parent: slot);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(inner);

        Assert.False(r.Supported);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Resolve_BailsOnArraySlotPointerAncestor()
    {
        // Pointer slot inside an array — same "per index" problem AND a
        // per-row deref. Bails out.
        var slot = new FakeNode(0x20, WalkedFieldKind.ArraySlotPointer);
        var inner = new FakeNode(0x10, WalkedFieldKind.Leaf, parent: slot);

        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(inner);

        Assert.False(r.Supported);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Resolve_NullNode_ReturnsUnsupportedWithReason()
    {
        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(null);
        Assert.False(r.Supported);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Resolve_SyntheticOffsetNode_ReturnsUnsupported()
    {
        // Array-slot rows in WalkedFieldNode use offset = -1 (synthetic). Even
        // for the row itself with no parent, resolving must refuse — there's
        // no field offset to report.
        var node = new FakeNode(-1, WalkedFieldKind.Leaf);
        CumulativeOffsetResult r = InspectorFieldOffsetResolver.Resolve(node);
        Assert.False(r.Supported);
    }
}
