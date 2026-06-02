// PivotFindByValueWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
// Handlers relocated verbatim from MainWindow.axaml.cs (DataContext is the
// shared MainWindowViewModel, so vm.ClassPivot.* resolves identically).
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    }

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
