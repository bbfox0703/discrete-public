// SceneEnumeratorTests.cs — Phase 20b fake-pipe smoke for SceneEnumerator.
//
// Multi-RPC paths (EnumerateGameObjectAddressesAsync, GetChildrenAsync)
// involve fanout to several pipe RPCs in sequence; those are covered by
// Phase 20d live verification rather than fake-pipe (too much harness
// for too little signal). This file pins the wire shape of the single-
// RPC entry points + the "every Unity icall sets main_thread=true"
// invariant — the critical correctness contract.

using System;
using System.Collections.Generic;
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

public class SceneEnumeratorTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_SE_{Guid.NewGuid():N}";
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
    public async Task GetNameAsync_CallsObjectGetName_WithMainThreadTrue()
    {
        // The exact wire-shape pin Phase 20-MT cares about: every Scene
        // Explorer icall routes through main_thread=true so a sustained
        // burst doesn't lock the game thread. If a future refactor
        // accidentally drops the flag, this test catches it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "ok": true,
          "returnType": "System.String",
          "returnValue": "Player",
          "elapsedMs": 7,
          "method": { "address": "0x0000000000000000", "signature": "()", "humanSuffix": "" }
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            var enumerator = new SceneEnumerator(adapter, new FileLogger(Path.Combine(Path.GetTempPath(), $"DiscreteUITestLogs_{Guid.NewGuid():N}")));
            string? name = await enumerator.GetNameAsync("0x123456789ABCDEF0", ct).ConfigureAwait(true);
            Assert.Equal("Player", name);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("callMethod", doc.RootElement.GetProperty("cmd").GetString());

            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("UnityEngine.CoreModule", args.GetProperty("assembly").GetString());
            Assert.Equal("UnityEngine",            args.GetProperty("namespace").GetString());
            Assert.Equal("Object",                 args.GetProperty("name").GetString());
            Assert.Equal("get_name",               args.GetProperty("method").GetString());
            Assert.Equal("()",                     args.GetProperty("signatureMatch").GetString());
            Assert.Equal("0x123456789ABCDEF0",     args.GetProperty("instance").GetString());
            // Phase 20-MT invariant: main_thread:true must be in the args.
            Assert.True(args.TryGetProperty("main_thread", out var mt));
            Assert.Equal(JsonValueKind.True, mt.ValueKind);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task GetNameAsync_FailedCall_ReturnsNull()
    {
        // Destroyed-wrapper case: get_name throws or returns non-string.
        // Caller iterates over a list and must keep going on each
        // failure — null is the contract.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "ok": false,
          "error": "instance_required_for_non_static",
          "returnType": null,
          "returnValue": null,
          "elapsedMs": 0,
          "method": { "address": "0x0000000000000000", "signature": "()", "humanSuffix": "" }
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            var enumerator = new SceneEnumerator(adapter, new FileLogger(Path.Combine(Path.GetTempPath(), $"DiscreteUITestLogs_{Guid.NewGuid():N}")));
            string? name = await enumerator.GetNameAsync("0xDEADBEEF", ct).ConfigureAwait(true);
            Assert.Null(name);
            await serverTask.ConfigureAwait(true);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task GetNameAsync_EmptyAddress_ShortCircuits()
    {
        // No RPC should fire when address is empty/null — defensive guard
        // for the case where a caller iterates a result set with stale
        // entries. We spin up the fake pipe but never drain it; the
        // server task is left dangling (timed out by the harness).
        var enumerator = new SceneEnumerator(
            new BackendAdapter(new PipeClient($"DiscreteUITest_NEVER_{Guid.NewGuid():N}")),
            new FileLogger(Path.Combine(Path.GetTempPath(), $"DiscreteUITestLogs_{Guid.NewGuid():N}")));
        string? name = await enumerator.GetNameAsync("", CancellationToken.None).ConfigureAwait(true);
        Assert.Null(name);
        name = await enumerator.GetNameAsync(null!, CancellationToken.None).ConfigureAwait(true);
        Assert.Null(name);
    }
}
