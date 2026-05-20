// MainWindowViewModel.cs — top-level VM for MainWindow.
//
// CommunityToolkit.Mvvm source generators (no reflection) drive the
// observable properties + commands per the AOT mandate.

using System;
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
    public const int TabIndexFields        = 0;
    public const int TabIndexInspector     = 1;
    public const int TabIndexMethods       = 2;
    public const int TabIndexAddressLookup = 3;
    public const int TabIndexSymbolFinder  = 4;
    public const int TabIndexScene         = 5;
    public const int TabIndexValueSearch   = 6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectAndConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectDiscreteBridgeCommand))]
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
    [ObservableProperty] private string? _backend2;     // "Backend" collides with field above
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

            // Phase 8: kick off assembly fetch so the left pane populates
            // without an extra click. Failures surface on Browser.ErrorMessage.
            _ = Browser.RefreshAssembliesCommand.ExecuteAsync(null);

            // Phase 14b: opportunistic metadata-stats call so the user
            // sees a "stripped/obfuscated" banner up front rather than
            // discovering it through garbled class names. Soft-fail.
            _ = LoadMetadataStatsAsync();
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
                $"OnInspectInstanceRequested: {ex.GetType().Name}: {ex.Message}");
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
