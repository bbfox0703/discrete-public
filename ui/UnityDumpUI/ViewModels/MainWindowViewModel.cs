// MainWindowViewModel.cs — top-level VM for MainWindow.
//
// CommunityToolkit.Mvvm source generators (no reflection) drive the
// observable properties + commands per the AOT mandate.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    // _pipe is mutable: after every involuntary disconnect we replace it
    // with a fresh PipeClient instance. Empirically, even with our
    // single-instance DLL pipe + retry loop, the same .NET process can't
    // reliably re-Connect to a re-launched game's DLL — the kernel keeps
    // routing the client to a residual ghost instance from the dying
    // game until enough wall-clock has passed for cleanup. A brand-new
    // PipeClient (which is what "restart the UI" effectively gives you)
    // bypasses this. _backend is stable; we swap its inner pipe via
    // ReplacePipe so Browser/Inspector references stay valid.
    private PipeClient                  _pipe;
    private readonly BackendAdapter     _backend;
    private readonly FileLogger         _logger;
    private readonly IProcessEnumerator _processes;
    private readonly IDllInjector       _injector;
    private readonly IProxyInstaller    _proxyInstaller;
    private readonly IUnityGameScanner  _gameScanner;
    private readonly AobMakerBridge     _aobMaker;
    private CancellationTokenSource?    _connectCts;

    /// <summary>
    /// View-supplied factory that opens the process picker dialog and returns
    /// the chosen row (null = user cancelled). Decoupled like this so the VM
    /// has no reference to Avalonia <c>Window</c> types — the View wires it
    /// up at construction time. Default no-op until configured.
    /// </summary>
    public Func<Task<ProcessRow?>>? PickProcessAsync { get; set; }

    public MainWindowViewModel()
        : this(new UnityDumpUI.Platform.WindowsProcessEnumerator(),
               new UnityDumpUI.Platform.WindowsDllInjector(),
               new UnityDumpUI.Platform.WindowsProxyInstaller(),
               null)
    {
        // Scanner is constructed AFTER the proxy installer so it can hand
        // it through. Done in the field-initialiser after `this(...)`.
    }

    public MainWindowViewModel(IProcessEnumerator processes,
                                IDllInjector injector,
                                IProxyInstaller proxyInstaller,
                                IUnityGameScanner? gameScanner)
    {
        _pipe           = new PipeClient();
        _backend        = new BackendAdapter(_pipe);
        _logger         = new FileLogger();
        _processes      = processes;
        _injector       = injector;
        _proxyInstaller = proxyInstaller;
        _gameScanner    = gameScanner ?? new UnityDumpUI.Platform.SteamGameScanner(proxyInstaller);
        Browser         = new AssemblyBrowserViewModel(_backend, _logger);
        Inspector       = new LiveInspectionViewModel(_backend, _logger);
        AddressLookup   = new AddressLookupViewModel(_backend, _logger);
        SymbolFinder    = new SymbolFinderViewModel(_backend, _logger, Browser.Assemblies);
        SymbolFinder.NavigateRequested += OnSymbolFinderNavigateRequested;
        ValueSearch     = new ValueSearchViewModel(_backend, _logger);
        ValueSearch.NavigateToInspectorRequested += OnValueSearchNavigateToInspector;
        // Phase 29b — Snapshot tab. Wraps snapshotBegin/Chunk/End RPCs,
        // builds local in-memory envelope per capture, diffs two
        // snapshots into a flat row set. Same cross-tab handoff
        // contract as the other tabs (Inspector / Address Lookup).
        // Phase 29c — also receives the UserSettingsStore so the
        // per-game SQLite DB respects the user's custom directory /
        // auto-save / auto-prune preferences. Settings load is async
        // so we kick it off as fire-and-forget; until LoadAsync
        // returns, Snapshot uses the defaults baked into UserSettings.
        SettingsStore   = new UserSettingsStore(logger: _logger);
        _settings       = new UserSettings();
        Snapshot        = new SnapshotViewModel(_backend, _logger, SettingsStore, _settings);
        _ = LoadUserSettingsAsync();
        Snapshot.NavigateToInspectorRequested     += OnSnapshotNavigateToInspector;
        Snapshot.NavigateToAddressLookupRequested += OnSnapshotNavigateToAddressLookup;

        // Phase 29d — SPC Query tab. Shares the Snapshot tab's pool so
        // saves / drops in one are visible to the other.
        SpcQuery = new SpcQueryViewModel(Snapshot, _logger);
        SpcQuery.NavigateToInspectorRequested += OnSpcNavigateToInspector;
        // Phase 29e-2 — Class Pivot tab. Shares the same envelope pool;
        // answers the question SPC can't ("for class C, group instances
        // by key field value across N snaps, project value field").
        ClassPivot = new PivotViewModel(Snapshot, _logger);
        ClassPivot.NavigateToInspectorRequested += OnSpcNavigateToInspector;
        // Phase 20b: Scene Explorer tab. SceneEnumerator wraps the
        // UnityEngine.Object.FindObjects* + Transform-walk recipe from
        // unity_scene.lua; Phase 20-MT keeps main-thread routing on
        // every icall so the game stays responsive under sustained
        // burst.
        Scene = new SceneExplorerViewModel(new SceneEnumerator(_backend, _logger), _logger);
        Scene.NavigateToInspectorRequested += OnSceneNavigateToInspector;
        // Phase 22a/d: hoisted from Inspector to a top-level VM property.
        // AobMaker is session-wide — the Inspector's per-row HEX/Disasm
        // buttons, MethodOverloads' "Send AA script to CE" entries, and
        // the Phase 22d "Inject Discrete Bridge into CE Table" Tools-menu
        // command all consume the SAME instance. Inspector still gets the
        // reference for backward-compat with code paths that read through
        // it; new code paths should use vm.AobMaker directly.
        _aobMaker = new AobMakerBridge(_logger);
        Inspector.AobMaker     = _aobMaker;
        AddressLookup.AobMaker = _aobMaker;  // Phase 22e Send-to-CE button
        _aobMaker.AvailabilityChanged += OnAobMakerAvailabilityChanged;
        // Opportunistic startup probe so the connection-bar status text
        // can show "AOBMaker plugin: connected" without waiting for the
        // user to click anything. Fire-and-forget; failure is silent
        // (IsAobMakerAvailable stays false, which is the correct initial
        // state).
        _ = _aobMaker.CheckAvailabilityAsync();

        // Phase 17c — handoff from the Address Lookup tab back to the
        // Inspector tab. The VM raises this when the user clicks
        // "Inspect this instance" with a found result; MainWindow's
        // code-behind hooks the event to switch tabs + push the address
        // into LiveInspectionViewModel. Done here (instead of in the
        // view) so unit tests can drive it without an Avalonia loop.
        AddressLookup.InspectInstanceRequested += OnInspectInstanceRequested;

        // Bridge: when the user picks a live instance in the Inspector tab,
        // mirror its address into the export base so the next "Export .CT"
        // roots the cheat table at that instance. User can still override
        // by typing in the Export-options flyout.
        Inspector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveInspectionViewModel.SelectedInstance)
                && Inspector.SelectedInstance is { } row)
            {
                Browser.CeXmlBaseAddress = row.Address;
            }
        };

        // Phase 14a: react to involuntary pipe teardown (target game exited,
        // pipe closed). Marshals onto the UI thread because the event fires
        // from whichever pool thread happened to be doing the failing read.
        _pipe.Disconnected += OnPipeDisconnected;

        // Banner first so each ui/init/current.log clearly identifies the
        // build that wrote it. Same scheme as the C++ DLL's main.cpp.
        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit,
            $"{VersionInfo.Banner} session start");
        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit, "MainWindowViewModel created");

        // One-shot housekeeping: each game injection creates a new
        // %LOCALAPPDATA%\Discrete_UEDumper\Logs\<pid>\ tree. They
        // accumulate forever otherwise. Drop directories whose last-write
        // time is older than Constants.LogPruneDaysToKeep (15 by default).
        // The `ui` directory is always preserved.
        try
        {
            int pruned = FileLogger.PruneOldDllPidDirectories();
            if (pruned > 0)
            {
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit,
                    $"pruned {pruned} stale DLL log directories (older than {UnityDumpUI.Core.Constants.LogPruneDaysToKeep} days)");
            }
        }
        catch (Exception ex)
        {
            // Best-effort housekeeping must never block UI start.
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit,
                $"log prune failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnPipeDisconnected(object? sender, PipeDisconnectedInfo info)
    {
        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
            $"pipe disconnected: {info.Reason}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleInvoluntaryDisconnect(info.Reason));
    }

    /// <summary>
    /// Runs the same teardown the explicit <see cref="DisconnectAsync"/>
    /// command does, but tagged with a "(game exited)" status string so the
    /// user can tell apart "I clicked Disconnect" from "the game went away".
    /// Connect / Pick & inject re-enable automatically because their
    /// CanExecute keys on <see cref="IsConnected"/>.
    /// </summary>
    private void HandleInvoluntaryDisconnect(string reason)
    {
        if (!IsConnected) return; // already torn down by an explicit click

        // Swap in a fresh PipeClient. Same-process re-Connect to a
        // re-launched game has been observed to keep hitting "Pipe is
        // broken." for tens of seconds even though the new game's DLL
        // is up and waiting for clients — something between .NET's
        // pipe handle layer and the kernel's pipe-instance namespace
        // prefers the dying game's residual ghost over the live one.
        // A fresh PipeClient + NamedPipeClientStream chain bypasses
        // it (matches the "close UI and restart" workaround exactly).
        ReplacePipeForFreshConnect();

        IsConnected      = false;
        ConnectionStatus = $"Disconnected ({reason})";
        Engine = Backend2 = DllVariant = GameModule = UnityVersion = null;
        MetadataVersion = null;
        AssembliesCount = 0;
        Build           = 0;
        Protocol        = 0;
        MetadataWarning = null;
        DispatcherBannerVisible = false;
        DispatcherBannerMessage = string.Empty;
        // ConnectionStatus already carries the disconnect reason; suppress
        // the redundant red banner so the user only sees one row of state.
        ErrorMessage = null;

        Browser.Assemblies.Clear();
        Browser.FilteredAssemblies.Clear();
        Browser.Classes.Clear();
        Browser.FilteredClasses.Clear();
        Browser.Fields.Clear();
        Browser.Methods.Clear();
        Browser.SelectedAssembly = null;
        Browser.SelectedClass    = null;
        Browser.ParentChain      = string.Empty;
        // The Browser tab fires its own RefreshAssemblies during the
        // teardown which may have raced and set its own "Pipe is broken"
        // banner. Wipe it — the connection-bar status is the authoritative
        // disconnect indicator.
        Browser.ErrorMessage     = null;

        Inspector.ClearCommand.Execute(null);
        Inspector.ErrorMessage = null;

        // Phase 17c: drop the address-lookup state too. Result block (if
        // any) refers to a class pointer that the freshly-relaunched game
        // will have at a different address, so leaving it visible would
        // be misleading.
        AddressLookup.ClearCommand.Execute(null);
    }

    public IProcessEnumerator ProcessEnumerator => _processes;

    /// <summary>Phase 18c — exposed so the View can construct sub-VMs
    /// (e.g. <c>MethodOverloadsViewModel</c>) without smuggling the adapter
    /// through Browser. Both the adapter and the logger are stable for the
    /// lifetime of the main window.</summary>
    public BackendAdapter BackendAdapter => _backend;
    public FileLogger     Logger         => _logger;

    /// <summary>
    /// Phase 8 panels — assembly tree / class browser / field+method grids.
    /// Populated automatically after a successful handshake.
    /// </summary>
    public AssemblyBrowserViewModel Browser { get; }

    /// <summary>
    /// Phase 9 panels — instance finder + live walker. Targeted via
    /// <see cref="InspectSelectedClassCommand"/> from the class list selection.
    /// </summary>
    public LiveInspectionViewModel Inspector { get; }

    /// <summary>
    /// Phase 17c — Address Lookup tab. Wraps the <c>whatIsAt</c> RPC; emits
    /// <see cref="AddressLookupViewModel.InspectInstanceRequested"/> when the
    /// user clicks "Inspect this instance" with a found result.
    /// </summary>
    public AddressLookupViewModel AddressLookup { get; }

    /// <summary>
    /// Phase 25 — cross-class symbol finder. Wraps `searchSymbols` so the
    /// user can hunt a field/method name across every class in an assembly
    /// without iterating the Classes list by hand. Shares
    /// <see cref="AssemblyBrowserViewModel.Assemblies"/> so the dropdown
    /// populates as soon as the Browser finishes its post-handshake refresh.
    /// </summary>
    public SymbolFinderViewModel SymbolFinder { get; }

    /// <summary>
    /// Phase 27d — CE-style First Scan / Next Scan tab. Wraps the
    /// beginValueScan / refineValueScan / endValueScan pipe RPCs so
    /// the user can iteratively narrow candidates by typed value with
    /// no class hint required.
    /// </summary>
    public ValueSearchViewModel ValueSearch { get; }

    /// <summary>
    /// Phase 29b — Snapshot &amp; Diff tab. Captures the running game's
    /// managed state (statics + live instances + array elements) via
    /// the Phase 29a DLL RPCs and diffs two snapshots locally. "全抓"
    /// default scope per Phase 29 design — user doesn't have to pick a
    /// class. UI handles synthetic-type filtering locally (strategy C).
    /// </summary>
    public SnapshotViewModel Snapshot { get; }

    /// <summary>
    /// Phase 29d — SPC Query tab. Cross-session multi-snapshot
    /// constraint queries — given N snapshots + N per-snapshot
    /// predicates, find every field whose value sequence satisfies the
    /// chain. The killer use case: identify the 銀兩 GameStat by its
    /// real-world spending pattern across 4 sessions
    /// (10160 → 9910 → 9410 → 9410).
    /// </summary>
    public SpcQueryViewModel SpcQuery { get; }

    /// <summary>Phase 29e-2 — Class Pivot tab. Same envelope pool as
    /// Snapshot + SPC tabs; pivot rows are keyed by a stable business
    /// field inside each instance rather than its managed address,
    /// which solves the cross-session matching problem SPC can't
    /// touch (instance addresses don't survive game launches).</summary>
    public PivotViewModel ClassPivot { get; }

    /// <summary>Phase 29c — JSON-persisted user preferences (snapshot DB
    /// directory override, auto-save toggle, auto-prune threshold).
    /// Surfaced here so the Settings dialog (29c-7) can bind to it.</summary>
    public UserSettingsStore SettingsStore { get; }

    private UserSettings _settings;
    private async Task LoadUserSettingsAsync()
    {
        try
        {
            _settings = await SettingsStore.LoadAsync().ConfigureAwait(true);
            Snapshot.ApplyLoadedSettings(_settings);
        }
        catch (Exception ex)
        {
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit,
                $"UserSettings load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 20b — Scene Explorer tab. Filter-driven TreeView walking
    /// Unity's authoritative scene graph via Object.FindObjects* +
    /// Transform.GetChild. All icalls main-thread-routed (Phase 20-MT)
    /// so even big scenes (40k+ GameObjects on BongoCat) don't freeze
    /// the host.
    /// </summary>
    public SceneExplorerViewModel Scene { get; }

    /// <summary>
    /// Phase 22d — session-wide AOBMaker CE-plugin bridge. Inspector's
    /// HEX/Disasm row buttons, MethodOverloads' "Send AA script to CE",
    /// and Tools→Inject Discrete Bridge all share this instance so a
    /// single pipe-client lifetime + log stream cover the whole session.
    /// </summary>
    public IAobMakerBridge AobMaker => _aobMaker;

    /// <summary>
    /// Footer credit line, decoded from the XOR-shielded blob in
    /// <see cref="LocalizedText"/>. Built once at VM construction so the
    /// plaintext never appears as a literal in the assembly.
    /// </summary>
    public string CopyrightText { get; } = $"© {LocalizedText.GetCopyright()}";

    /// <summary>
    /// Selected index of the right-pane TabControl. The Address Lookup tab
    /// is index 3 (Fields=0 / Inspector=1 / Methods=2 / AddressLookup=3).
    /// Bound TwoWay so cross-tab handoff (e.g. the inspector's right-click
    /// "Find what's at this address" or the lookup's "Inspect this
    /// instance" buttons) can switch tabs from the VM layer without
    /// touching the view.
    /// </summary>
    [ObservableProperty]
    private int _selectedDetailTabIndex;

    /// <summary>
    /// Tab indices for <see cref="SelectedDetailTabIndex"/>. Kept here so
    /// callers don't need magic numbers.
    /// </summary>
    // Must mirror the TabItem order in MainWindow.axaml. Symbol Finder
    // sits before Address Lookup in the XAML (Phase 25 → Phase 17c
    // ordering) — these two used to be swapped here, which silently
    // mis-routed every Address Lookup cross-tab handoff into Symbol
    // Finder. Caught 2026-05-26 by snapshot-diff right-click smoke.
    public const int TabIndexFields        = 0;
    public const int TabIndexInspector     = 1;
    public const int TabIndexMethods       = 2;
    public const int TabIndexSymbolFinder  = 3;
    public const int TabIndexAddressLookup = 4;
    public const int TabIndexScene         = 5;
    public const int TabIndexValueSearch   = 6;
    public const int TabIndexSnapshot      = 7;
    public const int TabIndexSpcQuery      = 8;
    public const int TabIndexClassPivot    = 9;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsX64dbgCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsGhidraCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsIdaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportIl2cppHeaderCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectDiscreteBridgeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsX64dbgCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsGhidraCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsIdaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportIl2cppHeaderCommand))]
    private bool _isBusy;

    /// <summary>
    /// Phase 22d — mirrors <see cref="IAobMakerBridge.IsAvailable"/> for
    /// data-binding. Updated on <see cref="IAobMakerBridge.AvailabilityChanged"/>
    /// transitions (false→true on first successful pipe contact, true→false
    /// when a request fails the connect / IO path). Surfaced in the
    /// connection bar so the user knows whether Tools→Inject Discrete
    /// Bridge will actually reach a CE instance.
    /// </summary>
    [ObservableProperty] private bool _isAobMakerAvailable;

    /// <summary>
    /// Phase 22d — status line for the bridge-inject pipeline. Updated
    /// per step ("Injecting unity_dump.lua (3/9)…") and on completion
    /// ("Bridge ready in CE — enable Load Discrete Bridge to activate")
    /// or failure ("Step 3/9 'Injecting unity_dump.lua' failed: …").
    /// Separate from <see cref="ConnectionStatus"/> because the bridge
    /// inject is independent of the DLL pipe lifecycle (you can inject
    /// the bridge without DiscreteUI being connected to the game DLL,
    /// and vice versa). Null when no inject has run this session yet.
    /// </summary>
    [ObservableProperty] private string? _bridgeInjectStatus;

    [ObservableProperty] private string? _engine;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsX64dbgCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsGhidraCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSymbolsIdaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportIl2cppHeaderCommand))]
    private string? _backend2;     // "Backend" collides with field above
    [ObservableProperty] private string? _dllVariant;
    [ObservableProperty] private string? _gameModule;
    [ObservableProperty] private string? _unityVersion;
    [ObservableProperty] private int?    _metadataVersion;
    [ObservableProperty] private int     _assembliesCount;
    [ObservableProperty] private uint    _build;
    [ObservableProperty] private int     _protocol;

    /// <summary>
    /// User-collapsed state for the Assemblies / Classes panes. When true,
    /// the pane shrinks to a thin rail (just title + reopen chevron) and
    /// gives its width back to the Inspector / Detail pane on the right.
    /// MainWindow code-behind subscribes to these and rewrites the
    /// corresponding <c>ColumnDefinition.Width</c> on toggle, remembering
    /// the user's last expanded width via the GridSplitter.
    /// </summary>
    [ObservableProperty] private bool _isAssembliesPaneCollapsed;
    [ObservableProperty] private bool _isClassesPaneCollapsed;

    [RelayCommand] private void ToggleAssembliesPane() => IsAssembliesPaneCollapsed = !IsAssembliesPaneCollapsed;
    [RelayCommand] private void ToggleClassesPane()    => IsClassesPaneCollapsed    = !IsClassesPaneCollapsed;

    /// <summary>
    /// Phase 14b: stripped/obfuscated metadata banner. Populated from
    /// <c>getMetadataStats</c>'s <c>verdict</c> field; null when metadata
    /// is clean or the heuristic call failed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStripWarning))]
    private string? _metadataWarning;

    public bool HasStripWarning => !string.IsNullOrEmpty(MetadataWarning);

    [ObservableProperty] private string? _errorMessage;

    // -----------------------------------------------------------------------
    // Phase 20-MT v2 — auto-prompt "Enable runInBackground" banner.
    //
    // After a successful Connect, we read getDispatcherStats once. When the
    // hook is installed but is_healthy=false (typical Mono target with
    // Application.runInBackground=false where the game window doesn't have
    // focus right now), surface a banner offering the one-shot fix instead
    // of letting the user discover it via a 5-second timeout on the first
    // Scene/Method/Field RPC.
    //
    // The banner stays until either the user clicks Enable (success →
    // auto-dismiss), Dismiss (manual hide), or Disconnect (cleared).
    // -----------------------------------------------------------------------

    /// <summary>Banner visibility. Bound to a yellow strip below the strip-
    /// warning banner.</summary>
    [ObservableProperty]
    private bool _dispatcherBannerVisible;

    /// <summary>Inline status text on the banner — flips between the
    /// initial offer, the countdown ("Calling in 2s…"), the in-flight
    /// state, and any error.</summary>
    [ObservableProperty]
    private string _dispatcherBannerMessage = string.Empty;

    /// <summary>When the banner button is mid-call. Disables both banner
    /// commands so a double-click doesn't re-fire.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableRunInBackgroundFromBannerCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissDispatcherBannerCommand))]
    private bool _dispatcherBannerBusy;

    /// <summary>One-shot probe kicked from the Connect happy-path. Reads
    /// dispatcher stats with a brief delay (gives Unity a few frames to
    /// tick after the engine pause that frequently follows window
    /// activation), then sets <see cref="DispatcherBannerVisible"/> if
    /// the hook is installed but the dispatcher hasn't been ticking.
    /// All paths are fire-and-forget — failures stay silent so we
    /// don't gate Connect on a diagnostic RPC.</summary>
    private async Task CheckDispatcherHealthAsync()
    {
        try
        {
            // Give Unity a moment to tick at least once after Connect. The
            // pipe handshake happens on a worker thread; the engine may
            // still be focused on DiscreteUI's connect dialog at the exact
            // moment we run this check. ~1.5s is enough for the dispatcher
            // hook to fire at least one drain on a healthy target.
            await Task.Delay(1500, _connectCts?.Token ?? default).ConfigureAwait(true);

            DispatcherStatsResult s =
                await _backend.GetDispatcherStatsAsync(
                    _connectCts?.Token ?? default).ConfigureAwait(true);

            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"dispatcher stats: hook={s.HookInstalled} healthy={s.IsHealthy} "
                + $"ticks={s.TickCount} drain={s.DrainCount} age_ms={s.LastTickAgeMs}");

            if (s.HookInstalled && !s.IsHealthy)
            {
                DispatcherBannerMessage =
                    "Game pauses when DiscreteUI takes focus — Scene / Method-call / "
                    + "Field-write RPCs will time out. Click below to fix it for this "
                    + "session (countdown lets you alt-tab back to the game first).";
                DispatcherBannerVisible = true;
            }
        }
        catch (OperationCanceledException) { /* disconnect cancellation, ignore */ }
        catch (Exception ex)
        {
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"dispatcher health probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnableRunInBackgroundFromBanner))]
    private async Task EnableRunInBackgroundFromBannerAsync()
    {
        DispatcherBannerBusy = true;
        try
        {
            // Hardcoded 3-second countdown — matches the suggested Focus
            // delay default. User clicks button, sees countdown, alt-tabs
            // back to the game, the call fires once Unity is ticking
            // again.
            for (int s = 3; s > 0; --s)
            {
                if (_connectCts?.Token.IsCancellationRequested == true) return;
                DispatcherBannerMessage = $"Focus the game window — calling in {s}s…";
                await Task.Delay(1000, _connectCts?.Token ?? default).ConfigureAwait(true);
            }
            DispatcherBannerMessage = "Calling Application.set_runInBackground(true)…";

            SetRunInBackgroundResult r =
                await Scene.Enumerator.SetApplicationRunInBackgroundAsync(
                    true, _connectCts?.Token ?? default).ConfigureAwait(true);

            if (r.Ok)
            {
                // Lock in the success on the Scene VM so its button greys
                // out symmetrically — Scene and banner share the state.
                Scene.RunInBackgroundEnabled = true;
                DispatcherBannerVisible     = false;
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                    $"runInBackground enabled via banner ({r.ResolvedAssembly})");
            }
            else
            {
                DispatcherBannerMessage =
                    $"Failed: {r.Error}. Re-focus the game window and try again.";
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                    $"runInBackground via banner failed: {r.Error}");
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            DispatcherBannerMessage = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"runInBackground via banner crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DispatcherBannerBusy = false;
        }
    }

    private bool CanEnableRunInBackgroundFromBanner() => !DispatcherBannerBusy;

    [RelayCommand(CanExecute = nameof(CanDismissDispatcherBanner))]
    private void DismissDispatcherBanner() => DispatcherBannerVisible = false;

    private bool CanDismissDispatcherBanner() => !DispatcherBannerBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // Wipe every red banner so a fresh attempt isn't visually polluted
        // by leftovers from the last failed/torn-down session.
        ErrorMessage           = null;
        Browser.ErrorMessage   = null;
        Inspector.ErrorMessage = null;
        IsBusy = true;
        ConnectionStatus = "Connecting…";
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        try
        {
            HandshakeResult hs = await ConnectWithRetryAsync(_connectCts.Token).ConfigureAwait(true);

            Engine          = hs.Engine;
            Backend2        = hs.Backend;
            DllVariant      = hs.DllVariant;
            GameModule      = hs.GameModule;
            UnityVersion    = hs.UnityVersion;
            MetadataVersion = hs.MetadataVersion;
            AssembliesCount = hs.AssembliesCount;
            Build           = hs.Build;
            Protocol        = hs.Protocol;

            IsConnected      = true;
            ConnectionStatus = $"Connected — build {hs.Build}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"connected backend={hs.Backend} build={hs.Build} unity={hs.UnityVersion}");

            // Phase 29b — mark snapshots from a different game build
            // as stale. User feedback: if I take a snapshot of 活俠傳,
            // disconnect (game hung), then connect to SaGa F2R, the
            // 活俠傳 snapshot is still in the dropdown but it's not
            // diffable against SaGa snapshots. Visual flag in the
            // snapshot's DisplayName tells the user. We DON'T delete
            // them — user might reconnect to the same game later and
            // want to keep them.
            if (!string.IsNullOrEmpty(hs.GameBuildId))
            {
                foreach (var s in Snapshot.Snapshots)
                {
                    s.IsStale = !string.Equals(
                        s.GameBuildId, hs.GameBuildId, StringComparison.Ordinal);
                }
            }

            // Phase 29-polish (2026-05-27) — hand the game-process
            // bitness + current memory to the Snapshot VM. On 32-bit
            // targets this auto-clamps InstanceCap/ArrayCap downward
            // to safe defaults (256 / 64) so the user doesn't trip
            // the 4 GB OOM CTD that build #646 hit on 活俠傳. Also
            // populates the "Game: 32-bit Mono — WorkingSet X GB"
            // banner that warns as memory climbs toward 3 GB.
            Snapshot.OnGameConnected(
                hs.GamePointerSize ?? 0,
                hs.GameWorkingSet  ?? 0UL,
                hs.GamePrivateUsage ?? 0UL,
                hs.Backend ?? string.Empty);

            // Phase 29c-3 — open (or switch to) the per-game SQLite DB.
            // Lazy-loads metadata from disk into Snapshot.Snapshots so
            // the user sees their saved snapshots from previous sessions
            // immediately. Soft-fail — DB errors don't block the
            // already-completed handshake.
            if (!string.IsNullOrEmpty(hs.GameBuildId))
            {
                _ = InitSnapshotDbAndPivotAsync(hs.GameBuildId, hs.GameModule ?? "unknown");
            }

            // Phase 8: kick off assembly fetch so the left pane populates
            // without an extra click. Failures surface on Browser.ErrorMessage.
            _ = Browser.RefreshAssembliesCommand.ExecuteAsync(null);

            // 2026-05-28: populate the Snapshot Scope picker + default the
            // scope to "Assembly-CSharp" when present. Soft-fail.
            _ = Snapshot.LoadScopeAssembliesAsync();

            // Phase 14b: opportunistic metadata-stats call so the user
            // sees a "stripped/obfuscated" banner up front rather than
            // discovering it through garbled class names. Soft-fail.
            _ = LoadMetadataStatsAsync();

            // Phase 20-MT v2: opportunistic dispatcher-health probe so the
            // user sees the "Enable runInBackground" banner before their
            // first Scene/Method/Field RPC eats a 5-second timeout.
            // Soft-fail.
            _ = CheckDispatcherHealthAsync();
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "Cancelled";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe, "connect cancelled");
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error";
            ErrorMessage     = ex.Message;
            IsConnected      = false;
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"connect failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Phase 29e-7 — open the per-game DB, then seed the Class
    /// Pivot tab's class / assembly autocomplete from its metadata (no
    /// blob hydrate) so the FQN box suggests names immediately. Both
    /// steps soft-fail independently; a DB error doesn't block the
    /// already-completed handshake.</summary>
    private async Task InitSnapshotDbAndPivotAsync(string buildId, string module)
    {
        await Snapshot.OnGameDbContextAsync(buildId, module).ConfigureAwait(true);
        await ClassPivot.SeedKnownClassesFromDbAsync().ConfigureAwait(true);
    }

    private bool CanConnect() => !IsBusy && !IsConnected;

    /// <summary>
    /// Dispose the current <see cref="PipeClient"/> and install a fresh
    /// instance into <see cref="_backend"/>. Re-subscribes the
    /// Disconnected event handler. Called from every disconnect path so
    /// the next Connect attempt runs against a completely fresh
    /// .NET-side pipe stack — matches the "restart UI and try again"
    /// workaround empirically.
    /// </summary>
    private void ReplacePipeForFreshConnect()
    {
        PipeClient old = _pipe;
        old.Disconnected -= OnPipeDisconnected;
        // Fire-and-forget dispose. Holding it on the UI thread would block
        // for the gate semaphore; the old streams are already torn down by
        // whatever event got us here, so dispose is just bookkeeping.
        _ = old.DisposeAsync();

        _pipe = new PipeClient();
        _pipe.Disconnected += OnPipeDisconnected;
        _backend.ReplacePipe(_pipe);
        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
            "PipeClient recreated for next Connect");
    }

    /// <summary>
    /// Connect + handshake with a retry loop that covers the proxy DLL's
    /// startup window. When a game re-launches under the proxy, two things
    /// happen in parallel for several seconds:
    ///   - the dying process's ghost named-pipe instance lingers in the
    ///     kernel namespace and `NamedPipeClientStream.ConnectAsync` happily
    ///     opens it, then the first IO fails with "Pipe is broken.";
    ///   - the proxy DLL's <c>BackendLoaderThread</c> polls for the engine
    ///     DLL for up to ~5 seconds before <c>LoadLibraryW</c>'ing the
    ///     backend, which is the only thing that starts the pipe server
    ///     (see dll/src/proxy/loader.cpp:65).
    /// Total realistic window: ~5–7 seconds. Budget the retry loop a bit
    /// past that. Bail fast on cancellation or genuine timeouts (no server
    /// reachable at all).
    /// </summary>
    private async Task<HandshakeResult> ConnectWithRetryAsync(CancellationToken ct)
    {
        const int maxAttempts = 12;
        const int delayMs     = 600;   // ~7.2 s total budget — covers proxy's 5 s detect + slack
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; ++attempt)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _backend.ConnectAsync(timeoutMs: 3000, ct).ConfigureAwait(true);
                if (attempt > 1)
                {
                    _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                        $"connect attempt {attempt}/{maxAttempts} succeeded");
                }
                return await _backend.HandshakeAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsTransientPipeError(ex) && attempt < maxAttempts)
            {
                lastEx = ex;
                // Surface progress on the connection-bar status so the
                // user can see we're not stuck — e.g., "Connecting… (3/12)".
                ConnectionStatus = $"Connecting… ({attempt}/{maxAttempts})";
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                    $"connect attempt {attempt}/{maxAttempts} failed transiently: {ex.GetType().Name}: {ex.Message}; retrying after {delayMs}ms");
                // Do NOT call _pipe.DisposeAsync() here — that disposes the
                // SemaphoreSlim gate permanently, which would brick the
                // PipeClient for any future Connect (the next attempt or any
                // user-initiated reconnect throws ObjectDisposedException on
                // the gate). PipeClient.ConnectAsync already calls
                // DisposeStreamsAsync() internally to reset stream state on
                // each call, which is exactly what we need.
                await Task.Delay(delayMs, ct).ConfigureAwait(true);
            }
        }
        // Retry budget exhausted. Wrap the original error in something
        // actionable so the user knows what to try next.
        throw new IOException(
            $"Could not connect after {maxAttempts} attempts ({maxAttempts * delayMs / 1000.0:F1}s). " +
            "If the game just launched, give the proxy DLL a few more seconds to initialize and click Connect again. " +
            $"Underlying error: {lastEx?.Message ?? "unknown"}",
            lastEx);
    }

    /// <summary>
    /// Distinguishes "stale handle, retry me" failures from "no server at
    /// all, surface the error" failures. Pipe-broken / EOF errors are the
    /// former; <see cref="TimeoutException"/> is the latter and bubbles
    /// straight to the user.
    /// </summary>
    private static bool IsTransientPipeError(Exception ex) =>
        ex is PipeDisconnectedException
           or System.IO.IOException
           or System.IO.EndOfStreamException;

    /// <summary>
    /// Fire-and-forget post-handshake call to <c>getMetadataStats</c>. A
    /// non-clean verdict (single-letter / two-letter type names dominating
    /// the metadata) raises a banner so the user knows the field/method
    /// names they see may be obfuscator output, not the original symbols.
    /// </summary>
    private async Task LoadMetadataStatsAsync()
    {
        try
        {
            MetadataStatsResult? stats = await _backend
                .GetMetadataStatsAsync(default).ConfigureAwait(true);
            if (stats is null) return;
            string verdict = stats.Verdict ?? string.Empty;
            if (verdict == "heavy_strip")
            {
                double ratio = stats.StripRatio ?? 0.0;
                MetadataWarning =
                    $"Metadata appears heavily stripped/obfuscated " +
                    $"({ratio * 100:F0}% short type names). Class & method " +
                    $"names may be obfuscator output, not the originals.";
            }
            else if (verdict == "partial_strip")
            {
                double ratio = stats.StripRatio ?? 0.0;
                MetadataWarning =
                    $"Metadata partially stripped ({ratio * 100:F0}% short " +
                    $"type names). Some type names may be uninformative.";
            }
            else
            {
                MetadataWarning = null;
            }
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"metadata stats: verdict={verdict} ratio={stats.StripRatio:F3} total={stats.TotalClasses}");
        }
        catch (Exception ex)
        {
            // Soft-fail — banner stays empty.
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"getMetadataStats failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try
        {
            _connectCts?.Cancel();
            // Use DisconnectAsync rather than DisposeAsync so the gate
            // semaphore stays alive for a future Connect. DisposeAsync is
            // reserved for app shutdown (see Dispose() at the bottom).
            await _pipe.DisconnectAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsConnected      = false;
            ConnectionStatus = "Disconnected";
            Engine = Backend2 = DllVariant = GameModule = UnityVersion = null;
            MetadataVersion = null;
            AssembliesCount = 0;
            Build           = 0;
            Protocol        = 0;
            MetadataWarning = null;
            DispatcherBannerVisible = false;
            DispatcherBannerMessage = string.Empty;

            // Phase 8: stale data is misleading — wipe the browser too.
            Browser.Assemblies.Clear();
            Browser.FilteredAssemblies.Clear();
            Browser.Classes.Clear();
            Browser.FilteredClasses.Clear();
            Browser.Fields.Clear();
            Browser.Methods.Clear();
            Browser.SelectedAssembly = null;
            Browser.SelectedClass    = null;
            Browser.ParentChain      = string.Empty;
            Browser.ErrorMessage     = null;

            // Phase 9: drop inspector state too.
            Inspector.ClearCommand.Execute(null);
            Inspector.ErrorMessage = null;

            // Same fresh-PipeClient policy as HandleInvoluntaryDisconnect:
            // never re-use a PipeClient instance across Connect cycles in
            // this UI process.
            ReplacePipeForFreshConnect();
        }
    }

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// Open the process picker, inject the matching <c>UnityDumper-&lt;Backend&gt;.dll</c>
    /// (looked up next to the running exe), then kick the existing Connect
    /// flow. One-click replacement for <c>tools\Test-Handshake.ps1</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInject))]
    private async Task InjectAndConnectAsync()
    {
        if (PickProcessAsync is null) return;

        ProcessRow? row;
        try
        {
            row = await PickProcessAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"picker failed: {ex.Message}";
            return;
        }
        if (row is null) return;

        if (row.Backend == UnityBackend.None)
        {
            ErrorMessage = $"PID {row.Pid} ({row.Name}) — no Unity backend detected. " +
                           "Pick another process or check that the game has finished loading.";
            return;
        }

        // Phase 12d-5: cross-bitness routing. 32-bit Unity targets (WOW64)
        // need the x86 Mono DLL from dist/x86/; the WindowsDllInjector
        // then shells out to dist/x86/inject-x86.exe to do the actual
        // LoadLibraryW (the in-process x64 injector can't cleanly target
        // WOW64). x86 IL2CPP isn't a shipping configuration — anyone
        // running a 32-bit Unity game today is on Mono.
        if (row.IsWow64 && row.Backend == UnityBackend.IL2CPP)
        {
            ErrorMessage = $"PID {row.Pid} ({row.Name}) — 32-bit IL2CPP isn't supported. " +
                           "No shipping IL2CPP target is 32-bit. Pick another process.";
            return;
        }
        string dllName = row.Backend == UnityBackend.IL2CPP
            ? "UnityDumper-IL2CPP.dll"
            : "UnityDumper-Mono.dll";
        // Resolve next to the actual DiscreteUI.exe on disk, NOT
        // AppContext.BaseDirectory: for the self-extracting single-file
        // build mode (build.cmd ui) the latter points at a per-launch temp
        // dir under %LOCALAPPDATA%\Temp\.net\... where the side-DLL never
        // lands. Environment.ProcessPath gives the real .exe path in both
        // self-contained-single-file and Native AOT publish modes.
        string? baseDir = Environment.ProcessPath is { Length: > 0 } pp
                          ? Path.GetDirectoryName(pp) : null;
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        string dllPath = row.IsWow64
            ? Path.Combine(baseDir, "x86", dllName)
            : Path.Combine(baseDir, dllName);

        ErrorMessage = null;
        IsBusy = true;
        ConnectionStatus = $"Injecting {dllName} into PID {row.Pid}…";
        try
        {
            InjectResult result = await _injector.InjectAsync(row.Pid, dllPath).ConfigureAwait(true);
            if (!result.Ok)
            {
                ErrorMessage = result.ErrorMessage;
                ConnectionStatus = "Inject failed";
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                    $"inject {dllPath} -> PID {row.Pid} failed: {result.ErrorMessage}");
                return;
            }
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"inject {dllName} -> PID {row.Pid} ok (LoadLibrary=0x{result.LoadLibraryExitCode:X8})");
        }
        finally
        {
            IsBusy = false;
        }

        // Hand off to the existing connect flow — same pipe, same handshake.
        await ConnectAsync().ConfigureAwait(true);
    }

    private bool CanInject() => !IsBusy && !IsConnected;

    // ---------- Phase 12c: proxy install/uninstall ----------------------

    /// <summary>
    /// View-supplied factory that opens the Proxy Deploy window non-modally.
    /// Returns once the window is shown (the window then runs independently
    /// — closing it doesn't tie back to the VM). Decoupled from Avalonia
    /// Window types same as <see cref="PickProcessAsync"/>.
    /// </summary>
    public Action<ProxyDeployViewModel>? ShowProxyDeploy { get; set; }

    public IUnityGameScanner GameScanner => _gameScanner;

    /// <summary>
    /// "Proxy Deploy…" button on the connection bar. Builds the panel VM
    /// fresh each time and hands it to the view-supplied opener; the
    /// window's first action is to kick a Steam scan so the grid isn't
    /// empty when it appears.
    /// </summary>
    [RelayCommand]
    private void OpenProxyDeploy()
    {
        if (ShowProxyDeploy is null) return;
        var vm = new ProxyDeployViewModel(_gameScanner, _proxyInstaller);
        _ = vm.RefreshCommand.ExecuteAsync(null);
        ShowProxyDeploy(vm);
    }

    /// <summary>
    /// Phase 22d — reacts to AobMaker pipe availability transitions.
    /// Marshals to the UI thread because the bridge fires the event from
    /// whichever pool thread happened to be doing the connect (matches
    /// the OnPipeDisconnected pattern).
    /// </summary>
    private void OnAobMakerAvailabilityChanged(object? sender, bool isAvailable)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsAobMakerAvailable = isAvailable;
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"AOBMaker plugin: {(isAvailable ? "available" : "unavailable")}");
        });
    }

    /// <summary>
    /// Phase 22d — Tools menu "Inject Discrete Bridge into CE Table".
    /// Embeds the 7 bridge lua files into the currently-open CE table
    /// (via AOBMaker plugin's InjectTableFile) + creates the "Load
    /// Discrete Bridge" AA-script entry that loads them on enable.
    ///
    /// Deviation from plan.md (documented here so reviewers understand
    /// the trade-off): the menu item does NOT pre-gate on
    /// <see cref="IsAobMakerAvailable"/>. Gating creates a chicken-and-egg
    /// problem when the user opens Cheat Engine AFTER DiscreteUI: the
    /// menu would stay disabled until either a periodic poller or an
    /// explicit "Recheck" button refreshes the flag, neither of which we
    /// want to add for v1. Instead the command runs CheckAvailability as
    /// step 1 of <see cref="BridgeInjector.InjectAsync"/> and surfaces
    /// "open CE with the AOBMaker plugin loaded" via
    /// <see cref="BridgeInjectStatus"/> on failure. Same pattern as
    /// <see cref="InjectAndConnectAsync"/> (which doesn't pre-gate on
    /// backend reachability either). The IsAobMakerAvailable property is
    /// still used by the connection bar's status indicator.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInjectDiscreteBridge))]
    private async Task InjectDiscreteBridgeAsync()
    {
        IsBusy = true;
        BridgeInjectStatus = "Starting…";
        try
        {
            var progress = new Progress<BridgeInjectProgress>(p =>
                BridgeInjectStatus = $"({p.Index}/{p.Total}) {p.Description}…");

            BridgeInjectResult result = await BridgeInjector
                .InjectAsync(_aobMaker, progress).ConfigureAwait(true);

            if (result.Ok)
            {
                BridgeInjectStatus =
                    $"Bridge ready ({result.FilesInjected} files + bootstrap AA) — " +
                    $"enable \"{BootstrapAaScriptGenerator.EntryDescription}\" in CE to activate";
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                    $"InjectDiscreteBridge: ok — {result.FilesInjected} files, AA created");
            }
            else
            {
                BridgeInjectStatus = result.FailedStepDescription is { } step
                    ? $"Failed at \"{step}\": {result.ErrorMessage}"
                    : $"Failed: {result.ErrorMessage ?? "(no detail)"}";
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                    $"InjectDiscreteBridge: failed — step='{result.FailedStepDescription}' err='{result.ErrorMessage}'");
            }
        }
        catch (Exception ex)
        {
            BridgeInjectStatus = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"InjectDiscreteBridge: exception {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInjectDiscreteBridge() => !IsBusy;

    // -----------------------------------------------------------------------
    // Debugger symbol export — Tools menu → "Export Symbols → x64dbg / Ghidra
    // / IDA". IL2CPP-only (Mono method addresses are dynamic JIT pages, not
    // module-relative; see docs/export-formats.md). Each command:
    //   1. Lists assemblies, drops the system / UnityEngine subset.
    //   2. Calls dumpIl2cppSymbols for the user-assembly union.
    //   3. Renders via SymbolExporter (one format per command).
    //   4. Writes via the View-supplied SaveFileAsync picker.
    // -----------------------------------------------------------------------

    /// <summary>
    /// View-supplied save-file picker, identical signature to
    /// <see cref="AssemblyBrowserViewModel.SaveFileAsync"/>. Wired by the
    /// View's code-behind; null until then.
    /// </summary>
    public Func<string /*suggestedName*/, string /*description*/, string /*ext*/, Task<string?>>? SaveFileAsync { get; set; }

    [RelayCommand(CanExecute = nameof(CanExportSymbols))]
    private Task ExportSymbolsX64dbgAsync() =>
        ExportSymbolsAsync(
            "x64dbg Database",
            ".dd64",
            "symbols_x64dbg.dd64",
            (rows, modName, modBase) =>
                SymbolExporter.RenderX64dbgDatabase(rows, modName, modBase));

    [RelayCommand(CanExecute = nameof(CanExportSymbols))]
    private Task ExportSymbolsGhidraAsync() =>
        ExportSymbolsAsync(
            "Ghidra Symbols",
            ".txt",
            "symbols_ghidra.txt",
            (rows, _, _) => SymbolExporter.RenderGhidraSymbols(rows));

    [RelayCommand(CanExecute = nameof(CanExportSymbols))]
    private Task ExportSymbolsIdaAsync() =>
        ExportSymbolsAsync(
            "IDA Script",
            ".idc",
            "symbols_ida.idc",
            (rows, _, _) => SymbolExporter.RenderIdaScript(rows));

    /// <summary>
    /// True only on IL2CPP connections with the View's save-file picker
    /// wired up. Mono backends don't expose stable module-relative
    /// addresses for managed methods (JIT pages, not GameAssembly.dll),
    /// so the export menu greys out.
    /// </summary>
    private bool CanExportSymbols() =>
        !IsBusy && IsConnected && SaveFileAsync is not null &&
        string.Equals(Backend2, "IL2CPP", StringComparison.OrdinalIgnoreCase);

    private async Task ExportSymbolsAsync(
        string filterDescription,
        string ext,
        string suggestedName,
        Func<IReadOnlyList<SymbolDumpRow>, string, ulong, string> renderer)
    {
        if (SaveFileAsync is null) return;

        string? path;
        try
        {
            path = await SaveFileAsync(suggestedName, filterDescription, ext).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"save dialog failed: {ex.Message}";
            return;
        }
        if (path is null) return;

        IsBusy = true;
        try
        {
            // Source the assembly list straight from the DLL — Browser's
            // collection might still be loading on a fresh connect, and
            // we don't want a partial dump as a result of UI lag.
            GetAssembliesResult asmResult = await _backend
                .GetAssembliesAsync().ConfigureAwait(true);
            var userAssemblies = SystemAssemblyFilter.SelectUserAssemblies(
                asmResult.Assemblies.Select(a => a.Name));
            if (userAssemblies.Count == 0)
            {
                ErrorMessage =
                    "No user assemblies to export (every assembly was flagged as " +
                    "system/Unity engine). Adjust SystemAssemblyFilter if your game " +
                    "uses an unusual naming scheme.";
                return;
            }

            DumpIl2cppSymbolsResult dump = await _backend
                .DumpIl2cppSymbolsAsync(
                    userAssemblies,
                    includeClasses: true,
                    includeMethods: true)
                .ConfigureAwait(true);

            ulong modBase = 0;
            if (!string.IsNullOrEmpty(dump.ModuleBase))
            {
                string clean = dump.ModuleBase;
                if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    clean = clean[2..];
                _ = ulong.TryParse(clean,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out modBase);
            }

            string text = renderer(dump.Rows, dump.ModuleName, modBase);
            await File.WriteAllTextAsync(path, text).ConfigureAwait(true);

            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"export symbols {ext} -> {path} ({dump.Rows.Count} rows, " +
                $"scanned={dump.ScannedClasses}, truncated={dump.Truncated})");
            if (dump.Truncated)
            {
                ErrorMessage = "Symbol dump hit the 1,000,000-row safety cap — " +
                               "output is incomplete.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"symbol export failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"export symbols {ext} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Phase C (Il2CppDumper feature parity, path B): whole-game il2cpp.h.
    // Walks every user assembly's classes via the existing getClasses /
    // getClassInfo RPCs (no DLL changes needed), then renders the batch
    // header via Il2cppHeaderBatchExporter. Same connection gating as
    // the symbol exports — IL2CPP only, save-file dialog wired, not busy.
    [RelayCommand(CanExecute = nameof(CanExportSymbols))]
    private async Task ExportIl2cppHeaderAsync()
    {
        if (SaveFileAsync is null) return;

        string? path;
        try
        {
            path = await SaveFileAsync("il2cpp.h", "C/C++ Header", ".h").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"save dialog failed: {ex.Message}";
            return;
        }
        if (path is null) return;

        IsBusy = true;
        try
        {
            GetAssembliesResult asmResult = await _backend
                .GetAssembliesAsync().ConfigureAwait(true);
            var userAssemblies = SystemAssemblyFilter.SelectUserAssemblies(
                asmResult.Assemblies.Select(a => a.Name));
            if (userAssemblies.Count == 0)
            {
                ErrorMessage =
                    "No user assemblies to export (every assembly was flagged " +
                    "as system/Unity engine). Adjust SystemAssemblyFilter if " +
                    "your game uses an unusual naming scheme.";
                return;
            }

            // Phase C follow-up — ONE bulk RPC instead of N×getClassInfo.
            // Fixes the post-export slow-quit documented in
            // docs/lessons-learned.md "Phase C follow-up — IL2CPP equivalent"
            // AND drops export time from ~20-40s to ~2-5s on large games.
            // The DLL handler walks every requested assembly server-side
            // under one thread context; no per-class pipe overhead.
            DumpClassInfoResult bulk = await _backend
                .DumpClassInfoAsync(userAssemblies)
                .ConfigureAwait(true);

            (string text, Il2cppHeaderBatchExporter.BatchStats stats) =
                Il2cppHeaderBatchExporter.RenderFromBulk(userAssemblies, bulk);

            await File.WriteAllTextAsync(path, text).ConfigureAwait(true);

            _logger.Write(
                UnityDumpUI.Core.Constants.LogCategoryView,
                $"export il2cpp.h -> {path}: " +
                $"assemblies={stats.AssembliesScanned} " +
                $"classes={stats.ClassesEmitted} " +
                $"failed={stats.ClassesFailed} " +
                $"fields={stats.FieldsEmitted} " +
                $"methods={stats.MethodsEmitted} " +
                $"(server scanned={bulk.Scanned}, truncated={bulk.Truncated})");

            var problems = new List<string>();
            if (stats.ClassesFailed > 0)
            {
                problems.Add(
                    $"{stats.ClassesFailed} class(es) failed ReadClassInfo — see // SKIPPED markers");
            }
            if (bulk.MissingAssemblies is { Count: > 0 } missing)
            {
                problems.Add($"{missing.Count} assemblies missing on the runtime side ({string.Join(", ", missing)})");
            }
            if (bulk.Truncated)
            {
                problems.Add("output truncated at the 1,000,000-row hard cap");
            }
            if (problems.Count > 0)
            {
                ErrorMessage = "il2cpp.h written; " + string.Join("; ", problems) + ".";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"il2cpp.h export failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"export il2cpp.h failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Hand the currently selected class off to the inspector and kick a
    /// `findInstances`. Wired to the "Inspect" button on the Phase 9 tab and
    /// the right-click menu on the Classes list.
    /// </summary>
    [RelayCommand]
    private async Task InspectSelectedClassAsync()
    {
        if (Browser.SelectedAssembly is null || Browser.SelectedClass is null) return;
        Inspector.SetTarget(
            Browser.SelectedAssembly.Name,
            Browser.SelectedClass.Namespace,
            Browser.SelectedClass.Name);
        await Inspector.FindInstancesCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    /// <summary>
    /// Phase 17c — handoff from the Address Lookup tab back to the
    /// Inspector tab. Loads the identified instance into the Inspector
    /// VM and switches the right-pane TabControl. The walk fires
    /// asynchronously via the Inspector's existing
    /// <c>OnSelectedInstanceChanged</c> cascade.
    /// </summary>
    private void OnInspectInstanceRequested(object? sender, InspectInstanceRequest req)
        => InspectInstance(req);

    /// <summary>
    /// Loads the requested instance into the Inspector tab and switches to it.
    /// Shared by the Address Lookup tab's "Inspect this instance" event and
    /// the Field-across-instances modal's double-click / context-menu handoff.
    /// The walk fires asynchronously via the Inspector's existing
    /// <c>OnSelectedInstanceChanged</c> cascade.
    /// </summary>
    public void InspectInstance(InspectInstanceRequest req)
    {
        try
        {
            Inspector.SetTarget(req.Assembly, req.Namespace, req.Class);
            Inspector.FocusOnInstance(req.Instance);
            SelectedDetailTabIndex = TabIndexInspector;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Inspect handoff failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"InspectInstance: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 17c — Inspector "Find what's at this address" right-click
    /// action. Pre-fills the Address Lookup tab's input with the queried
    /// row's effective address, runs the lookup, and switches tabs.
    /// </summary>
    public async Task LookupInspectorAddressAsync(string address)
    {
        if (string.IsNullOrEmpty(address)) return;
        SelectedDetailTabIndex = TabIndexAddressLookup;
        await AddressLookup.PrefillAndLookupAsync(address).ConfigureAwait(true);
    }

    /// <summary>
    /// Phase 25 — handoff from Symbol Finder row activation back to the
    /// Browser. Picks the assembly + class the row points at, pre-fills
    /// the Fields/Methods filter with the member name so the row stands
    /// out, and switches the right-pane tab to Fields or Methods
    /// depending on the row's kind.
    /// </summary>
    private void OnSymbolFinderNavigateRequested(SymbolFinderRow row)
    {
        try
        {
            // SelectedAssembly drives a class-list reload; SelectedClass
            // drives a field+method detail reload. Setting both walks the
            // existing browse cascade with no extra plumbing.
            //
            // Prefer the row's own Assembly (multi-assembly scan populates
            // this per-row), fall back to the finder's SelectedAssembly
            // for back-compat with rows that pre-date the multi-assembly
            // wire field.
            string? rowAssembly = !string.IsNullOrEmpty(row.Assembly)
                ? row.Assembly
                : SymbolFinder.SelectedAssembly?.Name;
            var asm = Browser.Assemblies.FirstOrDefault(
                a => string.Equals(a.Name, rowAssembly, System.StringComparison.Ordinal));
            if (asm is null) return;
            if (!ReferenceEquals(Browser.SelectedAssembly, asm))
                Browser.SelectedAssembly = asm;

            // Class lookup defers — Browser repopulates Classes async after
            // SelectedAssembly fires. Walk the (possibly stale) collection
            // first; if the class isn't there yet, the user can pick it
            // manually after the load settles.
            var cls = Browser.Classes.FirstOrDefault(
                c => string.Equals(c.Namespace, row.Namespace, System.StringComparison.Ordinal)
                  && string.Equals(c.Name,      row.Class,     System.StringComparison.Ordinal));
            if (cls is not null && !ReferenceEquals(Browser.SelectedClass, cls))
                Browser.SelectedClass = cls;

            if (string.Equals(row.Kind, "method", System.StringComparison.Ordinal))
            {
                Browser.MethodFilter   = row.Member;
                SelectedDetailTabIndex = TabIndexMethods;
            }
            else
            {
                Browser.FieldFilter    = row.Member;
                SelectedDetailTabIndex = TabIndexFields;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Navigate from finder failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnSymbolFinderNavigateRequested: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 27d — Value Search → Inspector cross-tab handoff. Same plumbing
    /// pattern as Phase 17c (Address Lookup → Inspector) and Phase 20c
    /// (Scene → Inspector): set the inspector's target to the row's class,
    /// focus on the instance address, switch tab.
    /// </summary>
    private void OnValueSearchNavigateToInspector(ValueSearchRow row)
    {
        try
        {
            if (string.IsNullOrEmpty(row.Class) || string.IsNullOrEmpty(row.InstanceAddr)) return;
            Inspector.SetTarget(row.Assembly, row.Namespace, row.Class);
            Inspector.FocusOnInstance(row.InstanceAddr);
            SelectedDetailTabIndex = TabIndexInspector;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Navigate to inspector failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnValueSearchNavigateToInspector: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 29b — Snapshot diff row → Inspector handoff. Pulls the
    /// (assembly, ns, class, instance address) tuple off the diff row,
    /// drops the Inspector on that instance, switches tab. Diff rows
    /// where the change is at "static" scope have an empty
    /// InstanceAddress; we still switch the inspector's class context
    /// so the user can browse statics from there.
    /// </summary>
    /// <summary>Phase 29d-2 — SPC result row → Inspector handoff. SPC
    /// only carries the instance address (not the surrounding diff-row
    /// context) so the wire is just a string. Inspector lazy-resolves
    /// class context off the address via its existing flow.</summary>
    private void OnSpcNavigateToInspector(object? sender, string instanceAddress)
    {
        if (string.IsNullOrWhiteSpace(instanceAddress)) return;
        try
        {
            _ = LookupInspectorAddressAsync(instanceAddress);
        }
        catch (Exception ex)
        {
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnSpcNavigateToInspector failed: {ex.Message}");
        }
    }

    private void OnSnapshotNavigateToInspector(SnapshotDiffRow row)
    {
        try
        {
            // ClassDisplay = "Namespace.ClassName" (or just ClassName for
            // global-namespace types). Split off the namespace prefix —
            // Inspector.SetTarget expects the bare class name.
            string ns = string.Empty;
            string name = row.ClassDisplay;
            int lastDot = row.ClassDisplay.LastIndexOf('.');
            if (lastDot > 0 && lastDot < row.ClassDisplay.Length - 1)
            {
                ns   = row.ClassDisplay.Substring(0, lastDot);
                name = row.ClassDisplay.Substring(lastDot + 1);
            }
            Inspector.SetTarget(row.Assembly, ns, name);
            if (!string.IsNullOrEmpty(row.InstanceAddress))
                Inspector.FocusOnInstance(row.InstanceAddress);
            SelectedDetailTabIndex = TabIndexInspector;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Navigate from snapshot to inspector failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnSnapshotNavigateToInspector: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 29b — Snapshot diff row → Address Lookup handoff. Fire-and-
    /// forget the prefill+lookup call; failures land in the lookup
    /// VM's own ErrorMessage so the user sees them in the right place.
    /// </summary>
    private void OnSnapshotNavigateToAddressLookup(SnapshotDiffRow row)
    {
        try
        {
            if (string.IsNullOrEmpty(row.InstanceAddress)) return;
            SelectedDetailTabIndex = TabIndexAddressLookup;
            _ = AddressLookup.PrefillAndLookupAsync(row.InstanceAddress);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Navigate from snapshot to address lookup failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnSnapshotNavigateToAddressLookup: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 20c — Scene → Inspector cross-tab handoff. SceneExplorer
    /// fires this when the user right-clicks a tree row → "Inspect this
    /// GameObject". We set the inspector's target to UnityEngine.GameObject,
    /// drop the address in as `SelectedInstance`, and flip the tab.
    ///
    /// Same plumbing pattern as the Phase 19d-2 Methods → Inspector hop
    /// (`OnInspectInstanceRequested`); we route through
    /// <see cref="LiveInspectionViewModel.SetTarget"/> + `FocusOnInstance`
    /// so the existing `walkInstance` cascade kicks off automatically.
    /// </summary>
    private void OnSceneNavigateToInspector(string gameObjectAddress)
    {
        if (string.IsNullOrEmpty(gameObjectAddress)) return;
        try
        {
            Inspector.SetTarget(
                assembly:  "UnityEngine.CoreModule",
                @namespace: "UnityEngine",
                name: "GameObject");
            Inspector.FocusOnInstance(gameObjectAddress);
            SelectedDetailTabIndex = TabIndexInspector;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Inspect from Scene failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"OnSceneNavigateToInspector({gameObjectAddress}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// "Find instances" button on the Inspector tab. When the inspector
    /// already has a target, this re-runs the existing find. When not, it
    /// auto-promotes the currently-selected class (same flow as
    /// <see cref="InspectSelectedClassAsync"/>) so the button is never a
    /// silent no-op for users who picked a class but never used the
    /// right-click "Inspect" menu first.
    /// </summary>
    [RelayCommand]
    private async Task FindInspectorInstancesAsync()
    {
        if (!Inspector.HasTarget)
        {
            await InspectSelectedClassAsync().ConfigureAwait(true);
            return;
        }
        await Inspector.FindInstancesCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _ = Inspector.DisposeAsync();
        _ = _pipe.DisposeAsync();
        _logger.Dispose();
    }
}
