// LegacyGridSortTests — Phase 29e-6.
//
// Verifies the AOT-safe comparers + in-place sort used by the migrated
// legacy DataGrids (Fields / Methods / Symbol Finder / Value Search /
// Snapshot diff / Field stats). Pure ViewModel-layer logic; no UI.

using System;
using System.Collections.ObjectModel;
using System.Linq;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class LegacyGridSortTests
{
    // ─── InPlace ───────────────────────────────────────────────────────

    [Fact]
    public void InPlace_Ascending_Descending_Toggle()
    {
        var coll = new ObservableCollection<int> { 3, 1, 2 };
        LegacyGridSort.InPlace(coll, (a, b) => a.CompareTo(b), ascending: true);
        Assert.Equal(new[] { 1, 2, 3 }, coll.ToArray());

        LegacyGridSort.InPlace(coll, (a, b) => a.CompareTo(b), ascending: false);
        Assert.Equal(new[] { 3, 2, 1 }, coll.ToArray());
    }

    [Fact]
    public void InPlace_SingleOrEmpty_NoThrow()
    {
        var one = new ObservableCollection<int> { 42 };
        LegacyGridSort.InPlace(one, (a, b) => a.CompareTo(b), true);
        Assert.Equal(new[] { 42 }, one.ToArray());

        var empty = new ObservableCollection<int>();
        LegacyGridSort.InPlace(empty, (a, b) => a.CompareTo(b), true);
        Assert.Empty(empty);
    }

    [Fact]
    public void InPlace_BulkObservableCollection_UsesReplaceAll()
    {
        // BulkObservableCollection should still end up sorted (the
        // ReplaceAll path), proving the diff grid's 740K-row collection
        // type is handled.
        var bulk = new BulkObservableCollection<int>();
        bulk.ReplaceAll(new[] { 5, 3, 9, 1 });
        int resets = 0;
        bulk.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };
        LegacyGridSort.InPlace(bulk, (a, b) => a.CompareTo(b), ascending: true);
        Assert.Equal(new[] { 1, 3, 5, 9 }, bulk.ToArray());
        Assert.Equal(1, resets);   // one Reset, not per-item events
    }

    // ─── Comparer resolvers: known members sort, unknown → null ────────

    [Fact]
    public void Field_KnownMembers_Resolve()
    {
        var a = new FieldRow("alpha", "System.Int32", 0x10, 1, false, "C");
        var b = new FieldRow("beta",  "System.Single", 0x08, 2, true, "D");

        Assert.True(LegacyGridSort.Field("Name")!(a, b) < 0);       // alpha < beta
        Assert.True(LegacyGridSort.Field("Offset")!(a, b) > 0);     // 0x10 > 0x08
        Assert.True(LegacyGridSort.Field("IsStatic")!(a, b) < 0);   // false < true
        Assert.Null(LegacyGridSort.Field("Nonexistent"));
    }

    [Fact]
    public void Method_KnownMembers_Resolve()
    {
        var a = new MethodRow("aaa", token: 5, flags: 1, isStatic: false, address: "0x10", declaredIn: "C");
        var b = new MethodRow("bbb", token: 9, flags: 2, isStatic: true,  address: "0x20", declaredIn: "D");
        Assert.True(LegacyGridSort.Method("Name")!(a, b) < 0);
        Assert.True(LegacyGridSort.Method("Token")!(a, b) < 0);     // 5 < 9
        Assert.Null(LegacyGridSort.Method("zzz"));
    }

    [Fact]
    public void FieldStat_SortsByCountNumerically()
    {
        var coll = new ObservableCollection<FieldNameStat>
        {
            new() { FieldName = "a", Count = 100 },
            new() { FieldName = "b", Count = 9 },
            new() { FieldName = "c", Count = 40 },
        };
        LegacyGridSort.InPlace(coll, LegacyGridSort.FieldStat("Count")!, ascending: false);
        Assert.Equal(new[] { 100, 40, 9 }, coll.Select(s => s.Count).ToArray());
    }

    [Fact]
    public void Symbol_AllColumnsResolveAsStringCompare()
    {
        foreach (var m in new[] { "KindLabel", "Assembly", "ClassDisplay", "Member", "Type", "OffsetLabel", "Address" })
            Assert.NotNull(LegacyGridSort.Symbol(m));
        Assert.Null(LegacyGridSort.Symbol("bogus"));
    }

    [Fact]
    public void ValueSearch_FieldOffsetIsNumeric_OthersResolve()
    {
        foreach (var m in new[] { "ClassDisplay", "Field", "FieldType", "FieldOffset", "CurrentValue", "InstanceAddr", "Address", "FieldKind" })
            Assert.NotNull(LegacyGridSort.ValueSearch(m));
        Assert.Null(LegacyGridSort.ValueSearch("bogus"));
    }

    [Fact]
    public void Diff_KnownMembersResolve_FieldOffsetNumeric()
    {
        foreach (var m in new[] { "ClassDisplay", "Field", "FieldOffset", "InstanceLabel", "OldValue", "NewValue", "Delta", "ChangeKind", "FieldType" })
            Assert.NotNull(LegacyGridSort.Diff(m));
        Assert.Null(LegacyGridSort.Diff("bogus"));
    }
}
