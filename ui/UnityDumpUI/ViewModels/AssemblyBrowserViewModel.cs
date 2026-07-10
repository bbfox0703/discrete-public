// AssemblyBrowserViewModel.cs — Phase 8 panels coordinator.
//
// Owns three lists driven by selection cascading:
//   1. Assemblies                       (left pane)
//   2. Classes (of selected assembly)   (middle pane top)
//   3. Fields + Methods (of selected class, optionally including inherited
//      members from the parent chain)   (middle pane bottom + right pane)
//
// Each list has its own substring filter (case-insensitive). The filtered
// views are independent ObservableCollections recomputed on filter or source
// changes — Avalonia's ListBox/DataGrid virtualizes them per Phase 8 perf
// gate ("5000+ classes without UI freeze").
//
// AOT mandate: CommunityToolkit.Mvvm source generators only. No reflection,
// no LINQ-to-Reflection, no INotifyDataErrorInfo derivatives that would pull
// in System.Reflection.
//
// Backend calls are awaited directly (no Task.Run): the pipe is opened with
// PipeOptions.Asynchronous so ReadAsync/WriteAsync are truly async and never
// block the UI thread while a request is in flight. These browse RPCs ride
// the INTERACTIVE pipe connection (see BackendAdapter), so they run in
// parallel with any heavy op (snapshot / value-scan) on the bulk connection
// instead of queueing behind it.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.ViewModels;

