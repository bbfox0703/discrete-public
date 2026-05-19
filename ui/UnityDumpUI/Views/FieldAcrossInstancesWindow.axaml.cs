// FieldAcrossInstancesWindow.axaml.cs — Phase 21a modal dialog code-behind.
//
// Wires the VM clipboard delegate to Avalonia's clipboard, kicks off the
// initial RPC on Opened, and installs the per-column CustomSortComparer
// needed for AOT-safe DataGrid sort (see DataGridSortComparers.cs for the
// three-layer trap defeated by this pattern).

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class FieldAcrossInstancesWindow : Window
{
    private FieldAcrossInstancesViewModel? _vm;

    public FieldAcrossInstancesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FieldAcrossInstancesWindow(FieldAcrossInstancesViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;

        // Loaded fires after the DataGrid materializes its column collection,
        // which is the safe time to assign CustomSortComparer per column.
        Loaded += OnLoaded;
        Opened += async (_, _) =>
        {
            try { await vm.RefreshCommand.ExecuteAsync(null); }
            catch { /* surfaced via VM */ }
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("RowsGrid");
        grid?.WireSortComparers(SortComparers);
    }

    /// <summary>
    /// AOT-safe sort wiring — keyed by each column's SortMemberPath. Hex
    /// columns use numeric compare (so 0x100000000 doesn't sort before
    /// 0x80000000); Value is a heterogeneous int / float / "true" /
    /// quoted-string column so we keep it as an ordinal string compare.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["Instance"]     = DataGridSortComparers.Hex<FieldAcrossInstanceRow>(r => r.Instance),
            ["FieldAddress"] = DataGridSortComparers.Hex<FieldAcrossInstanceRow>(r => r.FieldAddress),
            ["Value"]        = DataGridSortComparers.Ordinal<FieldAcrossInstanceRow>(r => r.Value),
        };

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
