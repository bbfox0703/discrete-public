using System.Collections.Generic;
using System.ComponentModel;
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
        vm.Browser.CopyToClipboardAsync       = CopyToClipboardAsync;
        vm.Inspector.CopyToClipboardAsync     = CopyToClipboardAsync;
        vm.Inspector.ScrollFieldIntoView      = ScrollInspectorFieldIntoView;
        // Phase 17c — clipboard hook for the "Copy instance address" button
        // on the Address Lookup tab.
        vm.AddressLookup.CopyToClipboardAsync = CopyToClipboardAsync;
        vm.ShowProxyDeploy                    = ShowProxyDeployWindow;
        DataContext = vm;
        // Pane-collapse plumbing — IsVisible bindings on the panes' content
        // hide the body, but the column itself only shrinks if we mutate
        // ColumnDefinition.Width. Listen to the VM's bool property so the
        // column collapses to a thin rail (28 px) and remembers the user's
        // last expanded width via the GridSplitter for restoration.
        vm.PropertyChanged += OnMainViewModelPropertyChanged;
    }

    /// <summary>Last expanded widths for the Assemblies / Classes columns.
    /// Captured immediately before collapse so the pane restores to the
    /// width the user actually had (which may differ from the XAML default
    /// thanks to the GridSplitter).</summary>
    private GridLength _assembliesPaneExpandedWidth = new GridLength(240);
    private GridLength _classesPaneExpandedWidth    = new GridLength(280);

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm) return;
        if (e.PropertyName == nameof(MainWindowViewModel.IsAssembliesPaneCollapsed))
        {
            ApplyPaneCollapse(columnIndex: 0, vm.IsAssembliesPaneCollapsed,
                              ref _assembliesPaneExpandedWidth);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsClassesPaneCollapsed))
        {
            ApplyPaneCollapse(columnIndex: 2, vm.IsClassesPaneCollapsed,
                              ref _classesPaneExpandedWidth);
        }
    }

    private void ApplyPaneCollapse(int columnIndex, bool collapsed, ref GridLength expandedWidth)
    {
        // ColumnDefinition isn't a regular control so FindControl by x:Name
        // doesn't reach it reliably across Avalonia versions. Reach it via
        // the parent Grid + index instead — index is documented at the
        // ColumnDefinitions block in MainWindow.axaml (0=Assemblies,
        // 2=Classes; odd indices are GridSplitters).
        var grid = this.FindControl<Grid>("BodyGrid");
        if (grid is null || columnIndex >= grid.ColumnDefinitions.Count) return;
        ColumnDefinition column = grid.ColumnDefinitions[columnIndex];
        if (collapsed)
        {
            // Snapshot the live width — the user may have dragged the
            // GridSplitter since the last toggle, so the XAML-default
            // value would be wrong on restore.
            expandedWidth = column.Width;
            column.Width  = new GridLength(28);
        }
        else
        {
            column.Width = expandedWidth;
        }
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

    /// <summary>
    /// Phase 17c — Inspector "Find what's at this address" right-click
    /// handler. Reads the right-clicked row's effective address out of the
    /// Inspector VM (it's the same node the existing Copy CE XML / Copy CE
    /// field commands operate on — i.e. <c>SelectedNode</c> at the time the
    /// context menu opened) and hands it to the Address Lookup VM via
    /// <see cref="MainWindowViewModel.LookupInspectorAddressAsync"/>, which
    /// also flips the right-pane TabControl over.
    /// </summary>
    private async void OnLookupInspectorAddressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var node = vm.Inspector.SelectedNode;
        if (node is null) return;
        // Prefer the dereferenced pointer for pointer-typed rows so the
        // lookup lands inside the target object, not at the field that
        // holds the pointer. Falls back to the field's storage address
        // for primitive / value-struct rows where EffectiveAddress IS
        // the storage address.
        string? addr = node.EffectiveAddress ?? node.Address;
        if (string.IsNullOrEmpty(addr)) return;
        await vm.LookupInspectorAddressAsync(addr).ConfigureAwait(true);
    }

    /// <summary>
    /// Phase 18c — right-click on a Methods row → enumerate every overload
    /// of that method on the currently-selected class, in a modal window
    /// that lets the user pick one or many and copy an AOB AA script. The
    /// selected-class spec comes from <see cref="AssemblyBrowserViewModel"/>
    /// (which already drove the Methods grid the user just clicked).
    /// </summary>
    private async void OnMethodFindOverloadsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var browser = vm.Browser;
        if (browser.SelectedAssembly is null || browser.SelectedClass is null) return;

        // Resolve which row the right-click targeted. Fall back to the
        // grid's current SelectedItem when the context menu was opened
        // without a row beneath the pointer.
        MethodRow? targetRow = null;
        if (sender is Avalonia.Controls.MenuItem mi &&
            mi.Parent is Avalonia.Controls.ContextMenu cm &&
            cm.PlacementTarget is Avalonia.Controls.DataGrid placedGrid &&
            placedGrid.SelectedItem is MethodRow sel)
        {
            targetRow = sel;
        }
        if (targetRow is null)
        {
            var methodsGrid = this.FindControl<Avalonia.Controls.DataGrid>("MethodsDataGrid");
            targetRow = methodsGrid?.SelectedItem as MethodRow;
        }
        if (targetRow is null) return;

        var overloadsVm = new MethodOverloadsViewModel(
            vm.BackendAdapter, vm.Logger,
            browser.SelectedAssembly.Name,
            browser.SelectedClass.Namespace,
            browser.SelectedClass.Name,
            targetRow.Name,
            // Re-use the inspector's existing AOBMaker bridge so a single
            // session shares one pipe-client lifetime + one log stream.
            // Bridge no-ops cleanly when AOBMaker / CE aren't running, so
            // the user just sees the "AOBMaker pipe unavailable" status
            // line — same behaviour as the Inspector's HEX button.
            vm.Inspector.AobMaker);
        var win = new MethodOverloadsWindow(overloadsVm);
        await win.ShowDialog(this);
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
