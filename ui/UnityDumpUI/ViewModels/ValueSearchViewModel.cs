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

    public ValueSearchViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;

        // Default dataType / scanType picks
        SelectedDataType  = "int32";
        SelectedScanType  = "exact";
        SelectedRefineType = "exact";   // most common Next-Scan usage
        SelectedEncoding  = "utf16";
        SelectedMatchMode = "contains";
    }

    public string[] DataTypes { get; } = new[]
    {
        "int8", "uint8", "int16", "uint16", "int32", "uint32",
        "int64", "uint64", "float", "double", "string",
    };

    public string[] ScanTypes { get; } = new[] { "exact", "range" };

    public string[] RefineTypes { get; } = new[]
    {
        "exact", "range", "newValue",
        "changed", "unchanged", "increased", "decreased",
    };

    public string[] Encodings  { get; } = new[] { "utf16", "utf8", "ascii" };
    public string[] MatchModes { get; } = new[] { "contains", "exact" };

    public ObservableCollection<ValueSearchRow> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstScanCommand))]
    [NotifyPropertyChangedFor(nameof(IsStringMode))]
    [NotifyPropertyChangedFor(nameof(IsNumericMode))]
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
    [ObservableProperty] private int    _limit          = 500;

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
        Results.Clear();
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
                result = await _backend.BeginValueScanAsync(
                    SelectedDataType, SelectedScanType, val,
                    valTo, Limit, tol, ct).ConfigureAwait(true);
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

            sw.Stop();
            string sessionNote = HasSession ? $", session={SessionId}" : " (one-shot, no session)";
            StatusMessage = result.Truncated
                ? $"{Results.Count} hit(s) (truncated at limit={Limit}); dropped {result.DroppedNonManaged} non-managed; scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms{sessionNote}."
                : $"{Results.Count} hit(s); dropped {result.DroppedNonManaged} non-managed; scanned {result.ScannedBytes / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms{sessionNote}.";
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
        }
    }

    [RelayCommand(CanExecute = nameof(CanNextScan))]
    public async Task NextScanAsync()
    {
        if (!HasSession) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true; ErrorMessage = null;
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

            sw.Stop();
            StatusMessage = $"refined '{SelectedRefineType}' — {result.TotalCandidates} remaining, removed {result.Removed} this round ({sw.ElapsedMilliseconds} ms).";
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
        StatusMessage = "Session ended.";
        ErrorMessage  = null;
    }

    public void Activate(ValueSearchRow row) => NavigateToInspectorRequested?.Invoke(row);
    public void RequestWriteValue(ValueSearchRow row) => WriteValueRequested?.Invoke(row);

    private bool CanFirstScan() => !IsBusy && !string.IsNullOrWhiteSpace(ValueText);
    private bool CanNextScan()  => !IsBusy && HasSession;
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
