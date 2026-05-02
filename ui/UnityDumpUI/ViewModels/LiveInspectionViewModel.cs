// LiveInspectionViewModel.cs — Phase 9 panels coordinator.
//
// Single VM coordinating the right-click-to-inspect flow from the Phase 8
// class browser:
//
//   1. SetTarget(assembly, ns, name)         — caller (Phase 8 VM) hands a class.
//   2. FindInstancesCommand                  — heap-scan for live instances.
//   3. SelectedInstance cascade              — walkInstance decodes fields.
//   4. AutoRefreshIntervalMs                 — re-walks at 250 ms / 1 s.
//
// Static-field reads (`getStaticFieldData` + `readMemory`) are exposed as a
// command so the view can wire a static-field row to a "fetch storage address"
// button; the full StaticFieldPanel UX comes in a later sub-phase.
//
// AOT mandate matches Phase 8: CommunityToolkit.Mvvm source-gen only, no
// reflection JSON paths (BackendAdapter already enforces that).
//
// Auto-refresh uses PeriodicTimer in an async loop. Continuations run on the
// captured UI sync context via the project-wide ConfigureAwait(true) habit;
// no DispatcherTimer dependency in the VM.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class LiveInspectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;

    private CancellationTokenSource? _findCts;
    private CancellationTokenSource? _walkCts;
    private CancellationTokenSource? _autoRefreshCts;
    private Task?                    _autoRefreshLoop;

    public LiveInspectionViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
    }

    // ---------- Target ----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string? _targetAssembly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string? _targetNamespace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string? _targetName;

    public bool HasTarget =>
        !string.IsNullOrEmpty(TargetAssembly) && !string.IsNullOrEmpty(TargetName);

    public string TargetLabel
    {
        get
        {
            if (!HasTarget) return string.Empty;
            string display = string.IsNullOrEmpty(TargetNamespace)
                ? TargetName!
                : $"{TargetNamespace}.{TargetName}";
            return $"{TargetAssembly}!{display}";
        }
    }

    /// <summary>
    /// Called by the assembly browser (right-click → "Find live instances").
    /// Resets all downstream state and stops auto-refresh; the caller decides
    /// whether to immediately kick a find via <see cref="FindInstancesCommand"/>.
    /// </summary>
    public void SetTarget(string assembly, string? @namespace, string name)
    {
        _ = StopAutoRefreshAsync();
        TargetAssembly  = assembly;
        TargetNamespace = @namespace ?? string.Empty;
        TargetName      = name;
        Instances.Clear();
        SelectedInstance = null;
        Fields.Clear();
        ErrorMessage = null;
        WalkSummary  = string.Empty;
    }

    // ---------- Instances -------------------------------------------------

    public ObservableCollection<InstanceItem> Instances { get; } = new();

    [ObservableProperty] private int _instanceLimit = 256;

    /// <summary>Class header pointer (IL2CPP) or vtable (Mono) reported by the DLL.</summary>
    [ObservableProperty] private string? _classPointer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    private InstanceItem? _selectedInstance;

    public bool HasSelectedInstance => SelectedInstance is not null;

    partial void OnSelectedInstanceChanged(InstanceItem? value)
    {
        Fields.Clear();
        WalkSummary = string.Empty;
        if (value is null || !HasTarget) return;
        _ = WalkInstanceAsync(value.Address);
    }

    // ---------- Walked fields ---------------------------------------------

    public ObservableCollection<WalkedFieldRow> Fields { get; } = new();

    /// <summary>Short "depth=N count=M" line shown above the field grid.</summary>
    [ObservableProperty] private string _walkSummary = string.Empty;

    // ---------- Auto refresh ---------------------------------------------

    /// <summary>0 = off, 250, 1000.</summary>
    [ObservableProperty] private int _autoRefreshIntervalMs;

    partial void OnAutoRefreshIntervalMsChanged(int value)
    {
        _ = RestartAutoRefreshAsync();
    }

    // ---------- Status ----------------------------------------------------

    [ObservableProperty] private bool    _isLoadingInstances;
    [ObservableProperty] private bool    _isLoadingWalk;
    [ObservableProperty] private string? _errorMessage;

    // ---------- Commands --------------------------------------------------

    [RelayCommand]
    private async Task FindInstancesAsync()
    {
        if (!HasTarget) return;

        _findCts?.Cancel();
        _findCts = new CancellationTokenSource();
        CancellationToken ct = _findCts.Token;

        ErrorMessage = null;
        IsLoadingInstances = true;
        Instances.Clear();
        SelectedInstance = null;
        try
        {
            FindInstancesResult result = await _backend
                .FindInstancesAsync(TargetAssembly!, TargetNamespace, TargetName!,
                                    InstanceLimit, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = $"findInstances: {result.Error}";
                return;
            }
            ClassPointer = result.ClassPtr ?? result.VTable;
            int idx = 0;
            foreach (string addr in result.Matches)
            {
                Instances.Add(new InstanceItem(idx++, addr));
            }
            _logger.Write(Constants.LogCategoryView,
                $"findInstances {TargetLabel} matches={result.Count}/{result.Limit} cls={ClassPointer ?? "?"}");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(Constants.LogCategoryView,
                $"findInstances({TargetLabel}) failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsLoadingInstances = false;
        }
    }

    [RelayCommand]
    private Task RefreshWalkAsync()
    {
        if (SelectedInstance is null) return Task.CompletedTask;
        return WalkInstanceAsync(SelectedInstance.Address);
    }

    [RelayCommand] private void AutoRefreshOff()  => AutoRefreshIntervalMs = 0;
    [RelayCommand] private void AutoRefreshFast() => AutoRefreshIntervalMs = 250;
    [RelayCommand] private void AutoRefreshSlow() => AutoRefreshIntervalMs = 1000;

    [RelayCommand]
    private void Clear()
    {
        _ = StopAutoRefreshAsync();
        TargetAssembly  = null;
        TargetNamespace = null;
        TargetName      = null;
        ClassPointer    = null;
        Instances.Clear();
        SelectedInstance = null;
        Fields.Clear();
        WalkSummary  = string.Empty;
        ErrorMessage = null;
    }

    private async Task WalkInstanceAsync(string address)
    {
        if (!HasTarget) return;

        _walkCts?.Cancel();
        _walkCts = new CancellationTokenSource();
        CancellationToken ct = _walkCts.Token;

        ErrorMessage = null;
        IsLoadingWalk = true;
        try
        {
            WalkInstanceResult result = await _backend
                .WalkInstanceAsync(address, TargetAssembly!, TargetNamespace, TargetName!, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = $"walkInstance: {result.Error}";
                return;
            }
            Fields.Clear();
            if (result.Fields is { } fields)
            {
                foreach (WalkedField f in fields)
                {
                    Fields.Add(WalkedFieldRow.From(f));
                }
            }
            WalkSummary = $"depth={result.Depth} count={result.Count}";
            _logger.Write(Constants.LogCategoryView,
                $"walkInstance {address} {TargetLabel} {WalkSummary}");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(Constants.LogCategoryView,
                $"walkInstance({address}) failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsLoadingWalk = false;
        }
    }

    // ---------- Static-field bridge --------------------------------------

    /// <summary>
    /// `getStaticFieldData` → `readMemory` for the named static field. Returns
    /// the raw bytes as uppercase hex, or an error message in
    /// <see cref="ErrorMessage"/>. Caller is responsible for type-aware decode.
    /// </summary>
    public async Task<StaticFieldRead?> ReadStaticFieldAsync(string field, int size,
                                                              CancellationToken ct = default)
    {
        if (!HasTarget) return null;
        ErrorMessage = null;
        try
        {
            GetStaticFieldDataResult info = await _backend
                .GetStaticFieldDataAsync(TargetAssembly!, TargetNamespace, TargetName!, field, ct)
                .ConfigureAwait(true);
            if (!string.IsNullOrEmpty(info.Error))
            {
                ErrorMessage = $"getStaticFieldData: {info.Error}";
                return null;
            }
            if (string.IsNullOrEmpty(info.Address))
            {
                ErrorMessage = "getStaticFieldData: missing address";
                return null;
            }
            ReadMemoryResult mem = await _backend
                .ReadMemoryAsync(info.Address, size, ct)
                .ConfigureAwait(true);
            if (!string.IsNullOrEmpty(mem.Error))
            {
                ErrorMessage = $"readMemory: {mem.Error}";
                return new StaticFieldRead(field, info.Type, info.Address, null);
            }
            return new StaticFieldRead(field, info.Type, info.Address, mem.Hex);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(Constants.LogCategoryView,
                $"readStaticField({field}) failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---------- Auto-refresh loop ----------------------------------------

    private async Task RestartAutoRefreshAsync()
    {
        await StopAutoRefreshAsync().ConfigureAwait(true);
        if (AutoRefreshIntervalMs <= 0) return;

        _autoRefreshCts = new CancellationTokenSource();
        CancellationToken ct = _autoRefreshCts.Token;
        TimeSpan interval = TimeSpan.FromMilliseconds(AutoRefreshIntervalMs);
        _autoRefreshLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
            {
                if (SelectedInstance is null || IsLoadingWalk) continue;
                await WalkInstanceAsync(SelectedInstance.Address).ConfigureAwait(true);
            }
        }, ct);
    }

    private async Task StopAutoRefreshAsync()
    {
        if (_autoRefreshCts is null) return;
        _autoRefreshCts.Cancel();
        try
        {
            if (_autoRefreshLoop is not null) await _autoRefreshLoop.ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* swallow shutdown errors */ }
        _autoRefreshCts.Dispose();
        _autoRefreshCts = null;
        _autoRefreshLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAutoRefreshAsync().ConfigureAwait(false);
        _findCts?.Cancel();
        _walkCts?.Cancel();
    }
}

// ----- Row records ----------------------------------------------------------

public sealed class InstanceItem
{
    public InstanceItem(int index, string address)
    {
        Index   = index;
        Address = address;
    }
    public int    Index   { get; }
    public string Address { get; }
    public string Label   => $"#{Index}  {Address}";
}

/// <summary>
/// Row projection of a <see cref="WalkedField"/> for the field DataGrid.
/// Computes a single human-readable <see cref="DisplayValue"/> from the
/// heterogeneous wire payload (bool / number / hex pointer / string / error).
/// </summary>
public sealed class WalkedFieldRow
{
    public WalkedFieldRow(string name, string type, int offset, string declaredIn,
                          string? address, string displayValue, bool isPointer)
    {
        Name         = name;
        Type         = type;
        Offset       = offset;
        DeclaredIn   = declaredIn;
        Address      = address;
        DisplayValue = displayValue;
        IsPointer    = isPointer;
    }

    public string  Name         { get; }
    public string  Type         { get; }
    public int     Offset       { get; }
    public string  OffsetHex    => $"0x{Offset:X}";
    public string  DeclaredIn   { get; }
    public string? Address      { get; }
    public string  DisplayValue { get; }
    /// <summary>True when the value is a pointer (qword) — UI can offer click-to-walk.</summary>
    public bool    IsPointer    { get; }

    public static WalkedFieldRow From(WalkedField f)
    {
        string display = FormatDisplay(f, out bool isPtr);
        return new WalkedFieldRow(
            f.Name,
            f.Type ?? string.Empty,
            f.Offset,
            f.From ?? string.Empty,
            f.Address,
            display,
            isPtr);
    }

    private static string FormatDisplay(WalkedField f, out bool isPointer)
    {
        isPointer = false;
        if (!string.IsNullOrEmpty(f.Error))      return $"<error: {f.Error}>";
        if (!string.IsNullOrEmpty(f.StringError)) return $"<string_error: {f.StringError}> {f.Ptr}";

        // String layout: prefer the decoded string (with length suffix); fall back to ptr.
        if (f.String is not null)
        {
            string len = f.StringLength is int sl ? $" (len {sl})" : string.Empty;
            return $"\"{f.String}\"{len}";
        }

        if (f.Value is JsonElement v)
        {
            return v.ValueKind switch
            {
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Null   => "null",
                _                    => v.GetRawText(),
            };
        }

        if (!string.IsNullOrEmpty(f.Ptr))
        {
            isPointer = true;
            return f.Ptr!;
        }

        return string.Empty;
    }
}

/// <summary>Snapshot returned by <see cref="LiveInspectionViewModel.ReadStaticFieldAsync"/>.</summary>
public sealed class StaticFieldRead
{
    public StaticFieldRead(string field, string? type, string address, string? hex)
    {
        Field   = field;
        Type    = type;
        Address = address;
        Hex     = hex;
    }
    public string  Field   { get; }
    public string? Type    { get; }
    public string  Address { get; }
    /// <summary>Uppercase hex bytes; null when the read failed (see VM ErrorMessage).</summary>
    public string? Hex     { get; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Field} @ {Address} = {Hex ?? "<unreadable>"}");
}
