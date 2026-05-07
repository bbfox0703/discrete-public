// MethodCallWindow.axaml.cs — Phase 19d code-behind.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class MethodCallWindow : Window
{
    public MethodCallWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MethodCallWindow(MethodCallViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
