// CallMethodMainThreadTests.cs — Phase 20-MT wire-shape pins.
//
// Verifies that BackendAdapter.CallMethodAsync correctly toggles the
// `main_thread` / `main_thread_timeout_ms` args based on its parameters,
// and that the response shape's `dispatch` sub-object round-trips.
// Mirrors the fake-pipe pattern from SearchSymbolsTests.

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

public class CallMethodMainThreadTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_CMMT_{Guid.NewGuid():N}";
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
    public async Task CallMethod_DefaultPath_OmitsMainThreadField()
    {
        // Phase 19 callers (Math.Abs / String.Concat) shouldn't pay the
        // queue+wait overhead. The legacy default of `mainThread: false`
        // must not include `main_thread` in the wire args — the DLL
        // doesn't care, but the absence is a regression guard against
        // accidental opt-in via a future code change.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "ok": true,
          "returnType": "System.Int32",
          "returnValue": 42,
          "elapsedMs": 1,
          "method": { "address": "0x0000000000000000", "signature": "(System.Int32)", "humanSuffix": "" }
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter.CallMethodAsync(
                "UnityEngine.CoreModule", "UnityEngine", "Mathf", "Abs",
                new System.Collections.Generic.List<CallArgRequest>(),
                ct: ct).ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");

            Assert.False(args.TryGetProperty("main_thread", out _),
                         "main_thread must be omitted when caller defaults the param");
            Assert.False(args.TryGetProperty("main_thread_timeout_ms", out _),
                         "main_thread_timeout_ms must be omitted when caller defaults the param");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CallMethod_MainThreadTrue_EmitsBooleanAndOptionalTimeout()
    {
        // The opt-in path must thread `main_thread: true` AND, if the
        // caller passes a custom timeout, ship that too. Together these
        // are the two args the DLL handler reads for Phase 20-MT.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "ok": true,
          "returnType": "System.Void",
          "returnValue": null,
          "elapsedMs": 16,
          "method":   { "address": "0x0000000000000000", "signature": "()", "humanSuffix": "" },
          "dispatch": { "main_thread": true, "path": "drained" }
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter.CallMethodAsync(
                "UnityEngine.CoreModule", "UnityEngine", "Transform", "get_parent",
                new System.Collections.Generic.List<CallArgRequest>(),
                instanceAddress: "0x1234ABCD",
                mainThread: true,
                mainThreadTimeoutMs: 2500,
                ct: ct).ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");

            Assert.True(args.TryGetProperty("main_thread", out var mt));
            Assert.Equal(JsonValueKind.True, mt.ValueKind);
            Assert.True(args.TryGetProperty("main_thread_timeout_ms", out var to));
            Assert.Equal(2500, to.GetInt32());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CallMethod_MainThreadTrue_NoTimeoutOverride_OmitsTimeoutField()
    {
        // When the caller wants main-thread routing but is fine with the
        // DLL's default 5000 ms timeout, `main_thread_timeout_ms` must
        // be omitted so the DLL applies its own default rather than
        // some accidental sentinel like 0.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "ok": true, "returnType": "System.Void", "returnValue": null, "elapsedMs": 12,
          "method": { "address": "0x0", "signature": "()", "humanSuffix": "" } }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter.CallMethodAsync(
                "UnityEngine.CoreModule", "UnityEngine", "Mathf", "Abs",
                new System.Collections.Generic.List<CallArgRequest>(),
                mainThread: true,
                ct: ct).ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");

            Assert.True(args.TryGetProperty("main_thread", out var mt));
            Assert.Equal(JsonValueKind.True, mt.ValueKind);
            Assert.False(args.TryGetProperty("main_thread_timeout_ms", out _),
                         "timeout must be omitted when caller doesn't override");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
