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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Models;

namespace UnityDumpUI.ViewModels;

public sealed partial class ProxyDeployViewModel : ObservableObject, IDisposable
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

    /// <summary>Show the supported 32-bit Mono rows. On by default — x86 Mono
    /// is a first-class supported target (x86 backend + proxy ship in
    /// dist/x86/), so it lists like any x64 game. Uncheck to hide 32-bit Mono
    /// when working a mixed library. Split out of the old combined
    /// "32-bit / unsupported" toggle so the two concerns are independent.</summary>
    [ObservableProperty] private bool _show32BitMono = true;
    partial void OnShow32BitMonoChanged(bool value) => Refilter();

    /// <summary>Toggle on to surface truly-unsupported rows (x86 IL2CPP —
    /// no 32-bit IL2CPP backend — Unknown backend, ARM64 / corrupt PE). Off
    /// by default: bulk ops can't act on them anyway. Distinct from
    /// <see cref="Show32BitMono"/>, which gates the SUPPORTED 32-bit Mono
    /// rows.</summary>
    [ObservableProperty] private bool _showUnsupported;
    partial void OnShowUnsupportedChanged(bool value) => Refilter();

    /// <summary>"Default kind" radio. Version is preferred (Unity always
    /// imports it); winmm is the fallback for games where another tool
    /// has already claimed version.dll.</summary>
    [ObservableProperty] private bool _defaultKindVersion = true;
    [ObservableProperty] private bool _defaultKindWinmm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(CanRunBulk))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllDeployedCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(CanRunBulk))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllDeployedCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;

    /// <summary>True when neither a scan nor a bulk proxy op is in flight.</summary>
    public bool CanRefresh => !IsScanning && !IsBusy;

    /// <summary>True when neither a bulk proxy op nor a library scan is in flight.</summary>
    public bool CanRunBulk => !IsBusy && !IsScanning;

    /// <summary>"3 selected · 12 deployed · 18 total" label above the grid.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>Free-form last-bulk-op message, e.g. "Installed in 3 of 3 games"
    /// or "1 failed: see error column." Cleared on next op.</summary>
    [ObservableProperty] private string? _lastBulkMessage;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        ErrorMessage    = null;
        LastBulkMessage = null;
        IsScanning      = true;
        CancellationTokenSource? prev = _scanCts;
        var myCts = new CancellationTokenSource();
        _scanCts = myCts;
        try { prev?.Cancel(); } catch (ObjectDisposedException) { }
        CancellationToken ct = myCts.Token;
        try
        {
            IReadOnlyList<UnityGameEntry> list = await _scanner
                .ScanAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || IsBusy || !ReferenceEquals(_scanCts, myCts)) return;

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
            if (ReferenceEquals(_scanCts, myCts))
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, myCts))
            {
                IsScanning = false;
                _scanCts = null;
            }
            myCts.Dispose();
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

    [RelayCommand(CanExecute = nameof(CanRunBulk))]
    private async Task InstallSelectedAsync()
    {
        ProxyKind kind = DefaultKindWinmm ? ProxyKind.Winmm : ProxyKind.Version;
        // A-L17: Act ONLY on currently visible (FilteredGames) rows so hidden
        // rows selected before changing the filter are never modified silently.
        await RunBulkAsync("Install", row => _installer
            .InstallAsync(row.ExePath, kind, default),
            row => row.IsSelected && row.IsSupported,
            useFiltered: true).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunBulk))]
    private async Task UninstallSelectedAsync()
    {
        // Match every "currently deployed" state, not just the unverifiable
        // fallback. After the version-stamping work most rows live in
        // DeployedCurrent / DeployedOutdated — earlier code checked only
        // ProxyDeployStatus.Deployed and silently skipped them all.
        // A-L17: Scope selection-based bulk ops to visible FilteredGames rows.
        await RunBulkAsync("Uninstall", row => _installer
            .UninstallAsync(row.ExePath, default),
            row => row.IsSelected && IsDeployed(row),
            useFiltered: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Re-install every already-deployed row with whatever proxy kind it
    /// currently has. Ignores selection — answers "I rebuilt DiscreteUI,
    /// push fresh DLLs to every game with a proxy."
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBulk))]
    private async Task UpdateAllDeployedAsync()
    {
        await RunBulkAsync("Update", row => _installer
            .InstallAsync(row.ExePath, row.Game.InstalledProxy, default),
            row => row.Game.InstalledProxy != ProxyKind.None && IsDeployed(row),
            useFiltered: false)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// True when a row's proxy DLL is on disk in any form — the version-
    /// stamped <c>DeployedCurrent</c> / <c>DeployedOutdated</c> states plus
    /// the unverifiable <c>Deployed</c> fallback. Used by every "act on
    /// deployed rows" filter so they stay in sync.
    /// </summary>
    private static bool IsDeployed(ProxyGameRow row) =>
        row.DeployStatus is ProxyDeployStatus.Deployed
                         or ProxyDeployStatus.DeployedCurrent
                         or ProxyDeployStatus.DeployedOutdated;

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
        Func<ProxyGameRow, bool> rowFilter,
        bool useFiltered)
    {
        if (IsBusy || IsScanning) return;
        IsBusy          = true;
        ErrorMessage    = null;
        LastBulkMessage = null;

        int ok = 0, fail = 0, skipped = 0;
        try
        {
            // Snapshot the row list before awaiting any IO so a concurrent
            // collection mutation can never throw InvalidOperationException
            // mid-foreach (A-M10). Selection-driven ops snapshot FilteredGames
            // so filter-hidden rows are never modified unexpectedly (A-L17).
            var snapshot = useFiltered ? FilteredGames.ToList() : Games.ToList();
            foreach (ProxyGameRow row in snapshot)
            {
                if (!rowFilter(row)) { ++skipped; continue; }
                try
                {
                    ProxyOperationResult r = await op(row).ConfigureAwait(true);
                    row.LastOperationOk      = r.Ok;
                    row.LastOperationMessage = r.Message;
                    // Refresh status from the post-op snapshot the installer
                    // attached to the result — cheaper than a second Detect.
                    // Pass r.Status into UpdateFrom so the row's
                    // InstalledVersion / SourceVersion / ForeignProxyPresent /
                    // DeployStatus all reflect the version comparison after the op.
                    var fresh = new UnityGameEntry(
                        row.Game.ExePath, row.Game.GameDirectory, row.Game.DisplayName,
                        row.Game.Backend, row.Game.Bitness,
                        row.Game.DataDirectoryName, r.Status.Kind, row.Game.Source,
                        installedProxyVersion: r.Status.InstalledProxyVersion,
                        sourceProxyVersion:    r.Status.SourceProxyVersion,
                        foreignProxyPresent:   r.Status.ForeignProxyPresent);
                    row.UpdateFrom(fresh, r.Status);
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
            // Two independent visibility gates:
            //   • supported 32-bit Mono → "Show 32-bit Mono" (on by default)
            //   • truly-unsupported     → "Show unsupported"  (off by default)
            // 32-bit Mono is the only supported 32-bit case, so the two sets
            // never overlap.
            if (r.Is32BitMono)
            {
                if (!Show32BitMono) continue;
            }
            else if (!r.IsSupported && !ShowUnsupported)
            {
                continue;
            }
            if (unfiltered ||
                r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                r.ExePath.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                FilteredGames.Add(r);
            }
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int visibleSelected = 0, totalSelected = 0, deployed = 0, outdated = 0;
        var visibleSet = new HashSet<ProxyGameRow>(FilteredGames);
        foreach (ProxyGameRow r in Games)
        {
            if (r.IsSelected)
            {
                ++totalSelected;
                if (visibleSet.Contains(r)) ++visibleSelected;
            }
            if (IsDeployed(r)) ++deployed;
            if (r.DeployStatus == ProxyDeployStatus.DeployedOutdated) ++outdated;
        }
        int hiddenSelected = totalSelected - visibleSelected;
        string hiddenSuffix = hiddenSelected > 0 ? $" ({hiddenSelected} hidden)" : string.Empty;
        string outdatedSuffix = outdated > 0 ? $" ({outdated} outdated)" : string.Empty;
        SummaryText = $"{visibleSelected} selected{hiddenSuffix} · {deployed} deployed{outdatedSuffix} · {Games.Count} total";
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

    /// <summary>A-L17: Cancel any active library scan and release row handlers.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _scanCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        UnregisterRowHandlers();
    }
}
