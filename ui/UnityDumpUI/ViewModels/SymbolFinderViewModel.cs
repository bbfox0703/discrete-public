// SymbolFinderViewModel.cs — Phase 25.
//
// Cross-class field + method finder. Wraps the `searchSymbols` pipe RPC
// so the user can hunt a property like "MaxHP" across every class in an
// assembly with one keystroke, instead of clicking each Classes entry
// in turn through the Browser pane.
//
// Surface (right-pane TabItem):
//   * Assembly dropdown — mirrors Browser.Assemblies; defaults to whatever
//     the Browser currently has selected. Empty when not connected.
//   * Pattern textbox — ASCII case-insensitive substring; Enter triggers Search.
//   * Kind toggles — Fields and/or Methods; refuses to submit when both off.
//   * Limit numeric — default 200, max 5000 (DLL-side cap).
//   * Results grid — flat rows: Kind / Class / Member / Type / Offset / Address / Static.
//
// Navigation hook: NavigateRequested fires when the user activates a row.
// MainWindowViewModel handles the cross-tab jump (select assembly → select
// class → switch to Fields/Methods tab depending on row kind).
//
// AOT-safe: CommunityToolkit.Mvvm source-gen + ASCII string ops only.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class SymbolFinderViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _searchCts;

    public SymbolFinderViewModel(BackendAdapter backend, FileLogger logger,
                                 ObservableCollection<AssemblyItem> assemblies)
    {
        _backend   = backend;
        _logger    = logger;
        Assemblies = assemblies;
    }

    /// <summary>
    /// Mirrors <see cref="AssemblyBrowserViewModel.Assemblies"/>. Same
    /// collection instance — when the Browser refreshes its assembly list
    /// after handshake, the dropdown picks it up automatically.
    /// </summary>
    public ObservableCollection<AssemblyItem> Assemblies { get; }

    public ObservableCollection<SymbolFinderRow> Matches { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private AssemblyItem? _selectedAssembly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _pattern = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _includeFields = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _includeMethods = true;

    [ObservableProperty]
    private int _limit = 200;

    /// <summary>
    /// When true, the search bypasses <see cref="SelectedAssembly"/> and
    /// ships every user assembly (anything <see cref="SystemAssemblyFilter.IsSystemAssembly"/>
    /// returns false for) to the DLL under one global limit. Lets the user
    /// hunt a property like "MaxHP" across the whole gameplay surface in
    /// one click instead of cycling through every Assembly-CSharp-firstpass
    /// / third-party DLL by hand.
    /// </summary>
    /// <remarks>
    /// Affects <see cref="SearchCommand"/>'s CanExecute: with the toggle
    /// on, SelectedAssembly is no longer required (the ComboBox can stay
    /// empty / disabled). Wire path branches in <see cref="SearchAsync"/>.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyPropertyChangedFor(nameof(IsAssemblyPickerEnabled))]
    private bool _searchAllUserAssemblies;

    /// <summary>
    /// XAML-bound enable flag for the assembly ComboBox. Greys out when
    /// the "Search all user assemblies" checkbox is on.
    /// </summary>
    public bool IsAssemblyPickerEnabled => !SearchAllUserAssemblies;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Pick an assembly, type a name substring, press Search.";

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Raised when the user activates a result row (double-click or Enter).
    /// MainWindowViewModel handles the cross-tab jump — selects the class
    /// in the Browser and switches to Fields/Methods tab depending on
    /// <see cref="SymbolFinderRow.Kind"/>.
    /// </summary>
    public event Action<SymbolFinderRow>? NavigateRequested;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy        = true;
        ErrorMessage  = null;
        StatusMessage = "Searching…";
        Matches.Clear();

        var pat = (Pattern ?? string.Empty).Trim();
        var kinds = new List<string>();
        if (IncludeFields)  kinds.Add("field");
        if (IncludeMethods) kinds.Add("method");

        // Branch on the "all user assemblies" toggle. Multi-assembly path
        // computes the user-assembly set from the same Assemblies list
        // the ComboBox draws from; system / Unity engine / BCL get
        // filtered out via SystemAssemblyFilter.
        List<string>? userAssemblies = null;
        string scopeLabel;
        if (SearchAllUserAssemblies)
        {
            userAssemblies = SystemAssemblyFilter.SelectUserAssemblies(
                Assemblies.Select(a => a.Name));
            if (userAssemblies.Count == 0)
            {
                IsBusy        = false;
                ErrorMessage  = "No user assemblies — every loaded assembly looks like a Unity / system module. Untick the checkbox and pick one explicitly.";
                StatusMessage = "Search aborted.";
                return;
            }
            scopeLabel = $"{userAssemblies.Count} user assembl{(userAssemblies.Count == 1 ? "y" : "ies")}";
        }
        else
        {
            scopeLabel = SelectedAssembly!.Name;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            SearchSymbolsResult result = userAssemblies is not null
                ? await _backend.SearchSymbolsAsync(userAssemblies, pat, kinds, Limit, ct).ConfigureAwait(true)
                : await _backend.SearchSymbolsAsync(SelectedAssembly!.Name, pat, kinds, Limit, ct).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                StatusMessage = "Search failed — see error above.";
                return;
            }

            foreach (var m in result.Matches)
                Matches.Add(new SymbolFinderRow(m));

            sw.Stop();
            string missingNote = result.MissingAssemblies is { Count: > 0 }
                ? $" {result.MissingAssemblies.Count} assembl{(result.MissingAssemblies.Count == 1 ? "y" : "ies")} not found in the backend (probably stripped or renamed)."
                : string.Empty;
            StatusMessage = result.Truncated
                ? $"{Matches.Count} hit(s) (truncated at limit={Limit}); scanned {result.ScannedClasses} class(es) across {scopeLabel} in {sw.ElapsedMilliseconds} ms. Narrow the pattern or raise the limit.{missingNote}"
                : $"{Matches.Count} hit(s) in {result.ScannedClasses} class(es) across {scopeLabel} ({sw.ElapsedMilliseconds} ms).{missingNote}";
        }
        catch (OperationCanceledException) { /* superseded by a fresher search */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Search failed.";
            _logger.Write(Constants.LogCategoryView,
                $"SymbolFinder: search '{pat}' on {scopeLabel} failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearch() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Pattern)
        && (IncludeFields || IncludeMethods)
        && (SearchAllUserAssemblies
                ? Assemblies.Count > 0
                : SelectedAssembly is not null);

    /// <summary>Wired from the view's DataGrid double-click / Enter handlers.</summary>
    public void Activate(SymbolFinderRow row) => NavigateRequested?.Invoke(row);

    // Pull SelectedAssembly to match Browser's selection so the user
    // doesn't have to re-pick when crossing in from another tab.
    public void SyncFromBrowserSelection(AssemblyItem? browserSelectedAssembly)
    {
        if (browserSelectedAssembly is null) return;
        if (SelectedAssembly is not null
            && string.Equals(SelectedAssembly.Name, browserSelectedAssembly.Name,
                             StringComparison.Ordinal))
            return;
        // Only adopt the Browser's pick when our own dropdown is at default.
        if (SelectedAssembly is null) SelectedAssembly = browserSelectedAssembly;
    }
}

/// <summary>
/// One row in the Symbol Finder results grid. Wraps
/// <see cref="SearchSymbolMatch"/> with display-shaped strings.
/// </summary>
public sealed class SymbolFinderRow
{
    public SymbolFinderRow(SearchSymbolMatch m)
    {
        Namespace = m.Namespace ?? string.Empty;
        Class     = m.Class     ?? string.Empty;
        Assembly  = m.Assembly  ?? string.Empty;
        Member    = m.Name      ?? string.Empty;
        Kind      = m.Kind      ?? string.Empty;
        Type      = m.Type      ?? string.Empty;
        Address   = m.Address   ?? string.Empty;
        IsStatic  = m.IsStatic;

        ClassDisplay = string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
        OffsetLabel  = m.Offset is { } off ? $"0x{off:X}" : string.Empty;
        // Kind+static badge: e.g. "field" / "static method".
        KindLabel    = IsStatic ? $"static {Kind}" : Kind;
    }

    public string Namespace    { get; }
    public string Class        { get; }
    /// <summary>Source assembly. In multi-assembly scans this varies row-to-row;
    /// in single-assembly scans every row repeats the same value. Used by
    /// the Navigate handler to flip the Browser to the right assembly tab
    /// before drilling into the class.</summary>
    public string Assembly     { get; }
    public string Member       { get; }
    public string Kind         { get; }
    public string Type         { get; }
    public string Address      { get; }
    public bool   IsStatic     { get; }
    public string ClassDisplay { get; }
    public string OffsetLabel  { get; }
    public string KindLabel    { get; }
}
