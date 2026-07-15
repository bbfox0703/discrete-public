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

    /// <summary>
    /// Set when the user double-clicks (or picks "Inspect this instance") on a
    /// row before the dialog closes. The owner reads it after
    /// <c>ShowDialog</c> returns and hands off to the Inspector tab. Null when
    /// the dialog was just closed without an inspect request.
    /// </summary>
    public InspectInstanceRequest? InspectRequest { get; private set; }

    public FieldAcrossInstancesWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public FieldAcrossInstancesWindow(FieldAcrossInstancesViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;

        // Capture the inspect handoff and close so the owner (MainWindow) can
        // route it to the Inspector tab after ShowDialog returns. Raised by
        // both the double-tap handler and the context-menu command.
        vm.InspectInstanceRequested += (_, req) =>
        {
            InspectRequest = req;
            Close();
        };

        // Detach the VM from the shared BulkOperationGate AND stop the class-wide
        // freeze loop when the dialog closes — the gate's PropertyChanged otherwise
        // roots every opened modal VM, and the freeze loop would keep writing to
        // the game after the window is gone.
        Closed += (_, _) => _vm?.Cleanup();

        // Loaded fires after the DataGrid materializes its column collection,
        // which is the safe time to assign CustomSortComparer per column.
        Loaded += OnLoaded;
        Opened += async (_, _) =>
        {
            try { await vm.RefreshCommand.ExecuteAsync(null); }
            catch { /* surfaced via VM */ }
        };
    }

    /// <summary>
    /// Double-click a row → jump back to the instance in the Inspector. The
    /// grid's SelectedItem is already the row under the cursor (single-click
    /// selects before the double-tap fires).
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not FieldAcrossInstanceRow row) return;
        _vm?.InspectInstanceCommand.Execute(row);
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
    /// quoted-string column, so it uses a numeric-or-string comparer that
    /// sorts numerically when both cells parse as numbers (Int32/Single
    /// fields) and ordinally otherwise (strings, bools).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["Instance"]     = DataGridSortComparers.Hex<FieldAcrossInstanceRow>(r => r.Instance),
            ["FieldAddress"] = DataGridSortComparers.Hex<FieldAcrossInstanceRow>(r => r.FieldAddress),
            ["Value"]        = DataGridSortComparers.NumericOrString<FieldAcrossInstanceRow>(r => r.Value),
        };

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        // SetDataAsync can throw when the clipboard is locked by another process
        // (clipboard managers, RDP). Swallow — a failed copy is non-destructive
        // and retryable; letting it escape an async void handler crashes the app.
        try { await clipboard.SetDataAsync(transfer); }
        catch { /* clipboard busy/locked — ignore */ }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
