// MethodOverloadsViewModel.cs — Phase 18c.
//
// Drives the MethodOverloadsWindow modal. Wraps `findMethodInstances` +
// `getMethodAobPattern` and produces:
//   * a multi-select grid of overloads with native addresses + signatures
//   * a "Copy AOB AA script" action that pre-fetches the pattern for each
//     selected overload, hands the bag to AobAaScriptGenerator, then writes
//     the resulting script string to the clipboard
//   * a "Copy registerSymbol AA script" alternative for users who prefer
//     the runtime-injection path (matches the user's existing
//     mono_registerSymbol* convention)
//
// AOT-safe: CommunityToolkit.Mvvm source-gen, no reflection.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

public sealed partial class MethodOverloadsViewModel : ObservableObject
{
    private readonly BackendAdapter    _backend;
    private readonly FileLogger        _logger;
    private readonly IAobMakerBridge?  _aobMaker;
    private CancellationTokenSource? _refreshCts;

    public MethodOverloadsViewModel(BackendAdapter backend, FileLogger logger,
                                    string assembly, string @namespace, string className,
                                    string methodName,
                                    IAobMakerBridge? aobMaker = null)
    {
        _backend     = backend;
        _logger      = logger;
        _aobMaker    = aobMaker;
        Assembly     = assembly;
        Namespace    = @namespace;
        ClassName    = className;
        MethodName   = methodName;

        TitleLabel = string.IsNullOrEmpty(@namespace)
            ? $"{className}.{methodName}"
            : $"{@namespace}.{className}.{methodName}";

        // Surface "Create AA in CE via AOBMaker" only when the bridge is
        // wired. When AOBMaker / CE aren't running the call is fail-soft,
        // but the button stays hidden so the user isn't tempted to click
        // it expecting magic.
        HasAobMakerBridge = aobMaker is not null;
    }

    public string Assembly   { get; }
    public string Namespace  { get; }
    public string ClassName  { get; }
    public string MethodName { get; }
    public string TitleLabel { get; }
    public bool   HasAobMakerBridge { get; }

    /// <summary>
    /// View-supplied clipboard write — wired by the Window code-behind.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    public ObservableCollection<MethodOverloadRow> Overloads { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAobScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyRegisterSymbolScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAddressesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateAaScriptInCeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Optional substring filter passed to `findMethodInstances`.
    /// Empty = return every overload.
    /// </summary>
    [ObservableProperty]
    private string _signatureFilter = string.Empty;

    /// <summary>AOB-pattern length; default 32, capped at 128 by the DLL.</summary>
    [ObservableProperty]
    private int _aobLength = 32;

