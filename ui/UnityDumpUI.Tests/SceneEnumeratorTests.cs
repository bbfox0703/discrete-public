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

    [Fact]
    public async Task EnumerateGameObjects_CoreModuleNullThenMonolithic_FallsBackAndUsesMonolithicAssemblyDownstream()
    {
        // Phase 20e — when Type.GetType("UnityEngine.GameObject,
        // UnityEngine.CoreModule") returns null (the legacy-Unity
        // monolithic case observed on 活俠傳 / Unity 2020.3.49f1 / Mono
        // x86), SceneEnumerator must fall back to
        // "UnityEngine.GameObject, UnityEngine" AND route every downstream
        // Object.FindObjects* call through assembly="UnityEngine" (not
        // CoreModule). The DLL's findImage step uses the assembly NAME so
        // a CoreModule arg against a monolithic target would surface as
        // "image not found" — the fix is end-to-end, not just the Type
        // probe.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"DiscreteUITest_SE_Mono_{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new PipeClient(pipeName);

        Task waitConn = server.WaitForConnectionAsync(ct);
        await client.ConnectAsync(timeoutMs: 2000, ct).ConfigureAwait(true);
        await waitConn.ConfigureAwait(true);

        // Sequence the server reads + replies for the 4 expected RPCs:
        //   1. callMethod Type.GetType("...UnityEngine.CoreModule") → null
        //   2. callMethod Type.GetType("...UnityEngine") (monolithic)  → valid address
        //   3. callMethod Object.FindObjectsByType (3-arg)              → array address
        //   4. walkArray                                                → 2 elements
        // We capture each request line so the test can assert the
        // downstream `assembly` arg flipped from CoreModule → UnityEngine.
        var captured = new List<string>(4);
        Task serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\n", AutoFlush = true };

            string MakeOkCallMethod(int id, string? returnValue) =>
                returnValue is null
                    ? $"{{\"id\":{id},\"result\":{{\"ok\":true,\"returnType\":null,\"returnValue\":null,\"elapsedMs\":1,\"method\":{{\"address\":\"0x0\",\"signature\":\"(System.String)\",\"humanSuffix\":\"\"}}}}}}"
                    : $"{{\"id\":{id},\"result\":{{\"ok\":true,\"returnType\":\"System.Object\",\"returnValue\":\"{returnValue}\",\"elapsedMs\":1,\"method\":{{\"address\":\"0x0\",\"signature\":\"\",\"humanSuffix\":\"\"}}}}}}";

            // 1. CoreModule probe — DLL returns null returnValue → adapter
            // surfaces .ReturnValue.ValueKind == JsonValueKind.Null, which
            // SceneEnumerator treats as "this probe failed, try the next".
            string line1 = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            captured.Add(line1);
            int id1 = JsonDocument.Parse(line1).RootElement.GetProperty("id").GetInt32();
            await writer.WriteLineAsync(MakeOkCallMethod(id1, returnValue: null)).ConfigureAwait(false);

            // 2. Monolithic probe — returns a valid Type address.
            string line2 = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            captured.Add(line2);
            int id2 = JsonDocument.Parse(line2).RootElement.GetProperty("id").GetInt32();
            await writer.WriteLineAsync(MakeOkCallMethod(id2, returnValue: "0x00000000DEADBEEF")).ConfigureAwait(false);

            // 3. FindObjectsByType — returns array address. The Type-pointer
            // probes downstream return as string-typed so adapter wraps as
            // JsonValueKind.String. We make the array-result the same.
            string line3 = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            captured.Add(line3);
            int id3 = JsonDocument.Parse(line3).RootElement.GetProperty("id").GetInt32();
            await writer.WriteLineAsync(MakeOkCallMethod(id3, returnValue: "0x00000000CAFEBABE")).ConfigureAwait(false);

            // 4. walkArray — single element so the test stays small.
            string line4 = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            captured.Add(line4);
            int id4 = JsonDocument.Parse(line4).RootElement.GetProperty("id").GetInt32();
            string walkArrayResult =
                $"{{\"id\":{id4},\"result\":{{\"address\":\"0x00000000CAFEBABE\",\"length\":1," +
                $"\"count\":1,\"elementType\":\"UnityEngine.Object\",\"elementSize\":8,\"dataAddress\":\"0x00000000CAFEBAC0\"," +
                $"\"elements\":[{{\"index\":0,\"address\":\"0x00000000CAFEBAC0\",\"ptr\":\"0x00000000ABCDEF00\"}}]}}}}";
            await writer.WriteLineAsync(walkArrayResult).ConfigureAwait(false);
        }, ct);

        try
        {
            var adapter = new BackendAdapter(client);
            var enumerator = new SceneEnumerator(adapter,
                new FileLogger(Path.Combine(Path.GetTempPath(), $"DiscreteUITestLogs_{Guid.NewGuid():N}")));
            EnumerateResult result = await enumerator.EnumerateGameObjectAddressesAsync(
                limit: 50, ct).ConfigureAwait(true);

            await serverTask.ConfigureAwait(true);

            Assert.True(result.Ok, $"enumerator should succeed via monolithic fallback; got error: {result.Error}");
            Assert.Single(result.Addresses);
            Assert.Equal("0x00000000ABCDEF00", result.Addresses[0]);

            // Wire-shape pin #1: first probe carries the CoreModule FQN.
            using var doc1 = JsonDocument.Parse(captured[0]);
            JsonElement args1 = doc1.RootElement.GetProperty("args");
            Assert.Equal("Type", args1.GetProperty("name").GetString());
            JsonElement probeArgs1 = args1.GetProperty("args");
            string fqn1 = probeArgs1[0].GetProperty("value").GetString()!;
            Assert.Equal("UnityEngine.GameObject, UnityEngine.CoreModule", fqn1);

            // Wire-shape pin #2: after the CoreModule probe returns null,
            // SceneEnumerator MUST retry with the monolithic FQN — this is
            // the heart of the fix.
            using var doc2 = JsonDocument.Parse(captured[1]);
            JsonElement args2 = doc2.RootElement.GetProperty("args");
            JsonElement probeArgs2 = args2.GetProperty("args");
            string fqn2 = probeArgs2[0].GetProperty("value").GetString()!;
            Assert.Equal("UnityEngine.GameObject, UnityEngine", fqn2);

            // Wire-shape pin #3: the downstream FindObjects* call MUST use
            // assembly="UnityEngine" (the monolithic survivor), NOT
            // "UnityEngine.CoreModule". Without this, the call would
            // surface as "image not found" on a target that genuinely has
            // only the monolithic UnityEngine.dll loaded.
            using var doc3 = JsonDocument.Parse(captured[2]);
            JsonElement args3 = doc3.RootElement.GetProperty("args");
            Assert.Equal("UnityEngine",   args3.GetProperty("assembly").GetString());
            Assert.Equal("UnityEngine",   args3.GetProperty("namespace").GetString());
            Assert.Equal("Object",        args3.GetProperty("name").GetString());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
