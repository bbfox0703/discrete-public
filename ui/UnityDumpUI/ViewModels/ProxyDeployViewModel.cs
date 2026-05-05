// ProxyDeployViewModel.cs — backs the dedicated Proxy Deploy window
// (replaces the per-game flyout from earlier Phase 12c). UE5CEDumper-style
// layout: top toolbar (Scan / Refresh / SelectAll / UnselectAll / Invert
// + default-kind radios), a multi-select DataGrid of detected games, and
// a bottom action row (Install / Uninstall / UpdateAll).
//
// Bulk semantics:
//   Install   — install DefaultProxyKind into every SELECTED & supported
//               row. If a row already has the OTHER kind, the installer's
//               mutex install swaps it (handled by WindowsProxyInstaller).
//   Uninstall — remove every proxy/backend file from each SELECTED row,
//               regardless of which kind is currently in place.
//   UpdateAll — re-install (with the row's CURRENT kind, preserving the
//               user's per-game choice) for every row that already has a
//               proxy. Selection ignored. Useful after rebuilding DiscreteUI
//               to push fresh DLLs to every game in one click.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Models;

namespace UnityDumpUI.ViewModels;

public sealed partial class ProxyDeployViewModel : ObservableObject
{
    private readonly IUnityGameScanner _scanner;
    private readonly IProxyInstaller   _installer;
    private CancellationTokenSource?   _scanCts;

    public ProxyDeployViewModel(IUnityGameScanner scanner, IProxyInstaller installer)
    {
        _scanner   = scanner;
        _installer = installer;
    }

    public ObservableCollection<ProxyGameRow> Games         { get; } = new();
    public ObservableCollection<ProxyGameRow> FilteredGames { get; } = new();

    [ObservableProperty] private string _filter = string.Empty;
    partial void OnFilterChanged(string value) => Refilter();

    /// <summary>Toggle on to surface 32-bit / Unknown rows (Phase 12d gate).
    /// Off by default — bulk ops can't act on them anyway.</summary>
    [ObservableProperty] private bool _showUnsupported;
    partial void OnShowUnsupportedChanged(bool value) => Refilter();

    /// <summary>"Default kind" radio. Version is preferred (Unity always
    /// imports it); winmm is the fallback for games where another tool
    /// has already claimed version.dll.</summary>
    [ObservableProperty] private bool _defaultKindVersion = true;
    [ObservableProperty] private bool _defaultKindWinmm;

