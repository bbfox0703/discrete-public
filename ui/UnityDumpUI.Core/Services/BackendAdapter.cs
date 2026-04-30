// BackendAdapter.cs — typed façade over PipeClient.
//
// One method per pipe command. ViewModels go through this layer so they
// never touch raw JSON or pipe semantics. Per Architecture.md "Platform
// Abstraction" rule.

using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

public sealed class BackendAdapter
{
    private readonly PipeClient _pipe;

    public BackendAdapter(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected => _pipe.IsConnected;

    public Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
        => _pipe.ConnectAsync(timeoutMs, ct);

    /// <summary>
    /// Round-trip liveness check.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var env = await _pipe.SendAsync(
            "ping", args: null,
            ProtocolJsonContext.Default.RpcEnvelopePingResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result?.Pong == true;
    }

    /// <summary>
    /// Identifies the runtime / DLL build / backend.
    /// </summary>
    public async Task<HandshakeResult> HandshakeAsync(CancellationToken ct = default)
    {
        var env = await _pipe.SendAsync(
            "handshake", args: null,
            ProtocolJsonContext.Default.RpcEnvelopeHandshakeResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("handshake returned no result");
    }

    private static void ThrowIfError(RpcError? error)
    {
        if (error is not null)
        {
            throw new PipeProtocolException(
                $"Pipe error {error.Code}: {error.Message}");
        }
    }
}
