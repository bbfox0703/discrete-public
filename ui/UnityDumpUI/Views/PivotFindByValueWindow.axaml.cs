// PivotFindByValueWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
// Handlers relocated verbatim from MainWindow.axaml.cs (DataContext is the
// shared MainWindowViewModel, so vm.ClassPivot.* resolves identically).
using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotFindByValueWindow : Window
{
    public PivotFindByValueWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PivotFindByValueWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
        Loaded += (_, _) =>
            this.FindControl<DataGrid>("PivotLocatorGrid")?.WireSortComparers(SortComparers);
    }

    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["ClassFqn"]       = DataGridSortComparers.Ordinal<ValueLocatorHit>(r => r.ClassFqn),
            ["Assembly"]       = DataGridSortComparers.Ordinal<ValueLocatorHit>(r => r.Assembly),
            ["FieldName"]      = DataGridSortComparers.Ordinal<ValueLocatorHit>(r => r.FieldName),
            ["InArrayElement"] = DataGridSortComparers.Bool   <ValueLocatorHit>(r => r.InArrayElement),
            ["SnapHitCount"]   = DataGridSortComparers.Number <ValueLocatorHit>(r => r.SnapHitCount),
            ["MatchRarity"]    = DataGridSortComparers.Double <ValueLocatorHit>(r => r.MatchRarity),
            ["Score"]          = DataGridSortComparers.Double <ValueLocatorHit>(r => r.Score),
        };

    private void OnPivotLocatorDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ValueLocatorHit hit) return;
        vm.ClassPivot.UseLocatorHit(hit);
    }

    private void OnPivotLocatorUseClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("PivotLocatorGrid");
        if (grid?.SelectedItem is not ValueLocatorHit hit) return;
        vm.ClassPivot.UseLocatorHit(hit);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
