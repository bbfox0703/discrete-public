// ProxyDeployWindow.axaml.cs — non-modal window code-behind. Owns no logic;
// the VM drives every command. Window lifetime: opened from MainWindow's
// "Proxy Deploy" button, closed by the user via the title-bar X.

using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Models;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class ProxyDeployWindow : Window
{
    public ProxyDeployWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // AOT-safe DataGrid sort — see DataGridSortComparers.cs.
        Loaded += (_, _) =>
            this.FindControl<DataGrid>("GamesGrid")?.WireSortComparers(SortComparers);
    }

    public ProxyDeployWindow(ProxyDeployViewModel vm) : this()
    {
        DataContext = vm;
    }

    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["DisplayName"]          = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.DisplayName),
            ["BackendLabel"]         = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.BackendLabel),
            ["BitnessLabel"]         = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.BitnessLabel),
            ["ProxyLabel"]           = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.ProxyLabel),
            ["DeployStatusLabel"]    = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.DeployStatusLabel),
            ["VersionLabel"]         = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.VersionLabel),
            ["LastOperationMessage"] = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.LastOperationMessage),
            ["ExePath"]              = DataGridSortComparers.Ordinal<ProxyGameRow>(r => r.ExePath),
        };
}
