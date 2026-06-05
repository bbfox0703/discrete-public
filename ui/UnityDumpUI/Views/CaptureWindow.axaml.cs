// CaptureWindow.axaml.cs — snapshot capture configuration, moved off the
// Snapshot tab (2026-06-05) to declutter the result grid. Modeless; bound to
// the SHARED SnapshotViewModel so captures land in the tab's A/B dropdowns.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class CaptureWindow : Window
{
    public CaptureWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <param name="vm">The shared <see cref="SnapshotViewModel"/> instance
    /// (MainWindowViewModel.Snapshot) — NOT a fresh VM — so the capture's
    /// results + progress are shared with the Snapshot tab.</param>
    public CaptureWindow(SnapshotViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
