// LegacyGridSort — Phase 29e-6.
//
// AOT-safe sort for the pre-29e DataGrids. Avalonia's built-in header
// sort resolves DataGridColumn.SortMemberPath via reflection
// (ClrPropertyInfo — IL2026/IL3050), which silently no-ops under Native
// AOT. The Pivot grids (29e-5) fixed this with ViewModel-side typed
// comparers; this file does the same for the six legacy grids that were
// still on the broken built-in path:
//
//   Fields tab     (FieldRow)         — Browser.FilteredFields
//   Methods tab    (MethodRow)        — Browser.FilteredMethods
//   Symbol Finder  (SymbolFinderRow)  — SymbolFinder.Matches
//   Value Search   (ValueSearchRow)   — ValueSearch.Results
//   Snapshot diff  (SnapshotDiffRow)  — Snapshot.FilteredDiffRows (Bulk)
//   Field stats    (FieldNameStat)    — Snapshot.FieldNameStats
//
// The comparers live here (one testable place, no reflection) and the
// MainWindow code-behind Sorting handlers call them. Each grid sorts the
// collection the grid is bound to, in place — for the filtered views
// (FilteredFields / FilteredMethods / FilteredDiffRows) a later re-filter
// rebuilds in source order, so sort is a transient view operation, which
// matches user expectation.
//
// The Inspector Fields grid is deliberately NOT here — its rows form an
// expand/collapse tree whose order is load-bearing, so reordering the
// backing collection would corrupt it. It keeps width-only treatment.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace UnityDumpUI.ViewModels;

public static class LegacyGridSort
{
    /// <summary>Sort an observable collection in place with a typed
    /// comparison. <see cref="BulkObservableCollection{T}"/> (the diff
    /// grid, up to ~740K rows) is replaced via a single Reset event;
    /// everything else uses Clear + re-add.</summary>
    public static void InPlace<T>(ObservableCollection<T> coll, Comparison<T> cmp, bool ascending)
    {
        if (coll.Count < 2) return;
        var list = new List<T>(coll);
        list.Sort(ascending ? cmp : (a, b) => cmp(b, a));
        if (coll is BulkObservableCollection<T> bulk)
        {
            bulk.ReplaceAll(list);
            return;
        }
        coll.Clear();
        foreach (var x in list) coll.Add(x);
    }

    private static int Str(string? a, string? b)
        => string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);

    public static Comparison<FieldRow>? Field(string member) => member switch
    {
        "Name"       => (a, b) => Str(a.Name, b.Name),
        "Offset"     => (a, b) => a.Offset.CompareTo(b.Offset),
        "Type"       => (a, b) => Str(a.Type, b.Type),
        "Flags"      => (a, b) => a.Flags.CompareTo(b.Flags),
        "IsStatic"   => (a, b) => a.IsStatic.CompareTo(b.IsStatic),
        "DeclaredIn" => (a, b) => Str(a.DeclaredIn, b.DeclaredIn),
        _ => null,
    };

    public static Comparison<MethodRow>? Method(string member) => member switch
    {
        "Name"       => (a, b) => Str(a.Name, b.Name),
        "Address"    => (a, b) => Str(a.Address, b.Address),
        "Token"      => (a, b) => a.Token.CompareTo(b.Token),
        "Flags"      => (a, b) => a.Flags.CompareTo(b.Flags),
        "IsStatic"   => (a, b) => a.IsStatic.CompareTo(b.IsStatic),
        "DeclaredIn" => (a, b) => Str(a.DeclaredIn, b.DeclaredIn),
        _ => null,
    };

    public static Comparison<SymbolFinderRow>? Symbol(string member) => member switch
    {
        "KindLabel"    => (a, b) => Str(a.KindLabel, b.KindLabel),
        "Assembly"     => (a, b) => Str(a.Assembly, b.Assembly),
        "ClassDisplay" => (a, b) => Str(a.ClassDisplay, b.ClassDisplay),
        "Member"       => (a, b) => Str(a.Member, b.Member),
        "Type"         => (a, b) => Str(a.Type, b.Type),
        "OffsetLabel"  => (a, b) => Str(a.OffsetLabel, b.OffsetLabel),
        "Address"      => (a, b) => Str(a.Address, b.Address),
        _ => null,
    };

    public static Comparison<ValueSearchRow>? ValueSearch(string member) => member switch
    {
        "ClassDisplay" => (a, b) => Str(a.ClassDisplay, b.ClassDisplay),
        "Field"        => (a, b) => Str(a.Field, b.Field),
        "FieldType"    => (a, b) => Str(a.FieldType, b.FieldType),
        "FieldOffset"  => (a, b) => a.FieldOffset.CompareTo(b.FieldOffset),
        "CurrentValue" => (a, b) => Str(a.CurrentValue, b.CurrentValue),
        "InstanceAddr" => (a, b) => Str(a.InstanceAddr, b.InstanceAddr),
        "Address"      => (a, b) => Str(a.Address, b.Address),
        "FieldKind"    => (a, b) => Str(a.FieldKind, b.FieldKind),
        _ => null,
    };

    public static Comparison<SnapshotDiffRow>? Diff(string member) => member switch
    {
        "ClassDisplay"  => (a, b) => Str(a.ClassDisplay, b.ClassDisplay),
        "Field"         => (a, b) => Str(a.Field, b.Field),
        "FieldOffset"   => (a, b) => a.FieldOffset.CompareTo(b.FieldOffset),
        "InstanceLabel" => (a, b) => Str(a.InstanceLabel, b.InstanceLabel),
        "OldValue"      => (a, b) => Str(a.OldValue, b.OldValue),
        "NewValue"      => (a, b) => Str(a.NewValue, b.NewValue),
        "Delta"         => (a, b) => Str(a.Delta, b.Delta),
        "ChangeKind"    => (a, b) => Str(a.ChangeKind, b.ChangeKind),
        "FieldType"     => (a, b) => Str(a.FieldType, b.FieldType),
        _ => null,
    };

    public static Comparison<FieldNameStat>? FieldStat(string member) => member switch
    {
        "FieldName" => (a, b) => Str(a.FieldName, b.FieldName),
        "Count"     => (a, b) => a.Count.CompareTo(b.Count),
        _ => null,
    };
}
