// PipeClientLifecycleTests.cs — regression tests for the Connect → Disconnect
// → Connect lifecycle. The original PipeClient.DisposeAsync was the only
// teardown surface and it disposed the gate semaphore permanently, which
// silently bricked the client for any future Connect (every subsequent
// _gate.WaitAsync threw ObjectDisposedException). Spotted in the field as
// "Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'."
// after a game-exit + reconnect cycle once Phase 14a's retry loop made it
// reachable.
//
// These tests pin the contract: DisconnectAsync is reusable, DisposeAsync
// is terminal.

using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class PipeClientLifecycleTests
{
    /// <summary>
    /// Connect to a server that closes immediately, then call
    /// DisconnectAsync, then Connect again. The second Connect must reach
    /// the actual pipe code (i.e. throw a *connect* error, not
    /// ObjectDisposedException from the gate semaphore).
    /// </summary>
    [Fact]
    public async Task DisconnectAsync_AllowsSubsequentConnect()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string pipeName = $"DiscreteUITest_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var client = new PipeClient(pipeName);

        // Round 1: connect successfully (server.WaitForConnectionAsync needed
        // for the client side to handshake).
        Task serverWait = server.WaitForConnectionAsync(ct);
        await client.ConnectAsync(timeoutMs: 1000, ct).ConfigureAwait(true);
        await serverWait.ConfigureAwait(true);
        Assert.True(client.IsConnected);

        // User-initiated disconnect — gate must survive.
        await client.DisconnectAsync().ConfigureAwait(true);
        Assert.False(client.IsConnected);

        // Round 2: should fail with TimeoutException (no server listening
        // anymore — we used a single-instance server). The KEY assertion is
        // that it does NOT throw ObjectDisposedException — i.e. the gate
        // semaphore is still usable.
        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => client.ConnectAsync(timeoutMs: 100, ct));
        Assert.DoesNotContain("disposed", ex.Message, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// DisposeAsync is terminal — any subsequent Connect must surface
    /// ObjectDisposedException promptly, not silently corrupt internal state.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_PreventsFurtherConnect()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var client = new PipeClient("DiscreteUITest_NeverExists_" + Guid.NewGuid().ToString("N"));
        await client.DisposeAsync().ConfigureAwait(true);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.ConnectAsync(timeoutMs: 50, ct));
    }

    /// <summary>
    /// Multiple DisconnectAsync calls in a row must be safe — the user can
    /// click Disconnect twice with no harm done.
    /// </summary>
    [Fact]
    public async Task DisconnectAsync_IsIdempotent()
    {
        var client = new PipeClient("DiscreteUITest_Idempotent_" + Guid.NewGuid().ToString("N"));
        await client.DisconnectAsync().ConfigureAwait(true);
        await client.DisconnectAsync().ConfigureAwait(true);

        // Still safely disposable as a final cleanup.
        await client.DisposeAsync().ConfigureAwait(true);
    }
}
