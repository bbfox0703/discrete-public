// BackendAdapter.cs — typed façade over PipeClient.
//
// One method per pipe command. ViewModels go through this layer so they
// never touch raw JSON or pipe semantics. Per Architecture.md "Platform
// Abstraction" rule.

using System.Collections.Generic;
using System.Text.Json;
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

    /// <summary>
    /// `getAssemblies` — list of loaded managed assemblies.
    /// </summary>
    public async Task<GetAssembliesResult> GetAssembliesAsync(CancellationToken ct = default)
    {
        var env = await _pipe.SendAsync(
            "getAssemblies", args: null,
            ProtocolJsonContext.Default.RpcEnvelopeGetAssembliesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getAssemblies returned no result");
    }

    /// <summary>
    /// `getClasses` — namespace + name list of classes in <paramref name="assembly"/>.
    /// </summary>
    public async Task<GetClassesResult> GetClassesAsync(string assembly, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"] = Str(assembly),
        };
        var env = await _pipe.SendAsync(
            "getClasses", args,
            ProtocolJsonContext.Default.RpcEnvelopeGetClassesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getClasses returned no result");
    }

    /// <summary>
    /// `getClassInfo` — fields + methods + parent ref for one class.
    /// </summary>
    public async Task<GetClassInfoResult> GetClassInfoAsync(
        string assembly, string? @namespace, string name,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(name),
        };
        var env = await _pipe.SendAsync(
            "getClassInfo", args,
            ProtocolJsonContext.Default.RpcEnvelopeGetClassInfoResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getClassInfo returned no result");
    }

    // -----------------------------------------------------------------------
    // Phase 9 — live inspection.
    // -----------------------------------------------------------------------

    /// <summary>
    /// `findInstances` — heap-scan for class header pointers matching the given
    /// class spec. Returns up to <paramref name="limit"/> matches (DLL caps at
    /// 4096; default 256 if null).
    /// </summary>
    public async Task<FindInstancesResult> FindInstancesAsync(
        string assembly, string? @namespace, string name,
        int? limit = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(name),
        };
        if (limit is int l) args["limit"] = Int(l);

        var env = await _pipe.SendAsync(
            "findInstances", args,
            ProtocolJsonContext.Default.RpcEnvelopeFindInstancesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("findInstances returned no result");
    }

    /// <summary>
    /// `walkInstance` — decode every managed instance field at
    /// <paramref name="address"/> for the given class spec.
    /// <paramref name="address"/> is passed through verbatim; protocol accepts
    /// hex with or without `0x` prefix or a decimal integer.
    /// </summary>
    public async Task<WalkInstanceResult> WalkInstanceAsync(
        string address, string assembly, string? @namespace, string name,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"]   = Str(address),
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(name),
        };
        var env = await _pipe.SendAsync(
            "walkInstance", args,
            ProtocolJsonContext.Default.RpcEnvelopeWalkInstanceResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("walkInstance returned no result");
    }

    /// <summary>
    /// `getStaticFieldData` — runtime address of a static field's storage,
    /// suitable for a follow-up <see cref="ReadMemoryAsync"/>.
    /// </summary>
    public async Task<GetStaticFieldDataResult> GetStaticFieldDataAsync(
        string assembly, string? @namespace, string name, string field,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(name),
            ["field"]     = Str(field),
        };
        var env = await _pipe.SendAsync(
            "getStaticFieldData", args,
            ProtocolJsonContext.Default.RpcEnvelopeGetStaticFieldDataResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getStaticFieldData returned no result");
    }

    /// <summary>
    /// `readMemory` — raw byte read. <paramref name="size"/> must be in
    /// [1, 1048576]; soft errors land in <see cref="ReadMemoryResult.Error"/>.
    /// </summary>
    public async Task<ReadMemoryResult> ReadMemoryAsync(
        string address, int size, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"] = Str(address),
            ["size"]    = Int(size),
        };
        var env = await _pipe.SendAsync(
            "readMemory", args,
            ProtocolJsonContext.Default.RpcEnvelopeReadMemoryResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("readMemory returned no result");
    }

    /// <summary>
    /// AOT-safe wrapper that serializes a string into a <see cref="JsonElement"/>
    /// via the source-gen context. The reflection-based
    /// <c>SerializeToElement&lt;T&gt;</c> overload trips IL2026 / IL3050.
    /// </summary>
    private static JsonElement Str(string value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.String);

    /// <summary>AOT-safe int → <see cref="JsonElement"/>.</summary>
    private static JsonElement Int(int value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Int32);

    private static void ThrowIfError(RpcError? error)
    {
        if (error is not null)
        {
            throw new PipeProtocolException(
                $"Pipe error {error.Code}: {error.Message}");
        }
    }
}
