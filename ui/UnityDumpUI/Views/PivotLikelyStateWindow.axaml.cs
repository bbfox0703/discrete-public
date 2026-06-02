// PivotLikelyStateWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotLikelyStateWindow : Window
{
    public PivotLikelyStateWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PivotLikelyStateWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
        // Wire AOT-safe sort comparers once the grid materializes its columns
        // (the tab's WireSortComparersForAllGrids no longer reaches this grid —
        // it lives in the dialog now).
        Loaded += (_, _) =>
            this.FindControl<DataGrid>("PivotInterestGrid")?.WireSortComparers(SortComparers);
    }

    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["ClassFqn"]      = DataGridSortComparers.Ordinal<ClassInterestRow>(r => r.ClassFqn),
            ["Assembly"]      = DataGridSortComparers.Ordinal<ClassInterestRow>(r => r.Assembly),
            ["InterestScore"] = DataGridSortComparers.Double <ClassInterestRow>(r => r.InterestScore),
            ["BestKeyScore"]  = DataGridSortComparers.Double <ClassInterestRow>(r => r.BestKeyScore),
            ["MaxVolatility"] = DataGridSortComparers.Double <ClassInterestRow>(r => r.MaxVolatility),
        };

    private void OnPivotInterestDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ClassInterestRow row) return;
        vm.ClassPivot.UseInterestRow(row);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
