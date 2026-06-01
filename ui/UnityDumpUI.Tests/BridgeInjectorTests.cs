// BridgeInjectorTests.cs — Phase 22d verification.
//
// Drives BridgeInjector with a hand-rolled fake IAobMakerBridge that
// records every method call + lets the test scenario script the
// success/failure outcome of each step. Avoids spinning up
// NamedPipeServerStream because what we're testing is the orchestration
// (call order, abort-on-failure semantics, progress reporting), not
// the wire format — that's already pinned in AobMakerBridgeTests.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class BridgeInjectorTests
{
    [Fact]
    public async Task InjectAsync_HappyPath_FiresCheckThenAllFilesThenAaScript()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge();

        // SyncProgress runs Report synchronously on the calling thread,
        // unlike Progress<T> which posts to the captured SyncContext —
        // xUnit tests run without a SyncContext so the post lands on a
        // pool thread asynchronously, occasionally losing the final
        // report by the time the await returns.
        var report = new SyncProgress<BridgeInjectProgress>();

        var result = await BridgeInjector.InjectAsync(bridge, report, ct);

        Assert.True(result.Ok);
        Assert.True(result.AaScriptCreated);
        Assert.Equal(BridgeLuaResources.LoadOrder.Count, result.FilesInjected);
        Assert.Null(result.FailedStepDescription);
        Assert.Null(result.ErrorMessage);

        // Call sequence: 1× CheckAvailability, N× InjectTableFile (in
        // LoadOrder), 1× CreateAAScript (bootstrap). Nothing else.
        Assert.Equal(1, bridge.CheckAvailabilityCount);
        Assert.Equal(BridgeLuaResources.LoadOrder.Count, bridge.InjectTableFileCalls.Count);
        Assert.Single(bridge.CreateAAScriptCalls);

        // File order matches LoadOrder.
        for (int i = 0; i < BridgeLuaResources.LoadOrder.Count; ++i)
        {
            Assert.Equal(BridgeLuaResources.LoadOrder[i],
                         bridge.InjectTableFileCalls[i].FileName);
            // Content sanity: every embedded bridge file starts with a
            // lua banner; passing it through verbatim should preserve that.
            Assert.StartsWith("-- =", bridge.InjectTableFileCalls[i].Content);
        }

        // Bootstrap AA: description + ENABLE/DISABLE blocks. Its [DISABLE]
        // performs the dormant teardown via discreteShutdown().
        var (aaDesc, aaScript, aaAuto) = bridge.CreateAAScriptCalls[0];
        Assert.Equal(BootstrapAaScriptGenerator.EntryDescription, aaDesc);
        Assert.Contains("[ENABLE]", aaScript);
        Assert.Contains("[DISABLE]", aaScript);
        Assert.Contains("discreteShutdown", aaScript);
        // autoActivate=false so the user sees the new CE entry first
        // and can verify game/DLL state before flipping the checkbox.
        Assert.False(aaAuto);
    }

    [Fact]
    public async Task InjectAsync_Subset_InjectsOnlySelectedFiles_AndAaLoadsOnlyThose()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge();

        // Core + Invoke only — the feature-picker's smallest useful pick
        // beyond core.
        var subset = BridgeLuaResources.ResolveFiles(
            new[] { BridgeLuaResources.BridgeFeature.Invoke });

        var result = await BridgeInjector.InjectAsync(bridge, subset, progress: null, ct: ct);

        Assert.True(result.Ok);
        Assert.Equal(subset.Count, result.FilesInjected);

        // Only the subset files were injected, in order.
        Assert.Equal(subset.Count, bridge.InjectTableFileCalls.Count);
        for (int i = 0; i < subset.Count; ++i)
            Assert.Equal(subset[i], bridge.InjectTableFileCalls[i].FileName);

        // Helpers NOT picked must be absent from the CT.
        Assert.DoesNotContain(bridge.InjectTableFileCalls,
            c => c.FileName == "unity_scene.lua");
        Assert.DoesNotContain(bridge.InjectTableFileCalls,
            c => c.FileName == "unity_dissect.lua");

        // The bootstrap AA's load list must match the injected subset —
        // never reference a file the CT doesn't carry.
        var (_, aaScript, _) = bridge.CreateAAScriptCalls[0];
        Assert.Contains("'unity_invoke.lua'", aaScript);
        Assert.DoesNotContain("'unity_scene.lua'",   aaScript);
        Assert.DoesNotContain("'unity_dissect.lua'", aaScript);
    }

    [Fact]
    public async Task InjectAsync_ProgressReporting_OneStepPerCall()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge();
        var report = new SyncProgress<BridgeInjectProgress>();
        await BridgeInjector.InjectAsync(bridge, report, ct);

        // Total = 1 check + N files + 1 AA (bootstrap). 1-based.
        int n        = BridgeLuaResources.LoadOrder.Count;
        int expected = 1 + n + 1;
        Assert.Equal(expected, report.Reports.Count);
        for (int i = 0; i < report.Reports.Count; ++i)
        {
            Assert.Equal(i + 1, report.Reports[i].Index);
            Assert.Equal(expected, report.Reports[i].Total);
        }

        Assert.StartsWith("Checking", report.Reports[0].Description);
        // Indices (0-based): 0 check, 1..n files, n+1 bootstrap.
        string lastFile = BridgeLuaResources.LoadOrder[n - 1];
        Assert.StartsWith("Injecting " + lastFile,
            report.Reports[n].Description); // last file step
        Assert.StartsWith("Creating " + BootstrapAaScriptGenerator.EntryDescription,
            report.Reports[n + 1].Description);
    }

    [Fact]
    public async Task InjectAsync_AbortsWhenAvailabilityCheckFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge { CheckAvailabilityResult = false };
        var result = await BridgeInjector.InjectAsync(bridge, progress: null, ct: ct);

        Assert.False(result.Ok);
        Assert.Equal(0, result.FilesInjected);
        Assert.False(result.AaScriptCreated);
        Assert.Equal(1, bridge.CheckAvailabilityCount);
        Assert.Empty(bridge.InjectTableFileCalls);
        Assert.Empty(bridge.CreateAAScriptCalls);
        Assert.Contains("AOBMaker", result.ErrorMessage!);
    }

    [Fact]
    public async Task InjectAsync_AbortsOnFirstFileInjectFailure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge();
        // Fail on the third file (index 2 = unity_dissect.lua per LoadOrder).
        // Surfaces a plugin-side message so we can also pin that we forward it.
        bridge.InjectTableFileFailureFor[BridgeLuaResources.LoadOrder[2]] =
            "Stream size mismatch: wrote 9456, stream has 0";

        var result = await BridgeInjector.InjectAsync(bridge, progress: null, ct: ct);

        Assert.False(result.Ok);
        Assert.Equal(2, result.FilesInjected);    // first 2 succeeded
        Assert.False(result.AaScriptCreated);
        Assert.Equal("Stream size mismatch: wrote 9456, stream has 0",
                     result.ErrorMessage);
        Assert.Contains(BridgeLuaResources.LoadOrder[2],
                        result.FailedStepDescription!);

        // Confirm we stopped IMMEDIATELY at the failed file — no
        // subsequent injects, no AA script attempt.
        Assert.Equal(3, bridge.InjectTableFileCalls.Count);
        Assert.Empty(bridge.CreateAAScriptCalls);
    }

    [Fact]
    public async Task InjectAsync_AbortsWhenAaScriptCreationFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge { CreateAAScriptResult = false };
        var result = await BridgeInjector.InjectAsync(bridge, progress: null, ct: ct);

        Assert.False(result.Ok);
        Assert.Equal(BridgeLuaResources.LoadOrder.Count, result.FilesInjected);
        Assert.False(result.AaScriptCreated);
        Assert.Contains("CreateAAScript", result.ErrorMessage!);
        Assert.Contains(BootstrapAaScriptGenerator.EntryDescription,
                        result.FailedStepDescription!);
    }

    [Fact]
    public async Task InjectAsync_PropagatesCancellationBetweenSteps()
    {
        var bridge = new FakeBridge();
        var cts    = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => BridgeInjector.InjectAsync(bridge, progress: null, ct: cts.Token));

        // Pre-cancellation contract: no inject / no AA, period. The
        // availability check IS allowed to fire (it's reported via
        // progress before the throw point) but the bridge methods that
        // mutate CE state must not run.
        Assert.Empty(bridge.InjectTableFileCalls);
        Assert.Empty(bridge.CreateAAScriptCalls);
    }

    [Fact]
    public async Task InjectAsync_ThrowsOnNullBridge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => BridgeInjector.InjectAsync(null!, progress: null, ct: ct));
    }

    // ---------- Synchronous IProgress<T> --------------------------------
    //
    // Progress<T>.Report posts to the captured SynchronizationContext;
    // xUnit tests run without one, so the post lands on the thread pool
    // and the final report can race the await BridgeInjector returns
    // on. This helper records synchronously instead — what test code
    // actually wants.

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) { lock (Reports) Reports.Add(value); }
    }

    // ---------- Fake bridge ---------------------------------------------

    private sealed class FakeBridge : IAobMakerBridge
    {
        public bool   IsAvailable { get; private set; }
        public event EventHandler<bool>? AvailabilityChanged;

        public bool   CheckAvailabilityResult { get; set; } = true;
        public bool   CreateAAScriptResult    { get; set; } = true;
        /// <summary>fileName → plugin-side error message. Files not in this
        /// dict succeed.</summary>
        public Dictionary<string, string> InjectTableFileFailureFor { get; }
            = new(StringComparer.Ordinal);

        public int CheckAvailabilityCount { get; private set; }
        public List<(string FileName, string Content)> InjectTableFileCalls { get; } = new();
        public List<(string Description, string Script, bool AutoActivate)> CreateAAScriptCalls { get; } = new();

        public Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
        {
            ++CheckAvailabilityCount;
            IsAvailable = CheckAvailabilityResult;
            AvailabilityChanged?.Invoke(this, IsAvailable);
            return Task.FromResult(CheckAvailabilityResult);
        }

        public Task<(bool Ok, string? ErrorMessage)> InjectTableFileAsync(
            string fileName, string content, CancellationToken ct = default)
        {
            InjectTableFileCalls.Add((fileName, content));
            if (InjectTableFileFailureFor.TryGetValue(fileName, out string? msg))
                return Task.FromResult((false, (string?)msg));
            return Task.FromResult((true, (string?)null));
        }

        public Task<bool> CreateAAScriptAsync(string description, string script,
                                              bool autoActivate = false,
                                              CancellationToken ct = default)
        {
            CreateAAScriptCalls.Add((description, script, autoActivate));
            return Task.FromResult(CreateAAScriptResult);
        }

        // Unused by BridgeInjector but required by the interface.
        public Task<bool> NavigateHexViewAsync(string a, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> NavigateDisassemblerAsync(string a, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CreateMemoryRecordAsync(string d, string a, CeValueType v, bool s = false, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CreateAAScriptWithRecordsAsync(string d, string s, IReadOnlyList<AaScriptChildRecord> r, bool aa = false, bool hc = true, CancellationToken ct = default) => Task.FromResult(true);
    }
}
