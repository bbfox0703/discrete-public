// FieldReferencesWindow.axaml.cs — Phase 28 modal code-behind.
//
// Wires VM clipboard delegate, kicks off the initial RPC on Opened, and
// installs per-column CustomSortComparer for AOT-safe DataGrid sort
// (see DataGridSortComparers.cs for the three-layer trap).

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

public partial class FieldReferencesWindow : Window
{
    private FieldReferencesViewModel? _vm;

    public FieldReferencesWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public FieldReferencesWindow(FieldReferencesViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;

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

    /// <summary>AOT-safe sort wiring — keyed by each column's SortMemberPath.
    /// Hex columns use numeric compare; everything else ordinal string compare
    /// (Kind / Class / Method / IlOffset / Confidence are short labels).</summary>
    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["Kind"]           = DataGridSortComparers.Ordinal<FieldReferenceRowVm>(r => r.Kind),
            ["ClassQualified"] = DataGridSortComparers.Ordinal<FieldReferenceRowVm>(r => r.ClassQualified),
            ["Method"]         = DataGridSortComparers.Ordinal<FieldReferenceRowVm>(r => r.Method),
            ["Address"]        = DataGridSortComparers.Hex<FieldReferenceRowVm>(r => r.Address),
            ["HitAddress"]     = DataGridSortComparers.Hex<FieldReferenceRowVm>(r => r.HitAddress),
            ["IlOffset"]       = DataGridSortComparers.Ordinal<FieldReferenceRowVm>(r => r.IlOffset),
            ["Confidence"]     = DataGridSortComparers.Ordinal<FieldReferenceRowVm>(r => r.Confidence),
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
