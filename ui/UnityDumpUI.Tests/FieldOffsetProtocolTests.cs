// FieldOffsetProtocolTests.cs — regression tests for C-H1:
// [ThreadStatic] fields carry THREAD_STATIC_FIELD_OFFSET = (size_t)-1 on
// IL2CPP (18446744073709551615 on x64, 4294967295 on x86). Before C-H1,
// System.Text.Json threw JsonException when deserializing getClassInfo /
// dumpClassInfo / searchSymbols / whatIsAt / getStaticFieldData /
// findFieldReferences payloads containing such fields, and PipeClient let
// JsonException escape unwrapped instead of PipeProtocolException.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class FieldOffsetProtocolTests
{
    [Fact]
    public void GetClassInfo_DeserializesThreadStaticUnsignedSentinels_AsMinusOne()
    {
        const string json = """
        {
          "id": 1,
          "result": {
            "namespace": "System.Threading",
            "name": "Thread",
            "fields": [
              { "name": "s_currentThreadX64", "type": "System.Threading.Thread", "offset": 18446744073709551615, "flags": 16, "static": true },
              { "name": "s_currentThreadX86", "type": "System.Threading.Thread", "offset": 4294967295, "flags": 16, "static": true },
              { "name": "s_normalized",       "type": "System.Threading.Thread", "offset": -1, "flags": 16, "static": true },
              { "name": "_managedThreadId",   "type": "System.Int32",            "offset": 24, "flags": 1,  "static": false }
            ],
            "methods": []
          }
        }
        """;

        RpcEnvelope<GetClassInfoResult>? env = JsonSerializer.Deserialize(
            json, ProtocolJsonContext.Default.RpcEnvelopeGetClassInfoResult);

        Assert.NotNull(env?.Result?.Fields);
        Assert.Equal(4, env!.Result!.Fields!.Count);
        Assert.Equal(-1, env.Result.Fields[0].Offset);
        Assert.Equal(-1, env.Result.Fields[1].Offset);
        Assert.Equal(-1, env.Result.Fields[2].Offset);
        Assert.Equal(24, env.Result.Fields[3].Offset);
    }

    [Fact]
    public void DumpClassInfo_BatchWithThreadStaticField_DoesNotAbortEntireBatch()
    {
        const string json = """
        {
          "id": 2,
          "result": {
            "rows": [
              {
                "assembly": "mscorlib",
                "namespace": "System.Threading",
                "name": "Thread",
                "fields": [
                  { "name": "t_current", "type": "System.Threading.Thread", "offset": 18446744073709551615, "flags": 16, "static": true }
                ],
                "methods": []
              },
              {
                "assembly": "mscorlib",
                "namespace": "System",
                "name": "String",
                "fields": [
                  { "name": "_stringLength", "type": "System.Int32", "offset": 16, "flags": 1, "static": false }
                ],
                "methods": []
              }
            ],
            "count": 2,
            "scanned_classes": 2,
            "truncated": false
          }
        }
        """;

        RpcEnvelope<DumpClassInfoResult>? env = JsonSerializer.Deserialize(
            json, ProtocolJsonContext.Default.RpcEnvelopeDumpClassInfoResult);

        Assert.NotNull(env?.Result?.Rows);
        Assert.Equal(2, env!.Result!.Rows.Count);
        Assert.Equal(-1, env.Result.Rows[0].Fields![0].Offset);
        Assert.Equal(16, env.Result.Rows[1].Fields![0].Offset);
    }

    [Fact]
    public void SearchSymbols_HandlesUnsignedSentinelsAndNullMethodOffset()
    {
        const string json = """
        {
          "id": 3,
          "result": {
            "matches": [
              { "namespace": "System.Threading", "class": "Thread", "assembly": "mscorlib", "name": "t_x64", "kind": "field",  "type": "System.Object", "offset": 18446744073709551615, "static": true },
              { "namespace": "System.Threading", "class": "Thread", "assembly": "mscorlib", "name": "t_x86", "kind": "field",  "type": "System.Object", "offset": 4294967295, "static": true },
              { "namespace": "System.Threading", "class": "Thread", "assembly": "mscorlib", "name": "Sleep", "kind": "method", "token": 100663297, "static": true },
              { "namespace": "System.Threading", "class": "Thread", "assembly": "mscorlib", "name": "_id",   "kind": "field",  "type": "System.Int32", "offset": 20, "static": false }
            ],
            "scanned_classes": 1,
            "truncated": false
          }
        }
        """;

        RpcEnvelope<SearchSymbolsResult>? env = JsonSerializer.Deserialize(
            json, ProtocolJsonContext.Default.RpcEnvelopeSearchSymbolsResult);

        Assert.NotNull(env?.Result?.Matches);
        Assert.Equal(4, env!.Result!.Matches.Count);
        Assert.Equal(-1, env.Result.Matches[0].Offset);
        Assert.Equal(-1, env.Result.Matches[1].Offset);
        Assert.Null(env.Result.Matches[2].Offset);
        Assert.Equal(20, env.Result.Matches[3].Offset);
    }

    [Fact]
    public void WhatIsAt_StaticFieldData_AndFieldReferences_NormalizeUnsignedSentinels()
    {
        const string whatIsAtJson = """
        {
          "id": 4,
          "result": {
            "found": true,
            "field": { "name": "t_slot", "type": "System.Int32", "offset": 18446744073709551615, "size": 4, "isStatic": true }
          }
        }
        """;
        const string staticFieldJson = """
        {
          "id": 5,
          "result": {
            "field": "t_slot",
            "type": "System.Int32",
            "offset": 18446744073709551615,
            "flags": 16,
            "address": "0x0000000000000000"
          }
        }
        """;
        const string fieldRefsJson = """
        {
          "id": 6,
          "result": {
            "ok": true,
            "field": { "name": "t_slot", "type": "System.Int32", "offset": 18446744073709551615, "isStatic": true },
            "references": []
          }
        }
        """;

        var whatIsAt = JsonSerializer.Deserialize(whatIsAtJson, ProtocolJsonContext.Default.RpcEnvelopeWhatIsAtResult);
        var staticField = JsonSerializer.Deserialize(staticFieldJson, ProtocolJsonContext.Default.RpcEnvelopeGetStaticFieldDataResult);
        var fieldRefs = JsonSerializer.Deserialize(fieldRefsJson, ProtocolJsonContext.Default.RpcEnvelopeFindFieldReferencesResult);

        Assert.Equal(-1, whatIsAt!.Result!.Field!.Offset);
        Assert.Equal(-1, staticField!.Result!.Offset);
        Assert.Equal(-1L, fieldRefs!.Result!.Field!.Offset);
    }

    [Fact]
    public async Task PipeClient_MalformedJsonResponse_ThrowsPipeProtocolException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"DiscreteUITest_BadJson_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var client = new PipeClient(pipeName);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync("{not-valid-json").ConfigureAwait(false);
        }, ct);

        await client.ConnectAsync(timeoutMs: 2000, ct).ConfigureAwait(true);

        PipeProtocolException ex = await Assert.ThrowsAsync<PipeProtocolException>(
            () => client.SendAsync("ping", args: null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct));

        Assert.IsType<JsonException>(ex.InnerException);

        await serverTask.ConfigureAwait(true);
        await client.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public void MetadataStatsResult_DeserializesTypeTablesSupportedAndDegradedFlags()
    {
        const string supportedJson = """
        {
          "id": 7,
          "result": {
            "scope": "metadata_all",
            "total_classes": 42,
            "tablesDegraded": false,
            "typeTablesSupported": true
          }
        }
        """;
        const string stringsOnlyJson = """
        {
          "id": 8,
          "result": {
            "scope": "metadata_all",
            "total_classes": 0,
            "tablesDegraded": false,
            "typeTablesSupported": false
          }
        }
        """;
        const string legacyJson = """
        {
          "id": 9,
          "result": {
            "scope": "metadata_all",
            "total_classes": 10
          }
        }
        """;

        var supported = JsonSerializer.Deserialize(supportedJson, ProtocolJsonContext.Default.RpcEnvelopeMetadataStatsResult);
        var stringsOnly = JsonSerializer.Deserialize(stringsOnlyJson, ProtocolJsonContext.Default.RpcEnvelopeMetadataStatsResult);
        var legacy = JsonSerializer.Deserialize(legacyJson, ProtocolJsonContext.Default.RpcEnvelopeMetadataStatsResult);

        Assert.True(supported!.Result!.TypeTablesSupported);
        Assert.False(supported.Result.TablesDegraded);
        Assert.False(stringsOnly!.Result!.TypeTablesSupported);
        Assert.Null(legacy!.Result!.TypeTablesSupported);
    }
}
