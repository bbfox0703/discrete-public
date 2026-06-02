// PivotSnapshotsWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotSnapshotsWindow : Window
{
    public PivotSnapshotsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PivotSnapshotsWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
