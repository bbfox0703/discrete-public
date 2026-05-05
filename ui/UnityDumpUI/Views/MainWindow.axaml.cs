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
        vm.PickFileAsync                  = PickFileAsync;
        vm.PickUnityGameAsync             = ShowUnityGamePickerAsync;
        DataContext = vm;
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
    /// Opens the Unity game picker (Steam scan) modally. The VM is supplied
    /// by the caller (MainWindowViewModel.ScanUnityGamesAsync) — keeps the
    /// scanner injection point in the VM rather than duplicating it here.
    /// </summary>
    private async Task<UnityDumpUI.Core.Platform.UnityGameEntry?> ShowUnityGamePickerAsync(
        UnityDumpUI.ViewModels.UnityGamePickerViewModel vm)
    {
        var win = new UnityGamePickerWindow(vm);
        return await win.ShowDialog<UnityDumpUI.Core.Platform.UnityGameEntry?>(this);
    }

    /// <summary>
    /// Open a Save File dialog scoped to this window. Returns the chosen
    /// absolute path or null on cancel. Wired into AssemblyBrowserViewModel
    /// for the Phase 10 export commands.
    /// </summary>
    /// <summary>
    /// Phase 12c: open-file picker scoped to this window. Returns the
    /// chosen absolute path or null on cancel. Wired into
    /// MainWindowViewModel.PickFileAsync for the proxy install flow.
    /// </summary>
    private async Task<string?> PickFileAsync(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title           = title,
            AllowMultiple   = false,
            FileTypeFilter  = new List<FilePickerFileType>
            {
                new(pattern) { Patterns = new[] { pattern } },
            },
        });
        if (files is null || files.Count == 0) return null;
        return files[0].TryGetLocalPath();
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
