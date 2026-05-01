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
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class AssemblyBrowserViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _classesCts;

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
