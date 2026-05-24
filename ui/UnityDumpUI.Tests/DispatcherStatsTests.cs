// DispatcherStatsTests.cs — Phase 20-MT v2 fake-pipe smoke.
//
// Pins the wire shape that BackendAdapter.GetDispatcherStatsAsync emits
// against the DLL, and verifies the source-gen deserializer round-trips
// every field the auto-prompt banner consumes. The banner's logic
// (CheckDispatcherHealthAsync in MainWindowViewModel) keys off
// HookInstalled + IsHealthy, so we cover both happy-path and "engine
// paused" shapes.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class DispatcherStatsTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_DS_{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new PipeClient(pipeName);

        Task waitConn = server.WaitForConnectionAsync(ct);
        await client.ConnectAsync(timeoutMs: 2000, ct).ConfigureAwait(true);
        await waitConn.ConfigureAwait(true);

        Task<string> serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\n", AutoFlush = true };
            string requestLine = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            await writer.WriteLineAsync(WrapEnvelope(requestLine, responseJson)).ConfigureAwait(false);
            return requestLine;
        }, ct);

        var adapter = new BackendAdapter(client);
        return (adapter, serverTask, server, client);
    }

    private static string WrapEnvelope(string requestLine, string resultJson)
    {
        using var reqDoc = JsonDocument.Parse(requestLine);
        int id = reqDoc.RootElement.GetProperty("id").GetInt32();
        using var resultDoc = JsonDocument.Parse(resultJson);
        return $"{{\"id\":{id},\"result\":{JsonSerializer.Serialize(resultDoc.RootElement)}}}";
    }

    [Fact]
    public async Task GetDispatcherStats_HealthyShape_DecodesAllCounters()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "initialized": true,
          "hook_installed": true,
          "main_thread_id": 2564,
          "tick_count": 12345,
          "submit_count": 42,
          "drain_count": 42,
          "timeout_count": 0,
          "reject_count": 0,
          "last_tick_age_ms": 16,
          "queue_depth": 0,
          "is_healthy": true
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            DispatcherStatsResult result =
                await adapter.GetDispatcherStatsAsync(ct).ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("getDispatcherStats", doc.RootElement.GetProperty("cmd").GetString());

            Assert.True(result.Initialized);
            Assert.True(result.HookInstalled);
            Assert.True(result.IsHealthy);
            Assert.Equal(2564u,  result.MainThreadId);
            Assert.Equal(12345ul, result.TickCount);
            Assert.Equal(42ul,    result.SubmitCount);
            Assert.Equal(42ul,    result.DrainCount);
            Assert.Equal(0ul,     result.TimeoutCount);
            Assert.Equal(16,      result.LastTickAgeMs);
            Assert.Equal(0u,      result.QueueDepth);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task GetDispatcherStats_FocusPausedShape_HookInstalledButUnhealthy()
    {
        // The exact condition the connection-bar banner keys off:
        // hook is installed but the engine hasn't ticked recently. On
        // a real target this happens whenever runInBackground=false +
        // the game window doesn't have focus. last_tick_age_ms blows up
        // and drain_count == 0 (no work has been picked up yet).
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "initialized": true,
          "hook_installed": true,
          "main_thread_id": 2564,
          "tick_count": 0,
          "submit_count": 0,
          "drain_count": 0,
          "timeout_count": 0,
          "reject_count": 0,
          "last_tick_age_ms": 5000,
          "queue_depth": 0,
          "is_healthy": false
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            DispatcherStatsResult result =
                await adapter.GetDispatcherStatsAsync(ct).ConfigureAwait(true);
            await serverTask.ConfigureAwait(true);

            // The two flags the banner checks together.
            Assert.True(result.HookInstalled);
            Assert.False(result.IsHealthy);

            // tick_count = 0 with hook installed is the smoking gun.
            Assert.Equal(0ul, result.TickCount);
            Assert.Equal(5000, result.LastTickAgeMs);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
