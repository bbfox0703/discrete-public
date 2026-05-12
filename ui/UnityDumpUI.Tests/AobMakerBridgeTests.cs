// AobMakerBridgeTests.cs — Phase 22a verification.
//
// Spins up a fake NamedPipeServerStream per test (matches the
// PipeClientLifecycleTests pattern) and exercises the 6 bridge methods
// the rest of Phase 22 will rely on. Coverage:
//
//   * Wire-shape round-trip — every command serializes the right keys
//     and the AOBMaker plugin would receive a parseable payload.
//   * IsAvailable lifecycle — flips false→true on first success, fires
//     AvailabilityChanged exactly once per transition.
//   * InjectTableFileAsync tuple return — surfaces plugin Message field
//     on failure; null Message on connect failure.
//   * CreateAAScriptWithRecords — nested records[] array carries
//     description/address/valueType/isSigned/offsets/color through
//     System.Text.Json source-gen unchanged.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class AobMakerBridgeTests
{
    private const int    TestTimeoutMs = 5000;

    [Fact]
    public async Task CheckAvailability_FlipsIsAvailable_AndFiresEvent_OnFirstSuccess()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Assert.False(bridge.IsAvailable);

        int eventCount = 0;
        bool? lastValue = null;
        bridge.AvailabilityChanged += (_, v) => { eventCount++; lastValue = v; };

        // CheckAvailability connects + immediately disconnects (no payload
        // exchange). Fake server just needs to accept the connection.
        Task serverWait = server.WaitForConnectionAsync(ct);
        bool ok = await bridge.CheckAvailabilityAsync(ct);
        await serverWait;

        Assert.True(ok);
        Assert.True(bridge.IsAvailable);
        Assert.Equal(1, eventCount);
        Assert.True(lastValue);
    }

    [Fact]
    public async Task CheckAvailability_DoesNotRefireEvent_OnSecondSuccess()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        // Allow two sequential connections (max instances = 2 isn't supported
        // for the same pipe; use two separate single-instance servers).
        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);
        int eventCount = 0;
        bridge.AvailabilityChanged += (_, _) => { eventCount++; };

        // Round 1
        await using (var server1 = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            Task wait = server1.WaitForConnectionAsync(ct);
            Assert.True(await bridge.CheckAvailabilityAsync(ct));
            await wait;
        }

        // Round 2
        await using (var server2 = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            Task wait = server2.WaitForConnectionAsync(ct);
            Assert.True(await bridge.CheckAvailabilityAsync(ct));
            await wait;
        }

        Assert.Equal(1, eventCount); // only the false→true transition fires
    }

    [Fact]
    public async Task CheckAvailability_OnPipeMissing_LeavesIsAvailableFalse_NoEvent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_NeverExists_{Guid.NewGuid():N}";
        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);
        int eventCount = 0;
        bridge.AvailabilityChanged += (_, _) => { eventCount++; };

        bool ok = await bridge.CheckAvailabilityAsync(ct);

        Assert.False(ok);
        Assert.False(bridge.IsAvailable);
        Assert.Equal(0, eventCount); // false→false is not a transition
    }

    [Fact]
    public async Task InjectTableFile_RoundTripsContent_AndReturnsOk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        // Server-side: accept, read request, reply success.
        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "InjectTableFileResult", success: true, message: null, ct);

        var (ok, error) = await bridge.InjectTableFileAsync(
            "unity_invoke.lua",
            "-- Phase 19 bridge\nfunction callUnityMethod(...) end\n", ct);

        TestPipeMessage received = await serverTask;

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("InjectTableFile", received.Type);
        Assert.Equal("unity_invoke.lua", received.FileName);
        Assert.Contains("callUnityMethod", received.Content!);
        Assert.True(bridge.IsAvailable);
    }

    [Fact]
    public async Task InjectTableFile_OnPluginFailure_SurfacesMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "InjectTableFileResult", success: false,
            message: "Stream size mismatch: wrote 42, stream has 0", ct);

        var (ok, error) = await bridge.InjectTableFileAsync("foo.lua", "bar", ct);
        await serverTask;

        Assert.False(ok);
        Assert.Equal("Stream size mismatch: wrote 42, stream has 0", error);
        // Plugin replied → bridge stayed reachable.
        Assert.True(bridge.IsAvailable);
    }

    [Fact]
    public async Task InjectTableFile_OnPipeMissing_ReturnsFalseAndNullMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_NeverExists_{Guid.NewGuid():N}";
        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        var (ok, error) = await bridge.InjectTableFileAsync("foo.lua", "bar", ct);

        Assert.False(ok);
        Assert.Null(error);              // null = UI-side connect failure (vs. plugin-side reason)
        Assert.False(bridge.IsAvailable);
    }

    [Fact]
    public async Task InjectTableFile_RejectsEmptyArgs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: "ignored");

        await Assert.ThrowsAsync<ArgumentException>(
            () => bridge.InjectTableFileAsync("", "content", ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => bridge.InjectTableFileAsync("foo.lua", "", ct));
    }

    [Fact]
    public async Task CreateMemoryRecord_SendsTypedAddress()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "CreateMemoryRecordResult", success: true, message: null, ct);

        bool ok = await bridge.CreateMemoryRecordAsync(
            description: "Player HP",
            address:     "PlayerBase+0x10",
            valueType:   CeValueType.Dword,
            isSigned:    false,
            ct: ct);
        TestPipeMessage received = await serverTask;

        Assert.True(ok);
        Assert.Equal("CreateMemoryRecord", received.Type);
        Assert.Equal("Player HP",          received.Description);
        Assert.Equal("PlayerBase+0x10",    received.Address);
        Assert.Equal(2,                    received.ValueType); // Dword=2
    }

    [Fact]
    public async Task CreateAAScriptWithRecords_SerializesNestedArray()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "CreateAAScriptWithRecordsResult", success: true, message: null, ct);

        var records = new List<AaScriptChildRecord>
        {
            new() { Description = "HP", Address = "PlayerBase",
                    ValueType   = CeValueType.Dword,
                    Offsets     = new[] { 0x10, 0x20 },
                    Color       = "FF0000" },
            new() { Description = "Name", Address = "PlayerBase",
                    ValueType   = CeValueType.String,
                    Offsets     = new[] { 0x40 } },
        };

        bool ok = await bridge.CreateAAScriptWithRecordsAsync(
            description: "Player",
            script:      "[ENABLE]\nalloc(x,4)\n[DISABLE]\ndealloc(x)",
            records:     records,
            autoActivate: false,
            hideChildren: true,
            ct: ct);
        TestPipeMessage received = await serverTask;

        Assert.True(ok);
        Assert.Equal("CreateAAScriptWithRecords", received.Type);
        Assert.True(received.HideChildren);
        Assert.NotNull(received.Records);
        Assert.Equal(2, received.Records!.Count);

        var hp = received.Records[0];
        Assert.Equal("HP", hp.Description);
        Assert.Equal(2,    hp.ValueType);   // Dword=2
        Assert.Equal(new[] { 0x10, 0x20 }, hp.Offsets);
        Assert.Equal("FF0000", hp.Color);

        var name = received.Records[1];
        Assert.Equal(6, name.ValueType);    // String=6
        Assert.Equal(new[] { 0x40 }, name.Offsets);
        Assert.Null(name.Color);            // omitted when null
    }

    [Fact]
    public async Task CreateAAScriptAsync_StillWorks_RegressionAfter22a()
    {
        // Phase 18c was already shipping; confirm Phase 22a's wire-model
        // refactor didn't change the existing CreateAAScript shape.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "CreateAAScriptResult", success: true, message: null, ct);

        bool ok = await bridge.CreateAAScriptAsync(
            description: "Test AA",
            script:      "[ENABLE]\nnop\n[DISABLE]\nnop",
            autoActivate: true, ct: ct);
        TestPipeMessage received = await serverTask;

        Assert.True(ok);
        Assert.Equal("CreateAAScript", received.Type);
        Assert.Equal("Test AA",        received.Description);
        Assert.True(received.AutoActivate);
        Assert.Null(received.Records); // no nested array on this command
    }

    [Fact]
    public async Task NavigateHexView_StripsLeadingZeroX()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"AobMakerTest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var bridge = new AobMakerBridge(logger: null, pipeNameOverride: pipeName);

        Task<TestPipeMessage> serverTask = RunServerOnceAsync(server,
            replyType: "NavigateHexViewResult", success: true, message: null, ct);

        bool ok = await bridge.NavigateHexViewAsync("0xDEADBEEF", ct);
        TestPipeMessage received = await serverTask;

        Assert.True(ok);
        Assert.Equal("NavigateHexView", received.Type);
        Assert.Equal("DEADBEEF",         received.Address); // 0x prefix stripped
    }

    // ---------- Test infrastructure -------------------------------------

    /// <summary>
    /// Wait for one client connection, read one length-prefixed JSON
    /// request, write one length-prefixed JSON response. Mirrors the
    /// AOBMaker CE-plugin's wire format byte-for-byte.
    /// </summary>
    private static async Task<TestPipeMessage> RunServerOnceAsync(
        NamedPipeServerStream server, string replyType, bool success, string? message,
        CancellationToken ct)
    {
        await server.WaitForConnectionAsync(ct);
        TestPipeMessage request = await ReadAsync(server, ct)
            ?? throw new InvalidOperationException("server got null request");

        var reply = new TestPipeMessage
        {
            Type    = replyType,
            Success = success,
            Message = message,
        };
        await WriteAsync(server, reply, ct);
        return request;
    }

    private static async Task<TestPipeMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (await ReadExactAsync(stream, lenBuf, 4, ct) < 4) return null;
        uint len = BitConverter.ToUInt32(lenBuf, 0);
        if (len == 0 || len > 10 * 1024 * 1024) return null;
        var buf = new byte[len];
        if (await ReadExactAsync(stream, buf, (int)len, ct) < (int)len) return null;
        string json = Encoding.UTF8.GetString(buf);
        return JsonSerializer.Deserialize(json, TestJsonContext.Default.TestPipeMessage);
    }

    private static async Task WriteAsync(Stream stream, TestPipeMessage msg, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(msg, TestJsonContext.Default.TestPipeMessage);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] lenBuf = BitConverter.GetBytes((uint)payload.Length);
        await stream.WriteAsync(lenBuf, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

/// <summary>
/// Test-only mirror of the bridge's internal AobMakerMessage. Lives in
/// the test project so the source-gen context here doesn't need to
/// share a graph with production code.
/// </summary>
public sealed class TestPipeMessage
{
    [JsonPropertyName("type")]         public string  Type        { get; set; } = "";
    [JsonPropertyName("address")]      public string? Address     { get; set; }
    [JsonPropertyName("description")]  public string? Description { get; set; }
    [JsonPropertyName("script")]       public string? Script      { get; set; }
    [JsonPropertyName("autoActivate")] public bool    AutoActivate{ get; set; }
    [JsonPropertyName("valueType")]    public int?    ValueType   { get; set; }
    [JsonPropertyName("isSigned")]     public bool    IsSigned    { get; set; }
    [JsonPropertyName("hideChildren")] public bool    HideChildren{ get; set; }
    [JsonPropertyName("records")]      public List<TestRecord>? Records { get; set; }
    [JsonPropertyName("fileName")]     public string? FileName    { get; set; }
    [JsonPropertyName("content")]      public string? Content     { get; set; }
    [JsonPropertyName("success")]      public bool    Success     { get; set; }
    [JsonPropertyName("message")]      public string? Message     { get; set; }
}

public sealed class TestRecord
{
    [JsonPropertyName("description")] public string  Description { get; set; } = "";
    [JsonPropertyName("address")]     public string  Address     { get; set; } = "";
    [JsonPropertyName("valueType")]   public int     ValueType   { get; set; }
    [JsonPropertyName("isSigned")]    public bool    IsSigned    { get; set; }
    [JsonPropertyName("offsets")]     public int[]?  Offsets     { get; set; }
    [JsonPropertyName("color")]       public string? Color       { get; set; }
}

[JsonSerializable(typeof(TestPipeMessage))]
[JsonSerializable(typeof(TestRecord))]
[JsonSerializable(typeof(List<TestRecord>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class TestJsonContext : JsonSerializerContext { }
