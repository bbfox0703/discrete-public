// PivotBrowseClassesWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    }

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
