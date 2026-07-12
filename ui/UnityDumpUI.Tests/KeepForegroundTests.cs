// KeepForegroundTests.cs — native run-in-background (ForegroundHook) wiring.
// Pins the setKeepForeground / getKeepForeground round-trip at the
// BackendAdapter layer, and the TeleportViewModel toggle → command,
// connect-time state sync, badge mapping, and disconnect-revert behaviour.
//
// Reuses the TeleportFakePipe harness (a BackendAdapter over a fake named pipe
// that answers a queue of canned results and captures every request line) and
// the unconnected-BackendAdapter idiom from TeleportHotkeyTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class KeepForegroundTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public KeepForegroundTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KeepFgTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static (string Cmd, JsonElement Args) Parse(string requestLine)
    {
        using var doc = JsonDocument.Parse(requestLine);
        JsonElement root = doc.RootElement;
        // getKeepForeground sends no args → the request line omits "args".
        JsonElement args = root.TryGetProperty("args", out JsonElement a) ? a.Clone() : default;
        return (root.GetProperty("cmd").GetString()!, args);
    }

    // ── BackendAdapter round-trip ────────────────────────────────────────────

    [Fact]
    public async Task SetKeepForeground_SendsEnableTrue_AndParsesState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": 1 } """ }, ct);
        try
        {
            KeepForegroundResult r = await fp.Adapter.SetKeepForegroundAsync(true, ct).ConfigureAwait(true);
            Assert.Equal(1, r.State);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            (string cmd, JsonElement args) = Parse(reqs[0]);
            Assert.Equal("setKeepForeground", cmd);
            Assert.Equal(JsonValueKind.True, args.GetProperty("enable").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task SetKeepForeground_Disable_SendsEnableFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": 0 } """ }, ct);
        try
        {
            KeepForegroundResult r = await fp.Adapter.SetKeepForegroundAsync(false, ct).ConfigureAwait(true);
            Assert.Equal(0, r.State);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            (string cmd, JsonElement args) = Parse(reqs[0]);
            Assert.Equal("setKeepForeground", cmd);
            Assert.Equal(JsonValueKind.False, args.GetProperty("enable").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task GetKeepForeground_SendsCommand_AndParsesState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": 1 } """ }, ct);
        try
        {
            KeepForegroundResult? r = await fp.Adapter.GetKeepForegroundAsync(ct).ConfigureAwait(true);
            Assert.NotNull(r);
            Assert.Equal(1, r!.State);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("getKeepForeground", Parse(reqs[0]).Cmd);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ── TeleportViewModel toggle / sync / badge ──────────────────────────────

    [Fact]
    public async Task Toggle_On_SendsSetKeepForeground()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": 1 } """ }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger);
            vm.KeepForegroundEnabled = true;   // fires OnChanged → setKeepForeground

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            (string cmd, JsonElement args) = Parse(reqs[0]);
            Assert.Equal("setKeepForeground", cmd);
            Assert.Equal(JsonValueKind.True, args.GetProperty("enable").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Sync_State1_SetsEnabled_AndOnBadge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": 1 } """ }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger);
            await vm.SyncKeepForegroundStateAsync().ConfigureAwait(true);

            Assert.True(vm.KeepForegroundEnabled);
            Assert.Contains("On", vm.KeepForegroundStatus);
            Assert.Equal("getKeepForeground", Parse((await fp.ServerTask.ConfigureAwait(true))[0]).Cmd);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task Sync_NegativeState_ShowsHookError_AndLeavesToggleOff()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { """ { "state": -3 } """ }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger);
            await vm.SyncKeepForegroundStateAsync().ConfigureAwait(true);

            Assert.False(vm.KeepForegroundEnabled);
            Assert.Contains("Hook error", vm.KeepForegroundStatus);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public void Toggle_On_WhenDisconnected_Reverts_AndReportsNotConnected()
    {
        // Unconnected backend → IsConnected false; the revert path is synchronous
        // (no await before it returns), so the toggle is back to false right after.
        var backend = new BackendAdapter(new PipeClient("Discrete_KFTest_" + Guid.NewGuid().ToString("N")));
        var vm = new TeleportViewModel(backend, _logger);

        vm.KeepForegroundEnabled = true;

        Assert.False(vm.KeepForegroundEnabled);
        Assert.Contains("Not connected", vm.KeepForegroundStatus);
    }
}
