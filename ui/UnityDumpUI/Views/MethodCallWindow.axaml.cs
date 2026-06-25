// MethodCallWindow.axaml.cs — Phase 19d code-behind.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class MethodCallWindow : Window
{
    public MethodCallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public MethodCallWindow(MethodCallViewModel vm) : this()
    {
        DataContext = vm;

        // Phase 19d-3: kick off async enum probes (one getEnumValues
        // round-trip per non-primitive param) once the window is shown.
        // Failures are surfaced via the VM's logger; rows stay on TextBox.
        Opened += async (_, _) =>
        {
            try { await vm.InitializeAsync(); }
            catch { /* surfaced via FileLogger inside the VM */ }
        };
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
