// PivotLikelyStateWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    }

    private void OnPivotInterestDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not ClassInterestRow row) return;
        vm.ClassPivot.UseInterestRow(row);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
