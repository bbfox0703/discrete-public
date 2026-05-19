// ProcessPickerWindow.axaml.cs — code-behind for the process picker dialog.
//
// Returns the selected ProcessRow (or null on cancel) via Window.Close(result).
// Caller awaits ShowDialog<ProcessRow?>(owner).

using System.Collections;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class ProcessPickerWindow : Window
{
    public ProcessPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // AOT-safe DataGrid sort — see DataGridSortComparers.cs.
        Loaded += (_, _) =>
            this.FindControl<DataGrid>("ProcessesGrid")?.WireSortComparers(SortComparers);
    }

    public ProcessPickerWindow(ProcessPickerViewModel vm) : this()
    {
        DataContext = vm;
    }

    private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
        new Dictionary<string, IComparer>
        {
            ["Pid"]          = DataGridSortComparers.Number <ProcessRow>(r => r.Pid),
            ["Name"]         = DataGridSortComparers.Ordinal<ProcessRow>(r => r.Name),
            ["BackendLabel"] = DataGridSortComparers.Ordinal<ProcessRow>(r => r.BackendLabel),
            ["Path"]         = DataGridSortComparers.Ordinal<ProcessRow>(r => r.Path),
        };

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnAcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProcessPickerViewModel vm && vm.Selected is { } row)
        {
            Close(row);
        }
        else
        {
            Close(null);
        }
    }
}
