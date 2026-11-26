// ExportCancellationAndBrowserChainTests.cs — regression tests for:
//   * E-M1: CE XML (live + template) and CSX exporters must propagate
//           OperationCanceledException instead of swallowing it and returning
//           truncated output; non-cancellation RPC faults still degrade gracefully.
//   * E-M6: AssemblyBrowserViewModel must clear _currentChain immediately when
//           SelectedClass changes and refuse CanExport until the matching class
//           chain finishes loading.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using UnityDumpUI.ViewModels;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class ExportCancellationAndBrowserChainTests : IDisposable
{
    private readonly string _tempDir;

    public ExportCancellationAndBrowserChainTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExportCancelChainTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class CancellingLiveSource : ILiveExportSource
    {
        private readonly string _cancelOnMethod;
        private readonly bool _throwNonCancel;

        public CancellingLiveSource(string cancelOnMethod, bool throwNonCancel = false)
        {
            _cancelOnMethod = cancelOnMethod;
            _throwNonCancel = throwNonCancel;
        }

        private void MaybeThrow(string method, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == _cancelOnMethod)
            {
                if (_throwNonCancel) throw new InvalidOperationException("simulated RPC failure");
                throw new OperationCanceledException(ct);
            }
        }

        public Task<GetClassInfoResult?> GetClassInfoAsync(
            string assembly, string @namespace, string name, CancellationToken ct = default)
        {
            MaybeThrow(nameof(GetClassInfoAsync), ct);
            return Task.FromResult<GetClassInfoResult?>(new GetClassInfoResult
            {
                Namespace = @namespace,
                Name      = name,
                Fields    = new List<FieldEntry>
                {
                    new() { Name = "x", Type = "System.Int32", Offset = 0x10 },
                },
            });
        }

        public Task<ReadMemoryResult> ReadMemoryAsync(
            string address, int size, CancellationToken ct = default)
        {
            MaybeThrow(nameof(ReadMemoryAsync), ct);
            return Task.FromResult(new ReadMemoryResult { Hex = "0000500000000000" });
        }

        public Task<WalkArrayResult> WalkArrayAsync(
            string address, string? elementType, int? limit, int? elementSize,
            string? elementAssembly, string? elementNamespace, string? elementName,
            CancellationToken ct = default)
        {
            MaybeThrow(nameof(WalkArrayAsync), ct);
            return Task.FromResult(new WalkArrayResult
            {
                Length = 1, Count = 1, IsPointer = true, ElementSize = 8,
                DataAddress = "0x0000000000500020",
                Elements = new List<WalkedArrayElement> { new() { Index = 0, Ptr = "0x0000000000600000" } },
            });
        }

        public Task<WalkInstanceResult> WalkInstanceAsync(
            string address, string assembly, string? @namespace, string name,
            CancellationToken ct = default)
        {
            MaybeThrow(nameof(WalkInstanceAsync), ct);
            return Task.FromResult(new WalkInstanceResult
            {
                Namespace   = @namespace,
                Name        = name,
                PointerSize = 8,
                Fields      = new List<WalkedField>
                {
                    new()
                    {
                        Name = "child", Type = "Game.Child", Offset = 0x10,
                        Address = "0x000000000000B910", Ptr = "0x000000000000C000",
                    },
                    new()
                    {
                        Name = "items", Type = "System.Collections.Generic.List<Game.Child>", Offset = 0x18,
                        Address = "0x000000000000B918", Ptr = "0x000000000000D000",
                    },
                },
            });
        }

        public Task<WalkInstanceResult> WalkInstanceFromHeaderAsync(
            string address, CancellationToken ct = default)
        {
            MaybeThrow(nameof(WalkInstanceFromHeaderAsync), ct);
            return Task.FromResult(new WalkInstanceResult { Error = "not found" });
        }
    }

    private static GetClassInfoResult SampleClass(string name = "Player") => new()
    {
        Namespace = "Game",
        Name      = name,
        Fields    = new List<FieldEntry>
        {
            new() { Name = "hp",    Type = "System.Int32", Offset = 0x10 },
            new() { Name = "child", Type = "Game.Child",   Offset = 0x18 },
        },
    };

    // ---- E-M1: Cancellation propagation across exporters ------------------

    [Theory]
    [InlineData(nameof(ILiveExportSource.WalkInstanceAsync))]
    [InlineData(nameof(ILiveExportSource.WalkInstanceFromHeaderAsync))]
    [InlineData(nameof(ILiveExportSource.ReadMemoryAsync))]
    [InlineData(nameof(ILiveExportSource.WalkArrayAsync))]
    public async Task CeXml_Live_PropagatesOperationCanceledException_NeverReturnsTruncatedXml(string cancelPoint)
    {
        var live = new CancellingLiveSource(cancelPoint);
        var opts = ExportOptions.Defaults with { LiveData = true, DrilldownDepth = 2, ArrayLimit = 4 };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CeXmlExporter.RenderAsync(
                "0x000000000000B900", SampleClass(), ancestors: null, "Assembly-CSharp", opts, live,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CeXml_Template_And_Csx_PropagateOperationCanceledException()
    {
        var lookup = new CancellingLiveSource(nameof(IClassInfoLookup.GetClassInfoAsync));
        var opts = ExportOptions.Defaults with { LiveData = false, DrilldownDepth = 1 };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CeXmlExporter.RenderAsync(
                "INSTANCE_ADDRESS", SampleClass(), ancestors: null, "Assembly-CSharp", opts, lookup,
                ct: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CsxExporter.RenderAsync(
                SampleClass(), ancestors: null, "Assembly-CSharp", opts, lookup,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exporters_PreCanceledToken_ThrowsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var live = new CancellingLiveSource("none");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CeXmlExporter.RenderAsync(
                "INSTANCE_ADDRESS", SampleClass(), ancestors: null, "Assembly-CSharp",
                ExportOptions.Defaults, live, ct: cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CeXmlExporter.RenderFieldsAsync(
                "INSTANCE_ADDRESS", SampleClass().Fields!, SampleClass(), "Assembly-CSharp",
                ExportOptions.Defaults, live, ct: cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CsxExporter.RenderAsync(
                SampleClass(), ancestors: null, "Assembly-CSharp",
                ExportOptions.Defaults, live, ct: cts.Token));
    }

    [Fact]
    public async Task Exporters_NonCancellationException_StillDegradesGracefully()
    {
        // A regular RPC failure on nested child lookup must still tolerate the
        // failure and emit the parent class instead of crashing the export.
        var lookup = new CancellingLiveSource(nameof(IClassInfoLookup.GetClassInfoAsync), throwNonCancel: true);
        var opts = ExportOptions.Defaults with { LiveData = false, DrilldownDepth = 1 };

        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", SampleClass(), ancestors: null, "Assembly-CSharp", opts, lookup,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("hp : System.Int32", xml);

        string csx = await CsxExporter.RenderAsync(
            SampleClass(), ancestors: null, "Assembly-CSharp", opts, lookup,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("hp : System.Int32", csx);
    }

    // ---- E-M6: AssemblyBrowserViewModel stale _currentChain prevention ----

    [Fact]
    public void AssemblyBrowser_SwitchingSelectedClass_ClearsChainAndDisablesExportsUntilLoaded()
    {
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs"));
        var vm      = new AssemblyBrowserViewModel(backend, logger);

        // Select ClassA and simulate its chain having finished loading.
        vm.SelectedClass = new ClassItem("Game", "ClassA", 0x02000001);
        Assert.False(vm.ExportCeXmlCommand.CanExecute(null));
        Assert.False(vm.CopyCeXmlCommand.CanExecute(null));
        Assert.False(vm.ExportCsxCommand.CanExecute(null));
        Assert.False(vm.ExportSdkHeaderCommand.CanExecute(null));

        vm.SetLoadedChainForTest(new List<GetClassInfoResult> { SampleClass("ClassA") });
        Assert.True(vm.ExportCeXmlCommand.CanExecute(null));
        Assert.True(vm.CopyCeXmlCommand.CanExecute(null));
        Assert.True(vm.ExportCsxCommand.CanExecute(null));
        Assert.True(vm.ExportSdkHeaderCommand.CanExecute(null));

        // E-M6: switching to ClassB MUST clear _currentChain immediately and
        // disable every export command until ClassB's own getClassInfo finishes.
        vm.SelectedClass = new ClassItem("Game", "ClassB", 0x02000002);
        Assert.Equal(0, vm.CurrentChainCountForTest);
        Assert.False(vm.ExportCeXmlCommand.CanExecute(null));
        Assert.False(vm.CopyCeXmlCommand.CanExecute(null));
        Assert.False(vm.ExportCsxCommand.CanExecute(null));
        Assert.False(vm.ExportSdkHeaderCommand.CanExecute(null));

        // Even if a mismatched chain (ClassA) were somehow present while
        // SelectedClass is ClassB, CanExport MUST reject the mismatch.
        vm.SetLoadedChainForTest(new List<GetClassInfoResult> { SampleClass("ClassA") });
        Assert.False(vm.ExportCeXmlCommand.CanExecute(null));
        Assert.False(vm.CopyCeXmlCommand.CanExecute(null));

        // Once ClassB's own chain loads, export commands re-enable.
        vm.SetLoadedChainForTest(new List<GetClassInfoResult> { SampleClass("ClassB") });
        Assert.True(vm.ExportCeXmlCommand.CanExecute(null));
        Assert.True(vm.CopyCeXmlCommand.CanExecute(null));
        Assert.True(vm.ExportCsxCommand.CanExecute(null));
        Assert.True(vm.ExportSdkHeaderCommand.CanExecute(null));
    }
}