    [ObservableProperty] private bool    _isScanning;
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>"3 selected · 12 deployed · 18 total" label above the grid.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>Free-form last-bulk-op message, e.g. "Installed in 3 of 3 games"
    /// or "1 failed: see error column." Cleared on next op.</summary>
    [ObservableProperty] private string? _lastBulkMessage;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ErrorMessage    = null;
        LastBulkMessage = null;
        IsScanning      = true;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;
        try
        {
            IReadOnlyList<UnityGameEntry> list = await _scanner
                .ScanAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            // Preserve selection across refresh by exe path.
            HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (ProxyGameRow r in Games)
                if (r.IsSelected) selectedPaths.Add(r.ExePath);

            UnregisterRowHandlers();
            Games.Clear();
            foreach (UnityGameEntry e in list)
            {
                var row = new ProxyGameRow(e)
                {
                    IsSelected = selectedPaths.Contains(e.ExePath),
                };
                row.PropertyChanged += OnRowPropertyChanged;
                Games.Add(row);
            }
            Refilter();
            UpdateSummary();
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand] private void SelectAll()      => SetSelection(true,  onlySupported: true);
    [RelayCommand] private void UnselectAll()    => SetSelection(false, onlySupported: false);
    [RelayCommand] private void InvertSelection()
    {
        foreach (ProxyGameRow r in FilteredGames)
        {
            if (!r.IsSupported) continue;
            r.IsSelected = !r.IsSelected;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        ProxyKind kind = DefaultKindWinmm ? ProxyKind.Winmm : ProxyKind.Version;
        await RunBulkAsync("Install", row => _installer
            .InstallAsync(row.ExePath, kind, default),
            row => row.IsSelected && row.IsSupported).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        await RunBulkAsync("Uninstall", row => _installer
            .UninstallAsync(row.ExePath, default),
            row => row.IsSelected && row.DeployStatus == ProxyDeployStatus.Deployed)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Re-install every already-deployed row with whatever proxy kind it
    /// currently has. Ignores selection — answers "I rebuilt DiscreteUI,
    /// push fresh DLLs to every game with a proxy."
    /// </summary>
    [RelayCommand]
    private async Task UpdateAllDeployedAsync()
    {
        await RunBulkAsync("Update", row => _installer
            .InstallAsync(row.ExePath, row.Game.InstalledProxy, default),
            row => row.DeployStatus == ProxyDeployStatus.Deployed
                && row.Game.InstalledProxy != ProxyKind.None).ConfigureAwait(true);
    }

    // ---------- Helpers --------------------------------------------------

    private void SetSelection(bool value, bool onlySupported)
    {
        foreach (ProxyGameRow r in FilteredGames)
        {
            if (onlySupported && !r.IsSupported) continue;
            r.IsSelected = value;
        }
    }

    private async Task RunBulkAsync(
        string verb,
        Func<ProxyGameRow, Task<ProxyOperationResult>> op,
        Func<ProxyGameRow, bool> rowFilter)
    {
        if (IsBusy) return;
        IsBusy          = true;
        ErrorMessage    = null;
        LastBulkMessage = null;

        int ok = 0, fail = 0, skipped = 0;
        try
        {
            foreach (ProxyGameRow row in Games)
            {
                if (!rowFilter(row)) { ++skipped; continue; }
                try
                {
                    ProxyOperationResult r = await op(row).ConfigureAwait(true);
                    row.LastOperationOk      = r.Ok;
                    row.LastOperationMessage = r.Message;
                    // Refresh status from the post-op snapshot the installer
                    // attached to the result — cheaper than a second Detect.
                    var fresh = new UnityGameEntry(
                        row.Game.ExePath, row.Game.GameDirectory, row.Game.DisplayName,
                        row.Game.Backend, row.Game.Bitness,
                        row.Game.DataDirectoryName, r.Status.Kind, row.Game.Source);
                    row.UpdateFrom(fresh);
                    if (r.Ok) ++ok; else ++fail;
                }
                catch (Exception ex)
                {
                    row.LastOperationOk      = false;
                    row.LastOperationMessage = ex.Message;
                    ++fail;
                }
            }
            int touched = ok + fail;
            LastBulkMessage = touched == 0
                ? $"{verb}: nothing to do (no matching rows)"
                : $"{verb}: {ok}/{touched} ok" + (fail > 0 ? $", {fail} failed" : "");
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    private void Refilter()
    {
        FilteredGames.Clear();
        string f = Filter ?? string.Empty;
        bool unfiltered = f.Length == 0;
        foreach (ProxyGameRow r in Games)
        {
            if (!ShowUnsupported && !r.IsSupported) continue;
            if (unfiltered ||
                r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                r.ExePath.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                FilteredGames.Add(r);
            }
        }
    }

    private void UpdateSummary()
    {
        int selected = 0, deployed = 0;
        foreach (ProxyGameRow r in Games)
        {
            if (r.IsSelected) ++selected;
            if (r.DeployStatus == ProxyDeployStatus.Deployed) ++deployed;
        }
        SummaryText = $"{selected} selected · {deployed} deployed · {Games.Count} total";
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProxyGameRow.IsSelected)
                            or nameof(ProxyGameRow.DeployStatus))
        {
            UpdateSummary();
        }
    }

    private void UnregisterRowHandlers()
    {
        foreach (ProxyGameRow r in Games)
            r.PropertyChanged -= OnRowPropertyChanged;
    }
}
