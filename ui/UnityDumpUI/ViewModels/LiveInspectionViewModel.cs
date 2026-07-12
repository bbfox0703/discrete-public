// LiveInspectionViewModel.cs — Phase 9 + Phase 11.5 panels coordinator.
//
// Right-click-to-inspect cascade:
//   1. SetTarget(assembly, ns, name)         — caller (Phase 8 VM) hands a class.
//   2. FindInstancesCommand                  — heap-scan for live instances.
//   3. SelectedInstance cascade              — walkInstance decodes top-level fields.
//   4. AutoRefreshIntervalMs                 — re-walks at 250 ms / 1 s.
//
// Phase 11.5 (this revision): the field grid is now a *hierarchical* view.
// Pointer-to-class fields and array/List<T> fields can be expanded inline; the
// children load lazily via walkInstance / walkArray and are inserted as
// indented siblings underneath the parent. Replaces the separate "Array
// elements" panel + manual WalkArray button from the original Phase 10
// follow-up.
//
// Right-click on any node copies a CE XML / CE field rooted at that node's
// effective address — pointer fields use the dereferenced child address,
// array slots use the slot's data address, top-level rows use the instance
// address. Both copy paths reuse CeXmlExporter.RenderAsync's live-data path
// (Phase 10f), so the absolute-rooted output is identical to the right-click
// "Copy CE XML" on the assembly browser.
//
// AOT mandate matches Phase 8: CommunityToolkit.Mvvm source-gen only, no
// reflection JSON paths (BackendAdapter already enforces that).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

/// <summary>
/// Args for <see cref="LiveInspectionViewModel.SendToBrowserRequested"/> — the
/// "→ Browser" handoff. Carries the Inspector's current class identity so
/// MainWindow can fill the Assembly Browser's Assemblies + Classes filter
/// inputs. <see cref="Class"/> keeps the slash form for nested types
/// (e.g. <c>MainGame/PLY_WORK</c>), matching how the Browser presents them.
/// </summary>
public sealed class InspectorBrowseRequest : EventArgs
{
    public InspectorBrowseRequest(string assembly, string @namespace, string className)
    {
        Assembly  = assembly;
        Namespace = @namespace;
        Class     = className;
    }
    public string Assembly  { get; }
    public string Namespace { get; }
    public string Class     { get; }
}

