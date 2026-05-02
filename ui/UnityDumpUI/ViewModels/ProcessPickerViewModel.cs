// ProcessPickerViewModel.cs — VM for the "Pick Unity process" dialog.
//
// Lists processes via IProcessEnumerator (Unity-only by default; full list
// available via the ShowAll toggle for cases where module enumeration was
// blocked). Substring filter mirrors the assembly browser pattern.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.ViewModels;

public sealed partial class ProcessPickerViewModel : ObservableObject
{
    private readonly IProcessEnumerator _enumerator;

    public ProcessPickerViewModel(IProcessEnumerator enumerator)
    {
        _enumerator = enumerator;
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ObservableCollection<ProcessRow> FilteredProcesses { get; } = new();

    [ObservableProperty] private string _filter = string.Empty;
    partial void OnFilterChanged(string value) => Refilter();

    /// <summary>When false, only processes with a detected Unity backend appear.</summary>
    [ObservableProperty] private bool _showAll;
    partial void OnShowAllChanged(bool value) => _ = RefreshAsync();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ProcessRow? _selected;
    public bool HasSelection => Selected is not null;

    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private string? _errorMessage;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            // Off the UI thread — Process.Modules walks can take a while when
            // there are 200+ processes (common on dev boxes).
            IReadOnlyList<ProcessInfo> list = await Task.Run(() =>
                ShowAll ? _enumerator.List() : _enumerator.ListUnityProcesses()
            ).ConfigureAwait(true);

            Processes.Clear();
            foreach (ProcessInfo p in list) Processes.Add(new ProcessRow(p));
            Refilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Refilter()
    {
        FilteredProcesses.Clear();
        string f = Filter ?? string.Empty;
        bool unfiltered = f.Length == 0;
        foreach (ProcessRow r in Processes)
        {
            if (unfiltered ||
                r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                r.Pid.ToString().Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProcesses.Add(r);
            }
        }
    }
}

public sealed class ProcessRow
{
    public ProcessRow(ProcessInfo info)
    {
        Pid     = info.Pid;
        Name    = info.Name;
        Path    = info.Path;
        Backend = info.Backend;
        IsWow64 = info.IsWow64;
    }
    public int          Pid          { get; }
    public string       Name         { get; }
    public string?      Path         { get; }
    public UnityBackend Backend      { get; }
    public bool         IsWow64      { get; }
    public string       BackendLabel => Backend switch
    {
        UnityBackend.IL2CPP => "IL2CPP",
        UnityBackend.Mono   => "Mono",
        _                   => IsWow64 ? "(32-bit)" : "—",
    };
}
