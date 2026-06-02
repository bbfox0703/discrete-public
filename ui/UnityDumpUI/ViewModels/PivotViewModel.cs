// PivotViewModel — Phase 29e-2.
//
// Drives the "Class Pivot" tab. Shares the Snapshot tab's envelope
// pool — exactly like SpcQueryViewModel — so the user takes / saves /
// loads snapshots once and three tabs (Snapshot diff / SPC / Pivot)
// all see the same set.
//
// What this tab answers that SPC can't: "for class C, group instances
// across N snapshots by a stable business key (e.g. _propertyID), and
// show how a value field (e.g. _value) trajectory looks for each key
// tuple". Sidesteps both cross-session and intra-session address
// drift — see docs/class-pivot.md for the full design.
//
// Stage 0 (class shortlist with stability metrics) + Stage 1 (auto-
// key discovery) are NOT in this VM yet — 29e-3 and 29e-4 respectively.
// v1 is "manual class + manual K + manual V": the user types the FQN
// + comma-separated field names; AutoCompleteBox suggestions come
// from KnownClasses / KnownFieldNames sourced from currently-hydrated
// snapshots.
//
// Cross-session / cross-build banner is repurposed from SPC's
// "instance fields suppressed" wording — pivot is *built for* cross-
// session, so the same situation reads as "Cross-session pivot —
// keying by (k1, k2) sidesteps address drift" instead of a warning.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class PivotViewModel : ObservableObject
{
    private readonly SnapshotViewModel _snapshot;
    private readonly FileLogger        _logger;

    public PivotViewModel(SnapshotViewModel snapshot, FileLogger logger)
    {
        _snapshot = snapshot;
        _logger   = logger;

        _snapshot.Snapshots.CollectionChanged += (_, _) =>
        {
            SyncSelectionRows();
            RunCommand.NotifyCanExecuteChanged();
            AnalyzeClassesCommand.NotifyCanExecuteChanged();
            ScoreFieldsCommand.NotifyCanExecuteChanged();
            ScoreElementFieldsCommand.NotifyCanExecuteChanged();
            FindByValueCommand.NotifyCanExecuteChanged();
            SuggestKeyAndRunCommand.NotifyCanExecuteChanged();
            RankLikelyGameStateCommand.NotifyCanExecuteChanged();
        };
        SyncSelectionRows();
        RunCommand.NotifyCanExecuteChanged();
        AnalyzeClassesCommand.NotifyCanExecuteChanged();
        ScoreFieldsCommand.NotifyCanExecuteChanged();
        ScoreElementFieldsCommand.NotifyCanExecuteChanged();
        FindByValueCommand.NotifyCanExecuteChanged();
        SuggestKeyAndRunCommand.NotifyCanExecuteChanged();
        RankLikelyGameStateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Class FQNs seen across currently-hydrated snapshots.
    /// Populated by <see cref="RefreshKnownClassesAndFields"/> after
    /// every successful Run (so the autocomplete grows with each
    /// hydration). Empty until at least one snapshot is hydrated.</summary>
    public ObservableCollection<string> KnownClasses { get; } = new();

    /// <summary>Field names observed on instances of the currently-typed
    /// <see cref="ClassFqn"/>. Refreshed in two places — after Run (full
    /// re-scan) and on every ClassFqn / Snapshots change (best-effort
    /// scan of already-hydrated snaps). Drives the Key + Value field
    /// AutoCompleteBoxes.</summary>
    public ObservableCollection<string> KnownFieldNames { get; } = new();

    /// <summary>Snapshot-picker rows. Mirrors <see cref="SpcQueryViewModel.SelectionRows"/>
    /// shape but without per-snap predicates — pivot doesn't have them.</summary>
    public ObservableCollection<PivotSnapshotRow> SelectionRows { get; } = new();

    private List<PivotSnapshotRow> SelectedRows()
        => SelectionRows.Where(r => r.Include).ToList();

    // ── Stage 0: class shortlist (29e-3) ───────────────────────────────

    /// <summary>Assemblies seen across hydrated snapshots — drives the
    /// shortlist's assembly AutoCompleteBox so the user can narrow to
    /// e.g. "Assembly-CSharp" before analysing.</summary>
    public ObservableCollection<string> KnownAssemblies { get; } = new();

    /// <summary>Filtered shortlist shown in the Stage 0 DataGrid. The
    /// unfiltered set lives in <see cref="_allShortlistRows"/>; toggles
    /// rebuild this view without re-analysing.</summary>
    public ObservableCollection<ClassShortlistRow> ClassShortlist { get; } = new();

    private List<ClassShortlistRow> _allShortlistRows = new();

    /// <summary>Currently-selected shortlist row — drives the 29i-1
    /// class-structure detail panel. Selecting a row repopulates
    /// <see cref="ShortlistFields"/> so the user reads the class's full
    /// field shape without switching <see cref="ClassFqn"/> + re-hydrating.</summary>
    [ObservableProperty] private ClassShortlistRow? _selectedShortlistRow;

    /// <summary>Full field list (name/type/kind) of
    /// <see cref="SelectedShortlistRow"/> — bound by the structure-browser
    /// detail grid. Empty when no row is selected.</summary>
    public ObservableCollection<ClassFieldInfo> ShortlistFields { get; } = new();

    /// <summary>Unfiltered field list of the selected shortlist row; the
    /// filtered view (<see cref="ShortlistFields"/>) is rebuilt from this.</summary>
    private List<ClassFieldInfo> _allShortlistFields = new();

    /// <summary>Case-insensitive substring filter on the field-structure
    /// detail grid (field name). Lets a wide class be narrowed quickly.</summary>
    [ObservableProperty] private string _shortlistFieldFilter = string.Empty;

    partial void OnSelectedShortlistRowChanged(ClassShortlistRow? value)
    {
        _allShortlistFields = value?.Fields?.ToList() ?? new List<ClassFieldInfo>();
        RebuildShortlistFieldsView();
    }

    partial void OnShortlistFieldFilterChanged(string value) => RebuildShortlistFieldsView();

    private void RebuildShortlistFieldsView()
    {
        string? filter = string.IsNullOrWhiteSpace(ShortlistFieldFilter) ? null : ShortlistFieldFilter.Trim();
        ShortlistFields.Clear();
        foreach (var f in _allShortlistFields)
        {
            if (filter is not null &&
                f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            ShortlistFields.Add(f);
        }
    }

    /// <summary>Distinct classes pulled from the per-game DB metadata
    /// (no blob hydrate) — seeds the class / assembly autocomplete
    /// before any Analyze/Score/Run. Unioned with hydrated-snap classes
    /// in <see cref="RefreshKnownClassesAndFields"/>.</summary>
    private IReadOnlyList<PersistedClassRef> _persistedClasses = System.Array.Empty<PersistedClassRef>();

    /// <summary>Exact assembly to scope Analyze to. Empty = all.</summary>
    [ObservableProperty] private string _assemblyFilter = string.Empty;

    /// <summary>Show compiler-generated (<c>&lt;…&gt;</c>) classes in the
    /// shortlist. Off by default — they're rarely pivot targets.</summary>
    [ObservableProperty] private bool _shortlistShowSynthetic;

    /// <summary>Show classes that aren't present in every snapshot.
    /// Off by default — partial presence means instances appear/vanish
    /// between snaps, so the pivot can't line them all up.</summary>
    [ObservableProperty] private bool _shortlistShowPartial;

    [ObservableProperty] private string _shortlistStatus =
        "Pick snapshots, optionally set an assembly, press Analyze to score classes by stability.";

    /// <summary>Case-insensitive substring filter on the shortlist's
    /// class FQN. Lets the user narrow 500+ classes to the few they
    /// half-remember the name of.</summary>
    [ObservableProperty] private string _classNameFilter = string.Empty;

    partial void OnShortlistShowSyntheticChanged(bool value) => RebuildShortlistView();
    partial void OnShortlistShowPartialChanged(bool value)   => RebuildShortlistView();
    partial void OnClassNameFilterChanged(string value)      => RebuildShortlistView();

    // ── Stage 1: field key auto-discovery (29e-4) ──────────────────────

    /// <summary>Filtered scored-field list shown in the Stage 1 DataGrid.
    /// Unfiltered set in <see cref="_allFieldScores"/>.</summary>
    public ObservableCollection<FieldScore> FieldScores { get; } = new();

    private List<FieldScore> _allFieldScores = new();

    /// <summary>Substring filter on the scored-field list (field name).</summary>
    [ObservableProperty] private string _fieldScoreFilter = string.Empty;

    /// <summary>The greedy compound-key proposal rendered for the UI,
    /// e.g. "(_itemGroup, _property)". Empty until Score fields runs.</summary>
    [ObservableProperty] private string _suggestedKeyDisplay = string.Empty;

    /// <summary>Why the suggestion looks the way it does (unique vs
    /// lowest-collision). Shown next to the Apply button.</summary>
    [ObservableProperty] private string _suggestedKeyReason = string.Empty;

    [ObservableProperty] private string _fieldScoreStatus =
        "Set a Class FQN (or pick one from the shortlist), then Score fields to rank key candidates.";

    private IReadOnlyList<string> _suggestedKey = System.Array.Empty<string>();

    partial void OnFieldScoreFilterChanged(string value) => RebuildFieldScoresView();

    /// <summary>Result rows from the last successful pivot. Each row
    /// carries a pre-rendered <see cref="PivotResultRow.TrajectoryDisplay"/>
    /// string the DataGrid shows directly — Avalonia 12 AOT bans
    /// reflection-based Binding paths, so we avoid building dynamic
    /// per-snap columns at runtime. The trajectory string keeps the
    /// per-snap "v0 → v1 → …" feel SPC users already know, and works
    /// for any (snap count, value field count) pair.</summary>
    public ObservableCollection<PivotResultRow> Results { get; } = new();

    [ObservableProperty] private string _statusMessage =
        "Type a class FQN, list key + value field name(s), pick 1+ snapshots, press Run.";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _modeBanner   = string.Empty;
    // Wave A — now that the engine compute runs off the UI thread (Task.Run),
    // the UI stays responsive DURING a run, so a second click could start a
    // concurrent run that races on Results. Notify every gated command's
    // CanExecute when IsRunning flips so the buttons grey out while busy.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScoreFieldsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScoreElementFieldsCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindByValueCommand))]
    [NotifyCanExecuteChangedFor(nameof(RankLikelyGameStateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuggestKeyAndRunCommand))]
    private bool   _isRunning;

    /// <summary>FQN of the class whose instances to pivot — typed by
    /// the user, suggestions from <see cref="KnownClasses"/>.</summary>
    [ObservableProperty] private string _classFqn = string.Empty;

    /// <summary>Comma-separated key field names. Empty → preflight
    /// error. Compound key = multiple names.</summary>
    [ObservableProperty] private string _keyFieldsText = string.Empty;

    /// <summary>Comma-separated value field names. Empty → preflight
    /// error.</summary>
    [ObservableProperty] private string _valueFieldsText = string.Empty;

    /// <summary>Optional substring filter on the rendered key tuple
    /// (parts joined with ", "). Case-insensitive.</summary>
    [ObservableProperty] private string _keyFilter = string.Empty;

    partial void OnClassFqnChanged(string value) => RefreshKnownFieldNamesForClass();

    // ── Layer B: array inner-pivot (29f-4) ─────────────────────────────

    /// <summary>When on, Run pivots the ELEMENTS of <see cref="ArrayFieldName"/>
    /// (a reference-type array on the class) by an inner element field via
    /// <see cref="ArrayPivotEngine"/> instead of pivoting whole instances.
    /// Requires snapshots captured with Layer B (Snapshot tab → "Capture
    /// array element fields"); old snapshots produce 0 rows.</summary>
    [ObservableProperty] private bool _arrayPivotMode;

    /// <summary>Reference-array field on the class whose elements are
    /// pivoted (e.g. <c>_statAdditionData</c>). Suggestions from
    /// <see cref="KnownArrayFields"/>.</summary>
    [ObservableProperty] private string _arrayFieldName = string.Empty;

    /// <summary>Comma-separated element-key field names (fields INSIDE each
    /// array element, e.g. <c>_propertyID</c>). The owning-instance key
    /// reuses <see cref="KeyFieldsText"/> as the outer key (may be empty).</summary>
    [ObservableProperty] private string _innerKeyFieldsText = string.Empty;

    /// <summary>Comma-separated element-value field names (e.g.
    /// <c>_amount</c>).</summary>
    [ObservableProperty] private string _innerValueFieldsText = string.Empty;

    /// <summary>Reference-array field names observed on the typed class —
    /// drives the Array field AutoCompleteBox.</summary>
    public ObservableCollection<string> KnownArrayFields { get; } = new();

    /// <summary>Inner field names observed on captured elements of the
    /// typed <see cref="ArrayFieldName"/> — drives the inner key / value
    /// AutoCompleteBoxes. Empty until a Layer-B snapshot is hydrated.</summary>
    public ObservableCollection<string> KnownElementFieldNames { get; } = new();

    partial void OnArrayFieldNameChanged(string value) => RefreshKnownElementFieldNames();

    // ── 29i-2: array element-field scored picker ───────────────────────

    /// <summary>Filtered scored-field list for the array element fields —
    /// Stage 1 ranking applied to the inner fields of <see cref="ArrayFieldName"/>'s
    /// captured elements (Layer B). Drives the element-field picker grid.</summary>
    public ObservableCollection<FieldScore> ElementFieldScores { get; } = new();

    private List<FieldScore> _allElementFieldScores = new();

    [ObservableProperty] private string _elementFieldScoreFilter = string.Empty;

    /// <summary>Greedy compound-key proposal for the INNER element key,
    /// e.g. "(_propertyID)". Empty until Score element fields runs.</summary>
    [ObservableProperty] private string _suggestedInnerKeyDisplay = string.Empty;
    [ObservableProperty] private string _suggestedInnerKeyReason  = string.Empty;

    [ObservableProperty] private string _elementFieldScoreStatus =
        "Set a Class FQN + Array field, then Score element fields to rank inner key candidates.";

    private IReadOnlyList<string> _suggestedInnerKey = System.Array.Empty<string>();

    partial void OnElementFieldScoreFilterChanged(string value) => RebuildElementFieldScoresView();

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task ScoreElementFieldsAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassFqn))
        {
            ErrorMessage = "Set a Class FQN first (type one or pick it from the Stage 0 shortlist).";
            return;
        }
        if (string.IsNullOrWhiteSpace(ArrayFieldName))
        {
            ErrorMessage = "Set an Array field first (the reference array whose elements to score, e.g. '_statAdditionData').";
            return;
        }

        try
        {
            IsRunning = true;
            ElementFieldScoreStatus = $"Hydrating {selected.Count:N0} snapshot(s)…";

            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);

            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            string? assembly = string.IsNullOrWhiteSpace(AssemblyFilter)
                ? selected.Select(r => ResolveAssemblyForClass(r.Snapshot, ClassFqn.Trim()))
                          .FirstOrDefault(a => !string.IsNullOrEmpty(a))
                : AssemblyFilter.Trim();
            if (string.IsNullOrEmpty(assembly))
            {
                ErrorMessage = $"Class '{ClassFqn}' not found in any selected snapshot. Check the FQN (case-sensitive, must include the namespace).";
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Compute off the UI thread (the engine iterates every instance —
            // synchronous on the UI thread froze the app on large snapshots).
            var elemQuery = new KeyDiscoveryQuery
            {
                Snapshots  = selected.Select(r => r.Snapshot).ToList(),
                Assembly   = assembly!,
                ClassFqn   = ClassFqn.Trim(),
                ArrayField = ArrayFieldName.Trim(),
            };
            var result = await Task.Run(() => KeyDiscoveryEngine.Run(elemQuery)).ConfigureAwait(true);
            sw.Stop();

            _allElementFieldScores = result.Fields.ToList();
            RebuildElementFieldScoresView();

            _suggestedInnerKey       = result.SuggestedKey;
            SuggestedInnerKeyDisplay = result.SuggestedKey.Count > 0
                ? "(" + string.Join(", ", result.SuggestedKey) + ")"
                : "(none)";
            SuggestedInnerKeyReason  = result.SuggestedKeyReason;

            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }

            ElementFieldScoreStatus = _allElementFieldScores.Count == 0
                ? "No element fields scored — the array has no captured inner fields. Re-take the snapshot with \"Capture array element fields\" (Layer B) enabled on the Snapshot tab."
                : $"Scored {_allElementFieldScores.Count:N0} element field(s) in {sw.ElapsedMilliseconds} ms. " +
                  $"Double-click → inner key; right-click → inner value. Suggested inner key: {SuggestedInnerKeyDisplay}.";
            _logger.Write(SnapshotStore.LogCategory,
                $"PivotElementKeyDiscovery: class={ClassFqn} array={ArrayFieldName} fields={_allElementFieldScores.Count} suggested=[{string.Join(',', result.SuggestedKey)}] in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"PivotElementKeyDiscovery FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void RebuildElementFieldScoresView()
    {
        string? filter = string.IsNullOrWhiteSpace(ElementFieldScoreFilter) ? null : ElementFieldScoreFilter.Trim();
        ElementFieldScores.Clear();
        foreach (var f in _allElementFieldScores)
        {
            if (filter is not null &&
                f.FieldName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            ElementFieldScores.Add(f);
        }
    }

    [RelayCommand]
    public void ApplySuggestedInnerKey()
    {
        if (_suggestedInnerKey.Count == 0) return;
        InnerKeyFieldsText = string.Join(", ", _suggestedInnerKey);
        StatusMessage = $"Inner key fields set to {string.Join(", ", _suggestedInnerKey)}.";
    }

    public void AddElementFieldToInnerKeys(FieldScore field)
    {
        if (field is null) return;
        InnerKeyFieldsText = AppendField(InnerKeyFieldsText, field.FieldName);
        StatusMessage = $"Added '{field.FieldName}' to inner key fields.";
    }

    public void AddElementFieldToInnerValues(FieldScore field)
    {
        if (field is null) return;
        InnerValueFieldsText = AppendField(InnerValueFieldsText, field.FieldName);
        StatusMessage = $"Added '{field.FieldName}' to inner value fields.";
    }

    // ── 29i-4: value-driven class location (Find by value) ─────────────

    /// <summary>Delta mode (find a field whose value universe MOVED by a
    /// cross-snap delta) vs exact mode (find a field carrying a value).</summary>
    [ObservableProperty] private bool _locatorDeltaMode;

    /// <summary>The value to find in exact mode (as typed; matched against
    /// rendered + raw forms).</summary>
    [ObservableProperty] private string _locatorValue = string.Empty;

    /// <summary>Optional ± numeric tolerance (handles float jitter).</summary>
    [ObservableProperty] private string _locatorTolerance = string.Empty;

    /// <summary>Cross-snap change to find in delta mode, e.g. -750.</summary>
    [ObservableProperty] private string _locatorDelta = string.Empty;

    [ObservableProperty] private int _locatorSnapAIndex = -1;
    [ObservableProperty] private int _locatorSnapBIndex = -1;

    [ObservableProperty] private bool _locatorIncludeArrays;
    [ObservableProperty] private bool _locatorNumericOnly;
    [ObservableProperty] private bool _locatorIncludeSynthetic;

    [ObservableProperty] private string _locatorStatus =
        "Type a value you can see in-game (exact), or switch to Delta to find a field that moved by Δ across two snaps. Then Find.";

    /// <summary>Ranked (class, field) candidates from the last Find.</summary>
    public ObservableCollection<ValueLocatorHit> LocatorHits { get; } = new();

    /// <summary>Snapshot labels (capture order) for the delta-mode A/B
    /// ComboBoxes. Rebuilt by <see cref="SyncSelectionRows"/>.</summary>
    public ObservableCollection<string> LocatorSnapChoices { get; } = new();

    [ObservableProperty] private ValueLocatorHit? _selectedLocatorHit;

    private void RebuildLocatorSnapChoices()
    {
        LocatorSnapChoices.Clear();
        foreach (var r in SelectionRows) LocatorSnapChoices.Add(r.DisplayLabel);
        if (LocatorSnapAIndex >= LocatorSnapChoices.Count) LocatorSnapAIndex = -1;
        if (LocatorSnapBIndex >= LocatorSnapChoices.Count) LocatorSnapBIndex = -1;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task FindByValueAsync()
    {
        ErrorMessage = string.Empty;
        LocatorHits.Clear();

        bool delta = LocatorDeltaMode;
        List<PivotSnapshotRow> rowsForHydrate;
        List<SnapshotEnvelope>  snaps;
        int snapA = -1, snapB = -1;
        double deltaVal = 0;

        if (delta)
        {
            var all = SelectionRows.ToList();   // capture order; delta picks 2 explicitly
            if (all.Count < 2) { ErrorMessage = "Delta mode needs at least 2 snapshots loaded."; return; }
            snapA = LocatorSnapAIndex; snapB = LocatorSnapBIndex;
            if (snapA < 0 || snapB < 0 || snapA >= all.Count || snapB >= all.Count)
            { ErrorMessage = "Pick snapshot A and snapshot B for delta mode."; return; }
            if (snapA == snapB) { ErrorMessage = "Snapshot A and B must differ."; return; }
            if (!double.TryParse(LocatorDelta, NumberStyles.Any, CultureInfo.InvariantCulture, out deltaVal))
            { ErrorMessage = "Enter a numeric Delta (e.g. -750)."; return; }
            rowsForHydrate = new List<PivotSnapshotRow> { all[snapA], all[snapB] };
            snaps = all.Select(r => r.Snapshot).ToList();
        }
        else
        {
            var selected = SelectedRows();
            if (selected.Count == 0) { ErrorMessage = "Pick at least 1 snapshot."; return; }
            if (string.IsNullOrWhiteSpace(LocatorValue)) { ErrorMessage = "Enter a value to find."; return; }
            rowsForHydrate = selected;
            snaps = selected.Select(r => r.Snapshot).ToList();
        }

        try
        {
            IsRunning = true;
            LocatorStatus = "Hydrating…";

            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in rowsForHydrate)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);
            foreach (var r in rowsForHydrate)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            double? tol = double.TryParse(LocatorTolerance, NumberStyles.Any, CultureInfo.InvariantCulture, out var tv)
                ? tv : (double?)null;

            var sw = Stopwatch.StartNew();
            var locQuery = new ValueLocatorQuery
            {
                Snapshots            = snaps,
                AssemblyFilter       = string.IsNullOrWhiteSpace(AssemblyFilter) ? null : AssemblyFilter.Trim(),
                ExactValue           = delta ? null : LocatorValue.Trim(),
                NumericTolerance     = tol,
                SnapA                = delta ? snapA : (int?)null,
                SnapB                = delta ? snapB : (int?)null,
                Delta                = delta ? deltaVal : (double?)null,
                IncludeArrayElements = LocatorIncludeArrays,
                NumericOnly          = LocatorNumericOnly,
                IncludeSynthetic     = LocatorIncludeSynthetic,
            };
            // Off-thread compute (scans every field of every instance).
            var result = await Task.Run(() => SnapshotValueLocatorEngine.Run(locQuery)).ConfigureAwait(true);
            sw.Stop();

            foreach (var h in result.Hits) LocatorHits.Add(h);

            int evicted = 0;
            foreach (var r in rowsForHydrate)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }
            string evictionNote = evicted > 0 ? $" — evicted {evicted:N0} hydrated-for-query envelope(s)" : string.Empty;

            LocatorStatus = $"{result.Summary} {sw.ElapsedMilliseconds} ms{evictionNote}. Double-tap a hit to load it into the pivot query.";
            _logger.Write(SnapshotStore.LogCategory,
                $"PivotLocator: mode={(delta ? "delta" : "exact")} asm='{AssemblyFilter}' hits={result.Hits.Count} in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"PivotLocator FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Handoff: load a locator hit into the pivot query. Instance
    /// hits fill ClassFqn + the value field; Layer B array-element hits
    /// (<c>arr[].inner</c>) switch to array mode + fill the inner value.</summary>
    public void UseLocatorHit(ValueLocatorHit hit)
    {
        if (hit is null) return;
        ClassFqn       = hit.ClassFqn;
        AssemblyFilter = hit.Assembly;
        RefreshKnownFieldNamesForClass();

        string field = hit.FieldName;
        if (hit.InArrayElement)
        {
            int sep = field.IndexOf("[].", StringComparison.Ordinal);
            if (sep >= 0)
            {
                ArrayPivotMode       = true;
                ArrayFieldName       = field.Substring(0, sep);
                InnerValueFieldsText = AppendField(InnerValueFieldsText, field.Substring(sep + 3));
                StatusMessage = $"Loaded array hit {hit.ClassFqn}.{field}. Score element fields for an inner key, then Run.";
                return;
            }
            // Layer A primitive element (arr[]) — no inner-field pivot path.
            StatusMessage = $"Loaded {hit.ClassFqn}.{field} (primitive array element). Class set; pick key/value manually.";
            return;
        }
        ValueFieldsText = AppendField(ValueFieldsText, field);
        StatusMessage = $"Loaded {hit.ClassFqn}.{field} as a value field. Use 'Suggest key & run' or pick a key.";
    }

    /// <summary>One-click close-the-loop for an instance hit: score fields,
    /// apply the suggested key, run the pivot.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task SuggestKeyAndRunAsync()
    {
        ErrorMessage = string.Empty;
        if (ArrayPivotMode)
        {
            ErrorMessage = "Suggest-key-and-run is for instance pivots; for array hits use 'Score element fields' + Run.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassFqn))
        {
            ErrorMessage = "Set a Class FQN first (load a locator hit, or pick one from the shortlist).";
            return;
        }
        if (string.IsNullOrWhiteSpace(ValueFieldsText))
        {
            ErrorMessage = "Set at least one value field first (load a locator hit).";
            return;
        }

        await ScoreFieldsAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(ErrorMessage)) return;
        ApplySuggestedKey();
        if (string.IsNullOrWhiteSpace(KeyFieldsText))
        {
            ErrorMessage = "No key could be suggested — pick a key field manually, then Run.";
            return;
        }
        await RunAsync().ConfigureAwait(true);
    }

    // ── 29i-3: unsupervised "likely game-state" ranking + preview ──────

    /// <summary>Ranked candidate classes (stable key + drifting value) —
    /// the filtered view. Unfiltered set in <see cref="_allInterestRows"/>.</summary>
    public ObservableCollection<ClassInterestRow> InterestRows { get; } = new();

    private List<ClassInterestRow> _allInterestRows = new();

    /// <summary>Case-insensitive substring filter on the ranked list
    /// (class FQN). Empty = show all.</summary>
    [ObservableProperty] private string _interestFilter = string.Empty;

    partial void OnInterestFilterChanged(string value) => RebuildInterestView();

    private void RebuildInterestView()
    {
        string? filter = string.IsNullOrWhiteSpace(InterestFilter) ? null : InterestFilter.Trim();
        InterestRows.Clear();
        foreach (var r in _allInterestRows)
        {
            if (filter is not null &&
                r.ClassFqn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            InterestRows.Add(r);
        }
    }

    /// <summary>How many Stage-0 candidates Pass B scores. Cost gate.</summary>
    [ObservableProperty] private int _interestTopN = 40;

    [ObservableProperty] private string _interestStatus =
        "Rank the most likely game-state classes (stable identity + drifting value) and preview them. Most heuristic — when you know the value, use Find by value instead.";

    [ObservableProperty] private ClassInterestRow? _selectedInterestRow;

    /// <summary>Sample trajectory lines of <see cref="SelectedInterestRow"/>
    /// — pre-built during ranking, shown in the preview panel.</summary>
    public ObservableCollection<string> InterestPreview { get; } = new();

    partial void OnSelectedInterestRowChanged(ClassInterestRow? value)
    {
        InterestPreview.Clear();
        if (value is null) return;
        foreach (var line in value.Preview) InterestPreview.Add(line);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RankLikelyGameStateAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot.";
            return;
        }

        try
        {
            IsRunning = true;
            InterestStatus = $"Hydrating {selected.Count:N0} snapshot(s)…";

            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);
            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            var snaps = selected.Select(r => r.Snapshot).ToList();
            var sw = Stopwatch.StartNew();

            // Pass A + B + preview all iterate every instance — run the whole
            // compute off the UI thread so the busy spinner can animate and the
            // app stays responsive on large snapshots.
            string? interestAsmFilter = string.IsNullOrWhiteSpace(AssemblyFilter) ? null : AssemblyFilter.Trim();
            int interestTopN = Math.Max(1, InterestTopN);
            int candidateCount = 0;   // captured out of the lambda for the status line
            var rows = await Task.Run(() =>
            {
                // Pass A — shortlist → top-N clean candidates.
                var shortlist = ClassShortlistEngine.Run(new ClassShortlistQuery
                {
                    Snapshots      = snaps,
                    AssemblyFilter = interestAsmFilter,
                });
                var candidates = shortlist
                    .Where(c => !c.IsSynthetic && c.Presence == c.SnapCount && c.FieldCount >= 2)
                    .Take(interestTopN)
                    .Select(c => new ClassInterestCandidate { Assembly = c.Assembly, ClassFqn = c.ClassFqn })
                    .ToList();
                candidateCount = candidates.Count;

                // Pass B — interestingness scoring.
                var scored = ClassInterestEngine.Run(new ClassInterestQuery
                {
                    Snapshots = snaps, Candidates = candidates,
                });

                // Eager preview (while hydrated): top value field + key → 3 rows.
                foreach (var row in scored)
                {
                    row.Preview.Clear();
                    if (row.KeyFields.Count == 0 || row.TopValueFieldsList.Count == 0) continue;
                    try
                    {
                        var pr = PivotEngine.Run(new PivotQuery
                        {
                            Snapshots   = snaps,
                            Assembly    = row.Assembly,
                            ClassFqn    = row.ClassFqn,
                            KeyFields   = row.KeyFields.ToList(),
                            ValueFields = new List<string> { row.TopValueFieldsList[0] },
                        });
                        foreach (var m in pr.Take(3))
                        {
                            string traj = string.Join(" → ", m.Values[0].Select(s => s ?? "—"));
                            row.Preview.Add($"{string.Join(", ", m.KeyTuple)} : {traj}");
                        }
                    }
                    catch { /* preview is best-effort */ }
                }
                return scored;
            }).ConfigureAwait(true);
            sw.Stop();

            _allInterestRows = rows.ToList();
            RebuildInterestView();

            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }

            InterestStatus =
                $"Ranked {rows.Count:N0} candidate class(es) in {sw.ElapsedMilliseconds} ms (from {candidateCount:N0} Stage-0 picks). " +
                "Double-tap a row to load key+value; select a row to preview.";
            _logger.Write(SnapshotStore.LogCategory,
                $"PivotInterest: asm='{AssemblyFilter}' topN={InterestTopN} candidates={candidateCount} → {rows.Count} ranked in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"PivotInterest FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Handoff: load a ranked row's class + suggested key + top
    /// value field into the pivot query.</summary>
    public void UseInterestRow(ClassInterestRow row)
    {
        if (row is null) return;
        ClassFqn       = row.ClassFqn;
        AssemblyFilter = row.Assembly;
        RefreshKnownFieldNamesForClass();
        if (row.KeyFields.Count > 0)
            KeyFieldsText = string.Join(", ", row.KeyFields);
        if (row.TopValueFieldsList.Count > 0)
            ValueFieldsText = AppendField(ValueFieldsText, row.TopValueFieldsList[0]);
        StatusMessage = $"Loaded {row.ClassFqn}: key {row.KeyDisplay}, value {(row.TopValueFieldsList.Count > 0 ? row.TopValueFieldsList[0] : "—")}. Run to confirm.";
    }

    /// <summary>NavigateToInspector handoff — wired by MainWindow to
    /// the cross-tab navigation handler that switches to Inspector
    /// and seeds the address. Mirrors SpcQuery + Snapshot tab.</summary>
    public event EventHandler<string>? NavigateToInspectorRequested;

    public void RequestInspectInstance(PivotResultRow row, int snapIdx = -1)
    {
        if (row is null) return;
        string? addr = null;
        if (snapIdx >= 0 && snapIdx < row.AddressPerSnap.Count)
        {
            addr = row.AddressPerSnap[snapIdx];
        }
        if (string.IsNullOrEmpty(addr))
        {
            // Fall back to the latest snap that has an address.
            for (int i = row.AddressPerSnap.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(row.AddressPerSnap[i])) { addr = row.AddressPerSnap[i]; break; }
            }
        }
        if (string.IsNullOrEmpty(addr)) return;
        NavigateToInspectorRequested?.Invoke(this, addr!);
    }

    /// <summary>Clipboard hook installed by MainWindow — same pattern
    /// as SpcQuery. Receives a single string to copy.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [RelayCommand]
    public async Task CopyKeyTupleAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        await CopyToClipboardAsync(row.KeyTupleDisplay).ConfigureAwait(true);
        StatusMessage = $"Copied key tuple: {row.KeyTupleDisplay}";
    }

    [RelayCommand]
    public async Task CopyValueSequenceAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        // For multi-value-field rows, flatten all value-field sequences
        // into one user-visible string. Per-field readability beats raw
        // tab-delimited dumps here — the user just wants "what trajectory
        // does this row show".
        var sb = new System.Text.StringBuilder();
        for (int v = 0; v < row.PerValueFieldSequences.Count; v++)
        {
            if (v > 0) sb.Append('\n');
            sb.Append(row.PerValueFieldSequences[v].FieldName).Append(": ");
            sb.Append(string.Join(" → ", row.PerValueFieldSequences[v].Values.Select(s => s ?? "—")));
        }
        await CopyToClipboardAsync(sb.ToString()).ConfigureAwait(true);
        StatusMessage = "Copied value sequence.";
    }

    [RelayCommand]
    public async Task CopyAddressLatestAsync(PivotResultRow? row)
    {
        if (row is null || CopyToClipboardAsync is null) return;
        for (int i = row.AddressPerSnap.Count - 1; i >= 0; i--)
        {
            var addr = row.AddressPerSnap[i];
            if (!string.IsNullOrEmpty(addr))
            {
                await CopyToClipboardAsync(addr!).ConfigureAwait(true);
                StatusMessage = $"Copied instance address: {addr}";
                return;
            }
        }
        StatusMessage = "No instance address available for this row.";
    }

    /// <summary>Sync SelectionRows to the snapshot pool. Preserves the
    /// per-row Include toggle for rows that survive the resync.
    /// Capture-order sorted so the per-snap columns map left-to-right
    /// to the user's chronological mental model.</summary>
    private void SyncSelectionRows()
    {
        var prevById = new Dictionary<SnapshotEnvelope, PivotSnapshotRow>();
        foreach (var r in SelectionRows) prevById[r.Snapshot] = r;

        var ordered = _snapshot.Snapshots
            .OrderBy(s => s.CapturedAtUtc, StringComparer.Ordinal)
            .ToList();

        SelectionRows.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            var snap = ordered[i];
            if (prevById.TryGetValue(snap, out var prev))
            {
                prev.Index = i;
                SelectionRows.Add(prev);
            }
            else
            {
                SelectionRows.Add(new PivotSnapshotRow
                {
                    Snapshot = snap, Index = i, Include = true,
                });
            }
        }
        RebuildLocatorSnapChoices();
    }

    private const int kLargeNAdvisory = 5;

    // ── Stage 0: Analyze classes ───────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task AnalyzeClassesAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot to analyze.";
            return;
        }

        try
        {
            IsRunning = true;
            ShortlistStatus = $"Hydrating {selected.Count:N0} snapshot(s)…";

            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);

            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var shortlistQuery = new ClassShortlistQuery
            {
                Snapshots      = selected.Select(r => r.Snapshot).ToList(),
                AssemblyFilter = string.IsNullOrWhiteSpace(AssemblyFilter) ? null : AssemblyFilter.Trim(),
            };
            // Off-thread compute (iterates every class × instance).
            _allShortlistRows = await Task.Run(() => ClassShortlistEngine.Run(shortlistQuery)).ConfigureAwait(true);
            sw.Stop();

            RebuildShortlistView();

            // Evict query-only hydrates (same policy as Run).
            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }

            ShortlistStatus =
                $"Analyzed {_allShortlistRows.Count:N0} class(es) in {sw.ElapsedMilliseconds} ms — " +
                $"{ClassShortlist.Count:N0} shown. Double-click a row to set Class FQN.";
            _logger.Write(SnapshotStore.LogCategory,
                $"PivotShortlist: snaps={selected.Count} asm='{AssemblyFilter}' → {_allShortlistRows.Count} classes in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"PivotShortlist FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Re-apply the show/hide filters to <see cref="_allShortlistRows"/>
    /// → <see cref="ClassShortlist"/>. Default keeps full-presence,
    /// non-synthetic, field-rich classes (FieldCount ≥ 2).</summary>
    private void RebuildShortlistView()
    {
        string? nameFilter = string.IsNullOrWhiteSpace(ClassNameFilter) ? null : ClassNameFilter.Trim();
        ClassShortlist.Clear();
        foreach (var row in _allShortlistRows)
        {
            if (!ShortlistShowSynthetic && row.IsSynthetic) continue;
            if (!ShortlistShowPartial && row.Presence < row.SnapCount) continue;
            if (row.FieldCount < 2) continue;   // nothing to key+value on
            if (nameFilter is not null &&
                row.ClassFqn.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            ClassShortlist.Add(row);
        }
    }

    // Column sort for the shortlist grid uses the shared AOT-safe
    // CustomSortComparer mechanism (DataGridSortComparers +
    // WireSortComparers in MainWindow.axaml.cs) — keeps the native arrow
    // glyph. The flat sort composes with the ClassNameFilter because the
    // DataGridCollectionView re-applies the active sort when the bound
    // collection rebuilds.

    /// <summary>Stage-0 → Stage-2 handoff: a double-clicked shortlist row
    /// fills <see cref="ClassFqn"/> + scopes the assembly filter so
    /// ResolveAssemblyForClass picks the right one, then refreshes the
    /// field-name autocomplete for that class.</summary>
    public void UseShortlistRow(ClassShortlistRow row)
    {
        if (row is null) return;
        ClassFqn       = row.ClassFqn;
        AssemblyFilter = row.Assembly;
        RefreshKnownFieldNamesForClass();
        StatusMessage  = $"Class set to {row.ClassFqn} ({row.Assembly}). Now list key + value field name(s) and press Run.";
    }

    // ── Stage 1: Score fields ──────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task ScoreFieldsAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassFqn))
        {
            ErrorMessage = "Set a Class FQN first (type one or pick it from the Stage 0 shortlist).";
            return;
        }

        try
        {
            IsRunning = true;
            FieldScoreStatus = $"Hydrating {selected.Count:N0} snapshot(s)…";

            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);

            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            string? assembly = string.IsNullOrWhiteSpace(AssemblyFilter)
                ? selected.Select(r => ResolveAssemblyForClass(r.Snapshot, ClassFqn.Trim()))
                          .FirstOrDefault(a => !string.IsNullOrEmpty(a))
                : AssemblyFilter.Trim();
            if (string.IsNullOrEmpty(assembly))
            {
                ErrorMessage = $"Class '{ClassFqn}' not found in any selected snapshot. Check the FQN (case-sensitive, must include the namespace).";
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fieldQuery = new KeyDiscoveryQuery
            {
                Snapshots = selected.Select(r => r.Snapshot).ToList(),
                Assembly  = assembly!,
                ClassFqn  = ClassFqn.Trim(),
            };
            // Off-thread compute (scores every field across all instances).
            var result = await Task.Run(() => KeyDiscoveryEngine.Run(fieldQuery)).ConfigureAwait(true);
            sw.Stop();

            _allFieldScores = result.Fields.ToList();
            RebuildFieldScoresView();

            _suggestedKey       = result.SuggestedKey;
            SuggestedKeyDisplay = result.SuggestedKey.Count > 0
                ? "(" + string.Join(", ", result.SuggestedKey) + ")"
                : "(none)";
            SuggestedKeyReason  = result.SuggestedKeyReason;

            // Evict query-only hydrates.
            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }

            FieldScoreStatus =
                $"Scored {_allFieldScores.Count:N0} field(s) in {sw.ElapsedMilliseconds} ms. " +
                $"Double-click a row to add it as a key; right-click for value. Suggested key: {SuggestedKeyDisplay}.";
            _logger.Write(SnapshotStore.LogCategory,
                $"PivotKeyDiscovery: class={ClassFqn} fields={_allFieldScores.Count} suggested=[{string.Join(',', result.SuggestedKey)}] in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"PivotKeyDiscovery FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void RebuildFieldScoresView()
    {
        string? filter = string.IsNullOrWhiteSpace(FieldScoreFilter) ? null : FieldScoreFilter.Trim();
        FieldScores.Clear();
        foreach (var f in _allFieldScores)
        {
            if (filter is not null &&
                f.FieldName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            FieldScores.Add(f);
        }
    }

    // Field-score + results grids sort via the shared CustomSortComparer
    // mechanism (DataGridSortComparers + WireSortComparers) — see
    // MainWindow.axaml.cs. No ViewModel sort method needed.

    /// <summary>Fill <see cref="KeyFieldsText"/> with the greedy
    /// compound-key suggestion.</summary>
    [RelayCommand]
    public void ApplySuggestedKey()
    {
        if (_suggestedKey.Count == 0) return;
        KeyFieldsText = string.Join(", ", _suggestedKey);
        StatusMessage = $"Key fields set to {string.Join(", ", _suggestedKey)}.";
    }

    /// <summary>Append a field to the Key fields list (dedup, comma
    /// separated). Wired to the Stage-1 grid's double-tap + context
    /// menu.</summary>
    public void AddFieldToKeys(FieldScore field)
    {
        if (field is not null) AddFieldNameToKeys(field.FieldName);
    }

    public void AddFieldToValues(FieldScore field)
    {
        if (field is not null) AddFieldNameToValues(field.FieldName);
    }

    /// <summary>29i-1: add a field (by name — from the structure-browser
    /// detail panel's context menu) to the Key fields list.</summary>
    public void AddFieldNameToKeys(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        KeyFieldsText = AppendField(KeyFieldsText, name);
        StatusMessage = $"Added '{name}' to key fields.";
    }

    public void AddFieldNameToValues(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        ValueFieldsText = AppendField(ValueFieldsText, name);
        StatusMessage = $"Added '{name}' to value fields.";
    }

    private static string AppendField(string existing, string name)
    {
        var parts = SplitFieldList(existing);
        if (parts.Any(p => string.Equals(p, name, StringComparison.Ordinal)))
            return existing;   // already present
        parts.Add(name);
        return string.Join(", ", parts);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        ErrorMessage = string.Empty;
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            ErrorMessage = "Pick at least 1 snapshot.";
            StatusMessage = string.Empty;
            return;
        }
        if (string.IsNullOrWhiteSpace(ClassFqn))
        {
            ErrorMessage = "Class FQN is required (e.g. 'Mortal.Core.GameStat').";
            return;
        }
        // Mode-aware field lists. Instance mode keys on KeyFieldsText;
        // array mode reuses KeyFieldsText as the OUTER (owner-instance) key
        // (may be empty) and adds inner element key + value fields.
        var keyFields      = SplitFieldList(KeyFieldsText);   // outer in array mode
        var innerKeyFields = new List<string>();
        List<string> valueFields;
        if (ArrayPivotMode)
        {
            if (string.IsNullOrWhiteSpace(ArrayFieldName))
            {
                ErrorMessage = "Array field is required for inner-pivot (e.g. '_statAdditionData').";
                return;
            }
            innerKeyFields = SplitFieldList(InnerKeyFieldsText);
            if (innerKeyFields.Count == 0)
            {
                ErrorMessage = "List at least one inner key field — a field INSIDE the array element (e.g. '_propertyID').";
                return;
            }
            valueFields = SplitFieldList(InnerValueFieldsText);
            if (valueFields.Count == 0)
            {
                ErrorMessage = "List at least one inner value field — a field INSIDE the array element (e.g. '_amount').";
                return;
            }
        }
        else
        {
            if (keyFields.Count == 0)
            {
                ErrorMessage = "List at least one key field name (e.g. '_propertyID' or '_itemGroup, _propertyID').";
                return;
            }
            valueFields = SplitFieldList(ValueFieldsText);
            if (valueFields.Count == 0)
            {
                ErrorMessage = "List at least one value field name (e.g. '_value' or '_hp, _mp').";
                return;
            }
        }

        try
        {
            IsRunning = true;
            UpdateModeBanner(selected,
                ArrayPivotMode ? keyFields.Concat(innerKeyFields).ToList() : keyFields);

            // Track which envelopes were already hydrated BEFORE this run
            // so we evict only the ones we ourselves loaded (same policy
            // as SpcQueryViewModel — keeps user-loaded snaps hot for
            // re-diff while not bloating RAM with query-only hydrates).
            var preloaded = new HashSet<SnapshotEnvelope>();
            foreach (var r in selected)
                if (r.Snapshot.ClassesLoaded) preloaded.Add(r.Snapshot);

            if (selected.Count > kLargeNAdvisory)
            {
                long estPerSnap = selected[0].Snapshot.EstimatedMemoryBytes;
                if (estPerSnap == 0)
                    estPerSnap = selected[0].Snapshot.PersistedBytes * 50;
                long total = estPerSnap * selected.Count;
                StatusMessage = $"Hydrating {selected.Count:N0} snapshots (~{FormatMb(total)} estimated peak RAM).";
            }

            foreach (var r in selected)
                await _snapshot.EnsureLoadedAsync(r.Snapshot).ConfigureAwait(true);

            RefreshKnownClassesAndFields();

            // Resolve assembly from any hydrated snapshot that has the
            // class. Required for PivotEngine — same FQN can exist in
            // multiple assemblies (game DLLs + plugins); we pick the
            // first assembly that contains the class in the earliest
            // selected snap.
            string? assembly = ResolveAssemblyForClass(selected[0].Snapshot, ClassFqn)
                            ?? selected.Select(r => ResolveAssemblyForClass(r.Snapshot, ClassFqn))
                                       .FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (string.IsNullOrEmpty(assembly))
            {
                ErrorMessage = $"Class '{ClassFqn}' not found in any selected snapshot. Check the FQN (it must include the namespace; case-sensitive).";
                return;
            }

            var snaps     = selected.Select(r => r.Snapshot).ToList();
            string? keyFilterArg = string.IsNullOrWhiteSpace(KeyFilter) ? null : KeyFilter.Trim();

            var sw = Stopwatch.StartNew();
            string classFqnArg = ClassFqn.Trim();
            // Off-thread compute (groups every instance across all snapshots).
            List<PivotRow> matches;
            if (ArrayPivotMode)
            {
                var apQuery = new ArrayPivotQuery
                {
                    Snapshots      = snaps,
                    Assembly       = assembly!,
                    ClassFqn       = classFqnArg,
                    ArrayField     = ArrayFieldName.Trim(),
                    OuterKeyFields = keyFields,        // KeyFieldsText = outer key (may be empty)
                    InnerKeyFields = innerKeyFields,
                    ValueFields    = valueFields,
                    KeyFilter      = keyFilterArg,
                };
                matches = await Task.Run(() => ArrayPivotEngine.Run(apQuery)).ConfigureAwait(true);
            }
            else
            {
                var pQuery = new PivotQuery
                {
                    Snapshots   = snaps,
                    Assembly    = assembly!,
                    ClassFqn    = classFqnArg,
                    KeyFields   = keyFields,
                    ValueFields = valueFields,
                    KeyFilter   = keyFilterArg,
                };
                matches = await Task.Run(() => PivotEngine.Run(pQuery)).ConfigureAwait(true);
            }
            sw.Stop();

            // Build PivotResultRow per match — pre-render the trajectory
            // as a multi-line "fieldName: v0 → v1 → …" string. The
            // DataGrid's single Trajectory column binds directly to it
            // via CompiledBinding (AOT-safe). For the single-value-field
            // case the row reads like one SPC line; multi-value rows
            // get one line per value field stacked.
            int snapCount  = selected.Count;
            int valueCount = valueFields.Count;
            Results.Clear();
            foreach (var m in matches)
            {
                var perField = new List<PivotValueFieldSequence>(valueCount);
                var sb = new System.Text.StringBuilder();
                for (int v = 0; v < valueCount; v++)
                {
                    perField.Add(new PivotValueFieldSequence
                    {
                        FieldName = valueFields[v],
                        Values    = m.Values[v],
                    });
                    if (v > 0) sb.Append('\n');
                    if (valueCount > 1) sb.Append(valueFields[v]).Append(": ");
                    for (int s = 0; s < snapCount; s++)
                    {
                        if (s > 0) sb.Append(" → ");
                        sb.Append(m.Values[v][s] ?? "—");
                    }
                }
                Results.Add(new PivotResultRow
                {
                    KeyTupleDisplay        = string.Join(", ", m.KeyTuple),
                    KeyTuple               = m.KeyTuple,
                    TrajectoryDisplay      = sb.ToString(),
                    AddressPerSnap         = m.AddressPerSnap,
                    HasCollision           = m.HasCollision,
                    PerValueFieldSequences = perField,
                });
            }

            int evicted = 0;
            foreach (var r in selected)
            {
                if (preloaded.Contains(r.Snapshot)) continue;
                if (!r.Snapshot.ClassesLoaded) continue;
                if (r.Snapshot.PersistedId is null) continue;
                r.Snapshot.Unload();
                evicted++;
            }
            string evictionNote = evicted > 0 ? $" — evicted {evicted:N0} hydrated-for-query envelope(s)" : string.Empty;

            int collisionCount = matches.Count(m => m.HasCollision);
            string collisionNote = collisionCount > 0 ? $", {collisionCount} with collisions" : string.Empty;
            string layerBHint = (ArrayPivotMode && matches.Count == 0)
                ? " — no captured element fields. Re-take the snapshot with \"Capture array element fields\" enabled on the Snapshot tab (Layer B)."
                : string.Empty;
            StatusMessage = $"Pivot: {matches.Count:N0} row(s){collisionNote} across {snapCount:N0} snapshot(s) in {sw.ElapsedMilliseconds} ms{evictionNote}.{layerBHint}";
            _logger.Write(SnapshotStore.LogCategory,
                $"Pivot: class={ClassFqn} keys=[{string.Join(',', keyFields)}] vals=[{string.Join(',', valueFields)}] " +
                $"snaps={snapCount} → {matches.Count} rows ({collisionCount} collisions) in {sw.ElapsedMilliseconds}ms evicted={evicted}");
        }
        catch (ArgumentException ax)
        {
            ErrorMessage = ax.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(SnapshotStore.LogCategory, $"Pivot FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning && _snapshot.Snapshots.Count >= 1;

    private static List<string> SplitFieldList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .ToList();
    }

    private static string? ResolveAssemblyForClass(SnapshotEnvelope env, string fqn)
    {
        if (!env.ClassesLoaded || env.Classes is null) return null;
        foreach (var c in env.Classes)
        {
            string cfqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
            if (SnapshotEnvelope.ClassFqnEquals(cfqn, fqn))
                return c.Assembly;
        }
        return null;
    }

    /// <summary>Rebuild the class + assembly autocomplete from the union
    /// of (a) the DB-seeded class list (<see cref="_persistedClasses"/>,
    /// available before any hydrate) and (b) any currently-hydrated
    /// snapshot's in-memory classes (which adds unsaved / fresh-capture
    /// classes the DB doesn't know yet), then refresh the field-name
    /// autocomplete for the typed class. Field names still require a
    /// hydrate — they live in the gzipped blob, not the metadata
    /// columns.</summary>
    private void RefreshKnownClassesAndFields()
    {
        var classSet = new HashSet<string>(StringComparer.Ordinal);
        var asmSet   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pc in _persistedClasses)
        {
            if (!string.IsNullOrEmpty(pc.ClassFqn)) classSet.Add(pc.ClassFqn);
            if (!string.IsNullOrEmpty(pc.Assembly)) asmSet.Add(pc.Assembly);
        }
        foreach (var env in _snapshot.Snapshots)
        {
            if (!env.ClassesLoaded || env.Classes is null) continue;
            foreach (var c in env.Classes)
            {
                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                classSet.Add(fqn);
                if (!string.IsNullOrEmpty(c.Assembly)) asmSet.Add(c.Assembly);
            }
        }

        KnownClasses.Clear();
        foreach (var c in classSet.OrderBy(s => s, StringComparer.Ordinal))
            KnownClasses.Add(c);
        KnownAssemblies.Clear();
        foreach (var a in asmSet.OrderBy(s => s, StringComparer.Ordinal))
            KnownAssemblies.Add(a);

        RefreshKnownFieldNamesForClass();
    }

    /// <summary>Phase 29e-7 — pull the distinct class / assembly list
    /// from the open per-game DB (no blob hydration) and refresh the
    /// autocomplete. Wired to run right after the DB opens on connect,
    /// so the Class FQN + Assembly boxes suggest names immediately —
    /// no "run something first to populate" chicken-and-egg.</summary>
    public async Task SeedKnownClassesFromDbAsync()
    {
        _persistedClasses = await _snapshot.GetPersistedClassesAsync().ConfigureAwait(true);
        RefreshKnownClassesAndFields();
    }

    private void RefreshKnownFieldNamesForClass()
    {
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ClassFqn))
        {
            foreach (var env in _snapshot.Snapshots)
            {
                if (!env.ClassesLoaded || env.Classes is null) continue;
                foreach (var c in env.Classes)
                {
                    string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                    if (!SnapshotEnvelope.ClassFqnEquals(fqn, ClassFqn)) continue;
                    if (c.Instances is not null)
                    {
                        // Phase 31 — EnumerateFlatFields also surfaces inline
                        // struct components (_position.x) as pickable fields.
                        foreach (var inst in c.Instances)
                            foreach (var (name, _) in SnapshotEnvelope.EnumerateFlatFields(inst.Fields))
                                if (!string.IsNullOrEmpty(name)) nameSet.Add(name);
                    }
                }
            }
        }
        KnownFieldNames.Clear();
        foreach (var n in nameSet.OrderBy(s => s, StringComparer.Ordinal))
            KnownFieldNames.Add(n);

        RefreshKnownArrayFields();
        RefreshKnownElementFieldNames();
    }

    /// <summary>Discover reference-type array field names on the typed
    /// class (fields whose <c>ArrayElements.IsPointer</c> is set) so the
    /// Array field AutoCompleteBox can suggest inner-pivot targets.</summary>
    private void RefreshKnownArrayFields()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ClassFqn))
        {
            foreach (var c in EnumerateTypedClass())
                if (c.Instances is not null)
                    foreach (var inst in c.Instances)
                        foreach (var f in inst.Fields)
                            if (f.ArrayElements is { IsPointer: true } && !string.IsNullOrEmpty(f.Name))
                                set.Add(f.Name);
        }
        KnownArrayFields.Clear();
        foreach (var n in set.OrderBy(s => s, StringComparer.Ordinal))
            KnownArrayFields.Add(n);
    }

    /// <summary>Discover inner field names captured on the elements of the
    /// typed <see cref="ArrayFieldName"/> (Layer B) so the inner key /
    /// value AutoCompleteBoxes can suggest them.</summary>
    private void RefreshKnownElementFieldNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ClassFqn) && !string.IsNullOrWhiteSpace(ArrayFieldName))
        {
            foreach (var c in EnumerateTypedClass())
                if (c.Instances is not null)
                    foreach (var inst in c.Instances)
                        foreach (var f in inst.Fields)
                        {
                            if (!string.Equals(f.Name, ArrayFieldName, StringComparison.Ordinal)) continue;
                            var els = f.ArrayElements?.Elements;
                            if (els is null) continue;
                            foreach (var el in els)
                                if (el.Fields is not null)
                                    foreach (var ef in el.Fields)
                                        if (!string.IsNullOrEmpty(ef.Name)) set.Add(ef.Name);
                        }
        }
        KnownElementFieldNames.Clear();
        foreach (var n in set.OrderBy(s => s, StringComparer.Ordinal))
            KnownElementFieldNames.Add(n);
    }

    /// <summary>Yield hydrated <see cref="SnapshotClassEntry"/> rows whose
    /// FQN matches the typed <see cref="ClassFqn"/> across all snapshots.</summary>
    private IEnumerable<SnapshotClassEntry> EnumerateTypedClass()
    {
        foreach (var env in _snapshot.Snapshots)
        {
            if (!env.ClassesLoaded || env.Classes is null) continue;
            foreach (var c in env.Classes)
            {
                string fqn = string.IsNullOrEmpty(c.Namespace) ? c.Name : $"{c.Namespace}.{c.Name}";
                if (SnapshotEnvelope.ClassFqnEquals(fqn, ClassFqn))
                    yield return c;
            }
        }
    }

    /// <summary>Compose the pivot-specific banner. Pivot is built for
    /// cross-session so the same "different session_id" situation
    /// that triggers an SPC warning is here a feature — the banner
    /// confirms what the user signed up for.</summary>
    private void UpdateModeBanner(IReadOnlyList<PivotSnapshotRow> selected, IReadOnlyList<string> keyFields)
    {
        if (selected.Count == 0) { ModeBanner = string.Empty; return; }
        var firstBuild   = selected[0].Snapshot.GameBuildId;
        var firstSession = selected[0].Snapshot.GameSessionId;
        bool buildMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameBuildId, firstBuild, StringComparison.Ordinal));
        bool sessionMismatch = selected.Skip(1)
            .Any(r => !string.Equals(r.Snapshot.GameSessionId, firstSession, StringComparison.Ordinal));

        string keyDisplay = "(" + string.Join(", ", keyFields) + ")";
        if (buildMismatch)
        {
            ModeBanner = $"⚠ Cross-build pivot — game build_id differs across the selected snapshots. Key fields {keyDisplay} are verified at run-time but field offsets may have shifted between game versions; missing keys render as '(missing)'.";
        }
        else if (sessionMismatch)
        {
            ModeBanner = $"ℹ Cross-session pivot — keying by {keyDisplay} sidesteps address drift across game sessions. This is exactly what the Class Pivot tab is built for.";
        }
        else
        {
            ModeBanner = string.Empty;
        }
    }

    private static string FormatMb(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        if (bytes >= 1024L * 1024 * 1024)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
        double mb = bytes / (1024.0 * 1024.0);
        return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }
}

