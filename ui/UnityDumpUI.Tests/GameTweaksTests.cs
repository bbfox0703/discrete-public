// GameTweaksTests.cs — universal speed (Time.timeScale) + gravity
// (Physics.gravity) tweaks. Pins the callMethod wire shape at the
// GameTweaksService layer (static class, no instance, float / Vector3 arg,
// correct assembly + signatureMatch) and the TeleportViewModel commands
// (apply / reset / preset, base-gravity capture, disconnect guard).
//
// Reuses the TeleportFakePipe harness (BackendAdapter over a fake pipe that
// answers a queue of canned callMethod results and captures every request).

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class GameTweaksTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public GameTweaksTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameTweaksTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private const string VoidOk = """
        { "ok": true, "returnType": "System.Void", "returnValue": null,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string NumOk(string numLiteral) => $$"""
        { "ok": true, "returnType": "System.Single", "returnValue": {{numLiteral}},
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string StructOk(string hex) => $$"""
        { "ok": true, "returnType": "UnityEngine.Vector3", "returnValue": null,
          "returnStructBytes": "{{hex}}", "returnStructSize": 12,
          "elapsedMs": 1, "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;

    private static string Cmd(string requestLine)
    {
        using var doc = JsonDocument.Parse(requestLine);
        return doc.RootElement.GetProperty("cmd").GetString()!;
    }

    // ── GameTweaksService wire ───────────────────────────────────────────────

    [Fact]
    public async Task SetTimeScale_CallsTimeSetTimeScale_StaticFloatArg()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var svc = new GameTweaksService(fp.Adapter);
            (bool ok, string? error) = await svc.SetTimeScaleAsync(0.5f, ct).ConfigureAwait(true);
            Assert.True(ok);
            Assert.Null(error);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal("callMethod", Cmd(reqs[0]));
            JsonElement p = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("UnityEngine.CoreModule", p.GetProperty("assembly").GetString());
            Assert.Equal("Time",                   p.GetProperty("name").GetString());
            Assert.Equal("set_timeScale",          p.GetProperty("method").GetString());
            Assert.Equal("(System.Single)",        p.GetProperty("signatureMatch").GetString());
            Assert.Equal(JsonValueKind.True,       p.GetProperty("main_thread").ValueKind);
            Assert.False(p.TryGetProperty("instance", out _));   // static — no instance
            JsonElement a0 = p.GetProperty("args")[0];
            Assert.Equal("float", a0.GetProperty("type").GetString());
            Assert.Equal(0.5, a0.GetProperty("value").GetDouble(), 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task SetGravity_CallsPhysicsSetGravity_Vector3ValueStruct()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var svc = new GameTweaksService(fp.Adapter);
            (bool ok, string? _) = await svc.SetGravityAsync(1f, -2f, 3f, ct).ConfigureAwait(true);
            Assert.True(ok);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            JsonElement p = TeleportFakePipe.Params(reqs[0]);
            Assert.Equal("UnityEngine.PhysicsModule", p.GetProperty("assembly").GetString());
            Assert.Equal("Physics",                   p.GetProperty("name").GetString());
            Assert.Equal("set_gravity",               p.GetProperty("method").GetString());
            Assert.Equal("(UnityEngine.Vector3)",     p.GetProperty("signatureMatch").GetString());
            JsonElement a0 = p.GetProperty("args")[0];
            Assert.Equal("value-struct", a0.GetProperty("type").GetString());
            float[] sent = CallArgFactory.DecodeStructFloats(a0.GetProperty("bytes").GetString(), 3);
            Assert.Equal(1.0, sent[0], 3);
            Assert.Equal(-2.0, sent[1], 3);
            Assert.Equal(3.0, sent[2], 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task GetGravity_DecodesReturnStruct()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string hex = CallArgFactory.Vector3(0f, -9.81f, 0f).Bytes!;
        var fp = await TeleportFakePipe.StartAsync(new[] { StructOk(hex) }, ct);
        try
        {
            var svc = new GameTweaksService(fp.Adapter);
            (float X, float Y, float Z)? g = await svc.GetGravityAsync(ct).ConfigureAwait(true);
            Assert.NotNull(g);
            Assert.Equal(0.0, g!.Value.X, 3);
            Assert.Equal(-9.81, g.Value.Y, 2);
            Assert.Equal(0.0, g.Value.Z, 3);

            JsonElement p = TeleportFakePipe.Params((await fp.ServerTask.ConfigureAwait(true))[0]);
            Assert.Equal("get_gravity", p.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.True, p.GetProperty("returnStruct").ValueKind);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task GetTimeScale_ParsesNumericReturn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { NumOk("0.5") }, ct);
        try
        {
            var svc = new GameTweaksService(fp.Adapter);
            float? s = await svc.GetTimeScaleAsync(ct).ConfigureAwait(true);
            Assert.NotNull(s);
            Assert.Equal(0.5, (double)s!.Value, 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    // ── TeleportViewModel commands ───────────────────────────────────────────

    [Fact]
    public async Task ApplyTimeScale_SendsSetTimeScale()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger) { TimeScaleInput = 0.5 };
            await vm.ApplyTimeScaleCommand.ExecuteAsync(null);

            JsonElement p = TeleportFakePipe.Params((await fp.ServerTask.ConfigureAwait(true))[0]);
            Assert.Equal("set_timeScale", p.GetProperty("method").GetString());
            Assert.Equal(0.5, p.GetProperty("args")[0].GetProperty("value").GetDouble(), 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task ResetTimeScale_SendsOne_AndResetsInput()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var fp = await TeleportFakePipe.StartAsync(new[] { VoidOk }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger) { TimeScaleInput = 3.0 };
            await vm.ResetTimeScaleCommand.ExecuteAsync(null);

            Assert.Equal(1.0, vm.TimeScaleInput, 3);
            JsonElement p = TeleportFakePipe.Params((await fp.ServerTask.ConfigureAwait(true))[0]);
            Assert.Equal("set_timeScale", p.GetProperty("method").GetString());
            Assert.Equal(1.0, p.GetProperty("args")[0].GetProperty("value").GetDouble(), 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task ApplyGravity_CapturesBase_ThenScales()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string baseHex = CallArgFactory.Vector3(0f, -9.81f, 0f).Bytes!;
        var fp = await TeleportFakePipe.StartAsync(new[] { StructOk(baseHex), VoidOk }, ct);
        try
        {
            var vm = new TeleportViewModel(fp.Adapter, _logger) { GravityScaleInput = 0.5 };
            await vm.ApplyGravityCommand.ExecuteAsync(null);

            var reqs = await fp.ServerTask.ConfigureAwait(true);
            Assert.Equal(2, reqs.Count);
            Assert.Equal("get_gravity", TeleportFakePipe.Params(reqs[0]).GetProperty("method").GetString());

            JsonElement setP = TeleportFakePipe.Params(reqs[1]);
            Assert.Equal("set_gravity", setP.GetProperty("method").GetString());
            float[] sent = CallArgFactory.DecodeStructFloats(
                setP.GetProperty("args")[0].GetProperty("bytes").GetString(), 3);
            Assert.Equal(0.0, sent[0], 3);
            Assert.Equal(-4.905, sent[1], 3);   // -9.81 base × 0.5
            Assert.Equal(0.0, sent[2], 3);
        }
        finally { await fp.DisposeAsync().ConfigureAwait(true); }
    }

    [Fact]
    public async Task ApplyTimeScale_WhenDisconnected_ReportsNotConnected()
    {
        var backend = new BackendAdapter(new PipeClient("Discrete_GTTest_" + Guid.NewGuid().ToString("N")));
        var vm = new TeleportViewModel(backend, _logger) { TimeScaleInput = 2.0 };

        await vm.ApplyTimeScaleCommand.ExecuteAsync(null);

        Assert.Contains("Not connected", vm.TimeScaleStatus);
    }
}
