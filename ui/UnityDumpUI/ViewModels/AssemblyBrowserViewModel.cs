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
// Backend calls happen on a background thread via Task.Run because the pipe
// IO is sync-over-async at the OS layer; the UI thread stays free during
// large class-list fetches.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    // ---------- Export options (Phase 10b) ------------------------------

    [ObservableProperty] private int  _drilldownDepth       = 1;
    [ObservableProperty] private int  _arrayLimit           = 16;
    [ObservableProperty] private bool _collapsePointerNodes = true;
    /// <summary>Base address used by CE XML root entry. User can paste an
    /// instance address from <c>findInstances</c>; defaults to a placeholder
    /// the user can find/replace in CE.</summary>
    [ObservableProperty] private string _ceXmlBaseAddress = "INSTANCE_ADDRESS";

    private ExportOptions CurrentExportOptions => new(
        DrilldownDepth: Math.Max(0, DrilldownDepth),
        ArrayLimit:     Math.Max(0, ArrayLimit),
        CollapsePointerNodes: CollapsePointerNodes);

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
        ParentChain = string.Empty;
        if (value is null || SelectedAssembly is null) return;
        _ = LoadClassDetailAsync(SelectedAssembly.Name, value.Namespace, value.Name);
    }

    // ---------- Fields / Methods ------------------------------------------

    public ObservableCollection<FieldRow>  Fields  { get; } = new();
    public ObservableCollection<MethodRow> Methods { get; } = new();

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
            foreach (AssemblyEntry entry in result.Assemblies)
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
            foreach (ClassEntry entry in result.Classes)
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

        try
        {
            string xml = await CeXmlExporter.RenderAsync(
                CeXmlBaseAddress, self, ancestors, assembly,
                CurrentExportOptions, _backend, default).ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE XML clipboard ({xml.Length} bytes, drilldown={DrilldownDepth} arr={ArrayLimit} collapse={CollapsePointerNodes})");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"copy CE XML failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE XML failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Copy a single field's CE XML pointer-chain to the clipboard. Uses the
    /// currently-selected row in the Fields DataGrid; when the field is a
    /// pointer/array and drilldown is configured, the resolved nested struct
    /// is included so pasting one entry into CE gives the full sub-tree.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyField))]
    private async Task CopyCeFieldAsync()
    {
        if (CopyToClipboardAsync is null || SelectedFieldRow is null || _currentChain.Count == 0) return;

        // Find the FieldEntry + the GetClassInfoResult that declared it (so
        // RenderField can label the entry with the right declaring class).
        FieldRow row = SelectedFieldRow;
        FieldEntry? entry = null;
        GetClassInfoResult declaring = _currentChain[^1];
        foreach (GetClassInfoResult info in _currentChain)
        {
            if (info.Fields is null) continue;
            foreach (FieldEntry f in info.Fields)
            {
                if (f.Name == row.Name && f.Offset == row.Offset && f.Static == row.IsStatic)
                {
                    entry = f;
                    declaring = info;
                    break;
                }
            }
            if (entry is not null) break;
        }
        if (entry is null) return;

        string assembly = SelectedAssembly?.Name ?? "Assembly";
        try
        {
            string xml = await CeXmlExporter.RenderFieldAsync(
                CeXmlBaseAddress, entry, declaring, assembly,
                CurrentExportOptions, _backend, default).ConfigureAwait(true);
            await CopyToClipboardAsync(xml).ConfigureAwait(true);
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE field {entry.Name} clipboard ({xml.Length} bytes, drilldown={DrilldownDepth} arr={ArrayLimit})");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"copy CE field failed: {ex.Message}";
            _logger.Write(UnityDumpUI.Core.Constants.LogCategoryView,
                $"copy CE field failed: {ex.GetType().Name}: {ex.Message}");
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
        bool unfiltered = filter.Length == 0;
        foreach (ClassItem c in Classes)
        {
            if (unfiltered ||
                c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredClasses.Add(c);
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
    }
    public string Namespace   { get; }
    public string Name        { get; }
    public uint?  Token       { get; }
    public string DisplayName { get; }
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
    }
    public string Name       { get; }
    public string Type       { get; }
    public int    Offset     { get; }
    public string OffsetHex  => $"0x{Offset:X}";
    public uint   Flags      { get; }
    public string FlagsHex   => $"0x{Flags:X4}";
    public bool   IsStatic   { get; }
    public string DeclaredIn { get; }
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
    }
    public string  Name       { get; }
    public uint    Token      { get; }
    public string  TokenHex   => $"0x{Token:X8}";
    public uint    Flags      { get; }
    public string  FlagsHex   => $"0x{Flags:X4}";
    public bool    IsStatic   { get; }
    public string? Address    { get; }
    public string  DeclaredIn { get; }
}
