// ValueScanWireAndDtoContractTests.cs — C-M6 / C-M7 / C-M8 regression tests.
//
// Verifies:
//   * C-M6: SearchSymbolsResult single-assembly truncated contract
//     (`truncated: true`, `truncated_assemblies: null`) vs multi-assembly.
//   * C-M7: Result-level `error` deserialization on GetAssembliesResult and
//     ClassRangeStatsResult, and `truncated` deserialization on
//     SnapshotClassEntry ("memory_budget" / "instance_budget").
//   * C-M8: Lossless 64-bit Int64 / UInt64 (> 2^53) encoding via
//     ValueScanWireEncoder, fractional rejection for integer scans, and exact
//     JSON integer emission on the wire for BeginValueScanAsync /
//     RefineValueScanAsync / BeginGroupScanAsync.

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

public class ValueScanWireAndDtoContractTests
{
    private static async Task<(BackendAdapter adapter, Task<string> serverTask, NamedPipeServerStream server, PipeClient client)>
        StartPairAsync(string resultJson, CancellationToken ct)
    {
        string pipeName = $"DiscreteUITest_CM678_{Guid.NewGuid():N}";
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
            string reqLine = (await reader.ReadLineAsync(ct).ConfigureAwait(false))!;
            using var doc = JsonDocument.Parse(reqLine);
            int id = doc.RootElement.GetProperty("id").GetInt32();
            using var resultDoc = JsonDocument.Parse(resultJson);
            using var compactBuf = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(compactBuf))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteNumber("id", id);
                jsonWriter.WritePropertyName("result");
                resultDoc.WriteTo(jsonWriter);
                jsonWriter.WriteEndObject();
            }
            string respLine = Encoding.UTF8.GetString(compactBuf.ToArray());
            await writer.WriteLineAsync(respLine).ConfigureAwait(false);
            return reqLine;
        }, ct);

        return (new BackendAdapter(client), serverTask, server, client);
    }

    // --- C-M6: SearchSymbols single-assembly vs multi-assembly truncation ---

    [Fact]
    public async Task SearchSymbols_SingleAssemblyCapped_SetsTruncatedTrueAndOmitsTruncatedAssemblies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string response = """
        {
          "matches": [
            { "namespace": "Game", "class": "Player", "assembly": "Assembly-CSharp",
              "name": "HP", "kind": "field", "type": "System.Int32", "offset": 16, "static": false }
          ],
          "scanned_classes": 1,
          "truncated": true
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SearchSymbolsResult res = await adapter.SearchSymbolsAsync(
                "Assembly-CSharp", "HP", limit: 1, ct: ct).ConfigureAwait(true);
            _ = await serverTask.ConfigureAwait(true);

            Assert.True(res.Truncated);
            Assert.Null(res.TruncatedAssemblies);
            Assert.Single(res.Matches);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    // --- C-M7: Handler-level error & SnapshotClassEntry.Truncated DTOs ---

    [Fact]
    public async Task GetAssemblies_ResultLevelError_SurfacesOnDto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string response = """
        { "error": "mono runtime not resolved", "count": 0, "assemblies": [] }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            GetAssembliesResult res = await adapter.GetAssembliesAsync(ct).ConfigureAwait(true);
            _ = await serverTask.ConfigureAwait(true);

            Assert.Equal("mono runtime not resolved", res.Error);
            Assert.Empty(res.Assemblies);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task GetClassRangeStats_ResultLevelError_SurfacesOnDto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string response = """
        { "error": "il2cpp runtime not resolved" }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            ClassRangeStatsResult res = await adapter.GetClassRangeStatsAsync(ct: ct).ConfigureAwait(true);
            _ = await serverTask.ConfigureAwait(true);

            Assert.Equal("il2cpp runtime not resolved", res.Error);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SnapshotChunk_ClassEntryTruncatedReason_PreservedOnDto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string response = """
        {
          "ok": true,
          "snapshotId": "snap_1",
          "cursor": 0,
          "nextCursor": 2,
          "done": true,
          "classes": [
            { "assembly": "Assembly-CSharp", "namespace": "Game", "name": "BigTable", "truncated": "memory_budget" },
            { "assembly": "Assembly-CSharp", "namespace": "Game", "name": "HugePool", "truncated": "instance_budget" }
          ]
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SnapshotChunkResult chunk = await adapter.SnapshotChunkAsync("snap_1", 0, 10, ct).ConfigureAwait(true);
            _ = await serverTask.ConfigureAwait(true);

            Assert.Equal(2, chunk.Classes.Count);
            Assert.Equal("memory_budget", chunk.Classes[0].Truncated);
            Assert.Equal("instance_budget", chunk.Classes[1].Truncated);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }

    // --- C-M8: Lossless 64-bit integer & strict non-truncating encoding ---

    [Fact]
    public void ValueScanWireEncoder_PreservesFull64BitIntAndUIntPrecision()
    {
        // > 2^53 (double would round 638500000000000123 to 638500000000000128!)
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("int64", "638500000000000123", out JsonElement i64El, out _));
        Assert.Equal(JsonValueKind.Number, i64El.ValueKind);
        Assert.Equal("638500000000000123", i64El.GetRawText());
        Assert.Equal(638500000000000123L, i64El.GetInt64());

        // Steam64 ID > 2^53 and full UInt64.MaxValue
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("uint64", "76561198012345679", out JsonElement u64El, out _));
        Assert.Equal("76561198012345679", u64El.GetRawText());
        Assert.Equal(76561198012345679UL, u64El.GetUInt64());

        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("uint64", "18446744073709551615", out JsonElement uMaxEl, out _));
        Assert.Equal(ulong.MaxValue, uMaxEl.GetUInt64());

        // Hex notation supported losslessly
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("uint64", "0xFFFFFFFFFFFFFFFF", out JsonElement uHexEl, out _));
        Assert.Equal(ulong.MaxValue, uHexEl.GetUInt64());

        // Fractional numbers rejected for integer widths (no silent truncation!)
        Assert.False(ValueScanWireEncoder.TryEncodeNumericArg("int32", "3.7", out _, out string fracErr));
        Assert.Contains("int32", fracErr);
        Assert.False(ValueScanWireEncoder.TryEncodeNumericArg("uint32", "-1", out _, out _));
        Assert.False(ValueScanWireEncoder.TryEncodeNumericArg("int8", "200", out _, out _));

        // Floating-point widths accept decimals and reject NaN/Infinity
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("float", "3.75", out JsonElement fltEl, out _));
        Assert.Equal(3.75, fltEl.GetDouble());
        Assert.False(ValueScanWireEncoder.TryEncodeNumericArg("double", "NaN", out _, out _));
        Assert.False(ValueScanWireEncoder.TryEncodeNumericArg("double", "Infinity", out _, out _));
    }

    [Fact]
    public async Task BeginValueScan_And_BeginGroupScan_EmitExact64BitJsonIntegersOnWire()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string beginResp = """
        { "sessionId": "vs_1", "totalCandidates": 0, "candidates": [], "scannedBytes": 0 }
        """;
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("int64", "638500000000000123", out JsonElement valEl, out _));
        var (adapter, serverTask, server, client) = await StartPairAsync(beginResp, ct);
        try
        {
            _ = await adapter.BeginValueScanAsync("int64", "exact", valEl, ct: ct).ConfigureAwait(true);
            string reqLine = await serverTask.ConfigureAwait(true);
            using var doc = JsonDocument.Parse(reqLine);
            JsonElement wireVal = doc.RootElement.GetProperty("args").GetProperty("value");
            Assert.Equal("638500000000000123", wireVal.GetRawText());
            Assert.Equal(638500000000000123L, wireVal.GetInt64());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }

        const string groupResp = """
        { "sessionId": "gs_1", "totalCandidates": 0, "candidates": [], "scannedBytes": 0 }
        """;
        Assert.True(ValueScanWireEncoder.TryEncodeNumericArg("uint64", "76561198012345679", out JsonElement uEl, out _));
        var (gAdapter, gServerTask, gServer, gClient) = await StartPairAsync(groupResp, ct);
        try
        {
            var slots = new List<GroupSlotRequest>
            {
                new() { DataType = "uint64", ScanType = "exact", Value = uEl },
            };
            _ = await gAdapter.BeginGroupScanAsync(slots, ct: ct).ConfigureAwait(true);
            string gReqLine = await gServerTask.ConfigureAwait(true);
            using var gDoc = JsonDocument.Parse(gReqLine);
            JsonElement wireSlotVal = gDoc.RootElement.GetProperty("args").GetProperty("slots")[0].GetProperty("value");
            Assert.Equal("76561198012345679", wireSlotVal.GetRawText());
            Assert.Equal(76561198012345679UL, wireSlotVal.GetUInt64());
        }
        finally
        {
            await gClient.DisposeAsync().ConfigureAwait(true);
            await gServer.DisposeAsync().ConfigureAwait(true);
        }
    }

    // --- C-M4 / C-M5: Snapshot struct-array hex, unknown_element_layout, staleInstances ---

    [Fact]
    public async Task SnapshotChunk_CustomStructArrayHex_UnknownLayoutError_AndStaleInstances_RoundTrip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string response = """
        {
          "ok": true,
          "snapshotId": "snap_cm45",
          "cursor": 0,
          "nextCursor": 1,
          "done": true,
          "decodeStats": {
            "decodedClasses": 1,
            "decodedInstances": 1,
            "staleInstances": 7,
            "decodedArrays": 1,
            "skippedOpenGenericArrays": 0,
            "classInfoCacheHits": 2,
            "classInfoCacheMisses": 1,
            "decodedElementSets": 0,
            "elementBudgetHit": false
          },
          "classes": [
            {
              "assembly": "Assembly-CSharp",
              "namespace": "Game",
              "name": "Inventory",
              "schema": [
                { "name": "slots", "type": "Game.ItemSlot[]", "offset": 16, "from": "Inventory" },
                { "name": "opaque", "type": "Game.Opaque[]", "offset": 24, "from": "Inventory" }
              ],
              "instances": [
                {
                  "address": "0x0000012345678900",
                  "v": [
                    {
                      "ptr": "0x0000012345679000",
                      "arrayElements": {
                        "length": 1,
                        "count": 1,
                        "element_type": "Game.ItemSlot",
                        "element_size": 12,
                        "is_pointer": false,
                        "elements": [
                          { "index": 0, "hex": "0102030405060708090a0b0c" }
                        ]
                      }
                    },
                    {
                      "ptr": "0x0000012345679100",
                      "arrayError": "unknown_element_layout"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
        var (adapter, serverTask, server, client) = await StartPairAsync(response, ct);
        try
        {
            SnapshotChunkResult chunk = await adapter.SnapshotChunkAsync("snap_cm45", 0, 10, ct).ConfigureAwait(true);
            _ = await serverTask.ConfigureAwait(true);

            Assert.NotNull(chunk.DecodeStats);
            Assert.Equal(7, chunk.DecodeStats!.StaleInstances);

            SnapshotClassEntry cls = Assert.Single(chunk.Classes);
            SnapshotInstanceEntry inst = Assert.Single(cls.Instances!);
            Assert.Equal(2, inst.Fields.Count);

            SnapshotFieldEntry slots = inst.Fields[0];
            Assert.NotNull(slots.ArrayElements);
            Assert.False(slots.ArrayElements!.IsPointer);
            Assert.Equal(12UL, slots.ArrayElements.ElementSize);
            SnapshotArrayElement el0 = Assert.Single(slots.ArrayElements.Elements);
            Assert.Null(el0.Ptr);
            Assert.Equal("0102030405060708090a0b0c", el0.Hex);

            SnapshotFieldEntry opaque = inst.Fields[1];
            Assert.Equal("unknown_element_layout", opaque.ArrayError);
            Assert.Null(opaque.ArrayElements);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            await server.DisposeAsync().ConfigureAwait(true);
        }
    }
}
