// BackendAdapter.cs — typed façade over PipeClient.
//
// One method per pipe command. ViewModels go through this layer so they
// never touch raw JSON or pipe semantics. Per Architecture.md "Platform
// Abstraction" rule.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Adapter that exposes <see cref="GetClassInfoAsync(string,string?,string,CancellationToken)"/>
/// to exporters via the <see cref="IClassInfoLookup"/> contract — soft-fails
/// to null on either pipe error or handler-level <c>result.error</c> so
/// drilldown can fall through to opaque pointers gracefully. Also exposes
/// the live-process surfaces (<see cref="ILiveExportSource"/>) for the CE
/// XML live-data render mode.
/// </summary>
public sealed class BackendAdapter : ILiveExportSource
{
    private PipeClient _pipe;

    public BackendAdapter(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected => _pipe.IsConnected;

    public Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
        => _pipe.ConnectAsync(timeoutMs, ct);

    /// <summary>
    /// Swap the underlying <see cref="PipeClient"/>. Caller is responsible
    /// for disposing the previous one. Used by <c>MainWindowViewModel</c>
    /// after an involuntary disconnect so the next Connect attempt runs
    /// against fresh .NET pipe state — empirically the only thing that
    /// reliably clears whatever .NET / kernel cache is preferring the
    /// dead instance over the live one in the same UI process.
    /// </summary>
    public void ReplacePipe(PipeClient newPipe)
    {
        _pipe = newPipe;
    }

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
    /// `getMetadataStats` — name-shape statistics over the loaded metadata.
    /// Phase 14b uses the <c>verdict</c> field to surface a stripped /
    /// obfuscated banner. Soft-fails to <c>null</c> on pipe errors so a
    /// post-handshake call doesn't gate the rest of the connect flow.
    /// </summary>
    public async Task<MetadataStatsResult?> GetMetadataStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var env = await _pipe.SendAsync(
                "getMetadataStats", args: null,
                ProtocolJsonContext.Default.RpcEnvelopeMetadataStatsResult, ct)
                .ConfigureAwait(false);
            if (env.Error is not null) return null;
            return env.Result;
        }
        catch
        {
            return null;
        }
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
    /// <see cref="IClassInfoLookup"/> implementation: soft-fails to null on
    /// pipe error so exporter recursion doesn't tear the whole render down.
    /// </summary>
    async Task<GetClassInfoResult?> IClassInfoLookup.GetClassInfoAsync(
        string assembly, string @namespace, string name, CancellationToken ct)
    {
        try
        {
            GetClassInfoResult info = await GetClassInfoAsync(assembly, @namespace, name, ct).ConfigureAwait(false);
            return string.IsNullOrEmpty(info.Error) ? info : null;
        }
        catch
        {
            return null;
        }
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
    /// `walkInstance` — header-resolved variant. Caller hands us the address
    /// of an Il2CppObject; the DLL reads the first qword (the runtime class
    /// pointer stamped by IL2CPP) and walks fields against that exact class.
    /// Required for generic instantiations like <c>ClassAry&lt;T&gt;</c>:
    /// concrete instantiations aren't registered by name in the image's
    /// class table, so <c>il2cpp_class_from_name</c> can't find them — but
    /// the runtime always stamps the concrete <c>Il2CppClass*</c> into the
    /// header, so reading it bypasses the name lookup entirely. IL2CPP only;
    /// Mono backend still uses the named overload.
    /// </summary>
    public async Task<WalkInstanceResult> WalkInstanceFromHeaderAsync(
        string address, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"]                = Str(address),
            ["klass_from_header_addr"] = Str(address),
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
    /// `walkArray` — read a managed <c>T[]</c> header at <paramref name="address"/>
    /// and return up to <paramref name="limit"/> per-element entries. Pass
    /// <paramref name="elementType"/> when the element type is known so
    /// primitives decode inline; pass <paramref name="elementSize"/> as a
    /// width hint for struct arrays whose layout the DLL can't introspect.
    /// </summary>
    public async Task<WalkArrayResult> WalkArrayAsync(
        string address, string? elementType = null,
        int? limit = null, int? elementSize = null,
        string? elementAssembly = null,
        string? elementNamespace = null,
        string? elementName = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"] = Str(address),
        };
        if (!string.IsNullOrEmpty(elementType))      args["elementType"]      = Str(elementType);
        if (limit       is int l) args["limit"]       = Int(l);
        if (elementSize is int s) args["elementSize"] = Int(s);
        if (!string.IsNullOrEmpty(elementAssembly))  args["elementAssembly"]  = Str(elementAssembly);
        if (elementNamespace is not null)            args["elementNamespace"] = Str(elementNamespace);
        if (!string.IsNullOrEmpty(elementName))      args["elementName"]      = Str(elementName);

        var env = await _pipe.SendAsync(
            "walkArray", args,
            ProtocolJsonContext.Default.RpcEnvelopeWalkArrayResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("walkArray returned no result");
    }

    /// <summary>
    /// `whatIsAt` — Phase 17a + 17b reverse address lookup. Strategy A
    /// backward 8-byte stride scan from <paramref name="address"/> until a
    /// managed object header turns up; the resolver then walks the parent
    /// chain to locate the declared field whose
    /// <c>[offset, offset+size)</c> range contains the queried byte offset.
    /// Soft outcomes (no class found in range, address unreadable, etc.)
    /// land on <see cref="WhatIsAtResult.Found"/> = false rather than
    /// throwing — pipe-level errors still throw.
    /// </summary>
    public async Task<WhatIsAtResult> WhatIsAtAsync(
        string address, int? maxBackwardScan = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"] = Str(address),
        };
        if (maxBackwardScan is int n) args["maxBackwardScan"] = Int(n);

        var env = await _pipe.SendAsync(
            "whatIsAt", args,
            ProtocolJsonContext.Default.RpcEnvelopeWhatIsAtResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("whatIsAt returned no result");
    }

    /// <summary>
    /// `getClassRangeStats` — diagnostic for the address-finder cache. Pre-warms
    /// the cache as a side effect (BuildClassRangeCache is idempotent in the
    /// backend; first call after handshake takes ~tens of ms, every subsequent
    /// call sub-ms). Pass <paramref name="rebuild"/> to drop + rebuild.
    /// </summary>
    public async Task<ClassRangeStatsResult> GetClassRangeStatsAsync(
        bool rebuild = false, CancellationToken ct = default)
    {
        var args = rebuild
            ? new Dictionary<string, JsonElement>
              {
                  ["rebuild"] = JsonSerializer.SerializeToElement(true, ProtocolJsonContext.Default.Boolean),
              }
            : null;
        var env = await _pipe.SendAsync(
            "getClassRangeStats", args,
            ProtocolJsonContext.Default.RpcEnvelopeClassRangeStatsResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getClassRangeStats returned no result");
    }

    /// <summary>
    /// `findMethodInstances` — Phase 18a-2. Walks the supplied class's
    /// declared methods and returns every overload of <paramref name="method"/>
    /// with native address + decoded signature. Pass
    /// <paramref name="signatureMatch"/> as a substring to narrow when a
    /// caller already knows part of the signature (e.g. <c>"Vector3"</c>).
    /// </summary>
    public async Task<FindMethodInstancesResult> FindMethodInstancesAsync(
        string assembly, string @namespace, string className, string method,
        string? signatureMatch = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace),
            ["name"]      = Str(className),
            ["method"]    = Str(method),
        };
        if (!string.IsNullOrEmpty(signatureMatch))
            args["signatureMatch"] = Str(signatureMatch);
        var env = await _pipe.SendAsync(
            "findMethodInstances", args,
            ProtocolJsonContext.Default.RpcEnvelopeFindMethodInstancesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("findMethodInstances returned no result");
    }

    /// <summary>
    /// `getMethodAobPattern` — Phase 18a-3. Reads <paramref name="length"/>
    /// bytes (default 32, capped 128) from <paramref name="address"/>,
    /// disassembles them, returns a CE-style AOB pattern with rel8/rel32
    /// immediates and RIP-relative ModRM disp32 fields wildcarded. Soft
    /// errors (unreadable address) land in
    /// <see cref="GetMethodAobPatternResult.Error"/> instead of throwing.
    /// </summary>
    public async Task<GetMethodAobPatternResult> GetMethodAobPatternAsync(
        string address, int length = 32, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"] = Str(address),
            ["length"]  = Int(length),
        };
        var env = await _pipe.SendAsync(
            "getMethodAobPattern", args,
            ProtocolJsonContext.Default.RpcEnvelopeGetMethodAobPatternResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getMethodAobPattern returned no result");
    }

    /// <summary>
    /// `listFieldValuesAcrossInstances` — Phase 21a reverse-property lookup.
    /// Heap-scans for every live instance of the class spec (same scan as
    /// <see cref="FindInstancesAsync"/>), then decodes the field at
    /// <c>instance + fieldOffset</c> for each. <paramref name="fieldOffset"/>
    /// is the cumulative offset from the instance base — the caller is
    /// responsible for summing it across value-struct ancestors (see
    /// <c>InspectorFieldOffsetResolver</c>). <paramref name="fieldType"/> is
    /// the managed CLR type name (e.g. <c>"System.Int32"</c>) and selects
    /// the per-type decoder; unknown types come back as raw qword
    /// <c>ptr</c>. Soft per-instance errors land on each
    /// <see cref="ListFieldValueRow.Error"/> instead of failing the call.
    /// </summary>
    public async Task<ListFieldValuesResult> ListFieldValuesAcrossInstancesAsync(
        string assembly, string? @namespace, string name,
        long fieldOffset, string fieldType,
        int? limit = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]    = Str(assembly),
            ["namespace"]   = Str(@namespace ?? string.Empty),
            ["name"]        = Str(name),
            ["fieldOffset"] = JsonSerializer.SerializeToElement(fieldOffset, ProtocolJsonContext.Default.Int64),
            ["fieldType"]   = Str(fieldType),
        };
        if (limit is int l) args["limit"] = Int(l);

        var env = await _pipe.SendAsync(
            "listFieldValuesAcrossInstances", args,
            ProtocolJsonContext.Default.RpcEnvelopeListFieldValuesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("listFieldValuesAcrossInstances returned no result");
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
    /// `callMethod` — Phase 19. Invokes a managed method via the runtime's
    /// runtime_invoke entry point with marshaled args + a typed return.
    /// Pass <paramref name="instanceAddress"/> = null/empty for static
    /// methods; the DLL silently ignores it on a static overload but
    /// requires it on a non-static one (returns
    /// <c>instance_required_for_non_static</c>). Soft errors (resolution,
    /// arg shape, missing exports) land in
    /// <see cref="CallMethodResult.Error"/>; managed exceptions land in
    /// <see cref="CallMethodResult.Exception"/> with <see cref="CallMethodResult.Ok"/>=false.
    /// </summary>
    public async Task<CallMethodResult> CallMethodAsync(
        string assembly, string @namespace, string className, string method,
        IReadOnlyList<CallArgRequest> args,
        string? signatureMatch = null, string? instanceAddress = null,
        CancellationToken ct = default)
    {
        // Source-gen serialize the args list — keeps AOT clean (the
        // List<CallArgRequest> + CallArgRequest types are registered in
        // ProtocolJsonContext). Each arg's `Value` is already a JsonElement
        // built by the caller via JsonSerializer.SerializeToElement against
        // the appropriate primitive context entry.
        var argsArr = JsonSerializer.SerializeToElement(
            args, ProtocolJsonContext.Default.ListCallArgRequest);
        var d = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(className),
            ["method"]    = Str(method),
            ["args"]      = argsArr,
        };
        if (!string.IsNullOrEmpty(signatureMatch)) d["signatureMatch"] = Str(signatureMatch);
        if (!string.IsNullOrEmpty(instanceAddress)) d["instance"]      = Str(instanceAddress);
        var env = await _pipe.SendAsync(
            "callMethod", d,
            ProtocolJsonContext.Default.RpcEnvelopeCallMethodResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("callMethod returned no result");
    }

    /// <summary>
    /// `getEnumValues` — Phase 6 segment D. Returns the named enum members of
    /// a managed enum class plus the underlying integer type, used by Phase
    /// 19d-3's MethodCallWindow to render a ComboBox for enum-typed
    /// parameters. Soft outcomes (class not an enum, mono missing exports,
    /// metadata not loaded) land in
    /// <see cref="GetEnumValuesResult.Error"/>; pipe-level errors still throw.
    /// </summary>
    public async Task<GetEnumValuesResult> GetEnumValuesAsync(
        string assembly, string @namespace, string className,
        CancellationToken ct = default)
    {
        var d = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(className),
        };
        var env = await _pipe.SendAsync(
            "getEnumValues", d,
            ProtocolJsonContext.Default.RpcEnvelopeGetEnumValuesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getEnumValues returned no result");
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
