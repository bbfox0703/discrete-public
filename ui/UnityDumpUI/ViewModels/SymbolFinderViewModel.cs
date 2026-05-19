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

        var asm = SelectedAssembly!.Name;
        var pat = (Pattern ?? string.Empty).Trim();
        var kinds = new List<string>();
        if (IncludeFields)  kinds.Add("field");
        if (IncludeMethods) kinds.Add("method");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _backend.SearchSymbolsAsync(asm, pat, kinds, Limit, ct).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                StatusMessage = "Search failed — see error above.";
                return;
            }

            foreach (var m in result.Matches)
                Matches.Add(new SymbolFinderRow(m));

            sw.Stop();
            StatusMessage = result.Truncated
                ? $"{Matches.Count} hit(s) (truncated at limit={Limit}); scanned {result.ScannedClasses} class(es) in {sw.ElapsedMilliseconds} ms. Narrow the pattern or raise the limit."
                : $"{Matches.Count} hit(s) in {result.ScannedClasses} class(es) ({sw.ElapsedMilliseconds} ms).";
        }
        catch (OperationCanceledException) { /* superseded by a fresher search */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Search failed.";
            _logger.Write(Constants.LogCategoryView,
                $"SymbolFinder: search '{pat}' on {asm} failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearch() =>
        !IsBusy
        && SelectedAssembly is not null
        && !string.IsNullOrWhiteSpace(Pattern)
        && (IncludeFields || IncludeMethods);

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
    public string Member       { get; }
    public string Kind         { get; }
    public string Type         { get; }
    public string Address      { get; }
    public bool   IsStatic     { get; }
    public string ClassDisplay { get; }
    public string OffsetLabel  { get; }
    public string KindLabel    { get; }
}