    [RelayCommand(CanExecute = nameof(CanRunCommands))]
    public async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        IsBusy        = true;
        ErrorMessage  = null;
        StatusMessage = "Searching overloads…";
        UnsubscribeRowEvents();
        Overloads.Clear();
        try
        {
            var result = await _backend.FindMethodInstancesAsync(
                Assembly, Namespace, ClassName, MethodName,
                string.IsNullOrWhiteSpace(SignatureFilter) ? null : SignatureFilter.Trim(),
                ct).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = result.Error;
                return;
            }
            foreach (var m in result.Matches)
            {
                var row = new MethodOverloadRow(m);
                row.PropertyChanged += OnRowPropertyChanged;
                Overloads.Add(row);
            }
            StatusMessage = result.Count == 0
                ? "No overloads matched."
                : $"{result.Count} overload(s) found.";
            // CommandToolkit.Mvvm doesn't auto-re-evaluate CanExecute when a
            // child observable item changes — refresh selection-dependent
            // commands explicitly so a freshly-loaded grid starts in the
            // right state (button disabled until a row gets ticked).
            NotifySelectionDependentCommands();
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodOverloadsViewModel: refresh failed for {Assembly}.{ClassName}.{MethodName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UnsubscribeRowEvents()
    {
        foreach (var row in Overloads)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MethodOverloadRow.IsSelected))
        {
            NotifySelectionDependentCommands();
        }
    }

    private void NotifySelectionDependentCommands()
    {
        CopyAobScriptCommand.NotifyCanExecuteChanged();
        CopyRegisterSymbolScriptCommand.NotifyCanExecuteChanged();
        CopyAddressesCommand.NotifyCanExecuteChanged();
        CreateAaScriptInCeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Selected rows that have a real native address (compiled successfully
    /// in the Mono path, AOT pointer in IL2CPP). Abstract / generic methods
    /// without instantiation drop out here.
    /// </summary>
    private List<MethodOverloadRow> SelectedAddressable() =>
        Overloads.Where(r => r.IsSelected && r.Compiled && !string.IsNullOrEmpty(r.Address) &&
                              r.Address != "0x0000000000000000").ToList();

    private bool CanRunCommands() => !IsBusy;

    /// <summary>
    /// Build one <see cref="AaScriptOverload"/> per selected row, fetching
    /// each one's AOB pattern via <c>getMethodAobPattern</c>. Returns null
    /// on failure (status / error message already set on the VM).
    /// </summary>
    private async Task<List<AaScriptOverload>?> BuildScriptBagAsync(
        IReadOnlyList<MethodOverloadRow> picks, int aobLength)
    {
        int len = Math.Clamp(aobLength, 8, 128);
        var bag = new List<AaScriptOverload>(picks.Count);
        foreach (var row in picks)
        {
            var patt = await _backend.GetMethodAobPatternAsync(row.Address!, len).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(patt.Error))
            {
                StatusMessage = $"AOB scan failed for {row.SignatureLabel}: {patt.Error}";
                return null;
            }
            bag.Add(new AaScriptOverload
            {
                MethodName       = MethodName,
                ClassName        = ClassName,
                Namespace        = Namespace,
                HumanSuffix      = row.HumanSuffix ?? "T",
                Signature        = row.SignatureLabel,
                Pattern          = patt.Pattern ?? string.Empty,
                ModuleName       = patt.ModuleName ?? "GameAssembly.dll",
                Address          = patt.Address ?? row.Address!,
                ModuleOffset     = patt.ModuleOffset ?? "0x0",
                ReturnType       = row.ReturnType,
                DecoderBailed    = patt.DecoderBailed,
                InstructionCount = patt.Stats?.Instructions ?? 0,
                WildcardCount    = patt.Stats?.Wildcards ?? 0,
            });
        }
        return bag;
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectionCmd))]
    public async Task CopyAobScriptAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0)
        {
            StatusMessage = "Select at least one overload with a native address.";
            return;
        }
        if (CopyToClipboardAsync is null) return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Generating AOB pattern for {picks.Count} overload(s)…";
        try
        {
            var bag = await BuildScriptBagAsync(picks, AobLength).ConfigureAwait(true);
            if (bag is null) return;
            string script = AobAaScriptGenerator.Build(bag);
            await CopyToClipboardAsync(script).ConfigureAwait(true);
            StatusMessage = $"Copied AOB AA script for {bag.Count} overload(s) to clipboard.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodOverloadsViewModel: AOB script generation failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Push the generated AOB AA script straight into CE's address list as
    /// a new memory record (vtAutoAssembler) via the AOBMaker bridge. Skip
    /// the clipboard round-trip — UE5CEDumper-style. Optional
    /// <c>autoActivate</c> flips the cheat entry's checkbox on after
    /// creation so the AOB scan + symbol register fires immediately.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunCreateInCe))]
    public async Task CreateAaScriptInCeAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0 || _aobMaker is null)
        {
            StatusMessage = "Select at least one overload with a native address.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Pushing AOB script for {picks.Count} overload(s) to CE…";
        try
        {
            var bag = await BuildScriptBagAsync(picks, AobLength).ConfigureAwait(true);
            if (bag is null) return;
            string script      = AobAaScriptGenerator.Build(bag);
            string description = picks.Count == 1
                ? $"{ClassName}.{MethodName} {bag[0].HumanSuffix}"
                : $"{ClassName}.{MethodName}  ({picks.Count} overloads)";

            bool ok = await _aobMaker.CreateAAScriptAsync(description, script,
                                                          autoActivate: AutoActivateInCe)
                                     .ConfigureAwait(true);
            StatusMessage = ok
                ? $"Created '{description}' in CE address list ({bag.Count} overload(s))."
                : "AOBMaker pipe unavailable — start CE with the AOBMaker plugin loaded, then retry.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodOverloadsViewModel: AOBMaker CreateAAScript failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>If true, the CE-side memory record's checkbox is flipped on
    /// after creation so AOB scan + registersymbol run immediately.</summary>
    [ObservableProperty]
    private bool _autoActivateInCe = true;

    private bool CanRunSelectionCmd() =>
        !IsBusy && CopyToClipboardAsync is not null && Overloads.Any(r => r.IsSelected);

    private bool CanRunCreateInCe() =>
        !IsBusy && _aobMaker is not null && Overloads.Any(r => r.IsSelected);

    /// <summary>
    /// Drop a runtime-injection style script that registers each selected
    /// overload as a CE symbol pointing at the JIT/AOT address as we
    /// resolved it now. Less version-stable than the AOB script (the
    /// address is baked in) but useful when the user wants to verify the
    /// finder output without dealing with AOB scan timing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunSelectionCmd))]
    public async Task CopyRegisterSymbolScriptAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0)
        {
            StatusMessage = "Select at least one overload with a native address.";
            return;
        }
        if (CopyToClipboardAsync is null) return;

        var sb = new StringBuilder(512);
        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine("-- discrete: hardcoded address registration. Re-run after game relaunch.");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbols = new List<string>();
        foreach (var row in picks)
        {
            var ov = new AaScriptOverload
            {
                MethodName    = MethodName,
                ClassName     = ClassName,
                Namespace     = Namespace,
                HumanSuffix   = row.HumanSuffix ?? "T",
                Signature     = row.SignatureLabel,
                Pattern       = string.Empty,
                ModuleName    = string.Empty,
                Address       = row.Address!,
                ModuleOffset  = string.Empty,
                ReturnType    = row.ReturnType,
                DecoderBailed = false,
            };
            string sym = AobAaScriptGenerator.MakeSymbolName(ov, taken);
            taken.Add(sym);
            symbols.Add(sym);
            sb.Append("registerSymbol(\"").Append(sym).Append("\", 0x")
              .Append(ParseAddrAsHex(row.Address)).Append(")  -- ")
              .Append(ClassName).Append('.').Append(MethodName).AppendLine(row.SignatureLabel);
        }
        sb.AppendLine();
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        foreach (var sym in symbols)
        {
            sb.Append("unregisterSymbol(\"").Append(sym).AppendLine("\")");
        }

        try
        {
            await CopyToClipboardAsync(sb.ToString()).ConfigureAwait(true);
            StatusMessage = $"Copied registerSymbol script for {symbols.Count} overload(s) to clipboard.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Fallback: copy a newline-separated list of addresses for the
    /// selected overloads. Useful when the user just wants to paste them
    /// into a CE manual-add dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunSelectionCmd))]
    public async Task CopyAddressesAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0 || CopyToClipboardAsync is null) return;
        string text = string.Join(Environment.NewLine,
            picks.Select(r => $"{r.Address}  // {ClassName}.{MethodName}{r.SignatureLabel}"));
        await CopyToClipboardAsync(text).ConfigureAwait(true);
        StatusMessage = $"Copied {picks.Count} address(es) to clipboard.";
    }

    private static string ParseAddrAsHex(string? addr)
    {
        if (string.IsNullOrEmpty(addr)) return "0";
        var s = addr.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("0X", StringComparison.Ordinal))
            return s.Substring(2);
        // Accept already-bare hex too.
        return s;
    }
}

