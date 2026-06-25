// PivotSnapshotsWindow.axaml.cs — Class Pivot UI/UX overhaul Wave B.
using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class PivotSnapshotsWindow : Window
{
    public PivotSnapshotsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public PivotSnapshotsWindow(MainWindowViewModel vm) : this()
    {
        DataContext = vm;
        // The picker is already built in capture-time order (SyncSelectionRows
        // orders by CapturedAtUtc); this makes the header click re-sortable too.
        Loaded += (_, _) =>
            this.FindControl<DataGrid>("PivotSnapshotsGrid")?.WireSortComparers(SortComparers);
    }

    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            // ISO-8601 UTC string → ordinal compare is chronological.
            ["CapturedAt"] = DataGridSortComparers.Ordinal<PivotSnapshotRow>(r => r.Snapshot.CapturedAtUtc),
        };

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
