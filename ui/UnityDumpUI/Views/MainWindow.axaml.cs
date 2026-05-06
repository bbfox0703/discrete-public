using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var vm = new MainWindowViewModel();
        // Hand the VM the View-scoped callbacks. Keeps the VM free of
        // Avalonia Window / StorageProvider types.
        vm.PickProcessAsync          = ShowPickerDialogAsync;
        vm.Browser.SaveFileAsync     = SaveFileAsync;
        vm.Browser.CopyToClipboardAsync   = CopyToClipboardAsync;
        vm.Inspector.CopyToClipboardAsync = CopyToClipboardAsync;
        vm.Inspector.ScrollFieldIntoView  = ScrollInspectorFieldIntoView;
        vm.ShowProxyDeploy                = ShowProxyDeployWindow;
        DataContext = vm;
    }

    /// <summary>
    /// Bring an inspector field row into the visible portion of the Walked
    /// fields DataGrid. Called by the VM after re-walking (instance switch
    /// or auto-refresh tick) so the user's previously-focused row stays
    /// visible without manual re-scroll. Lookup is by x:Name on the grid.
    /// </summary>
    private void ScrollInspectorFieldIntoView(WalkedFieldNode node)
    {
        // Defer to the next dispatcher pass — at the moment SelectedNode is
        // set, the DataGrid hasn't necessarily realised the new row container,
        // so ScrollIntoView would no-op. Posting it back through the
        // dispatcher gives the layout pass a chance to materialise the row.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldsDataGrid");
            grid?.ScrollIntoView(node, null);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Write text to the system clipboard via the top-level's Clipboard.
    /// Wired into AssemblyBrowserViewModel for the Phase 10c "Copy CE XML"
    /// / "Copy CE field" commands.
    /// </summary>
    private async Task CopyToClipboardAsync(string text)
    {
        // Avalonia 12: the legacy DataObject / DataFormats / SetDataObjectAsync
        // surface is gone. The single supported writer is
        // IClipboard.SetDataAsync(IAsyncDataTransfer) — wrap a single text
        // DataTransferItem in a DataTransfer and hand it over.
        var clipboard = Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    private async Task<ProcessRow?> ShowPickerDialogAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return null;
        var pickerVm = new ProcessPickerViewModel(vm.ProcessEnumerator);
        var picker   = new ProcessPickerWindow(pickerVm);
        _ = pickerVm.RefreshCommand.ExecuteAsync(null);
        return await picker.ShowDialog<ProcessRow?>(this);
    }

    /// <summary>
    /// Show the Proxy Deploy panel. Non-modal so the user can keep it open
    /// alongside the main window while connecting / browsing other panels.
    /// VM lifetime tracks the window — when the user closes it, the VM is
    /// dropped (a fresh one is built next time the user clicks the button).
    /// </summary>
    private void ShowProxyDeployWindow(ProxyDeployViewModel vm)
    {
        var win = new ProxyDeployWindow(vm);
        win.Show(this);
    }

    private async Task<string?> SaveFileAsync(string suggestedName, string description, string ext)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension  = ext,
            FileTypeChoices   = new List<FilePickerFileType>
            {
                new(description) { Patterns = new[] { "*." + ext } },
            },
        });
        return file?.TryGetLocalPath();
    }
}
