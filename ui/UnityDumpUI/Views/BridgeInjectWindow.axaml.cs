// BridgeInjectWindow — feature picker for "Inject Discrete Bridge into CE
// Table". The user ticks which optional Lua tools to embed; Core
// (utils.lua + unity_dump.lua) is always included. OK returns the selected
// BridgeFeature set, which the caller feeds to
// BridgeLuaResources.ResolveFiles → BridgeInjector.InjectAsync so the CT
// only carries — and the AA script only loads — the picked subset.
//
// Mirrors SnapshotSettingsWindow's plain-code-behind pattern (FindControl
// by Name, Close(result)) — no MVVM binding, so it's Native-AOT clean.

using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Views;

public partial class BridgeInjectWindow : Window
{
    public BridgeInjectWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnInjectClicked(object? sender, RoutedEventArgs e)
    {
        var features = new List<BridgeLuaResources.BridgeFeature>();

        void Add(string name, BridgeLuaResources.BridgeFeature feature)
        {
            var box = this.FindControl<CheckBox>(name);
            if (box?.IsChecked == true) features.Add(feature);
        }

        Add("DissectCheckbox",       BridgeLuaResources.BridgeFeature.Dissect);
        Add("SymbolsCheckbox",       BridgeLuaResources.BridgeFeature.Symbols);
        Add("AddressFinderCheckbox", BridgeLuaResources.BridgeFeature.AddressFinder);
        Add("InvokeCheckbox",        BridgeLuaResources.BridgeFeature.Invoke);
        Add("SceneCheckbox",         BridgeLuaResources.BridgeFeature.Scene);

        Close(features);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