/// <summary>
/// One overload row in the multi-select grid.
/// </summary>
public sealed partial class MethodOverloadRow : ObservableObject
{
    public MethodOverloadRow(MethodOverloadEntry e)
    {
        Name              = e.Name;
        Address           = e.Address;
        ReturnType        = e.ReturnType;
        Signature         = e.Signature ?? "()";
        HumanSuffix       = e.HumanSuffix;
        IsStatic          = e.IsStatic;
        IsAbstract        = e.IsAbstract;
        Compiled          = e.Compiled;
        Token             = e.Token;
        ParamTypes        = e.ParamTypes;
        ParamNames        = e.ParamNames;
        DeclaringClass    = e.DeclaringClass;
        DeclaringNamespace = e.DeclaringNamespace;

        SignatureLabel = e.Signature ?? "()";
        ParamSummary   = BuildParamSummary(e.ParamTypes, e.ParamNames);
        ModifiersLabel = BuildModifiers(e);
    }

    public string  Name             { get; }
    public string? Address          { get; }
    public string? ReturnType       { get; }
    public string  Signature        { get; }
    public string? HumanSuffix      { get; }
    public bool    IsStatic         { get; }
    public bool    IsAbstract       { get; }
    public bool    Compiled         { get; }
    public uint    Token            { get; }
    public IList<string> ParamTypes { get; }
    public IList<string> ParamNames { get; }
    public string? DeclaringClass    { get; }
    public string? DeclaringNamespace { get; }

    public string SignatureLabel    { get; }
    public string ParamSummary      { get; }
    public string ModifiersLabel    { get; }
    public string TokenHex          => $"0x{Token:X8}";

    [ObservableProperty]
    private bool _isSelected;

    private static string BuildParamSummary(IList<string> types, IList<string> names)
    {
        if (types.Count == 0) return "(no parameters)";
        var sb = new StringBuilder(64);
        for (int i = 0; i < types.Count; ++i)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(types[i]);
            if (i < names.Count && !string.IsNullOrEmpty(names[i]))
            {
                sb.Append(' ').Append(names[i]);
            }
        }
        return sb.ToString();
    }

    private static string BuildModifiers(MethodOverloadEntry e)
    {
        var parts = new List<string>(3);
        if (e.IsStatic)        parts.Add("static");
        if (e.IsAbstract)      parts.Add("abstract");
        else if (e.IsVirtual)  parts.Add("virtual");
        if (!e.Compiled)       parts.Add("UNCOMPILED");
        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }
}
