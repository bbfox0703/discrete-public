// PivotPickFieldsWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotPickFieldsWindow : Window
{
    public PivotPickFieldsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PivotPickFieldsWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
    }

    private FieldScore? GetSelectedFieldScore()
    {
        var grid = this.FindControl<DataGrid>("PivotFieldScoreGrid");
        return grid?.SelectedItem as FieldScore;
    }

    private void OnPivotFieldScoreDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not FieldScore row) return;
        vm.ClassPivot.AddFieldToKeys(row);
    }

    private void OnPivotFieldAddToKeysClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var f = GetSelectedFieldScore();
        if (f is null) return;
        vm.ClassPivot.AddFieldToKeys(f);
    }

    private void OnPivotFieldAddToValuesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var f = GetSelectedFieldScore();
        if (f is null) return;
        vm.ClassPivot.AddFieldToValues(f);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
