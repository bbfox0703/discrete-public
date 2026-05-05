// ProxyGameRow.cs — UI-side wrapper around UnityGameEntry. Adds the
// per-row checkbox state and a cached install-status enum for the bulk
// proxy-deploy panel. UnityGameEntry stays a pure-data record in Core;
// keeping IsSelected here avoids dragging CommunityToolkit.Mvvm into
// the Core project.

using CommunityToolkit.Mvvm.ComponentModel;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Models;

/// <summary>
/// Row-level deploy verdict used by the proxy-deploy DataGrid's status
/// column. Mirrors UE5CEDumper.Models.ProxyDeployStatus shape but reads
/// directly off our existing <see cref="UnityGameEntry.InstalledProxy"/>
/// — we don't have a per-DLL version stamp yet, so "outdated vs current"
/// collapses into "deployed."
/// </summary>
public enum ProxyDeployStatus
{
    /// <summary>x86 / unknown backend / missing exe — bulk ops skip these.</summary>
    Unsupported,
    /// <summary>x64 + recognised backend, no proxy in place.</summary>
    NotDeployed,
    /// <summary>Our proxy already lives next to the .exe (version.dll or winmm.dll).</summary>
    Deployed,
}

public sealed partial class ProxyGameRow : ObservableObject
{
    public ProxyGameRow(UnityGameEntry game)
    {
        Game = game;
        DeployStatus = !game.IsSupported            ? ProxyDeployStatus.Unsupported
                     : game.InstalledProxy != ProxyKind.None ? ProxyDeployStatus.Deployed
                     :                                          ProxyDeployStatus.NotDeployed;
    }

    public UnityGameEntry Game { get; private set; }

    /// <summary>Bulk-action selection. Bound to the per-row CheckBox.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Result of the most recent install/uninstall on this row.
    /// Surfaced in the Status column when present; cleared on next op.</summary>
    [ObservableProperty] private string? _lastOperationMessage;

    /// <summary>Last operation succeeded — drives status-column colour.</summary>
    [ObservableProperty] private bool _lastOperationOk = true;

    [ObservableProperty] private ProxyDeployStatus _deployStatus;

    // Pass-through display fields. The DataGrid binds to the row, not the
    // wrapped game, so we forward these for compiled-bindings to find them.
    public string DisplayName  => Game.DisplayName;
    public string ExePath      => Game.ExePath;
    public string BackendLabel => Game.BackendLabel;
    public string BitnessLabel => Game.BitnessLabel;
    public string ProxyLabel   => Game.ProxyLabel;
    public bool   IsSupported  => Game.IsSupported;

    public string DeployStatusLabel => DeployStatus switch
    {
        ProxyDeployStatus.Deployed    => $"Deployed ({Game.ProxyLabel})",
        ProxyDeployStatus.NotDeployed => "Not deployed",
        _                             => "Unsupported",
    };

    /// <summary>
    /// Replace the wrapped game with a fresh post-op snapshot, recomputing
    /// the derived status fields. Cheaper than rebuilding the whole row +
    /// re-applying selection state.
    /// </summary>
    public void UpdateFrom(UnityGameEntry fresh)
    {
        Game = fresh;
        DeployStatus = !fresh.IsSupported            ? ProxyDeployStatus.Unsupported
                     : fresh.InstalledProxy != ProxyKind.None ? ProxyDeployStatus.Deployed
                     :                                          ProxyDeployStatus.NotDeployed;
        OnPropertyChanged(nameof(ProxyLabel));
        OnPropertyChanged(nameof(DeployStatusLabel));
    }
}
