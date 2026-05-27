// SnapshotSettingsWindow — Phase 29c-7.
//
// Modal dialog backing the Snapshot tab's "⚙ Settings…" button. Edits a
// CLONE of UserSettings so Cancel really cancels; OK returns the updated
// instance and the caller (MainWindow.axaml.cs) hands it to
// SnapshotViewModel.ApplySettingsAsync which persists + may reopen the DB.

using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Views;

public partial class SnapshotSettingsWindow : Window
{
    private readonly UserSettings    _draft;
    private readonly IStorageProvider _storageProvider;

    /// <summary>For the XAML loader. Real construction goes through the
    /// (UserSettings, IStorageProvider) overload.</summary>
    public SnapshotSettingsWindow() : this(new UserSettings(), null!) { }

    public SnapshotSettingsWindow(UserSettings draft, IStorageProvider storageProvider)
    {
        _draft           = draft;
        _storageProvider = storageProvider;
        InitializeComponent();

        // Seed controls from the draft.
        var dirBox       = this.FindControl<TextBox>("DirectoryBox")!;
        var autoBox      = this.FindControl<CheckBox>("AutoSaveCheckbox")!;
        var pruneBox     = this.FindControl<NumericUpDown>("PruneGbBox")!;
        var effectiveDir = this.FindControl<TextBlock>("EffectiveDirHint")!;

        dirBox.Text   = _draft.SnapshotDbDirectory ?? string.Empty;
        autoBox.IsChecked = _draft.AutoSaveSnapshots;
        pruneBox.Value = (decimal)(_draft.AutoPruneThresholdBytes / (1024.0 * 1024.0 * 1024.0));

        UpdateEffectiveDirHint(dirBox, effectiveDir);
        dirBox.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(TextBox.Text))
                UpdateEffectiveDirHint(dirBox, effectiveDir);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static void UpdateEffectiveDirHint(TextBox dirBox, TextBlock hint)
    {
        string text = dirBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string sub = Constants.LogRootSubpath;
            int slash = sub.IndexOf('\\');
            string appRoot = slash > 0 ? sub[..slash] : sub;
            string defaultDir = Path.Combine(local, appRoot, "snapshots");
            hint.Text = $"Using default: {defaultDir}";
        }
        else
        {
            hint.Text = $"Custom: {text}";
        }
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (_storageProvider is null) return;
        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Pick snapshot DB directory",
            AllowMultiple = false,
        });
        var first = folders?.FirstOrDefault();
        if (first is null) return;
        var local = first.TryGetLocalPath();
        if (string.IsNullOrEmpty(local)) return;
        var box = this.FindControl<TextBox>("DirectoryBox")!;
        box.Text = local;
    }

    private void OnResetDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("DirectoryBox")!;
        box.Text = string.Empty;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var dirBox   = this.FindControl<TextBox>("DirectoryBox")!;
        var autoBox  = this.FindControl<CheckBox>("AutoSaveCheckbox")!;
        var pruneBox = this.FindControl<NumericUpDown>("PruneGbBox")!;

        string typedDir = (dirBox.Text ?? string.Empty).Trim();
        _draft.SnapshotDbDirectory = string.IsNullOrEmpty(typedDir) ? null : typedDir;
        _draft.AutoSaveSnapshots   = autoBox.IsChecked ?? false;
        decimal gb = pruneBox.Value ?? 0m;
        _draft.AutoPruneThresholdBytes = (long)(gb * (decimal)(1024.0 * 1024.0 * 1024.0));

        Close(_draft);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
