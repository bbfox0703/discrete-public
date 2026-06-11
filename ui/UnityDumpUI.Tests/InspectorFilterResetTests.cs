// InspectorFilterResetTests — regression for the 2026-06-08 user report fixed
// in c77ba68 / 5395edf: switching the inspected class must RESET the per-class
// filters (the Field-filter JSON predicate, the walked-fields keyword, and the
// filter-builder picker + value boxes). Otherwise a stale predicate from the
// previous class silently hides the new class's instances/fields. Re-inspecting
// the SAME class must PRESERVE the filters (matches "preserve on re-walk").
//
// Pure VM state — no pipe / DLL (never-connecting fake pipe, same construction
// pattern as InspectorSortTreeTests).

using System;
using System.IO;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class InspectorFilterResetTests : IDisposable
{
    private readonly string _tempDir;

    public InspectorFilterResetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "InspectorFilterResetTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private LiveInspectionViewModel BuildVm()
    {
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + Guid.NewGuid().ToString("N")));
        return new LiveInspectionViewModel(backend, logger);
    }

    private const string SampleFieldFilterJson = "[{\"name\":\"hp\",\"op\":\">\",\"value\":1}]";

    /// <summary>Populate every per-class filter so a reset is observable.</summary>
    private static void PopulateFilters(LiveInspectionViewModel vm)
    {
        vm.FieldFilterJson = SampleFieldFilterJson;     // "Field filter" JSON predicate box
        vm.Filter          = "hp";                       // "filter walked fields" keyword box
        vm.FilterFieldOptions.Add(new FilterFieldOption("hp", 0x10, "System.Int32"));
        vm.SelectedFilterField = vm.FilterFieldOptions[0];
        vm.FilterValue1 = "1";
        vm.FilterValue2 = "99";
    }

    [Fact]
    public void SetTarget_ToDifferentClass_ClearsAllPerClassFilters()
    {
        var vm = BuildVm();
        vm.SetTarget("Assembly-CSharp", "Game", "PlayerState");
        PopulateFilters(vm);

        vm.SetTarget("Assembly-CSharp", "Game", "EnemyState");   // different class name

        Assert.Equal(string.Empty, vm.FieldFilterJson);
        Assert.Equal(string.Empty, vm.Filter);
        Assert.Empty(vm.FilterFieldOptions);
        Assert.Null(vm.SelectedFilterField);
        Assert.Equal(string.Empty, vm.FilterValue1);
        Assert.Equal(string.Empty, vm.FilterValue2);
    }

    [Fact]
    public void SetTarget_ToSameClass_PreservesFilters()
    {
        var vm = BuildVm();
        vm.SetTarget("Assembly-CSharp", "Game", "PlayerState");
        PopulateFilters(vm);

        vm.SetTarget("Assembly-CSharp", "Game", "PlayerState");  // re-inspect the SAME class

        Assert.Equal(SampleFieldFilterJson, vm.FieldFilterJson);
        Assert.Equal("hp", vm.Filter);
        Assert.Single(vm.FilterFieldOptions);
        Assert.NotNull(vm.SelectedFilterField);
        Assert.Equal("1", vm.FilterValue1);
        Assert.Equal("99", vm.FilterValue2);
    }

    [Fact]
    public void SetTarget_DifferingOnlyByNamespace_CountsAsClassChange()
    {
        // The class-change check compares assembly + namespace + name, so two
        // same-named classes in different namespaces are distinct → reset.
        var vm = BuildVm();
        vm.SetTarget("Assembly-CSharp", "Game", "State");
        PopulateFilters(vm);

        vm.SetTarget("Assembly-CSharp", "Game.Combat", "State");

        Assert.Equal(string.Empty, vm.FieldFilterJson);
        Assert.Equal(string.Empty, vm.Filter);
        Assert.Empty(vm.FilterFieldOptions);
    }
}
