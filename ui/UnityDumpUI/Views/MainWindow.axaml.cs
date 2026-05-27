using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UnityDumpUI.Core.Services;
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
        vm.SaveFileAsync             = SaveFileAsync;
        vm.Browser.SaveFileAsync     = SaveFileAsync;
        vm.Browser.CopyToClipboardAsync       = CopyToClipboardAsync;
        // Hand the VMs an accessor that snapshots the DataGrid's current
        // multi-row selection — Avalonia's DataGrid.SelectedItems isn't a
        // bindable property, so the View has to push it through a callback.
        // Lets "Copy CE field" on a multi-row Ctrl/Shift selection emit
        // every highlighted row, not just the last one focused. (Bug fix:
        // single-row paste still works because the VM falls back to its
        // SelectedItem binding when the accessor returns an empty list.)
        vm.Browser.GetSelectedFieldRows       = GetBrowserSelectedFieldRows;
        vm.Inspector.CopyToClipboardAsync     = CopyToClipboardAsync;
        vm.Inspector.GetSelectedNodes         = GetInspectorSelectedNodes;
        vm.Inspector.ScrollFieldIntoView      = ScrollInspectorFieldIntoView;
        // Phase 17c — clipboard hook for the "Copy instance address" button
        // on the Address Lookup tab.
        vm.AddressLookup.CopyToClipboardAsync = CopyToClipboardAsync;
        vm.SpcQuery.CopyToClipboardAsync      = CopyToClipboardAsync;
        vm.ShowProxyDeploy                    = ShowProxyDeployWindow;
        DataContext = vm;
        // Pane-collapse plumbing — IsVisible bindings on the panes' content
        // hide the body, but the column itself only shrinks if we mutate
        // ColumnDefinition.Width. Listen to the VM's bool property so the
        // column collapses to a thin rail (28 px) and remembers the user's
        // last expanded width via the GridSplitter for restoration.
        vm.PropertyChanged += OnMainViewModelPropertyChanged;

        // AOT-safe DataGrid sort wiring — Avalonia's default sort uses
        // reflection on each column's SortMemberPath which the AOT trimmer
        // drops. Set CustomSortComparer per column at Loaded time (when the
        // column collection is fully realized) so the grid's built-in
        // direction-toggle + arrow glyph keep working but the comparison
        // goes through our typed selectors. See DataGridSortComparers.cs
        // for the multi-layer trap this defeats.
        Loaded += (_, _) => WireSortComparersForAllGrids();

        // Avalonia 12 dual-monitor restore quirk: when the window goes
        // Normal → Maximized, Avalonia auto-saves the pre-maximize
        // bounds; but on dual-monitor setups with mismatched DPI scales
        // the restore math sometimes lands the window straddling the
        // monitor boundary instead of returning to its original
        // position. Track the bounds ourselves on Normal → Maximized
        // and force-restore them on Maximized → Normal.
        PropertyChanged += OnWindowPropertyChanged;
    }

    private PixelPoint? _preMaximizePosition;
    private double      _preMaximizeWidth;
    private double      _preMaximizeHeight;

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        var oldState = (WindowState)e.OldValue!;
        var newState = (WindowState)e.NewValue!;

        if (oldState == WindowState.Normal && newState == WindowState.Maximized)
        {
            // Save where we WERE before maximizing.
            _preMaximizePosition = Position;
            _preMaximizeWidth    = Width;
            _preMaximizeHeight   = Height;
        }
        else if (oldState == WindowState.Maximized && newState == WindowState.Normal
                 && _preMaximizePosition is { } savedPos)
        {
            // Avalonia has already restored the window to *some* bounds
            // by the time this fires; override with the bounds we
            // captured pre-maximize. PixelPoint is screen-coordinates
            // so this snaps the window back onto the exact monitor it
            // started on.
            Position = savedPos;
            if (_preMaximizeWidth  > 0) Width  = _preMaximizeWidth;
            if (_preMaximizeHeight > 0) Height = _preMaximizeHeight;
        }
    }

    private void WireSortComparersForAllGrids()
    {
        this.FindControl<DataGrid>("BrowserFieldsDataGrid")?
            .WireSortComparers(BrowserFieldsSortComparers);
        this.FindControl<DataGrid>("FieldsDataGrid")?
            .WireSortComparers(InspectorFieldsSortComparers);
        this.FindControl<DataGrid>("MethodsDataGrid")?
            .WireSortComparers(MethodsSortComparers);
        // Phase 29b — wire the Snapshot diff grid. Without this the
        // header-click sort silently no-ops under AOT
        // (docs/aot-pitfalls.md §4.5 "three-layer trap").
        this.FindControl<DataGrid>("SnapshotDiffGrid")?
            .WireSortComparers(SnapshotDiffSortComparers);
    }

    private static readonly IReadOnlyDictionary<string, IComparer> BrowserFieldsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["Name"]       = DataGridSortComparers.Ordinal<FieldRow>(r => r.Name),
            ["Offset"]     = DataGridSortComparers.Number <FieldRow>(r => r.Offset),
            ["Type"]       = DataGridSortComparers.Ordinal<FieldRow>(r => r.Type),
            ["Flags"]      = DataGridSortComparers.Number <FieldRow>(r => r.Flags),
            ["IsStatic"]   = DataGridSortComparers.Bool   <FieldRow>(r => r.IsStatic),
            ["DeclaredIn"] = DataGridSortComparers.Ordinal<FieldRow>(r => r.DeclaredIn),
        };

    private static readonly IReadOnlyDictionary<string, IComparer> InspectorFieldsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["Name"]         = DataGridSortComparers.Ordinal<WalkedFieldNode>(n => n.Name),
            ["Offset"]       = DataGridSortComparers.Number <WalkedFieldNode>(n => n.Offset),
            ["Address"]      = DataGridSortComparers.Hex    <WalkedFieldNode>(n => n.Address),
            ["Type"]         = DataGridSortComparers.Ordinal<WalkedFieldNode>(n => n.Type),
            ["DisplayValue"] = DataGridSortComparers.Ordinal<WalkedFieldNode>(n => n.DisplayValue),
            ["DeclaredIn"]   = DataGridSortComparers.Ordinal<WalkedFieldNode>(n => n.DeclaredIn),
        };

    private static readonly IReadOnlyDictionary<string, IComparer> MethodsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["Name"]       = DataGridSortComparers.Ordinal<MethodRow>(r => r.Name),
            ["Address"]    = DataGridSortComparers.Hex    <MethodRow>(r => r.Address),
            ["Token"]      = DataGridSortComparers.Number <MethodRow>(r => r.Token),
            ["Flags"]      = DataGridSortComparers.Number <MethodRow>(r => r.Flags),
            ["IsStatic"]   = DataGridSortComparers.Bool   <MethodRow>(r => r.IsStatic),
            ["DeclaredIn"] = DataGridSortComparers.Ordinal<MethodRow>(r => r.DeclaredIn),
        };

    // Phase 29b — Snapshot diff grid. SortMemberPath keys must match
    // the XAML's <DataGridTextColumn SortMemberPath="…"> values. Note
    // that Old/New/Delta sort as strings — they hold heterogeneous
    // shapes (int/float/ptr/quoted string), and a single numeric
    // comparer would mis-sort the non-numeric rows. Future polish
    // (Phase 29d) could expose typed delta filters.
    private static readonly IReadOnlyDictionary<string, IComparer> SnapshotDiffSortComparers =
        new Dictionary<string, IComparer>
        {
            ["ClassDisplay"]  = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.ClassDisplay),
            ["Field"]         = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.Field),
            // Cast ulong → long (unchecked preserves bit pattern; field
            // offsets stay well below Int64.MaxValue in practice anyway).
            ["FieldOffset"]   = DataGridSortComparers.Number <SnapshotDiffRow>(r => unchecked((long)r.FieldOffset)),
            ["InstanceLabel"] = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.InstanceLabel),
            ["OldValue"]      = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.OldValue),
            ["NewValue"]      = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.NewValue),
            ["Delta"]         = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.Delta),
            ["ChangeKind"]    = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.ChangeKind),
            ["FieldType"]     = DataGridSortComparers.Ordinal<SnapshotDiffRow>(r => r.FieldType),
        };

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
    /// Snapshot the Fields-tab DataGrid's current selection as a typed list
    /// the VM can consume without referencing Avalonia. Returns an empty
    /// list when only one row (or none) is highlighted — the VM treats
    /// that as the "use SelectedItem" path so single-row right-click stays
    /// unchanged.
    /// </summary>
    private IReadOnlyList<FieldRow> GetBrowserSelectedFieldRows()
        => SnapshotSelectedItems<FieldRow>("BrowserFieldsDataGrid");

    /// <summary>
    /// Same shape as <see cref="GetBrowserSelectedFieldRows"/>, for the
    /// Inspector tab's hierarchical Walked-fields grid.
    /// </summary>
    private IReadOnlyList<WalkedFieldNode> GetInspectorSelectedNodes()
        => SnapshotSelectedItems<WalkedFieldNode>("FieldsDataGrid");

    private IReadOnlyList<T> SnapshotSelectedItems<T>(string gridName) where T : class
    {
        var grid = this.FindControl<DataGrid>(gridName);
        if (grid?.SelectedItems is not System.Collections.IList selected || selected.Count <= 1)
            return System.Array.Empty<T>();
        var list = new List<T>(selected.Count);
        foreach (var item in selected)
        {
            if (item is T typed) list.Add(typed);
        }
        return list;
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

    /// <summary>Phase 29d-2: double-clicking a SPC result row pushes
    /// the instance address into the Inspector cross-tab handoff
    /// (static rows are no-op since there's no instance to inspect).</summary>
    private void OnSpcResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Avalonia.Controls.DataGrid grid) return;
        if (grid.SelectedItem is not UnityDumpUI.ViewModels.SpcMatch match) return;
        vm.SpcQuery.RequestInspectInstance(match);
    }

    /// <summary>Phase 29d-5: helper for the SPC result context-menu —
    /// the four handlers below all need the currently right-clicked
    /// row, which Avalonia surfaces via the DataGrid's SelectedItem
    /// (right-click selects under the standard interaction). Returns
    /// null if no row is selected so handlers can no-op gracefully.</summary>
    private UnityDumpUI.ViewModels.SpcMatch? GetSelectedSpcMatch()
    {
        var grid = this.FindControl<Avalonia.Controls.DataGrid>("SpcResultsGrid");
        return grid?.SelectedItem as UnityDumpUI.ViewModels.SpcMatch;
    }

    private void OnSpcInspectInstanceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var m = GetSelectedSpcMatch();
        if (m is null) return;
        vm.SpcQuery.RequestInspectInstance(m);
    }

    private async void OnSpcCopyClassFieldClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var m = GetSelectedSpcMatch();
        if (m is null) return;
        await vm.SpcQuery.CopyClassFieldAsync(m).ConfigureAwait(true);
    }

    private async void OnSpcCopyInstanceAddressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var m = GetSelectedSpcMatch();
        if (m is null) return;
        await vm.SpcQuery.CopyInstanceAddressAsync(m).ConfigureAwait(true);
    }

    private async void OnSpcCopyValueSequenceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var m = GetSelectedSpcMatch();
        if (m is null) return;
        await vm.SpcQuery.CopyValueSequenceAsync(m).ConfigureAwait(true);
    }

    /// <summary>Phase 29c-7: open the snapshot DB directory in Windows
    /// Explorer. <c>SnapshotDbFolder</c> returns the active store's dir
    /// or the platform default — either way it's created lazily so the
    /// shell call never sees a missing path.</summary>
    private void OnOpenSnapshotDbFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        string folder = vm.Snapshot.SnapshotDbFolder;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = folder,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            vm.Snapshot.ErrorMessage = $"Open folder failed: {ex.Message}";
        }
    }

    /// <summary>Phase 29c-7: launch the Settings modal so the user can
    /// change DB directory / AutoSave / AutoPrune threshold. The modal
    /// edits a clone of UserSettings and hands the updated copy back via
    /// ApplySettingsAsync on OK.</summary>
    private async void OnSnapshotSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var draft = vm.Snapshot.Settings.Clone();
        var dialog = new SnapshotSettingsWindow(draft, this.StorageProvider);
        var result = await dialog.ShowDialog<UserSettings?>(this);
        if (result is not null)
        {
            await vm.Snapshot.ApplySettingsAsync(result).ConfigureAwait(true);
        }
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

        MethodRow? targetRow = ResolveMethodRow(sender);
        if (targetRow is null) return;

        var overloadsVm = BuildOverloadsVm(vm, browser, targetRow);
        var win = new MethodOverloadsWindow(overloadsVm, vm.BackendAdapter, vm.Logger);
        await win.ShowDialog(this);
    }

    /// <summary>
    /// Phase 19d-2 — right-click on a Methods row → "Call method… (pick
    /// overload)". Fast-paths the common single-overload case: when
    /// <c>findMethodInstances</c> returns count == 1, jump straight to the
    /// MethodCallWindow seeded from that one entry — most user-defined
    /// methods (<c>AddItems</c>, <c>get_numberOfItems</c>, <c>Save</c>) are
    /// non-overloaded, so this saves an extra click. Multi-overload methods
    /// (<c>Mathf.Abs</c>, <c>String.Concat</c>) still drop into the picker
    /// modal so the user can choose. Errors (resolution failure, no entry
    /// returned) fall through to the modal where the user sees the
    /// diagnostic message in <c>vm.ErrorMessage</c>.
    /// </summary>
    private async void OnMethodCallMethodClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var browser = vm.Browser;
        if (browser.SelectedAssembly is null || browser.SelectedClass is null) return;

        MethodRow? targetRow = ResolveMethodRow(sender);
        if (targetRow is null) return;

        string asm = browser.SelectedAssembly.Name;
        string ns  = browser.SelectedClass.Namespace;
        string cls = browser.SelectedClass.Name;
        string mth = targetRow.Name;
        string? defaultInstance = GetDefaultMethodCallInstance(vm, asm, ns, cls);

        // Probe overloads first. Single-overload → jump straight to the
        // call dialog. Anything else (zero / multiple / RPC error) falls
        // through to the picker modal — the user gets to see the actual
        // diagnostic there and can refine via the signature filter.
        UnityDumpUI.Core.Services.FindMethodInstancesResult? probe = null;
        try
        {
            probe = await vm.BackendAdapter
                .FindMethodInstancesAsync(asm, ns, cls, mth, signatureMatch: null,
                                          ct: System.Threading.CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            // Fall through to the picker modal — it will re-issue the same
            // RPC and surface the same error in vm.ErrorMessage where the
            // user can read it.
        }

        if (probe is not null && string.IsNullOrEmpty(probe.Error) &&
            probe.Count == 1 && probe.Matches.Count == 1)
        {
            var only = probe.Matches[0];
            string sig    = only.Signature ?? "()";
            string sigLbl = only.Signature ?? "()";
            var callVm = new MethodCallViewModel(
                vm.BackendAdapter, vm.Logger,
                asm, ns, cls, mth,
                signatureMatch:   sig,
                signatureDisplay: sigLbl,
                paramTypes:       only.ParamTypes,
                paramNames:       only.ParamNames,
                returnType:       only.ReturnType ?? "System.Void",
                isStatic:         only.IsStatic,
                defaultInstanceAddress: defaultInstance);
            // Phase 22e: Send-to-CE button needs the session bridge.
            callVm.AobMaker = vm.AobMaker;
            var callWin = new MethodCallWindow(callVm);
            await callWin.ShowDialog(this);
            return;
        }

        // Zero / multiple overloads (or probe error): fall back to the
        // picker. Caller's instance default still threads through, so the
        // call dialog the user opens from a row-context-menu still
        // pre-fills with the inspector's selected instance.
        var overloadsVm = BuildOverloadsVm(vm, browser, targetRow);
        var win = new MethodOverloadsWindow(overloadsVm, vm.BackendAdapter, vm.Logger);
        await win.ShowDialog(this);
    }

    /// <summary>
    /// Resolve which Methods-grid row the context-menu click targeted —
    /// trying the menu's PlacementTarget first (for menus opened on a
    /// specific row), falling back to the grid's current SelectedItem when
    /// the menu was opened without a row beneath the pointer.
    /// </summary>
    private MethodRow? ResolveMethodRow(object? sender)
    {
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
        return targetRow;
    }

    /// <summary>
    /// Build a <see cref="MethodOverloadsViewModel"/> for the right-clicked
    /// Methods-grid row. Threads in the inspector's currently-selected
    /// instance address (when its target class matches), so the
    /// MethodCallWindow opened from a row context menu pre-fills the
    /// Instance address textbox without the user pasting it.
    /// </summary>
    private static MethodOverloadsViewModel BuildOverloadsVm(MainWindowViewModel vm,
                                                              AssemblyBrowserViewModel browser,
                                                              MethodRow targetRow)
    {
        string asm = browser.SelectedAssembly!.Name;
        string ns  = browser.SelectedClass!.Namespace;
        string cls = browser.SelectedClass.Name;
        return new MethodOverloadsViewModel(
            vm.BackendAdapter, vm.Logger,
            asm, ns, cls, targetRow.Name,
            // Phase 22a/d: session-wide AOBMaker bridge. Hoisted from
            // Inspector to a top-level VM property so the Phase 22d
            // Tools-menu Inject command, Inspector HEX/Disasm buttons,
            // and this Methods-grid context all share one pipe-client
            // lifetime + one log stream. Bridge no-ops cleanly when
            // AOBMaker / CE aren't running.
            vm.AobMaker,
            // Phase 19d-2: thread the inspector's selected instance through
            // when its target class matches the browser's selected class.
            // Pre-fills the Instance address textbox in MethodCallWindow.
            defaultInstanceAddress: GetDefaultMethodCallInstance(vm, asm, ns, cls),
            // Backend gate for the DLL-free Mono register actions —
            // mono_registerSymbolEx is meaningless on IL2CPP. Backend2
            // is the live handshake's backend string; falls back to
            // false on disconnect / unknown backend.
            isMonoBackend: string.Equals(vm.Backend2, "Mono", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Phase 19d-2 — return the inspector's currently-selected instance
    /// address ONLY when its target class matches the (assembly, namespace,
    /// class) the user is about to call a method on. Cross-class wiring
    /// would silently feed a wrong-type pointer to the trampoline, which
    /// the runtime would either crash on or quietly misinterpret — neither
    /// is acceptable, so we scope strictly. Null = no default; the
    /// MethodCallWindow shows an empty Instance textbox the user fills in.
    /// </summary>
    private static string? GetDefaultMethodCallInstance(MainWindowViewModel vm,
                                                        string assembly, string @namespace, string className)
    {
        var inspector = vm.Inspector;
        if (inspector.SelectedInstance is null) return null;
        if (string.IsNullOrEmpty(inspector.TargetAssembly) || string.IsNullOrEmpty(inspector.TargetName))
            return null;
        if (!string.Equals(inspector.TargetAssembly, assembly, System.StringComparison.Ordinal))
            return null;
        if (!string.Equals(inspector.TargetNamespace ?? string.Empty, @namespace ?? string.Empty,
                           System.StringComparison.Ordinal))
            return null;
        if (!string.Equals(inspector.TargetName, className, System.StringComparison.Ordinal))
            return null;
        return inspector.SelectedInstance.Address;
    }

    /// <summary>
    /// Phase 19d-2 — right-click an Inspector instance → "Call method on
    /// this instance…". Switches the right-pane TabControl over to the
    /// Methods grid and ensures the right-clicked instance is the
    /// inspector's <see cref="LiveInspectionViewModel.SelectedInstance"/>
    /// so the GetDefaultMethodCallInstance helper picks it up when the
    /// user right-clicks a method. Discoverability shim — the existing
    /// Methods-grid right-click flow ("Call method… (pick overload)") does
    /// all the heavy lifting; we just navigate the user there with the
    /// instance address remembered.
    /// </summary>
    private void OnCallMethodOnInstanceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var inspector = vm.Inspector;

        // Resolve the right-clicked instance. Same pattern as the Methods
        // grid handler — try the menu's PlacementTarget first, fall back to
        // the ListBox's current SelectedItem.
        InstanceItem? targetInstance = null;
        if (sender is Avalonia.Controls.MenuItem mi &&
            mi.Parent is Avalonia.Controls.ContextMenu cm &&
            cm.PlacementTarget is Avalonia.Controls.ListBox placedList &&
            placedList.SelectedItem is InstanceItem sel)
        {
            targetInstance = sel;
        }
        if (targetInstance is null)
        {
            targetInstance = inspector.SelectedInstance;
        }
        if (targetInstance is null) return;

        // Snap the inspector's SelectedInstance to the right-clicked row so
        // the cross-class scope check in GetDefaultMethodCallInstance picks
        // up THIS address (not whichever instance was previously walked).
        // Setter cascades to OnSelectedInstanceChanged → WalkInstanceAsync,
        // which is harmless and matches what a user click would do.
        if (!ReferenceEquals(inspector.SelectedInstance, targetInstance))
        {
            inspector.SelectedInstance = targetInstance;
        }
        vm.SelectedDetailTabIndex = MainWindowViewModel.TabIndexMethods;
    }

    // Right-click on a row in the Classes list → "Copy class name". Pushes
    // the fully-qualified "Namespace.Name" (or just "Name" when the namespace
    // is empty — handled by ClassItem.DisplayName) onto the clipboard. v1 is
    // single-select; the Classes ListBox is single-select today, so this
    // handler doesn't enumerate SelectedItems.
    private async void OnCopyClassNameClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var browser = vm.Browser;

        ClassItem? target = null;
        if (sender is Avalonia.Controls.MenuItem mi &&
            mi.Parent is Avalonia.Controls.ContextMenu cm &&
            cm.PlacementTarget is Avalonia.Controls.ListBox placedList &&
            placedList.SelectedItem is ClassItem sel)
        {
            target = sel;
        }
        target ??= browser.SelectedClass;
        if (target is null) return;

        if (browser.CopyToClipboardAsync is { } copy)
        {
            await copy(target.DisplayName).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Resolve the right-clicked row of a DataGrid hosting <typeparamref name="T"/>
    /// rows. Prefers the menu's PlacementTarget (the row beneath the pointer when
    /// the menu opened) and falls back to the grid's current SelectedItem when
    /// the menu was opened away from a row. Mirrors the pattern in
    /// <see cref="ResolveMethodRow"/>.
    /// </summary>
    private static T? ResolveDataGridRow<T>(object? sender, DataGrid? fallbackGrid)
        where T : class
    {
        if (sender is Avalonia.Controls.MenuItem mi &&
            mi.Parent is Avalonia.Controls.ContextMenu cm &&
            cm.PlacementTarget is Avalonia.Controls.DataGrid placedGrid &&
            placedGrid.SelectedItem is T sel)
        {
            return sel;
        }
        return fallbackGrid?.SelectedItem as T;
    }

    private async Task CopyBrowserTextAsync(string? text)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (text is null) return;
        if (vm.Browser.CopyToClipboardAsync is { } copy)
        {
            await copy(text).ConfigureAwait(true);
        }
    }

    // Browser Fields tab per-cell copy. Single-select only; multi-select
    // handling is a v2 follow-up (would need to join values per column
    // with newlines). See en.axaml Browser.CopyField*Menu strings.
    private async void OnCopyFieldNameClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<FieldRow>(sender, this.FindControl<DataGrid>("BrowserFieldsDataGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.Name).ConfigureAwait(true);
    }

    private async void OnCopyFieldTypeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<FieldRow>(sender, this.FindControl<DataGrid>("BrowserFieldsDataGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.Type).ConfigureAwait(true);
    }

    private async void OnCopyFieldDeclaredInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<FieldRow>(sender, this.FindControl<DataGrid>("BrowserFieldsDataGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.DeclaredIn).ConfigureAwait(true);
    }

    // Browser Methods tab per-cell copy. The "Copy address" entry
    // substitutes for "Copy return type" — MethodRow doesn't carry a return
    // type (only the FindMethodInstances RPC does), and Address is a visible
    // column on this grid. Single-select only; multi-select is a follow-up.
    private async void OnCopyMethodNameClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<MethodRow>(sender, this.FindControl<DataGrid>("MethodsDataGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.Name).ConfigureAwait(true);
    }

    private async void OnCopyMethodAddressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<MethodRow>(sender, this.FindControl<DataGrid>("MethodsDataGrid"));
        if (row is null) return;
        // MethodRow.Address is null for some rows (no JIT'd entry yet on
        // Mono, or AOT entries that couldn't be resolved). Copying an empty
        // string would be useless; bail out instead so the user notices.
        if (string.IsNullOrEmpty(row.Address)) return;
        await CopyBrowserTextAsync(row.Address).ConfigureAwait(true);
    }

    private async void OnCopyMethodDeclaredInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<MethodRow>(sender, this.FindControl<DataGrid>("MethodsDataGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.DeclaredIn).ConfigureAwait(true);
    }

    private async void OnCopyInstanceAddressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var inspector = vm.Inspector;

        InstanceItem? targetInstance = null;
        if (sender is Avalonia.Controls.MenuItem mi &&
            mi.Parent is Avalonia.Controls.ContextMenu cm &&
            cm.PlacementTarget is Avalonia.Controls.ListBox placedList &&
            placedList.SelectedItem is InstanceItem sel)
        {
            targetInstance = sel;
        }
        targetInstance ??= inspector.SelectedInstance;
        if (targetInstance is null) return;

        if (inspector.CopyToClipboardAsync is { } copy)
        {
            await copy(targetInstance.Address).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Phase 21a — "List this field across instances…" right-click handler.
    /// Resolves the selected Inspector row's cumulative byte offset from its
    /// parent chain via <see cref="InspectorFieldOffsetResolver"/>, then
    /// opens the FieldAcrossInstancesWindow seeded with the inspector's
    /// target class spec, the field name + type, and the cumulative offset.
    ///
    /// Bails (with the soft reason rendered as a tiny info window) when the
    /// resolver reports the chain isn't supported — typically because the
    /// selection crosses a pointer-traversal field or a collection element,
    /// neither of which has a statically-fixed address offset.
    /// </summary>
    private async void OnListFieldAcrossInstancesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var inspector = vm.Inspector;
        WalkedFieldNode? node = inspector.SelectedNode;
        if (node is null) return;
        if (!inspector.HasTarget) return;

        CumulativeOffsetResult resolved = InspectorFieldOffsetResolver.Resolve(node);
        if (!resolved.Supported)
        {
            // Soft surface — write to the inspector's error banner so the
            // user sees the reason without a separate dialog. The banner
            // is the same one Find/Walk errors use.
            inspector.ErrorMessage = $"Reverse lookup: {resolved.Reason}";
            return;
        }

        // Build the modal. The target class spec is the OUTER class the
        // inspector is walking — not the value-struct type that may live
        // inside it. Field type comes from the row itself (e.g.
        // System.Int32 for a primitive, UnityEngine.Vector3 if the user
        // picked the struct row itself — though the most useful case is a
        // primitive leaf).
        var fieldVm = new FieldAcrossInstancesViewModel(
            vm.BackendAdapter, vm.Logger,
            inspector.TargetAssembly!,
            inspector.TargetNamespace,
            inspector.TargetName!,
            fieldName:   node.Name,
            fieldType:   node.Type ?? "System.Object",
            fieldOffset: resolved.Offset);
        var win = new FieldAcrossInstancesWindow(fieldVm);
        await win.ShowDialog(this);
    }

    /// <summary>
    /// Phase 21b — "Write value…" right-click handler. Opens the
    /// <see cref="FieldWriteWindow"/> modal seeded with the selected
    /// Inspector row's field metadata + current display. The VM gates
    /// the Write button on <see cref="InspectorWriteTypeMap"/> support,
    /// so unsupported types (reference / value-struct / collection)
    /// open the modal but show the unsupported banner instead of writing.
    /// On success the VM fires the Written event, which we hook here to
    /// kick a walkInstance refresh so the row's displayed value updates.
    /// </summary>
    private async void OnWriteFieldClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var inspector = vm.Inspector;
        WalkedFieldNode? node = inspector.SelectedNode;
        if (node is null) return;
        if (string.IsNullOrEmpty(node.Address))
        {
            inspector.ErrorMessage = "Write: row has no field address";
            return;
        }

        var fieldVm = new FieldWriteViewModel(
            vm.BackendAdapter, vm.Logger,
            fieldName:   node.Name,
            clrTypeName: node.Type ?? string.Empty,
            declaredIn:  node.DeclaredIn ?? string.Empty,
            fieldAddress: node.Address!,
            currentDisplay: node.DisplayValue ?? string.Empty);

        // Refresh the walked-fields grid on successful write so the
        // displayed value reflects the new bytes. Reusing the existing
        // RefreshWalkCommand keeps us on the same code path as the
        // toolbar button and the auto-refresh loop.
        fieldVm.Written += async (_, _) =>
        {
            try
            {
                await inspector.RefreshWalkCommand.ExecuteAsync(null);
            }
            catch { /* surfaced via inspector.ErrorMessage */ }
        };

        var win = new FieldWriteWindow(fieldVm);
        await win.ShowDialog(this);
    }

    // ----------------------------------------------------------------------
    // Phase 28 — Field → Methods cross-reference (findFieldReferences).
    //
    // Three entry points all route through OpenFieldReferencesAsync — Inspector
    // grid right-click, Symbol Finder result-row right-click (fields only),
    // and Browser Fields tab right-click. Each resolves the field's
    // (assembly, ns, class, name, type, isStatic) tuple from its own source
    // VM and feeds them into one shared modal.
    // ----------------------------------------------------------------------

    /// <summary>Parse a fully-qualified class name into (namespace, simple class).
    /// Handles nested-type "Outer/Inner" by anchoring the last '.' before the
    /// first '/' boundary. Empty namespace returns ("", className).</summary>
    private static (string ns, string simpleClass) ParseClassFqn(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return (string.Empty, string.Empty);
        int firstSlash = fqn.IndexOf('/');
        int lastDot = firstSlash < 0
            ? fqn.LastIndexOf('.')
            : fqn.LastIndexOf('.', firstSlash);
        if (lastDot < 0) return (string.Empty, fqn);
        return (fqn.Substring(0, lastDot), fqn.Substring(lastDot + 1));
    }

    private async Task OpenFieldReferencesAsync(
        string assembly, string? ns, string className,
        string fieldName, string? fieldType, bool isStatic)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrEmpty(assembly) ||
            string.IsNullOrEmpty(className) ||
            string.IsNullOrEmpty(fieldName))
        {
            return;
        }
        var refsVm = new FieldReferencesViewModel(
            vm.BackendAdapter, vm.Logger,
            assembly, ns, className, fieldName, fieldType, isStatic);
        var win = new FieldReferencesWindow(refsVm);
        await win.ShowDialog(this);
    }

    /// <summary>Phase 28 — Inspector right-click. Uses the row's DeclaredIn
    /// to identify the declaring class (which may differ from the inspector's
    /// TargetClass when the field is inherited from a parent), and the
    /// inspector's TargetAssembly. Inherited fields whose declaring class
    /// lives in a *different* assembly (e.g. UnityEngine.MonoBehaviour from
    /// UnityEngine.CoreModule) will surface a "class not found" error from
    /// the DLL — out of scope for v1.</summary>
    private async void OnFindFieldReferencesFromInspectorClicked(
        object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var inspector = vm.Inspector;
        WalkedFieldNode? node = inspector.SelectedNode;
        if (node is null || !inspector.HasTarget) return;
        if (string.IsNullOrEmpty(node.Name)) return;

        // Prefer the field's DeclaredIn class — inherited fields belong on
        // the parent class, not the target. Fall back to the inspector's
        // TargetClass when DeclaredIn is empty (synthetic / array-slot rows).
        //
        // Caveat: walkInstance's `from` field returns `res.info.self.name`
        // (the SIMPLE class name, without namespace) — so a field declared
        // on `Mortal.Core.GameStat` shows DeclaredIn=`"GameStat"`, not the
        // FQN. When ParseClassFqn finds no namespace dot we fall back to
        // the inspector's TargetNamespace; this is correct for the common
        // case (field declared on target class or on a parent in the same
        // namespace) and surfaces a clean "class not found" error for the
        // cross-namespace inheritance edge case (e.g. UnityEngine.MonoBehaviour
        // parent in Assembly-CSharp inspector) which is v1-out-of-scope.
        string declaredIn = node.DeclaredIn ?? string.Empty;
        string ns; string clsName;
        if (!string.IsNullOrEmpty(declaredIn))
        {
            (ns, clsName) = ParseClassFqn(declaredIn);
            if (string.IsNullOrEmpty(ns))
            {
                ns = inspector.TargetNamespace ?? string.Empty;
            }
        }
        else
        {
            ns = inspector.TargetNamespace ?? string.Empty;
            clsName = inspector.TargetName ?? string.Empty;
        }

        // Flags isn't on the row — we can't tell static vs instance from the
        // walked-node directly. Pass `false` and let the DLL surface the
        // real status in the response header (the modal renders it from
        // result.Field.IsStatic).
        await OpenFieldReferencesAsync(
            assembly:   inspector.TargetAssembly ?? string.Empty,
            ns:         ns,
            className:  clsName,
            fieldName:  node.Name,
            fieldType:  node.Type,
            isStatic:   false);
    }

    /// <summary>Phase 28 — Symbol Finder right-click. Only proceeds when the
    /// right-clicked row is a field; method rows quietly no-op.</summary>
    private async void OnFindFieldReferencesFromSymbolFinderClicked(
        object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SymbolFinderRow>(
            sender, this.FindControl<DataGrid>("SymbolFinderResultsGrid"));
        if (row is null) return;
        if (!string.Equals(row.Kind, "field", System.StringComparison.OrdinalIgnoreCase))
        {
            // Method rows can't drive findFieldReferences; surface nothing
            // (the menu item is shared across kinds for menu-layout reasons).
            return;
        }
        await OpenFieldReferencesAsync(
            assembly:   row.Assembly,
            ns:         row.Namespace,
            className:  row.Class,
            fieldName:  row.Member,
            fieldType:  row.Type,
            isStatic:   row.IsStatic);
    }

    /// <summary>Phase 28 — Browser Fields tab right-click. Uses the row's
    /// DeclaredIn for the declaring class FQN and the Browser's current
    /// assembly selection.</summary>
    private async void OnFindFieldReferencesFromFieldsTabClicked(
        object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var browser = vm.Browser;
        var row = ResolveDataGridRow<FieldRow>(
            sender, this.FindControl<DataGrid>("BrowserFieldsDataGrid"));
        if (row is null) return;

        string declaredIn = row.DeclaredIn ?? string.Empty;
        string ns; string clsName;
        if (!string.IsNullOrEmpty(declaredIn))
        {
            (ns, clsName) = ParseClassFqn(declaredIn);
        }
        else
        {
            ns      = browser.SelectedClass?.Namespace ?? string.Empty;
            clsName = browser.SelectedClass?.Name      ?? string.Empty;
        }

        await OpenFieldReferencesAsync(
            assembly:   browser.SelectedAssembly?.Name ?? string.Empty,
            ns:         ns,
            className:  clsName,
            fieldName:  row.Name,
            fieldType:  row.Type,
            isStatic:   row.IsStatic);
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

    // ----------------------------------------------------------------------
    // Phase 25 — Symbol Finder tab handlers.
    // ----------------------------------------------------------------------

    /// <summary>
    /// Enter key in the pattern textbox kicks the Search command — saves
    /// the user from having to mouse over to the button.
    /// </summary>
    private void OnSymbolFinderPatternKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.SymbolFinder.SearchCommand.CanExecute(null))
        {
            vm.SymbolFinder.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSymbolFinderResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("SymbolFinderResultsGrid");
        if (grid?.SelectedItem is SymbolFinderRow row)
            vm.SymbolFinder.Activate(row);
    }

    // -----------------------------------------------------------------------
    // Phase 27d — Value Search row activation + context menu.
    // -----------------------------------------------------------------------

    private void OnValueSearchRowActivated(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("ValueSearchGrid");
        if (grid?.SelectedItem is ValueSearchRow row)
            vm.ValueSearch.Activate(row);
    }

    private void OnValueSearchInspectInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<ValueSearchRow>(sender, this.FindControl<DataGrid>("ValueSearchGrid"));
        if (row is not null) vm.ValueSearch.Activate(row);
    }

    private async void OnValueSearchCopyInstanceAddr(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<ValueSearchRow>(sender, this.FindControl<DataGrid>("ValueSearchGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.InstanceAddr);
    }

    private async void OnValueSearchCopyFieldAddr(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<ValueSearchRow>(sender, this.FindControl<DataGrid>("ValueSearchGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.Address);
    }

    private async void OnValueSearchCopyClass(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<ValueSearchRow>(sender, this.FindControl<DataGrid>("ValueSearchGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.ClassDisplay);
    }

    // -----------------------------------------------------------------------
    // Phase 29b — Snapshot diff row activation + context menu.
    // -----------------------------------------------------------------------

    private void OnSnapshotRowActivated(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("SnapshotDiffGrid");
        if (grid?.SelectedItem is SnapshotDiffRow row)
            vm.Snapshot.NavigateToInspector(row);
    }

    private void OnSnapshotInspectInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is not null) vm.Snapshot.NavigateToInspector(row);
    }

    private void OnSnapshotAddressLookup(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is not null) vm.Snapshot.NavigateToAddressLookup(row);
    }

    private async void OnSnapshotCopyInstanceAddr(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.InstanceAddress);
    }

    private async void OnSnapshotCopyClass(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.ClassDisplay);
    }

    private async void OnSnapshotCopyField(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.Field);
    }

    private void OnSnapshotExcludeFieldName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is null) return;
        vm.Snapshot.ToggleFieldNameExclusion(row.Field);
    }

    private void OnSnapshotFieldStatActivated(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("SnapshotFieldStatsGrid");
        if (grid?.SelectedItem is FieldNameStat stat)
            vm.Snapshot.ToggleFieldNameExclusion(stat.FieldName);
    }

    private void OnSymbolFinderNavigateMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<SymbolFinderRow>(sender, this.FindControl<DataGrid>("SymbolFinderResultsGrid"));
        if (row is null) return;
        vm.SymbolFinder.Activate(row);
    }

    private async void OnSymbolFinderCopyClassMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SymbolFinderRow>(sender, this.FindControl<DataGrid>("SymbolFinderResultsGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.ClassDisplay).ConfigureAwait(true);
    }

    private async void OnSymbolFinderCopyMemberMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<SymbolFinderRow>(sender, this.FindControl<DataGrid>("SymbolFinderResultsGrid"));
        if (row is null) return;
        await CopyBrowserTextAsync(row.Member).ConfigureAwait(true);
    }

    // -------- Phase 20b — Scene Explorer tab handlers --------------------
    //
    // The Scene TreeView's selected node lives on the VM
    // (Scene.SelectedNode); these handlers read it + dispatch the
    // requested action. The Inspect menu raises the VM's
    // NavigateToInspectorRequested event, which MainWindowViewModel
    // routes into the cross-tab handoff (Phase 20c).

    private void OnScenePatternKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Scene.SearchCommand.CanExecute(null))
        {
            vm.Scene.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private SceneNode? ResolveSelectedSceneNode()
    {
        var tv = this.FindControl<TreeView>("SceneTreeView");
        return tv?.SelectedItem as SceneNode;
    }

    private void OnSceneInspectMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        SceneNode? node = ResolveSelectedSceneNode();
        if (node is null) return;
        vm.Scene.NavigateToInspector(node);
    }

    private async void OnSceneCopyAddressMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SceneNode? node = ResolveSelectedSceneNode();
        if (node is null) return;
        await CopyBrowserTextAsync(node.Address).ConfigureAwait(true);
    }

    private async void OnSceneCopyNameMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SceneNode? node = ResolveSelectedSceneNode();
        if (node is null) return;
        await CopyBrowserTextAsync(node.Name).ConfigureAwait(true);
    }
}
