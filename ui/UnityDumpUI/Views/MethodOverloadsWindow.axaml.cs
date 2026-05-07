// MethodOverloadsWindow.axaml.cs — Phase 18c modal dialog code-behind.
//
// Wires the VM's clipboard delegate to Avalonia's top-level Clipboard, and
// kicks off the initial findMethodInstances pass when the window opens.

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class MethodOverloadsWindow : Window
{
    public MethodOverloadsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MethodOverloadsWindow(MethodOverloadsViewModel vm) : this()
    {
        DataContext = vm;
        vm.CopyToClipboardAsync = CopyToClipboardAsync;
        // Kick off the search as soon as the window is shown so the grid
        // populates without an extra click. Errors land in vm.ErrorMessage.
        Opened += async (_, _) =>
        {
            try { await vm.RefreshAsync(); }
            catch { /* surfaced via VM */ }
        };
    }

    private async Task CopyToClipboardAsync(string text)
    {
        // Avalonia 12: same clipboard pattern MainWindow uses — see the
        // detailed comment there. IClipboard.SetDataAsync(IAsyncDataTransfer).
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