public sealed partial class AssemblyBrowserViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _classesCts;
    private CancellationTokenSource? _exportCts;

    /// <summary>
    /// Most recent class-info chain (oldest-first ancestors + self at the
    /// end), captured by <see cref="LoadClassDetailAsync"/>. Drives the
    /// export commands without re-issuing pipe calls.
    /// </summary>
    private List<GetClassInfoResult> _currentChain = new();

    /// <summary>
    /// View-supplied save-file picker. Returns the chosen absolute path or
    /// null if the user cancelled. Decouples the VM from Avalonia
    /// <c>Window</c> / <c>StorageProvider</c> types.
    /// </summary>
    public Func<string /*suggestedName*/, string /*description*/, string /*ext*/, Task<string?>>? SaveFileAsync { get; set; }

    /// <summary>
    /// View-supplied clipboard write. Wired by MainWindow code-behind to
    /// the Avalonia top-level Clipboard so the VM stays UI-agnostic.
    /// </summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>Live status for the Copy CE XML button — "exporting… N entries"
    /// during a (potentially large/slow) live export, then "copied N lines".
    /// Shown next to the Export options button. Empty until the first export.</summary>
    [ObservableProperty] private string _copyCeXmlProgress = string.Empty;

    /// <summary>True while a Copy CE XML / Copy CE field export is running. Drives
    /// the Cancel button next to the progress label and the
    /// <see cref="CancelExportCommand"/> CanExecute.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isExporting;

    /// <summary>Abort the in-progress Copy CE XML / Copy CE field export.</summary>
    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCts?.Cancel();

    /// <summary>
    /// View-supplied accessor for the Fields DataGrid's current multi-row
    /// selection. Returns an empty list when nothing's selected or only one
    /// row is highlighted (in which case the singular <see cref="SelectedFieldRow"/>
    /// path is sufficient). Wired by MainWindow code-behind — keeps the VM
    /// free of Avalonia DataGrid types.
    /// </summary>
    public Func<IReadOnlyList<FieldRow>>? GetSelectedFieldRows { get; set; }

    // ---------- Export options (Phase 10b) ------------------------------

    [ObservableProperty] private int  _drilldownDepth       = 1;
    [ObservableProperty] private int  _arrayLimit           = 16;
    [ObservableProperty] private bool _collapsePointerNodes = true;
    /// <summary>When set, CE XML render does live <c>readMemory</c>/<c>walkArray</c> calls so
    /// every node carries an absolute address (no <c>Offsets</c> chain). Requires
    /// <see cref="CeXmlBaseAddress"/> to be a real hex address.</summary>
    [ObservableProperty] private bool _useLiveData          = true;
    /// <summary>Base address used by CE XML root entry. User can paste an
    /// instance address from <c>findInstances</c>; defaults to a placeholder
    /// the user can find/replace in CE.</summary>
    [ObservableProperty] private string _ceXmlBaseAddress = "INSTANCE_ADDRESS";
    /// <summary>When set, CE XML descriptions carry the full
    /// <c>"name : Type [= value]"</c> form. Off by default → bare field name
    /// only, for a terser CE address list. Honoured by the class export here
    /// and (via <c>ExportOptionsProvider</c>) the Inspector's instance export.</summary>
    [ObservableProperty] private bool _verboseCeXmlDescription = false;

    // ---------- Collapse / flatten knobs (ported from UE5CEDumper) -------
    // All default OFF; honoured by the live CE XML exporter (the Browser's
    // class export AND, via ExportOptionsProvider, the Inspector's instance /
    // node export). See ExportOptions for per-flag semantics.
    [ObservableProperty] private bool _descShowOffset;
    [ObservableProperty] private bool _flattenValueStructs;
    [ObservableProperty] private bool _flattenLeafRecords;
    [ObservableProperty] private bool _collapseLeafPointers;
    [ObservableProperty] private bool _collapseChain;
    [ObservableProperty] private bool _recordColors;
    /// <summary>Even-row colour, RRGGBB hex (no '#'). Default azure.</summary>
    [ObservableProperty] private string _recordColorEven = "0080FF";
    /// <summary>Odd-row colour, RRGGBB hex; empty = CE theme.</summary>
    [ObservableProperty] private string _recordColorOdd = string.Empty;

    // ---------- Feature 1 / Feature 2 opt-ins ---------------------------
    /// <summary>Feature 1: floor every CE String &lt;Length&gt; to
    /// <see cref="MinStringLength"/> characters. Off by default.</summary>
    [ObservableProperty] private bool _enforceMinStringLength;
    /// <summary>Feature 1: the minimum CE String buffer length (characters).</summary>
    [ObservableProperty] private int  _minStringLength = 32;
    /// <summary>Feature 2: fill null pointer slots / empty collections from the
    /// element class's metadata so every array slot carries data. Off by default.</summary>
    [ObservableProperty] private bool _fillEmptyArraySlots;

    internal ExportOptions CurrentExportOptions => new(
        DrilldownDepth: Math.Max(0, DrilldownDepth),
        ArrayLimit:     Math.Max(0, ArrayLimit),
        CollapsePointerNodes: CollapsePointerNodes,
        LiveData: UseLiveData,
        VerboseDescription: VerboseCeXmlDescription,
        DescShowOffset:       DescShowOffset,
        FlattenValueStructs:  FlattenValueStructs,
        FlattenLeafRecords:   FlattenLeafRecords,
        CollapseLeafPointers: CollapseLeafPointers,
        CollapseChain:        CollapseChain,
        RecordColors:         RecordColors,
        RecordColorEven:      string.IsNullOrWhiteSpace(RecordColorEven) ? null : RecordColorEven,
        RecordColorOdd:       string.IsNullOrWhiteSpace(RecordColorOdd)  ? null : RecordColorOdd,
        EnforceMinStringLength: EnforceMinStringLength,
        MinStringLength:        Math.Max(0, MinStringLength),
        FillEmptyArraySlots:    FillEmptyArraySlots);

    /// <summary>Raised when any persisted export knob changes, so the owner can
    /// write the full <see cref="UserSettings"/> back to disk. Distinct from the
    /// MVVM PropertyChanged storm (only the export-option setters fire it).</summary>
    public event Action? ExportOptionsChanged;

    // Each export-knob setter notifies the persistence owner. CommunityToolkit's
    // source generator emits these partial hooks from the [ObservableProperty]s.
    partial void OnDrilldownDepthChanged(int value)        => ExportOptionsChanged?.Invoke();
    partial void OnArrayLimitChanged(int value)            => ExportOptionsChanged?.Invoke();
    partial void OnCollapsePointerNodesChanged(bool value) => ExportOptionsChanged?.Invoke();
    partial void OnUseLiveDataChanged(bool value)          => ExportOptionsChanged?.Invoke();
    partial void OnVerboseCeXmlDescriptionChanged(bool value) => ExportOptionsChanged?.Invoke();
    partial void OnDescShowOffsetChanged(bool value)       => ExportOptionsChanged?.Invoke();
    partial void OnFlattenValueStructsChanged(bool value)  => ExportOptionsChanged?.Invoke();
    partial void OnFlattenLeafRecordsChanged(bool value)   => ExportOptionsChanged?.Invoke();
    partial void OnCollapseLeafPointersChanged(bool value) => ExportOptionsChanged?.Invoke();
    partial void OnCollapseChainChanged(bool value)        => ExportOptionsChanged?.Invoke();
    partial void OnRecordColorsChanged(bool value)         => ExportOptionsChanged?.Invoke();
    partial void OnRecordColorEvenChanged(string value)    => ExportOptionsChanged?.Invoke();
    partial void OnRecordColorOddChanged(string value)     => ExportOptionsChanged?.Invoke();
    partial void OnCeXmlBaseAddressChanged(string value)   => ExportOptionsChanged?.Invoke();
    partial void OnEnforceMinStringLengthChanged(bool value) => ExportOptionsChanged?.Invoke();
    partial void OnMinStringLengthChanged(int value)         => ExportOptionsChanged?.Invoke();
    partial void OnFillEmptyArraySlotsChanged(bool value)    => ExportOptionsChanged?.Invoke();

    /// <summary>Push persisted export knobs from loaded <see cref="UserSettings"/>
    /// into the bound properties. Numeric / true-default keys are nullable in the
    /// settings model so an older settings.json missing them leaves the VM
    /// defaults intact. Called once after load — the setters will fire
    /// <see cref="ExportOptionsChanged"/>, which the owner ignores until load
    /// completes.</summary>
    public void ApplyExportSettings(UserSettings s)
    {
        if (s.ExportDrilldownDepth is int d)        DrilldownDepth       = d;
        if (s.ExportArrayLimit is int al)           ArrayLimit           = al;
        if (s.ExportCollapsePointerNodes is bool cp) CollapsePointerNodes = cp;
        if (s.ExportUseLiveData is bool ul)         UseLiveData          = ul;
        VerboseCeXmlDescription = s.ExportVerboseDescription;
        DescShowOffset          = s.ExportDescShowOffset;
        FlattenValueStructs     = s.ExportFlattenValueStructs;
        FlattenLeafRecords      = s.ExportFlattenLeafRecords;
        CollapseLeafPointers    = s.ExportCollapseLeafPointers;
        CollapseChain           = s.ExportCollapseChain;
        RecordColors            = s.ExportRecordColors;
        if (!string.IsNullOrWhiteSpace(s.ExportRecordColorEven)) RecordColorEven = s.ExportRecordColorEven!;
        RecordColorOdd          = s.ExportRecordColorOdd ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(s.ExportCeXmlBaseAddress)) CeXmlBaseAddress = s.ExportCeXmlBaseAddress!;
        EnforceMinStringLength  = s.ExportEnforceMinStringLength;
        if (s.ExportMinStringLength is int msl) MinStringLength = msl;
        FillEmptyArraySlots     = s.ExportFillEmptyArraySlots;
    }

    /// <summary>Copy current export knobs into <paramref name="s"/> ahead of a
    /// persist. Mirrors <see cref="ApplyExportSettings"/>.</summary>
    public void WriteExportSettings(UserSettings s)
    {
        s.ExportDrilldownDepth       = DrilldownDepth;
        s.ExportArrayLimit           = ArrayLimit;
        s.ExportCollapsePointerNodes = CollapsePointerNodes;
        s.ExportUseLiveData          = UseLiveData;
        s.ExportVerboseDescription   = VerboseCeXmlDescription;
        s.ExportDescShowOffset       = DescShowOffset;
        s.ExportFlattenValueStructs  = FlattenValueStructs;
        s.ExportFlattenLeafRecords   = FlattenLeafRecords;
        s.ExportCollapseLeafPointers = CollapseLeafPointers;
        s.ExportCollapseChain        = CollapseChain;
        s.ExportRecordColors         = RecordColors;
        s.ExportRecordColorEven      = string.IsNullOrWhiteSpace(RecordColorEven) ? null : RecordColorEven;
        s.ExportRecordColorOdd       = string.IsNullOrWhiteSpace(RecordColorOdd)  ? null : RecordColorOdd;
        s.ExportCeXmlBaseAddress     = CeXmlBaseAddress;
        s.ExportEnforceMinStringLength = EnforceMinStringLength;
        s.ExportMinStringLength        = MinStringLength;
        s.ExportFillEmptyArraySlots    = FillEmptyArraySlots;
    }

    public AssemblyBrowserViewModel(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
    }

    // ---------- Assemblies ------------------------------------------------

    public ObservableCollection<AssemblyItem> Assemblies { get; } = new();
    public ObservableCollection<AssemblyItem> FilteredAssemblies { get; } = new();

    [ObservableProperty] private string _assemblyFilter = string.Empty;
    partial void OnAssemblyFilterChanged(string value) => RefilterAssemblies();

    /// <summary>When on, the Assemblies list hides system / Unity-engine / BCL
    /// assemblies (<see cref="SystemAssemblyFilter.IsSystemAssembly"/>) so only
    /// gameplay assemblies remain. Assembly-CSharp and third-party gameplay
    /// DLLs are NOT system, so they stay. Composes with the text filter.</summary>
    [ObservableProperty] private bool _excludeSystemAssemblies;
    partial void OnExcludeSystemAssembliesChanged(bool value) => RefilterAssemblies();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAssembly))]
    private AssemblyItem? _selectedAssembly;
    public bool HasSelectedAssembly => SelectedAssembly is not null;

    partial void OnSelectedAssemblyChanged(AssemblyItem? value)
    {
        // Cascade: clear class list and load fresh.
        Classes.Clear();
        FilteredClasses.Clear();
        SelectedClass = null;
        if (value is null) return;
        _ = LoadClassesAsync(value.Name);
    }

    // ---------- Classes ---------------------------------------------------

    public ObservableCollection<ClassItem> Classes { get; } = new();
    public ObservableCollection<ClassItem> FilteredClasses { get; } = new();

    [ObservableProperty] private string _classFilter = string.Empty;
    partial void OnClassFilterChanged(string value) => RefilterClasses();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedClass))]
    [NotifyCanExecuteChangedFor(nameof(ExportSdkHeaderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsxCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCeXmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCeXmlCommand))]
    private ClassItem? _selectedClass;
    public bool HasSelectedClass => SelectedClass is not null;

    partial void OnSelectedClassChanged(ClassItem? value)
    {
        Fields.Clear();
        Methods.Clear();
        FilteredFields.Clear();
        FilteredMethods.Clear();
        ParentChain = string.Empty;
        if (value is null || SelectedAssembly is null) return;
        _ = LoadClassDetailAsync(SelectedAssembly.Name, value.Namespace, value.Name);
    }

    // ---------- Fields / Methods ------------------------------------------

    public ObservableCollection<FieldRow>  Fields  { get; } = new();
    public ObservableCollection<MethodRow> Methods { get; } = new();

    /// <summary>Filtered view of <see cref="Fields"/> driven by
    /// <see cref="FieldFilter"/>. UI-only — export commands and "Copy CE
    /// field" still operate on the full <see cref="Fields"/> /
    /// <c>_currentChain</c> data.</summary>
    public ObservableCollection<FieldRow>  FilteredFields  { get; } = new();
    public ObservableCollection<MethodRow> FilteredMethods { get; } = new();

    [ObservableProperty] private string _fieldFilter  = string.Empty;
    [ObservableProperty] private string _methodFilter = string.Empty;
    partial void OnFieldFilterChanged(string value)  => RefilterFields();
    partial void OnMethodFilterChanged(string value) => RefilterMethods();

    /// <summary>Currently selected row in the Fields DataGrid — drives "Copy CE field".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCeFieldCommand))]
    private FieldRow? _selectedFieldRow;

    [ObservableProperty] private string _parentChain = string.Empty;

    /// <summary>
    /// "show inherited fields" toggle — when on, walks the parent chain via
    /// repeated <c>getClassInfo</c> calls and prepends each ancestor's
    /// fields/methods, oldest-first.
    /// </summary>
    [ObservableProperty] private bool _showInherited;

    partial void OnShowInheritedChanged(bool value)
    {
        if (SelectedAssembly is null || SelectedClass is null) return;
        _ = LoadClassDetailAsync(SelectedAssembly.Name, SelectedClass.Namespace, SelectedClass.Name);
    }

    // ---------- Status ----------------------------------------------------

    [ObservableProperty] private bool    _isLoadingAssemblies;
    [ObservableProperty] private bool    _isLoadingClasses;
    [ObservableProperty] private bool    _isLoadingDetail;
    [ObservableProperty] private string? _errorMessage;

    // ---------- Commands --------------------------------------------------

    [RelayCommand]
    private async Task RefreshAssembliesAsync()
    {
        ErrorMessage = null;
        IsLoadingAssemblies = true;
        try
        {
            GetAssembliesResult result = await _backend.GetAssembliesAsync().ConfigureAwait(true);
            Assemblies.Clear();
            // Sort alphabetically (case-insensitive). Backend order is image-load
            // order which is engine-implementation-defined; the user expects
            // Assembly-CSharp / common engine assemblies easy to find.
            foreach (AssemblyEntry entry in result.Assemblies
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                Assemblies.Add(new AssemblyItem(entry.Name, entry.Path));
            }
            RefilterAssemblies();
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"loaded {result.Count} assemblies");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"refreshAssemblies failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsLoadingAssemblies = false;
        }
    }

    private async Task LoadClassesAsync(string assembly)
    {
        _classesCts?.Cancel();
        _classesCts = new CancellationTokenSource();
        CancellationToken ct = _classesCts.Token;

        ErrorMessage = null;
        IsLoadingClasses = true;
        try
        {
            GetClassesResult result = await _backend.GetClassesAsync(assembly, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                ErrorMessage = $"getClasses: {result.Error}";
                return;
            }
            Classes.Clear();
            // Sort alphabetically by full name ("Namespace.Name") so PlayerData
            // (etc.) are easy to scroll to. Token order from the backend is
            // metadata declaration order which is rarely useful for browsing.
            foreach (ClassEntry entry in result.Classes
                .OrderBy(e => string.IsNullOrEmpty(e.Namespace) ? e.Name : $"{e.Namespace}.{e.Name}",
                         StringComparer.OrdinalIgnoreCase))
            {
                Classes.Add(new ClassItem(entry.Namespace ?? string.Empty, entry.Name, entry.Token));
            }
            RefilterClasses();
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"loaded {Classes.Count} classes from {assembly}");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"getClasses({assembly}) failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsLoadingClasses = false;
        }
    }

    private async Task LoadClassDetailAsync(string assembly, string @namespace, string name)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        CancellationToken ct = _detailCts.Token;

        ErrorMessage = null;
        IsLoadingDetail = true;
        try
        {
            // Walk: self -> parent -> grandparent -> ... bounded at 32 to dodge cycles.
            var chain = new List<GetClassInfoResult>();
            string? curNs = @namespace;
            string? curName = name;
            for (int i = 0; i < 32 && curName is not null; ++i)
            {
                GetClassInfoResult info = await _backend
                    .GetClassInfoAsync(assembly, curNs ?? string.Empty, curName, ct)
                    .ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;
                if (!string.IsNullOrEmpty(info.Error))
                {
                    if (i == 0)
                    {
                        ErrorMessage = $"getClassInfo: {info.Error}";
                        return;
                    }
                    // Parent lookup failed (parent assembly differs etc.) — stop
                    // walking but keep what we already collected.
                    break;
                }
                chain.Add(info);
                if (!ShowInherited) break;
                if (info.Parent is null || string.IsNullOrEmpty(info.Parent.Name)) break;
                curNs = info.Parent.Namespace;
                curName = info.Parent.Name;
            }

            // Merge oldest-first so derived classes' members come last in the
            // grids — a familiar layout when reading top-to-bottom is "what
            // you inherited" then "what you added".
            chain.Reverse();
            _currentChain = chain;
            // The chain is what the export commands actually consume; ping
            // their CanExecute so any UI bindings that latched the disabled
            // state during the brief load window flip to enabled now.
            ExportSdkHeaderCommand.NotifyCanExecuteChanged();
            ExportCsxCommand.NotifyCanExecuteChanged();
            ExportCeXmlCommand.NotifyCanExecuteChanged();
            CopyCeXmlCommand.NotifyCanExecuteChanged();
            Fields.Clear();
            Methods.Clear();
            ParentChain = BuildParentChainLabel(chain);
            foreach (GetClassInfoResult info in chain)
            {
                string from = string.IsNullOrEmpty(info.Namespace)
                    ? (info.Name ?? "?")
                    : $"{info.Namespace}.{info.Name}";
                if (info.Fields is { } fields)
                {
                    foreach (FieldEntry f in fields)
                    {
                        Fields.Add(new FieldRow(
                            f.Name, f.Type ?? string.Empty, f.Offset, f.Flags, f.Static, from));
                    }
                }
                if (info.Methods is { } methods)
                {
                    foreach (MethodEntry m in methods)
                    {
                        Methods.Add(new MethodRow(
                            m.Name, m.Token, m.Flags, m.Static, m.Address, from));
                    }
                }
            }
            RefilterFields();
            RefilterMethods();
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"classInfo {assembly}!{@namespace}.{name} fields={Fields.Count} methods={Methods.Count} inherited={ShowInherited}");
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"getClassInfo({assembly}!{@namespace}.{name}) failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    // ---------- Export (Phase 10) ----------------------------------------

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportSdkHeaderAsync() =>
        ExportAsync("h", "C/C++ header", (self, ancestors, assembly, _, _, _) =>
            Task.FromResult(SdkHeaderExporter.Render(self, ancestors, assembly)));

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsxAsync() =>
        ExportAsync("csx", "CE Structure Dissect", (self, ancestors, assembly, options, lookup, ct) =>
            CsxExporter.RenderAsync(self, ancestors, assembly, options, lookup, ct));

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCeXmlAsync() =>
        ExportAsync("CT", "CE cheat table", (self, ancestors, assembly, options, lookup, ct) =>
            CeXmlExporter.RenderAsync(CeXmlBaseAddress, self, ancestors, assembly, options, lookup, ct));

    /// <summary>
    /// Copy CE XML for the whole class to the clipboard — paste straight into
    /// CE's address-list pane (Edit → Paste, or Ctrl+V on the entry list).
    /// Honors all export options (depth / arrayLimit / collapse) and uses the
    /// current CeXmlBaseAddress as the root anchor.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task CopyCeXmlAsync()
    {
        if (CopyToClipboardAsync is null || _currentChain.Count == 0) return;

        GetClassInfoResult self = _currentChain[^1];
        IReadOnlyList<GetClassInfoResult>? ancestors =
            (ShowInherited && _currentChain.Count > 1)
                ? _currentChain.GetRange(0, _currentChain.Count - 1)
                : null;
        string assembly = SelectedAssembly?.Name ?? "Assembly";

        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;
        IsExporting = true;
        try
        {
            // Progress<T> created on the UI thread → its Report() marshals the
            // count back here, so the label updates live during a long export.
            CopyCeXmlProgress = "exporting…";
            var progress = new Progress<int>(n => CopyCeXmlProgress = $"exporting… {n:N0} entries");
            string xml = await CeXmlExporter.RenderAsync(
                CeXmlBaseAddress, self, ancestors, assembly,
                CurrentExportOptions, _backend, ct, progress).ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            int lines = CountLines(xml);
            CopyCeXmlProgress = $"copied {lines:N0} lines";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE XML clipboard ({xml.Length} bytes, {lines} lines, drilldown={DrilldownDepth} arr={ArrayLimit} collapse={CollapsePointerNodes})");
        }
        catch (OperationCanceledException)
        {
            CopyCeXmlProgress = "export cancelled";
        }
        catch (Exception ex)
        {
            CopyCeXmlProgress = "export failed";
            ErrorMessage = $"copy CE XML failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE XML failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 1;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    /// <summary>
    /// Copy one or more fields' CE XML pointer-chain to the clipboard. Uses
    /// every currently-selected row in the Fields DataGrid (Ctrl/Shift-click
    /// for multi-select); falls back to <see cref="SelectedFieldRow"/> for
    /// the common single-row case. When a field is a pointer/array and
    /// drilldown is configured, the resolved nested struct is included so
    /// pasting one entry into CE gives the full sub-tree.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyField))]
    private async Task CopyCeFieldAsync()
    {
        if (CopyToClipboardAsync is null || _currentChain.Count == 0) return;

        // Build the working set: prefer the multi-select snapshot from the
        // View when more than one row is highlighted, fall back to the
        // singular SelectedFieldRow otherwise. Preserves single-select
        // behaviour byte-for-byte (RenderFieldsAsync with one entry produces
        // the same output the old RenderFieldAsync did).
        IReadOnlyList<FieldRow> rows = GetSelectedFieldRows?.Invoke() ?? Array.Empty<FieldRow>();
        if (rows.Count == 0 && SelectedFieldRow is not null)
            rows = new[] { SelectedFieldRow };
        if (rows.Count == 0) return;

        // Resolve FieldRow → FieldEntry once per row and remember the
        // declaring GetClassInfoResult so the wrapping group label points at
        // the right class. Multi-row exports root every entry under the same
        // declaring class — that's the common case (the user just shift-
        // selected a range in the Fields grid), and matches the bug-report
        // acceptance criterion ("all 3 fields present in clipboard").
        var entries  = new List<FieldEntry>(rows.Count);
        GetClassInfoResult declaring = _currentChain[^1];
        bool declaringSet = false;
        foreach (FieldRow row in rows)
        {
            foreach (GetClassInfoResult info in _currentChain)
            {
                if (info.Fields is null) continue;
                FieldEntry? match = null;
                foreach (FieldEntry f in info.Fields)
                {
                    if (f.Name == row.Name && f.Offset == row.Offset && f.Static == row.IsStatic)
                    {
                        match = f;
                        break;
                    }
                }
                if (match is not null)
                {
                    entries.Add(match);
                    if (!declaringSet)
                    {
                        declaring = info;
                        declaringSet = true;
                    }
                    break;
                }
            }
        }
        if (entries.Count == 0) return;

        string assembly = SelectedAssembly?.Name ?? "Assembly";

        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;
        IsExporting = true;
        try
        {
            CopyCeXmlProgress = "exporting…";
            string xml = await CeXmlExporter.RenderFieldsAsync(
                CeXmlBaseAddress, entries, declaring, assembly,
                CurrentExportOptions, _backend, ct).ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            string nameSummary = entries.Count == 1
                ? entries[0].Name
                : $"{entries.Count} fields ({entries[0].Name}, …)";
            CopyCeXmlProgress = $"copied {CountLines(xml):N0} lines";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE field {nameSummary} clipboard ({xml.Length} bytes, drilldown={DrilldownDepth} arr={ArrayLimit})");
        }
        catch (OperationCanceledException)
        {
            CopyCeXmlProgress = "export cancelled";
        }
        catch (Exception ex)
        {
            CopyCeXmlProgress = "export failed";
            ErrorMessage = $"copy CE field failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE field failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    // CanExport / CanCopyField only gate on the *visible* selection state —
    // _currentChain is populated asynchronously after SelectedClass changes,
    // and there's no observable signal to drive a CanExecute re-evaluation
    // on a private field. Each command no-ops safely when the chain is
    // momentarily empty (rare race window during initial load).
    private bool CanCopyField() => SelectedFieldRow is not null;
    private bool CanExport()    => SelectedClass is not null;

    private async Task ExportAsync(
        string ext, string description,
        Func<GetClassInfoResult, IReadOnlyList<GetClassInfoResult>?, string,
             ExportOptions, IClassInfoLookup, CancellationToken,
             Task<string>> renderer)
    {
        if (SaveFileAsync is null) return;
        if (_currentChain.Count == 0) return;

        GetClassInfoResult self = _currentChain[^1];
        IReadOnlyList<GetClassInfoResult>? ancestors =
            (ShowInherited && _currentChain.Count > 1)
                ? _currentChain.GetRange(0, _currentChain.Count - 1)
                : null;

        string assembly = SelectedAssembly?.Name ?? "Assembly";
        string suggested = SafeFilename(
            $"{assembly}_{(self.Namespace is { Length: > 0 } ns ? ns + "." : "")}{self.Name}.{ext}");

        string? path;
        try
        {
            path = await SaveFileAsync(suggested, description, ext).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"save dialog failed: {ex.Message}";
            return;
        }
        if (path is null) return;

        try
        {
            string text = await renderer(self, ancestors, assembly,
                CurrentExportOptions, _backend, default).ConfigureAwait(true);
            await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"export {ext} -> {path} ({text.Length} bytes, drilldown={DrilldownDepth} arr={ArrayLimit} collapse={CollapsePointerNodes})");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"export failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"export {ext} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafeFilename(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static string BuildParentChainLabel(List<GetClassInfoResult> oldestFirst)
    {
        if (oldestFirst.Count == 0) return string.Empty;
        var parts = new List<string>(oldestFirst.Count);
        foreach (GetClassInfoResult info in oldestFirst)
        {
            string ns = info.Namespace ?? string.Empty;
            string nm = info.Name ?? "?";
            parts.Add(string.IsNullOrEmpty(ns) ? nm : $"{ns}.{nm}");
        }
        return string.Join(" → ", parts);
    }

    // ---------- Filtering -------------------------------------------------

    private void RefilterAssemblies()
    {
        FilteredAssemblies.Clear();
        string filter = AssemblyFilter ?? string.Empty;
        bool unfiltered = filter.Length == 0;
        foreach (AssemblyItem a in Assemblies)
        {
            if (ExcludeSystemAssemblies && SystemAssemblyFilter.IsSystemAssembly(a.Name))
                continue;
            if (unfiltered ||
                a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAssemblies.Add(a);
            }
        }
    }

    private void RefilterClasses()
    {
        FilteredClasses.Clear();
        string filter = ClassFilter ?? string.Empty;
        if (filter.Length == 0)
        {
            // No filter — Classes is already sorted alphabetically by full
            // name in LoadClassesAsync, just mirror it.
            foreach (ClassItem c in Classes) FilteredClasses.Add(c);
            return;
        }

        // With a filter: rank by relevance so an exact-name search lands on
        // top. Three buckets, each kept in the source's alphabetical order:
        //   0. Exact match on simple name OR full name (case-insensitive)
        //   1. Simple name STARTS with the filter (also covers "PlayerData"
        //      vs "PlayerDataBase" — the prefix match wins over the
        //      same-substring "SetPlayerDataBool")
        //   2. Anywhere-in-DisplayName substring match
        // Anything that doesn't even substring-match is filtered out.
        var bucket0 = new List<ClassItem>();
        var bucket1 = new List<ClassItem>();
        var bucket2 = new List<ClassItem>();
        foreach (ClassItem c in Classes)
        {
            bool nameExact = string.Equals(c.Name, filter, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(c.DisplayName, filter, StringComparison.OrdinalIgnoreCase);
            if (nameExact) { bucket0.Add(c); continue; }
            bool simplePrefix = c.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                             || c.FriendlyName.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
            if (simplePrefix) { bucket1.Add(c); continue; }
            if (c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                bucket2.Add(c);
            }
        }
        foreach (ClassItem c in bucket0) FilteredClasses.Add(c);
        foreach (ClassItem c in bucket1) FilteredClasses.Add(c);
        foreach (ClassItem c in bucket2) FilteredClasses.Add(c);
    }

    /// <summary>
    /// Rebuild <see cref="FilteredFields"/> from <see cref="Fields"/>
    /// using a substring match against the row's name (case-insensitive).
    /// UI-only — exports + Copy CE field still walk the unfiltered Fields
    /// / current parent chain.
    /// </summary>
    private void RefilterFields()
    {
        FilteredFields.Clear();
        string filter = FieldFilter ?? string.Empty;
        bool unfiltered = filter.Length == 0;
        foreach (FieldRow f in Fields)
        {
            if (unfiltered ||
                f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                f.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (f.Type?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                f.FriendlyType.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredFields.Add(f);
            }
        }
    }

    /// <summary>
    /// Same shape as <see cref="RefilterFields"/> for the Methods grid.
    /// </summary>
    private void RefilterMethods()
    {
        FilteredMethods.Clear();
        string filter = MethodFilter ?? string.Empty;
        bool unfiltered = filter.Length == 0;
        foreach (MethodRow m in Methods)
        {
            if (unfiltered ||
                m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.FriendlyDeclaredIn.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredMethods.Add(m);
            }
        }
    }
}

// ----- Row records for the views --------------------------------------------
// Keep these as plain immutable types — they aren't observed for change
// notifications (the parent collection clears + repopulates on every refresh).

public sealed class AssemblyItem
{
    public AssemblyItem(string name, string? path)
    {
        Name = name;
        Path = path;
    }
    public string  Name { get; }
    public string? Path { get; }
}

public sealed class ClassItem
{
    public ClassItem(string @namespace, string name, uint? token)
    {
        Namespace = @namespace;
        Name      = name;
        Token     = token;
        DisplayName = string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
        FriendlyName = CompilerNameDemangler.DemangleClassPath(name);
        // OriginHint is derived from the deepest nested segment — that's
        // the one most likely to be a state machine / closure / lambda
        // host. Top-level outer classes are user code 99% of the time.
        string deepest = name;
        int sep = -1;
        for (int i = name.Length - 1; i >= 0; i--)
            if (name[i] == '/' || name[i] == '+') { sep = i; break; }
        if (sep >= 0) deepest = name.Substring(sep + 1);
        OriginHint = CompilerNameDemangler.DescribeOrigin(deepest);
    }
    public string  Namespace    { get; }
    public string  Name         { get; }
    public uint?   Token        { get; }
    public string  DisplayName  { get; }
    public string  FriendlyName { get; }
    public string? OriginHint   { get; }
    /// <summary>Tooltip text — raw name plus origin if compiler-generated.</summary>
    public string  Tooltip      =>
        OriginHint is null ? DisplayName : $"{DisplayName}\n({OriginHint})";
}

public sealed class FieldRow
{
    public FieldRow(string name, string type, int offset, uint flags, bool isStatic, string declaredIn)
    {
        Name = name;
        Type = type;
        Offset = offset;
        Flags = flags;
        IsStatic = isStatic;
        DeclaredIn = declaredIn;
        FriendlyName       = CompilerNameDemangler.Demangle(name);
        FriendlyType       = CompilerNameDemangler.DemangleClassPath(type);
        FriendlyDeclaredIn = CompilerNameDemangler.DemangleClassPath(declaredIn);
        OriginHint         = CompilerNameDemangler.DescribeOrigin(name);
    }
    public string  Name               { get; }
    public string  Type               { get; }
    public int     Offset             { get; }
    public string  OffsetHex          => $"0x{Offset:X}";
    public uint    Flags              { get; }
    public string  FlagsHex           => $"0x{Flags:X4}";
    public bool    IsStatic           { get; }
    public string  DeclaredIn         { get; }
    public string  FriendlyName       { get; }
    public string  FriendlyType       { get; }
    public string  FriendlyDeclaredIn { get; }
    public string? OriginHint         { get; }
    public string  Tooltip      =>
        OriginHint is null ? Name : $"{Name}\n({OriginHint})";
}

public sealed class MethodRow
{
    public MethodRow(string name, uint token, uint flags, bool isStatic, string? address, string declaredIn)
    {
        Name = name;
        Token = token;
        Flags = flags;
        IsStatic = isStatic;
        Address = address;
        DeclaredIn = declaredIn;
        // Methods on a state-machine class are almost always literally
        // "MoveNext" / "SetStateMachine" — boring on their own. The
        // useful hint lives on the *DeclaredIn* path. Demangle both so
        // the user can read e.g. "CreateUnitResult [async 41] :: MoveNext".
        FriendlyName       = CompilerNameDemangler.Demangle(name);
        FriendlyDeclaredIn = CompilerNameDemangler.DemangleClassPath(declaredIn);
        OriginHint         = CompilerNameDemangler.DescribeOrigin(name);
    }
    public string  Name               { get; }
    public uint    Token              { get; }
    public string  TokenHex           => $"0x{Token:X8}";
    public uint    Flags              { get; }
    public string  FlagsHex           => $"0x{Flags:X4}";
    public bool    IsStatic           { get; }
    public string? Address            { get; }
    public string  DeclaredIn         { get; }
    public string  FriendlyName       { get; }
    public string  FriendlyDeclaredIn { get; }
    public string? OriginHint         { get; }
    public string  Tooltip            =>
        OriginHint is null ? Name : $"{Name}\n({OriginHint})";
}
