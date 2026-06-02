// PivotBrowseClassesWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotBrowseClassesWindow : Window
{
    public PivotBrowseClassesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PivotBrowseClassesWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
        Loaded += (_, _) =>
        {
            this.FindControl<DataGrid>("PivotShortlistGrid")?.WireSortComparers(ShortlistComparers);
            this.FindControl<DataGrid>("PivotShortlistFieldGrid")?.WireSortComparers(FieldComparers);
        };
    }

    private static readonly IReadOnlyDictionary<string, IComparer> ShortlistComparers =
        new Dictionary<string, IComparer>
        {
            ["ClassFqn"]            = DataGridSortComparers.Ordinal<ClassShortlistRow>(r => r.ClassFqn),
            ["Assembly"]            = DataGridSortComparers.Ordinal<ClassShortlistRow>(r => r.Assembly),
            ["Presence"]            = DataGridSortComparers.Number <ClassShortlistRow>(r => r.Presence),
            ["AvgInstances"]        = DataGridSortComparers.Double <ClassShortlistRow>(r => r.AvgInstances),
            ["Cv"]                  = DataGridSortComparers.Double <ClassShortlistRow>(r => r.Cv),
            ["FieldCount"]          = DataGridSortComparers.Number <ClassShortlistRow>(r => r.FieldCount),
            ["PrimitiveFieldRatio"] = DataGridSortComparers.Double <ClassShortlistRow>(r => r.PrimitiveFieldRatio),
            ["NullFieldRatio"]      = DataGridSortComparers.Double <ClassShortlistRow>(r => r.NullFieldRatio),
            ["StringFieldRatio"]    = DataGridSortComparers.Double <ClassShortlistRow>(r => r.StringFieldRatio),
            ["ArrayFieldRatio"]     = DataGridSortComparers.Double <ClassShortlistRow>(r => r.ArrayFieldRatio),
        };

    private static readonly IReadOnlyDictionary<string, IComparer> FieldComparers =
        new Dictionary<string, IComparer>
        {
            ["Name"]        = DataGridSortComparers.Ordinal<ClassFieldInfo>(r => r.Name),
            ["TypeDisplay"] = DataGridSortComparers.Ordinal<ClassFieldInfo>(r => r.TypeDisplay),
            ["FlagsDisplay"] = DataGridSortComparers.Ordinal<ClassFieldInfo>(r => r.FlagsDisplay),
        };

    private void OnPivotShortlistDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ClassShortlistRow row) return;
        vm.ClassPivot.UseShortlistRow(row);
    }

    private ClassFieldInfo? GetSelectedShortlistField()
    {
        var grid = this.FindControl<DataGrid>("PivotShortlistFieldGrid");
        return grid?.SelectedItem as ClassFieldInfo;
    }

    private void OnPivotShortlistFieldDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ClassFieldInfo f) return;
        vm.ClassPivot.AddFieldNameToKeys(f.Name);
    }

    private void OnPivotShortlistFieldAddToKeysClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var f = GetSelectedShortlistField();
        if (f is null) return;
        vm.ClassPivot.AddFieldNameToKeys(f.Name);
    }

    private void OnPivotShortlistFieldAddToValuesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var f = GetSelectedShortlistField();
        if (f is null) return;
        vm.ClassPivot.AddFieldNameToValues(f.Name);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
