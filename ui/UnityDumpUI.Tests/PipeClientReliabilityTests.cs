// PipeClientReliabilityTests.cs — C-M2 + C-M3 regression tests.
//
// Uses real in-process NamedPipeServerStream instances paired with PipeClient
// to verify wire-level behavior end to end:
//   * C-M2: `"id": null` error envelopes deserialize cleanly into `int? Id`
//     and throw PipeProtocolException carrying the server's real error code
//     and message (NOT JsonException); malformed JSON wraps as
//     PipeProtocolException; response id mismatches tear down the pipe and
//     fire Disconnected so subsequent calls do not stay desynchronized.
//   * C-M3: Per-request ReadTimeout tears down the pipe on stall, fires
//     Disconnected once, and throws PipeDisconnectedException; DisconnectAsync
//     and DisposeAsync abort the underlying pipe before acquiring `_gate` so
//     they never deadlock behind a stuck server handler.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class PipeClientReliabilityTests
{
    private static string UniquePipeName() =>
        "DiscreteTestPipe_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SendAsync_NullIdErrorEnvelope_ThrowsPipeProtocolExceptionWithServerMessage()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync(
                "{\"id\":null,\"error\":{\"code\":-32603,\"message\":\"response serialization failed: bad allocation\"}}")
                .ConfigureAwait(false);
        }, ct);

        await using var client = new PipeClient(pipeName);
        await client.ConnectAsync(5000, ct).ConfigureAwait(true);

        var ex = await Assert.ThrowsAsync<PipeProtocolException>(() =>
            client.SendAsync("ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct))
            .ConfigureAwait(true);

        Assert.Contains("-32603", ex.Message);
        Assert.Contains("response serialization failed: bad allocation", ex.Message);
        await serverTask.ConfigureAwait(true);
    }

    [Fact]
    public async Task SendAsync_MalformedJsonResponse_ThrowsPipeProtocolException()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync("{not-valid-json").ConfigureAwait(false);
        }, ct);

        await using var client = new PipeClient(pipeName);
        await client.ConnectAsync(5000, ct).ConfigureAwait(true);

        var ex = await Assert.ThrowsAsync<PipeProtocolException>(() =>
            client.SendAsync("ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct))
            .ConfigureAwait(true);

        Assert.Contains("Invalid JSON response", ex.Message);
        await serverTask.ConfigureAwait(true);
    }

    [Fact]
    public async Task SendAsync_IdMismatch_TearsDownPipeAndFiresDisconnected()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync("{\"id\":999,\"result\":{\"pong\":true}}").ConfigureAwait(false);
        }, ct);

        await using var client = new PipeClient(pipeName);
        int disconnectCount = 0;
        string? disconnectReason = null;
        client.Disconnected += (_, info) =>
        {
            Interlocked.Increment(ref disconnectCount);
            disconnectReason = info.Reason;
        };

        await client.ConnectAsync(5000, ct).ConfigureAwait(true);

        var ex = await Assert.ThrowsAsync<PipeProtocolException>(() =>
            client.SendAsync("ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct))
            .ConfigureAwait(true);

        Assert.Contains("id mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(client.IsConnected);
        Assert.Equal(1, disconnectCount);
        Assert.Contains("id mismatch", disconnectReason ?? "", StringComparison.OrdinalIgnoreCase);
        await serverTask.ConfigureAwait(true);
    }

    [Fact]
    public async Task SendAsync_ReadTimeout_TearsDownPipeAndThrowsPipeDisconnectedException()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            // Deliberately stall without responding.
            await releaseServer.Task.ConfigureAwait(false);
        }, ct);

        try
        {
            await using var client = new PipeClient(pipeName)
            {
                ReadTimeout = TimeSpan.FromMilliseconds(150),
            };
            int disconnectCount = 0;
            client.Disconnected += (_, _) => Interlocked.Increment(ref disconnectCount);

            await client.ConnectAsync(5000, ct).ConfigureAwait(true);

            var ex = await Assert.ThrowsAsync<PipeDisconnectedException>(() =>
                client.SendAsync("ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct))
                .ConfigureAwait(true);

            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(client.IsConnected);
            Assert.Equal(1, disconnectCount);
        }
        finally
        {
            releaseServer.TrySetResult();
            await serverTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DisconnectAsync_WhileSendBlockedOnStalledServer_UnblocksImmediatelyWithoutDeadlock()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            requestReceived.TrySetResult();
            await releaseServer.Task.ConfigureAwait(false);
        }, ct);

        try
        {
            await using var client = new PipeClient(pipeName);
            await client.ConnectAsync(5000, ct).ConfigureAwait(true);

            Task<RpcEnvelope<PingResult>> inFlight = client.SendAsync(
                "ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct);

            await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

            // C-M3: DisconnectAsync must abort the underlying pipe before taking _gate,
            // completing promptly instead of deadlocking behind the blocked ReadLineAsync.
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

            await Assert.ThrowsAsync<PipeDisconnectedException>(() => inFlight).ConfigureAwait(true);
            Assert.False(client.IsConnected);
        }
        finally
        {
            releaseServer.TrySetResult();
            await serverTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhileSendBlockedOnStalledServer_UnblocksImmediatelyAndIsIdempotent()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            requestReceived.TrySetResult();
            await releaseServer.Task.ConfigureAwait(false);
        }, ct);

        try
        {
            var client = new PipeClient(pipeName);
            await client.ConnectAsync(5000, ct).ConfigureAwait(true);

            Task<RpcEnvelope<PingResult>> inFlight = client.SendAsync(
                "ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct);

            await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            // Second DisposeAsync must be a safe no-op.
            await client.DisposeAsync().ConfigureAwait(true);

            await Assert.ThrowsAsync<PipeDisconnectedException>(() => inFlight).ConfigureAwait(true);
        }
        finally
        {
            releaseServer.TrySetResult();
            await serverTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SendWithTelemetryAsync_ReturnsCallScopedTelemetryInsideGate()
    {
        string pipeName = UniquePipeName();
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string payload = "{\"id\":1,\"result\":{\"pong\":true}}";

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync(payload).ConfigureAwait(false);
        }, ct);

        await using var client = new PipeClient(pipeName);
        await client.ConnectAsync(5000, ct).ConfigureAwait(true);

        var (env, telemetry) = await client.SendWithTelemetryAsync(
            "ping", null, ProtocolJsonContext.Default.RpcEnvelopePingResult, ct).ConfigureAwait(true);

        Assert.True(env.Result?.Pong);
        Assert.Equal(payload.Length, telemetry.ResponseChars);
        Assert.True(telemetry.ReadMs >= 0);
        Assert.True(telemetry.ParseMs >= 0);
        await serverTask.ConfigureAwait(true);
    }

    [Fact]
    public void HandshakeResult_DeserializesCommandsAndSupportsCommand()
    {
        const string json = """
            {"id":1,"result":{"backend":"mono","commands":["handshake","ping","snapshotBegin"]}}
            """;
        var env = System.Text.Json.JsonSerializer.Deserialize(
            json, ProtocolJsonContext.Default.RpcEnvelopeHandshakeResult);

        Assert.NotNull(env?.Result);
        Assert.NotNull(env!.Result!.Commands);
        Assert.Equal(3, env.Result.Commands!.Count);
        Assert.True(env.Result.SupportsCommand("snapshotBegin"));
        Assert.False(env.Result.SupportsCommand("dumpIl2cppSymbols"));
        Assert.False(env.Result.SupportsCommand(""));
    }
}
