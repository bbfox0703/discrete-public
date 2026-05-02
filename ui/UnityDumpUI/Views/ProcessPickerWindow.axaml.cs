// ProcessPickerWindow.axaml.cs — code-behind for the process picker dialog.
//
// Returns the selected ProcessRow (or null on cancel) via Window.Close(result).
// Caller awaits ShowDialog<ProcessRow?>(owner).

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class ProcessPickerWindow : Window
{
    public ProcessPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ProcessPickerWindow(ProcessPickerViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnAcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProcessPickerViewModel vm && vm.Selected is { } row)
        {
            Close(row);
        }
        else
        {
            Close(null);
        }
    }
}
