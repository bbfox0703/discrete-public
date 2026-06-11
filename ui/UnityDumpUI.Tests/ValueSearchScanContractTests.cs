// ValueSearchScanContractTests — regression coverage for the value-scan wire
// contract + status-note decouple fixed in the recent audit batch:
//
//   * ParallelArg (efd19b0): the optional "parallel" arg is OMITTED (null) when
//     parallel scan is ON, so the DLL's own `args.value("parallel", true)`
//     default applies; it is sent as `false` ONLY when the user turns parallel
//     OFF (server then caps to one thread). Guards against regressing to always
//     transmitting a redundant `true`.
//   * RetainNote / ScanTruncationNote (1aabf94): the First-Scan status line
//     decouples managed candidates RETAINED for refine from the rows actually
//     shown, and names WHY a result set was capped, so the user knows whether
//     to raise Max candidates or just refine.
//
// Pure logic — no pipe / DLL. ParallelArg is read off a constructed VM (fake,
// never-connecting pipe, exactly as InspectorSortTreeTests build theirs); the
// note helpers are static. All three are internal — visible here via the UI
// project's InternalsVisibleTo("UnityDumpUI.Tests").

using System;
using System.IO;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ValueSearchScanContractTests : IDisposable
{
    private readonly string _tempDir;

    public ValueSearchScanContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ValueSearchContractTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private ValueSearchViewModel BuildVm()
    {
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + Guid.NewGuid().ToString("N")));
        return new ValueSearchViewModel(backend, logger);
    }

    // ─── ParallelArg wire contract (efd19b0) ───────────────────────────

    [Fact]
    public void ParallelArg_Omitted_ByDefault_BecauseParallelScanDefaultsOn()
    {
        var vm = BuildVm();
        Assert.True(vm.ParallelScan);            // default ON (cores−2 workers)
        Assert.False(vm.ParallelArg.HasValue);   // omitted → DLL default = parallel
    }

    [Fact]
    public void ParallelArg_Omitted_WhenParallelScanOn()
    {
        var vm = BuildVm();
        vm.ParallelScan = true;
        Assert.False(vm.ParallelArg.HasValue);
    }

    [Fact]
    public void ParallelArg_SentAsFalse_WhenParallelScanOff()
    {
        var vm = BuildVm();
        vm.ParallelScan = false;
        Assert.True(vm.ParallelArg.HasValue);    // present on the wire …
        Assert.False(vm.ParallelArg!.Value);     // … as false → server caps to one thread
    }

    // ─── RetainNote — retain/display decouple (1aabf94) ────────────────

    [Theory]
    [InlineData(100, 100)]   // equal — nothing extra retained, nothing to report
    [InlineData(50, 100)]    // fewer retained than shown (defensive)
    public void RetainNote_Empty_WhenRetainedNotAboveShown(int retained, int shown)
        => Assert.Equal(string.Empty, ValueSearchViewModel.RetainNote(retained, shown));

    [Fact]
    public void RetainNote_SurfacesRetainedCount_WhenAboveShown()
    {
        string note = ValueSearchViewModel.RetainNote(retained: 12345, shown: 100);
        // Use the same N0 formatting so the assert is culture-agnostic.
        Assert.Contains($"{12345:N0} retained for refine", note);
    }

    // ─── ScanTruncationNote — name the cap reason (1aabf94) ────────────

    [Fact]
    public void ScanTruncationNote_Empty_WhenNeitherCapHit()
        => Assert.Equal(string.Empty,
            ValueSearchViewModel.ScanTruncationNote(
                rawTruncated: false, retainTruncated: false, retainCap: 50_000, maxCandidates: 200_000));

    [Fact]
    public void ScanTruncationNote_RawCap_TakesPriorityOverRetainCap()
    {
        // Both flags set: the raw-scan cap is the more fundamental limit, so it
        // wins and the retain-cap message must NOT also appear.
        string note = ValueSearchViewModel.ScanTruncationNote(
            rawTruncated: true, retainTruncated: true, retainCap: 50_000, maxCandidates: 200_000);
        Assert.Contains("raw scan capped", note);
        Assert.Contains($"{200_000:N0}", note);          // surfaces Max candidates to raise
        Assert.DoesNotContain("retained set capped", note);
    }

    [Fact]
    public void ScanTruncationNote_RetainCap_WhenOnlyRetainTruncated()
    {
        string note = ValueSearchViewModel.ScanTruncationNote(
            rawTruncated: false, retainTruncated: true, retainCap: 50_000, maxCandidates: 200_000);
        Assert.Contains("retained set capped", note);
        Assert.Contains($"{50_000:N0}", note);           // surfaces the retain cap that was hit
    }
}
