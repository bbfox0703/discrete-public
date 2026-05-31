// FindFieldReferencesTests.cs — Phase 28 fake-pipe smoke.
//
// Verifies the wire shape that BackendAdapter.FindFieldReferencesAsync
// emits against the DLL, and that the source-gen deserializer round-trips
// the Mono + IL2CPP response shapes documented in pipe-protocol.md. The
// fake-pipe harness mirrors SearchSymbolsTests so each new RPC gets the
// same coverage pattern without test-helper drift.

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

public class FindFieldReferencesTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_FFR_{Guid.NewGuid():N}";
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
    public async Task FindFieldReferences_MonoShape_DecodesIlOffsetAndExactKind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Mono response: ldfld + stfld pair on the same field, address null
        // (Mono JIT not run during bulk scan), ilOffset populated.
        string response = """
        {
          "ok": true,
          "field": {
            "name":     "HP",
            "type":     "System.Int32",
            "offset":   16,
            "isStatic": false,
            "token":    67108865
          },
          "references": [
            { "assembly": "Assembly-CSharp", "namespace": "MainGame",
              "class": "PLY_WORK", "method": "TakeDamage",
              "methodToken": 100663553, "methodFlags": 134, "isMethodStatic": false,
              "isStatic": false, "kind": "read", "confidence": "exact",
              "ilOffset": 35, "address": null },
            { "assembly": "Assembly-CSharp", "namespace": "MainGame",
              "class": "PLY_WORK", "method": "TakeDamage",
              "methodToken": 100663553, "methodFlags": 134, "isMethodStatic": false,
              "isStatic": false, "kind": "write", "confidence": "exact",
              "ilOffset": 42, "address": null }
          ],
          "scanned_methods": 1284,
          "truncated": false
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            FindFieldReferencesResult result = await adapter.FindFieldReferencesAsync(
                "Assembly-CSharp", "MainGame", "PLY_WORK", "HP",
                kind: "any", limit: 200, ct)
                .ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("findFieldReferences", doc.RootElement.GetProperty("cmd").GetString());
            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("Assembly-CSharp", args.GetProperty("assembly").GetString());
            Assert.Equal("MainGame",        args.GetProperty("ns").GetString());
            Assert.Equal("PLY_WORK",        args.GetProperty("class").GetString());
            Assert.Equal("HP",              args.GetProperty("fieldName").GetString());
            Assert.Equal("any",             args.GetProperty("kind").GetString());
            Assert.Equal(200,               args.GetProperty("limit").GetInt32());

            Assert.True(result.Ok);
            Assert.Equal(1284, result.ScannedMethods);
            Assert.False(result.Truncated);

            Assert.NotNull(result.Field);
            Assert.Equal("HP",                result.Field!.Name);
            Assert.Equal("System.Int32",      result.Field.Type);
            Assert.Equal(16,                  result.Field.Offset);
            Assert.False(result.Field.IsStatic);
            Assert.Equal(67108865u,           result.Field.Token);
            Assert.Null(result.Field.StaticAddress);  // Mono shape

            Assert.Equal(2, result.References.Count);
            Assert.Equal("read",  result.References[0].Kind);
            Assert.Equal(35u,     result.References[0].IlOffset);
            Assert.Null(result.References[0].Address);
            Assert.Equal("exact", result.References[0].Confidence);
            Assert.Equal("write", result.References[1].Kind);
            Assert.Equal(42u,     result.References[1].IlOffset);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FindFieldReferences_IL2CPPShape_DecodesStaticAddressAndHitAddress()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // IL2CPP response: static field, response carries staticAddress on
        // the field header + each row carries the enclosing method's AOT
        // address AND the exact hit IP. ilOffset is absent (RIP scan
        // doesn't know IL offsets — native scan).
        string response = """
        {
          "ok": true,
          "field": {
            "name":          "InstanceCount",
            "type":          "System.Int32",
            "offset":        0,
            "isStatic":      true,
            "staticAddress": "0x00007FF876200000"
          },
          "references": [
            { "assembly": "Assembly-CSharp", "namespace": "Game",
              "class": "Player", "method": "RegisterPlayer",
              "methodToken": 100663600, "methodFlags": 22, "isMethodStatic": true,
              "isStatic": true, "kind": "read", "confidence": "exact",
              "address": "0x00007FF876123450", "hitAddress": "0x00007FF876123460" }
          ],
          "scanned_methods": 4523,
          "truncated": false
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            FindFieldReferencesResult result = await adapter.FindFieldReferencesAsync(
                "Assembly-CSharp", "Game", "Player", "InstanceCount",
                kind: "read", limit: 100, ct)
                .ConfigureAwait(true);

            await serverTask.ConfigureAwait(true);

            Assert.True(result.Ok);
            Assert.NotNull(result.Field);
            Assert.True(result.Field!.IsStatic);
            Assert.Equal("0x00007FF876200000", result.Field.StaticAddress);
            Assert.Null(result.Field.Token);  // IL2CPP shape

            Assert.Single(result.References);
            var row = result.References[0];
            Assert.Equal("read",                  row.Kind);
            Assert.Equal("0x00007FF876123450",    row.Address);
            Assert.Equal("0x00007FF876123460",    row.HitAddress);
            Assert.Null(row.IlOffset);  // IL2CPP doesn't carry IL offset
            Assert.True(row.IsMethodStatic);
            Assert.True(row.IsStatic);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FindFieldReferences_KindFilter_PropagatesArgVerbatim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "ok": true, "field": { "name": "x" }, "references": [],
          "scanned_methods": 0, "truncated": false }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter.FindFieldReferencesAsync(
                "Assembly-CSharp", null, "C", "x",
                kind: "write", limit: null, ct)
                .ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("write", args.GetProperty("kind").GetString());
            // Empty namespace passes through as "" (Mono accepts it for
            // top-level types).
            Assert.Equal("", args.GetProperty("ns").GetString());
            // Limit omitted → not sent → DLL uses its 200 default.
            Assert.False(args.TryGetProperty("limit", out _));
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FindFieldReferences_IL2CPPInstanceShape_DecodesHeuristicConfidence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Phase 28b instance path: the DLL now Zydis-decodes the declaring
        // class's own methods for `[reg+fieldOffset]` accesses and returns
        // confidence="heuristic" rows (instead of the old v1 error). The
        // field header carries isStatic=false and NO staticAddress (instance
        // fields have no single storage address); rows carry address (method
        // AOT entry) + hitAddress (exact insn IP) but NO ilOffset (native
        // scan). This verifies the deserializer round-trips that shape.
        string response = """
        {
          "ok": true,
          "field": {
            "name":     "currentHp",
            "type":     "System.Int32",
            "offset":   24,
            "isStatic": false
          },
          "references": [
            { "assembly": "Assembly-CSharp", "namespace": "Game",
              "class": "Player", "method": "TakeDamage",
              "methodToken": 100663700, "methodFlags": 134, "isMethodStatic": false,
              "isStatic": false, "kind": "read", "confidence": "heuristic",
              "address": "0x00007FF876123450", "hitAddress": "0x00007FF876123461" },
            { "assembly": "Assembly-CSharp", "namespace": "Game",
              "class": "Player", "method": "TakeDamage",
              "methodToken": 100663700, "methodFlags": 134, "isMethodStatic": false,
              "isStatic": false, "kind": "write", "confidence": "heuristic",
              "address": "0x00007FF876123450", "hitAddress": "0x00007FF876123468" }
          ],
          "scanned_methods": 87,
          "truncated": false
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            FindFieldReferencesResult result = await adapter.FindFieldReferencesAsync(
                "Assembly-CSharp", "Game", "Player", "currentHp",
                kind: "any", limit: 200, ct)
                .ConfigureAwait(true);

            await serverTask.ConfigureAwait(true);

            Assert.True(result.Ok);
            Assert.NotNull(result.Field);
            Assert.False(result.Field!.IsStatic);          // instance field
            Assert.Null(result.Field.StaticAddress);       // no single storage addr
            Assert.Null(result.Field.Token);               // IL2CPP shape (no Mono token)
            Assert.Equal(24, result.Field.Offset);

            Assert.Equal(2, result.References.Count);
            var read = result.References[0];
            Assert.Equal("read",      read.Kind);
            Assert.Equal("heuristic", read.Confidence);    // <- the key assertion
            Assert.False(read.IsStatic);
            Assert.Equal("0x00007FF876123461", read.HitAddress);
            Assert.Equal("0x00007FF876123450", read.Address);
            Assert.Null(read.IlOffset);                    // native scan, no IL offset
            Assert.Equal("write",     result.References[1].Kind);
            Assert.Equal("heuristic", result.References[1].Confidence);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