/// <summary>One row in the snapshot-picker grid. Mirrors
/// <see cref="SpcSnapshotRow"/> but without the per-snap predicate
/// fields — pivot doesn't have them.</summary>
public sealed partial class PivotSnapshotRow : ObservableObject
{
    public required SnapshotEnvelope Snapshot { get; init; }
    [ObservableProperty] private int  _index;
    [ObservableProperty] private bool _include;

    public string DisplayLabel => $"[{Index}] {Snapshot.DisplayName}";
}

/// <summary>One result row. Wraps a <see cref="PivotRow"/> with a
/// flat Cells[] array (so a DataGrid column at index
/// <c>v*snapCount + s</c> binds directly to the value-field /
/// snapshot pair).</summary>
public sealed class PivotResultRow
{
    public required string                          KeyTupleDisplay        { get; init; }
    public required IReadOnlyList<string>           KeyTuple               { get; init; }
    /// <summary>Pre-rendered "fieldName: v0 → v1 → …" string (multi-line
    /// when multiple value fields). Bound directly by the result
    /// DataGrid's Trajectory column — avoids Avalonia 12 AOT's
    /// reflection-Binding ban on dynamic column paths.</summary>
    public required string                          TrajectoryDisplay      { get; init; }
    public required IReadOnlyList<string?>          AddressPerSnap         { get; init; }
    public required bool                            HasCollision           { get; init; }
    public required IReadOnlyList<PivotValueFieldSequence> PerValueFieldSequences { get; init; }

    /// <summary>UI helper: a single chip-like cell that renders the
    /// collision flag — used by the DataGrid's first column. Empty
    /// string when no collision so cells look identical to non-
    /// collision rows.</summary>
    public string CollisionMarker => HasCollision ? "⚠" : string.Empty;
}

/// <summary>One value field's per-snap trajectory, kept alongside the
/// flat Cells[] for the Copy-value-sequence context-menu action.</summary>
public sealed class PivotValueFieldSequence
{
    public required string                FieldName { get; init; }
    public required IReadOnlyList<string?> Values   { get; init; }
}
