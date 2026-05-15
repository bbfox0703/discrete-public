// MethodCallViewModel.cs — Phase 19d.
//
// Drives the MethodCallWindow modal. Wraps `callMethod` with one input row
// per declared parameter (typed by formal parameter type), validates user
// input client-side before sending, and renders the typed return value /
// captured managed exception / protocol error in a result panel.
//
// AOT-safe: CommunityToolkit.Mvvm source-gen, no reflection. Param-value
// JsonElement payloads are built via CallArgFactory which wraps the
// source-gen ProtocolJsonContext entries (kept internal to Core).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

public sealed partial class MethodCallViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _callCts;

    public MethodCallViewModel(BackendAdapter backend, FileLogger logger,
                               string assembly, string @namespace, string className,
                               string methodName,
                               string signatureMatch,
                               string signatureDisplay,
                               IReadOnlyList<string> paramTypes,
                               IReadOnlyList<string> paramNames,
                               string returnType,
                               bool   isStatic,
                               string? defaultInstanceAddress = null)
    {
        _backend         = backend;
        _logger          = logger;
        Assembly         = assembly;
        Namespace        = @namespace;
        ClassName        = className;
        MethodName       = methodName;
        SignatureMatch   = signatureMatch;
        SignatureDisplay = signatureDisplay;
        ReturnType       = returnType;
        IsStatic         = isStatic;
        InstanceAddress  = isStatic ? string.Empty : (defaultInstanceAddress ?? string.Empty);

        ClassDisplay = string.IsNullOrEmpty(@namespace)
            ? className
            : $"{@namespace}.{className}";
        MethodDisplay = $"{methodName}{signatureDisplay} -> {returnType}";

        for (int i = 0; i < paramTypes.Count; ++i)
        {
            string name = i < paramNames.Count ? paramNames[i] : string.Empty;
            Params.Add(new MethodCallParamRow(paramTypes[i], name));
        }
    }

    public string Assembly         { get; }
    public string Namespace        { get; }
    public string ClassName        { get; }
    public string MethodName       { get; }
    public string SignatureMatch   { get; }
    public string SignatureDisplay { get; }
    public string ReturnType       { get; }
    public bool   IsStatic         { get; }
    public string ClassDisplay     { get; }
    public string MethodDisplay    { get; }

    public ObservableCollection<MethodCallParamRow> Params { get; } = new();

    public bool HasParams   => Params.Count > 0;
    public bool HasNoParams => Params.Count == 0;

    /// <summary>
    /// Phase 22e — optional AOBMaker bridge. Wired post-construction
    /// from the spawning site (MethodOverloadsWindow / MainWindow code-
    /// behind) so the existing 2 ctor call sites don't need to change
    /// signature. Bridge no-ops cleanly when AOBMaker / CE aren't running.
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

    /// <summary>
    /// True once <see cref="AobMaker"/> has been wired. The Send-to-CE
    /// button binds Visibility to this so the column header doesn't
    /// flicker on call sites that don't pass a bridge.
    /// </summary>
    public bool HasAobMaker => _aobMaker is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToCeTableCommand))]
    private bool _isBusy;

    /// <summary>
    /// Optional managed-object pointer for instance dispatch. Hex string
    /// (`0x...`). Empty / null means "static call" — the DLL silently
    /// ignores the field on a static method, so the user can leave it
    /// blank either way.
    /// </summary>
    [ObservableProperty]
    private string _instanceAddress = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Result panel state. Shown only after a call lands.
    /// </summary>
    [ObservableProperty] private bool    _hasResult;
    [ObservableProperty] private bool    _resultOk;
    [ObservableProperty] private string? _resultReturnType;
    [ObservableProperty] private string? _resultReturnValue;
    [ObservableProperty] private string? _resultException;
    [ObservableProperty] private string? _resultError;
    [ObservableProperty] private string? _resultNote;
    [ObservableProperty] private string? _resultElapsedLabel;

    [RelayCommand(CanExecute = nameof(CanCall))]
    public async Task CallAsync()
    {
        _callCts?.Cancel();
        _callCts = new CancellationTokenSource();
        var ct = _callCts.Token;

        // Reset result state up front so the panel doesn't show stale values
        // while the new call is in flight.
        HasResult         = false;
        ResultOk          = false;
        ResultReturnType  = null;
        ResultReturnValue = null;
        ResultException   = null;
        ResultError       = null;
        ResultNote        = null;
        ResultElapsedLabel = null;

        // Build the typed args list from the param rows. Per-row validation
        // surfaces a ResultError if any input fails to parse — we never ship
        // a half-built request to the DLL.
        var argsList = new List<CallArgRequest>(Params.Count);
        foreach (var p in Params)
        {
            if (!TryBuildArg(p, out var arg, out string err))
            {
                IsBusy      = false;
                HasResult   = true;
                ResultError = $"arg '{p.Name}' ({p.TypeName}): {err}";
                StatusMessage = "Argument parse failed.";
                return;
            }
            argsList.Add(arg);
        }

        IsBusy        = true;
        StatusMessage = "Calling…";
        try
        {
            string? instance = string.IsNullOrWhiteSpace(InstanceAddress)
                ? null
                : InstanceAddress.Trim();

            var result = await _backend.CallMethodAsync(
                Assembly, Namespace, ClassName, MethodName,
                argsList,
                signatureMatch: string.IsNullOrEmpty(SignatureMatch) ? null : SignatureMatch,
                instanceAddress: instance,
                ct).ConfigureAwait(true);

            HasResult         = true;
            ResultReturnType  = result.ReturnType;
            ResultElapsedLabel = $"{result.ElapsedMs} ms";
            ResultNote        = result.Note;

            if (!string.IsNullOrEmpty(result.Error))
            {
                ResultOk      = false;
                ResultError   = result.Error;
                StatusMessage = $"Error: {result.Error}";
            }
            else if (!string.IsNullOrEmpty(result.Exception))
            {
                ResultOk         = false;
                ResultException  = result.Exception;
                ResultReturnValue = FormatJsonValue(result.ReturnValue);
                StatusMessage    = "Managed exception caught.";
            }
            else
            {
                ResultOk          = result.Ok;
                ResultReturnValue = FormatJsonValue(result.ReturnValue);
                StatusMessage     = result.Ok ? "OK." : "Call failed.";
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            HasResult     = true;
            ResultError   = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Pipe / dispatch error.";
            _logger.Write(Constants.LogCategoryView,
                $"MethodCallViewModel: callMethod failed for {ClassDisplay}.{MethodName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCall() => !IsBusy;

    /// <summary>
    /// Phase 22e — push the current MethodCallWindow inputs to CE's
    /// address list as an AA-script entry. End-user toggles the entry's
    /// checkbox to repeat the call. Generates the script via
    /// <see cref="CallMethodAaScriptGenerator"/> (which prepends the
    /// _G.discrete_bridgeLoaded guard so the entry abort cleanly when
    /// the user hasn't loaded the Phase 22c bootstrap yet).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendToCeTable))]
    private async Task SendToCeTableAsync()
    {
        if (_aobMaker is null) return;

        // Snapshot the current row inputs into the generator's request
        // shape. We deliberately don't validate (e.g. via TryBuildArg)
        // — letting CE see what the user typed surfaces parse errors
        // in CE's lua console, which is the right place for them: the
        // "Send" path is for repeat-this-call workflows where the
        // user has typically already proven the inputs work via the
        // Call button.
        var args = new List<CallMethodArg>(Params.Count);
        foreach (var p in Params)
        {
            args.Add(new CallMethodArg
            {
                TypeName  = p.TypeName,
                ValueText = p.ValueText ?? string.Empty,
                IsNull    = p.IsNull,
            });
        }

        string? instance = IsStatic || string.IsNullOrWhiteSpace(InstanceAddress)
            ? null
            : InstanceAddress.Trim();
        string? sigMatch = string.IsNullOrEmpty(SignatureMatch) ? null : SignatureMatch;

        IsBusy = true;
        StatusMessage = "Sending to CE…";
        try
        {
            string script = CallMethodAaScriptGenerator.Build(
                Assembly, Namespace, ClassName, MethodName,
                signatureMatch:  sigMatch,
                instanceAddress: instance,
                args:            args);
            string desc = CallMethodAaScriptGenerator.SuggestDescription(
                ClassName, MethodName, sigMatch);

            bool ok = await _aobMaker.CreateAAScriptAsync(desc, script,
                                                          autoActivate: false)
                                     .ConfigureAwait(true);
            StatusMessage = ok
                ? $"Created '{desc}' in CE — toggle the entry to repeat the call."
                : "AOBMaker pipe unavailable — open CE with the AOBMaker plugin loaded, then retry.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Send to CE failed: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodCallViewModel: SendToCeTable failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSendToCeTable() => !IsBusy && _aobMaker is not null;

    /// <summary>
    /// Convert a single param row's user input into a typed
    /// <see cref="CallArgRequest"/>. Returns false on parse failure with the
    /// reason in <paramref name="err"/>. The wire `type` tags map exactly
    /// to the formal parameter type's CLR name; for primitive integers the
    /// short alias (`int32`, `float`, etc.) keeps the wire compact.
    /// Construction goes through <see cref="CallArgFactory"/> so the AOT
    /// source-gen JsonContext stays internal to Core.
    /// </summary>
    private static bool TryBuildArg(MethodCallParamRow p, out CallArgRequest arg, out string err)
    {
        arg = new CallArgRequest();
        err = string.Empty;

        if (p.IsNull)
        {
            arg = CallArgFactory.Null();
            return true;
        }

        string raw = p.ValueText ?? string.Empty;

        switch (p.TypeName)
        {
        case "System.Boolean":
            if (!bool.TryParse(raw.Trim(), out bool bv))
            {
                err = "expected 'true' or 'false'";
                return false;
            }
            arg = CallArgFactory.Bool(bv);
            return true;

        case "System.Byte":   return TryUInt(raw, "byte",   byte.MaxValue,   out arg, out err);
        case "System.UInt16": return TryUInt(raw, "uint16", ushort.MaxValue, out arg, out err);
        case "System.UInt32": return TryUInt(raw, "uint32", uint.MaxValue,   out arg, out err);
        case "System.UInt64": return TryUInt(raw, "uint64", ulong.MaxValue,  out arg, out err);
        case "System.Char":   return TryChar(raw, out arg, out err);

        case "System.SByte":  return TryInt(raw, "sbyte", sbyte.MinValue, sbyte.MaxValue, out arg, out err);
        case "System.Int16":  return TryInt(raw, "int16", short.MinValue, short.MaxValue, out arg, out err);
        case "System.Int32":  return TryInt(raw, "int32", int.MinValue,   int.MaxValue,   out arg, out err);
        case "System.Int64":
            if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                               out long lv))
            {
                err = "expected integer";
                return false;
            }
            arg = CallArgFactory.Int("int64", lv);
            return true;

        case "System.Single":
        case "System.Double":
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out double dv))
            {
                err = "expected floating-point number";
                return false;
            }
            arg = CallArgFactory.Float(p.TypeName == "System.Single" ? "float" : "double", dv);
            return true;

        case "System.String":
            arg = CallArgFactory.String(raw);
            return true;

        case "System.IntPtr":
        case "System.UIntPtr":
            return TryAddress(raw, out arg, out err);
        }

        // Unknown / non-primitive formal type — accept hex address. This
        // covers reference-type params like `MyClass`, `string[]`, etc. —
        // the user typically gets the address from findInstances and
        // pastes it here. Empty input becomes a null reference.
        if (string.IsNullOrWhiteSpace(raw))
        {
            arg = CallArgFactory.Null();
            return true;
        }
        return TryAddress(raw, out arg, out err);
    }

    private static bool TryInt(string raw, string typeTag, long min, long max,
                               out CallArgRequest arg, out string err)
    {
        arg = new CallArgRequest();
        err = string.Empty;
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                           out long v))
        {
            err = "expected integer";
            return false;
        }
        if (v < min || v > max)
        {
            err = $"out of range [{min}, {max}]";
            return false;
        }
        arg = CallArgFactory.Int(typeTag, v);
        return true;
    }

    private static bool TryUInt(string raw, string typeTag, ulong max,
                                out CallArgRequest arg, out string err)
    {
        arg = new CallArgRequest();
        err = string.Empty;
        if (!ulong.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out ulong v))
        {
            err = "expected non-negative integer";
            return false;
        }
        if (v > max)
        {
            err = $"out of range [0, {max}]";
            return false;
        }
        arg = CallArgFactory.UInt(typeTag, v);
        return true;
    }

    private static bool TryChar(string raw, out CallArgRequest arg, out string err)
    {
        arg = new CallArgRequest();
        err = string.Empty;
        string s = raw ?? string.Empty;
        if (s.Length == 1)
        {
            arg = CallArgFactory.UInt("char", s[0]);
            return true;
        }
        // Accept a numeric code-unit too.
        if (ulong.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) &&
            v <= ushort.MaxValue)
        {
            arg = CallArgFactory.UInt("char", v);
            return true;
        }
        err = "expected single character or UTF-16 code unit";
        return false;
    }

    private static bool TryAddress(string raw, out CallArgRequest arg, out string err)
    {
        arg = new CallArgRequest();
        err = string.Empty;
        string s = (raw ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            err = "expected hex address (0x...)";
            return false;
        }
        // Accept "0x..." or bare hex. The DLL's parseAddr tolerates both.
        arg = CallArgFactory.Address(s);
        return true;
    }

    /// <summary>
    /// Render the wire <c>returnValue</c> (a polymorphic JsonElement) into a
    /// string for the result panel. Caller already has the typed
    /// <c>returnType</c> available — this is purely display.
    /// </summary>
    private static string FormatJsonValue(JsonElement v)
    {
        return v.ValueKind switch
        {
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Null      => "(null)",
            JsonValueKind.String    => v.GetString() ?? string.Empty,
            JsonValueKind.Number    => v.GetRawText(),
            JsonValueKind.True      => "true",
            JsonValueKind.False     => "false",
            _                       => v.GetRawText(),
        };
    }
}

