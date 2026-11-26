// PipeClient.cs — async, single-flight wrapper around the DLL named pipe.
//
// The protocol is line-delimited JSON over a duplex named pipe. There is one
// client at a time and requests are handled one-at-a-time on the server, so
// we serialize concurrent callers with a SemaphoreSlim and correlate every
// response by `id` for sanity (mismatches are bugs, not normal).
//
// Phase 14a: surfaces pipe teardown to subscribers via the
// <see cref="Disconnected"/> event. When the target game exits, the next
// SendAsync read returns null / throws IOException; the client catches it,
// disposes streams, raises Disconnected, and rethrows as
// <see cref="PipeDisconnectedException"/> so callers can react (the
// MainWindowViewModel handler resets UI state + re-enables Connect).

using System;
using System.Diagnostics;
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
    /// <summary>1 once we've already raised Disconnected for the current
    /// connection — guards against double-fires when both write and read
    /// fail in quick succession.</summary>
    private int                     _disconnectFired;

    /// <summary>
    /// Raised exactly once per connection when the pipe is detected to be
    /// gone (server closed, IO error). Fired off-gate so subscribers can
    /// safely call back into the VM (and the VM can re-issue ConnectAsync).
    /// Subscribers should not assume any UI thread context — marshal as
    /// needed (MainWindowViewModel uses Dispatcher.UIThread.Post).
    /// </summary>
    public event EventHandler<PipeDisconnectedInfo>? Disconnected;

    public PipeClient(string pipeName = Constants.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>Phase 30d telemetry — split of the most recent <see cref="SendAsync"/>
    /// round-trip: <see cref="LastReadMs"/> = ReadLineAsync wall-time (it blocks until
    /// the DLL writes the WHOLE response, so it also covers the server-side handler +
    /// pipe transfer); <see cref="LastParseMs"/> = JSON deserialize; and
    /// <see cref="LastResponseChars"/> = the raw response length. Single-flight via
    /// <c>_gate</c>, so plain fields are safe for a caller to read right after the call.</summary>
    public long LastReadMs        { get; private set; }
    public long LastParseMs       { get; private set; }
    public int  LastResponseChars { get; private set; }

    /// <summary>
    /// Resolve the PID of the process that owns the *server* end of this pipe.
    /// Our pipe server is the backend DLL running inside the target game, so
    /// the server PID == the game's PID. Used to tell the AOBMaker app which
    /// process to scan for <c>GenerateAob</c>, without depending on the
    /// inject-time PID (works for plain Connect too). Returns false when not
    /// connected or the query fails — callers then fall back to letting the
    /// AOBMaker app resolve the process itself (e.g. via CE).
    /// </summary>
    public bool TryGetServerProcessId(out uint pid)
    {
        pid = 0;
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return false;
        try
        {
            var handle = pipe.SafePipeHandle;
            if (handle is null || handle.IsInvalid || handle.IsClosed) return false;
            return GetNamedPipeServerProcessId(handle, out pid) && pid != 0;
        }
        catch
        {
            // SafePipeHandle access can throw if the pipe tore down between the
            // IsConnected check and here — treat as "unknown PID".
            pid = 0;
            return false;
        }
    }

    // DllImport (not LibraryImport) so Core needn't enable AllowUnsafeBlocks —
    // matches WindowsProcessEnumerator's kernel32 P/Invoke style and stays
    // Native-AOT compatible (classic P/Invoke is fully supported under AOT).
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint serverProcessId);

    public async Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisposeStreamsAsync().ConfigureAwait(false);
            // Reset the one-shot Disconnected guard so the next teardown
            // fires the event exactly once (the subscriber may have
            // re-Connected after a previous disconnect).
            Interlocked.Exchange(ref _disconnectFired, 0);
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

        // Honour cancellation while waiting for the gate (caller can still
        // bail before they own the wire); once we own the gate we deliberately
        // run the actual write+read uncancellable so we never abandon a
        // response in the pipe buffer for the next caller to misread.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        // Set inside the gate when an IO failure tells us the pipe is gone;
        // the actual event fire happens after we release the gate so handlers
        // can call back into the client without deadlocking.
        PipeDisconnectedInfo? disconnectInfo = null;
        try
        {
            if (_pipe is null || _writer is null || _reader is null || !_pipe.IsConnected)
            {
                throw new InvalidOperationException("PipeClient is not connected.");
            }

            // Last chance to bail without committing anything to the wire.
            ct.ThrowIfCancellationRequested();

            int id = Interlocked.Increment(ref _nextId);
            var request = new PipeRequest { Id = id, Cmd = cmd, Args = args };
            string requestLine = JsonSerializer.Serialize(
                request, ProtocolJsonContext.Default.PipeRequest);

            // CRITICAL: do not pass `ct` past this point. If the caller cancels
            // between our write and our read, the DLL's response for `id` ends
            // up sitting unread in the pipe buffer and the *next* SendAsync
            // (different `id`) reads our stale response → id-mismatch crash.
            // We always drain the response and check `ct` after.
            try
            {
                await _writer.WriteLineAsync(requestLine.AsMemory()).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPipeBrokenException(ex))
            {
                disconnectInfo = new PipeDisconnectedInfo("write failed: " + ex.Message);
                await DisposeStreamsAsync().ConfigureAwait(false);
                throw new PipeDisconnectedException(disconnectInfo.Reason, ex);
            }

            string? responseLine;
            var readSw = Stopwatch.StartNew();
            try
            {
                responseLine = await _reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPipeBrokenException(ex))
            {
                disconnectInfo = new PipeDisconnectedInfo("read failed: " + ex.Message);
                await DisposeStreamsAsync().ConfigureAwait(false);
                throw new PipeDisconnectedException(disconnectInfo.Reason, ex);
            }
            readSw.Stop();
            if (responseLine is null)
            {
                disconnectInfo = new PipeDisconnectedInfo("server closed pipe before responding");
                await DisposeStreamsAsync().ConfigureAwait(false);
                throw new PipeDisconnectedException(disconnectInfo.Reason);
            }
            // Phase 30d telemetry — record the read-wait (covers DLL handler + transfer)
            // and response size, then time the deserialize separately, so a caller can
            // split a slow round-trip into wire/server vs JSON-parse vs payload size.
            LastReadMs        = readSw.ElapsedMilliseconds;
            LastResponseChars = responseLine.Length;
            var parseSw = Stopwatch.StartNew();
            RpcEnvelope<TResult>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(responseLine, envelopeType);
            }
            catch (JsonException ex)
            {
                throw new PipeProtocolException("Invalid JSON response: " + ex.Message, ex);
            }
            parseSw.Stop();
            LastParseMs = parseSw.ElapsedMilliseconds;
            if (envelope is null)
            {
                throw new PipeProtocolException("Empty response envelope.");
            }
            if (envelope.Id != id)
            {
                throw new PipeProtocolException(
                    $"Response id mismatch: expected {id}, got {envelope.Id}.");
            }
            // Honour the late cancel now — pipe is back in a clean state.
            ct.ThrowIfCancellationRequested();
            return envelope;
        }
        finally
        {
            _gate.Release();
            // Off-gate: fire Disconnected exactly once. Subscribers may
            // re-enter the pipe (e.g. ConnectAsync) without deadlocking.
            if (disconnectInfo is not null
                && Interlocked.Exchange(ref _disconnectFired, 1) == 0)
            {
                Disconnected?.Invoke(this, disconnectInfo);
            }
        }
    }

    /// <summary>
    /// Recognises the exception shapes Windows raises when a peer pipe goes
    /// away mid-IO (target process exited, server thread closed handle, etc.)
    /// so the catch sites can distinguish "real" exceptions (caller bug,
    /// JSON deserialisation issue) from pipe teardown.
    /// </summary>
    private static bool IsPipeBrokenException(Exception ex) => ex is
        IOException or
        ObjectDisposedException or
        EndOfStreamException;

    /// <summary>
    /// Tear down the underlying pipe + reader + writer without disposing
    /// the client itself — caller can re-enter <see cref="ConnectAsync"/>
    /// afterwards. Intended for user-initiated Disconnect (DLL still alive
    /// in the target, user just wants to stop talking to it). Pairs with
    /// <see cref="DisposeAsync"/>, which is for final teardown only.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisposeStreamsAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
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

/// <summary>
/// Thrown when the named pipe is detected to be gone mid-request (target
/// process exited, server handle closed). The corresponding
/// <see cref="PipeClient.Disconnected"/> event fires once before the
/// exception surfaces; subscribers should reset UI state.
/// </summary>
public sealed class PipeDisconnectedException : Exception
{
    public PipeDisconnectedException(string reason) : base(reason) { }
    public PipeDisconnectedException(string reason, Exception inner) : base(reason, inner) { }
}

/// <summary>
/// Payload for <see cref="PipeClient.Disconnected"/>: human-readable
/// reason captured at detection time.
/// </summary>
public sealed class PipeDisconnectedInfo
{
    public PipeDisconnectedInfo(string reason) { Reason = reason; }
    public string Reason { get; }
}
