// BackendAdapter.cs — typed façade over PipeClient.
//
// One method per pipe command. ViewModels go through this layer so they
// never touch raw JSON or pipe semantics. Per Architecture.md "Platform
// Abstraction" rule.

using System.Collections.Generic;
using System.Diagnostics;
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
    // Two connections to the SAME \\.\pipe\DiscreteBfx server (which serves
    // up to kMaxPipeInstances=4 clients on independent threads). Heavy,
    // long-running commands (snapshot / value-scan / heap walks / bulk
    // dumps) ride the BULK pipe; lightweight read-only browse commands
    // (getClasses / getClassInfo / inspect reads) ride the INTERACTIVE
    // pipe. This lets the user keep browsing the tree + Inspector while a
    // snapshot/value-scan streams hundreds of RPCs — the interactive
    // requests no longer queue behind the bulk RPCs on a single wire.
    //
    // The DLL handlers are read-mostly and lock-free across connections
    // (only the handler-map lookup takes a brief lock; handlers run
    // outside it), so getClasses on the interactive pipe runs truly in
    // parallel with snapshotChunk on the bulk pipe. Verified safe — see
    // docs/ce-bridge-integration.md / the pipe-server concurrency notes.
    private PipeClient _pipe;   // interactive (default for all light cmds)
    private PipeClient _bulk;   // heavy / long-running cmds

    /// <summary>
    /// Commands routed to the BULK connection. Everything not listed here
    /// (metadata reads, single-instance walks, address lookups, method
    /// calls) goes on the interactive connection. Kept here as the single
    /// source of truth for the routing split.
    /// </summary>
    private static readonly HashSet<string> BulkCommands = new(System.StringComparer.Ordinal)
    {
        "snapshotBegin", "snapshotChunk", "snapshotEnd",
        "beginValueScan", "refineValueScan", "endValueScan", "scanFieldValue",
        "beginGroupScan", "refineGroupScan", "queryGroupCandidates", "endGroupScan",
        "findInstances", "listFieldValuesAcrossInstances",
        "dumpIl2cppSymbols", "dumpClassInfo", "findFieldReferences",
    };

    /// <param name="pipe">Interactive connection (light browse RPCs).</param>
    /// <param name="bulkPipe">Heavy-op connection. When null, the adapter
    /// runs single-connection (everything on <paramref name="pipe"/>) — the
    /// back-compat path used by unit tests and any caller that doesn't need
    /// the split.</param>
    public BackendAdapter(PipeClient pipe, PipeClient? bulkPipe = null)
    {
        _pipe = pipe;
        _bulk = bulkPipe ?? pipe;
    }

    /// <summary>Pick the connection for a command by its cmd string.</summary>
    private PipeClient PipeFor(string cmd)
        => BulkCommands.Contains(cmd) ? _bulk : _pipe;

    /// <summary>
    /// Route + send. Drop-in for the old <c>_pipe.SendAsync(cmd, …)</c>:
    /// the cmd (first arg) selects the bulk vs interactive connection.
    /// </summary>
    private Task<RpcEnvelope<TResult>> Send<TResult>(
        string cmd,
        Dictionary<string, JsonElement>? args,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<RpcEnvelope<TResult>> envelopeType,
        CancellationToken ct = default)
        where TResult : class
        => PipeFor(cmd).SendAsync(cmd, args, envelopeType, ct);

    /// <summary>Connected only when BOTH connections are up (a half-open
    /// state can't serve the full command surface).</summary>
    public bool IsConnected
        => _pipe.IsConnected && (ReferenceEquals(_bulk, _pipe) || _bulk.IsConnected);

    /// <summary>
    /// PID of the connected game process, resolved from the interactive pipe's
    /// server end (our backend DLL lives inside the game). Used to target the
    /// AOBMaker app's <c>GenerateAob</c> scan at the right process. Returns
    /// false when disconnected or the OS query fails.
    /// </summary>
    public bool TryGetConnectedGamePid(out uint pid)
        => _pipe.TryGetServerProcessId(out pid);

    public async Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
    {
        await _pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        if (!ReferenceEquals(_bulk, _pipe))
            await _bulk.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Swap the underlying <see cref="PipeClient"/>(s). Caller disposes the
    /// previous ones. Used by <c>MainWindowViewModel</c> after an
    /// involuntary disconnect so the next Connect attempt runs against fresh
    /// .NET pipe state — empirically the only thing that reliably clears
    /// whatever .NET / kernel cache is preferring the dead instance over the
    /// live one in the same UI process. Pass <paramref name="bulkPipe"/>
    /// null to stay single-connection.
    /// </summary>
    public void ReplacePipe(PipeClient newPipe, PipeClient? bulkPipe = null)
    {
        _pipe = newPipe;
        _bulk = bulkPipe ?? newPipe;
    }

    /// <summary>
    /// Round-trip liveness check.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var env = await Send(
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
        var env = await Send(
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
            var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
            "getClassInfo", args,
            ProtocolJsonContext.Default.RpcEnvelopeGetClassInfoResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("getClassInfo returned no result");
    }

    /// <summary>
    /// `searchSymbols` — Phase 25. Walks every class in the supplied
    /// assembly, iterates fields + methods, returns rows whose name contains
    /// the supplied substring (ASCII case-insensitive). Replaces the
    /// "click each class in turn" loop the user had to do when hunting a
    /// property like "MaxHP" that could live anywhere.
    ///
    /// `kinds` filters by row type (`"field"` / `"method"`); leave empty/null
    /// for both. `limit` caps the result set (DLL caps at 5000, default 200);
    /// when the cap fires <see cref="SearchSymbolsResult.Truncated"/> is true.
    /// Passing <paramref name="staticOnly"/>=true drops every instance member
    /// at the DLL — this is the only path that accepts an empty
    /// <paramref name="pattern"/> ("list every static member in this assembly").
    /// </summary>
    public async Task<SearchSymbolsResult> SearchSymbolsAsync(
        string assembly, string pattern,
        IReadOnlyList<string>? kinds = null, int? limit = null,
        bool staticOnly = false,
        CancellationToken ct = default)
        => await SearchSymbolsCoreAsync(
            assembly, assemblies: null, pattern, kinds, limit, staticOnly, ct).ConfigureAwait(false);

    /// <summary>
    /// Multi-assembly variant of <see cref="SearchSymbolsAsync(string,string,IReadOnlyList{string}?,int?,bool,CancellationToken)"/>.
    /// Walks every assembly in <paramref name="assemblies"/> under a single
    /// global <paramref name="limit"/>. Used by the Symbol Finder's
    /// "Search all user assemblies" toggle so the user can hunt a property
    /// across the whole user-code surface in one click.
    /// </summary>
    public async Task<SearchSymbolsResult> SearchSymbolsAsync(
        IReadOnlyList<string> assemblies, string pattern,
        IReadOnlyList<string>? kinds = null, int? limit = null,
        bool staticOnly = false,
        CancellationToken ct = default)
        => await SearchSymbolsCoreAsync(
            assembly: null, assemblies, pattern, kinds, limit, staticOnly, ct).ConfigureAwait(false);

    private async Task<SearchSymbolsResult> SearchSymbolsCoreAsync(
        string? assembly, IReadOnlyList<string>? assemblies,
        string pattern, IReadOnlyList<string>? kinds, int? limit,
        bool staticOnly,
        CancellationToken ct)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["pattern"] = Str(pattern),
        };
        if (assemblies is { Count: > 0 })
        {
            using var doc = JsonDocument.Parse(BuildStringArrayJson(assemblies));
            args["assemblies"] = doc.RootElement.Clone();
        }
        else if (!string.IsNullOrEmpty(assembly))
        {
            args["assembly"] = Str(assembly);
        }
        else
        {
            throw new ArgumentException(
                "searchSymbols requires either a non-empty `assembly` or `assemblies`.");
        }
        if (kinds is { Count: > 0 })
        {
            // Build an inline JSON array — same Str/Int helper pattern used
            // by the rest of this file; keeps everything reflection-free
            // for AOT.
            using var doc = JsonDocument.Parse(BuildStringArrayJson(kinds));
            args["kinds"] = doc.RootElement.Clone();
        }
        if (limit.HasValue) args["limit"] = Int(limit.Value);
        if (staticOnly)     args["staticOnly"] = Bool(true);

        var env = await Send(
            "searchSymbols", args,
            ProtocolJsonContext.Default.RpcEnvelopeSearchSymbolsResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("searchSymbols returned no result");
    }

    /// <summary>
    /// Phase 20-MT diagnostics — read-only snapshot of the DLL's main-thread
    /// dispatcher counters. Useful for: (1) the connection-bar banner that
    /// proactively offers Enable runInBackground when the engine is paused
    /// (Phase 20-MT v2), (2) pre-flight health checks before mainThread RPCs
    /// (avoid 5-second timeouts when we can already tell the dispatcher is
    /// stuck), (3) the stress smoke <c>Test-MainThreadBurst.ps1</c>.
    /// </summary>
    public async Task<DispatcherStatsResult> GetDispatcherStatsAsync(CancellationToken ct = default)
    {
        var env = await Send(
            "getDispatcherStats", new Dictionary<string, JsonElement>(),
            ProtocolJsonContext.Default.RpcEnvelopeDispatcherStatsResult, ct)
            .ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result
            ?? throw new PipeProtocolException("getDispatcherStats returned no result");
    }

    /// <summary>
    /// Phase 28 — Field → Methods cross-reference. Inverse of
    /// <see cref="SearchSymbolsAsync(string,string,IReadOnlyList{string}?,int?,bool,CancellationToken)"/>:
    /// given the FQN of a Unity field, list every method that reads
    /// and/or writes it. Mono walks IL bytecode (exact, no false
    /// positives). IL2CPP scans GameAssembly.dll's .text for
    /// RIP-relative references to the static-storage address
    /// (static fields only in v1; instance fields surface a clean
    /// "not supported in v1" error). Same wire shape across backends.
    /// </summary>
    /// <param name="assembly">Declaring assembly (e.g. "Assembly-CSharp").</param>
    /// <param name="ns">Namespace ("" allowed).</param>
    /// <param name="className">Class simple name; "Outer/Inner" for nested.</param>
    /// <param name="fieldName">Field simple name.</param>
    /// <param name="kind">"any" / "read" / "write" — default "any".</param>
    /// <param name="limit">Match cap (DLL hard-caps at 5000, default 200).</param>
    public async Task<FindFieldReferencesResult> FindFieldReferencesAsync(
        string assembly, string? ns, string className, string fieldName,
        string kind = "any", int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(assembly))
            throw new ArgumentException("assembly required", nameof(assembly));
        if (string.IsNullOrEmpty(className))
            throw new ArgumentException("className required", nameof(className));
        if (string.IsNullOrEmpty(fieldName))
            throw new ArgumentException("fieldName required", nameof(fieldName));
        if (kind != "any" && kind != "read" && kind != "write")
            throw new ArgumentException("kind must be 'any' / 'read' / 'write'", nameof(kind));

        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["ns"]        = Str(ns ?? string.Empty),
            ["class"]     = Str(className),
            ["fieldName"] = Str(fieldName),
            ["kind"]      = Str(kind),
        };
        if (limit.HasValue) args["limit"] = Int(limit.Value);

        var env = await Send(
            "findFieldReferences", args,
            ProtocolJsonContext.Default.RpcEnvelopeFindFieldReferencesResult, ct)
            .ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result
            ?? throw new PipeProtocolException("findFieldReferences returned no result");
    }

    private static string BuildStringArrayJson(IReadOnlyList<string> items)
    {
        // Tiny hand-rolled emitter so we don't need a JsonSerializable for
        // a string[] (AOT analyzers complain on the generic path). Shared
        // by `kinds` and `assemblies` arg builders.
        var sb = new System.Text.StringBuilder(32);
        sb.Append('[');
        for (int i = 0; i < items.Count; ++i)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(items[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// `dumpIl2cppSymbols` — bulk-dump every IL2CPP class and/or method
    /// across the supplied assemblies into the rows the debugger-symbol
    /// exporters consume. IL2CPP-only (Mono methods don't have stable
    /// module-relative addresses — see [docs/export-formats.md] for why
    /// Mono isn't supported here).
    /// </summary>
    public async Task<DumpIl2cppSymbolsResult> DumpIl2cppSymbolsAsync(
        IReadOnlyList<string> assemblies,
        bool includeClasses = true,
        bool includeMethods = true,
        bool computeHumanSuffix = true,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (assemblies is null || assemblies.Count == 0)
        {
            throw new ArgumentException(
                "dumpIl2cppSymbols requires at least one assembly.",
                nameof(assemblies));
        }
        if (!includeClasses && !includeMethods)
        {
            throw new ArgumentException(
                "dumpIl2cppSymbols requires includeClasses or includeMethods.");
        }

        var args = new Dictionary<string, JsonElement>();
        using (var asmDoc = JsonDocument.Parse(BuildStringArrayJson(assemblies)))
        {
            args["assemblies"] = asmDoc.RootElement.Clone();
        }

        var kinds = new List<string>(2);
        if (includeClasses) kinds.Add("class");
        if (includeMethods) kinds.Add("method");
        using (var kindsDoc = JsonDocument.Parse(BuildStringArrayJson(kinds)))
        {
            args["kinds"] = kindsDoc.RootElement.Clone();
        }

        args["computeHumanSuffix"] = Bool(computeHumanSuffix);
        if (limit.HasValue) args["limit"] = Int(limit.Value);

        var env = await Send(
            "dumpIl2cppSymbols", args,
            ProtocolJsonContext.Default.RpcEnvelopeDumpIl2cppSymbolsResult, ct)
            .ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result
            ?? throw new PipeProtocolException("dumpIl2cppSymbols returned no result");
    }

    /// <summary>
    /// `dumpClassInfo` — bulk equivalent of getClassInfo. Walks every
    /// class in every requested assembly server-side under ONE thread
    /// context and returns the full payload in ONE pipe round-trip.
    /// Replaces the N×getClassInfo client-side loop that
    /// <see cref="Export.Il2cppHeaderBatchExporter"/> originally used —
    /// fixes the slow-quit issue documented in lessons-learned.md
    /// "Phase C follow-up — IL2CPP equivalent". Supported on BOTH
    /// backends; Mono method rows carry null Address (JIT addresses
    /// don't survive restart, on-demand monoCompileMethod is the right
    /// answer when an address is needed).
    /// </summary>
    public async Task<DumpClassInfoResult> DumpClassInfoAsync(
        IReadOnlyList<string> assemblies,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (assemblies is null || assemblies.Count == 0)
        {
            throw new ArgumentException(
                "dumpClassInfo requires at least one assembly.",
                nameof(assemblies));
        }

        var args = new Dictionary<string, JsonElement>();
        using (var asmDoc = JsonDocument.Parse(BuildStringArrayJson(assemblies)))
        {
            args["assemblies"] = asmDoc.RootElement.Clone();
        }
        if (limit.HasValue) args["limit"] = Int(limit.Value);

        var env = await Send(
            "dumpClassInfo", args,
            ProtocolJsonContext.Default.RpcEnvelopeDumpClassInfoResult, ct)
            .ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result
            ?? throw new PipeProtocolException("dumpClassInfo returned no result");
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
        int? limit = null, bool aliveOnly = false, string? fieldFilterJson = null,
        bool sane = false, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["assembly"]  = Str(assembly),
            ["namespace"] = Str(@namespace ?? string.Empty),
            ["name"]      = Str(name),
        };
        if (limit is int l) args["limit"] = Int(l);
        // Phase 32 — generic false-positive reduction. aliveOnly drops destroyed
        // Unity shells (m_CachedPtr == 0); sane drops stale/torn objects (any
        // managed reference field holding garbage); fieldFilter is the per-class
        // predicate engine (a JSON array of {offset,type,op,value}). Parsing the
        // array is AOT-safe (JsonDocument.Parse, no reflection).
        if (aliveOnly) args["aliveOnly"] = Bool(true);
        if (sane)      args["sane"]      = Bool(true);
        if (!string.IsNullOrWhiteSpace(fieldFilterJson))
        {
            JsonDocument? ffDoc = null;
            try { ffDoc = JsonDocument.Parse(fieldFilterJson); }
            catch (JsonException jx)
            {
                throw new PipeProtocolException("fieldFilter is not valid JSON: " + jx.Message);
            }
            using (ffDoc)
            {
                if (ffDoc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new PipeProtocolException("fieldFilter must be a JSON array of predicate objects");
                args["fieldFilter"] = ffDoc.RootElement.Clone();
            }
        }

        var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
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

        var env = await Send(
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

        var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
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
        var env = await Send(
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

        var env = await Send(
            "listFieldValuesAcrossInstances", args,
            ProtocolJsonContext.Default.RpcEnvelopeListFieldValuesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("listFieldValuesAcrossInstances returned no result");
    }

    /// <summary>
    /// `setInstanceField` — Phase 21a Inspector field write-back. Writes
    /// <paramref name="value"/> as <paramref name="type"/> at
    /// <c>instance + fieldOffset</c> via SEH-protected SafeWriteBytes.
    /// <paramref name="value"/> must be a <see cref="JsonElement"/> already
    /// serialized via the source-gen context (use <see cref="CallArgFactory"/>
    /// helpers or one of the convenience overloads below). Soft errors
    /// (memory not writable, value out of range, unsupported type) land in
    /// <see cref="SetInstanceFieldResult.Error"/>; the result carries
    /// <c>Ok=false</c> in that case. Pipe-level errors still throw.
    /// </summary>
    public async Task<SetInstanceFieldResult> SetInstanceFieldAsync(
        string instanceAddress, long fieldOffset, string type, JsonElement value,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["address"]     = Str(instanceAddress),
            ["fieldOffset"] = JsonSerializer.SerializeToElement(fieldOffset, ProtocolJsonContext.Default.Int64),
            ["type"]        = Str(type),
            ["value"]       = value,
        };
        var env = await Send(
            "setInstanceField", args,
            ProtocolJsonContext.Default.RpcEnvelopeSetInstanceFieldResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("setInstanceField returned no result");
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
        var env = await Send(
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
        bool mainThread = false, int? mainThreadTimeoutMs = null,
        CancellationToken ct = default)
    {
        // Source-gen serialize the args list — keeps AOT clean (the
        // List<CallArgRequest> + CallArgRequest types are registered in
        // ProtocolJsonContext). Each arg's `Value` is already a JsonElement
        // built by the caller via JsonSerializer.SerializeToElement against
        // the appropriate primitive context entry.
        //
        // Source-gen's `ListCallArgRequest` JsonTypeInfo refuses to box
        // an array — callers pass IReadOnlyList<CallArgRequest> for API
        // ergonomics but the source-gen wants the concrete List<>. Coerce
        // in one place so callers can pass arrays, lists, or hand-rolled
        // collections interchangeably without surprises.
        List<CallArgRequest> argsList = args as List<CallArgRequest>
            ?? new List<CallArgRequest>(args);
        var argsArr = JsonSerializer.SerializeToElement(
            argsList, ProtocolJsonContext.Default.ListCallArgRequest);
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
        // Phase 20-MT: opt-in main-thread marshaling. Off by default to
        // preserve Phase 19 caller behaviour (pure-managed math methods
        // run faster on the worker thread). MethodCallWindow's "Run on
        // main thread" checkbox flips it on for Unity engine API calls.
        if (mainThread)
        {
            d["main_thread"] = Bool(true);
            if (mainThreadTimeoutMs.HasValue)
            {
                d["main_thread_timeout_ms"] = Int(mainThreadTimeoutMs.Value);
            }
        }
        var env = await Send(
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
        var env = await Send(
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

    /// <summary>AOT-safe bool → <see cref="JsonElement"/>.</summary>
    private static JsonElement Bool(bool value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Boolean);

    /// <summary>AOT-safe double → <see cref="JsonElement"/>. Used by the
    /// value-scan path which carries float / range bounds.</summary>
    private static JsonElement Dbl(double value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Double);

    // -----------------------------------------------------------------------
    // Phase 27 — Unity-aware value search.
    // -----------------------------------------------------------------------

    /// <summary>
    /// `beginValueScan` — initial scan, opens a session for `refineValueScan`.
    /// Numeric path: dataType is one of int8..uint64 / float / double; value
    /// is a double (typed into the right bitness server-side via PackJsonValueToBytes).
    /// String path: dataType = "string", value = the search string.
    /// </summary>
    public async Task<BeginValueScanResult> BeginValueScanAsync(
        string dataType, string scanType, double value,
        double? valueTo = null, int? limit = null, double? tolerance = null,
        int? reuseIndexMs = null, int? retainCap = null, bool? parallel = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["dataType"] = Str(dataType),
            ["scanType"] = Str(scanType),
            ["value"]    = Dbl(value),
        };
        if (valueTo.HasValue)   args["valueTo"]   = Dbl(valueTo.Value);
        if (limit.HasValue)     args["limit"]     = Int(limit.Value);
        if (tolerance.HasValue) args["tolerance"] = Dbl(tolerance.Value);
        // Value Search "Parallel" toggle (default on server-side). Only sent when
        // the user turned it OFF → server caps the scan to a single thread.
        if (parallel.HasValue)  args["parallel"]  = Bool(parallel.Value);
        // Decoupled retain cap (how many managed candidates the DLL KEEPS for
        // refine, separate from `limit` which is the display count). Bitness-
        // gated server-side; null = server default.
        if (retainCap.HasValue) args["retainCap"] = Int(retainCap.Value);
        // Opt the DLL into reusing a recently-built reverse-lookup index across
        // back-to-back scans (the aggregate "any numeric width" fan-out). 0 /
        // null = always a fresh heap walk.
        if (reuseIndexMs.HasValue) args["reuseIndexMs"] = Int(reuseIndexMs.Value);

        var env = await Send(
            "beginValueScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeBeginValueScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("beginValueScan returned no result");
    }

    /// <summary>Cancel an in-flight value scan. The primary connection is busy
    /// running the scan, so this opens a SHORT-LIVED second pipe connection (the
    /// DLL allows multiple instances) and sends <c>cancelScan</c>, which arms the
    /// DLL's cooperative cancel flag so the long loops bail + free their work.
    /// Best-effort / fire-and-forget — connect/IO failures are swallowed (if the
    /// UI itself is going away, the DLL's disconnect monitor aborts the scan).</summary>
    public async Task CancelScanAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cancelPipe = new PipeClient();
            await cancelPipe.ConnectAsync(2000, ct).ConfigureAwait(false);
            await cancelPipe.SendAsync(
                "cancelScan", null,
                ProtocolJsonContext.Default.RpcEnvelopeCancelScanResult, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: the scan will still time out / disconnect-abort */ }
    }

    /// <summary>String-scan variant of beginValueScan. Server uses scanFieldValue
    /// internally (no session for strings in v1 — string refine isn't useful
    /// because the matched bytes are deterministic; a refine "unchanged" would
    /// always pass).</summary>
    public async Task<BeginValueScanResult> ScanStringAsync(
        string needle, string encoding = "utf16", string matchMode = "contains",
        int? limit = null, bool? parallel = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["dataType"]  = Str("string"),
            ["value"]     = Str(needle),
            ["encoding"]  = Str(encoding),
            ["matchMode"] = Str(matchMode),
        };
        if (limit.HasValue) args["limit"] = Int(limit.Value);
        // "Parallel" toggle — only sent when OFF (server caps to single thread).
        if (parallel.HasValue) args["parallel"] = Bool(parallel.Value);

        // scanFieldValue returns the same wire envelope shape as
        // beginValueScan (minus sessionId), so it deserializes cleanly
        // into BeginValueScanResult — the SessionId field stays empty
        // for string scans.
        var env = await Send(
            "scanFieldValue", args,
            ProtocolJsonContext.Default.RpcEnvelopeBeginValueScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("scanFieldValue (string) returned no result");
    }

    /// <summary>
    /// `refineValueScan` — narrow an existing session by scanType.
    /// scanType ∈ exact / range / changed / unchanged / increased / decreased / newValue.
    /// `value` / `valueTo` only required for exact / range / newValue.
    /// </summary>
    public async Task<RefineValueScanResult> RefineValueScanAsync(
        string sessionId, string scanType,
        double? value = null, double? valueTo = null, int? limit = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["sessionId"] = Str(sessionId),
            ["scanType"]  = Str(scanType),
        };
        if (value.HasValue)   args["value"]   = Dbl(value.Value);
        if (valueTo.HasValue) args["valueTo"] = Dbl(valueTo.Value);
        if (limit.HasValue)   args["limit"]   = Int(limit.Value);

        var env = await Send(
            "refineValueScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeRefineValueScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("refineValueScan returned no result");
    }

    /// <summary>`endValueScan` — release the session's candidate buffer.</summary>
    public async Task<EndValueScanResult> EndValueScanAsync(
        string sessionId, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement> { ["sessionId"] = Str(sessionId) };
        var env = await Send(
            "endValueScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeEndValueScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("endValueScan returned no result");
    }

    // -----------------------------------------------------------------------
    // Group scan (multiple-values) — begin/refine/query/end. Reuses the value
    // scan pipeline server-side (one shared reverse-lookup index, ScanForValue
    // per slot, per-instance SDR). Same session/cancel contract.
    // -----------------------------------------------------------------------

    /// <summary>`beginGroupScan` — scan 2..4 values at once; returns instances
    /// holding ALL of them at distinct numeric fields (a System of Distinct
    /// Representatives). Each slot's first scan is exact|range; prev-value modes
    /// go through <see cref="RefineGroupScanAsync"/>.</summary>
    public async Task<BeginGroupScanResult> BeginGroupScanAsync(
        List<GroupSlotRequest> slots,
        int? limit = null, int? retainCap = null, int? rawCap = null, bool? parallel = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["slots"] = JsonSerializer.SerializeToElement(
                slots, ProtocolJsonContext.Default.ListGroupSlotRequest),
        };
        if (limit.HasValue)     args["limit"]     = Int(limit.Value);
        if (retainCap.HasValue) args["retainCap"] = Int(retainCap.Value);
        if (rawCap.HasValue)    args["rawCap"]    = Int(rawCap.Value);
        if (parallel.HasValue)  args["parallel"]  = Bool(parallel.Value);

        var env = await Send(
            "beginGroupScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeBeginGroupScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("beginGroupScan returned no result");
    }

    /// <summary>`refineGroupScan` — narrow a session with a PER-SLOT predicate
    /// chain (slots aligned to the original scan). Each slot's scanType ∈ exact /
    /// range / newValue / changed / unchanged / increased / decreased; value(s)
    /// only required for exact / range / newValue. A candidate survives only
    /// while every slot still maps to a distinct field.</summary>
    public async Task<RefineGroupScanResult> RefineGroupScanAsync(
        string sessionId, List<GroupSlotRequest> slots, int? limit = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["sessionId"] = Str(sessionId),
            ["slots"]     = JsonSerializer.SerializeToElement(
                slots, ProtocolJsonContext.Default.ListGroupSlotRequest),
        };
        if (limit.HasValue) args["limit"] = Int(limit.Value);

        var env = await Send(
            "refineGroupScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeRefineGroupScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("refineGroupScan returned no result");
    }

    /// <summary>`queryGroupCandidates` — paged view of a session's surviving
    /// candidates (the result sets are small, but this supports scrolling).</summary>
    public async Task<QueryGroupCandidatesResult> QueryGroupCandidatesAsync(
        string sessionId, int? offset = null, int? limit = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement> { ["sessionId"] = Str(sessionId) };
        if (offset.HasValue) args["offset"] = Int(offset.Value);
        if (limit.HasValue)  args["limit"]  = Int(limit.Value);

        var env = await Send(
            "queryGroupCandidates", args,
            ProtocolJsonContext.Default.RpcEnvelopeQueryGroupCandidatesResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("queryGroupCandidates returned no result");
    }

    /// <summary>`endGroupScan` — release the group session.</summary>
    public async Task<EndGroupScanResult> EndGroupScanAsync(
        string sessionId, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement> { ["sessionId"] = Str(sessionId) };
        var env = await Send(
            "endGroupScan", args,
            ProtocolJsonContext.Default.RpcEnvelopeEndGroupScanResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("endGroupScan returned no result");
    }

    // -----------------------------------------------------------------------
    // Phase 29 — Snapshot & Diff.
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>snapshotBegin</c> — run one wide heap walk + decode every class
    /// (statics + instances + array elements) under one outer
    /// ThreadAttachGuard, cache the per-class JSON payload server-side,
    /// return the snapshot id + plan summary.
    /// </summary>
    /// <param name="assemblies">Optional narrowing list. <c>null</c> or
    /// empty → server defaults to every visible assembly ("全抓").</param>
    /// <param name="instanceCap">Max instances captured per class. Default
    /// 100; hard-cap 1024 server-side.</param>
    /// <param name="arrayCap">Max elements per <c>T[]</c> / <c>List&lt;T&gt;</c>
    /// field. Default 50; hard-cap 256 server-side.</param>
    /// <param name="instanceDecodeBudget">Total decoded-instance budget across
    /// all classes (0 = unlimited; server default 50000, hard-cap 2,000,000).
    /// Bounds decode time + memory; the server decodes low-population classes
    /// first so truncation drops high-population noise.</param>
    public async Task<SnapshotBeginResult> SnapshotBeginAsync(
        IReadOnlyList<string>? assemblies = null,
        int? instanceCap = null,
        int? arrayCap    = null,
        bool includeSystemAssemblies = false,
        bool arrayElementFields = false,
        int? elementFieldCap = null,
        IReadOnlyList<string>? arrayElementTargets = null,
        bool captureStaticValues = false,
        int? instanceDecodeBudget = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>();
        if (assemblies is { Count: > 0 })
        {
            using var doc = JsonDocument.Parse(BuildStringArrayJson(assemblies));
            args["assemblies"] = doc.RootElement.Clone();
        }
        if (instanceCap.HasValue) args["instanceCap"] = Int(instanceCap.Value);
        if (arrayCap.HasValue)    args["arrayCap"]    = Int(arrayCap.Value);
        // Phase 30a — total decoded-instance budget (0 = unlimited). Bounds
        // decode time + memory; the DLL sorts the plan low-population-first so
        // truncation drops high-population noise classes.
        if (instanceDecodeBudget.HasValue) args["instanceDecodeBudget"] = Int(instanceDecodeBudget.Value);
        if (includeSystemAssemblies) args["includeSystemAssemblies"] = Bool(true);
        // Phase 29f (Layer B) — reference-array element inner-field capture.
        if (arrayElementFields) args["arrayElementFields"] = Bool(true);
        if (elementFieldCap.HasValue) args["elementFieldCap"] = Int(elementFieldCap.Value);
        if (arrayElementTargets is { Count: > 0 })
        {
            using var doc = JsonDocument.Parse(BuildStringArrayJson(arrayElementTargets));
            args["arrayElementTargets"] = doc.RootElement.Clone();
        }
        if (captureStaticValues) args["captureStaticValues"] = Bool(true);

        var env = await Send(
            "snapshotBegin", args,
            ProtocolJsonContext.Default.RpcEnvelopeSnapshotBeginResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("snapshotBegin returned no result");
    }

    /// <summary>
    /// <c>snapshotChunk</c> — fetch the next slice of pre-decoded class
    /// payloads. Loop until <see cref="SnapshotChunkResult.Done"/>.
    /// </summary>
    public async Task<SnapshotChunkResult> SnapshotChunkAsync(
        string snapshotId, int cursor, int? limit = null,
        CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["snapshotId"] = Str(snapshotId),
            ["cursor"]     = Int(cursor),
        };
        if (limit.HasValue) args["limit"] = Int(limit.Value);

        var env = await Send(
            "snapshotChunk", args,
            ProtocolJsonContext.Default.RpcEnvelopeSnapshotChunkResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        var result = env.Result ?? throw new PipeProtocolException("snapshotChunk returned no result");
        // Phase 30d telemetry — copy the round-trip split off the pipe that handled
        // this call (deterministic via PipeFor) so the capture summary can attribute
        // the per-chunk cost to wire/server (read) vs JSON deserialize vs payload size.
        var chunkPipe = PipeFor("snapshotChunk");
        result.ReadMs        = chunkPipe.LastReadMs;
        result.ParseMs       = chunkPipe.LastParseMs;
        result.ResponseChars = chunkPipe.LastResponseChars;
        // Phase 30a — re-expand the deduped wire (schema-once + per-instance value
        // arrays) back into the inline SnapshotFieldEntry model the rest of the UI
        // + SQLite expect. No-op for old (non-deduped) chunks.
        // Phase 30d telemetry — time the re-expansion so the capture summary can
        // tell it apart from the server-decode + pipe cost inside `fetch`.
        var expandSw = Stopwatch.StartNew();
        result.NormalizeDeduped();
        result.ExpandMs = expandSw.ElapsedMilliseconds;
        return result;
    }

    /// <summary><c>snapshotEnd</c> — drop the server-side cached plan.</summary>
    public async Task<SnapshotEndResult> SnapshotEndAsync(
        string snapshotId, CancellationToken ct = default)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["snapshotId"] = Str(snapshotId),
        };
        var env = await Send(
            "snapshotEnd", args,
            ProtocolJsonContext.Default.RpcEnvelopeSnapshotEndResult, ct).ConfigureAwait(false);
        ThrowIfError(env.Error);
        return env.Result ?? throw new PipeProtocolException("snapshotEnd returned no result");
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