public sealed partial class LiveInspectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;

    private CancellationTokenSource? _findCts;
    private CancellationTokenSource? _walkCts;
    private CancellationTokenSource? _autoRefreshCts;
    private CancellationTokenSource? _exportCts;
    private Task?                    _autoRefreshLoop;

    // Cross-VM bulk lane: findInstances is a heap-scan bulk op. Null in
    // tests / single-VM use.
    private readonly BulkOperationGate? _bulkGate;
    private IDisposable? _bulkLease;

    // Persistent per-game Inspector bookmarks (pin an instance, jump back later).
    private readonly BookmarkStore _bookmarkStore = new();

    /// <summary>True when ANOTHER bulk op (snapshot / value-scan) holds the
    /// lane — greys out Find Instances. Our own scan holds _bulkLease.</summary>
    private bool BulkBusyElsewhere => _bulkGate is not null && _bulkGate.IsBusy && _bulkLease is null;

    public LiveInspectionViewModel(BackendAdapter backend, FileLogger logger,
                                   BulkOperationGate? bulkGate = null)
    {
        _backend = backend;
        _logger  = logger;
        _bulkGate = bulkGate;
        if (_bulkGate is not null)
            _bulkGate.PropertyChanged += (_, _) => FindInstancesCommand.NotifyCanExecuteChanged();
        // Per-row action callbacks. Built once and reused by every
        // WalkedFieldNode the VM creates so the row-action buttons
        // (addr / ptr / name / HEX) reach the right helpers without each
        // node holding its own copy of the lambdas. CopyText and
        // NavigateHex resolve to whatever's wired on the VM at call time
        // (CopyToClipboardAsync is set late by the View; AobMaker is
        // optional), so the closures degrade to no-op when absent.
        _actions = new WalkedFieldActions
        {
            Toggle      = ToggleExpandAsync,
            CopyText    = s => CopyToClipboardAsync?.Invoke(s ?? string.Empty)
                                    ?? Task.CompletedTask,
            // Surface a message when CE / the AOBMaker plugin isn't there,
            // instead of a silent no-op. Covers both the row HEX buttons and
            // the inspector's HEX command (which calls this same Func).
            NavigateHex = async (addr, ct) =>
            {
                if (AobMaker is null)
                {
                    ErrorMessage = "AOBMaker pipe unavailable — open CE with the AOBMaker plugin loaded.";
                    return false;
                }
                bool ok = await AobMaker.NavigateHexViewAsync(addr, ct).ConfigureAwait(true);
                if (!ok)
                    ErrorMessage = "Couldn't reach CE's Memory View — open CE with the AOBMaker plugin loaded, then retry.";
                return ok;
            },
        };
        // Keep FilteredFields in lockstep with Fields. Every singleton
        // mutation funnels through this; bulk paths (walk repopulate,
        // expand-children insert, subtree collapse) bracket their loops
        // with `_refilterSuppressionDepth++` and call RefilterFields once
        // at the end — otherwise a 1350-field PlayerData walk would do
        // 1350 * O(N) refilters and 1350 DataGrid layout passes.
        Fields.CollectionChanged += (_, _) => RefilterFields();
    }

    private readonly WalkedFieldActions _actions;

    /// <summary>
    /// Bulk-mutation re-entry guard. >0 means a caller is in the middle
    /// of adding / removing many rows at once and prefers to refilter
    /// once at the end. Counter (not bool) so nested batches compose
    /// correctly — e.g. WalkInstanceAsync's repopulate loop wrapping
    /// ToggleExpandAsync's InsertChildrenIntoFields during expansion replay.
    /// </summary>
    private int _refilterSuppressionDepth;

    /// <summary>
    /// View-supplied clipboard write — wired by MainWindow code-behind.
    /// Mirrors the AssemblyBrowser pattern so both VMs stay UI-agnostic.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>
    /// View-supplied accessor for the inspector DataGrid's current multi-row
    /// selection. Returns an empty list when nothing's selected or only one
    /// row is highlighted (in which case the singular <see cref="SelectedNode"/>
    /// path is sufficient). Wired by MainWindow code-behind — keeps the VM
    /// free of Avalonia DataGrid types.
    /// </summary>
    public Func<IReadOnlyList<WalkedFieldNode>>? GetSelectedNodes { get; set; }

    /// <summary>
    /// View-supplied DataGrid scroll-into-view bridge. Invoked by the VM
    /// after a fresh walk repopulates <see cref="Fields"/> so the
    /// previously-selected field stays visible across instance switches
    /// (and auto-refresh ticks). Null is fine — VM falls back to plain
    /// SelectedNode reassignment and the user just sees the scroll reset.
    /// </summary>
    public Action<WalkedFieldNode>? ScrollFieldIntoView { get; set; }

    /// <summary>
    /// Optional AOBMaker CE-plugin bridge. Wired from MainWindowViewModel
    /// once at startup. Null when the host hasn't supplied one (also fine —
    /// the per-row HEX button just no-ops, no error surfaced).
    /// </summary>
    public IAobMakerBridge? AobMaker { get; set; }

    /// <summary>
    /// Game-process pointer size (4 on x86 / WOW64, 8 on x64), from the
    /// handshake. Drives the managed-String header offsets in the
    /// drilldown decode fallback — the object header is <c>2 × pointerSize</c>,
    /// so the int32 length sits at +0x10 / chars at +0x14 on x64, but at
    /// +0x8 / +0xC on x86. 0 = not yet connected; treated as 8 (x64) so the
    /// common case keeps working before the first handshake. Wired by
    /// <c>MainWindowViewModel.OnGameConnected</c>, mirroring the Snapshot VM.
    /// </summary>
    [ObservableProperty] private int _gamePointerSize;

    /// <summary>
    /// Supplies the base live-export options (drilldown depth, array limit,
    /// description verbosity, and the collapse / flatten / record-colour knobs)
    /// for the instance / node "Copy CE XML" / "Copy CE field" commands. Wired by
    /// MainWindowViewModel to read the Browser's Export-options flyout so the
    /// Inspector honours the same settings the user set there. Null ⇒
    /// <see cref="ExportOptions.Defaults"/>. The live flags (LiveData /
    /// PointerSize) are overlaid per-call from the active game session.
    /// </summary>
    public Func<ExportOptions>? ExportOptionsProvider { get; set; }

    private ExportOptions ResolveExportOptions()
    {
        ExportOptions baseOpts = ExportOptionsProvider?.Invoke() ?? ExportOptions.Defaults;
        return baseOpts with
        {
            LiveData    = true,
            PointerSize = GamePointerSize == 4 ? 4 : 8,
        };
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

    public void SetTarget(string assembly, string? @namespace, string name)
    {
        _ = StopAutoRefreshAsync();
        // When the inspected class actually changes, reset the per-class filters
        // (2026-06-08 user report): a stale Field filter JSON predicate or
        // walked-fields keyword from the previous class otherwise silently hides
        // the new class's instances/fields. Re-inspecting the SAME class keeps
        // the filters (matches the existing "preserve on re-walk" behaviour).
        bool classChanged =
            !string.Equals(TargetAssembly, assembly, StringComparison.Ordinal) ||
            !string.Equals(TargetNamespace ?? string.Empty, @namespace ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(TargetName, name, StringComparison.Ordinal);
        TargetAssembly  = assembly;
        TargetNamespace = @namespace ?? string.Empty;
        TargetName      = name;
        Instances.Clear();
        SelectedInstance = null;
        Fields.Clear();
        ErrorMessage = null;
        WalkSummary  = string.Empty;
        if (classChanged)
        {
            FieldFilterJson = string.Empty;   // "Field filter" JSON predicate box
            Filter          = string.Empty;   // "filter walked fields" keyword box
            // Filter builder (numeric-field picker + value boxes): clear at the
            // SAME time as the two filters above, not lazily on the next walk —
            // otherwise the dropdown keeps showing the PREVIOUS class's fields
            // until you click an instance (2026-06-08 user report).
            FilterFieldOptions.Clear();
            SelectedFilterField = null;
            FilterValue1 = string.Empty;
            FilterValue2 = string.Empty;
        }
    }

    /// <summary>
    /// Cross-tab handoff to the Assembly Browser. Raised by
    /// <see cref="SendToBrowserCommand"/>; MainWindow routes it to fill the
    /// Browser's Assemblies + Classes filter inputs with the current target's
    /// identity (and best-effort selects the assembly so the class list loads)
    /// then switches to the Fields tab.
    /// </summary>
    public event EventHandler<InspectorBrowseRequest>? SendToBrowserRequested;

    /// <summary>
    /// "→ Browser" toolbar button. Hands the current target's assembly + class
    /// to the Assembly Browser's filter inputs. No-op without a target (same
    /// always-enabled convention as Find instances).
    /// </summary>
    [RelayCommand]
    private void SendToBrowser()
    {
        if (!HasTarget) return;
        SendToBrowserRequested?.Invoke(this, new InspectorBrowseRequest(
            assembly:   TargetAssembly  ?? string.Empty,
            @namespace: TargetNamespace ?? string.Empty,
            className:  TargetName      ?? string.Empty));
    }

    /// <summary>Class Pivot overhaul Wave D — "→ Pivot" handoff. Lands the
    /// current target class into the Class Pivot tab (same request shape as
    /// SendToBrowser; MainWindow builds the FQN + switches tabs).</summary>
    public event EventHandler<InspectorBrowseRequest>? SendToPivotRequested;

    [RelayCommand]
    private void SendToPivot()
    {
        if (!HasTarget) return;
        SendToPivotRequested?.Invoke(this, new InspectorBrowseRequest(
            assembly:   TargetAssembly  ?? string.Empty,
            @namespace: TargetNamespace ?? string.Empty,
            className:  TargetName      ?? string.Empty));
    }

    /// <summary>
    /// Phase 17c — cross-tab handoff entry point. Pushes
    /// <paramref name="address"/> into the Instances list (head) and
    /// selects it so the existing <see cref="OnSelectedInstanceChanged"/>
    /// cascade fires <c>walkInstance</c>. Caller must <see cref="SetTarget"/>
    /// first so the walk has a class spec; otherwise this is a no-op.
    /// Returns immediately — the walk runs in the background per the
    /// existing convention (auto-refresh and FindInstances both behave the
    /// same way).
    /// </summary>
    /// <summary>
    /// Absolute address of a field to auto-focus once the walk that the
    /// <see cref="SelectedInstance"/> change kicks off finishes. Set by
    /// <see cref="FocusOnInstance(string,string?)"/> for the Value-Search →
    /// Inspector handoff so a deeply-nested match (e.g. team.gold inside a
    /// value struct) is expanded-to + selected, not left at the top level.
    /// One-shot: cleared by the walk after it consumes it.
    /// </summary>
    private string? _pendingFocusFieldAddr;

    public void FocusOnInstance(string address) => FocusOnInstance(address, null);

    /// <summary>
    /// Focus an instance and, optionally, drill the walked tree down to the
    /// field at <paramref name="focusFieldAddress"/> (its absolute storage
    /// address) and select it. The drilldown runs after the walk completes —
    /// see <see cref="FocusFieldByAddressAsync"/>.
    /// </summary>
    public void FocusOnInstance(string address, string? focusFieldAddress)
    {
        if (!HasTarget || string.IsNullOrWhiteSpace(address)) return;
        // Stash BEFORE the SelectedInstance assignment below — that setter fires
        // WalkInstanceAsync, which consumes the pending address on completion.
        _pendingFocusFieldAddr =
            string.IsNullOrWhiteSpace(focusFieldAddress) ? null : focusFieldAddress;
        InstanceItem? existing = null;
        foreach (var i in Instances)
        {
            if (string.Equals(i.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                existing = i;
                break;
            }
        }
        if (existing is null)
        {
            existing = new InstanceItem(Instances.Count, address);
            Instances.Insert(0, existing);
        }
        bool alreadySelected = ReferenceEquals(SelectedInstance, existing);
        SelectedInstance = existing;  // setter fires WalkInstanceAsync (on change)
        // If it was already the selected instance the setter is a no-op, so no
        // walk runs to consume _pendingFocusFieldAddr — re-walk explicitly so
        // the focus still lands (e.g. navigating to two fields of one instance).
        if (alreadySelected && _pendingFocusFieldAddr is not null)
            _ = WalkInstanceAsync(existing.Address);
    }

    // ---------- Bookmarks (persistent per-game pins) ----------------------

    /// <summary>Per-game identity (GameBuildId) selecting the bookmark file. Set
    /// by MainWindowViewModel on connect; changing it reloads the list.</summary>
    [ObservableProperty] private string? _gameKey;

    partial void OnGameKeyChanged(string? value)
    {
        Bookmarks.Clear();
        if (string.IsNullOrEmpty(value)) return;   // disconnected — just clear
        foreach (InstanceBookmark b in _bookmarkStore.Load(value))
            Bookmarks.Add(b);
    }

    /// <summary>Saved Inspector pins for the connected game.</summary>
    public ObservableCollection<InstanceBookmark> Bookmarks { get; } = new();

    /// <summary>Optional label for the next bookmark (blank = class name).</summary>
    [ObservableProperty] private string _bookmarkLabelInput = string.Empty;

    /// <summary>Bookmark picked in the dropdown (Recall / Delete target).</summary>
    [ObservableProperty] private InstanceBookmark? _selectedBookmark;

    /// <summary>Pin the current target + selected instance so you can jump back
    /// later (persisted per game). No-op without a target.</summary>
    [RelayCommand]
    private void BookmarkCurrent()
    {
        if (!HasTarget) { ErrorMessage = "Inspect a class first, then bookmark it."; return; }
        string address = SelectedInstance?.Address ?? string.Empty;
        string asm = TargetAssembly!;
        string ns  = TargetNamespace ?? string.Empty;
        string cls = TargetName!;

        // De-dup on (assembly, class, address): re-bookmarking the same instance
        // just refreshes the label and moves it to the top.
        for (int i = Bookmarks.Count - 1; i >= 0; i--)
        {
            InstanceBookmark b = Bookmarks[i];
            if (string.Equals(b.Assembly, asm, StringComparison.Ordinal)
                && string.Equals(b.Class, cls, StringComparison.Ordinal)
                && string.Equals(b.Address, address, StringComparison.OrdinalIgnoreCase))
                Bookmarks.RemoveAt(i);
        }
        Bookmarks.Insert(0, new InstanceBookmark
        {
            Label = BookmarkLabelInput.Trim(), Assembly = asm, Namespace = ns,
            Class = cls, Address = address,
        });
        BookmarkLabelInput = string.Empty;
        PersistBookmarks();
    }

    /// <summary>Jump back to a bookmark: re-target its class and focus its
    /// instance. After a restart the saved address is stale, so we re-anchor the
    /// stable CLASS via findInstances (Unity has no universal root to re-walk).</summary>
    [RelayCommand]
    private async Task RecallBookmarkAsync(InstanceBookmark? bookmark)
    {
        if (bookmark is null) return;
        SetTarget(bookmark.Assembly, bookmark.Namespace, bookmark.Class);

        _bulkLease = _bulkGate?.TryEnter("Recall bookmark");
        if (_bulkGate is not null && _bulkLease is null)
        {
            ErrorMessage = "Another bulk operation is running — try Recall again in a moment.";
            return;
        }
        _findCts?.Cancel();
        _findCts = new CancellationTokenSource();
        CancellationToken ct = _findCts.Token;
        try
        {
            FindInstancesResult r = await _backend.FindInstancesAsync(
                bookmark.Assembly, bookmark.Namespace, bookmark.Class,
                InstanceLimit, aliveOnly: true, null, Sane, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(r.Error) || r.Matches.Count == 0)
            {
                ErrorMessage = $"Bookmark '{bookmark.DisplayLabel}': no live instance of {bookmark.ClassDisplay} — it may be stale (game restarted or object destroyed). Use Find instances to re-pick.";
                return;
            }
            // Prefer the exact saved address if it's still alive; otherwise
            // re-anchor to the first live instance and update the pin.
            string chosen = r.Matches[0];
            foreach (string a in r.Matches)
                if (string.Equals(a, bookmark.Address, StringComparison.OrdinalIgnoreCase)) { chosen = a; break; }
            if (!string.Equals(chosen, bookmark.Address, StringComparison.OrdinalIgnoreCase))
            {
                bookmark.Address = chosen;   // re-anchored across a restart
                PersistBookmarks();
            }
            FocusOnInstance(chosen);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"Recall failed: {ex.Message}";
        }
        finally
        {
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    /// <summary>Remove a saved bookmark.</summary>
    [RelayCommand]
    private void DeleteBookmark(InstanceBookmark? bookmark)
    {
        if (bookmark is not null && Bookmarks.Remove(bookmark)) PersistBookmarks();
    }

    private void PersistBookmarks() => _bookmarkStore.Save(GameKey, Bookmarks);

    // ---------- Instances -------------------------------------------------

    public ObservableCollection<InstanceItem> Instances { get; } = new();

    [ObservableProperty] private int _instanceLimit = 256;

    // Phase 32 — false-positive reduction for findInstances.
    // AliveOnly drops destroyed Unity shells (m_CachedPtr == 0). FieldFilterJson
    // is the generic per-class predicate engine: a JSON array of
    // {offset,type,op,value} (e.g. [{"offset":1532,"type":"System.Int32",
    // "op":"==","value":1}] to isolate one logical instance). Empty = no filter.
    [ObservableProperty] private bool _aliveOnly;

    // Sane: drop stale/torn objects whose managed reference fields hold garbage
    // (non-null but unreadable pointers) or out-of-range bools. Generic, no
    // per-class config. On by default — false positives are the common pain.
    [ObservableProperty] private bool _sane = true;

    [ObservableProperty] private string _fieldFilterJson = string.Empty;

    // ---- Field-filter builder (friendly UI over FieldFilterJson) ----------
    // Lets the user compose numeric predicates (level_ > 0, level_ <= 99,
    // level_ == 1, …) without hand-writing the JSON. Each "Add" appends a
    // predicate to FieldFilterJson; the existing Find applies the AND-combined
    // set. Top-level numeric fields only this round (strings / nested deref
    // chains are a follow-up). Populated from the walked top-level fields.
    public ObservableCollection<FilterFieldOption> FilterFieldOptions { get; } = new();

    /// <summary>Operators offered by the builder. "between" expands to a
    /// `&gt;=` + `&lt;=` pair so a single Add covers a closed range.</summary>
    public IReadOnlyList<string> FilterOps { get; } =
        new[] { "==", "!=", ">", ">=", "<", "<=", "between" };

    [ObservableProperty] private FilterFieldOption? _selectedFilterField;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeOp))]
    private string _filterOp = "==";

    [ObservableProperty] private string _filterValue1 = string.Empty;
    [ObservableProperty] private string _filterValue2 = string.Empty;

    /// <summary>True when the "between" operator is selected — reveals the
    /// second value box.</summary>
    public bool IsRangeOp => string.Equals(FilterOp, "between", StringComparison.Ordinal);

    /// <summary>Managed primitive types the builder treats as filterable
    /// numerics (matches the DLL's <c>evalPredicate</c> numeric switch).</summary>
    private static readonly HashSet<string> s_filterNumericTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte",  "System.SByte",
        "System.Int16",   "System.UInt16","System.Char",
        "System.Int32",   "System.UInt32","System.Int64", "System.UInt64",
        "System.Single",  "System.Double",
    };

    /// <summary>Refresh <see cref="FilterFieldOptions"/> from the current
    /// top-level walked fields (numeric only). Called after each walk so the
    /// picker reflects the inspected class's schema.</summary>
    private void RebuildFilterFieldOptions()
    {
        var prev = SelectedFilterField?.Name;
        FilterFieldOptions.Clear();
        // Collect numeric top-level fields, then sort by name (case-insensitive)
        // so the picker is alphabetical — far easier to locate a field by name
        // than scrolling offset order, and it pairs with the type-to-filter
        // dropdown (2026-06-08 user report: many fields, only findable by offset).
        var opts = new List<FilterFieldOption>();
        foreach (WalkedFieldNode n in Fields)
        {
            if (n.IndentLevel != 0) continue;
            if (n.Offset < 0) continue;
            if (!string.IsNullOrEmpty(n.Type) && s_filterNumericTypes.Contains(n.Type))
                opts.Add(new FilterFieldOption(n.Name, n.Offset, n.Type));
        }
        opts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var o in opts) FilterFieldOptions.Add(o);
        if (prev is not null)
            SelectedFilterField = FilterFieldOptions.FirstOrDefault(o => o.Name == prev);
    }

    /// <summary>Append the composed predicate(s) to <see cref="FieldFilterJson"/>.
    /// "between" emits two predicates (`&gt;=` lo, `&lt;=` hi); everything else
    /// one. The next Find applies them AND-combined. Built as plain JSON text
    /// (no DOM) so it stays Native-AOT / trim-safe.</summary>
    [RelayCommand]
    private void AddFilterPredicate()
    {
        FilterFieldOption? f = SelectedFilterField;
        if (f is null) { ErrorMessage = "Pick a field to filter on first."; return; }

        // Bail if the existing box holds something that isn't a JSON array — we
        // won't silently discard a hand-edited filter.
        string cur = (FieldFilterJson ?? string.Empty).Trim();
        if (cur.Length != 0 && !(cur.StartsWith('[') && cur.EndsWith(']')))
        { ErrorMessage = "The current field filter isn't a JSON array — clear it before using the builder."; return; }

        bool isFloat = f.Type is "System.Single" or "System.Double";
        var preds = new List<string>(2);
        if (IsRangeOp)
        {
            if (!TryFormatFilterValue(FilterValue1, isFloat, out string lo) ||
                !TryFormatFilterValue(FilterValue2, isFloat, out string hi))
            { ErrorMessage = "Range needs two numeric bounds (low and high)."; return; }
            preds.Add(PredicateJson(f, ">=", lo));
            preds.Add(PredicateJson(f, "<=", hi));
        }
        else
        {
            if (!TryFormatFilterValue(FilterValue1, isFloat, out string v))
            { ErrorMessage = $"Enter a numeric value for {f.Name}."; return; }
            preds.Add(PredicateJson(f, FilterOp, v));
        }

        string joined = string.Join(",", preds);
        string inner = cur.Length == 0 ? string.Empty : cur.Substring(1, cur.Length - 2).Trim();
        FieldFilterJson = inner.Length == 0 ? $"[{joined}]" : $"[{inner},{joined}]";
        ErrorMessage = null;
    }

    /// <summary>Clear the whole field-filter predicate set.</summary>
    [RelayCommand]
    private void ClearFieldFilter() => FieldFilterJson = string.Empty;

    private static string PredicateJson(FilterFieldOption f, string op, string valueLiteral) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{{\"offset\":{f.Offset},\"type\":\"{JsonEscape(f.Type)}\",\"op\":\"{op}\",\"value\":{valueLiteral}}}");

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Validate + normalise a user-entered value into a bare JSON
    /// number literal. Integer types parse as Int64, float types as Double.</summary>
    private static bool TryFormatFilterValue(string? s, bool isFloat, out string literal)
    {
        literal = "0";
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0) return false;
        if (isFloat)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && !double.IsNaN(d) && !double.IsInfinity(d))
            { literal = d.ToString("R", CultureInfo.InvariantCulture); return true; }
            return false;
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        { literal = l.ToString(CultureInfo.InvariantCulture); return true; }
        return false;
    }

    [ObservableProperty] private string? _classPointer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    [NotifyCanExecuteChangedFor(nameof(CopyCeXmlInstanceCommand))]
    private InstanceItem? _selectedInstance;

    public bool HasSelectedInstance => SelectedInstance is not null;

    partial void OnSelectedInstanceChanged(InstanceItem? value)
    {
        // Don't eagerly Fields.Clear() here — emptying the bound collection
        // forces the DataGrid to drop SelectedItem, which propagates back
        // through the TwoWay binding and nulls out SelectedNode BEFORE
        // WalkInstanceAsync gets a chance to snapshot it. Result: the
        // "preserve selected field across instance switches" logic loses
        // its target. WalkInstanceAsync clears Fields itself, AFTER the
        // snapshot, so the user briefly sees the previous instance's
        // values during the walk RPC — that's fine (it's also what
        // auto-refresh shows).
        WalkSummary = string.Empty;
        if (value is null || !HasTarget) return;
        _ = WalkInstanceAsync(value.Address);
    }

    // ---------- Walked fields (hierarchical, flat-with-indent) -----------

    /// <summary>
    /// Canonical tree of walked fields in display order — the authority for
    /// expand / collapse / scroll-into-view operations. Children of an
    /// expanded node are inserted as siblings immediately after the parent
    /// and removed on collapse. Each row's
    /// <see cref="WalkedFieldNode.IndentLevel"/> drives the visual indent.
    /// The DataGrid does NOT bind to this collection directly — it binds to
    /// <see cref="FilteredFields"/>, which is a filtered mirror so the user
    /// can substring-search by name / type without disturbing the
    /// expand-state machinery (which operates on Fields).
    /// </summary>
    public ObservableCollection<WalkedFieldNode> Fields { get; } = new();

    /// <summary>
    /// View-side filtered mirror of <see cref="Fields"/>, driven by
    /// <see cref="Filter"/>. Empty filter mirrors Fields exactly. UI-only
    /// — Copy CE XML / Copy CE field still operate on the unfiltered tree.
    /// </summary>
    public ObservableCollection<WalkedFieldNode> FilteredFields { get; } = new();

    /// <summary>
    /// Substring filter applied to <see cref="FilteredFields"/>. Matches
    /// against Name OR Type (case-insensitive). Empty = show all.
    /// </summary>
    [ObservableProperty] private string _filter = string.Empty;
    partial void OnFilterChanged(string value) => RefilterFields();

    [ObservableProperty] private string _walkSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCeFieldNodeCommand))]
    private WalkedFieldNode? _selectedNode;

    // ---------- Auto refresh ---------------------------------------------

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

    [RelayCommand(CanExecute = nameof(CanFindInstances))]
    private async Task FindInstancesAsync()
    {
        if (!HasTarget) return;

        // Bulk lane: skip if a snapshot / value-scan holds it (also covers
        // the auto-refresh timer path, which calls ExecuteAsync directly and
        // bypasses CanExecute).
        _bulkLease = _bulkGate?.TryEnter("Find instances");
        if (_bulkGate is not null && _bulkLease is null) return;

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
                                    InstanceLimit, AliveOnly,
                                    string.IsNullOrWhiteSpace(FieldFilterJson) ? null : FieldFilterJson,
                                    Sane, ct)
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
            _bulkLease?.Dispose(); _bulkLease = null;
        }
    }

    // Only the bulk lane gates the button — the HasTarget precondition is
    // enforced inside the method (early return), matching the prior
    // always-enabled behaviour so we don't need to thread HasTarget change
    // notifications into this command.
    private bool CanFindInstances() => !BulkBusyElsewhere;

    [RelayCommand]
    private Task RefreshWalkAsync()
    {
        if (SelectedInstance is null) return Task.CompletedTask;
        // Deep replay so nested array sizes (e.g. Team→Item after buying items)
        // are re-read, not just the top-level fields.
        return WalkInstanceAsync(SelectedInstance.Address, deepReplay: true);
    }

    /// <summary>Slash-joined path of field names / array-index labels from the
    /// root to <paramref name="n"/> — a stable identity for replaying which
    /// nodes were expanded across a re-walk (field names are unique per class;
    /// array slots are unique by index label).</summary>
    private static string NodePath(WalkedFieldNode n)
    {
        var segs = new List<string>(4);
        for (WalkedFieldNode? cur = n; cur is not null; cur = cur.Parent)
            segs.Add(cur.Name);
        segs.Reverse();
        return string.Join('/', segs);
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

    // ---------- Copy Class name / Class ptr ------------------------------
    //
    // Bound to the two small buttons next to the "Class ptr" row in the
    // Inspector header. The whole StackPanel is IsVisible-gated on
    // ClassPointer being non-null, so by the time these fire HasTarget is
    // already true. Null-guards stay in for defence-in-depth (CopyToClipboardAsync
    // is wired late by the View and could in theory be null in tests).

    [RelayCommand]
    private async Task CopyClassNameAsync()
    {
        if (CopyToClipboardAsync is null || string.IsNullOrEmpty(TargetName)) return;
        string text = string.IsNullOrEmpty(TargetNamespace)
            ? TargetName!
            : $"{TargetNamespace}.{TargetName}";
        await CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyClassAddressAsync()
    {
        if (CopyToClipboardAsync is null || string.IsNullOrEmpty(ClassPointer)) return;
        await CopyToClipboardAsync(ClassPointer!).ConfigureAwait(true);
    }

    /// <summary>
    /// Root walk for a fresh selected instance. Replaces the entire
    /// <see cref="Fields"/> list with top-level rows. When a previous walk
    /// had subtrees expanded, the same expansion *paths* (by field name)
    /// are re-walked transparently so auto-refresh doesn't collapse the
    /// user's drilldown view on every tick.
    /// </summary>
    /// <summary>
    /// Walk the instance and repopulate the field grid. Returns false only on a
    /// genuine walk failure (DLL error or thrown exception) — the auto-refresh
    /// loop uses this to stop itself instead of re-hitting the same error every
    /// tick. Cancellation / no-target return true (not an error).
    /// </summary>
    private async Task<bool> WalkInstanceAsync(string address, bool deepReplay = false)
    {
        if (!HasTarget) return true;

        _walkCts?.Cancel();
        _walkCts = new CancellationTokenSource();
        CancellationToken ct = _walkCts.Token;

        ErrorMessage = null;
        IsLoadingWalk = true;
        try
        {
            // Snapshot which nodes were expanded so we can replay. Auto-refresh /
            // instance-switch replay only the ROOT level (bounded cost). A manual
            // Re-walk does a DEEP replay (every expanded node, by path) so nested
            // array sizes are re-read — e.g. Team→Item growing 4→8 after an
            // in-game purchase shows the new slots.
            HashSet<string> expandedRoots = new(StringComparer.Ordinal);
            HashSet<string> expandedPaths = new(StringComparer.Ordinal);
            foreach (WalkedFieldNode n in Fields)
            {
                if (!n.IsExpanded) continue;
                if (n.IndentLevel == 0) expandedRoots.Add(n.Name);
                if (deepReplay) expandedPaths.Add(NodePath(n));
            }
            // Snapshot the currently-selected field's identity so we can
            // restore selection (and scroll position) after the rebuild.
            // Same class = same field schema, so a name+from match almost
            // always succeeds; falls back to top when the user changed
            // class entirely.
            string? selectedName = SelectedNode?.Name;
            string? selectedFrom = SelectedNode?.DeclaredIn;

            WalkInstanceResult result = await _backend
                .WalkInstanceAsync(address, TargetAssembly!, TargetNamespace, TargetName!, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return true;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = $"walkInstance: {result.Error}";
                return false;
            }
            // Bulk repopulate: suppress the per-Add CollectionChanged
            // refilter so a 1350-field PlayerData walk doesn't trigger
            // 1350 refilter passes + 1350 DataGrid layout passes (was
            // making instance-switch take several seconds). Final
            // refilter is invoked once after the loop AND any expansion
            // replay completes.
            _refilterSuppressionDepth++;
            try
            {
                Fields.Clear();
                if (result.Fields is { } fields)
                {
                    foreach (WalkedField f in fields)
                    {
                        Fields.Add(WalkedFieldNode.FromInstanceField(f, indent: 0,
                                                                     actions: _actions));
                    }
                }
                WalkSummary = $"depth={result.Depth} count={result.Count}";

                if (deepReplay)
                {
                    // Deep replay: re-expand every node whose path was expanded
                    // before, at all depths, re-reading nested array sizes along
                    // the way. Each ToggleExpandAsync mutates Fields, so re-scan
                    // after every expansion; the path set + IsExpanded guard make
                    // it converge, and the counter backstops a pathological loop.
                    int guard = 0;
                    bool progressed = true;
                    while (progressed && guard++ < 20000)
                    {
                        progressed = false;
                        foreach (WalkedFieldNode n in Fields.ToArray())
                        {
                            if (n.CanExpand && !n.IsExpanded && expandedPaths.Contains(NodePath(n)))
                            {
                                await ToggleExpandAsync(n).ConfigureAwait(true);
                                progressed = true;
                                break;   // Fields changed — restart the scan
                            }
                        }
                    }
                }
                // Replay top-level expansions only. Deeper-than-one expansions
                // come back collapsed — keeping the auto-refresh cost bounded
                // (one extra pipe RPC per expanded root, not the whole tree).
                else if (expandedRoots.Count > 0)
                {
                    foreach (WalkedFieldNode n in Fields.ToArray())
                    {
                        if (n.IndentLevel == 0 && expandedRoots.Contains(n.Name) && n.CanExpand)
                        {
                            await ToggleExpandAsync(n).ConfigureAwait(true);
                        }
                    }
                }
            }
            finally
            {
                _refilterSuppressionDepth--;
            }
            RefilterFields();
            RebuildFilterFieldOptions();

            // Re-select the previously-focused field by Name + DeclaredIn so
            // the user keeps their viewport when flipping between instances
            // of the same class. DeclaredIn disambiguates when a derived
            // class shadows a base member with the same name.
            if (!string.IsNullOrEmpty(selectedName))
            {
                WalkedFieldNode? match = null;
                foreach (WalkedFieldNode n in Fields)
                {
                    if (n.Name == selectedName
                        && (string.IsNullOrEmpty(selectedFrom) || n.DeclaredIn == selectedFrom))
                    {
                        match = n;
                        break;
                    }
                }
                if (match is not null)
                {
                    SelectedNode = match;
                    ScrollFieldIntoView?.Invoke(match);
                }
            }

            // Value-Search → Inspector handoff: drill the tree down to the
            // matched field and select it. Runs AFTER the name-based restore so
            // an explicit navigation target wins over the carried-over selection.
            // One-shot — clear it whether or not the field was reachable.
            if (_pendingFocusFieldAddr is { } focusAddr)
            {
                _pendingFocusFieldAddr = null;
                await FocusFieldByAddressAsync(focusAddr, ct).ConfigureAwait(true);
            }

            _logger.Write(Constants.LogCategoryView,
                $"walkInstance {address} {TargetLabel} {WalkSummary}");
            return true;
        }
        catch (OperationCanceledException) { return true; /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(Constants.LogCategoryView,
                $"walkInstance({address}) failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            IsLoadingWalk = false;
        }
    }

    /// <summary>
    /// Drill the walked tree down to the field whose absolute storage address is
    /// <paramref name="targetHex"/> and select + scroll to it. Used by the
    /// Value-Search → Inspector handoff so a nested match (a value inside a value
    /// struct, e.g. team.gold) is revealed, not left at the instance top level.
    ///
    /// Strategy: look for an exact address match among the currently-materialised
    /// rows; if absent, find the expandable VALUE-STRUCT ancestor whose contiguous
    /// span contains the target, expand it (one walkInstance RPC), and retry.
    /// Bounded so a missed target can't loop. Matches that live in deref'd memory
    /// outside the instance's contiguous range (behind a pointer / collection)
    /// aren't auto-reached — the instance stays selected (no worse than before).
    /// </summary>
    private async Task FocusFieldByAddressAsync(string targetHex, CancellationToken ct)
    {
        if (!TryParseHexAddr(targetHex, out ulong target) || target == 0) return;

        for (int iter = 0; iter < 16; ++iter)
        {
            if (ct.IsCancellationRequested) return;

            // 1) Exact hit among materialised rows → select it.
            WalkedFieldNode? hit = null;
            foreach (WalkedFieldNode n in Fields)
            {
                if (AddrEquals(n.Address, target) || AddrEquals(n.EffectiveAddress, target))
                { hit = n; break; }
            }
            if (hit is not null)
            {
                SelectedNode = hit;
                ScrollFieldIntoView?.Invoke(hit);
                return;
            }

            // 2) No exact row yet — expand the collapsed value-struct whose
            //    contiguous span covers the target, then retry. Re-derived each
            //    pass (row indices shift as children materialise).
            WalkedFieldNode? container = FindStructContainerCovering(target);
            if (container is null) return;            // can't pinpoint — leave as-is

            await ToggleExpandAsync(container).ConfigureAwait(true);
            if (!container.IsExpanded) return;        // expansion failed — stop
        }
    }

    /// <summary>
    /// Find the collapsed, expandable VALUE-STRUCT row whose inline storage span
    /// contains <paramref name="target"/>. A value struct lives contiguously at
    /// its own address, so its members fall in [node.Address, nextRowAtSameOr
    /// ShallowerIndent). Reference / collection nodes are skipped — their data
    /// lives in deref'd memory, not in this contiguous range.
    /// </summary>
    private WalkedFieldNode? FindStructContainerCovering(ulong target)
    {
        var rows = Fields;
        for (int i = 0; i < rows.Count; ++i)
        {
            WalkedFieldNode n = rows[i];
            if (n.Kind != WalkedFieldKind.ValueStruct || n.IsExpanded || !n.CanExpand) continue;
            if (!TryParseHexAddr(n.Address, out ulong start) || target < start) continue;

            // Upper bound = the next row at the same-or-shallower indent (where
            // n's subtree ends); infinity if n is the last such row.
            ulong end = ulong.MaxValue;
            for (int j = i + 1; j < rows.Count; ++j)
            {
                if (rows[j].IndentLevel <= n.IndentLevel)
                {
                    if (TryParseHexAddr(rows[j].Address, out ulong nextStart) && nextStart > start)
                        end = nextStart;
                    break;
                }
            }
            if (target < end) return n;
        }
        return null;
    }

    private static bool AddrEquals(string? hex, ulong target)
        => TryParseHexAddr(hex, out ulong v) && v == target;

    private static bool TryParseHexAddr(string? hex, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    // ---------- Hierarchical expand / collapse ---------------------------

    /// <summary>
    /// Toggle expansion on a node. Pointer-to-class fields expand via
    /// <c>walkInstance</c> on the dereferenced object; array / List&lt;T&gt;
    /// fields expand via <c>walkArray</c> with one synthetic child row per
    /// slot (slots that themselves point at managed objects are
    /// recursively expandable). Expansion is idempotent — re-expanding a
    /// previously-expanded subtree does not re-issue pipe calls.
    /// </summary>
    public async Task ToggleExpandAsync(WalkedFieldNode node)
    {
        if (node is null || !node.CanExpand) return;
        if (node.IsExpanded)
        {
            CollapseSubtree(node);
            node.IsExpanded = false;
            return;
        }

        if (!node.IsLoaded)
        {
            node.IsLoading = true;
            try
            {
                await LoadChildrenAsync(node, default).ConfigureAwait(true);
                node.IsLoaded = true;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"expand {node.Name}: {ex.Message}";
                _logger.Write(Constants.LogCategoryView,
                    $"expand({node.Name}) failed: {ex.GetType().Name}: {ex.Message}");
                node.IsLoading = false;
                return;
            }
            node.IsLoading = false;
        }
        InsertChildrenIntoFields(node);
        node.IsExpanded = true;
    }

    private async Task LoadChildrenAsync(WalkedFieldNode node, CancellationToken ct)
    {
        node.Children.Clear();

        switch (node.Kind)
        {
            case WalkedFieldKind.PointerToClass:
                await LoadPointerChildrenAsync(node, ct).ConfigureAwait(true);
                break;

            // Inline value-type substruct: same RPC as PointerToClass —
            // walkInstance(addr, type) — but EffectiveAddress is the field's
            // own storage (no deref). The DLL's walkInstance iterates
            // fields by offset starting at the supplied address, no header
            // dereference, so this works for both class instances (with
            // header) and inline value types (without).
            case WalkedFieldKind.ValueStruct:
                await LoadPointerChildrenAsync(node, ct).ConfigureAwait(true);
                break;

            case WalkedFieldKind.Collection:
                await LoadArrayChildrenAsync(node, ct).ConfigureAwait(true);
                break;

            case WalkedFieldKind.ArraySlotPointer:
                await LoadPointerChildrenAsync(node, ct).ConfigureAwait(true);
                break;

            // Dictionary<K,V> — the DLL already decoded the key→value pairs
            // into the wire payload (CaptureMono/Il2cppDictionary). Build the
            // child rows straight from the embedded Fields; no RPC. Each child
            // runs back through the classifier in FromInstanceField, so a nested
            // dict / value-struct value stays recursively expandable.
            case WalkedFieldKind.Dictionary:
                if (node.EmbeddedFields is { } pairs)
                {
                    foreach (WalkedField f in pairs)
                    {
                        WalkedFieldNode child = WalkedFieldNode.FromInstanceField(
                            f, indent: node.IndentLevel + 1, actions: _actions);
                        child.Parent = node;
                        node.Children.Add(child);
                    }
                }
                break;
        }
    }

    private async Task LoadPointerChildrenAsync(WalkedFieldNode node, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(node.EffectiveAddress)) return;

        // Header-resolve preferred path (IL2CPP only, but harmless for Mono —
        // its walkInstance handler ignores the extra arg and errors out, after
        // which we fall through to the name-based path below). For pointer
        // fields the EffectiveAddress IS the heap pointer to the managed
        // object, so the runtime class is stamped at *(uintptr_t*)addr — this
        // works for ANY class, including generic instantiations like
        // `ClassAry<CARRYITEM>` that il2cpp_class_from_name can't resolve
        // because concrete instantiations aren't registered by name.
        // ValueStruct doesn't reach this path — it has no header (the field
        // storage IS the value, no class pointer at offset 0) — and is
        // routed to LoadPointerChildrenAsync separately with name-based
        // resolution still required.
        WalkInstanceResult? res = null;
        string? lastErr = null;
        if (node.Kind is WalkedFieldKind.PointerToClass or WalkedFieldKind.ArraySlotPointer)
        {
            WalkInstanceResult attempt = await _backend
                .WalkInstanceFromHeaderAsync(node.EffectiveAddress!, ct)
                .ConfigureAwait(true);
            if (string.IsNullOrEmpty(attempt.Error)) res = attempt;
            else lastErr = attempt.Error;
        }

        // Resolve the managed type to walk. For pointer fields the type
        // stored on the row is the field's declared managed type; ignore
        // collection wrappers (those go through Collection above).
        if (res is null && InstanceFieldClassifier.TryParseClassName(node.Type, out string ns, out string name))
        {
            // Generic instances need the IL-form name (with backtick suffix) for
            // FindClass to match — the wire-side `il2cpp_type_get_name` strips it
            // ("ClassAry`1<T>" → "ClassAry<T>"), but the runtime class registry
            // keeps the suffix. Try the bare name first (covers non-generic
            // types), then arity 1..3 fallbacks. NOTE: name-based lookup of an
            // open generic definition still won't yield the concrete
            // instantiation's field layout — the header path above is the
            // primary fix for generics. This block stays as the legacy fallback
            // for value-type substructs whose header path doesn't apply.
            bool isGeneric = node.Type is { Length: > 0 } t && t.IndexOf('<') > 0;
            var nameVariants = new List<string> { name };
            if (isGeneric)
            {
                nameVariants.Add(name + "`1");
                nameVariants.Add(name + "`2");
                nameVariants.Add(name + "`3");
            }

            // Assembly resolution: walkInstance searches ONLY the named image,
            // so UnityEngine.Vector3 won't be found in Assembly-CSharp even
            // though the field lives in an Assembly-CSharp class. Try the
            // parent's assembly first, then fall back to common Unity assemblies
            // for well-known namespaces.
            string[] candidates = BuildAssemblyCandidates(TargetAssembly, ns);
            foreach (string nv in nameVariants)
            {
                foreach (string asm in candidates)
                {
                    WalkInstanceResult attempt = await _backend
                        .WalkInstanceAsync(node.EffectiveAddress!, asm, ns, nv, ct)
                        .ConfigureAwait(true);
                    if (string.IsNullOrEmpty(attempt.Error)) { res = attempt; break; }
                    lastErr = attempt.Error;
                }
                if (res is not null) break;
            }
        }
        if (res is null || res.Fields is null)
        {
            // Surface the failure so the chevron-clicked-but-no-children
            // case isn't silent.
            ErrorMessage = $"expand {node.Name} ({node.Type}): "
                         + (lastErr ?? "class not resolved");
            return;
        }
        // Diagnostic — when a drilldown returns suspiciously few fields (e.g.
        // only an inherited m_CachedPtr while the real class has many), this
        // line pinpoints whether the DLL resolved the WRONG class (res.Name !=
        // node.Type's simple name) or the right class with truncated metadata
        // (res.Name matches but Count is low). Helps diagnose the 活俠傳
        // StatConvertData "only m_CachedPtr" report without a DLL rebuild.
        _logger.Write(Constants.LogCategoryView,
            $"[Inspector] expand {node.Name} type={node.Type} addr={node.EffectiveAddress} " +
            $"-> resolved {res.Namespace}.{res.Name} depth={res.Depth} fields={res.Fields.Count}");
        foreach (WalkedField f in res.Fields)
        {
            WalkedFieldNode child = WalkedFieldNode.FromInstanceField(
                f, indent: node.IndentLevel + 1, actions: _actions);
            child.Parent = node;
            node.Children.Add(child);
        }
    }

    /// <summary>
    /// Order to try when looking up a managed type for expansion. The
    /// caller's assembly first (covers user-defined types in
    /// Assembly-CSharp), then well-known fallbacks based on the namespace
    /// — UnityEngine types live across CoreModule / IMGUIModule / etc.,
    /// System types live in mscorlib / System.Runtime.
    /// </summary>
    internal static string[] BuildAssemblyCandidates(string? primaryAssembly, string @namespace)
    {
        List<string> list = new(4);
        if (!string.IsNullOrEmpty(primaryAssembly)) list.Add(primaryAssembly);
        if (@namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
        {
            // Most Unity primitives live in CoreModule. Add fallbacks for
            // the common splits without enumerating every UnityEngine.*.dll.
            if (!list.Contains("UnityEngine.CoreModule")) list.Add("UnityEngine.CoreModule");
            if (!list.Contains("UnityEngine"))            list.Add("UnityEngine");
            // uGUI types (UnityEngine.UI.Text / Image / Button …) live in the
            // UnityEngine.UI assembly, NOT CoreModule — without these the
            // name-based fallback can never resolve a uGUI field's class
            // ("class not found: UnityEngine.UI.Text"). TextRenderingModule
            // covers UnityEngine.UI.* legacy text rendering helpers.
            if (@namespace.StartsWith("UnityEngine.UI", StringComparison.Ordinal))
            {
                if (!list.Contains("UnityEngine.UI"))                  list.Add("UnityEngine.UI");
                if (!list.Contains("UnityEngine.UIModule"))            list.Add("UnityEngine.UIModule");
                if (!list.Contains("UnityEngine.TextRenderingModule")) list.Add("UnityEngine.TextRenderingModule");
            }
        }
        else if (@namespace.StartsWith("Unity.", StringComparison.Ordinal))
        {
            // DOTS / package types (Unity.Entities.Entity, Unity.Mathematics.
            // quaternion / float3, Unity.Collections.*) live in an assembly
            // usually named after the namespace itself or its two-segment
            // prefix — NOT the field's declaring assembly. Without these the
            // name fallback can never resolve them ("class not found:
            // Unity.Entities.Entity"). Add both forms plus the common DOTS
            // assemblies; the DLL ignores any that don't exist (and has its own
            // cross-assembly backstop in resolveClassFromArgs).
            void AddUnique(string a) { if (!string.IsNullOrEmpty(a) && !list.Contains(a)) list.Add(a); }
            AddUnique(@namespace);
            string[] segs = @namespace.Split('.');
            if (segs.Length >= 2) AddUnique(segs[0] + "." + segs[1]);
            AddUnique("Unity.Mathematics");
            AddUnique("Unity.Entities");
            AddUnique("Unity.Collections");
        }
        else if (@namespace.StartsWith("System", StringComparison.Ordinal))
        {
            if (!list.Contains("mscorlib"))       list.Add("mscorlib");
            if (!list.Contains("System.Runtime")) list.Add("System.Runtime");
        }
        return list.ToArray();
    }

    private async Task LoadArrayChildrenAsync(WalkedFieldNode node, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(node.EffectiveAddress)) return;
        if (!InstanceFieldClassifier.TryParseCollectionType(node.Type ?? string.Empty,
                                     out string elementType, out bool isList)) return;

        string arrayAddr = node.EffectiveAddress!;
        if (isList)
        {
            // List<T>._items is the FIRST instance field, so it sits right
            // after the object header (2 * pointerSize): +0x10 on x64, +0x8
            // on x86. The pointer it holds is pointer-sized too — read 8
            // bytes on x64 / 4 on x86. Hardcoding +0x10 / 8-byte read picked
            // up _version (an int) on 32-bit Mono and walkArray'd the garbage,
            // surfacing the bogus "header unreadable" (活俠傳 Mono x86,
            // StatConvertCollectionData._list). Mirror the StringFieldOffsets
            // bitness handling already used for managed-string decode.
            int ptrSize  = GamePointerSize == 4 ? 4 : 8;
            int itemsOff = 2 * ptrSize;
            ReadMemoryResult mem = await _backend.ReadMemoryAsync(
                HexAdd(arrayAddr, itemsOff), ptrSize, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(mem.Error) || string.IsNullOrEmpty(mem.Hex))
            {
                node.LoadHint = $"List._items unreadable: {mem.Error ?? "no hex"}";
                return;
            }
            // ParseLittleEndianHexQword byte-swaps whatever byte count it's
            // given, so the x86 4-byte blob parses to the right address.
            arrayAddr = "0x" + ParseLittleEndianHexQword(mem.Hex!);
        }

        (string ns, string name)? elemSpec = SplitFullName(elementType);
        WalkArrayResult result = await _backend.WalkArrayAsync(
            arrayAddr,
            elementType: elementType,
            limit: 256,
            elementSize: null,
            elementAssembly:  elemSpec is null ? null : (TargetAssembly ?? string.Empty),
            elementNamespace: elemSpec?.ns,
            elementName:      elemSpec?.name,
            ct).ConfigureAwait(true);

        if (!string.IsNullOrEmpty(result.Error))
        {
            node.LoadHint = $"walkArray: {result.Error}";
            return;
        }

        // Refresh the header label with the live count + element type.
        string elemLabel = result.ElementClassName is { Length: > 0 }
            ? (string.IsNullOrEmpty(result.ElementClassNamespace)
                ? result.ElementClassName!
                : $"{result.ElementClassNamespace}.{result.ElementClassName}")
            : elementType;
        node.LoadHint = $"[{result.Length} of {elemLabel}, showing {result.Count}]";

        if (result.Elements is null) return;

        // String-element fallback. The DLL's array-element path
        // (walkArrayElementsInline / CmdWalkArray) treats System.String as a
        // generic reference slot — it returns each slot's raw pointer without
        // the decoded `str` payload (the slot shows "0x…" instead of the
        // text), unlike walkInstance's decodeField which decodes strings
        // inline. When the element type is System.String, decode any slot that
        // came back with a non-null pointer but no string by reading the
        // managed String object directly — same layout the DLL uses for
        // walkInstance (length @ +0x10, UTF-16 payload @ +0x14; 64-bit object
        // header). No-op when the slot already has a decoded string, so a
        // future DLL fix costs nothing here.
        if (string.Equals(elementType, "System.String", StringComparison.Ordinal))
        {
            foreach (WalkedArrayElement e in result.Elements)
            {
                if (e.String is not null || !string.IsNullOrEmpty(e.StringError)) continue;
                if (string.IsNullOrEmpty(e.Ptr) || e.Ptr == "0x0000000000000000") continue;
                (string text, int len)? decoded =
                    await ReadManagedStringAsync(e.Ptr!, ct).ConfigureAwait(true);
                if (decoded is { } d)
                {
                    e.String       = d.text;
                    e.StringLength = d.len;
                }
            }
        }

        foreach (WalkedArrayElement e in result.Elements)
        {
            WalkedFieldNode child = WalkedFieldNode.FromArrayElement(
                e, elementType, result.IsPointer,
                indent: node.IndentLevel + 1, actions: _actions);
            child.Parent = node;
            node.Children.Add(child);
        }
    }

    /// <summary>
    /// Read + decode a managed <c>System.String</c> object at
    /// <paramref name="strObjPtr"/> via two <c>readMemory</c> RPCs. Mirrors the
    /// DLL's string decode: the object header is <c>2 × pointerSize</c> bytes,
    /// then an int32 length, then <c>length</c> UTF-16 code units. So on x64
    /// length sits at +0x10 / chars at +0x14, and on x86 at +0x8 / +0xC.
    /// Returns null on any read failure / implausible length so the caller
    /// falls back to showing the pointer.
    /// </summary>
    private async Task<(string text, int len)?> ReadManagedStringAsync(
        string strObjPtr, CancellationToken ct)
    {
        // 0 (not yet connected) → assume x64, the common case.
        int ptrSize = GamePointerSize == 4 ? 4 : 8;
        (int lenOff, int charsOff) = StringFieldOffsets(ptrSize);
        try
        {
            ReadMemoryResult lenMem = await _backend
                .ReadMemoryAsync(HexAdd(strObjPtr, lenOff), 4, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(lenMem.Error) || string.IsNullOrEmpty(lenMem.Hex))
                return null;
            if (!TryParseLeInt32(lenMem.Hex!, out int len)) return null;
            if (len < 0 || len > 0x100000) return null;     // implausible — bail
            if (len == 0) return (string.Empty, 0);

            ReadMemoryResult chMem = await _backend
                .ReadMemoryAsync(HexAdd(strObjPtr, charsOff), len * 2, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(chMem.Error) || string.IsNullOrEmpty(chMem.Hex))
                return null;
            string? text = DecodeUtf16LeHex(chMem.Hex!, len);
            return text is null ? null : (text, len);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    /// <summary>
    /// Managed <c>System.String</c> field offsets for a given pointer size:
    /// header is <c>2 × pointerSize</c>, then int32 length, then UTF-16 chars.
    /// x64 (8) → (0x10, 0x14); x86 (4) → (0x8, 0xC). Pure — unit-tested.
    /// </summary>
    public static (int lengthOffset, int charsOffset) StringFieldOffsets(int pointerSize)
    {
        int header = 2 * (pointerSize == 4 ? 4 : 8);
        return (header, header + 4);
    }

    /// <summary>
    /// Parse the first 4 bytes of a contiguous little-endian hex blob (2 hex
    /// chars per byte, as <c>readMemory</c> returns) into an int32. Returns
    /// false when the blob is too short or contains non-hex.
    /// </summary>
    public static bool TryParseLeInt32(string hex, out int value)
    {
        value = 0;
        if (hex is null || hex.Length < 8) return false;
        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b0) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b1) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b2) ||
            !byte.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b3))
            return false;
        value = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        return true;
    }

    /// <summary>
    /// Decode <paramref name="charCount"/> UTF-16 code units from a contiguous
    /// little-endian hex blob (2 hex chars per byte, 4 per code unit). Returns
    /// null when the blob is shorter than <c>charCount*4</c> hex chars or has
    /// non-hex content.
    /// </summary>
    public static string? DecodeUtf16LeHex(string hex, int charCount)
    {
        if (charCount < 0) return null;
        if (charCount == 0) return string.Empty;
        if (hex is null || hex.Length < charCount * 4) return null;
        var sb = new System.Text.StringBuilder(charCount);
        for (int i = 0; i < charCount; ++i)
        {
            int off = i * 4;
            if (!byte.TryParse(hex.AsSpan(off, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte lo) ||
                !byte.TryParse(hex.AsSpan(off + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hi))
                return null;
            sb.Append((char)(lo | (hi << 8)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Insert the children list into <see cref="Fields"/> immediately after
    /// the parent. Caller must have already populated <c>node.Children</c>.
    /// Suppresses per-Insert refilter — for a deep array expansion a 256-
    /// slot Insert loop would otherwise hammer the DataGrid 256 times.
    /// </summary>
    private void InsertChildrenIntoFields(WalkedFieldNode node)
    {
        int parentIdx = Fields.IndexOf(node);
        if (parentIdx < 0) return;
        // When no filter is active FilteredFields mirrors Fields 1:1, so we
        // splice the new child rows straight in next to the parent rather
        // than clearing + rebuilding the whole bound collection. The rebuild
        // (RefilterFields → FilteredFields.Clear) reset the DataGrid's scroll
        // offset to the top on every expand — the "expanding charaInfo jumps
        // to page top" bug. An in-place Insert leaves the viewport untouched.
        // bulk = an outer caller (walk repopulate / expansion replay) holds
        // the suppression lock and will refilter once at the end; don't touch
        // FilteredFields here in that case (it's stale mid-rebuild). A
        // non-empty filter falls back to the full RefilterFields pass since
        // the visible set genuinely changes.
        bool bulk   = _refilterSuppressionDepth > 0;
        bool mirror = !bulk && (Filter ?? string.Empty).Length == 0;
        int  filteredAt = mirror ? FilteredFields.IndexOf(node) + 1 : 0;
        bool spliced = mirror && filteredAt > 0;
        _refilterSuppressionDepth++;
        try
        {
            int insertAt = parentIdx + 1;
            foreach (WalkedFieldNode c in node.Children)
            {
                Fields.Insert(insertAt++, c);
                if (spliced) FilteredFields.Insert(filteredAt++, c);
            }
        }
        finally
        {
            _refilterSuppressionDepth--;
        }
        if (bulk) return;            // outer bulk caller refilters once at the end
        if (!spliced) RefilterFields();
    }

    /// <summary>
    /// Recursively remove every descendant of <paramref name="node"/> from
    /// <see cref="Fields"/>. Uses the parent-pointer chain rather than
    /// IndentLevel comparisons so collapsing is robust against odd layouts.
    /// </summary>
    private void CollapseSubtree(WalkedFieldNode node)
    {
        int parentIdx = Fields.IndexOf(node);
        if (parentIdx < 0) return;
        // Mirror the removals straight into FilteredFields when unfiltered so
        // the DataGrid keeps its scroll offset, same rationale as
        // InsertChildrenIntoFields.
        bool bulk   = _refilterSuppressionDepth > 0;
        bool mirror = !bulk && (Filter ?? string.Empty).Length == 0;
        _refilterSuppressionDepth++;
        try
        {
            int i = parentIdx + 1;
            while (i < Fields.Count && IsDescendant(Fields[i], node))
            {
                WalkedFieldNode removed = Fields[i];
                // Mark the row collapsed too so nested expand state doesn't
                // leak when re-expanding the ancestor.
                removed.IsExpanded = false;
                Fields.RemoveAt(i);
                if (mirror) FilteredFields.Remove(removed);
            }
        }
        finally
        {
            _refilterSuppressionDepth--;
        }
        if (bulk) return;
        if (!mirror) RefilterFields();
    }

    private static bool IsDescendant(WalkedFieldNode candidate, WalkedFieldNode ancestor)
    {
        for (WalkedFieldNode? p = candidate.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, ancestor)) return true;
        }
        return false;
    }

    // ---------- Copy CE XML / CE field on a tree node --------------------

    /// <summary>Live progress for the Inspector's Copy CE XML / Copy CE field —
    /// entry count while exporting, then the final line count. A deep walk can
    /// emit tens of thousands of rows and take a few seconds; this is the same
    /// feedback the (removed) class-level Copy CE XML used to show.</summary>
    [ObservableProperty] private string? _copyCeXmlProgress;

    /// <summary>True while a Copy CE XML / Copy CE field export is running. Drives
    /// the toolbar Cancel button's visibility and the <see cref="CancelExportCommand"/>
    /// CanExecute so the user can abort a deep (multi-second) walk.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isExporting;

    /// <summary>Abort the in-progress export. Cancels the shared export token; the
    /// exporter's next pipe RPC throws OperationCanceledException and the command
    /// surfaces "export cancelled".</summary>
    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCts?.Cancel();

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 1;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    /// <summary>
    /// Instance-level "Copy CE XML" — copies a CE table for the whole
    /// currently-selected instance rooted at its address. Mirrors the
    /// Assembly Browser's class-level Copy CE XML, but works wherever the
    /// Inspector was reached from: Find instances, or a cross-tab handoff
    /// (Value Search / Address Lookup / Scene / Snapshot) where there's no
    /// Browser selection to anchor the export. No field row needs to be
    /// selected — that's the gap this fills vs. the per-row "Copy CE XML
    /// rooted here" context-menu item.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyInstance))]
    private async Task CopyCeXmlInstanceAsync()
    {
        if (CopyToClipboardAsync is null || SelectedInstance is null || !HasTarget) return;

        string ns   = TargetNamespace ?? string.Empty;
        string name = TargetName!;

        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;
        IsExporting = true;
        try
        {
            // Same assembly-resolution fallback as the expansion / node-copy
            // paths — the class may live in a different image than the
            // target assembly name suggests (Unity primitives, etc.).
            string[] candidates = BuildAssemblyCandidates(TargetAssembly, ns);
            GetClassInfoResult? info = null;
            string usedAssembly = TargetAssembly ?? "Assembly";
            foreach (string asm in candidates)
            {
                GetClassInfoResult attempt = await _backend
                    .GetClassInfoAsync(asm, ns, name, ct).ConfigureAwait(true);
                if (string.IsNullOrEmpty(attempt.Error)) { info = attempt; usedAssembly = asm; break; }
            }
            if (info is null)
            {
                ErrorMessage = $"getClassInfo({ns}.{name}): not found in {string.Join(", ", candidates)}";
                return;
            }
            ExportOptions options = ResolveExportOptions();
            CopyCeXmlProgress = "exporting…";
            var progress = new Progress<int>(n => CopyCeXmlProgress = $"exporting… {n:N0} entries");
            string xml = await CeXmlExporter.RenderAsync(
                SelectedInstance.Address, info, ancestors: null,
                usedAssembly, options, _backend, ct, progress)
                .ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            CopyCeXmlProgress = $"copied {CountLines(xml):N0} lines";
            _logger.Write(Constants.LogCategoryView,
                $"copy CE XML instance {TargetLabel} @ {SelectedInstance.Address} ({xml.Length} bytes)");
        }
        catch (OperationCanceledException)
        {
            CopyCeXmlProgress = "export cancelled";
        }
        catch (Exception ex)
        {
            CopyCeXmlProgress = "export failed";
            ErrorMessage = $"copy CE XML failed: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"copy CE XML instance failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanCopyInstance() => SelectedInstance is not null && HasTarget;

    /// <summary>"Copy CE field" — each selected node becomes a STANDALONE subtree
    /// rooted at its own ABSOLUTE storage address (paste anywhere in CE).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyNode))]
    private Task CopyCeFieldNodeAsync() => CopyCeFieldNodeCoreAsync(rootAsOffset: false);

    /// <summary>"Copy CE field (relative root)" — same subtree, but the TOP entry
    /// is addressed as <c>+{offset}</c> (the field's offset within its parent)
    /// instead of an absolute address, so it can be pasted directly UNDER the
    /// parent struct in CE and resolve there (e.g. a <c>member</c> field at +28
    /// pasted under its <c>team</c> parent). Children stay relative beneath it.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyNode))]
    private Task CopyCeFieldNodeRelativeAsync() => CopyCeFieldNodeCoreAsync(rootAsOffset: true);

    private async Task CopyCeFieldNodeCoreAsync(bool rootAsOffset)
    {
        if (CopyToClipboardAsync is null) return;

        // Multi-select preferred — falls back to the singular SelectedNode
        // when the View hasn't wired the accessor (tests) or only one row
        // is highlighted. A pointer / string / collection field expands
        // properly; a scalar is a single leaf.
        IReadOnlyList<WalkedFieldNode> nodes = GetSelectedNodes?.Invoke() ?? Array.Empty<WalkedFieldNode>();
        if (nodes.Count == 0 && SelectedNode is not null)
            nodes = new[] { SelectedNode };
        if (nodes.Count == 0) return;

        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;
        IsExporting = true;
        try
        {
            ExportOptions options = ResolveExportOptions();

            var built = new List<(WalkedField field, string absoluteAddress)>(nodes.Count);
            foreach (WalkedFieldNode n in nodes)
            {
                string? rootAddr = NodeRootAddress(n);
                if (string.IsNullOrEmpty(rootAddr)) continue;
                built.Add((n.ToWalkedField(), rootAddr!));
            }
            if (built.Count == 0) return;

            CopyCeXmlProgress = "exporting…";
            var progress = new Progress<int>(n => CopyCeXmlProgress = $"exporting… {n:N0} entries");
            string xml = await CeXmlExporter.RenderFieldNodesLiveAsync(
                built, TargetAssembly, options, _backend, ct, progress, rootAsOffset)
                .ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            CopyCeXmlProgress = $"copied {CountLines(xml):N0} lines";
            string summary = built.Count == 1
                ? $"{nodes[0].Name} @ {(rootAsOffset ? "+" + nodes[0].Offset.ToString("X") : built[0].absoluteAddress)}"
                : $"{built.Count} nodes";
            _logger.Write(Constants.LogCategoryView,
                $"copy CE field node {summary} ({xml.Length} bytes, depth={options.DrilldownDepth}, relRoot={rootAsOffset})");
        }
        catch (OperationCanceledException)
        {
            CopyCeXmlProgress = "export cancelled";
        }
        catch (Exception ex)
        {
            CopyCeXmlProgress = "export failed";
            ErrorMessage = $"copy CE field failed: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"copy CE field node failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanCopyNode()
        => SelectedNode is not null && !string.IsNullOrEmpty(SelectedNode.EffectiveAddress);

    /// <summary>
    /// Absolute address the node's standalone "Copy CE field" entry roots at.
    /// For value-type structs + scalars it's the field's own storage
    /// (<see cref="WalkedFieldNode.Address"/>); for pointer / string /
    /// collection rows the relative-deref emitter expects the FIELD storage
    /// (it derefs +0 itself), so prefer Address and fall back to the
    /// effective (deref'd) address only when Address is missing (synthetic
    /// array slots whose storage isn't tracked).
    /// </summary>
    private static string? NodeRootAddress(WalkedFieldNode n)
        => !string.IsNullOrEmpty(n.Address) ? n.Address : n.EffectiveAddress;

    // ---------- Static-field bridge --------------------------------------

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
                // The walk mutates the UI-bound Fields collection, so it MUST run
                // on the UI thread — the PeriodicTimer continuation is on a pool
                // thread, which previously tripped Avalonia's cross-thread guard
                // ("calling thread cannot access this object…") every tick.
                string addr = SelectedInstance.Address;
                bool ok = await Dispatcher.UIThread
                    .InvokeAsync(() => WalkInstanceAsync(addr))
                    .ConfigureAwait(true);
                if (ct.IsCancellationRequested) break;
                // A genuine walk failure (e.g. the instance was freed, or a
                // thread-affinity error from the game) would otherwise repeat the
                // same error every tick. Stop auto-refresh and leave the error
                // visible; the user can re-arm it once the cause is resolved.
                if (!ok)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { AutoRefreshIntervalMs = 0; });
                    break;
                }
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

    // ---------- Filter ---------------------------------------------------

    /// <summary>
    /// Rebuild <see cref="FilteredFields"/> from <see cref="Fields"/> using
    /// a substring match against Name OR Type (case-insensitive). Filter
    /// applies uniformly to top-level rows AND any expanded children — a
    /// child whose parent doesn't match still appears (with its indent), so
    /// targeted sub-tree searches stay legible. Empty filter mirrors Fields
    /// exactly.
    /// </summary>
    private void RefilterFields()
    {
        if (_refilterSuppressionDepth > 0) return;
        FilteredFields.Clear();
        string filter = Filter ?? string.Empty;
        bool unfiltered = filter.Length == 0;
        foreach (WalkedFieldNode n in Fields)
        {
            if (unfiltered ||
                n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (n.Type?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                FilteredFields.Add(n);
            }
        }
    }

    /// <summary>Phase 29e-8 — tree-aware AOT-safe column sort. Avalonia's
    /// built-in DataGrid sort is reflection-based (no-op under Native AOT)
    /// AND flat — it would scramble the parent/child adjacency that the
    /// expand/collapse machinery relies on. Instead we sort each sibling
    /// group in place (roots, then every node's <c>Children</c>,
    /// recursively) with a typed comparer, then re-flatten
    /// <see cref="Fields"/> in DFS order — descending only into expanded
    /// nodes, exactly like the live tree — and refilter. Children stay
    /// under their parent; a collapsed subtree keeps its sorted order for
    /// when it's re-expanded. Direction is tracked by the view (Avalonia
    /// 12 has no public DataGridColumn.SortDirection).</summary>
    public void SortTree(string member, bool ascending)
    {
        Comparison<WalkedFieldNode>? cmp = member switch
        {
            "Name"         => (a, b) => string.CompareOrdinal(a.Name, b.Name),
            "Offset"       => (a, b) => a.Offset.CompareTo(b.Offset),
            "Address"      => (a, b) => string.CompareOrdinal(a.Address ?? string.Empty, b.Address ?? string.Empty),
            "Type"         => (a, b) => string.CompareOrdinal(a.Type, b.Type),
            "DisplayValue" => (a, b) => string.CompareOrdinal(a.DisplayValue, b.DisplayValue),
            "DeclaredIn"   => (a, b) => string.CompareOrdinal(a.DeclaredIn, b.DeclaredIn),
            _ => null,
        };
        if (cmp is null) return;
        Comparison<WalkedFieldNode> eff = ascending ? cmp : (a, b) => cmp(b, a);

        // Roots are the only nodes with no Parent; every other node in
        // Fields is a descendant of an expanded chain (collapse removes
        // descendants from Fields), so roots + the IsExpanded recursion
        // reconstruct exactly the current visible set.
        var roots = new List<WalkedFieldNode>();
        foreach (WalkedFieldNode n in Fields)
            if (n.Parent is null) roots.Add(n);
        if (roots.Count == 0) return;

        SortSiblingsRecursive(roots, eff);

        _refilterSuppressionDepth++;
        try
        {
            Fields.Clear();
            foreach (WalkedFieldNode r in roots) FlattenSorted(r);
        }
        finally
        {
            _refilterSuppressionDepth--;
        }
        RefilterFields();
    }

    private static void SortSiblingsRecursive(
        List<WalkedFieldNode> nodes, Comparison<WalkedFieldNode> eff)
    {
        nodes.Sort(eff);
        foreach (WalkedFieldNode n in nodes)
            if (n.Children.Count > 0) SortSiblingsRecursive(n.Children, eff);
    }

    private void FlattenSorted(WalkedFieldNode node)
    {
        Fields.Add(node);
        if (node.IsExpanded)
            foreach (WalkedFieldNode c in node.Children) FlattenSorted(c);
    }

    // ---------- Static helpers (type / hex parsing) ----------------------
    //
    // Field-classification helpers (TryParseCollectionType / TryParseClassName /
    // ClassifyInstanceField + the WalkedFieldKind enum) live in the Core
    // project at UnityDumpUI.Core.Services.InstanceFieldClassifier so they
    // can be unit-tested offline without spinning up a VM. SplitFullName
    // stays here for now — it's only used by code that sits next to the VM.

    /// <summary>
    /// Split a managed full-name into (namespace, name). Returns null for
    /// primitives (System.*) where there's no class to walk.
    /// </summary>
    internal static (string ns, string name)? SplitFullName(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return null;
        if (full.StartsWith("System.", StringComparison.Ordinal)) return null;
        int dot = full.LastIndexOf('.');
        if (dot < 0) return (string.Empty, full);
        return (full.Substring(0, dot), full.Substring(dot + 1));
    }

    internal static string HexAdd(string hexAddr, long delta)
    {
        string trimmed = hexAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hexAddr.Substring(2) : hexAddr;
        if (!ulong.TryParse(trimmed, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ulong v))
            return hexAddr;
        return "0x" + (v + (ulong)delta).ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>LE hex blob → host-order value as uppercase hex (no "0x").
    /// Byte-swaps however many whole bytes are present (capped at 8), so it
    /// handles BOTH an 8-byte x64 pointer (16 hex chars) AND a 4-byte x86
    /// pointer (8 hex chars). The old <c>length &lt; 16 → return unchanged</c>
    /// guard silently returned the un-swapped little-endian string for x86
    /// reads, producing a wrong address (the List._items x86 drilldown bug).</summary>
    internal static string ParseLittleEndianHexQword(string leHex)
    {
        if (leHex.Length < 2 || (leHex.Length % 2) != 0) return leHex;
        int useChars = leHex.Length < 16 ? leHex.Length : 16;  // cap at 8 bytes
        var sb = new System.Text.StringBuilder(useChars);
        for (int i = useChars - 2; i >= 0; i -= 2)
            sb.Append(leHex, i, 2);
        return sb.ToString();
    }
}

// =================== Tree-node and row records =============================
//
// WalkedFieldKind moved to UnityDumpUI.Core.Services.InstanceFieldClassifier
// (2026-05-08) to enable offline unit-tests for the kind-routing logic.

/// <summary>
/// Per-row UI callbacks for a <see cref="WalkedFieldNode"/>. Bundles the
/// existing expand toggle with the Phase 11.6 row-action callbacks (copy
/// hex address / pointer / field name to clipboard, hand off to AOBMaker
/// for CE Memory-View navigation). One shared instance per VM lifetime —
/// nodes hold a reference; nulls degrade gracefully (button command is a
/// no-op rather than throwing).
/// </summary>
public sealed class WalkedFieldActions
{
    /// <summary>Expand / collapse a node — drives the chevron in the Name column.</summary>
    public Func<WalkedFieldNode, Task>? Toggle { get; init; }

    /// <summary>Copy a string to the clipboard. Wired to the same helper
    /// the existing Copy CE XML / Copy CE field commands use.</summary>
    public Func<string, Task>? CopyText { get; init; }

    /// <summary>
    /// Navigate Cheat Engine's open Memory View hex pane to the given
    /// address via the AOBMaker bridge (named pipe). Returns false when
    /// AOBMaker isn't running. Null when the bridge isn't wired at all.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? NavigateHex { get; init; }
}

/// <summary>
/// One row in the hierarchical field grid. Doubles as both top-level
/// instance fields and child rows produced by lazy expansion.
/// </summary>
public sealed partial class WalkedFieldNode : ObservableObject, IInspectorFieldNode
{
    // Phase 21a — adapter for the offset resolver in Core. Cumulative-offset
    // resolution needs a parent-chain walk; the resolver lives in Core (so it
    // can be unit-tested offline), and Core can't see this UI type — hence
    // the interface seam. ParentNode forwards to the existing Parent field
    // typed as the resolver's contract; Offset and Kind are already public.
    IInspectorFieldNode? IInspectorFieldNode.ParentNode => Parent;


    public WalkedFieldNode(string name, string type, int offset, string declaredIn,
                            string? address, string displayValue, string? ptr,
                            int indentLevel, WalkedFieldKind kind,
                            string? effectiveAddress,
                            WalkedFieldActions? actions,
                            WalkedField? source = null)
    {
        Name             = name;
        Type             = type;
        Offset           = offset;
        DeclaredIn       = declaredIn;
        Address          = address;
        DisplayValue     = displayValue;
        Ptr              = ptr;
        IndentLevel      = indentLevel;
        Kind             = kind;
        EffectiveAddress = effectiveAddress;
        _actions         = actions;
        _source          = source;
    }

    private readonly WalkedFieldActions? _actions;

    /// <summary>
    /// The wire <see cref="WalkedField"/> this node was built from (instance
    /// fields) or a synthesized equivalent (array slots). Carries the
    /// classification signals (IsEnum / IsValueType / Value / String /
    /// StringLength) that the plain node properties drop, so "Copy CE field"
    /// can re-render the node through the live exporter and get the SAME shape
    /// the in-place export produces. Null only for hand-built test nodes.
    /// </summary>
    private readonly WalkedField? _source;

    /// <summary>
    /// Reconstruct a <see cref="WalkedField"/> for the live CE exporter. Uses
    /// the stashed wire source when present (faithful enum/string/struct
    /// classification); otherwise best-effort from the node's own properties.
    /// </summary>
    public WalkedField ToWalkedField()
        => _source ?? new WalkedField
        {
            Name    = Name,
            Type    = Type,
            Offset  = Offset < 0 ? 0 : Offset,
            From    = DeclaredIn,
            Address = Address,
            Ptr     = Ptr,
        };

    public string  Name         { get; }
    public string  Type         { get; }
    public int     Offset       { get; }
    public string  OffsetHex    => Offset >= 0 ? $"0x{Offset:X}" : string.Empty;
    public string  DeclaredIn   { get; }
    public string? Address      { get; }
    public string  DisplayValue { get; }
    public string? Ptr          { get; }
    public int     IndentLevel  { get; }
    public WalkedFieldKind Kind { get; }

    /// <summary>
    /// Address used as the root for child loads + Copy CE XML. For pointer
    /// fields this is the dereferenced object pointer (Ptr); for arrays
    /// it's also Ptr (the T[] header); for top-level value-type leaves
    /// it's the field's storage address; null for unaddressable values.
    /// </summary>
    public string? EffectiveAddress { get; }

    /// <summary>Lazily-loaded child rows. Populated by the VM on first expand.</summary>
    public List<WalkedFieldNode> Children { get; } = new();
    public WalkedFieldNode? Parent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isLoading;

    public bool IsLoaded { get; set; }

    /// <summary>Optional one-line note from the last load attempt (count, error).</summary>
    [ObservableProperty] private string? _loadHint;

    public bool CanExpand => Kind is
        WalkedFieldKind.PointerToClass or
        WalkedFieldKind.ValueStruct    or
        WalkedFieldKind.Collection     or
        WalkedFieldKind.ArraySlotPointer or
        WalkedFieldKind.Dictionary;

    /// <summary>Embedded child fields the DLL fanned out inline (Dictionary
    /// key→value pairs). The VM consumes these directly on expand — no RPC.
    /// Null for nodes with no embedded payload.</summary>
    public IReadOnlyList<WalkedField>? EmbeddedFields => _source?.Fields;

    /// <summary>"▶" / "▼" / "⋯" / " " — drives the chevron in the Name column.</summary>
    public string ExpandGlyph
    {
        get
        {
            if (!CanExpand) return " ";
            if (IsLoading)  return "⋯";
            return IsExpanded ? "▼" : "▶";
        }
    }

    /// <summary>Pixel indent for the Name cell — IndentLevel * 14 px.</summary>
    public double IndentPx => IndentLevel * 14.0;

    [RelayCommand]
    private Task ToggleAsync() => _actions?.Toggle?.Invoke(this) ?? Task.CompletedTask;

    // ---------- Per-row action commands (Phase 11.6) ---------------------

    /// <summary>True when the field has a readable storage address — drives
    /// the addr / HEX button IsEnabled bindings.</summary>
    public bool CanCopyAddr => !string.IsNullOrEmpty(Address);

    /// <summary>True when the wire payload reported a non-null pointer
    /// value (string fields, pointer-to-class fields, T[] / List handles).
    /// Drives the ptr button IsEnabled binding.</summary>
    public bool CanCopyPtr =>
        !string.IsNullOrEmpty(Ptr) && Ptr != "0x0000000000000000";

    /// <summary>True when there's a row-rooted address worth handing to CE.
    /// HEX targets the field's OWN storage (the Address column) — what the user
    /// sees on the row — falling back to the deref'd address only for synthetic
    /// slots with no tracked storage. The deref'd target is the "→ptr" button.
    /// Drives the HEX button.</summary>
    public bool CanNavigateHex =>
        !string.IsNullOrEmpty(Address) || !string.IsNullOrEmpty(EffectiveAddress);

    [RelayCommand]
    private Task CopyAddrAsync() =>
        (_actions?.CopyText is { } f && !string.IsNullOrEmpty(Address))
            ? f(Address!)
            : Task.CompletedTask;

    [RelayCommand]
    private Task CopyPtrAsync() =>
        (_actions?.CopyText is { } f && CanCopyPtr)
            ? f(Ptr!)
            : Task.CompletedTask;

    [RelayCommand]
    private Task CopyNameAsync() =>
        (_actions?.CopyText is { } f && !string.IsNullOrEmpty(Name))
            ? f(Name)
            : Task.CompletedTask;

    [RelayCommand]
    private Task NavigateHexAsync()
    {
        // The field's OWN storage address (the Address column the user clicked
        // on the row) — NOT the deref'd pointer. For a List / array / pointer
        // field that means the slot holding the pointer, not the object it
        // points at (that's the "→ptr" button). Fall back to EffectiveAddress
        // only for synthetic slots whose storage isn't tracked.
        string? target = !string.IsNullOrEmpty(Address) ? Address : EffectiveAddress;
        return (_actions?.NavigateHex is { } f && !string.IsNullOrEmpty(target))
            ? f(target!, default)
            : Task.FromResult(false);
    }

    /// <summary>True when the row carries a non-null managed pointer value worth
    /// browsing in CE — drives the "→ptr" AOBMaker button.</summary>
    public bool CanNavigatePtrHex => CanCopyPtr;

    /// <summary>
    /// Hand the row's POINTER VALUE (the address it points AT — string object,
    /// reference target, IntPtr handle) to CE's Memory View via AOBMaker, as
    /// opposed to <see cref="NavigateHexAsync"/> which browses the field's own
    /// storage / deref'd group address. Lets the user inspect where a ptr leads
    /// even when the field decode failed (e.g. an unreadable String header).
    /// </summary>
    [RelayCommand]
    private Task NavigatePtrHexAsync() =>
        (_actions?.NavigateHex is { } f && CanCopyPtr)
            ? f(Ptr!, default)
            : Task.FromResult(false);

    // ---------- Constructors from wire payloads --------------------------

    public static WalkedFieldNode FromInstanceField(
        WalkedField f, int indent, WalkedFieldActions actions)
    {
        string display = FormatDisplay(f, out bool isPointerValue);
        WalkedFieldKind kind = InstanceFieldClassifier.Classify(f, isPointerValue);
        string? effective = ComputeEffectiveAddress(f, kind);
        // For inline value-type structs the wire `ptr` is meaningless garbage
        // (decodeField's catch-all reads 8 bytes opaque) — replace the
        // misleading hex value with a hint that the row is expandable.
        if (kind == WalkedFieldKind.ValueStruct)
        {
            display = "(struct — expand)";
        }
        else if (f.Dict)
        {
            // Dictionary<K,V> fanned out by the DLL — show the live entry count
            // (the wire `ptr` was erased on success). Expandable when > 0.
            string unit = f.DictCount == 1 ? "entry" : "entries";
            display = $"(dict — {f.DictCount} {unit}{(f.DictTruncated ? "+" : string.Empty)})";
        }
        else if (f.DictError is { Length: > 0 })
        {
            display = $"(dict — {f.DictError})";
        }
        else
        {
            display = MaybePerturbDisplay(display, f.Name, f.Offset);
        }
        return new WalkedFieldNode(
            f.Name,
            f.Type ?? string.Empty,
            f.Offset,
            f.From ?? string.Empty,
            f.Address,
            display,
            f.Ptr,
            indent,
            kind,
            effective,
            actions,
            source: f);
    }

    public static WalkedFieldNode FromArrayElement(
        WalkedArrayElement e, string elementType, bool isPointerArray,
        int indent, WalkedFieldActions actions)
    {
        string display = FormatArrayElement(e);
        display = MaybePerturbDisplay(display, $"[{e.Index}]", e.Index);
        WalkedFieldKind kind;
        string? effective;
        if (isPointerArray)
        {
            // The slot stores a pointer to the element object; expand that
            // pointer (recursive walkInstance) using elementType's class.
            kind      = InstanceFieldClassifier.TryParseClassName(elementType, out _, out _)
                          ? WalkedFieldKind.ArraySlotPointer
                          : WalkedFieldKind.ArraySlotLeaf;
            effective = e.Ptr;
        }
        else if (InstanceFieldClassifier.TryParseClassName(elementType, out _, out _)
                 && !string.IsNullOrEmpty(e.Address))
        {
            // Inline value-type struct slot (e.g. PlayerData[], Vector3[]) — the
            // struct data lives at e.Address with no object header. Expand it
            // exactly like a ValueStruct field: a name-based walkInstance at that
            // address iterates the struct's fields by offset. (Primitive / enum
            // element types fail TryParseClassName and stay leaves below.)
            kind      = WalkedFieldKind.ValueStruct;
            effective = e.Address;
            display   = "(struct — expand)";
        }
        else
        {
            // Primitive / enum-as-int value slot — element data lives at
            // e.Address; primitive leaf (no walkable managed fields).
            kind      = WalkedFieldKind.ArraySlotLeaf;
            effective = e.Address;
        }

        // Synthesize a WalkedField equivalent so "Copy CE field" on an array
        // slot re-renders through the live exporter with the same decode (a
        // String slot expands to its length/chars pair, a ref slot drills).
        var slotSource = new WalkedField
        {
            Name         = $"[{e.Index}]",
            Type         = elementType,
            Offset       = 0,
            Address      = e.Address,
            Ptr          = e.Ptr,
            String       = e.String,
            StringLength = e.StringLength,
            Value        = e.Value,
        };

        return new WalkedFieldNode(
            name:        $"[{e.Index}]",
            type:        elementType,
            offset:      -1, // synthetic
            declaredIn:  string.Empty,
            address:     e.Address,
            displayValue: display,
            ptr:         e.Ptr,
            indentLevel: indent,
            kind:        kind,
            effectiveAddress: effective,
            actions:     actions,
            source:      slotSource);
    }

    // ClassifyInstanceField moved to UnityDumpUI.Core.Services.InstanceFieldClassifier.Classify
    // (2026-05-08). Call sites in this file use InstanceFieldClassifier.Classify directly.

    private static string? ComputeEffectiveAddress(WalkedField f, WalkedFieldKind kind)
        => kind switch
        {
            // PointerToClass / Collection follow the deref'd pointer.
            WalkedFieldKind.PointerToClass or WalkedFieldKind.Collection => f.Ptr,
            // ValueStruct uses the field's own storage address — no deref,
            // since the value type IS the data at that location.
            WalkedFieldKind.ValueStruct => f.Address,
            _ => f.Address ?? f.Ptr,
        };

    private static string FormatDisplay(WalkedField f, out bool isPointer)
    {
        isPointer = false;
        if (!string.IsNullOrEmpty(f.Error))      return $"<error: {f.Error}>";
        if (!string.IsNullOrEmpty(f.StringError)) return $"<string_error: {f.StringError}> {f.Ptr}";
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

    private static string FormatArrayElement(WalkedArrayElement e)
    {
        if (!string.IsNullOrEmpty(e.Error))       return $"<error: {e.Error}>";
        if (!string.IsNullOrEmpty(e.StringError)) return $"<string_error: {e.StringError}>";
        if (e.String is not null)
        {
            string len = e.StringLength is int sl ? $" (len {sl})" : string.Empty;
            return $"\"{e.String}\"{len}";
        }
        if (e.Value is JsonElement v)
        {
            return v.ValueKind switch
            {
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Null   => "null",
                _                    => v.GetRawText(),
            };
        }
        if (!string.IsNullOrEmpty(e.Ptr)) return e.Ptr!;
        if (!string.IsNullOrEmpty(e.Hex)) return e.Hex!;
        return string.Empty;
    }

    // On intact / dev builds all token readers return 0 and this is a pass-
    // through. On a tampered binary ~1/11 numeric rows have their low byte
    // perturbed by the combined factor — same row corrupts the same way
    // across refreshes (deterministic per-row seed), so the misbehaviour
    // looks like a "this field's reader is buggy", not a tamper response.
    // Non-numeric strings (errors, "(struct — expand)", quoted strings,
    // bool literals) pass through untouched.
    //
    // No `if (factor == 0) return` branch: the mask is an OR-fold of all four
    // factor bytes, so it is 0 IFF factor is 0 and the XOR below is then a pure
    // identity — there is no single early-out for an attacker to NOP. The
    // factor is read via Combined() (inlined) OR a direct reader so zeroing one
    // path alone doesn't disarm it.
    private static string MaybePerturbDisplay(string display, string? rowName, int seedSalt)
    {
        if (string.IsNullOrEmpty(display)) return display;

        int factor = TokenAggregator.Combined() | BuildArtifactToken.Read();
        byte mask = (byte)(factor | (factor >> 8) | (factor >> 16) | (factor >> 24));

        uint seed = (uint)((rowName?.GetHashCode() ?? 0) ^ seedSalt ^ unchecked((int)0x5A5A5A5A));
        if (seed % 11 != 0) return display;

        // Hex form "0x..." — XOR the low byte (identity when mask == 0).
        if (display.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(display.AsSpan(2), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out ulong hexVal))
        {
            return "0x" + (hexVal ^ mask).ToString("X", CultureInfo.InvariantCulture);
        }

        // Decimal integer (signed) — perturb low byte while preserving sign.
        if (long.TryParse(display, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out long decVal))
        {
            long perturbed = (decVal & ~0xFFL) | ((decVal & 0xFFL) ^ mask);
            return perturbed.ToString(CultureInfo.InvariantCulture);
        }

        return display;
    }
}

/// <summary>One numeric top-level field offered by the field-filter builder
/// dropdown. Carries the offset + managed type needed to compose a predicate.</summary>
public sealed class FilterFieldOption
{
    public FilterFieldOption(string name, int offset, string type)
    {
        Name   = name;
        Offset = offset;
        Type   = type;
    }
    public string Name   { get; }
    public int    Offset { get; }
    public string Type   { get; }

    private string ShortType => Type.StartsWith("System.", StringComparison.Ordinal)
        ? Type.Substring("System.".Length) : Type;

    /// <summary>Dropdown label, e.g. <c>level_  (+0x3C · Int32)</c>.</summary>
    public string Display =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}  (+0x{Offset:X} · {ShortType})");

    /// <summary>AutoCompleteBox uses the item's ToString for both its
    /// type-to-filter matching and the selected-item text. Returning Display
    /// lets the user type any part of the name (or offset / type) to narrow
    /// the picker — not just scroll offset order.</summary>
    public override string ToString() => Display;
}

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
    public string? Hex     { get; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Field} @ {Address} = {Hex ?? "<unreadable>"}");
}
