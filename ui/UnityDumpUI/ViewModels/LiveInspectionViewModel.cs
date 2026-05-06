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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

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

    /// <summary>
    /// View-supplied clipboard write — wired by MainWindow code-behind.
    /// Mirrors the AssemblyBrowser pattern so both VMs stay UI-agnostic.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

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

    // ---------- Walked fields (hierarchical, flat-with-indent) -----------

    /// <summary>
    /// Visible rows in display order. Children of an expanded node are
    /// inserted as siblings immediately after the parent and removed on
    /// collapse. Each row's <see cref="WalkedFieldNode.IndentLevel"/> drives
    /// the visual indent; sorting is intentionally disabled in the view to
    /// preserve parent-child adjacency.
    /// </summary>
    public ObservableCollection<WalkedFieldNode> Fields { get; } = new();

    [ObservableProperty] private string _walkSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCeXmlNodeCommand))]
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

    /// <summary>
    /// Root walk for a fresh selected instance. Replaces the entire
    /// <see cref="Fields"/> list with top-level rows. When a previous walk
    /// had subtrees expanded, the same expansion *paths* (by field name)
    /// are re-walked transparently so auto-refresh doesn't collapse the
    /// user's drilldown view on every tick.
    /// </summary>
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
            // Snapshot which root-level nodes were expanded so we can replay.
            HashSet<string> expandedRoots = new(StringComparer.Ordinal);
            foreach (WalkedFieldNode n in Fields)
            {
                if (n.IndentLevel == 0 && n.IsExpanded)
                    expandedRoots.Add(n.Name);
            }

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
                    Fields.Add(WalkedFieldNode.FromInstanceField(f, indent: 0,
                                                                 toggle: ToggleExpandAsync));
                }
            }
            WalkSummary = $"depth={result.Depth} count={result.Count}";

            // Replay top-level expansions only. Deeper-than-one expansions
            // come back collapsed — keeping the auto-refresh cost bounded
            // (one extra pipe RPC per expanded root, not the whole tree).
            if (expandedRoots.Count > 0)
            {
                foreach (WalkedFieldNode n in Fields.ToArray())
                {
                    if (n.IndentLevel == 0 && expandedRoots.Contains(n.Name) && n.CanExpand)
                    {
                        await ToggleExpandAsync(n).ConfigureAwait(true);
                    }
                }
            }

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
        if (res is null && TryParseClassName(node.Type, out string ns, out string name))
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
        foreach (WalkedField f in res.Fields)
        {
            WalkedFieldNode child = WalkedFieldNode.FromInstanceField(
                f, indent: node.IndentLevel + 1, toggle: ToggleExpandAsync);
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
        if (!TryParseCollectionType(node.Type ?? string.Empty,
                                     out string elementType, out bool isList)) return;

        string arrayAddr = node.EffectiveAddress!;
        if (isList)
        {
            // List<T>._items at +0x10.
            ReadMemoryResult mem = await _backend.ReadMemoryAsync(
                HexAdd(arrayAddr, 0x10), 8, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(mem.Error) || string.IsNullOrEmpty(mem.Hex))
            {
                node.LoadHint = $"List._items unreadable: {mem.Error ?? "no hex"}";
                return;
            }
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
        foreach (WalkedArrayElement e in result.Elements)
        {
            WalkedFieldNode child = WalkedFieldNode.FromArrayElement(
                e, elementType, result.IsPointer,
                indent: node.IndentLevel + 1, toggle: ToggleExpandAsync);
            child.Parent = node;
            node.Children.Add(child);
        }
    }

    /// <summary>
    /// Insert the children list into <see cref="Fields"/> immediately after
    /// the parent. Caller must have already populated <c>node.Children</c>.
    /// </summary>
    private void InsertChildrenIntoFields(WalkedFieldNode node)
    {
        int parentIdx = Fields.IndexOf(node);
        if (parentIdx < 0) return;
        int insertAt = parentIdx + 1;
        foreach (WalkedFieldNode c in node.Children)
        {
            Fields.Insert(insertAt++, c);
        }
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
        int i = parentIdx + 1;
        while (i < Fields.Count && IsDescendant(Fields[i], node))
        {
            // Mark the row collapsed too so nested expand state doesn't
            // leak when re-expanding the ancestor.
            Fields[i].IsExpanded = false;
            Fields.RemoveAt(i);
        }
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

    [RelayCommand(CanExecute = nameof(CanCopyNode))]
    private async Task CopyCeXmlNodeAsync()
    {
        if (CopyToClipboardAsync is null || SelectedNode is null) return;
        if (string.IsNullOrEmpty(SelectedNode.EffectiveAddress)) return;
        if (!TryParseClassName(SelectedNode.Type, out string ns, out string name))
        {
            // Not a class-rooted node — fall back to a single leaf entry.
            string xml = WrapLeafXml(SelectedNode);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            return;
        }

        try
        {
            // Same assembly-resolution fallback as the inspector's expansion
            // path: try the parent's assembly first, then well-known
            // fallbacks. Critical for value-type substructs whose type
            // (UnityEngine.Vector3 etc.) lives in a different assembly.
            string[] candidates = BuildAssemblyCandidates(TargetAssembly, ns);
            GetClassInfoResult? info = null;
            string usedAssembly = TargetAssembly ?? "Assembly";
            foreach (string asm in candidates)
            {
                GetClassInfoResult attempt = await _backend
                    .GetClassInfoAsync(asm, ns, name, default).ConfigureAwait(true);
                if (string.IsNullOrEmpty(attempt.Error)) { info = attempt; usedAssembly = asm; break; }
            }
            if (info is null)
            {
                ErrorMessage = $"getClassInfo({ns}.{name}): not found in {string.Join(", ", candidates)}";
                return;
            }
            ExportOptions options = new(
                DrilldownDepth: 1,
                ArrayLimit:     16,
                CollapsePointerNodes: true,
                LiveData: true);
            string xml = await CeXmlExporter.RenderAsync(
                SelectedNode.EffectiveAddress!, info, ancestors: null,
                usedAssembly, options, _backend, default)
                .ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            _logger.Write(Constants.LogCategoryView,
                $"copy CE XML node {SelectedNode.Name} @ {SelectedNode.EffectiveAddress} ({xml.Length} bytes)");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"copy CE XML failed: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"copy CE XML node failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyNode))]
    private async Task CopyCeFieldNodeAsync()
    {
        if (CopyToClipboardAsync is null || SelectedNode is null) return;
        string xml = WrapLeafXml(SelectedNode);
        try
        {
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            _logger.Write(Constants.LogCategoryView,
                $"copy CE field node {SelectedNode.Name} @ {SelectedNode.EffectiveAddress ?? "?"} ({xml.Length} bytes)");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"copy CE field failed: {ex.Message}";
        }
    }

    private bool CanCopyNode()
        => SelectedNode is not null && !string.IsNullOrEmpty(SelectedNode.EffectiveAddress);

    /// <summary>
    /// Tiny single-entry CE table for nodes that don't have a resolvable
    /// class type (primitives, pointer-to-anonymous, array slots that
    /// shouldn't recurse further). Uses an absolute address so paste-into-CE
    /// just works without an existing scope.
    /// </summary>
    private string WrapLeafXml(WalkedFieldNode node)
    {
        string addr = node.EffectiveAddress ?? "0x0";
        string desc = $"{node.Name} : {node.Type ?? "?"}";
        // Pick the right CE VariableType + display flags from the managed
        // type — without this every leaf got "4 Bytes" + hex, which is wrong
        // for signed integers (Int32 → CE shows the signed value with
        // ShowAsSigned=1, no hex) and floats (Float type).
        ManagedTypeShape shape = UnityTypeMap.Resolve(node.Type);
        (bool hex, bool signed) = UnityTypeMap.CeDisplay(node.Type);
        string vartype = shape.CeTypeKeyword;
        var sb = new System.Text.StringBuilder(512);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<CheatTable>\n  <CheatEntries>\n");
        sb.Append("    <CheatEntry>\n");
        sb.Append("      <ID>0</ID>\n");
        sb.Append("      <Description>\"").Append(System.Net.WebUtility.HtmlEncode(desc)).Append("\"</Description>\n");
        if (hex)
            sb.Append("      <ShowAsHex>1</ShowAsHex>\n");
        sb.Append("      <ShowAsSigned>").Append(signed ? '1' : '0').Append("</ShowAsSigned>\n");
        sb.Append("      <VariableType>").Append(vartype).Append("</VariableType>\n");
        sb.Append("      <Address>").Append(addr).Append("</Address>\n");
        sb.Append("    </CheatEntry>\n");
        sb.Append("  </CheatEntries>\n</CheatTable>\n");
        return sb.ToString();
    }

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

    // ---------- Static helpers (type / hex parsing) ----------------------

    /// <summary>Parses <c>T[]</c> and <c>List`1&lt;T&gt;</c> wrappers.</summary>
    internal static bool TryParseCollectionType(string typeName, out string elementType, out bool isList)
    {
        isList = false;
        elementType = string.Empty;
        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            elementType = typeName.Substring(0, typeName.Length - 2);
            return true;
        }
        const string listPrefix = "System.Collections.Generic.List`1<";
        if (typeName.StartsWith(listPrefix, StringComparison.Ordinal)
            && typeName.EndsWith(">", StringComparison.Ordinal))
        {
            elementType = typeName.Substring(listPrefix.Length, typeName.Length - listPrefix.Length - 1).Trim();
            isList = true;
            return true;
        }
        return false;
    }

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

    internal static bool TryParseClassName(string? type, out string ns, out string name)
    {
        ns = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(type)) return false;
        if (type.StartsWith("System.", StringComparison.Ordinal)) return false;
        if (type.EndsWith("[]", StringComparison.Ordinal)) return false;

        // Strip the generic instantiation suffix BEFORE splitting at the dot —
        // for `MainGame.ClassAry<MainGame.CARRYITEM>` we want the last `.` in
        // `MainGame.ClassAry`, not the one inside the generic argument. Without
        // this, ns/name come out as `MainGame.ClassAry<MainGame` / `CARRYITEM>`
        // and the DLL's FindClass(ns, name) lookup fails silently.
        int lt = type.IndexOf('<');
        string outer = lt > 0 ? type.Substring(0, lt) : type;

        int dot = outer.LastIndexOf('.');
        if (dot < 0)
        {
            name = outer;
            return true;
        }
        ns = outer.Substring(0, dot);
        name = outer.Substring(dot + 1);
        return true;
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

    /// <summary>LE hex blob → host-order qword as uppercase hex (no "0x").</summary>
    internal static string ParseLittleEndianHexQword(string leHex)
    {
        if (leHex.Length < 16) return leHex;
        var sb = new System.Text.StringBuilder(16);
        for (int i = 14; i >= 0; i -= 2)
            sb.Append(leHex, i, 2);
        return sb.ToString();
    }
}

