// FieldAcrossInstancesWindow.axaml.cs — Phase 21a modal dialog code-behind.
//
// Hooks the VM's clipboard delegate to Avalonia's top-level Clipboard and
// kicks off the initial listFieldValuesAcrossInstances pass on Opened so the
// user sees rows without an extra click. Matches the MethodOverloadsWindow
// pattern for consistency.

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core;
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
        Opened += async (_, _) =>
        {
            HookSortDiagnostics();
            try { await vm.RefreshCommand.ExecuteAsync(null); }
            catch { /* surfaced via VM */ }
        };
    }

    /// <summary>
    /// Diagnostic — hook the DataGrid's Sorting event + log effective per-
    /// column sort flags so we can tell apart "header click not recognized"
    /// from "comparer broken" when published / AOT-recompiled. Logs land in
    /// the standard FileLogger output (ui\view category).
    /// </summary>
    private void HookSortDiagnostics()
    {
        var grid = this.FindControl<DataGrid>("RowsGrid");
        if (grid is null) { _vm?.LogDiagnostic("RowsGrid not found"); return; }

        _vm?.LogDiagnostic(
            $"RowsGrid found. CanUserSortColumns={grid.CanUserSortColumns}, " +
            $"ItemsSourceType={grid.ItemsSource?.GetType().Name ?? "<null>"}, " +
            $"ColumnCount={grid.Columns.Count}");
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var c = grid.Columns[i];
            string binding = c is DataGridBoundColumn b
                ? (b.Binding as Avalonia.Data.Binding)?.Path ?? "<no binding path>"
                : "<not bound column>";
            _vm?.LogDiagnostic(
                $"  col[{i}] type={c.GetType().Name} header={c.Header} " +
                $"canUserSort={c.CanUserSort} sortMemberPath='{c.SortMemberPath}' " +
                $"binding='{binding}' customSortComparer={(c.CustomSortComparer is null ? "null" : "set")}");
        }

        grid.Sorting += (s, e) =>
        {
            _vm?.LogDiagnostic(
                $"Sorting event fired. column.Header='{e.Column.Header}' " +
                $"SortMemberPath='{e.Column.SortMemberPath}'");
        };
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
