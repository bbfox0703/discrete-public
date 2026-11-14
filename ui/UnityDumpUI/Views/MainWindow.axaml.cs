using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
        vm.ClassPivot.CopyToClipboardAsync    = CopyToClipboardAsync;
        vm.Teleport.CopyToClipboardAsync      = CopyToClipboardAsync;
        vm.Teleport.HotkeyService             = new UnityDumpUI.Services.WindowsGlobalHotkeyService();
        vm.Teleport.HotkeyStore               = new UnityDumpUI.Services.TeleportHotkeyStore();
        vm.Teleport.LoadHotkeys();
        vm.ShowProxyDeploy                    = ShowProxyDeployWindow;
        DataContext = vm;

        // Teleport hotkey rebinding: capture the key press while a row is in
        // "Press keys…" mode BEFORE any focused child (e.g. a TextBox) consumes
        // it, via a tunnelling KeyDown handler on the window.
        AddHandler(KeyDownEvent, OnTeleportHotkeyCapture, RoutingStrategies.Tunnel);

        // Cancel an armed capture if the window loses focus (alt-tab to the game
        // — otherwise the next in-game keystroke would be swallowed and bound) or
        // the user clicks away from the Set/Cancel button.
        Deactivated += (_, _) => (DataContext as MainWindowViewModel)?.Teleport.CancelActiveCapture();
        AddHandler(PointerPressedEvent, OnTeleportCaptureClickAway, RoutingStrategies.Tunnel);

        // The Teleport tab's auto-BringIntoView suppression is wired from the
        // content's OWN Loaded event (OnTeleportContentLoaded), NOT here. A
        // direct `TeleportContent.AddHandler(...)` in the ctor NREs at startup
        // under Native AOT: the compiled x:Name field for a control nested in a
        // non-selected TabItem is left null by the trimmer. See that handler.
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
        // Normal → Maximized → Normal, Avalonia's auto-restore math can
        // land the window straddling the monitor boundary instead of
        // returning to its original position/size (worst on dual-monitor
        // setups with mismatched DPI scales). We snapshot the last known
        // Normal bounds ourselves and force-restore them on the way back.
        //
        // The catch: on Windows the property-change ORDER during a
        // maximize is (Width/Height first, WindowState second). Capturing
        // bounds synchronously inside the WindowState handler therefore
        // reads the ALREADY-maximized dimensions. Instead we keep the
        // snapshot continuously fresh while the window is Normal, via the
        // Width/Height/Position change handlers, with a deferred commit
        // (see ScheduleSnapshotCommit) that re-checks WindowState so a
        // concurrent maximize in the same Win32 message doesn't poison it.
        PositionChanged += OnWindowPositionChanged;
        PropertyChanged += OnWindowPropertyChanged;
        // Close the IME whenever focus enters a text field, app-wide. Every input
        // field takes ASCII (hex addresses, AOB patterns, numbers, class / field
        // names, file paths), so a CJK IME left in composition mode only gets in
        // the way. Dialogs get this via DialogWindowGeometry; the main window
        // wires it here since it manages its own geometry. See ImeSoftDisable.
        ImeSoftDisable.Attach(this);
        // Block a still-queued Background restore re-apply from touching the
        // window after it has closed (the posted lambda reads _closed first).
        Closed += (_, _) => _closed = true;
        // Seed the snapshot with the XAML-declared defaults so the very
        // first restore (without a prior maximize) still has sane values.
        _normalWidth     = Width;
        _normalHeight    = Height;
        _pendingWidth    = Width;
        _pendingHeight   = Height;
        _pendingPosition = Position;

        // Remember the XAML default size (1280x780) as the reset fallback for
        // when a restored placement turns out to be off every current monitor.
        _defaultWidth  = Width;
        _defaultHeight = Height;
    }

    /// <summary>
    /// Position + size of the window the last time it was confirmed to be
    /// in <see cref="WindowState.Normal"/>. Re-applied when the window
    /// returns to Normal so a Maximized → Normal restore lands back on the
    /// monitor it started from at its original size.
    /// </summary>
    private PixelPoint? _normalPosition;
    private double      _normalWidth;
    private double      _normalHeight;
    private WindowState _previousWindowState = WindowState.Normal;

    // Last "non-minimized" state. _previousWindowState is overwritten on every
    // transition, so after Maximized → Minimized it is already Minimized and
    // can't tell us the window was minimized FROM a maximized state. Track that
    // separately here so OnClosingSaveState can persist Maximized=true when the
    // window is closed while minimized-from-maximized.
    private WindowState _lastNonMinimizedState = WindowState.Normal;

    // Deferred-commit snapshot state. On Windows + Avalonia 12 the
    // property-change order during a maximize is (Width/Height first,
    // WindowState second), so reading WindowState inside the Width/Height
    // handler still sees Normal when the values are already the maximized
    // dimensions. Committing synchronously at that point poisons the
    // snapshot — the next restore then lands at near-maximized size. We
    // stash into _pending* and commit one dispatcher tick later at
    // Background priority; the commit re-checks WindowState and abandons
    // when it has flipped to non-Normal.
    private double      _pendingWidth;
    private double      _pendingHeight;
    private PixelPoint  _pendingPosition;
    private bool        _snapshotCommitScheduled;

    // Set once the window has closed so a deferred (Background-posted) restore re-apply
    // that fires afterwards can't set Position/Width/Height on a torn-down window.
    private bool        _closed;

    // ── Cross-restart placement persistence (AttachWindowState) ──────────────
    // Restores last-session position / size / maximized state, validated against
    // the monitors present THIS session (a window saved on a now-absent second
    // monitor is reset to a centered default). Reuses the normal-vs-maximized
    // snapshot above so un-maximizing a restored window lands on the right rect.
    private Services.WindowStateStore? _stateStore;
    private bool   _restorePending;     // a saved record was applied; validate on Opened
    private double _defaultWidth;       // XAML default, used when resetting off-screen
    private double _defaultHeight;

    /// <summary>
    /// Wire up cross-restart window placement. Called once by App right after
    /// construction and BEFORE the window is shown, so saved geometry is applied
    /// without a visible jump. Validation against the monitors present THIS
    /// session happens on Opened (Screens is only reliable once the platform
    /// window exists).
    /// </summary>
    public void AttachWindowState(Services.WindowStateStore store)
    {
        _stateStore = store;
        Closing += OnClosingSaveState;

        if (store.Load() is not { } r)
        {
            return; // first run / corrupt → keep XAML defaults + OS placement
        }

        // Clamp restored size to at least the window minimum so a tiny saved
        // size can't make the window unusable.
        double w = Math.Max(r.Width, MinWidth);
        double h = Math.Max(r.Height, MinHeight);
        var pos = new PixelPoint(r.X, r.Y);

        // Take placement over from the OS and seed BOTH the live geometry and the
        // normal-vs-maximized snapshot, so a later un-maximize restores to exactly
        // this rect.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = pos;
        Width  = w;
        Height = h;
        _normalPosition = pos;
        _normalWidth    = w;
        _normalHeight   = h;
        _pendingPosition = pos;
        _pendingWidth    = w;
        _pendingHeight   = h;

        _restorePending = true;
        if (r.Maximized)
        {
            // Show maximized immediately; the snapshot above is the restore-down
            // target. Avalonia doesn't reliably keep the normal rect across a
            // maximize, and a born-maximized window has no valid OS restore
            // rectangle — so the first un-maximize is corrected by the deferred
            // ReapplyNormalSnapshot, which consumes this seeded snapshot.
            WindowState = WindowState.Maximized;
        }

        Opened += OnOpenedValidatePlacement;
    }

    /// <summary>
    /// Once the platform window exists (Screens reliable), confirm the restored
    /// NORMAL rect is reachable on some current monitor. If not — the monitor was
    /// removed or the resolution shrank — reset to a default size centered on the
    /// primary screen. Runs once.
    /// </summary>
    private void OnOpenedValidatePlacement(object? sender, EventArgs e)
    {
        Opened -= OnOpenedValidatePlacement;
        if (!_restorePending) return;
        _restorePending = false;

        var screens = CurrentScreenWorkingAreas();
        if (screens.Count == 0) return; // no screen info — leave as restored

        // RenderScaling reflects the monitor the window is CURRENTLY on; it is
        // fine for the visibility tolerance check (the restored rect is on that
        // monitor), but must NOT be used to convert the default size for primary-
        // screen centering — see ResetToDefaultPlacement / PrimaryScaling().
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int rx = _normalPosition?.X ?? 0;
        int ry = _normalPosition?.Y ?? 0;
        int rw = (int)Math.Round(_normalWidth  * scale);
        int rh = (int)Math.Round(_normalHeight * scale);

        if (Services.WindowPlacement.IsVisibleEnough(rx, ry, rw, rh, screens))
            return; // restored placement is reachable — keep it

        ResetToDefaultPlacement();
    }

    /// <summary>Drop to a default-size window centered on the primary monitor.</summary>
    private void ResetToDefaultPlacement()
    {
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        Width  = _defaultWidth;
        Height = _defaultHeight;

        // Convert the default DIP size to physical px using the PRIMARY screen's
        // own scaling — NOT the window's current RenderScaling. On a mixed-DPI
        // multi-monitor setup the two differ, and using the current monitor's
        // scale offsets the window from center (can push the title bar off the
        // working area).
        var primary = PrimaryWorkingArea();
        double primaryScale = PrimaryScaling();
        int pw = (int)Math.Round(_defaultWidth  * primaryScale);
        int ph = (int)Math.Round(_defaultHeight * primaryScale);
        var (cx, cy) = Services.WindowPlacement.CenterIn(primary, pw, ph);
        var pos = new PixelPoint(cx, cy);
        Position = pos;

        _normalPosition  = pos;
        _normalWidth     = _defaultWidth;
        _normalHeight    = _defaultHeight;
        _pendingPosition = pos;
        _pendingWidth    = _defaultWidth;
        _pendingHeight   = _defaultHeight;
    }

    /// <summary>Persist the current placement on close (geometry still valid).</summary>
    private void OnClosingSaveState(object? sender, WindowClosingEventArgs e)
    {
        if (_stateStore is null) return;

        bool maximized = WindowState == WindowState.Maximized
            || (WindowState == WindowState.Minimized && _lastNonMinimizedState == WindowState.Maximized);

        int x, y;
        double w, h;
        if (WindowState == WindowState.Normal)
        {
            // Live geometry is authoritative when Normal.
            x = Position.X;
            y = Position.Y;
            w = Width;
            h = Height;
        }
        else
        {
            // Maximized / minimized report sentinel geometry — use the tracked
            // normal snapshot so we persist the real restore-down rect.
            var p = _normalPosition ?? Position;
            x = p.X;
            y = p.Y;
            w = _normalWidth;
            h = _normalHeight;
        }

        _stateStore.Save(new Services.WindowStateRecord(x, y, w, h, maximized));
    }

    /// <summary>
    /// True when (<paramref name="pos"/>, the current normal size) shows a grabbable chunk
    /// on some current monitor — the position twin of <see cref="CommitSnapshot"/>'s size
    /// &gt;0 guard. Stops a transition-artifact / off-screen top-left from latching into the
    /// snapshot (and being re-applied on the next restore — the "jumps to 0,0" class). No
    /// screen info yet (pre-show) => accept, so the very first open-time stash isn't dropped.
    /// </summary>
    private bool IsSnapshotPositionAcceptable(PixelPoint pos)
    {
        var screens = CurrentScreenWorkingAreas();
        if (screens.Count == 0) return true;
        return Services.WindowPlacement.IsVisibleEnough(
            pos.X, pos.Y,
            (int)Math.Round(_pendingWidth), (int)Math.Round(_pendingHeight),
            screens);
    }

    /// <summary>Current monitors' working areas as plain physical-pixel rects.</summary>
    private List<(int X, int Y, int W, int H)> CurrentScreenWorkingAreas()
    {
        var list = new List<(int, int, int, int)>();
        var all = Screens?.All;
        if (all == null) return list;
        foreach (var s in all)
        {
            var wa = s.WorkingArea;
            list.Add((wa.X, wa.Y, wa.Width, wa.Height));
        }
        return list;
    }

    /// <summary>Primary monitor working area (fallback: first screen, then 1080p).</summary>
    private (int X, int Y, int W, int H) PrimaryWorkingArea()
    {
        var primary = Screens?.Primary;
        if (primary == null)
        {
            var all = Screens?.All;
            if (all != null && all.Count > 0) primary = all[0];
        }
        if (primary != null)
        {
            var wa = primary.WorkingArea;
            return (wa.X, wa.Y, wa.Width, wa.Height);
        }
        return (0, 0, 1920, 1080);
    }

    /// <summary>
    /// Primary monitor's scaling (fallback: first screen, then 1.0). Used for the
    /// DIP→physical-px conversion when centering on the primary screen, so a
    /// mixed-DPI second monitor's RenderScaling can't skew the centered result.
    /// </summary>
    private double PrimaryScaling()
    {
        var primary = Screens?.Primary;
        if (primary == null)
        {
            var all = Screens?.All;
            if (all != null && all.Count > 0) primary = all[0];
        }
        double s = primary?.Scaling ?? 1.0;
        return s > 0 ? s : 1.0;
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        // Reject an off-screen / maximized-origin top-left so a transition transient can't
        // poison the snapshot (the position twin of the size >0 guard in CommitSnapshot).
        if (WindowState == WindowState.Normal && IsSnapshotPositionAcceptable(Position))
        {
            _pendingPosition = Position;
            ScheduleSnapshotCommit();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            var newState = (WindowState)e.NewValue!;
            HandleWindowStateTransition(_previousWindowState, newState);
            _previousWindowState = newState;
            if (newState != WindowState.Minimized)
                _lastNonMinimizedState = newState;
        }
        else if (e.Property == WidthProperty || e.Property == HeightProperty)
        {
            // Stash, but commit later (see the field comments). Reading
            // WindowState here is unreliable on the to-maximized
            // transition.
            if (WindowState == WindowState.Normal)
            {
                _pendingWidth  = Width;
                _pendingHeight = Height;
                ScheduleSnapshotCommit();
            }
        }
    }

    /// <summary>
    /// Queue a deferred snapshot commit on the dispatcher at Background
    /// priority. Coalesced: scheduling while a commit is already pending
    /// is a no-op.
    /// </summary>
    private void ScheduleSnapshotCommit()
    {
        if (_snapshotCommitScheduled) return;
        _snapshotCommitScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            CommitSnapshot,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Apply the pending snapshot — but only if the window is still in
    /// <see cref="WindowState.Normal"/> at commit time. If a WindowState
    /// change snuck in during the same dispatcher tick (the bug we're
    /// guarding against), the state will have flipped by now and the
    /// pending values are the maximized dimensions we don't want.
    /// </summary>
    private void CommitSnapshot()
    {
        _snapshotCommitScheduled = false;
        if (WindowState != WindowState.Normal) return;
        if (_pendingWidth  > 0) _normalWidth  = _pendingWidth;
        if (_pendingHeight > 0) _normalHeight = _pendingHeight;
        // Position promotion is gated like the stash (was unconditional): an off-screen /
        // maximized-origin top-left must never latch into _normalPosition, or the next
        // restore re-applies it (the "second restore jumps to 0,0" bug).
        if (IsSnapshotPositionAcceptable(_pendingPosition))
        {
            _normalPosition = _pendingPosition;
        }
    }

    private void HandleWindowStateTransition(WindowState oldState, WindowState newState)
    {
        // Leaving Normal: the snapshot was already kept fresh by the
        // property-change handlers above. Nothing to do here.
        if (oldState == WindowState.Normal && newState != WindowState.Normal)
        {
            return;
        }
        // Returning to Normal: re-apply the snapshot. Without this, restoring
        // from Maximized on a multi-monitor setup can land the window
        // straddling both screens or at the wrong position.
        //
        // DEFER the re-apply to a Background dispatcher tick rather than setting
        // Position/Width/Height synchronously inside the WindowState change. The
        // synchronous set fought the OS mid-un-maximize: it (a) emitted a maximized-origin
        // (~0,0) position transient that latched into the snapshot and re-surfaced as the
        // window jumping to 0,0 on the SECOND restore, and (b) lost the placement race
        // against a window BORN maximized (cross-restart), whose OS restore rectangle is a
        // garbage default — leaving the window off-screen. Background priority runs FIFO
        // after the current Win32 message burst, so the re-apply lands AFTER any OS
        // placement and OVERRIDES it; re-seeding _pending* stops the events it triggers
        // from being read as a fresh user move. The position acceptability guard
        // (IsSnapshotPositionAcceptable) backstops the snapshot should any off-screen
        // transient still be stashed.
        if (newState == WindowState.Normal && oldState != WindowState.Normal)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                ReapplyNormalSnapshot,
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Force the live window back onto the saved normal snapshot. Posted at Background
    /// priority from the Maximized/Minimized → Normal transition. Gated so it never runs
    /// during startup placement restore (<see cref="_restorePending"/> — that path is the
    /// sole authority until validated) nor on a closed window.
    /// </summary>
    private void ReapplyNormalSnapshot()
    {
        if (_closed || _restorePending) return;
        if (WindowState != WindowState.Normal) return;
        // Size before position so final DPI scaling resolves against the target monitor.
        if (_normalWidth  > 0) Width  = _normalWidth;
        if (_normalHeight > 0) Height = _normalHeight;
        if (_normalPosition is { } pos) Position = pos;
        // Re-seed the stash so the Position/Size events this re-apply triggers aren't
        // mis-read as a user move (which would thrash, or re-poison, the snapshot).
        _pendingPosition = _normalPosition ?? _pendingPosition;
        _pendingWidth    = _normalWidth;
        _pendingHeight   = _normalHeight;
    }

    private void WireSortComparersForAllGrids()
    {
        this.FindControl<DataGrid>("BrowserFieldsDataGrid")?
            .WireSortComparers(BrowserFieldsSortComparers);
        // NOTE: the Inspector FieldsDataGrid is deliberately NOT wired here.
        // It's an expand/collapse tree; a flat CustomSortComparer would tear
        // children away from their parent. It uses the tree-aware
        // LiveInspectionViewModel.SortTree via OnInspectorFieldsSorting
        // instead (Phase 29e-8). It's the one grid on the Sorting-event path.
        this.FindControl<DataGrid>("MethodsDataGrid")?
            .WireSortComparers(MethodsSortComparers);
        // Phase 29b — wire the Snapshot diff grid. Without this the
        // header-click sort silently no-ops under AOT
        // (docs/aot-pitfalls.md §4.5 "three-layer trap").
        this.FindControl<DataGrid>("SnapshotDiffGrid")?
            .WireSortComparers(SnapshotDiffSortComparers);
        // Phase 29e-9 — fold the rest of the sortable grids onto the same
        // CustomSortComparer mechanism (keeps the native arrow glyph +
        // direction toggle; AOT-safe). Replaces the parallel Sorting-event
        // approach 29e-5/6 briefly used.
        this.FindControl<DataGrid>("ValueSearchGrid")?
            .WireSortComparers(ValueSearchSortComparers);
        this.FindControl<DataGrid>("SymbolFinderResultsGrid")?
            .WireSortComparers(SymbolFinderSortComparers);
        this.FindControl<DataGrid>("SnapshotFieldStatsGrid")?
            .WireSortComparers(SnapshotFieldStatsSortComparers);
        // The Pivot shortlist / field-score / element-field / locator / interest
        // grids moved into their own dialogs (Class Pivot overhaul Wave B); each
        // dialog wires its own comparers in its Loaded handler. Only the Results
        // grid stays on the tab here.
        this.FindControl<DataGrid>("PivotResultsGrid")?
            .WireSortComparers(PivotResultsSortComparers);
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

    // Phase 29e-9 — comparer dicts for the grids folded onto the
    // CustomSortComparer mechanism. Keys match each column's SortMemberPath.
    private static readonly IReadOnlyDictionary<string, IComparer> ValueSearchSortComparers =
        new Dictionary<string, IComparer>
        {
            ["ClassDisplay"] = DataGridSortComparers.Ordinal<ValueSearchRow>(r => r.ClassDisplay),
            ["Field"]        = DataGridSortComparers.Ordinal<ValueSearchRow>(r => r.Field),
            ["FieldType"]    = DataGridSortComparers.Ordinal<ValueSearchRow>(r => r.FieldType),
            ["FieldOffset"]  = DataGridSortComparers.Number <ValueSearchRow>(r => r.FieldOffset),
            ["CurrentValue"] = DataGridSortComparers.Ordinal<ValueSearchRow>(r => r.CurrentValue),
            ["InstanceAddr"] = DataGridSortComparers.Hex    <ValueSearchRow>(r => r.InstanceAddr),
            ["Address"]      = DataGridSortComparers.Hex    <ValueSearchRow>(r => r.Address),
            ["FieldKind"]    = DataGridSortComparers.Ordinal<ValueSearchRow>(r => r.FieldKind),
        };

    private static readonly IReadOnlyDictionary<string, IComparer> SymbolFinderSortComparers =
        new Dictionary<string, IComparer>
        {
            ["KindLabel"]    = DataGridSortComparers.Ordinal<SymbolFinderRow>(r => r.KindLabel),
            ["Assembly"]     = DataGridSortComparers.Ordinal<SymbolFinderRow>(r => r.Assembly),
            ["ClassDisplay"] = DataGridSortComparers.Ordinal<SymbolFinderRow>(r => r.ClassDisplay),
            ["Member"]       = DataGridSortComparers.Ordinal<SymbolFinderRow>(r => r.Member),
            ["Type"]         = DataGridSortComparers.Ordinal<SymbolFinderRow>(r => r.Type),
            ["OffsetLabel"]  = DataGridSortComparers.Hex    <SymbolFinderRow>(r => r.OffsetLabel),
            ["Address"]      = DataGridSortComparers.Hex    <SymbolFinderRow>(r => r.Address),
        };

    private static readonly IReadOnlyDictionary<string, IComparer> SnapshotFieldStatsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["FieldName"] = DataGridSortComparers.Ordinal<FieldNameStat>(r => r.FieldName),
            ["Count"]     = DataGridSortComparers.Number <FieldNameStat>(r => r.Count),
        };

    // Wave B: the shortlist / field-score / interest / locator comparers moved
    // to their dialogs (each wires its own). Only the Results-grid comparers
    // remain on the tab.
    private static readonly IReadOnlyDictionary<string, IComparer> PivotResultsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["HasCollision"]    = DataGridSortComparers.Bool   <PivotResultRow>(r => r.HasCollision),
            ["KeyTupleDisplay"] = DataGridSortComparers.Ordinal<PivotResultRow>(r => r.KeyTupleDisplay),
        };

    /// <summary>Last expanded widths for the Assemblies / Classes columns.
    /// Captured immediately before collapse so the pane restores to the
    /// width the user actually had (which may differ from the XAML default
    /// thanks to the GridSplitter).</summary>
    private GridLength _assembliesPaneExpandedWidth = new GridLength(240);
    private GridLength _classesPaneExpandedWidth    = new GridLength(280);

    // Tunnelling KeyDown: while a teleport-hotkey row is capturing, route the
    // press to the VM and swallow it so it doesn't reach a focused TextBox / the
    // command bindings. No-op otherwise.
    private void OnTeleportHotkeyCapture(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm
            && vm.Teleport.IsCapturingHotkey
            && vm.Teleport.ApplyCapturedKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    // While a hotkey row is capturing, a pointer press anywhere EXCEPT on the
    // Set/Cancel button (whose Click toggles capture) cancels the capture — so a
    // click into a textbox can't leave the capture armed to eat the next key.
    private void OnTeleportCaptureClickAway(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Teleport.IsCapturingHotkey) return;
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
            vm.Teleport.CancelActiveCapture();
    }

    private bool _teleportBringIntoViewWired;

    /// <summary>
    /// Suppress the Teleport tab's auto-BringIntoView. When a button / textbox
    /// on the tab gains focus (a click, or focus moving on reconnect), the
    /// enclosing ScrollViewer scrolls it into view and the long page "jumps",
    /// sometimes out from under the cursor. Swallowing RequestBringIntoView on
    /// the tab content — before it bubbles to the ScrollViewer — keeps the
    /// scroll position put.
    ///
    /// Wired from the content's own <c>Loaded</c> event (via <paramref name="sender"/>),
    /// NOT the ctor. The x:Name field for a control nested in a non-selected
    /// TabItem is null under Native AOT — the trimmer drops the compiled field
    /// assignment — so a direct <c>TeleportContent.AddHandler(...)</c> in the
    /// ctor NRE-crashes startup. Taking the instance from <paramref name="sender"/>
    /// sidesteps name resolution entirely (AOT-safe). Guarded so a tab
    /// re-attach (switch away and back) doesn't stack duplicate handlers.
    /// </summary>
    private void OnTeleportContentLoaded(object? sender, RoutedEventArgs e)
    {
        if (_teleportBringIntoViewWired || sender is not Control content) return;
        _teleportBringIntoViewWired = true;
        content.AddHandler(RequestBringIntoViewEvent, (_, ev) => ev.Handled = true);
    }

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
        // SetDataAsync can throw when the clipboard is locked by another process
        // (clipboard managers, RDP). Swallow — a failed copy is non-destructive
        // and retryable; letting it escape an async void handler crashes the app.
        try { await clipboard.SetDataAsync(transfer); }
        catch { /* clipboard busy/locked — ignore */ }
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

    // ── Phase 29e-2 Class Pivot tab — context menu + DoubleTapped ────

    private UnityDumpUI.ViewModels.PivotResultRow? GetSelectedPivotRow()
    {
        var grid = this.FindControl<Avalonia.Controls.DataGrid>("PivotResultsGrid");
        return grid?.SelectedItem as UnityDumpUI.ViewModels.PivotResultRow;
    }

    private void OnPivotResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Avalonia.Controls.DataGrid grid) return;
        if (grid.SelectedItem is not UnityDumpUI.ViewModels.PivotResultRow row) return;
        vm.ClassPivot.RequestInspectInstance(row);
    }

    private void OnPivotInspectInstanceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = GetSelectedPivotRow();
        if (row is null) return;
        vm.ClassPivot.RequestInspectInstance(row);
    }

    private async void OnPivotCopyKeyTupleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = GetSelectedPivotRow();
        if (row is null) return;
        await vm.ClassPivot.CopyKeyTupleAsync(row).ConfigureAwait(true);
    }

    private async void OnPivotCopyValueSequenceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = GetSelectedPivotRow();
        if (row is null) return;
        await vm.ClassPivot.CopyValueSequenceAsync(row).ConfigureAwait(true);
    }

    private async void OnPivotCopyAddressLatestClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = GetSelectedPivotRow();
        if (row is null) return;
        await vm.ClassPivot.CopyAddressLatestAsync(row).ConfigureAwait(true);
    }

    // ── Class Pivot UI/UX overhaul Wave B — open each mode as a dialog ──
    // Every dialog binds to the SHARED MainWindowViewModel, so the work it does
    // (populating ClassPivot.Results, filling ClassFqn/key/value, etc.) is
    // reflected in the tab's Results grid when the dialog closes.
    private async void OnPivotOpenSnapshotsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotSnapshotsWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenQueryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotQueryWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenFindByValueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotFindByValueWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenBrowseClassesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotBrowseClassesWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenLikelyStateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotLikelyStateWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenPickFieldsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotPickFieldsWindow(vm).ShowDialog(this);
    }

    private async void OnPivotOpenArrayClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await new PivotArrayWindow(vm).ShowDialog(this);
    }

    // ── Phase 29e-8/9: Inspector tree-aware sort ────────────────────────
    //
    // Every flat grid uses the established CustomSortComparer mechanism
    // (DataGridSortComparers + WireSortComparers — keeps the native arrow
    // glyph + direction toggle, AOT-safe). The Inspector grid is the sole
    // exception: it's an expand/collapse tree, so a flat comparer would
    // tear children from their parent. It stays on the Sorting-event path,
    // routing to the tree-aware LiveInspectionViewModel.SortTree. Direction
    // is tracked here (Avalonia 12 has no public DataGridColumn.SortDirection).

    private (string? Member, bool Asc) _inspectorSort;

    private static bool ToggleDir(ref (string? Member, bool Asc) state, string path)
    {
        bool asc = !(string.Equals(state.Member, path, StringComparison.Ordinal) && state.Asc);
        state = (path, asc);
        return asc;
    }

    private void OnInspectorFieldsSorting(object? sender, Avalonia.Controls.DataGridColumnEventArgs e)
    {
        e.Handled = true;   // tree sort handled in the VM; suppress the flat default
        if (DataContext is not MainWindowViewModel vm) return;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;
        vm.Inspector.SortTree(path, ToggleDir(ref _inspectorSort, path));
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
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

    /// <summary>Footer credit click: open the project page in the default
    /// browser. Same shell-execute pattern as the folder buttons.</summary>
    private void OnCopyrightPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = MainWindowViewModel.ProjectUrl,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ErrorMessage = $"Open project page failed: {ex.Message}";
        }
    }

    /// <summary>Open the log root (<c>%LOCALAPPDATA%\Discrete_Dumper\Logs</c>) in
    /// Explorer — the UI's <c>ui\</c> tree plus the DLL's per-PID folders. Created
    /// on demand so the shell call never sees a missing path. Mirrors
    /// <see cref="OnOpenSnapshotDbFolderClicked"/>.</summary>
    private void OnOpenLogFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string folder = UnityDumpUI.Core.Services.FileLogger.LogRootDirectory();
            System.IO.Directory.CreateDirectory(folder);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = folder,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ErrorMessage = $"Open log folder failed: {ex.Message}";
        }
    }

    /// <summary>Phase 29c-7: launch the Settings modal so the user can
    /// change DB directory / AutoSave / AutoPrune threshold. The modal
    /// edits a clone of UserSettings and hands the updated copy back via
    /// ApplySettingsAsync on OK.</summary>
    private async void OnSnapshotSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var draft = vm.Snapshot.Settings.Clone();
            var dialog = new SnapshotSettingsWindow(draft, this.StorageProvider);
            var result = await dialog.ShowDialog<UserSettings?>(this);
            if (result is not null)
            {
                await vm.Snapshot.ApplySettingsAsync(result).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // async void: an escaping exception is unhandled and terminates the
            // process, with no dialog under Native AOT. ApplySettingsAsync can
            // genuinely throw — it moves the DB directory and runs prune/VACUUM,
            // so a locked file, a full disk or a corrupt database all surface
            // here. Report it in the UI instead of taking the app down.
            vm.Snapshot.ErrorMessage = $"Settings could not be applied: {ex.Message}";
        }
    }

    /// <summary>Open the modeless snapshot Capture window. The capture
    /// configuration was moved off the Snapshot tab (2026-06-05) so the result
    /// grid gets its height back. Single-instance: re-focus the existing window
    /// if open. Bound to the SHARED Snapshot VM so the capture's results +
    /// progress flow into the tab's A/B dropdowns.</summary>
    private CaptureWindow? _captureWindow;
    private void OnNewSnapshotClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_captureWindow is not null)
        {
            _captureWindow.Activate();
            return;
        }
        var win = new CaptureWindow(vm.Snapshot);
        win.Closed += (_, _) => _captureWindow = null;
        _captureWindow = win;
        win.Show(this);
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
    /// Phase 18e-B — right-click a Methods row → jump CE's disassembler to
    /// that method's native entry, JIT-compiling on demand for Mono.
    /// IL2CPP rows already carry the AOT entry (<see cref="MethodRow.Address"/>),
    /// so we jump straight there. Mono rows have no address until compiled, so
    /// we issue <c>findMethodInstances</c> — which <c>mono_compile_method</c>s
    /// every overload — and match THIS row by token (the grid lists each
    /// overload as its own row but carries no signature). Abstract /
    /// generic-definition methods have no compiled body; we surface that on
    /// <c>Browser.ErrorMessage</c> instead of jumping nowhere. The resolved
    /// address is session-local on Mono (a fresh JIT address each launch).
    /// </summary>
    private async void OnMethodGotoAddressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var browser = vm.Browser;
        if (browser.SelectedAssembly is null || browser.SelectedClass is null) return;

        MethodRow? row = ResolveMethodRow(sender);
        if (row is null) return;

        var bridge = vm.AobMaker;
        if (bridge is null || !bridge.IsAvailable)
        {
            browser.ErrorMessage = "AOBMaker pipe unavailable — open CE with the AOBMaker plugin loaded, then retry.";
            return;
        }

        string asm = browser.SelectedAssembly.Name;
        string ns  = browser.SelectedClass.Namespace;
        string cls = browser.SelectedClass.Name;

        // IL2CPP rows carry the AOT entry already; Mono (and any IL2CPP row
        // whose AOT entry didn't resolve at browse time) need a JIT/resolve.
        string? addr = row.Address;
        if (string.IsNullOrEmpty(addr))
        {
            UnityDumpUI.Core.Services.FindMethodInstancesResult? probe = null;
            try
            {
                probe = await vm.BackendAdapter
                    .FindMethodInstancesAsync(asm, ns, cls, row.Name, signatureMatch: null,
                                              ct: System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                browser.ErrorMessage = $"JIT/resolve failed: {ex.Message}";
                return;
            }
            if (probe is null || !string.IsNullOrEmpty(probe.Error))
            {
                browser.ErrorMessage = probe?.Error ?? "findMethodInstances returned no result.";
                return;
            }

            // Match the right overload by token; fall back to the sole match
            // when a class has exactly one overload of this name.
            UnityDumpUI.Core.Services.MethodOverloadEntry? match = null;
            foreach (var m in probe.Matches)
                if (m.Token == row.Token) { match = m; break; }
            if (match is null && probe.Matches.Count == 1) match = probe.Matches[0];
            if (match is null)
            {
                browser.ErrorMessage =
                    $"Couldn't identify this overload of {row.Name} ({probe.Matches.Count} overloads) — "
                    + "use “Find overloads…” and jump from there.";
                return;
            }

            addr = match.Address;
            if (string.IsNullOrEmpty(addr))
            {
                browser.ErrorMessage = match.IsAbstract
                    ? $"{row.Name} is abstract — no compiled body to jump to."
                    : $"{row.Name} has no compiled native entry (generic definition or compile failure).";
                return;
            }
        }

        bool ok = await bridge
            .NavigateDisassemblerAsync(addr, System.Threading.CancellationToken.None)
            .ConfigureAwait(true);
        if (ok)
        {
            browser.ErrorMessage = null;
            vm.Logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"goto method address in CE: {ns}.{cls}.{row.Name} @ {addr}");
        }
        else
        {
            browser.ErrorMessage =
                "Couldn't reach CE's disassembler — open CE with the AOBMaker plugin loaded, then retry.";
        }
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
            // Signature-match register actions work on ANY connected Unity
            // target — CE's "Active Mono features" dissector drives the mono_*
            // Lua API on BOTH Mono AND IL2CPP, so this is NOT Mono-only. Enable
            // whenever the backend is a recognised Unity runtime.
            supportsSignatureMatch:
                (vm.Backend2 ?? string.Empty).StartsWith("Mono",   StringComparison.OrdinalIgnoreCase) ||
                (vm.Backend2 ?? string.Empty).StartsWith("IL2CPP", StringComparison.OrdinalIgnoreCase),
            // AOBMaker standalone-app client: when its pipe is up, "Copy: aobscan"
            // / "Push aobscan to CE" route through GenerateAob for a unique AOB.
            aobMakerApp: vm.AobMakerApp,
            // "Copy: register (sig-match)" is gated on the experimental opt-in.
            experimentalEnabled: vm.ExperimentalEnabled);
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

    // Class Pivot overhaul Wave D — "Pivot this class…" from the Browser class
    // list. Lands the class FQN (+ its assembly) into the Class Pivot tab and
    // switches to it, so the user doesn't copy/paste the name.
    private void OnPivotThisClassFromBrowserClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
        vm.PivotForClass(browser.SelectedAssembly?.Name, target.DisplayName);
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
            fieldOffset: resolved.Offset,
            bulkGate:    vm.BulkGate);
        var win = new FieldAcrossInstancesWindow(fieldVm);
        await win.ShowDialog(this);

        // Double-click / "Inspect this instance" in the modal sets InspectRequest
        // then closes — route it back to the Inspector tab (same handoff the
        // Address Lookup tab uses).
        if (win.InspectRequest is { } req)
            vm.InspectInstance(req);
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
            assembly, ns, className, fieldName, fieldType, isStatic,
            bulkGate: vm.BulkGate,
            aobMaker: vm.AobMaker);
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

    private void OnSymbolFinderSendToBrowserMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("SymbolFinderResultsGrid");
        if (grid?.SelectedItem is SymbolFinderRow row)
            vm.SymbolFinder.SendToBrowser(row);
    }

    private void OnStatFindingSendToBrowserMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("StatFindingsGrid");
        if (grid?.SelectedItem is StatFindingRow row)
            vm.SymbolFinder.SendFindingToBrowser(row);
    }

    private void OnMethodFindingSendToBrowserMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("MethodFindingsGrid");
        if (grid?.SelectedItem is MethodFindingRow row)
            vm.SymbolFinder.SendMethodFindingToBrowser(row);
    }

    private void OnRelatedObjectActivated(object? sender, Avalonia.Input.TappedEventArgs e)
        => GoToSelectedRelated();

    private void OnRelatedObjectGoMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => GoToSelectedRelated();

    private void GoToSelectedRelated()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("RelatedObjectsGrid");
        if (grid?.SelectedItem is UnityDumpUI.Core.Services.RelatedObject row)
            vm.Inspector.GoToRelated(row);
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
    // Group scan (multiple-values) — candidate activation + context menu.
    // -----------------------------------------------------------------------
    private void OnGroupCandidateActivated(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<DataGrid>("GroupResultsGrid");
        if (grid?.SelectedItem is GroupCandidateRow row)
            vm.ValueSearch.ActivateGroup(row);
    }

    private void OnGroupCandidateInspect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<GroupCandidateRow>(sender, this.FindControl<DataGrid>("GroupResultsGrid"));
        if (row is not null) vm.ValueSearch.ActivateGroup(row);
    }

    private async void OnGroupCandidateCopyInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<GroupCandidateRow>(sender, this.FindControl<DataGrid>("GroupResultsGrid"));
        if (row is null) return;
        await CopyToClipboardAsync(row.InstanceAddr);
    }

    private async void OnGroupCandidateCopyClass(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var row = ResolveDataGridRow<GroupCandidateRow>(sender, this.FindControl<DataGrid>("GroupResultsGrid"));
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

    // Class Pivot overhaul Wave D — "Pivot this class…" from a Snapshot diff row.
    // ClassDisplay is the FQN; the pivot resolves the assembly at Run time.
    private void OnSnapshotPivotClassClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var row = ResolveDataGridRow<SnapshotDiffRow>(sender, this.FindControl<DataGrid>("SnapshotDiffGrid"));
        if (row is not null) vm.PivotForClass(null, row.ClassDisplay);
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
