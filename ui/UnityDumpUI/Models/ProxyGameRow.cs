// ProxyGameRow.cs — UI-side wrapper around UnityGameEntry. Adds the
// per-row checkbox state, a cached install-status enum, and the
// installed-vs-source proxy version comparison used by the bulk
// proxy-deploy panel. UnityGameEntry stays a pure-data record in Core;
// keeping IsSelected + ObservableObject machinery here avoids dragging
// CommunityToolkit.Mvvm into the Core project.

using CommunityToolkit.Mvvm.ComponentModel;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Models;

/// <summary>
/// Row-level deploy verdict used by the proxy-deploy DataGrid's status
/// column. <see cref="DeployedCurrent"/> and <see cref="DeployedOutdated"/>
/// are derived from the PE FileVersion of the installed proxy DLL vs the
/// one in <c>dist/</c> (read by <see cref="WindowsProxyInstaller"/>'s
/// <c>FileVersionInfo</c> probe). <see cref="Deployed"/> is the
/// fallback when version info is unreadable on either side (e.g. an
/// older build with no <c>version.rc</c> baked in).
/// </summary>
public enum ProxyDeployStatus
{
    /// <summary>Unsupported target — bulk ops skip these. Covers x86 IL2CPP
    /// (no 32-bit IL2CPP backend), unknown backend, and missing exe. Mono is
    /// supported on both x64 and x86.</summary>
    Unsupported,
    /// <summary>Supported target (x64 Mono/IL2CPP or x86 Mono), no proxy in place.</summary>
    NotDeployed,
    /// <summary>Proxy installed and FileVersion matches <c>dist/</c>.</summary>
    DeployedCurrent,
    /// <summary>Proxy installed but FileVersion differs from <c>dist/</c>
    /// — user should run "Update all deployed".</summary>
    DeployedOutdated,
    /// <summary>Proxy installed but version info couldn't be read on at
    /// least one side. Treat as "good enough" — uninstall still works.</summary>
    Deployed,
}

public sealed partial class ProxyGameRow : ObservableObject
{
    public ProxyGameRow(UnityGameEntry game)
    {
        Game = game;
        ApplyStatusForGame(game,
            installedProxyVersion: game.InstalledProxyVersion,
            sourceProxyVersion:    game.SourceProxyVersion);
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

    /// <summary>PE FileVersion of the installed proxy (e.g. "0.1.0.135").
    /// <c>null</c> when not installed or unreadable.</summary>
    [ObservableProperty] private string? _installedVersion;

    /// <summary>PE FileVersion of the proxy DLL in <c>dist/</c>
    /// (e.g. "0.1.0.140"). Same string for every row — surfaced per-row
    /// so the comparison is visible directly in the grid.</summary>
    [ObservableProperty] private string? _sourceVersion;

    // Pass-through display fields. The DataGrid binds to the row, not the
    // wrapped game, so we forward these for compiled-bindings to find them.
    public string DisplayName  => Game.DisplayName;
    public string ExePath      => Game.ExePath;
    public string BackendLabel => Game.BackendLabel;
    public string BitnessLabel => Game.BitnessLabel;
    public string ProxyLabel   => Game.ProxyLabel;
    public bool   IsSupported  => Game.IsSupported;
    public bool   Is32BitMono  => Game.Is32BitMono;

    public string DeployStatusLabel => DeployStatus switch
    {
        ProxyDeployStatus.DeployedCurrent  => $"Deployed ({Game.ProxyLabel}, current)",
        ProxyDeployStatus.DeployedOutdated => $"Deployed ({Game.ProxyLabel}, OUTDATED)",
        ProxyDeployStatus.Deployed         => $"Deployed ({Game.ProxyLabel})",
        ProxyDeployStatus.NotDeployed      => "Not deployed",
        _                                  => "Unsupported",
    };

    /// <summary>"0.1.0.135 → 0.1.0.140" when outdated; just "0.1.0.140" when
    /// current; bare source version when not deployed; "unknown → 0.1.0.140"
    /// when an old un-stamped proxy is installed but a stamped source exists.
    /// Empty otherwise. Bound to the "Version" column in the deploy grid.</summary>
    public string VersionLabel => DeployStatus switch
    {
        ProxyDeployStatus.DeployedOutdated =>
            $"{(string.IsNullOrEmpty(InstalledVersion) ? "unknown" : InstalledVersion)} → " +
            $"{(string.IsNullOrEmpty(SourceVersion) ? "?" : SourceVersion)}",
        ProxyDeployStatus.DeployedCurrent  => SourceVersion ?? string.Empty,
        ProxyDeployStatus.Deployed         => InstalledVersion ?? SourceVersion ?? string.Empty,
        ProxyDeployStatus.NotDeployed      => SourceVersion is { Length: > 0 } v ? $"(would install {v})" : string.Empty,
        _                                  => string.Empty,
    };

    /// <summary>
    /// Replace the wrapped game with a fresh post-op snapshot, recomputing
    /// the derived status + version fields. Cheaper than rebuilding the
    /// whole row + re-applying selection state. The <paramref name="status"/>
    /// argument carries the version strings detected by the installer.
    /// </summary>
    public void UpdateFrom(UnityGameEntry fresh, ProxyInstallStatus? status = null)
    {
        Game = fresh;
        ApplyStatusForGame(fresh,
            installedProxyVersion: status?.InstalledProxyVersion,
            sourceProxyVersion:    status?.SourceProxyVersion);
        OnPropertyChanged(nameof(ProxyLabel));
    }

    private void ApplyStatusForGame(UnityGameEntry game,
                                    string? installedProxyVersion,
                                    string? sourceProxyVersion)
    {
        InstalledVersion = installedProxyVersion;
        SourceVersion    = sourceProxyVersion;

        if (!game.IsSupported)
        {
            DeployStatus = ProxyDeployStatus.Unsupported;
        }
        else if (game.InstalledProxy == ProxyKind.None)
        {
            DeployStatus = ProxyDeployStatus.NotDeployed;
        }
        else if (!string.IsNullOrEmpty(installedProxyVersion)
              && !string.IsNullOrEmpty(sourceProxyVersion))
        {
            DeployStatus = string.Equals(installedProxyVersion, sourceProxyVersion, System.StringComparison.Ordinal)
                ? ProxyDeployStatus.DeployedCurrent
                : ProxyDeployStatus.DeployedOutdated;
        }
        else if (string.IsNullOrEmpty(installedProxyVersion)
              && !string.IsNullOrEmpty(sourceProxyVersion))
        {
            // Source has a stamped version but the installed proxy doesn't —
            // overwhelmingly that means the installed copy was deployed by a
            // pre-version-stamping build of DiscreteUI (≤ build #140 or so)
            // and is by definition outdated. Flag for redeploy.
            DeployStatus = ProxyDeployStatus.DeployedOutdated;
        }
        else
        {
            // Source DLL itself has no version (dev-side scenario: DLL not
            // built yet). Treat as the unverifiable fallback.
            DeployStatus = ProxyDeployStatus.Deployed;
        }
        OnPropertyChanged(nameof(DeployStatusLabel));
        OnPropertyChanged(nameof(VersionLabel));
    }
}
