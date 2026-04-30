// PipeClient.cs — async, single-flight wrapper around the DLL named pipe.
//
// The protocol is line-delimited JSON over a duplex named pipe. There is one
// client at a time and requests are handled one-at-a-time on the server, so
// we serialize concurrent callers with a SemaphoreSlim and correlate every
// response by `id` for sanity (mismatches are bugs, not normal).

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

public sealed class PipeClient : IAsyncDisposable
{
    private readonly string         _pipeName;
    private readonly SemaphoreSlim  _gate = new(1, 1);
    private NamedPipeClientStream?  _pipe;
    private StreamReader?           _reader;
    private StreamWriter?           _writer;
    private int                     _nextId;

    public PipeClient(string pipeName = Constants.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisposeStreamsAsync().ConfigureAwait(false);
            var pipe = new NamedPipeClientStream(
                serverName:   ".",
                pipeName:     _pipeName,
                direction:    PipeDirection.InOut,
                options:      PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _pipe   = pipe;
            // BufferedStream(true) -> leaveOpen on Dispose; we own the underlying pipe.
            _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                                       bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                       bufferSize: 4096, leaveOpen: true)
            {
                NewLine    = "\n",
                AutoFlush  = true,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Send a typed request and parse the typed response envelope. Throws
    /// <see cref="InvalidOperationException"/> if not connected,
    /// <see cref="PipeProtocolException"/> on protocol-level errors,
    /// or propagates I/O / cancellation exceptions.
    /// </summary>
    public async Task<RpcEnvelope<TResult>> SendAsync<TResult>(
        string cmd,
        Dictionary<string, JsonElement>? args,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<RpcEnvelope<TResult>> envelopeType,
        CancellationToken ct = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(envelopeType);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pipe is null || _writer is null || _reader is null || !_pipe.IsConnected)
            {
                throw new InvalidOperationException("PipeClient is not connected.");
            }

            int id = Interlocked.Increment(ref _nextId);
            var request = new PipeRequest { Id = id, Cmd = cmd, Args = args };
            string requestLine = JsonSerializer.Serialize(
                request, ProtocolJsonContext.Default.PipeRequest);

            await _writer.WriteLineAsync(requestLine.AsMemory(), ct).ConfigureAwait(false);

            string? responseLine = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (responseLine is null)
            {
                throw new PipeProtocolException("Server closed pipe before responding.");
            }
            RpcEnvelope<TResult>? envelope =
                JsonSerializer.Deserialize(responseLine, envelopeType);
            if (envelope is null)
            {
                throw new PipeProtocolException("Empty response envelope.");
            }
            if (envelope.Id != id)
            {
                throw new PipeProtocolException(
                    $"Response id mismatch: expected {id}, got {envelope.Id}.");
            }
            return envelope;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisposeStreamsAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async ValueTask DisposeStreamsAsync()
    {
        if (_writer is not null) { await _writer.DisposeAsync().ConfigureAwait(false); _writer = null; }
        if (_reader is not null) { _reader.Dispose(); _reader = null; }
        if (_pipe is not null)   { await _pipe.DisposeAsync().ConfigureAwait(false);   _pipe   = null; }
    }
}

public sealed class PipeProtocolException : Exception
{
    public PipeProtocolException(string message) : base(message) { }
    public PipeProtocolException(string message, Exception inner) : base(message, inner) { }
}
