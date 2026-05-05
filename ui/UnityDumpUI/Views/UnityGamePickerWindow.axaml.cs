// UnityGamePickerWindow.axaml.cs — modal dialog code-behind. Returns the
// selected UnityGameEntry (or null on cancel) via Window.Close(result).

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class UnityGamePickerWindow : Window
{
    public UnityGamePickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public UnityGamePickerWindow(UnityGamePickerViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close((UnityGameEntry?)null);
    }

    private void OnAcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UnityGamePickerViewModel vm && vm.Selected is { } row)
        {
            Close(row);
        }
        else
        {
            Close((UnityGameEntry?)null);
        }
    }
}
