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
               new UnityDumpUI.Platform.WindowsDllInjector())
    {
    }

    public MainWindowViewModel(IProcessEnumerator processes, IDllInjector injector)
    {
        _pipe       = new PipeClient();
        _backend    = new BackendAdapter(_pipe);
        _logger     = new FileLogger();
        _processes  = processes;
        _injector   = injector;
        Browser     = new AssemblyBrowserViewModel(_backend, _logger);
        Inspector   = new LiveInspectionViewModel(_backend, _logger);

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

        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit, "MainWindowViewModel created");
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
        string dllPath = Path.Combine(AppContext.BaseDirectory, dllName);

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

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _ = Inspector.DisposeAsync();
        _ = _pipe.DisposeAsync();
        _logger.Dispose();
    }
}
