// PivotQueryWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
// The core pivot query (class · key · value · Run) extracted into a dialog.
// DataContext is the shared MainWindowViewModel so the existing
// {Binding ClassPivot.*} bindings + RelayCommands work unchanged; Run populates
// ClassPivot.Results, which the tab's results grid reflects once this closes.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotQueryWindow : Window
{
    public PivotQueryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public PivotQueryWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
