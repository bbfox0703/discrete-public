// SearchSymbolsTests.cs — Phase 25 fake-pipe smoke.
//
// Verifies the wire shape that BackendAdapter.SearchSymbolsAsync emits
// against the DLL, and that the source-gen deserializer round-trips
// the response shape we documented in pipe-protocol.md. The fake pipe
// pattern mirrors ListFieldValuesAcrossInstancesTests so each new RPC
// gets the same coverage shape without test-helper drift.

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

public class SearchSymbolsTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_SS_{Guid.NewGuid():N}";
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
    public async Task SearchSymbols_HappyPath_DecodesMixedKinds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "matches": [
            { "namespace": "Game", "class": "Player", "name": "MaxHP",
              "kind": "field",  "type": "System.Int32", "offset": 64, "static": false },
            { "namespace": "",     "class": "PlayerStats", "name": "get_MaxHP",
              "kind": "method", "token": 100663552, "static": false,
              "address": "0x00007FF876123450" }
          ],
          "scanned_classes": 153,
          "truncated": false
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SearchSymbolsResult result = await adapter.SearchSymbolsAsync(
                "Assembly-CSharp", "MaxHP",
                kinds: new[] { "field", "method" }, limit: 200, ct)
                .ConfigureAwait(true);

            // Wire-shape pin: cmd + args must match what the DLL handler expects.
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("searchSymbols", doc.RootElement.GetProperty("cmd").GetString());
            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("Assembly-CSharp", args.GetProperty("assembly").GetString());
            Assert.Equal("MaxHP",           args.GetProperty("pattern").GetString());
            Assert.Equal(200,               args.GetProperty("limit").GetInt32());
            // Kinds threads through as a JSON array.
            JsonElement kinds = args.GetProperty("kinds");
            Assert.Equal(JsonValueKind.Array, kinds.ValueKind);
            Assert.Equal(2, kinds.GetArrayLength());
            Assert.Equal("field",  kinds[0].GetString());
            Assert.Equal("method", kinds[1].GetString());

            // Result shape.
            Assert.Equal(2, result.Matches.Count);
            Assert.Equal(153, result.ScannedClasses);
            Assert.False(result.Truncated);

            var field = result.Matches[0];
            Assert.Equal("field",        field.Kind);
            Assert.Equal("Game",         field.Namespace);
            Assert.Equal("Player",       field.Class);
            Assert.Equal("MaxHP",        field.Name);
            Assert.Equal("System.Int32", field.Type);
            Assert.Equal(64,             field.Offset);
            Assert.False(field.IsStatic);

            var method = result.Matches[1];
            Assert.Equal("method",                 method.Kind);
            Assert.Equal("",                       method.Namespace);
            Assert.Equal("PlayerStats",            method.Class);
            Assert.Equal("get_MaxHP",              method.Name);
            Assert.Equal("0x00007FF876123450",     method.Address);
            Assert.False(method.IsStatic);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SearchSymbols_OmittingKindsAndLimit_SendsBareMinimumArgs()
    {
        // When the caller doesn't constrain by kind / limit, the adapter
        // must omit those fields entirely so the DLL falls back to its
        // defaults (both kinds, limit 200). Asserting they're absent
        // protects against drift where we might start sending sentinel
        // values that confuse the backend.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "matches": [], "scanned_classes": 0, "truncated": false }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter.SearchSymbolsAsync("UnityEngine.CoreModule", "Foo",
                                                 kinds: null, limit: null, ct)
                .ConfigureAwait(true);
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");

            Assert.True(args.TryGetProperty("assembly", out _));
            Assert.True(args.TryGetProperty("pattern",  out _));
            Assert.False(args.TryGetProperty("kinds",   out _),
                         "kinds must be omitted when caller passes null");
            Assert.False(args.TryGetProperty("limit",   out _),
                         "limit must be omitted when caller passes null");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SearchSymbols_MultiAssembly_EmitsAssembliesArrayInsteadOfSingleAssembly()
    {
        // Phase 25 multi-assembly extension: the IReadOnlyList<string>
        // overload must send `assemblies` as a JSON array AND must NOT
        // include the legacy `assembly` string. Wire shape pin.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "matches": [
            { "namespace": "Game", "class": "Player", "assembly": "Assembly-CSharp",
              "name": "MaxHP", "kind": "field", "type": "System.Int32",
              "offset": 64, "static": false },
            { "namespace": "Mods", "class": "Boost", "assembly": "ThirdPartyMods",
              "name": "get_MaxHP", "kind": "method", "token": 100663552, "static": false }
          ],
          "scanned_classes": 412,
          "truncated": false,
          "missing_assemblies": ["DeadAsm"]
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SearchSymbolsResult result = await adapter.SearchSymbolsAsync(
                new[] { "Assembly-CSharp", "ThirdPartyMods", "DeadAsm" },
                "MaxHP",
                kinds: new[] { "field", "method" }, limit: 500, ct)
                .ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("searchSymbols", doc.RootElement.GetProperty("cmd").GetString());
            JsonElement args = doc.RootElement.GetProperty("args");

            // assemblies present, single assembly absent.
            Assert.False(args.TryGetProperty("assembly", out _),
                "legacy single-arg `assembly` must be omitted when caller passes the array");
            Assert.True(args.TryGetProperty("assemblies", out var assemblies));
            Assert.Equal(JsonValueKind.Array, assemblies.ValueKind);
            Assert.Equal(3, assemblies.GetArrayLength());
            Assert.Equal("Assembly-CSharp", assemblies[0].GetString());
            Assert.Equal("ThirdPartyMods", assemblies[1].GetString());
            Assert.Equal("DeadAsm",        assemblies[2].GetString());

            // Result row carries per-row assembly field.
            Assert.Equal(2, result.Matches.Count);
            Assert.Equal("Assembly-CSharp", result.Matches[0].Assembly);
            Assert.Equal("ThirdPartyMods", result.Matches[1].Assembly);

            // missing_assemblies surfaces unresolved names.
            Assert.NotNull(result.MissingAssemblies);
            Assert.Single(result.MissingAssemblies!);
            Assert.Equal("DeadAsm", result.MissingAssemblies![0]);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SearchSymbols_Truncated_FlagsTruncatedTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "matches": [
            { "namespace": "", "class": "A", "name": "x", "kind": "field",
              "type": "System.Int32", "offset": 0, "static": false }
          ],
          "scanned_classes": 99,
          "truncated": true
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SearchSymbolsResult result = await adapter.SearchSymbolsAsync(
                "Assembly-CSharp", "x", kinds: null, limit: 1, ct)
                .ConfigureAwait(true);
            await serverTask.ConfigureAwait(true);

            Assert.Single(result.Matches);
            Assert.Equal(99, result.ScannedClasses);
            Assert.True(result.Truncated, "truncated flag must surface to the UI");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    // SymbolFinderRow's display projection (ClassDisplay / OffsetLabel /
    // KindLabel) lives in the UI project (not Core) so the xUnit tests
    // here can't reach it without pulling Avalonia into the test deps.
    // That projection is small + covered by the manual UI click-through
    // smoke; if it grows enough to need its own coverage, lift the helper
    // into Core first.
}
