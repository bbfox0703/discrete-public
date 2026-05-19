// FieldAcrossInstancesWindow.axaml.cs — Phase 21a modal dialog code-behind.
//
// Hooks the VM's clipboard delegate to Avalonia's top-level Clipboard and
// kicks off the initial listFieldValuesAcrossInstances pass on Opened so the
// user sees rows without an extra click. Matches the MethodOverloadsWindow
// pattern for consistency.

using System.Collections;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class FieldAcrossInstancesWindow : Window
{
    public FieldAcrossInstancesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FieldAcrossInstancesWindow(FieldAcrossInstancesViewModel vm) : this()
    {
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;

        // Phase 21a polish — AOT-safe column sorting. Avalonia 12's default
        // DataGrid sort uses reflection to read each row's property via the
        // column's SortMemberPath, which the AOT trimmer drops — so header
        // clicks become silent no-ops in published builds. The documented
        // escape hatch is the per-column CustomSortComparer (IComparer),
        // which short-circuits the reflection path. We wire one comparer
        // per column at construction time so the DataGrid's built-in
        // sort-direction toggle + glyph rendering keep working unchanged.
        Opened += async (_, _) =>
        {
            WireCustomSortComparers();
            try { await vm.RefreshCommand.ExecuteAsync(null); }
            catch { /* surfaced via VM */ }
        };
    }

    private void WireCustomSortComparers()
    {
        var grid = this.FindControl<DataGrid>("RowsGrid");
        if (grid is null) return;
        foreach (var col in grid.Columns)
        {
            col.CustomSortComparer = col.SortMemberPath switch
            {
                "Instance"     => InstanceAddressComparer.Instance,
                "FieldAddress" => FieldAddressComparer.Instance,
                "Value"        => ValueOrdinalComparer.Instance,
                _              => col.CustomSortComparer,
            };
        }
    }

    // Per-column comparers below. Each takes two FieldAcrossInstanceRow objects
    // (Avalonia hands them to IComparer.Compare as `object?`) and produces a
    // sensible ordering for that column. Hex address columns parse to ulong so
    // "0x100000000" doesn't sort before "0x80000000" (alpha-sort gotcha).
    private sealed class InstanceAddressComparer : IComparer
    {
        public static readonly IComparer Instance = new InstanceAddressComparer();
        public int Compare(object? x, object? y) =>
            CompareHex((x as FieldAcrossInstanceRow)?.Instance,
                       (y as FieldAcrossInstanceRow)?.Instance);
    }

    private sealed class FieldAddressComparer : IComparer
    {
        public static readonly IComparer Instance = new FieldAddressComparer();
        public int Compare(object? x, object? y) =>
            CompareHex((x as FieldAcrossInstanceRow)?.FieldAddress,
                       (y as FieldAcrossInstanceRow)?.FieldAddress);
    }

    private sealed class ValueOrdinalComparer : IComparer
    {
        public static readonly IComparer Instance = new ValueOrdinalComparer();
        public int Compare(object? x, object? y)
        {
            string a = (x as FieldAcrossInstanceRow)?.Value ?? string.Empty;
            string b = (y as FieldAcrossInstanceRow)?.Value ?? string.Empty;
            return string.Compare(a, b, System.StringComparison.Ordinal);
        }
    }

    private static int CompareHex(string? a, string? b)
    {
        if (TryParseHex(a, out ulong ua) && TryParseHex(b, out ulong ub))
            return ua.CompareTo(ub);
        return string.Compare(a ?? string.Empty, b ?? string.Empty,
                              System.StringComparison.Ordinal);
    }

    private static bool TryParseHex(string? s, out ulong value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        System.ReadOnlySpan<char> span = s.AsSpan();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
            span = span.Slice(2);
        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
