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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand))]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    private string? _sessionId;

    public bool HasSession => !string.IsNullOrEmpty(SessionId);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand))]
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

        // If a previous session is alive, free it before opening a new one.
        if (HasSession)
        {
            try { await _backend.EndValueScanAsync(SessionId!, ct).ConfigureAwait(true); }
            catch { /* best-effort */ }
            SessionId = null;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            BeginValueScanResult result;
            if (IsStringMode)
            {
                result = await _backend.ScanStringAsync(
                    ValueText, SelectedEncoding, SelectedMatchMode, Limit, ct)
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
                    valTo, Limit, tol, ct: ct).ConfigureAwait(true);
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
            StatusMessage = result.Truncated
                ? $"{Results.Count} hit(s){shownNote} (truncated at limit={Limit}); dropped {result.DroppedNonManaged} non-managed; scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms{sessionNote}."
                : $"{Results.Count} hit(s){shownNote}; dropped {result.DroppedNonManaged} non-managed; scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms{sessionNote}.";
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
    /// and closes every transient session. Presents a one-shot result —
    /// <see cref="SessionId"/> stays null so Next Scan stays disabled.
    /// Caller has already validated the value/range/tolerance args.
    /// </summary>
    private async Task RunAggregateFirstScanAsync(
        double val, double? valTo, double? tol, Stopwatch sw, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    reuseIndexMs: kReuseIndexMs, ct: ct)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                firstError ??= $"{type}: {ex.Message}";
                continue;
            }

            // Close this width's transient session immediately — aggregate
            // mode never refines, so holding it would leak server memory.
            if (!string.IsNullOrEmpty(r.SessionId))
            {
                try { await _backend.EndValueScanAsync(r.SessionId!, ct).ConfigureAwait(true); }
                catch { /* best-effort cleanup */ }
            }

            if (!string.IsNullOrEmpty(r.Error))
            {
                firstError ??= $"{type}: {r.Error}";
                continue;
            }

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
        RefreshResultView();
        sw.Stop();

        if (Results.Count == 0 && firstError is not null)
        {
            ErrorMessage  = firstError;
            StatusMessage = "First Scan failed.";
            return;
        }
        if (firstError is not null)
            ErrorMessage = $"some widths errored (first: {firstError})";

        string trunc = truncated ? $" (truncated at limit={Limit})" : string.Empty;
        string shownNote = FilteredResults.Count != Results.Count
            ? $" (showing {FilteredResults.Count} after filters)" : string.Empty;
        StatusMessage =
            $"{Results.Count} unique hit(s){shownNote} across {typesScanned} numeric width(s){trunc}; "
            + $"dropped {totalDropped} non-managed; scanned {totalScannedBytes / (1024 * 1024)} MB "
            + $"in {sw.ElapsedMilliseconds} ms (one-shot — pick a single type to enable Next Scan).";
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
        try
        {
            await _backend.EndValueScanAsync(SessionId!).ConfigureAwait(true);
        }
        catch { /* best effort */ }
        SessionId = null;
        Results.Clear();
        FilteredResults.Clear();
        StatusMessage = "Session ended.";
        ErrorMessage  = null;
    }

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