// =================== Tree-node and row records =============================

public enum WalkedFieldKind
{
    /// <summary>Primitive value or unrecognised pointer; no children.</summary>
    Leaf,
    /// <summary>Pointer to a managed class instance — expand via walkInstance on the dereferenced address.</summary>
    PointerToClass,
    /// <summary>
    /// Inline value-type struct (e.g. <c>Vector3</c>, custom struct) — its fields
    /// live at the parent address + offset (no header). Expand via
    /// <c>walkInstance</c> at the field's storage address; the DLL's
    /// walkInstance iterates fields by offset starting there, so it works
    /// for both class instances (with header) and value types (without).
    /// </summary>
    ValueStruct,
    /// <summary>T[] / List&lt;T&gt; — expand via walkArray.</summary>
    Collection,
    /// <summary>Pointer slot inside an expanded array — recursively expandable like PointerToClass.</summary>
    ArraySlotPointer,
    /// <summary>Value-type slot inside an expanded array — primitive leaf for now.</summary>
    ArraySlotLeaf,
}

/// <summary>
/// One row in the hierarchical field grid. Doubles as both top-level
/// instance fields and child rows produced by lazy expansion.
/// </summary>
public sealed partial class WalkedFieldNode : ObservableObject
{
    public WalkedFieldNode(string name, string type, int offset, string declaredIn,
                            string? address, string displayValue, string? ptr,
                            int indentLevel, WalkedFieldKind kind,
                            string? effectiveAddress,
                            Func<WalkedFieldNode, Task>? toggle)
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
        _toggle          = toggle;
    }

    private readonly Func<WalkedFieldNode, Task>? _toggle;

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
        WalkedFieldKind.ArraySlotPointer;

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
    private Task ToggleAsync() => _toggle?.Invoke(this) ?? Task.CompletedTask;

    // ---------- Constructors from wire payloads --------------------------

    public static WalkedFieldNode FromInstanceField(
        WalkedField f, int indent, Func<WalkedFieldNode, Task> toggle)
    {
        string display = FormatDisplay(f, out bool isPointerValue);
        WalkedFieldKind kind = ClassifyInstanceField(f, isPointerValue);
        string? effective = ComputeEffectiveAddress(f, kind);
        // For inline value-type structs the wire `ptr` is meaningless garbage
        // (decodeField's catch-all reads 8 bytes opaque) — replace the
        // misleading hex value with a hint that the row is expandable.
        if (kind == WalkedFieldKind.ValueStruct)
        {
            display = "(struct — expand)";
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
            toggle);
    }

    public static WalkedFieldNode FromArrayElement(
        WalkedArrayElement e, string elementType, bool isPointerArray,
        int indent, Func<WalkedFieldNode, Task> toggle)
    {
        string display = FormatArrayElement(e);
        WalkedFieldKind kind;
        string? effective;
        if (isPointerArray)
        {
            // The slot stores a pointer to the element object; expand that
            // pointer (recursive walkInstance) using elementType's class.
            kind      = LiveInspectionViewModel.TryParseClassName(elementType, out _, out _)
                          ? WalkedFieldKind.ArraySlotPointer
                          : WalkedFieldKind.ArraySlotLeaf;
            effective = e.Ptr;
        }
        else
        {
            // Value-type slot — element data lives at e.Address; primitive
            // leaf for now (no inline struct expansion in this revision).
            kind      = WalkedFieldKind.ArraySlotLeaf;
            effective = e.Address;
        }

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
            toggle:      toggle);
    }

    private static WalkedFieldKind ClassifyInstanceField(WalkedField f, bool isPointerValue)
    {
        // Collection takes priority — its Type is `T[]` / `List<T>`,
        // distinguishable without inspecting the wire payload.
        if (!string.IsNullOrEmpty(f.Type) &&
            LiveInspectionViewModel.TryParseCollectionType(f.Type, out _, out _))
        {
            // Empty pointer = empty array, no expansion possible.
            if (string.IsNullOrEmpty(f.Ptr) || f.Ptr == "0x0000000000000000")
                return WalkedFieldKind.Leaf;
            return WalkedFieldKind.Collection;
        }
        // Inline value-type substruct: when UnityTypeMap classifies the
        // managed type as a value type (Vector3, Quaternion, Color, Bounds,
        // …) the field's storage IS the struct — no pointer to deref. The
        // DLL still emits a `ptr` field for it (decodeField's catch-all
        // reads 8 bytes as opaque pointer), but that's misleading garbage
        // for value types. Override before the pointer-classification path
        // so the chevron points at walkInstance(field.Address, type) for
        // these — which the DLL handles correctly because its walkInstance
        // doesn't assume an object header.
        if (!string.IsNullOrEmpty(f.Type)
            && !UnityTypeMap.IsPointerType(f.Type)
            && LiveInspectionViewModel.TryParseClassName(f.Type, out _, out _)
            && !string.IsNullOrEmpty(f.Address))
        {
            return WalkedFieldKind.ValueStruct;
        }
        // Pointer-to-class: the wire layer reports a Ptr value AND the
        // field type resolves to a non-primitive class.
        if (isPointerValue
            && !string.IsNullOrEmpty(f.Ptr)
            && f.Ptr != "0x0000000000000000"
            && LiveInspectionViewModel.TryParseClassName(f.Type, out _, out _))
        {
            return WalkedFieldKind.PointerToClass;
        }
        return WalkedFieldKind.Leaf;
    }

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
