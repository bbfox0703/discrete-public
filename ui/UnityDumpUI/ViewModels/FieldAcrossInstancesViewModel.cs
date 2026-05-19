// FieldAcrossInstancesViewModel.cs — Phase 21a.
//
// Drives the "List this field across instances" modal: heap-scans for every
// live instance of a target class, decodes one specific field at each
// instance, and exposes the (instance, fieldAddress, value) tuples as a
// DataGrid-friendly row list. Inverse of the Inspector (which goes from one
// instance → all its fields).
//
// Wire path: BackendAdapter.ListFieldValuesAcrossInstancesAsync —
// see `docs/pipe-protocol.md` §"listFieldValuesAcrossInstances". The DLL
// returns rows in whatever order Memory::ScanForPointer found them, which
// is the same order findInstances returns — we don't re-sort because
// instance address rarely carries meaning the user wants to sort by;
// they'll filter/sort in the grid if they need to.

using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class FieldAcrossInstancesViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _cts;

    public FieldAcrossInstancesViewModel(
        BackendAdapter backend, FileLogger logger,
        string targetAssembly, string? targetNamespace, string targetName,
        string fieldName, string fieldType, long fieldOffset)
    {
        _backend         = backend;
        _logger          = logger;
        TargetAssembly   = targetAssembly;
        TargetNamespace  = targetNamespace ?? string.Empty;
        TargetName       = targetName;
        FieldName        = fieldName;
        FieldType        = fieldType;
        FieldOffset      = fieldOffset;
    }

    public string TargetAssembly  { get; }
    public string TargetNamespace { get; }
    public string TargetName      { get; }
    public string FieldName       { get; }
    public string FieldType       { get; }
    public long   FieldOffset     { get; }

    public string ClassLabel => string.IsNullOrEmpty(TargetNamespace)
        ? $"{TargetAssembly}!{TargetName}"
        : $"{TargetAssembly}!{TargetNamespace}.{TargetName}";

    public string OffsetLabel => $"+0x{FieldOffset:X} ({FieldOffset} bytes)";

    /// <summary>
    /// Raw rows the DLL returned. The DataGrid binds to <see cref="FilteredRows"/>
    /// instead, which is the visible projection after the
    /// <see cref="ValueFilter"/> substring match is applied.
    /// </summary>
    public ObservableCollection<FieldAcrossInstanceRow> Rows { get; } = new();

    /// <summary>
    /// View-side filtered mirror of <see cref="Rows"/>, driven by
    /// <see cref="ValueFilter"/>. Empty filter mirrors Rows exactly. UI-only
    /// — Copy commands still pull the clicked row's tuple verbatim, no need
    /// to project through the filter.
    /// </summary>
    public ObservableCollection<FieldAcrossInstanceRow> FilteredRows { get; } = new();

    /// <summary>
    /// Substring filter applied to <see cref="FilteredRows"/>. Matches the
    /// row's <see cref="FieldAcrossInstanceRow.Value"/> column
    /// (case-insensitive). Empty = show all. Phase 21a polish — lets the
    /// user type e.g. <c>"99"</c> to find every instance whose chrNo is 99.
    /// </summary>
    [ObservableProperty] private string _valueFilter = string.Empty;
    partial void OnValueFilterChanged(string value) => RefilterRows();

    [ObservableProperty] private int     _instanceLimit = 256;
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private int _matchCount;

    public string CountLabel => $"{MatchCount} live";

    /// <summary>
    /// View-supplied clipboard write — same pattern as the other VMs (wired
    /// by the View code-behind so this stays Avalonia-agnostic).
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            ListFieldValuesResult result = await _backend
                .ListFieldValuesAcrossInstancesAsync(
                    TargetAssembly, TargetNamespace, TargetName,
                    FieldOffset, FieldType, InstanceLimit, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                Rows.Clear();
                FilteredRows.Clear();
                MatchCount = 0;
                return;
            }
            Rows.Clear();
            if (result.Rows is { } rows)
            {
                foreach (ListFieldValueRow r in rows)
                {
                    Rows.Add(new FieldAcrossInstanceRow(
                        r.Instance     ?? string.Empty,
                        r.FieldAddress ?? string.Empty,
                        FormatValue(r)));
                }
            }
            MatchCount = result.Count;
            RefilterRows();
            _logger.Write(Constants.LogCategoryView,
                $"listFieldValuesAcrossInstances {ClassLabel} +0x{FieldOffset:X} {FieldType}: {result.Count} matches");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"listFieldValuesAcrossInstances failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand]
    private Task CopyInstanceAsync(FieldAcrossInstanceRow? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.Instance))
            ? f(row.Instance) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyFieldAddressAsync(FieldAcrossInstanceRow? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.FieldAddress))
            ? f(row.FieldAddress) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyValueAsync(FieldAcrossInstanceRow? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.Value))
            ? f(row.Value) : Task.CompletedTask;

    /// <summary>
    /// Rebuild <see cref="FilteredRows"/> from <see cref="Rows"/> using the
    /// current <see cref="ValueFilter"/> as a case-insensitive substring
    /// match against each row's <see cref="FieldAcrossInstanceRow.Value"/>.
    /// Called on filter-change AND at the end of <see cref="RefreshAsync"/>
    /// so a brand-new result set immediately respects the typed filter.
    /// Sorting is owned by the DataGrid via per-column
    /// <c>CustomSortComparer</c> instances wired in the View's code-behind,
    /// so this method doesn't need to reorder.
    /// </summary>
    private void RefilterRows()
    {
        FilteredRows.Clear();
        string needle = (ValueFilter ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            foreach (var r in Rows) FilteredRows.Add(r);
            return;
        }
        foreach (var r in Rows)
        {
            if (r.Value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                FilteredRows.Add(r);
            }
        }
    }

    /// <summary>
    /// Render one decoded wire row as the human-readable string the DataGrid
    /// shows in the Value column. Mirrors <see cref="WalkedFieldNode"/>'s
    /// <c>FormatDisplay</c> rules (the inspector decodes the same payload
    /// shape per type), minus the pointer-fallback heuristic — we just dump
    /// whatever the wire layer gave us.
    /// </summary>
    private static string FormatValue(ListFieldValueRow r)
    {
        if (!string.IsNullOrEmpty(r.Error)) return $"<error: {r.Error}>";
        if (!string.IsNullOrEmpty(r.StringError)) return $"<string_error: {r.StringError}>";
        if (r.String is not null)
        {
            string len = r.StringLength is int sl ? $" (len {sl})" : string.Empty;
            return $"\"{r.String}\"{len}";
        }
        if (r.Value is JsonElement v)
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
        if (!string.IsNullOrEmpty(r.Ptr)) return r.Ptr!;
        return string.Empty;
    }
}

/// <summary>
/// One row in the Field-across-instances grid. Three columns: instance base
/// address, the resolved field's address (instance + cumulative offset),
/// and the decoded value (already stringified by the VM).
/// </summary>
public sealed class FieldAcrossInstanceRow
{
    public FieldAcrossInstanceRow(string instance, string fieldAddress, string value)
    {
        Instance     = instance;
        FieldAddress = fieldAddress;
        Value        = value;
    }
    public string Instance     { get; }
    public string FieldAddress { get; }
    public string Value        { get; }
}
