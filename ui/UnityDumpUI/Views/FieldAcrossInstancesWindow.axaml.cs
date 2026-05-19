// FieldAcrossInstancesWindow.axaml.cs — Phase 21a modal dialog code-behind.
//
// Hooks the VM's clipboard delegate to Avalonia's top-level Clipboard and
// kicks off the initial listFieldValuesAcrossInstances pass on Opened so the
// user sees rows without an extra click. Matches the MethodOverloadsWindow
// pattern for consistency.

using System;
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

        // Diag prints fire from TWO event hooks so we can tell which lifecycle
        // stage works. Opened fires after the window is shown; Loaded fires
        // earlier on the root visual. Both are sync handlers — no async-lambda
        // unobserved-exception trap.
        Opened += OnOpenedDiag;
        Loaded += OnLoadedDiag;
    }

    private void OnLoadedDiag(object? sender, RoutedEventArgs e)
    {
        TryLog("Loaded event fired");
        SafeHookSortDiagnostics(stage: "Loaded");
    }

    private async void OnOpenedDiag(object? sender, EventArgs e)
    {
        TryLog("Opened event fired");
        SafeHookSortDiagnostics(stage: "Opened");
        if (_vm is not null)
        {
            try { await _vm.RefreshCommand.ExecuteAsync(null); }
            catch (Exception ex) { TryLog($"RefreshCommand threw: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void SafeHookSortDiagnostics(string stage)
    {
        try
        {
            HookSortDiagnostics(stage);
        }
        catch (Exception ex)
        {
            TryLog($"HookSortDiagnostics({stage}) threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic — log effective per-column sort flags + hook the DataGrid's
    /// Sorting event so we can tell apart "header click not recognized" from
    /// "comparer broken" when published / AOT-recompiled. Logs land in the
    /// standard FileLogger output (`ui\view\current.log`).
    /// </summary>
    private void HookSortDiagnostics(string stage)
    {
        var grid = this.FindControl<DataGrid>("RowsGrid");
        if (grid is null) { TryLog($"[{stage}] RowsGrid not found"); return; }

        string itemsType = grid.ItemsSource?.GetType().Name ?? "<null>";
        TryLog($"[{stage}] RowsGrid found. CanUserSortColumns={grid.CanUserSortColumns}, " +
               $"ItemsSourceType={itemsType}, ColumnCount={grid.Columns.Count}");
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var c = grid.Columns[i];
            string binding = "<n/a>";
            if (c is DataGridBoundColumn b && b.Binding is { } ib)
            {
                binding = ib is Avalonia.Data.Binding av
                    ? $"Binding(Path='{av.Path}')"
                    : $"{ib.GetType().Name}";
            }
            TryLog($"[{stage}]   col[{i}] type={c.GetType().Name} header={c.Header} " +
                   $"canUserSort={c.CanUserSort} sortMemberPath='{c.SortMemberPath}' " +
                   $"binding={binding} customSortComparer={(c.CustomSortComparer is null ? "null" : "set")}");
        }

        // Hook Sorting only once — Loaded can fire before Opened so guard via a flag.
        if (!_sortingHooked)
        {
            grid.Sorting += (_, e) =>
            {
                TryLog($"Sorting event fired. header='{e.Column.Header}' " +
                       $"SortMemberPath='{e.Column.SortMemberPath}'");
            };
            _sortingHooked = true;
            TryLog($"[{stage}] Sorting event handler attached");
        }
    }

    private bool _sortingHooked;

    private void TryLog(string msg)
    {
        try { _vm?.LogDiagnostic(msg); }
        catch { /* logger should never throw, but belt-and-braces */ }
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
