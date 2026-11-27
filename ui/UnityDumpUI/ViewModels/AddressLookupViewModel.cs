// AddressLookupViewModel.cs — Phase 17c.
//
// Drives the *Address Lookup* tab on MainWindow. Wraps the BackendAdapter's
// `whatIsAt` RPC and surfaces the resolved instance + class + declared field
// in a UI-friendly shape. Shares the AOT mandate with every other VM —
// CommunityToolkit.Mvvm source-gen properties + commands, no reflection.
//
// Cross-tab handoff: when the result includes a class spec the user can
// click "Inspect this instance" — the VM raises an event the MainWindow
// hooks to switch tabs + push the address into LiveInspectionViewModel.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

public sealed partial class AddressLookupViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _lookupCts;

    public AddressLookupViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
    }

    /// <summary>
    /// Address text the user typed (or had pre-filled from the Inspector
    /// right-click). Accepts hex with or without the <c>0x</c> prefix and
    /// plain decimal — matches the DLL's <c>parseAddr</c> behaviour.
    /// </summary>
    [ObservableProperty]
    private string _addressInput = string.Empty;

    /// <summary>
    /// User-tunable backward-scan window. 4 KB matches the DLL default and
    /// covers every Unity class layout we've seen; raise to 16 KB if a
    /// known-large class is missing matches. Capped at 16 KB by the DLL.
    /// </summary>
    [ObservableProperty]
    private int _maxBackwardScan = 4096;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToCeTableCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Most recent <see cref="WhatIsAtResult"/>. Null until first lookup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasField))]
    [NotifyPropertyChangedFor(nameof(HasNearestField))]
    [NotifyPropertyChangedFor(nameof(InstanceLabel))]
    [NotifyPropertyChangedFor(nameof(ClassLabel))]
    [NotifyPropertyChangedFor(nameof(FieldOffsetLabel))]
    [NotifyPropertyChangedFor(nameof(FieldLabel))]
    [NotifyPropertyChangedFor(nameof(NearestFieldLabel))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(ScannedLabel))]
    [NotifyCanExecuteChangedFor(nameof(InspectInstanceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyInstanceAddrCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToCeTableCommand))]
    private WhatIsAtResult? _result;

    public bool HasResult        => Result is { Found: true };
    public bool HasField         => Result?.Field        is not null;
    public bool HasNearestField  => Result is { Found: true, Field: null, NearestField: not null };

    public string InstanceLabel    => Result?.Instance ?? string.Empty;
    public string ClassLabel
    {
        get
        {
            var r = Result;
            if (r is null || !r.Found) return string.Empty;
            string ns = r.Namespace ?? string.Empty;
            string cls = r.Class ?? string.Empty;
            return string.IsNullOrEmpty(ns) ? cls : $"{ns}.{cls}";
        }
    }
    public string FieldOffsetLabel
    {
        get
        {
            var r = Result;
            if (r is null || !r.Found) return string.Empty;
            return $"+0x{r.FieldOffset:X} ({r.FieldOffset} bytes from instance base)";
        }
    }

    public string FieldLabel
    {
        get
        {
            var f = Result?.Field;
            if (f is null) return string.Empty;
            string declared = string.IsNullOrEmpty(f.DeclaredIn) ? string.Empty : $" — declared in {f.DeclaredIn}";
            return $"{f.Name}  @+0x{f.Offset:X}  ({f.Type}, {f.Size} byte{(f.Size == 1 ? "" : "s")}){declared}";
        }
    }

    public string NearestFieldLabel
    {
        get
        {
            var r = Result;
            if (r?.NearestField is not { } nf) return string.Empty;
            long delta = r.NearestFieldDelta ?? 0;
            return $"closest field below offset: {nf.Name} @+0x{nf.Offset:X} ({nf.Type}, {nf.Size} bytes) — delta {delta} bytes";
        }
    }

    public string KindLabel
    {
        get
        {
            var f = Result?.Field;
            if (f is null) return string.Empty;
            string k = f.Kind ?? string.Empty;
            if (k == "interior" && f.InteriorOffset is int io)
                return $"kind: interior  (offset +0x{io:X} within field)";
            if (f.IsEnum && !string.IsNullOrEmpty(f.UnderlyingType))
                return $"kind: {k}  (enum, underlying {f.UnderlyingType})";
            return $"kind: {k}";
        }
    }

    public string ScannedLabel
    {
        get
        {
            var r = Result;
            if (r is null) return string.Empty;
            string range = (r.ScannedRange is { } sr && !string.IsNullOrEmpty(sr.From))
                ? $"{sr.From} .. {sr.To}"
                : "—";
            return $"scanned {r.ScannedSteps} step(s) in [{range}]";
        }
    }

    /// <summary>
    /// Pre-fill the address input + run a lookup. Called by the Inspector
    /// right-click "Find what's at this address" path.
    /// </summary>
    public Task PrefillAndLookupAsync(string address)
    {
        AddressInput = address;
        return LookupAsync();
    }

    [RelayCommand(CanExecute = nameof(CanLookup))]
    private async Task LookupAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressInput)) return;
        if (!_backend.IsConnected)
        {
            ErrorMessage = "Not connected — connect to a running game first.";
            return;
        }

        _lookupCts?.Cancel();
        _lookupCts = new CancellationTokenSource();
        var ct = _lookupCts.Token;

        IsBusy       = true;
        ErrorMessage = null;
        try
        {
            int? scan = MaxBackwardScan > 0 ? MaxBackwardScan : null;
            WhatIsAtResult r = await _backend
                .WhatIsAtAsync(AddressInput.Trim(), scan, ct)
                .ConfigureAwait(true);
            Result = r;
            if (!r.Found && string.IsNullOrEmpty(r.Reason))
                ErrorMessage = "No managed instance located in the scanned window.";
        }
        catch (OperationCanceledException)
        {
            // superseded — ignore
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"AddressLookupViewModel: lookup failed for '{AddressInput}': {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLookup() => !IsBusy;

    [RelayCommand]
    private void Clear()
    {
        AddressInput = string.Empty;
        Result       = null;
        ErrorMessage = null;
    }

    /// <summary>
    /// Cross-tab handoff. View hooks this event and (a) switches the
    /// TabControl to the Inspector tab, (b) hands the class spec +
    /// instance address into <see cref="LiveInspectionViewModel"/>. Null
    /// when no result is loaded.
    /// </summary>
    public event EventHandler<InspectInstanceRequest>? InspectInstanceRequested;

    [RelayCommand(CanExecute = nameof(CanInspectInstance))]
    private void InspectInstance()
    {
        var r = Result;
        if (r is null || !r.Found || string.IsNullOrEmpty(r.Class) || string.IsNullOrEmpty(r.Instance))
            return;
        InspectInstanceRequested?.Invoke(this, new InspectInstanceRequest(
            assembly:  r.Assembly  ?? string.Empty,
            @namespace: r.Namespace ?? string.Empty,
            className: r.Class,
            instance:  r.Instance));
    }

    private bool CanInspectInstance() =>
        Result is { Found: true, Class.Length: > 0, Instance.Length: > 0, Assembly.Length: > 0 };

    /// <summary>
    /// View-supplied clipboard write — wired by MainWindow code-behind, same
    /// pattern as <see cref="LiveInspectionViewModel.CopyToClipboardAsync"/>.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [RelayCommand(CanExecute = nameof(CanCopyInstanceAddr))]
    private Task CopyInstanceAddrAsync() =>
        (CopyToClipboardAsync is { } f && Result?.Instance is { Length: > 0 } addr)
            ? f(addr)
            : Task.CompletedTask;

    private bool CanCopyInstanceAddr() => !string.IsNullOrEmpty(Result?.Instance);

    /// <summary>
    /// Phase 22e — optional AOBMaker bridge. Wired post-construction by
    /// MainWindowViewModel (matches the
    /// <see cref="CopyToClipboardAsync"/> pattern). Bridge no-ops cleanly
    /// when AOBMaker / CE aren't running.
    /// </summary>
    public IAobMakerBridge? AobMaker
    {
        get => _aobMaker;
        set
        {
            if (ReferenceEquals(_aobMaker, value)) return;
            _aobMaker = value;
            OnPropertyChanged(nameof(HasAobMaker));
            SendToCeTableCommand.NotifyCanExecuteChanged();
        }
    }
    private IAobMakerBridge? _aobMaker;

    public bool HasAobMaker => _aobMaker is not null;

    /// <summary>
    /// Target game pointer width in bytes (8 for x64, 4 for x86; 0 when unknown,
    /// which defaults to 8). Set by <c>MainWindowViewModel</c> from handshake so
    /// Send-to-CE emits the right String offset and pointer-width CE value types.
    /// </summary>
    public int GamePointerSize { get; set; }

    /// <summary>
    /// Phase 22e — push the current lookup result to CE's address list
    /// as a parent AA-script (registers the instance pointer as a
    /// CE symbol) plus child memory records (one per resolved field
    /// at its offset). Generated via
    /// <see cref="AddressLookupAaScriptGenerator"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendToCeTable))]
    private async Task SendToCeTableAsync()
    {
        if (_aobMaker is null) return;
        var r = Result;
        if (r is null || !r.Found || string.IsNullOrEmpty(r.Instance) || string.IsNullOrEmpty(r.Class))
        {
            ErrorMessage = "Send to CE: lookup result has no instance/class to register.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var req = new AddressLookupRequest
            {
                Instance     = r.Instance!,
                ClassName    = r.Class!,
                Namespace    = r.Namespace,
                Assembly     = r.Assembly,
                Field        = r.Field        is null ? null : ToGenField(r.Field),
                NearestField = r.NearestField is null ? null : ToGenField(r.NearestField),
                PointerSize  = GamePointerSize == 4 ? 4 : 8,
            };

            AddressLookupAaPayload payload = AddressLookupAaScriptGenerator.Build(req);
            bool ok = await _aobMaker.CreateAAScriptWithRecordsAsync(
                description:  payload.ParentDescription,
                script:       payload.ParentScript,
                records:      payload.ChildRecords,
                autoActivate: false,
                hideChildren: true).ConfigureAwait(true);

            if (!ok)
            {
                ErrorMessage = "AOBMaker pipe unavailable — open CE with the AOBMaker plugin loaded, then retry.";
            }
            else
            {
                _logger.Write(Constants.LogCategoryView,
                    $"AddressLookupViewModel: sent '{payload.ParentDescription}' to CE ({payload.ChildRecords.Count} child record(s))");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Send to CE failed: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"AddressLookupViewModel: SendToCeTable failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSendToCeTable() =>
        !IsBusy && _aobMaker is not null
        && Result is { Found: true, Class.Length: > 0, Instance.Length: > 0 };

    private static AddressLookupField ToGenField(WhatIsAtField f) => new()
    {
        Name           = f.Name,
        Type           = f.Type ?? "System.Object",
        Offset         = f.Offset,
        Size           = f.Size,
        IsEnum         = f.IsEnum,
        UnderlyingType = f.UnderlyingType,
    };
}

/// <summary>
/// Args for <see cref="AddressLookupViewModel.InspectInstanceRequested"/>.
/// </summary>
public sealed class InspectInstanceRequest : EventArgs
{
    public InspectInstanceRequest(string assembly, string @namespace, string className, string instance)
    {
        Assembly  = assembly;
        Namespace = @namespace;
        Class     = className;
        Instance  = instance;
    }
    public string Assembly  { get; }
    public string Namespace { get; }
    public string Class     { get; }
    public string Instance  { get; }
}
