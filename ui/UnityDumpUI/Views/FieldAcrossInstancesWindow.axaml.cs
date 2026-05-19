// FieldAcrossInstancesWindow.axaml.cs — Phase 21a modal dialog code-behind.
//
// Hooks the VM's clipboard delegate to Avalonia's top-level Clipboard and
// kicks off the initial listFieldValuesAcrossInstances pass on Opened so the
// user sees rows without an extra click. Matches the MethodOverloadsWindow
// pattern for consistency.

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class FieldAcrossInstancesWindow : Window
{
    public FieldAcrossInstancesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FieldAcrossInstancesWindow(FieldAcrossInstancesViewModel vm) : this()
    {
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;
        Opened += async (_, _) =>
        {
            try { await vm.RefreshCommand.ExecuteAsync(null); }
            catch { /* surfaced via VM */ }
        };
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
