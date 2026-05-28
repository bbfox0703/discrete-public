// DataGridSortComparers.cs — AOT-safe per-column sort comparers for
// Avalonia 12 DataGrid.
//
// Why this exists — three-layer trap defeated Avalonia's default sort under
// AOT trimming on this project:
//
//   1. With AvaloniaUseCompiledBindingsByDefault=true and a Window-level
//      `x:DataType` in scope, column Bindings compile to `CompiledBinding`
//      whose Path string is empty at runtime.
//   2. Without a readable Path, the DataGrid auto-sets each column's
//      CanUserSort=False (even though CanUserSortColumns=True on the grid).
//      Header clicks become silent no-ops — the Sorting event never fires.
//   3. Even if we restore CanUserSort by setting it explicitly + setting
//      SortMemberPath, Avalonia's default sort uses reflection on
//      SortMemberPath to read each row's property value. The AOT trimmer
//      drops that reflection path, so the comparer reads nothing and
//      "sorts" produce no reorder.
//
// The escape hatch is `DataGridColumn.CustomSortComparer` — when set, the
// DataGrid uses our IComparer directly and skips the reflection path.
// This file provides typed factories so each window's code-behind reads
// like a small dictionary mapping SortMemberPath → row-property selector.
//
// Per-row XAML stays minimal — each column needs:
//   * SortMemberPath="<PropertyName>"  — defeats the CompiledBinding-empty-
//     Path auto-disable (layer 2)
//   * CanUserSort="True"               — belt-and-braces against the same
//   * (CustomSortComparer wired here at Loaded time — layer 3)
//
// Cross-checked against sibling project D:\Github\CrimsonAtomtic which uses
// the same Avalonia 12 + Native AOT setup. CrimsonAtomtic doesn't need
// CustomSortComparer because its XAML scope shape produces a plain
// `Binding` (not `CompiledBinding`) for column Bindings, which keeps the
// default reflection path alive. The helper here is the strict-superset
// fix — works in both scope shapes.

using System;
using System.Collections;
using System.Globalization;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Helpers for wiring AOT-safe per-column sort on Avalonia 12 DataGrids.
/// </summary>
/// <remarks>
/// Pure factories — kept Avalonia-free so this lives in
/// UnityDumpUI.Core. The matching <c>DataGrid.WireSortComparers(...)</c>
/// extension method lives in the UI project where Avalonia.Controls is
/// referenced.
/// </remarks>
public static class DataGridSortComparers
{
    /// <summary>
    /// Hex-address sort comparer — parses "0x..." (or bare hex) to ulong and
    /// compares numerically so "0x100000000" doesn't sort before
    /// "0x80000000" the way an alphabetical compare would.
    /// </summary>
    public static IComparer Hex<TRow>(Func<TRow, string?> selector) where TRow : class
        => new HexAddressComparer<TRow>(selector);

    /// <summary>
    /// Ordinal string comparer — case-sensitive, culture-invariant. Best
    /// default for property values the user will visually compare letter by
    /// letter (assembly names, type names, etc.).
    /// </summary>
    public static IComparer Ordinal<TRow>(Func<TRow, string?> selector) where TRow : class
        => new OrdinalStringComparer<TRow>(selector);

    /// <summary>
    /// Numeric comparer for integer-typed properties (Int32 / UInt32 / etc.).
    /// </summary>
    public static IComparer Number<TRow>(Func<TRow, long> selector) where TRow : class
        => new NumberComparer<TRow>(selector);

    /// <summary>
    /// Numeric comparer for floating-point properties (CV, ratios, scores).
    /// </summary>
    public static IComparer Double<TRow>(Func<TRow, double> selector) where TRow : class
        => new DoubleComparer<TRow>(selector);

    /// <summary>
    /// Boolean comparer — false sorts before true (matches conventional
    /// "unchecked first" UX in spreadsheet apps).
    /// </summary>
    public static IComparer Bool<TRow>(Func<TRow, bool> selector) where TRow : class
        => new BoolComparer<TRow>(selector);

    // ---- Concrete comparers ------------------------------------------------

    private sealed class HexAddressComparer<TRow> : IComparer where TRow : class
    {
        private readonly Func<TRow, string?> _selector;
        public HexAddressComparer(Func<TRow, string?> selector) { _selector = selector; }

        public int Compare(object? x, object? y)
        {
            string? a = x is TRow ax ? _selector(ax) : null;
            string? b = y is TRow by ? _selector(by) : null;
            if (TryParseHex(a, out ulong ua) && TryParseHex(b, out ulong ub))
                return ua.CompareTo(ub);
            return string.Compare(a ?? string.Empty, b ?? string.Empty,
                                  StringComparison.Ordinal);
        }

        private static bool TryParseHex(string? s, out ulong value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            ReadOnlySpan<char> span = s.AsSpan();
            if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
                span = span.Slice(2);
            return ulong.TryParse(span, NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out value);
        }
    }

    private sealed class OrdinalStringComparer<TRow> : IComparer where TRow : class
    {
        private readonly Func<TRow, string?> _selector;
        public OrdinalStringComparer(Func<TRow, string?> selector) { _selector = selector; }
        public int Compare(object? x, object? y)
        {
            string a = (x is TRow ax ? _selector(ax) : null) ?? string.Empty;
            string b = (y is TRow by ? _selector(by) : null) ?? string.Empty;
            return string.Compare(a, b, StringComparison.Ordinal);
        }
    }

    private sealed class NumberComparer<TRow> : IComparer where TRow : class
    {
        private readonly Func<TRow, long> _selector;
        public NumberComparer(Func<TRow, long> selector) { _selector = selector; }
        public int Compare(object? x, object? y)
        {
            long a = x is TRow ax ? _selector(ax) : 0L;
            long b = y is TRow by ? _selector(by) : 0L;
            return a.CompareTo(b);
        }
    }

    private sealed class BoolComparer<TRow> : IComparer where TRow : class
    {
        private readonly Func<TRow, bool> _selector;
        public BoolComparer(Func<TRow, bool> selector) { _selector = selector; }
        public int Compare(object? x, object? y)
        {
            bool a = x is TRow ax && _selector(ax);
            bool b = y is TRow by && _selector(by);
            return a.CompareTo(b);
        }
    }

    private sealed class DoubleComparer<TRow> : IComparer where TRow : class
    {
        private readonly Func<TRow, double> _selector;
        public DoubleComparer(Func<TRow, double> selector) { _selector = selector; }
        public int Compare(object? x, object? y)
        {
            double a = x is TRow ax ? _selector(ax) : 0d;
            double b = y is TRow by ? _selector(by) : 0d;
            return a.CompareTo(b);
        }
    }
}
