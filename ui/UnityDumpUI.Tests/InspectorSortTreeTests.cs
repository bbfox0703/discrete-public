// InspectorSortTreeTests — Phase 29e-8.
//
// The Inspector Fields grid is a tree (WalkedFieldNode parent/children,
// flattened into Fields with indent). Its column sort must reorder
// siblings WITHIN each parent and re-flatten in DFS order — never
// flatten globally, which would tear children away from their parent.
// These tests build a small tree by hand and assert SortTree keeps the
// structure intact while reordering.

using System;
using System.IO;
using System.Linq;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class InspectorSortTreeTests : IDisposable
{
    private readonly string _tempDir;

    public InspectorSortTreeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "InspectorSortTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private LiveInspectionViewModel BuildVm()
    {
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + Guid.NewGuid().ToString("N")));
        return new LiveInspectionViewModel(backend, logger);
    }

    private static WalkedFieldNode Node(string name, int offset, int indent,
        WalkedFieldKind kind = WalkedFieldKind.Leaf, string type = "System.Int32")
        => new(name, type, offset, declaredIn: "C", address: null,
               displayValue: name + "_val", ptr: null, indentLevel: indent,
               kind: kind, effectiveAddress: null, actions: null);

    /// <summary>Add a parent + its (pre-set) children to Fields in
    /// flattened DFS order, marking the parent expanded so the children
    /// are part of the visible tree.</summary>
    private static void AddExpandedParent(LiveInspectionViewModel vm,
        WalkedFieldNode parent, params WalkedFieldNode[] children)
    {
        parent.IsExpanded = true;
        parent.IsLoaded   = true;
        vm.Fields.Add(parent);
        foreach (var c in children)
        {
            c.Parent = parent;
            parent.Children.Add(c);
            vm.Fields.Add(c);   // flattened: child appears after parent
        }
    }

    // ─── Flat roots ────────────────────────────────────────────────────

    [Fact]
    public void SortTree_FlatRoots_SortsByName()
    {
        var vm = BuildVm();
        vm.Fields.Add(Node("charlie", 0x20, 0));
        vm.Fields.Add(Node("alpha",   0x10, 0));
        vm.Fields.Add(Node("bravo",   0x30, 0));

        vm.SortTree("Name", ascending: true);
        Assert.Equal(new[] { "alpha", "bravo", "charlie" },
            vm.FilteredFields.Select(n => n.Name).ToArray());

        vm.SortTree("Name", ascending: false);
        Assert.Equal(new[] { "charlie", "bravo", "alpha" },
            vm.FilteredFields.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SortTree_ByOffset_IsNumericNotLexicographic()
    {
        var vm = BuildVm();
        vm.Fields.Add(Node("a", 0x100, 0));
        vm.Fields.Add(Node("b", 0x80, 0));
        vm.Fields.Add(Node("c", 0x8, 0));

        vm.SortTree("Offset", ascending: true);
        // Numeric: 0x8 < 0x80 < 0x100 (lexicographic would give 0x100,0x8,0x80).
        Assert.Equal(new[] { "c", "b", "a" },
            vm.FilteredFields.Select(n => n.Name).ToArray());
    }

    // ─── Tree structure preserved ──────────────────────────────────────

    [Fact]
    public void SortTree_KeepsChildrenUnderTheirParent()
    {
        var vm = BuildVm();
        // Two expanded parents, each with children. After a Name sort the
        // children must still immediately follow their own parent.
        var pZ = Node("zeta", 0x10, 0, WalkedFieldKind.PointerToClass);
        var pA = Node("apex", 0x20, 0, WalkedFieldKind.PointerToClass);
        AddExpandedParent(vm, pZ, Node("z_b", 0x4, 1), Node("z_a", 0x8, 1));
        AddExpandedParent(vm, pA, Node("a_y", 0x4, 1), Node("a_x", 0x8, 1));

        vm.SortTree("Name", ascending: true);

        // apex sorts before zeta; each parent's children sort among
        // themselves and stay grouped under their parent.
        Assert.Equal(
            new[] { "apex", "a_x", "a_y", "zeta", "z_a", "z_b" },
            vm.FilteredFields.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SortTree_CollapsedParentChildrenNotFlattened_ButStaySorted()
    {
        var vm = BuildVm();
        var parent = Node("parent", 0x10, 0, WalkedFieldKind.PointerToClass);
        // Children exist + are loaded, but the parent is COLLAPSED — so
        // they are not part of Fields, only of parent.Children.
        parent.IsLoaded = true;
        var cB = Node("c_b", 0x4, 1); cB.Parent = parent; parent.Children.Add(cB);
        var cA = Node("c_a", 0x8, 1); cA.Parent = parent; parent.Children.Add(cA);
        vm.Fields.Add(parent);   // collapsed: children not in Fields

        vm.SortTree("Name", ascending: true);

        // Only the root shows (collapsed) …
        Assert.Equal(new[] { "parent" }, vm.FilteredFields.Select(n => n.Name).ToArray());
        // … but the hidden children were still sorted for the next expand.
        Assert.Equal(new[] { "c_a", "c_b" }, parent.Children.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SortTree_UnknownMember_NoOp()
    {
        var vm = BuildVm();
        vm.Fields.Add(Node("b", 0, 0));
        vm.Fields.Add(Node("a", 0, 0));
        vm.SortTree("Bogus", ascending: true);
        // Order unchanged (no comparer for unknown member).
        Assert.Equal(new[] { "b", "a" }, vm.FilteredFields.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SortTree_RespectsActiveFilter()
    {
        var vm = BuildVm();
        vm.Fields.Add(Node("hp_max", 0x10, 0));
        vm.Fields.Add(Node("mp_cur", 0x20, 0));
        vm.Fields.Add(Node("hp_cur", 0x30, 0));
        vm.Filter = "hp";   // only hp_* visible

        vm.SortTree("Name", ascending: true);
        Assert.Equal(new[] { "hp_cur", "hp_max" },
            vm.FilteredFields.Select(n => n.Name).ToArray());
    }
}
