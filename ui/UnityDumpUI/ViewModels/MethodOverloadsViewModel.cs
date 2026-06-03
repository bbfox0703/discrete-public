// MethodOverloadsViewModel.cs — Phase 18c.
//
// Drives the MethodOverloadsWindow modal. Wraps `findMethodInstances` +
// `getMethodAobPattern` and produces:
//   * a multi-select grid of overloads with native addresses + signatures
//   * a "Copy: aobscan" action that pre-fetches the pattern for each
//     selected overload, hands the bag to AobAaScriptGenerator, then writes
//     the resulting script string to the clipboard
//   * a "Copy: registersymbol" alternative for users who prefer the
//     runtime-injection path (fixed address baked into the AA)
//   * a "Copy: mono_register" Mono-only DLL-free path (calls
//     mono_registerSymbolEx via CE's built-in Mono API)
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
    private readonly IAobMakerAppClient? _aobMakerApp;
    private readonly bool              _experimentalEnabled;
    private CancellationTokenSource? _refreshCts;

    public MethodOverloadsViewModel(BackendAdapter backend, FileLogger logger,
                                    string assembly, string @namespace, string className,
                                    string methodName,
                                    IAobMakerBridge? aobMaker = null,
                                    string? defaultInstanceAddress = null,
                                    bool   supportsSignatureMatch = false,
                                    IAobMakerAppClient? aobMakerApp = null,
                                    bool   experimentalEnabled = false)
    {
        _backend     = backend;
        _logger      = logger;
        _aobMaker    = aobMaker;
        _aobMakerApp = aobMakerApp;
        _experimentalEnabled = experimentalEnabled;
        Assembly     = assembly;
        Namespace    = @namespace;
        ClassName    = className;
        MethodName   = methodName;
        DefaultInstanceAddress = defaultInstanceAddress;
        SupportsSignatureMatch = supportsSignatureMatch;

        TitleLabel = string.IsNullOrEmpty(@namespace)
            ? $"{className}.{methodName}"
            : $"{@namespace}.{className}.{methodName}";

        // Surface "Push aobscan to CE" only when the AOBMaker bridge is
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
    /// True for any connected Unity target (Mono OR IL2CPP). Gates the
    /// signature-match register action (and its AOBMaker-push sibling). CE's
    /// "Active Mono features" dissector drives the <c>mono_*</c> Lua API on BOTH
    /// backends, so signature-match registration is universal — it is NOT
    /// Mono-only. Wired from <c>MainWindowViewModel.Backend2</c> at VM
    /// construction; stays false on disconnect / unknown backend.
    /// </summary>
    public bool   SupportsSignatureMatch { get; }

    /// <summary>
    /// True when both the AOBMaker bridge is wired AND the target is a Unity
    /// game. Used by the signature-match "Create in CE" button — needs both to
    /// make sense (push the script to CE via AOBMaker, register by signature on
    /// enable).
    /// </summary>
    public bool   ShowSignatureCreateInCe => SupportsSignatureMatch && HasAobMakerBridge;

    /// <summary>
    /// Gates the "Copy: register (sig-match)" clipboard button. The sig-match
    /// register path is still considered experimental, so it surfaces only when
    /// the user has opted into experimental features (footer checkbox). Its
    /// "Push register to CE ★" sibling stays gated on
    /// <see cref="ShowSignatureCreateInCe"/> as before — only the Copy button
    /// is hidden by default.
    /// </summary>
    public bool   ShowSignatureMatchCopy => SupportsSignatureMatch && _experimentalEnabled;

    /// <summary>
    /// Phase 22e — exposed so the spawning window (MethodOverloadsWindow)
    /// can thread the bridge through to a child MethodCallWindow. Same
    /// instance the local Create-AA command uses; null when the parent
    /// chose not to provide one (then SendToCeTable on the child stays
    /// hidden too).
    /// </summary>
    public IAobMakerBridge? AobMaker => _aobMaker;

    /// <summary>
    /// Phase 19d-2 — sticky `this` address piped through from the caller
    /// (typically the Inspector's currently-selected instance, when its
    /// target class matches the one this overloads window is for). Threaded
    /// into <see cref="MethodCallViewModel"/> as the default
    /// <c>InstanceAddress</c> so the user doesn't have to paste it by hand.
    /// Null = no default (static methods don't need it; non-static methods
    /// will show an empty Instance address textbox the user must fill in).
    /// </summary>
    public string? DefaultInstanceAddress { get; }

    /// <summary>
    /// View-supplied clipboard write — wired by the Window code-behind.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>
    /// View-supplied "Call method dialog opener" — wired by the Window
    /// code-behind. Takes the row whose context menu was clicked, opens
    /// the modal MethodCallWindow seeded from that row's signature /
    /// param types / static-ness. The VM stays AOT-clean by handing out
    /// the typed view-model instead of opening windows itself.
    /// </summary>
    public Func<MethodOverloadRow, Task>? OpenCallMethodDialogAsync { get; set; }

    /// <summary>
    /// Right-click context-menu command on each overload row → opens the
    /// MethodCallWindow seeded from this row.
    /// </summary>
    [RelayCommand]
    public async Task CallMethodAsync(MethodOverloadRow? row)
    {
        if (row is null || OpenCallMethodDialogAsync is null) return;
        try { await OpenCallMethodDialogAsync(row); }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodOverloadsViewModel: opening call dialog for {row.Name}{row.SignatureLabel} failed: {ex.Message}");
        }
    }

    public ObservableCollection<MethodOverloadRow> Overloads { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAobScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyRegisterSymbolScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAddressesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateAaScriptInCeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyMonoSymbolScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMonoSymbolInCeCommand))]
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
            // Single-overload fast-path: tick the only row so the bottom-bar
            // commands enable without a manual click. Mirrors the Phase 19d-2
            // auto-skip in the "Call method…" Methods-grid context menu, just
            // here we open the picker anyway (this entry point is "Find
            // overloads…", which surfaces the Copy/Push buttons). Multi-row
            // results keep the legacy "user must tick" gating.
            if (Overloads.Count == 1)
                Overloads[0].IsSelected = true;
            StatusMessage = result.Count == 0
                ? "No overloads matched."
                : Overloads.Count == 1
                    ? "1 overload found — pre-selected. Use the buttons below or right-click → Call method…"
                    : $"{result.Count} overload(s) found.  Tick rows to enable the buttons below, or right-click → Call method…";
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
        CopyMonoSymbolScriptCommand.NotifyCanExecuteChanged();
        CreateMonoSymbolInCeCommand.NotifyCanExecuteChanged();
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
        IReadOnlyList<MethodOverloadRow> picks, int aobLength, bool preferUniqueAob = false)
    {
        int len = Math.Clamp(aobLength, 8, 128);
        // Every param-type token across ALL overloads of this method (not just
        // the picked ones) — drives per-overload sigExcludes for the Mono
        // signature-match register path, so a 1-param overload's signature
        // match can exclude a longer-arity sibling.
        var allParamTokens = Overloads
            .Where(o => o.ParamTypes is { Count: > 0 })
            .SelectMany(o => o.ParamTypes!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // AOBMaker-app unique-AOB route: only when the caller asked for it AND
        // the app pipe is up. Resolve the game PID once so the app scans the
        // right process (falls back to the app's own resolution when unknown).
        bool useApp = preferUniqueAob && _aobMakerApp is { IsAvailable: true };
        uint? gamePid = null;
        if (useApp && _backend.TryGetConnectedGamePid(out uint pid)) gamePid = pid;

        var bag = new List<AaScriptOverload>(picks.Count);
        int idx = 0;
        foreach (var row in picks)
        {
            idx++;
            var patt = await _backend.GetMethodAobPatternAsync(row.Address!, len).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(patt.Error))
            {
                StatusMessage = $"AOB scan failed for {row.SignatureLabel}: {patt.Error}";
                return null;
            }

            string moduleName = patt.ModuleName ?? string.Empty;
            string pattern    = patt.Pattern ?? string.Empty;
            bool   aobUnique  = false;
            int    aobOffset  = 0;

            // Module-backed (IL2CPP) overloads can be upgraded to a UNIQUE AOB
            // via the app. Mono-JIT entries (empty module) keep the bridge
            // resolve path. App failure (game unreadable / no unique pattern in
            // the extend cap) falls back to the DLL prologue pattern + warning.
            if (useApp && !string.IsNullOrEmpty(moduleName))
            {
                StatusMessage = $"Asking AOBMaker app for a unique AOB ({idx}/{picks.Count})…";
                var gen = await _aobMakerApp!
                    .GenerateAobAsync(moduleName, patt.Address ?? row.Address!, gamePid)
                    .ConfigureAwait(true);
                if (gen.Success && !string.IsNullOrEmpty(gen.Aob))
                {
                    pattern   = gen.Aob!;
                    aobUnique = true;
                    aobOffset = gen.InjectionOffset;
                }
                else
                {
                    _logger.Write(Constants.LogCategoryView,
                        $"MethodOverloadsViewModel: AOBMaker app GenerateAob failed for {row.SignatureLabel} " +
                        $"— falling back to DLL prologue pattern ({gen.Message ?? "no message"})");
                }
            }

            bag.Add(new AaScriptOverload
            {
                MethodName       = MethodName,
                ClassName        = ClassName,
                Namespace        = Namespace,
                Assembly         = Assembly,  // Phase 22f follow-up: Mono JIT path needs this for registerMethodSymbol
                HumanSuffix      = row.HumanSuffix ?? "T",
                Signature        = row.SignatureLabel,
                Pattern          = pattern,
                // ModuleName="" → Mono JIT path (no module); generator
                // switches to bridge-call AA instead of aobscan. Don't
                // default to GameAssembly.dll — that would silently
                // misclassify Mono targets as IL2CPP and emit a broken
                // aobscanmodule.
                ModuleName       = moduleName,
                Address          = patt.Address ?? row.Address!,
                ModuleOffset     = patt.ModuleOffset ?? "0x0",
                ReturnType       = row.ReturnType,
                DecoderBailed    = aobUnique ? false : patt.DecoderBailed,
                InstructionCount = patt.Stats?.Instructions ?? 0,
                WildcardCount    = patt.Stats?.Wildcards ?? 0,
                AobIsUnique      = aobUnique,
                AobInjectionOffset = aobOffset,
                // Per-param type names — populated for the DLL-free
                // mono_registerSymbolEx generator path. Snapshot to an
                // array so the generator can't see in-place mutations
                // if the row is later refreshed.
                ParamTypes       = row.ParamTypes is { Count: > 0 }
                    ? row.ParamTypes.ToArray()
                    : Array.Empty<string>(),
                // Tokens present in OTHER overloads of this method but not in
                // this one — the signature-match sigExcludes set.
                SigExcludes      = allParamTokens
                    .Where(t => row.ParamTypes is not { Count: > 0 }
                                || !row.ParamTypes.Contains(t, StringComparer.Ordinal))
                    .ToArray(),
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
            var bag = await BuildScriptBagAsync(picks, AobLength, preferUniqueAob: true).ConfigureAwait(true);
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
            var bag = await BuildScriptBagAsync(picks, AobLength, preferUniqueAob: true).ConfigureAwait(true);
            if (bag is null) return;
            string script      = AobAaScriptGenerator.Build(bag);
            string description = picks.Count == 1
                ? $"{ClassName}.{MethodName} {bag[0].HumanSuffix}"
                : $"{ClassName}.{MethodName}  ({picks.Count} overloads)";

            // Mono-JIT overloads resolve via registerMethodSymbol (the AA
            // self-loads unity_symbols.lua); embed it only when the script
            // actually uses it (IL2CPP AOB scripts need no lua).
            if (script.Contains("registerMethodSymbol", StringComparison.Ordinal)
                && !await EmbedBridgeLuaAsync("unity_symbols.lua").ConfigureAwait(true))
                return;

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

    /// <summary>
    /// Embed a bridge lua file into the open CE table (idempotent on the
    /// plugin side) so a generated AA that self-loads it finds it. Returns
    /// false + sets StatusMessage on failure (e.g. AOBMaker pipe down).
    /// </summary>
    private async Task<bool> EmbedBridgeLuaAsync(string fileName)
    {
        if (_aobMaker is null)
        {
            StatusMessage = "AOBMaker pipe unavailable — open CE with the AOBMaker plugin loaded.";
            return false;
        }
        var (ok, err) = await _aobMaker.InjectTableFileAsync(
            fileName, UnityDumpUI.Core.Services.BridgeLuaResources.Read(fileName))
            .ConfigureAwait(true);
        if (!ok)
            StatusMessage = $"Couldn't embed {fileName}: {err ?? "AOBMaker pipe unavailable"}";
        return ok;
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
        // Single, unmissable break warning on its own line, immediately after
        // the guard — the hardcoded address goes stale on every relaunch.
        sb.AppendLine("-- WARNING: hardcoded address — this BREAKS after a game relaunch/update (the address changes); use \"Copy: register (sig-match)\" for a relaunch-safe symbol.");
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
    /// DLL-free Mono path: copy an AA script that calls
    /// <c>mono_registerSymbolEx</c> from <c>mono_registersymbol_helpers.lua</c>
    /// (lazy-loaded out of the CT on enable). Works without ever
    /// injecting the Discrete DLL — the user's existing
    /// <c>mono_*</c> Lua helper pattern, generated for them.
    /// Universal across Mono + IL2CPP (CE Active Mono features) — gated on
    /// <see cref="SupportsSignatureMatch"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunMonoSelectionCmd))]
    public async Task CopyMonoSymbolScriptAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0)
        {
            StatusMessage = "Select at least one overload with a native address.";
            return;
        }
        if (CopyToClipboardAsync is null) return;

        IsBusy       = true;
        ErrorMessage = null;
        StatusMessage = $"Generating mono_registerSymbolEx script for {picks.Count} overload(s)…";
        try
        {
            // Re-use BuildScriptBagAsync (already populates ParamTypes
            // for us) — pattern bytes are unused by this generator, but
            // the shared bag keeps the address + ModuleName='' check
            // honest. AOB length doesn't matter; minimum allowed is 8.
            var bag = await BuildScriptBagAsync(picks, aobLength: 8).ConfigureAwait(true);
            if (bag is null) return;
            string script = MonoRegisterSymbolAaScriptGenerator.Build(bag);
            await CopyToClipboardAsync(script).ConfigureAwait(true);
            StatusMessage = $"Copied mono_registerSymbolEx AA script for {bag.Count} overload(s) to clipboard.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"MethodOverloadsViewModel: mono_register script generation failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Push the DLL-free Mono register AA straight into CE's address
    /// list via the AOBMaker bridge — same UE5CEDumper-style flow as
    /// <see cref="CreateAaScriptInCeAsync"/>, but using the
    /// <c>mono_registerSymbolEx</c> generator instead of the AOB one.
    /// Needs a Unity target AND AOBMaker bridge — gated on
    /// <see cref="ShowSignatureCreateInCe"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunMonoCreateInCe))]
    public async Task CreateMonoSymbolInCeAsync()
    {
        var picks = SelectedAddressable();
        if (picks.Count == 0 || _aobMaker is null)
        {
            StatusMessage = "Select at least one overload with a native address.";
            return;
        }

        IsBusy       = true;
        ErrorMessage = null;
        StatusMessage = $"Pushing mono_registerSymbolEx script for {picks.Count} overload(s) to CE…";
        try
        {
            var bag = await BuildScriptBagAsync(picks, aobLength: 8).ConfigureAwait(true);
            if (bag is null) return;
            string script      = MonoRegisterSymbolAaScriptGenerator.Build(bag);
            string description = picks.Count == 1
                ? $"{ClassName}.{MethodName} {bag[0].HumanSuffix} (DLL-free)"
                : $"{ClassName}.{MethodName}  ({picks.Count} overloads, DLL-free)";

            // The AA self-loads mono_registersymbol_helpers.lua — embed it.
            if (!await EmbedBridgeLuaAsync("mono_registersymbol_helpers.lua").ConfigureAwait(true))
                return;

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
                $"MethodOverloadsViewModel: mono_register AOBMaker push failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunMonoSelectionCmd() =>
        !IsBusy && SupportsSignatureMatch && CopyToClipboardAsync is not null
        && Overloads.Any(r => r.IsSelected);

    private bool CanRunMonoCreateInCe() =>
        !IsBusy && SupportsSignatureMatch && _aobMaker is not null
        && Overloads.Any(r => r.IsSelected);

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
