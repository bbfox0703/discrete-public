// SetInstanceFieldTests.cs — Phase 21a fake-pipe smoke for the new
// setInstanceField RPC.
//
// Validates two contracts the BackendAdapter has with the DLL:
//   • request wire shape — address / fieldOffset / type / value all surface
//     correctly on the request line, with int / bool / float / address values
//     formatted in the agreed forms (numbers for numerics, JSON true/false
//     for bool, "0x..." string for address).
//   • response deserializer — happy path (Ok=true + Written) and soft-error
//     path (Error populated, Ok=false) both decode without throwing.

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

public class SetInstanceFieldTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string responseJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_SetField_{Guid.NewGuid():N}";
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
            await writer.WriteLineAsync(ResponseLineFor(requestLine, responseJson)).ConfigureAwait(false);
            return requestLine;
        }, ct);

        var adapter = new BackendAdapter(client);
        return (adapter, serverTask, server, client);
    }

    private static string ResponseLineFor(string requestLine, string resultJson)
    {
        using var reqDoc = JsonDocument.Parse(requestLine);
        int id = reqDoc.RootElement.GetProperty("id").GetInt32();
        using var resultDoc = JsonDocument.Parse(resultJson);
        string compactResult = JsonSerializer.Serialize(resultDoc.RootElement);
        return $"{{\"id\":{id},\"result\":{compactResult}}}";
    }

    [Fact]
    public async Task SetInstanceField_Int32_HappyPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "ok": true,
          "address": "0x00007FF600000010",
          "fieldOffset": 32,
          "fieldAddress": "0x00007FF600000030",
          "type": "int32",
          "written": 4
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SetInstanceFieldResult result = await adapter
                .SetInstanceFieldAsync("0x00007FF600000010", fieldOffset: 32,
                    type: "int32", value: SetFieldValueFactory.Int(999), ct)
                .ConfigureAwait(true);

            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement args = doc.RootElement.GetProperty("args");
            Assert.Equal("setInstanceField",      doc.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("0x00007FF600000010",    args.GetProperty("address").GetString());
            Assert.Equal(32,                      args.GetProperty("fieldOffset").GetInt32());
            Assert.Equal("int32",                 args.GetProperty("type").GetString());
            Assert.Equal(999,                     args.GetProperty("value").GetInt32());

            Assert.True(result.Ok);
            Assert.Equal(4, result.Written);
            Assert.Equal("0x00007FF600000030", result.FieldAddress);
            Assert.Null(result.Error);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SetInstanceField_Bool_RoundTripsAsJsonBool()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "ok": true, "address": "0x1", "fieldOffset": 0, "fieldAddress": "0x1", "type": "bool", "written": 1 }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter
                .SetInstanceFieldAsync("0x1", 0, "bool", SetFieldValueFactory.Bool(true), ct)
                .ConfigureAwait(true);
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement value = doc.RootElement.GetProperty("args").GetProperty("value");
            Assert.Equal(JsonValueKind.True, value.ValueKind);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SetInstanceField_Float_RoundTripsAsJsonNumber()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "ok": true, "address": "0x2", "fieldOffset": 4, "fieldAddress": "0x6", "type": "float", "written": 4 }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter
                .SetInstanceFieldAsync("0x2", 4, "float", SetFieldValueFactory.Float(3.14), ct)
                .ConfigureAwait(true);
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement value = doc.RootElement.GetProperty("args").GetProperty("value");
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.Equal(3.14, value.GetDouble(), precision: 6);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SetInstanceField_Address_RoundTripsAsHexString()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "ok": true, "address": "0x3", "fieldOffset": 0, "fieldAddress": "0x3", "type": "address", "written": 8 }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            _ = await adapter
                .SetInstanceFieldAsync("0x3", 0, "address",
                    SetFieldValueFactory.Address("0x00007FFE12345678"), ct)
                .ConfigureAwait(true);
            string requestLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(requestLine);
            JsonElement value = doc.RootElement.GetProperty("args").GetProperty("value");
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            Assert.Equal("0x00007FFE12345678", value.GetString());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SetInstanceField_SoftError_DeserializesCleanly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        {
          "error": "memory not writable",
          "address": "0xDEADBEEF",
          "fieldOffset": 16,
          "fieldAddress": "0xDEADBEFF",
          "type": "int32",
          "size": 4
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SetInstanceFieldResult result = await adapter
                .SetInstanceFieldAsync("0xDEADBEEF", 16, "int32",
                    SetFieldValueFactory.Int(42), ct)
                .ConfigureAwait(true);
            await serverTask.ConfigureAwait(true);

            Assert.False(result.Ok);
            Assert.Equal("memory not writable", result.Error);
            Assert.Equal("0xDEADBEFF", result.FieldAddress);
            Assert.Equal(4, result.Size);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SetInstanceField_OutOfRangeError_SurfacesHint()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string response = """
        { "error": "'value' out of range for int8", "hint": null }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SetInstanceFieldResult result = await adapter
                .SetInstanceFieldAsync("0x1", 0, "int8",
                    SetFieldValueFactory.Int(200), ct)
                .ConfigureAwait(true);
            await serverTask.ConfigureAwait(true);

            Assert.False(result.Ok);
            Assert.Equal("'value' out of range for int8", result.Error);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
