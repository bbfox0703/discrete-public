// ListFieldValuesAcrossInstancesTests.cs — Phase 21a fake-pipe smoke.
//
// Spins up a NamedPipeServerStream that impersonates the injected DLL,
// validates the request envelope the BackendAdapter sends, and replies with
// a hand-crafted ListFieldValuesAcrossInstances result so the source-gen
// deserializer is exercised end to end. Two cases:
//   • happy path — int field decoded inline (`value` carrying a number)
//   • per-row soft error — `error: "unreadable"` for one of the rows.

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

public class ListFieldValuesAcrossInstancesTests
{
    /// <summary>
    /// Construct a connected PipeClient/BackendAdapter pair against a one-shot
    /// in-memory NamedPipeServer. Returns the server-side task that reads one
    /// request line + writes one response line; assertions on the server side
    /// run inside that task and surface via the returned task.
    /// </summary>
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_LFVAI_{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new PipeClient(pipeName);

        Task waitConn = server.WaitForConnectionAsync(ct);
        await client.ConnectAsync(timeoutMs: 2000, ct).ConfigureAwait(true);
        await waitConn.ConfigureAwait(true);

        // Read one request line, write back the supplied JSON response.
        Task<string> serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\n", AutoFlush = true };
            string requestLine = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            await writer.WriteLineAsync(responseLineFor(requestLine, responseJson)).ConfigureAwait(false);
            return requestLine;
        }, ct);

        var adapter = new BackendAdapter(client);
        return (adapter, serverTask, server, client);
    }

    /// <summary>
    /// Build the response line by parsing the request `id` and wrapping the
    /// supplied result JSON in the standard envelope so the source-gen
    /// deserializer accepts it. The pipe is line-delimited, so the response
    /// MUST be a single line — we re-serialise the parsed JSON to compact
    /// form (the raw-string-literal payloads in the tests are pretty-printed
    /// for readability).
    /// </summary>
    private static string responseLineFor(string requestLine, string resultJson)
    {
        using var reqDoc = JsonDocument.Parse(requestLine);
        int id = reqDoc.RootElement.GetProperty("id").GetInt32();
        using var resultDoc = JsonDocument.Parse(resultJson);
        string compactResult = JsonSerializer.Serialize(resultDoc.RootElement);
        return $"{{\"id\":{id},\"result\":{compactResult}}}";
    }

    [Fact]
    public async Task ListFieldValuesAcrossInstances_HappyPath_DecodesIntRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "class_ptr": "0x0000000123456789",
          "count": 2,
          "limit": 256,
          "fieldOffset": 32,
          "fieldType": "System.Int32",
          "rows": [
            { "instance": "0x00007FF600000010", "fieldAddress": "0x00007FF600000030", "value": 5 },
            { "instance": "0x00007FF600000A00", "fieldAddress": "0x00007FF600000A20", "value": 42 }
          ]
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            ListFieldValuesResult result = await adapter
                .ListFieldValuesAcrossInstancesAsync(
                    "Assembly-CSharp", "", "PARTY_WORK",
                    fieldOffset: 32, fieldType: "System.Int32", limit: 256, ct)
                .ConfigureAwait(true);

            // Verify wire shape — the server side captured the request line;
            // the BackendAdapter should have sent the right args.
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("listFieldValuesAcrossInstances", doc.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("Assembly-CSharp",                args.GetProperty("assembly").GetString());
            Assert.Equal("PARTY_WORK",                     args.GetProperty("name").GetString());
            Assert.Equal(32,                               args.GetProperty("fieldOffset").GetInt32());
            Assert.Equal("System.Int32",                   args.GetProperty("fieldType").GetString());
            Assert.Equal(256,                              args.GetProperty("limit").GetInt32());

            Assert.Equal(2, result.Count);
            Assert.Equal(32, result.FieldOffset);
            Assert.Equal("System.Int32", result.FieldType);
            Assert.NotNull(result.Rows);
            Assert.Equal(2, result.Rows!.Count);
            Assert.Equal("0x00007FF600000010", result.Rows[0].Instance);
            Assert.Equal("0x00007FF600000030", result.Rows[0].FieldAddress);
            Assert.NotNull(result.Rows[0].Value);
            Assert.Equal(5,  result.Rows[0].Value!.Value.GetInt32());
            Assert.Equal(42, result.Rows[1].Value!.Value.GetInt32());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ListFieldValuesAcrossInstances_SoftErrorRow_PreservesErrorField()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "class_ptr": "0x0000000123456789",
          "count": 2,
          "limit": 256,
          "fieldOffset": 16,
          "fieldType": "System.Int32",
          "rows": [
            { "instance": "0x00007FF600000100", "fieldAddress": "0x00007FF600000110", "value": 1 },
            { "instance": "0x00007FF600000200", "fieldAddress": "0x00007FF600000210", "error": "unreadable" }
          ]
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            ListFieldValuesResult result = await adapter
                .ListFieldValuesAcrossInstancesAsync(
                    "Assembly-CSharp", "MyNs", "Foo",
                    fieldOffset: 16, fieldType: "System.Int32", limit: null, ct)
                .ConfigureAwait(true);
            await serverTask.ConfigureAwait(true);  // drain

            Assert.NotNull(result.Rows);
            Assert.Equal(2, result.Rows!.Count);
            Assert.Null(result.Rows[1].Value);
            Assert.Equal("unreadable", result.Rows[1].Error);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ListFieldValuesAcrossInstances_PassesNamespaceArg()
    {
        // Regression guard: namespace must thread through as an empty string
        // (not omitted) when null is passed by the caller — matches the
        // FindInstances / WalkInstance convention so the DLL's
        // resolveClassFromArgs sees the expected arg set.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "class_ptr": "0x0", "count": 0, "limit": 256, "fieldOffset": 0, "fieldType": "System.Int32", "rows": [] }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter
                .ListFieldValuesAcrossInstancesAsync(
                    "Assembly-CSharp", null, "Foo",
                    fieldOffset: 0, fieldType: "System.Int32", limit: null, ct)
                .ConfigureAwait(true);
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            Assert.Equal("", doc.RootElement.GetProperty("args").GetProperty("namespace").GetString());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
