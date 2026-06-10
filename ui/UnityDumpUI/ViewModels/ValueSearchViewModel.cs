// ValueSearchViewModel.cs — Phase 27d.
//
// CE-style First Scan / Next Scan UI surface. Wraps the Begin / Refine /
// End RPCs in a session-state-aware ViewModel so the user can:
//   1) Press First Scan with a value + dataType → server scans the whole
//      committed RW heap, filters non-managed via whatIsAt, opens a
//      session, populates the results grid.
//   2) Press Next Scan with one of seven scanTypes (exact / range /
//      changed / unchanged / increased / decreased / newValue) → server
//      prunes the session in place, grid refreshes.
//   3) Press Clear → server drops the session, grid empties.
//
// AOT-safe: CommunityToolkit.Mvvm source-gen + zero reflection.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class ValueSearchViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _elapsedTimer;
    private Stopwatch?       _elapsedStopwatch;

    // Cross-VM bulk lane: value-scan is a long session op, mutually
    // exclusive with snapshot capture. Null in tests / single-VM use.
    private readonly BulkOperationGate? _bulkGate;
    private IDisposable? _bulkLease;

    public ValueSearchViewModel(BackendAdapter backend, FileLogger logger,
                                BulkOperationGate? bulkGate = null)
    {
        _backend = backend;
        _logger  = logger;
        _bulkGate = bulkGate;
        if (_bulkGate is not null)
            _bulkGate.PropertyChanged += (_, _) =>
            {
                FirstScanCommand.NotifyCanExecuteChanged();
                NextScanCommand.NotifyCanExecuteChanged();
            };

        // Default dataType / scanType picks
        SelectedDataType  = "int32";
        SelectedScanType  = "exact";
        SelectedRefineType = "exact";   // most common Next-Scan usage
        SelectedEncoding  = "utf16";
        SelectedMatchMode = "contains";
    }

    // Aggregate "don't make me guess the width" pseudo-types. Selecting one
    // runs a First Scan across every concrete numeric width and merges the
    // results, so the user doesn't have to know whether the game stores a
    // stat as int32 / float / double. They are one-shot (no server session),
    // so Next Scan is unavailable until the user re-picks a single concrete
    // type — refining a union of widths in place isn't well-defined.
    public const string AggregateNoByte = "numeric (no 1-byte)";
    public const string AggregateAll    = "numeric (all widths)";

    public string[] DataTypes { get; } = new[]
    {
        "int8", "uint8", "int16", "uint16", "int32", "uint32",
        "int64", "uint64", "float", "double", "string",
        AggregateNoByte, AggregateAll,
    };

    // Concrete widths each aggregate expands into. "no 1-byte" drops int8 /
    // uint8 because single-byte matches are overwhelmingly false positives
    // on a value like 100; "all widths" includes them for completeness.
    private static readonly string[] AggregateNoByteTypes =
    {
        "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double",
    };
    private static readonly string[] AggregateAllTypes =
    {
        "int8", "uint8", "int16", "uint16", "int32", "uint32",
        "int64", "uint64", "float", "double",
    };

    public bool IsAggregateMode =>
        string.Equals(SelectedDataType, AggregateNoByte, StringComparison.Ordinal) ||
        string.Equals(SelectedDataType, AggregateAll,    StringComparison.Ordinal);

    private string[] CurrentAggregateTypes =>
        string.Equals(SelectedDataType, AggregateAll, StringComparison.Ordinal)
            ? AggregateAllTypes
            : AggregateNoByteTypes;

    public string[] ScanTypes { get; } = new[] { "exact", "range" };

    public string[] RefineTypes { get; } = new[]
    {
        "exact", "range", "newValue",
        "changed", "unchanged", "increased", "decreased",
    };

    public string[] Encodings  { get; } = new[] { "utf16", "utf8", "ascii" };
    public string[] MatchModes { get; } = new[] { "contains", "exact" };

    /// <summary>Raw scan hits (every candidate the backend returned). The grid
    /// binds to <see cref="FilteredResults"/>; this stays the unfiltered store
    /// so toggling the filters re-derives the view without re-scanning.</summary>
    public ObservableCollection<ValueSearchRow> Results { get; } = new();

    /// <summary>View over <see cref="Results"/> after applying
    /// <see cref="IgnoreSystemAssemblies"/> + <see cref="ResultFilter"/>. This
    /// is what the results DataGrid binds to.</summary>
    public ObservableCollection<ValueSearchRow> FilteredResults { get; } = new();

    /// <summary>When true, hide hits in engine / framework assemblies
    /// (Unity*, System*, Cinemachine*, …) so the grid shows game-code hits
    /// only. Defaults on — a value like 65 otherwise matches thousands of
    /// engine objects (see the Cinemachine.* noise). Toggling re-filters the
    /// existing results in place.</summary>
    [ObservableProperty] private bool _ignoreSystemAssemblies = true;
    partial void OnIgnoreSystemAssembliesChanged(bool value) => RefreshResultView();

    /// <summary>Case-insensitive substring filter over the results grid —
    /// matches Class / Field / Type / Instance / Address. Empty shows all
    /// (subject to <see cref="IgnoreSystemAssemblies"/>).</summary>
    [ObservableProperty] private string _resultFilter = string.Empty;
    partial void OnResultFilterChanged(string value) => RefreshResultView();

    // Engine / framework assembly + namespace prefixes hidden when
    // IgnoreSystemAssemblies is on. Matched against the row's Assembly,
    // Namespace AND ClassDisplay (so namespaced engine types like
    // "Cinemachine.CinemachineBrain" are caught even when the Assembly field
    // is blank). Game code (Assembly-CSharp, bare-namespace types like
    // CharacterData) is left visible.
    private static readonly string[] SystemPrefixes =
    {
        "Unity", "System", "Mono", "Microsoft", "mscorlib", "netstandard",
        "Cinemachine", "Newtonsoft", "TMPro", "DG.Tweening", "Spine",
    };

    public static bool IsSystemName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string p in SystemPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsSystemRow(ValueSearchRow r)
        => IsSystemName(r.Assembly) || IsSystemName(r.Namespace) || IsSystemName(r.ClassDisplay);

    /// <summary>Rebuild <see cref="FilteredResults"/> from <see cref="Results"/>
    /// applying the assembly toggle + substring filter. Cheap O(n) rebuild —
    /// result sets are capped at <see cref="Limit"/>.</summary>
    private void RefreshResultView()
    {
        FilteredResults.Clear();
        string filter = ResultFilter ?? string.Empty;
        bool hasFilter = filter.Length > 0;
        bool hideSys   = IgnoreSystemAssemblies;
        foreach (ValueSearchRow r in Results)
        {
            if (hideSys && IsSystemRow(r)) continue;
            if (hasFilter && !RowMatchesFilter(r, filter)) continue;
            FilteredResults.Add(r);
        }
    }

    private static bool RowMatchesFilter(ValueSearchRow r, string filter)
        => r.ClassDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.Field.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.FieldType.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.InstanceAddr.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || r.Address.Contains(filter, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstScanCommand))]
    [NotifyPropertyChangedFor(nameof(IsStringMode))]
    [NotifyPropertyChangedFor(nameof(IsNumericMode))]
    [NotifyPropertyChangedFor(nameof(IsAggregateMode))]
    private string _selectedDataType = "int32";

    public bool IsStringMode  => string.Equals(SelectedDataType, "string", StringComparison.Ordinal);
    public bool IsNumericMode => !IsStringMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValueTo))]
    private string _selectedScanType = "exact";

    [ObservableProperty] private string _selectedRefineType = "exact";
    [ObservableProperty] private string _selectedEncoding   = "utf16";
    [ObservableProperty] private string _selectedMatchMode  = "contains";

    public bool NeedsValueTo => string.Equals(SelectedScanType, "range", StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstScanCommand))]
    private string _valueText = string.Empty;

    [ObservableProperty] private string _valueToText  = string.Empty;
    // Default 0.5 matches CE's "Rounded (default)" float-exact semantics:
    // searching for float 32 catches anything in [31.5, 32.5). Game HUDs
    // round float HP/MP/coins to int display, so this is the value almost
    // every CT author actually wants. Set to 0 for exact-binary match
    // (rarely useful — IEEE-754 floats accumulate FP drift).
    [ObservableProperty] private string _toleranceText = "0.5";
    [ObservableProperty] private int    _limit          = 10000;

    /// <summary>Max managed candidates the DLL RETAINS for refine — decoupled
    /// from the display <see cref="Limit"/> (only Limit rows cross the pipe).
    /// High so a COMMON value's specific instance survives the first scan and
    /// refine can narrow to it. The DLL clamps this by bitness (32-bit Mono →
    /// 50000 since the DLL shares the game's address space; x64 → up to
    /// 1,000,000), so it's a request, not a guarantee.</summary>
    [ObservableProperty] private int    _maxCandidates  = 200000;

    /// <summary>Multi-threaded scanning. On by default (cores−2 worker threads ⇒
    /// much faster). Turn OFF for games whose anti-tamper reacts to many threads
    /// hammering memory in parallel — the scan then runs single-threaded (slower
    /// but lower-profile). Safe on Mono either way: the parallel parts only read
    /// process memory, so no GC/BDWGC interaction regardless of this setting.</summary>
    [ObservableProperty] private bool   _parallelScan   = true;

    /// <summary>Wire form of <see cref="ParallelScan"/> for the backend's optional
    /// <c>parallel</c> arg: <c>null</c> when ON (omitted from the request → the
    /// DLL's <c>args.value("parallel", true)</c> default applies = full parallel),
    /// <c>false</c> when OFF (sent → server caps the scan to a single thread). This
    /// honours the "only sent when OFF" contract documented on BackendAdapter and
    /// the DLL handlers, instead of always transmitting a redundant <c>true</c>.</summary>
    private bool? ParallelArg => ParallelScan ? (bool?)null : false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand))]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    private string? _sessionId;

    /// <summary>
    /// Aggregate ("numeric ...") mode keeps ONE live refine session per concrete
    /// width (the DLL's SessionManager already holds many sessions concurrently),
    /// so Next Scan can refine every width and merge — instead of the old
    /// fan-out-then-close one-shot. Empty in single-type mode (that uses
    /// <see cref="SessionId"/>). Each entry is (sessionId, dataType).
    /// </summary>
    private readonly List<(string id, string type)> _multiSessions = new();

    public bool HasSession => !string.IsNullOrEmpty(SessionId) || _multiSessions.Count > 0;

    /// <summary>Raise the session-derived gates after the multi-session list
    /// changes (it isn't an observable property, so notify by hand).</summary>
    private void NotifyMultiSessionChanged()
    {
        OnPropertyChanged(nameof(HasSession));
        NextScanCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Close every live session (single + per-width) and clear state.
    /// Best-effort — a failed End is non-fatal (server expiry reaps it).</summary>
    private async Task CloseAllSessionsAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(SessionId))
        {
            try { await _backend.EndValueScanAsync(SessionId!, ct).ConfigureAwait(true); }
            catch { /* best-effort */ }
            SessionId = null;
        }
        if (_multiSessions.Count > 0)
        {
            foreach ((string id, _) in _multiSessions.ToArray())
            {
                try { await _backend.EndValueScanAsync(id, ct).ConfigureAwait(true); }
                catch { /* best-effort */ }
            }
            _multiSessions.Clear();
            NotifyMultiSessionChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Pick a dataType, type a value, press First Scan.";
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private ValueSearchRow? _selectedRow;

    /// <summary>Raised when the user activates a row (double-click).
    /// MainWindowViewModel cross-tabs to the Inspector via FocusOnInstance
    /// — same plumbing the Phase 17c Address Lookup tab uses.</summary>
    public event Action<ValueSearchRow>? NavigateToInspectorRequested;
    public event Action<ValueSearchRow>? WriteValueRequested;

    [RelayCommand(CanExecute = nameof(CanFirstScan))]
    public async Task FirstScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
        _bulkLease = _bulkGate?.TryEnter("Value search");
        Results.Clear();
        FilteredResults.Clear();
        StartElapsedTimer("Scanning");

        // If any previous session is alive (single OR per-width aggregate), free
        // it before opening a new one.
        if (HasSession)
            await CloseAllSessionsAsync(ct).ConfigureAwait(true);

        var sw = Stopwatch.StartNew();
        try
        {
            BeginValueScanResult result;
            if (IsStringMode)
            {
                result = await _backend.ScanStringAsync(
                    ValueText, SelectedEncoding, SelectedMatchMode, Limit,
                    parallel: ParallelArg, ct: ct)
                    .ConfigureAwait(true);
            }
            else
            {
                if (!double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    ErrorMessage  = $"Value '{ValueText}' is not a valid number.";
                    StatusMessage = "First Scan failed.";
                    return;
                }
                double? valTo = null;
                if (NeedsValueTo)
                {
                    if (!double.TryParse(ValueToText, NumberStyles.Float, CultureInfo.InvariantCulture, out double vt))
                    {
                        ErrorMessage  = $"Range upper bound '{ValueToText}' is not a valid number.";
                        StatusMessage = "First Scan failed.";
                        return;
                    }
                    valTo = vt;
                }
                double? tol = null;
                if (double.TryParse(ToleranceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    tol = t;

                // Aggregate "any numeric width" mode: the DLL only scans one
                // width per session, so we fan out — one beginValueScan per
                // concrete type — and merge the unique addresses. Each scan
                // opens its own session; we close them all and present the
                // union as a one-shot result (SessionId stays null, so
                // Next Scan is disabled until the user picks a single type).
                if (IsAggregateMode)
                {
                    await RunAggregateFirstScanAsync(val, valTo, tol, sw, ct).ConfigureAwait(true);
                    return;
                }

                result = await _backend.BeginValueScanAsync(
                    SelectedDataType, SelectedScanType, val,
                    valTo, Limit, tol, retainCap: MaxCandidates,
                    parallel: ParallelArg, ct: ct).ConfigureAwait(true);
            }

            if (string.Equals(result.Error, "cancelled", StringComparison.Ordinal))
            {
                StatusMessage = "Scan cancelled.";
                return;
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                StatusMessage = "First Scan failed.";
                return;
            }

            SessionId = string.IsNullOrEmpty(result.SessionId) ? null : result.SessionId;
            foreach (var c in result.Candidates)
                Results.Add(new ValueSearchRow(c));
            RefreshResultView();

            sw.Stop();
            string sessionNote = HasSession ? $", session={SessionId}" : " (one-shot, no session)";
            string shownNote = FilteredResults.Count != Results.Count
                ? $" (showing {FilteredResults.Count} after filters)" : string.Empty;
            // Retain/display are decoupled: TotalCandidates = managed matches KEPT
            // for refine; Results.Count = rows actually shown. Surface both + the
            // reason for any truncation so the user knows whether to raise
            // Max candidates or just refine.
            int retained = result.TotalCandidates;
            string retainNote = retained > Results.Count
                ? $" — {retained:N0} retained for refine" : string.Empty;
            string truncNote = result.RawTruncated
                ? $" ⚠ raw scan capped — more instances may exist; raise Max candidates ({MaxCandidates:N0}) or refine."
                : result.RetainTruncated
                    ? $" ⚠ retained set capped at {result.RetainCap:N0} — raise Max candidates to keep more."
                    : string.Empty;
            StatusMessage =
                $"{Results.Count} shown{shownNote}{retainNote}; dropped {result.DroppedNonManaged} non-managed; " +
                $"scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms{sessionNote}.{truncNote}";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "First Scan failed.";
            _logger.Write(Constants.LogCategoryView, $"ValueSearch.FirstScan failed: {ex.Message}");
        }
        finally
        {
            StopElapsedTimer();
            IsBusy = false;
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    /// <summary>
    /// Fan-out First Scan for the aggregate "any numeric width" modes. Runs
    /// one <c>beginValueScan</c> per concrete width, merges the unique hit
    /// addresses into <see cref="Results"/> (capped at <see cref="Limit"/>),
    /// and RETAINS each width's session in <see cref="_multiSessions"/> so
    /// Next Scan can refine every width and re-merge (the DLL's SessionManager
    /// holds them all concurrently). Caller has already validated the
    /// value/range/tolerance args.
    /// </summary>
    private async Task RunAggregateFirstScanAsync(
        double val, double? valTo, double? tol, Stopwatch sw, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newSessions = new List<(string id, string type)>();
        long totalScannedBytes = 0;
        long totalDropped = 0;
        int  typesScanned = 0;
        bool truncated = false;
        string? firstError = null;

        // Reuse window for the DLL-side reverse-lookup index. Every width in
        // this fan-out scans the same heap, so the instance-base index is
        // identical — building it once (~48s on a large 32-bit Mono heap) and
        // reusing it for the remaining widths turns an N×48s wait into one.
        // The window must comfortably exceed the whole sweep's wall time; the
        // per-width scan itself is bounded, so 5 min is safe. The first width's
        // call still pays the full build (cache empty / stale), the rest copy.
        const int kReuseIndexMs = 5 * 60 * 1000;

        foreach (string type in CurrentAggregateTypes)
        {
            if (ct.IsCancellationRequested) return;

            BeginValueScanResult r;
            try
            {
                r = await _backend.BeginValueScanAsync(
                    type, SelectedScanType, val, valTo, Limit, tol,
                    reuseIndexMs: kReuseIndexMs, parallel: ParallelArg, ct: ct)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                firstError ??= $"{type}: {ex.Message}";
                continue;
            }

            if (!string.IsNullOrEmpty(r.Error))
            {
                // Failed width — close its session if one was opened, then skip.
                if (!string.IsNullOrEmpty(r.SessionId))
                {
                    try { await _backend.EndValueScanAsync(r.SessionId!, ct).ConfigureAwait(true); }
                    catch { /* best-effort */ }
                }
                firstError ??= $"{type}: {r.Error}";
                continue;
            }

            // Retain this width's session so Next Scan can refine it.
            if (!string.IsNullOrEmpty(r.SessionId))
                newSessions.Add((r.SessionId!, type));

            typesScanned++;
            totalScannedBytes = Math.Max(totalScannedBytes, r.ScannedBytes);
            totalDropped += r.DroppedNonManaged;

            foreach (ValueScanCandidate c in r.Candidates)
            {
                if (Results.Count >= Limit) { truncated = true; break; }
                if (seen.Add(c.Address))
                    Results.Add(new ValueSearchRow(c));
            }
            if (Results.Count >= Limit) { truncated = true; break; }
        }

        SessionId = null;
        // If a value/error left us with NO usable sessions, don't pretend we have
        // one; otherwise retain them so Next Scan refines every width.
        if (Results.Count == 0 && firstError is not null && newSessions.Count == 0)
        {
            _multiSessions.Clear();
            NotifyMultiSessionChanged();
            RefreshResultView();
            sw.Stop();
            ErrorMessage  = firstError;
            StatusMessage = "First Scan failed.";
            return;
        }
        _multiSessions.Clear();
        _multiSessions.AddRange(newSessions);
        NotifyMultiSessionChanged();
        RefreshResultView();
        sw.Stop();

        if (firstError is not null)
            ErrorMessage = $"some widths errored (first: {firstError})";

        string trunc = truncated ? $" (truncated at limit={Limit})" : string.Empty;
        string shownNote = FilteredResults.Count != Results.Count
            ? $" (showing {FilteredResults.Count} after filters)" : string.Empty;
        string refineNote = _multiSessions.Count > 0
            ? $" (Next Scan refines all {_multiSessions.Count} width(s))"
            : " (one-shot)";
        StatusMessage =
            $"{Results.Count} unique hit(s){shownNote} across {typesScanned} numeric width(s){trunc}; "
            + $"dropped {totalDropped} non-managed; scanned {totalScannedBytes / (1024 * 1024)} MB "
            + $"in {sw.ElapsedMilliseconds} ms{refineNote}.";
    }

    [RelayCommand(CanExecute = nameof(CanNextScan))]
    public async Task NextScanAsync()
    {
        if (!HasSession) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
        _bulkLease = _bulkGate?.TryEnter("Value search");
        StartElapsedTimer("Refining");
        var sw = Stopwatch.StartNew();
        try
        {
            double? val = null, valTo = null;
            bool needsArg = SelectedRefineType is "exact" or "range" or "newValue";
            if (needsArg)
            {
                if (!double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    ErrorMessage  = $"Refine mode '{SelectedRefineType}' needs a numeric value; got '{ValueText}'.";
                    StatusMessage = "Next Scan failed.";
                    return;
                }
                val = v;
                if (SelectedRefineType == "range")
                {
                    if (!double.TryParse(ValueToText, NumberStyles.Float, CultureInfo.InvariantCulture, out double vt))
                    {
                        ErrorMessage  = "Range refine needs a valid upper bound.";
                        StatusMessage = "Next Scan failed.";
                        return;
                    }
                    valTo = vt;
                }
            }

            // Aggregate mode: refine every per-width session and re-merge.
            if (_multiSessions.Count > 0)
            {
                await NextScanMultiAsync(val, valTo, sw, ct).ConfigureAwait(true);
                return;
            }

            RefineValueScanResult result = await _backend.RefineValueScanAsync(
                SessionId!, SelectedRefineType, val, valTo, 500, ct).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                StatusMessage = "Next Scan failed.";
                return;
            }

            Results.Clear();
            foreach (var c in result.Candidates)
                Results.Add(new ValueSearchRow(c));
            RefreshResultView();

            sw.Stop();
            string shownNote = FilteredResults.Count != Results.Count
                ? $" (showing {FilteredResults.Count} after filters)" : string.Empty;
            StatusMessage = $"refined '{SelectedRefineType}' — {result.TotalCandidates} remaining{shownNote}, removed {result.Removed} this round ({sw.ElapsedMilliseconds} ms).";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Next Scan failed.";
        }
        finally
        {
            StopElapsedTimer();
            IsBusy = false;
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    /// <summary>
    /// Next Scan for aggregate mode: refine each retained per-width session with
    /// the same comparator + value, merge the surviving unique addresses, and
    /// report aggregate remaining/removed counts. A width whose session has
    /// expired (server-side 5-min reap) is dropped from the set; other errors
    /// keep the session and are surfaced. Caller owns the elapsed timer + IsBusy.
    /// </summary>
    private async Task NextScanMultiAsync(double? val, double? valTo, Stopwatch sw, CancellationToken ct)
    {
        var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var survivors = new List<(string id, string type)>();
        long totalRemaining = 0, totalRemoved = 0;
        int  widthsRefined  = 0;
        string? firstError  = null;

        Results.Clear();
        foreach ((string id, string type) in _multiSessions.ToArray())
        {
            if (ct.IsCancellationRequested) return;

            RefineValueScanResult r;
            try
            {
                r = await _backend.RefineValueScanAsync(id, SelectedRefineType, val, valTo, 500, ct)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                firstError ??= $"{type}: {ex.Message}";
                survivors.Add((id, type));   // keep — transient failure
                continue;
            }

            if (!string.IsNullOrEmpty(r.Error))
            {
                // A reaped session is gone for good — drop it. Anything else is
                // (probably) recoverable, so keep it and note the first error.
                if (r.Error.Contains("session_not_found", StringComparison.OrdinalIgnoreCase) ||
                    r.Error.Contains("not_found", StringComparison.OrdinalIgnoreCase))
                    firstError ??= $"{type}: session expired";
                else { firstError ??= $"{type}: {r.Error}"; survivors.Add((id, type)); }
                continue;
            }

            widthsRefined++;
            totalRemaining += r.TotalCandidates;
            totalRemoved   += r.Removed;
            survivors.Add((id, type));
            foreach (ValueScanCandidate c in r.Candidates)
                if (seen.Add(c.Address)) Results.Add(new ValueSearchRow(c));
        }

        _multiSessions.Clear();
        _multiSessions.AddRange(survivors);
        NotifyMultiSessionChanged();
        RefreshResultView();
        sw.Stop();

        if (firstError is not null)
            ErrorMessage = $"some widths errored (first: {firstError})";
        string shownNote = FilteredResults.Count != Results.Count
            ? $" (showing {FilteredResults.Count} after filters)" : string.Empty;
        StatusMessage =
            $"refined '{SelectedRefineType}' across {widthsRefined} width(s) — "
            + $"{totalRemaining} remaining{shownNote}, removed {totalRemoved} this round "
            + $"({sw.ElapsedMilliseconds} ms).";
    }

    // Live elapsed-time display while a scan is in flight. Real per-step
    // progress would need pipe-protocol changes (the request-response
    // pipe can't stream interim updates); for v1 just surface the wall
    // clock so the user knows the UI isn't frozen.
    private void StartElapsedTimer(string verb)
    {
        _elapsedStopwatch = Stopwatch.StartNew();
        StatusMessage = $"{verb}… 0.0 s";
        _elapsedTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tag = verb;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        if (_elapsedTimer is not null) _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedStopwatch = null;
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        if (_elapsedStopwatch is null) return;
        if (sender is DispatcherTimer t && t.Tag is string verb)
        {
            StatusMessage = $"{verb}… {_elapsedStopwatch.Elapsed.TotalSeconds:F1} s";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEndSession))]
    public async Task EndSessionAsync()
    {
        if (!HasSession) return;
        await CloseAllSessionsAsync().ConfigureAwait(true);
        Results.Clear();
        FilteredResults.Clear();
        StatusMessage = "Session ended.";
        ErrorMessage  = null;
    }

    /// <summary>Cancel an in-flight scan. Two parts: cancel the UI-side awaits
    /// (which also stops the aggregate "all widths" fan-out from sending the next
    /// width) AND tell the DLL to abort the scan it's currently running — the
    /// in-flight RPC runs uncancellable on the wire, so the UI token alone won't
    /// stop the DLL; <c>cancelScan</c> arms the DLL's cooperative cancel.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private async Task CancelScanAsync()
    {
        StatusMessage = "Cancelling…";
        _cts?.Cancel();
        await _backend.CancelScanAsync().ConfigureAwait(true);
    }
    private bool CanCancelScan() => IsBusy;

    public void Activate(ValueSearchRow row) => NavigateToInspectorRequested?.Invoke(row);
    public void RequestWriteValue(ValueSearchRow row) => WriteValueRequested?.Invoke(row);

    private bool CanFirstScan() => !IsBusy && !BulkBusyElsewhere && !string.IsNullOrWhiteSpace(ValueText);
    private bool CanNextScan()  => !IsBusy && !BulkBusyElsewhere && HasSession;

    /// <summary>True when ANOTHER bulk op (e.g. snapshot) holds the lane.
    /// Our own scan sets IsBusy, so this only greys us out for others'.</summary>
    private bool BulkBusyElsewhere => _bulkGate is not null && _bulkGate.IsBusy && _bulkLease is null;
    private bool CanEndSession() => !IsBusy && HasSession;
}

public sealed class ValueSearchRow
{
    public ValueSearchRow(ValueScanCandidate c)
    {
        Address       = c.Address;
        InstanceAddr  = c.InstanceAddr ?? string.Empty;
        Assembly      = c.Assembly     ?? string.Empty;
        Namespace     = c.Namespace    ?? string.Empty;
        Class         = c.Class        ?? string.Empty;
        Field         = c.Field        ?? string.Empty;
        FieldType     = c.FieldType    ?? string.Empty;
        FieldKind     = c.FieldKind    ?? string.Empty;
        FieldOffset   = c.FieldOffset  ?? 0;
        CurrentValue  = c.CurrentValue?.ToString() ?? string.Empty;
        ClassDisplay  = string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";
        OffsetLabel   = $"0x{FieldOffset:X}";
    }

    public string Address      { get; }
    public string InstanceAddr { get; }
    public string Assembly     { get; }
    public string Namespace    { get; }
    public string Class        { get; }
    public string ClassDisplay { get; }
    public string Field        { get; }
    public string FieldType    { get; }
    public string FieldKind    { get; }
    public long   FieldOffset  { get; }
    public string OffsetLabel  { get; }
    public string CurrentValue { get; }
}
