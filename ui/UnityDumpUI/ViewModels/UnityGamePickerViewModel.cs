// UnityGamePickerViewModel.cs — VM for the "Pick Unity game (Steam scan)"
// dialog. Mirrors ProcessPickerViewModel's structure: refresh, filter,
// selected row, OK/Cancel via Window.Close(result).
//
// The dialog is purely a scanner + selector — actual install/uninstall
// runs through the existing flyout wired in MainWindowViewModel. Caller
// awaits ShowDialog<UnityGameEntry?>(owner); a non-null return means
// "the user picked this game's exe path, populate the flyout with it."

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.ViewModels;

public sealed partial class UnityGamePickerViewModel : ObservableObject
{
    private readonly IUnityGameScanner _scanner;
    private CancellationTokenSource?   _scanCts;

    public UnityGamePickerViewModel(IUnityGameScanner scanner)
    {
        _scanner = scanner;
    }

    public ObservableCollection<UnityGameEntry> Games          { get; } = new();
    public ObservableCollection<UnityGameEntry> FilteredGames  { get; } = new();

    [ObservableProperty] private string _filter = string.Empty;
    partial void OnFilterChanged(string value) => Refilter();

    /// <summary>When false (default), only games the dumper actively supports
    /// (x64 + recognised backend) appear. Toggle on to see 32-bit / Unknown
    /// rows for diagnosis.</summary>
    [ObservableProperty] private bool _showUnsupported;
    partial void OnShowUnsupportedChanged(bool value) => Refilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private UnityGameEntry? _selected;
    public bool HasSelection => Selected is not null;

    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private string? _errorMessage;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;
        try
        {
            IReadOnlyList<UnityGameEntry> list = await _scanner
                .ScanAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            Games.Clear();
            foreach (UnityGameEntry e in list) Games.Add(e);
            Refilter();
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Refilter()
    {
        FilteredGames.Clear();
        string f = Filter ?? string.Empty;
        bool unfiltered = f.Length == 0;
        foreach (UnityGameEntry e in Games)
        {
            if (!ShowUnsupported && !e.IsSupported) continue;
            if (unfiltered ||
                e.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                e.GameDirectory.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                FilteredGames.Add(e);
            }
        }
    }
}
