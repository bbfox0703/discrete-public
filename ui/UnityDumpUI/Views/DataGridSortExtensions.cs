// DataGridSortExtensions.cs — UI-side glue for the AOT-safe DataGrid sort
// helpers defined in UnityDumpUI.Core.Services.DataGridSortComparers.
//
// The `WireSortComparers` extension lives here (not in Core) because it
// needs Avalonia.Controls.DataGrid. The pure-IComparer factories
// (`.Hex<T>` / `.Ordinal<T>` / `.Number<T>` / `.Bool<T>`) stay in Core.

using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;

namespace UnityDumpUI.Views;

internal static class DataGridSortExtensions
{
    /// <summary>
    /// Walk the grid's columns once and assign <c>CustomSortComparer</c>
    /// for each column whose <c>SortMemberPath</c> appears in
    /// <paramref name="comparers"/>. Idempotent — calling twice with the
    /// same dictionary is a no-op. Columns whose SortMemberPath isn't in
    /// the dictionary are left alone (so callers can wire only the
    /// columns that need a custom comparer).
    /// </summary>
    public static void WireSortComparers(this DataGrid grid,
        IReadOnlyDictionary<string, IComparer> comparers)
    {
        foreach (var col in grid.Columns)
        {
            string? path = col.SortMemberPath;
            if (string.IsNullOrEmpty(path)) continue;
            if (comparers.TryGetValue(path, out var cmp))
            {
                col.CustomSortComparer = cmp;
            }
        }
    }
}
