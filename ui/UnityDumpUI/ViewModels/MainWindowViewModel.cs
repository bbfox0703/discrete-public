// MainWindowViewModel.cs — top-level VM for MainWindow.
//
// CommunityToolkit.Mvvm source generators (no reflection) drive the
// observable properties + commands per the AOT mandate.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly PipeClient _pipe;
    private readonly BackendAdapter _backend;
    private readonly FileLogger _logger;
    private CancellationTokenSource? _connectCts;

    public MainWindowViewModel()
    {
        _pipe    = new PipeClient();
        _backend = new BackendAdapter(_pipe);
        _logger  = new FileLogger();
        _logger.Write(UnityDumpUI.Core.Constants.LogCategoryInit, "MainWindowViewModel created");
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
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
        }
    }

    private bool CanDisconnect() => IsConnected;

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _ = _pipe.DisposeAsync();
        _logger.Dispose();
    }
}
