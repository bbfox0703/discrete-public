// FieldReferencesViewModel.cs — Phase 28.
//
// Drives the "Find methods that touch this field…" modal: calls the
// `findFieldReferences` RPC for the supplied field, surfaces the resulting
// (Class / Method / Kind / Confidence / Address / Source) rows in a
// DataGrid-friendly collection.
//
// Three UI entry points all route through this VM:
//   1. Inspector grid right-click "Find methods that touch this field…"
//   2. Symbol Finder result row right-click (when the row is a field)
//   3. Browser Fields tab right-click
//
// All three pass the same constructor args (assembly + ns + class + field
// name + type + isStatic flag). isStatic is informational — both backends
// return the resolved field metadata in the response header, the VM just
// surfaces it in the title strip so the user immediately sees whether the
// IL2CPP "instance fields not supported in v1" path is going to fire.

using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class FieldReferencesViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _cts;

    public FieldReferencesViewModel(
        BackendAdapter backend, FileLogger logger,
        string targetAssembly, string? targetNamespace, string targetName,
        string fieldName, string? fieldType, bool isStatic)
    {
        _backend         = backend;
        _logger          = logger;
        TargetAssembly   = targetAssembly;
        TargetNamespace  = targetNamespace ?? string.Empty;
        TargetName       = targetName;
        FieldName        = fieldName;
        FieldType        = fieldType ?? string.Empty;
        IsFieldStatic    = isStatic;
    }

    public string TargetAssembly  { get; }
    public string TargetNamespace { get; }
    public string TargetName      { get; }
    public string FieldName       { get; }
    public string FieldType       { get; }
    public bool   IsFieldStatic   { get; }

    public string ClassLabel => string.IsNullOrEmpty(TargetNamespace)
        ? $"{TargetAssembly}!{TargetName}"
        : $"{TargetAssembly}!{TargetNamespace}.{TargetName}";

    public string FieldLabel => string.IsNullOrEmpty(FieldType)
        ? FieldName
        : $"{FieldName} : {FieldType}";

    public string StaticBadgeLabel => IsFieldStatic ? "[static]" : "[instance]";

    public ObservableCollection<FieldReferenceRowVm> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private int _matchCount;

    [ObservableProperty] private int     _matchLimit = 200;
    [ObservableProperty] private string  _kindFilter = "any";   // "any" / "read" / "write"
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private bool    _truncated;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _resolvedFieldOffsetLabel;
    [ObservableProperty] private string? _resolvedStaticAddressLabel;
    [ObservableProperty] private int     _scannedMethods;

    public string CountLabel => Truncated
        ? $"{MatchCount}+ matches (truncated)"
        : $"{MatchCount} matches";

    /// <summary>Clipboard delegate wired by the View code-behind so this VM
    /// stays Avalonia-agnostic.</summary>
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
            FindFieldReferencesResult result = await _backend
                .FindFieldReferencesAsync(
                    TargetAssembly,
                    TargetNamespace,
                    TargetName,
                    FieldName,
                    kind: KindFilter,
                    limit: MatchLimit,
                    ct: ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                Rows.Clear();
                MatchCount = 0;
                Truncated  = false;
                return;
            }

            Rows.Clear();
            if (result.References is { } refs)
            {
                foreach (FieldReferenceRow r in refs)
                {
                    Rows.Add(new FieldReferenceRowVm(
                        assembly:   r.Assembly,
                        @namespace: r.Namespace,
                        @class:     r.Class,
                        method:     r.Method,
                        kind:       r.Kind,
                        confidence: r.Confidence,
                        address:    r.Address ?? string.Empty,
                        hitAddress: r.HitAddress ?? string.Empty,
                        ilOffset:   r.IlOffset.HasValue ? $"IL_{r.IlOffset.Value:X4}" : string.Empty,
                        isMethodStatic: r.IsMethodStatic));
                }
            }
            MatchCount      = result.References?.Count ?? 0;
            Truncated       = result.Truncated;
            ScannedMethods  = result.ScannedMethods;
            ResolvedFieldOffsetLabel    = result.Field?.Offset is long off
                ? $"+0x{off:X} ({off} bytes)"
                : null;
            ResolvedStaticAddressLabel  = !string.IsNullOrEmpty(result.Field?.StaticAddress)
                ? result.Field!.StaticAddress
                : null;

            _logger.Write(Constants.LogCategoryView,
                $"findFieldReferences {ClassLabel}::{FieldName} kind={KindFilter}: "
                + $"{MatchCount} matches ({ScannedMethods} methods scanned, "
                + $"truncated={Truncated})");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"findFieldReferences failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand]
    private Task CopyClassAsync(FieldReferenceRowVm? row) =>
        (CopyToClipboardAsync is { } f && row is not null)
            ? f($"{row.Namespace}.{row.Class}".TrimStart('.')) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyMethodAsync(FieldReferenceRowVm? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.Method))
            ? f(row.Method) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyAddressAsync(FieldReferenceRowVm? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.Address))
            ? f(row.Address) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyHitAddressAsync(FieldReferenceRowVm? row) =>
        (CopyToClipboardAsync is { } f && row is not null && !string.IsNullOrEmpty(row.HitAddress))
            ? f(row.HitAddress) : Task.CompletedTask;
}

/// <summary>One row of the references grid. Wire shape flattened into
/// view-friendly strings so the DataGrid can bind without value
/// converters.</summary>
public sealed class FieldReferenceRowVm
{
    public FieldReferenceRowVm(
        string assembly, string @namespace, string @class, string method,
        string kind, string confidence, string address, string hitAddress,
        string ilOffset, bool isMethodStatic)
    {
        Assembly       = assembly;
        Namespace      = @namespace;
        Class          = @class;
        Method         = method;
        Kind           = kind;
        Confidence     = confidence;
        Address        = address;
        HitAddress     = hitAddress;
        IlOffset       = ilOffset;
        IsMethodStatic = isMethodStatic;
    }

    public string Assembly       { get; }
    public string Namespace      { get; }
    public string Class          { get; }
    public string Method         { get; }
    /// <summary>"read" / "write".</summary>
    public string Kind           { get; }
    public string Confidence     { get; }
    /// <summary>Enclosing method's AOT entry address (IL2CPP) or empty (Mono).</summary>
    public string Address        { get; }
    /// <summary>Exact instruction IP of the matching insn (IL2CPP).</summary>
    public string HitAddress     { get; }
    /// <summary>"IL_NNNN" — byte offset of the matching IL op (Mono).</summary>
    public string IlOffset       { get; }
    public bool   IsMethodStatic { get; }

    public string ClassQualified =>
        string.IsNullOrEmpty(Namespace) ? Class : $"{Namespace}.{Class}";

    public string KindLabel =>
        IsMethodStatic ? $"{Kind} (static)" : Kind;
}
