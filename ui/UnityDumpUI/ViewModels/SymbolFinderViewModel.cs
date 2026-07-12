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
    private readonly StatFinderService _statFinder;
    private CancellationTokenSource? _searchCts;

    public SymbolFinderViewModel(BackendAdapter backend, FileLogger logger,
                                 ObservableCollection<AssemblyItem> assemblies)
    {
        _backend    = backend;
        _logger     = logger;
        Assemblies  = assemblies;
        _statFinder = new StatFinderService(backend, logger);
    }

    /// <summary>
    /// Mirrors <see cref="AssemblyBrowserViewModel.Assemblies"/>. Same
    /// collection instance — when the Browser refreshes its assembly list
    /// after handshake, the dropdown picks it up automatically.
    /// </summary>
    public ObservableCollection<AssemblyItem> Assemblies { get; }

    /// <summary>Full result set from the last search (what the RPC returned).</summary>
    public ObservableCollection<SymbolFinderRow> Matches { get; } = new();

    /// <summary>Post-search, client-side view of <see cref="Matches"/> narrowed
    /// by <see cref="ResultFilter"/> + <see cref="NumericTypesOnly"/>. The grid
    /// binds to this, so re-filtering never re-runs the search RPC.</summary>
    public ObservableCollection<SymbolFinderRow> FilteredMatches { get; } = new();

    /// <summary>Substring filter over the loaded results — matches Member /
    /// Class / Type, case-insensitive. Empty = show all.</summary>
    [ObservableProperty] private string _resultFilter = string.Empty;
    partial void OnResultFilterChanged(string value) => RefilterMatches();

    /// <summary>When set, keep only rows whose Type is a numeric / bool
    /// primitive (the directly-editable scalars — Boolean, Byte … Int64,
    /// Single, Double, …). Filters out strings, classes, arrays, enums.</summary>
    [ObservableProperty] private bool _numericTypesOnly;
    partial void OnNumericTypesOnlyChanged(bool value) => RefilterMatches();

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

    /// <summary>
    /// When true, the DLL drops every instance member before applying the
    /// name substring filter. Two-way bonus: pattern becomes optional, so
    /// leaving the textbox blank now yields "every static member in the
    /// chosen assembly scope" — closes the gap users hit when hunting
    /// singletons / constants without knowing a name fragment.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _staticOnly;

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
    [NotifyCanExecuteChangedFor(nameof(FindGameplayFieldsCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindGameplayMethodsCommand))]
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
        FilteredMatches.Clear();

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
                ? await _backend.SearchSymbolsAsync(userAssemblies, pat, kinds, Limit, StaticOnly, ct).ConfigureAwait(true)
                : await _backend.SearchSymbolsAsync(SelectedAssembly!.Name, pat, kinds, Limit, StaticOnly, ct).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                StatusMessage = "Search failed — see error above.";
                return;
            }

            foreach (var m in result.Matches)
                Matches.Add(new SymbolFinderRow(m));
            RefilterMatches();

            sw.Stop();
            string missingNote = result.MissingAssemblies is { Count: > 0 }
                ? $" {result.MissingAssemblies.Count} assembl{(result.MissingAssemblies.Count == 1 ? "y" : "ies")} not found in the backend (probably stripped or renamed)."
                : string.Empty;
            // Build #547 follow-up — per-asm truncation note. When the
            // multi-asm scan hits the per-assembly budget on one or more
            // assemblies (but not the global cap), surface which assemblies
            // got cut short so the user knows where to look if they raise
            // the limit. Distinct from the global Truncated flag which only
            // fires when the overall ceiling (5x*N or 25k) was reached.
            string perAsmNote = result.TruncatedAssemblies is { Count: > 0 }
                ? $" Per-asm cap ({Limit}) hit on: {string.Join(", ", result.TruncatedAssemblies)} — raise the limit to see more from those."
                : string.Empty;
            StatusMessage = result.Truncated
                ? $"{Matches.Count} hit(s) (GLOBAL truncated); scanned {result.ScannedClasses} class(es) across {scopeLabel} in {sw.ElapsedMilliseconds} ms. Narrow the pattern or raise the limit.{missingNote}{perAsmNote}"
                : $"{Matches.Count} hit(s) in {result.ScannedClasses} class(es) across {scopeLabel} ({sw.ElapsedMilliseconds} ms).{missingNote}{perAsmNote}";
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
        // StaticOnly mode relaxes the pattern requirement at the DLL, so
        // the UI follows suit — empty pattern + StaticOnly = "list every
        // static in the chosen scope".
        && (StaticOnly || !string.IsNullOrWhiteSpace(Pattern))
        && (IncludeFields || IncludeMethods)
        && (SearchAllUserAssemblies
                ? Assemblies.Count > 0
                : SelectedAssembly is not null);

    /// <summary>Wired from the view's DataGrid double-click / Enter handlers.</summary>
    public void Activate(SymbolFinderRow row) => NavigateRequested?.Invoke(row);

    /// <summary>
    /// Raised when the user picks "Send class to Browser" on a result row.
    /// Handled by MainWindowViewModel via the SAME handler the Inspector's
    /// "→ Browser" button uses — drops assembly + class into the Browser's
    /// filter inputs and flips to the Fields tab.
    /// </summary>
    public event EventHandler<InspectorBrowseRequest>? SendToBrowserRequested;

    /// <summary>Wired from the view's context-menu Click handler.</summary>
    public void SendToBrowser(SymbolFinderRow row)
        => SendToBrowserRequested?.Invoke(this,
            new InspectorBrowseRequest(row.Assembly, row.Namespace, row.Class));

    // ---------- Interesting gameplay fields (auto keyword scan + live confirm) ----

    /// <summary>Ranked gameplay-field findings from the last auto-find run. The
    /// second results grid binds to this.</summary>
    public ObservableCollection<StatFindingRow> Findings { get; } = new();

    /// <summary>When set, the auto-find also live-confirms the top numeric
    /// candidates (resolve a live instance + read the current value). Off =
    /// keyword score only, no game probe (faster).</summary>
    [ObservableProperty] private bool _confirmLive = true;

    [ObservableProperty] private string _findingsStatus =
        "Auto-find keyword-scores every field name in the user assemblies for gameplay signals (HP / Gold / Damage / Speed …).";

    /// <summary>
    /// #5/#6 — score every field in the user assemblies for gameplay keywords,
    /// then (when <see cref="ConfirmLive"/>) confirm the top numeric candidates
    /// against a live instance. One bulk <c>dumpClassInfo</c> + a per-class
    /// findInstances/walk probe. No pattern needed — the keyword tables drive it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindGameplay))]
    public async Task FindGameplayFieldsAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy = true;
        ErrorMessage = null;
        Findings.Clear();

        List<string> userAssemblies = SystemAssemblyFilter.SelectUserAssemblies(
            Assemblies.Select(a => a.Name));
        if (userAssemblies.Count == 0)
        {
            IsBusy = false;
            FindingsStatus = "No user assemblies found — connect to a running game first.";
            return;
        }

        string asmLabel = $"{userAssemblies.Count} user assembl{(userAssemblies.Count == 1 ? "y" : "ies")}";
        var sw = Stopwatch.StartNew();
        try
        {
            FindingsStatus = ConfirmLive
                ? $"Scanning {asmLabel} + confirming live values…"
                : $"Scoring {asmLabel}…";

            int shown;
            if (ConfirmLive)
            {
                var findings = await _statFinder.DetectAsync(userAssemblies, ct: ct).ConfigureAwait(true);
                foreach (var f in findings) Findings.Add(new StatFindingRow(f));
                shown = findings.Count;
            }
            else
            {
                var scored = await _statFinder.ScoreAsync(userAssemblies, ct: ct).ConfigureAwait(true);
                foreach (var f in scored)
                    Findings.Add(new StatFindingRow(new StatFinding { Field = f, Confidence = f.Score }));
                shown = scored.Count;
            }

            sw.Stop();
            int confirmed = Findings.Count(r => r.IsConfirmed);
            FindingsStatus = ConfirmLive
                ? $"{shown} gameplay field(s) — {confirmed} confirmed live — across {asmLabel} ({sw.ElapsedMilliseconds} ms). ✓ = a live instance holds a plausible value now; try those in CE first."
                : $"{shown} gameplay field(s) scored across {asmLabel} ({sw.ElapsedMilliseconds} ms). Tick 'Confirm live values' to verify each against a running instance.";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            FindingsStatus = "Auto-find failed.";
            _logger.Write(Constants.LogCategoryView, $"SymbolFinder: auto-find failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Enabled whenever not busy; the empty-assemblies case is handled inside with
    // a friendly status (gating on Assemblies.Count would go stale on connect).
    private bool CanFindGameplay() => !IsBusy;

    /// <summary>Reuse the Send-to-Browser handoff for a findings row.</summary>
    public void SendFindingToBrowser(StatFindingRow row)
        => SendToBrowserRequested?.Invoke(this,
            new InspectorBrowseRequest(row.Assembly, row.Namespace, row.Class));

    // ---------- Interesting gameplay methods (auto keyword scan) ------------------

    /// <summary>Ranked gameplay-method findings (invoke targets) from the last scan.</summary>
    public ObservableCollection<MethodFindingRow> MethodFindings { get; } = new();

    /// <summary>Opt into the extra GameplayAction keyword pack (Dash / Interact /
    /// Buy / Sell). Off by default — those verbs are noisier.</summary>
    [ObservableProperty] private bool _includeGameplayActions;

    [ObservableProperty] private string _methodFindingsStatus =
        "Auto-find keyword-scores every method name for gameplay verbs (SetHealth / AddGold / Teleport / Kill / God …) — invoke targets.";

    /// <summary>#7 — keyword-score every method in the user assemblies, surfacing
    /// gameplay invoke targets. One bulk dumpClassInfo; no pattern needed.</summary>
    [RelayCommand(CanExecute = nameof(CanFindGameplay))]
    public async Task FindGameplayMethodsAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy = true;
        ErrorMessage = null;
        MethodFindings.Clear();

        List<string> userAssemblies = SystemAssemblyFilter.SelectUserAssemblies(
            Assemblies.Select(a => a.Name));
        if (userAssemblies.Count == 0)
        {
            IsBusy = false;
            MethodFindingsStatus = "No user assemblies found — connect to a running game first.";
            return;
        }

        string asmLabel = $"{userAssemblies.Count} user assembl{(userAssemblies.Count == 1 ? "y" : "ies")}";
        var sw = Stopwatch.StartNew();
        try
        {
            MethodFindingsStatus = $"Scoring methods across {asmLabel}…";
            var scored = await _statFinder
                .ScoreMethodsAsync(userAssemblies, IncludeGameplayActions, ct: ct).ConfigureAwait(true);
            foreach (var m in scored) MethodFindings.Add(new MethodFindingRow(m));
            sw.Stop();
            MethodFindingsStatus =
                $"{scored.Count} gameplay method(s) across {asmLabel} ({sw.ElapsedMilliseconds} ms). "
                + "Right-click → Send class to Browser, then invoke from the Methods tab.";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            MethodFindingsStatus = "Auto-find failed.";
            _logger.Write(Constants.LogCategoryView, $"SymbolFinder: method auto-find failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reuse the Send-to-Browser handoff for a method-findings row.</summary>
    public void SendMethodFindingToBrowser(MethodFindingRow row)
        => SendToBrowserRequested?.Invoke(this,
            new InspectorBrowseRequest(row.Assembly, row.Namespace, row.Class));

    // ---------- Client-side result filtering -----------------------------

    /// <summary>System.* primitive type names treated as "numeric" by the
    /// numeric-only toggle. Boolean is included per the user-facing label
    /// ("bool, byte, int32 … single, double"); strings / enums / classes are
    /// intentionally excluded.</summary>
    private static readonly HashSet<string> s_numericTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte",   "System.SByte",
        "System.Int16",   "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64",   "System.UInt64", "System.IntPtr", "System.UIntPtr",
        "System.Single",  "System.Double", "System.Decimal", "System.Char",
    };

    private static bool IsNumericType(string? type)
        => !string.IsNullOrEmpty(type) && s_numericTypes.Contains(type);

    private static bool RowMatchesFilter(SymbolFinderRow r, string filter)
        => r.Member.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.ClassDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.Type.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuild <see cref="FilteredMatches"/> from <see cref="Matches"/>
    /// applying the result-filter substring + numeric-only toggle.</summary>
    private void RefilterMatches()
    {
        FilteredMatches.Clear();
        string filter = ResultFilter ?? string.Empty;
        bool hasFilter   = filter.Length > 0;
        bool numericOnly = NumericTypesOnly;
        foreach (SymbolFinderRow r in Matches)
        {
            if (numericOnly && !IsNumericType(r.Type)) continue;
            if (hasFilter && !RowMatchesFilter(r, filter)) continue;
            FilteredMatches.Add(r);
        }
    }

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

/// <summary>
/// One row in the auto-find gameplay-fields grid. Wraps a
/// <see cref="StatFinding"/> (scored field + live-confirm verdict) with
/// display-shaped strings.
/// </summary>
public sealed class StatFindingRow
{
    public StatFindingRow(StatFinding f)
    {
        InterestingField fld = f.Field;
        Assembly     = fld.Assembly;
        Namespace    = fld.Namespace;
        Class        = fld.Class;
        ClassDisplay = fld.ClassDisplay;
        Member       = fld.Name;
        Type         = fld.Type;
        OffsetLabel  = fld.OffsetLabel;
        Category     = fld.Category.ToString();
        Score        = fld.Score;
        Confidence   = f.Confidence;
        IsConfirmed  = f.Confirmed;
        // ✓ confirmed live, · live instance seen but value not plausible, blank otherwise.
        ConfirmedMark = f.Confirmed ? "✓" : f.LiveInstanceExists ? "·" : string.Empty;
        LiveValue    = f.LiveValue ?? string.Empty;
        Keywords     = fld.KeywordsDisplay;
        StaticLabel  = fld.IsStatic ? "static" : string.Empty;
    }

    public string Assembly     { get; }
    public string Namespace    { get; }
    public string Class        { get; }
    public string ClassDisplay { get; }
    public string Member       { get; }
    public string Type         { get; }
    public string OffsetLabel  { get; }
    public string Category     { get; }
    public int    Score        { get; }
    public int    Confidence   { get; }
    public bool   IsConfirmed  { get; }
    public string ConfirmedMark{ get; }
    public string LiveValue    { get; }
    public string Keywords     { get; }
    public string StaticLabel  { get; }
}

/// <summary>One row in the auto-find gameplay-methods grid (invoke targets).</summary>
public sealed class MethodFindingRow
{
    public MethodFindingRow(InterestingMethod m)
    {
        Assembly     = m.Assembly;
        Namespace    = m.Namespace;
        Class        = m.Class;
        ClassDisplay = m.ClassDisplay;
        Method       = m.Name;
        Category     = m.Category.ToString();
        Score        = m.Score;
        Keywords     = m.KeywordsDisplay;
        StaticLabel  = m.IsStatic ? "static" : string.Empty;
    }

    public string Assembly     { get; }
    public string Namespace    { get; }
    public string Class        { get; }
    public string ClassDisplay { get; }
    public string Method       { get; }
    public string Category     { get; }
    public int    Score        { get; }
    public string Keywords     { get; }
    public string StaticLabel  { get; }
}