/// <summary>
/// One parameter input row in the call-method dialog.
/// </summary>
public sealed partial class MethodCallParamRow : ObservableObject
{
    public MethodCallParamRow(string typeName, string name)
    {
        TypeName    = typeName;
        Name        = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        IsReference = !IsValueType(typeName);
        ValueText   = DefaultValueFor(typeName);
    }

    public string TypeName    { get; }
    public string Name        { get; }
    /// <summary>True for reference-type params — the "null" checkbox is enabled.</summary>
    public bool   IsReference { get; }

    [ObservableProperty]
    private string _valueText = string.Empty;

    /// <summary>
    /// "Pass null" checkbox state. Only enabled for reference-type params
    /// (and IntPtr / UIntPtr, which accept a 0-valued address). When checked
    /// the <see cref="ValueText"/> input is dimmed and ignored on Call.
    /// </summary>
    [ObservableProperty]
    private bool _isNull;

    private static bool IsValueType(string typeName) => typeName switch
    {
        "System.Boolean" or "System.Byte"   or "System.SByte"  or
        "System.Int16"   or "System.UInt16" or "System.Char"   or
        "System.Int32"   or "System.UInt32" or
        "System.Int64"   or "System.UInt64" or
        "System.Single"  or "System.Double" or
        "System.IntPtr"  or "System.UIntPtr" => true,
        _ => false,
    };

    private static string DefaultValueFor(string typeName) => typeName switch
    {
        "System.Boolean" => "false",
        "System.Single" or "System.Double" => "0.0",
        "System.String" => string.Empty,
        "System.IntPtr" or "System.UIntPtr" => "0x0",
        _ when IsValueType(typeName) => "0",
        _ => string.Empty,
    };
}
