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
               new UnityDumpUI.Platform.WindowsProxyInstaller())
    {
    }

    public MainWindowViewModel(IProcessEnumerator processes,
                                IDllInjector injector,
                                IProxyInstaller proxyInstaller)
    {
        _pipe           = new PipeClient();
        _backend        = new BackendAdapter(_pipe);
        _logger         = new FileLogger();
        _processes      = processes;
        _injector       = injector;
        _proxyInstaller = proxyInstaller;
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

        Browser.Assemblies.Clear();
        Browser.FilteredAssemblies.Clear();
        Browser.Classes.Clear();
        Browser.FilteredClasses.Clear();
        Browser.Fields.Clear();
        Browser.Methods.Clear();
        Browser.SelectedAssembly = null;
        Browser.SelectedClass    = null;
        Browser.ParentChain      = string.Empty;

        Inspector.ClearCommand.Execute(null);
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
        ErrorMessage = null;
        IsBusy = true;
        ConnectionStatus = "Connecting…";
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        try
        {
            await _backend.ConnectAsync(timeoutMs: 3000, _connectCts.Token).ConfigureAwait(true);
            HandshakeResult hs = await _backend.HandshakeAsync(_connectCts.Token).ConfigureAwait(true);

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
            await _pipe.DisposeAsync().ConfigureAwait(true);
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

            // Phase 9: drop inspector state too.
            Inspector.ClearCommand.Execute(null);
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
    /// View-supplied open-file picker. Wired by MainWindow code-behind so
    /// the VM stays UI-agnostic. Returns absolute path or null on cancel.
    /// </summary>
    public Func<string /*title*/, string /*pattern*/, Task<string?>>? PickFileAsync { get; set; }

    [ObservableProperty] private string  _proxyGameExePath = string.Empty;
    [ObservableProperty] private bool    _proxyKindVersion = true;   // radio default
    [ObservableProperty] private bool    _proxyKindWinmm;
    [ObservableProperty] private string? _proxyStatusText;
    [ObservableProperty] private string? _proxyOperationMessage;

    [RelayCommand]
    private async Task BrowseProxyTargetAsync()
    {
        if (PickFileAsync is null) return;
        string? path = await PickFileAsync("Pick game executable", "*.exe").ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;
        ProxyGameExePath = path;
        await RefreshProxyStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshProxyStatusAsync()
    {
        if (string.IsNullOrEmpty(ProxyGameExePath)) { ProxyStatusText = null; return; }
        try
        {
            ProxyInstallStatus st = await _proxyInstaller
                .DetectAsync(ProxyGameExePath, default).ConfigureAwait(true);
            ProxyStatusText = FormatProxyStatus(st);
        }
        catch (Exception ex)
        {
            ProxyStatusText = $"detect failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallProxyAsync()
    {
        if (string.IsNullOrEmpty(ProxyGameExePath)) return;
        ProxyKind kind = ProxyKindWinmm ? ProxyKind.Winmm : ProxyKind.Version;
        ProxyOperationMessage = "Installing…";
        try
        {
            ProxyOperationResult r = await _proxyInstaller
                .InstallAsync(ProxyGameExePath, kind, default).ConfigureAwait(true);
            ProxyOperationMessage = r.Message;
            ProxyStatusText = FormatProxyStatus(r.Status);
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"proxy install {kind} -> {ProxyGameExePath}: {(r.Ok ? "ok" : "fail")} ({r.Message})");
        }
        catch (Exception ex)
        {
            ProxyOperationMessage = $"install failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UninstallProxyAsync()
    {
        if (string.IsNullOrEmpty(ProxyGameExePath)) return;
        ProxyOperationMessage = "Uninstalling…";
        try
        {
            ProxyOperationResult r = await _proxyInstaller
                .UninstallAsync(ProxyGameExePath, default).ConfigureAwait(true);
            ProxyOperationMessage = r.Message;
            ProxyStatusText = FormatProxyStatus(r.Status);
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryPipe,
                $"proxy uninstall -> {ProxyGameExePath}: {(r.Ok ? "ok" : "fail")} ({r.Message})");
        }
        catch (Exception ex)
        {
            ProxyOperationMessage = $"uninstall failed: {ex.Message}";
        }
    }

    private static string FormatProxyStatus(ProxyInstallStatus s)
    {
        if (!string.IsNullOrEmpty(s.ErrorMessage)) return s.ErrorMessage;
        string proxy = s.Kind switch
        {
            ProxyKind.Version => "version.dll installed",
            ProxyKind.Winmm   => "winmm.dll installed",
            _                 => "no proxy installed",
        };
        string backends = (s.HasMonoBackend, s.HasIl2cppBackend) switch
        {
            (true, true)   => "Mono+IL2CPP backends present",
            (true, false)  => "Mono backend present",
            (false, true)  => "IL2CPP backend present",
            _              => "no backend DLLs",
        };
        return s.IsInstalled ? $"{proxy}, {backends}" : $"{proxy} ({backends})";
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
