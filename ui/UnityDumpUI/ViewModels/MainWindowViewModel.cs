// MainWindowViewModel.cs — top-level VM for MainWindow.
//
// CommunityToolkit.Mvvm source generators (no reflection) drive the
// observable properties + commands per the AOT mandate.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly PipeClient         _pipe;
    private readonly BackendAdapter     _backend;
    private readonly FileLogger         _logger;
    private readonly IProcessEnumerator _processes;
    private readonly IDllInjector       _injector;
    private readonly IProxyInstaller    _proxyInstaller;
    private readonly IUnityGameScanner  _gameScanner;
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

        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit, "MainWindowViewModel created");

        // One-shot housekeeping: each game injection creates a new
        // %LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\ tree. They accumulate
        // forever otherwise. Keep the 10 most-recent + the `ui` folder.
        try
        {
            int pruned = FileLogger.PruneOldDllPidDirectories(keepCount: 10);
            if (pruned > 0)
            {
                _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit,
                    $"pruned {pruned} stale DLL log directories (kept newest 10)");
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
    }

    public IProcessEnumerator ProcessEnumerator => _processes;

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
    private bool _isBusy;

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
        string dllPath = Path.Combine(baseDir, dllName);

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
