// SceneEnumerator.cs — Phase 20b helper for Scene Explorer UI.
//
// Ports the recipe from scripts/unity_scene.lua into C# so DiscreteUI's
// Scene tab can drive the same enumeration without going through a CE
// Lua shell. Pure composition over BackendAdapter.CallMethodAsync +
// WalkArrayAsync; no DLL changes needed — the heavy lifting lives in
// the existing Phase 19 callMethod RPC.
//
// All Unity engine icalls are made with `mainThread: true` (Phase 20-MT
// dispatcher) — calling them on the worker thread freezes the game on
// any non-trivial scene (the original Phase 20a discovery).
//
// Cross-Unity-version note: the FindObjects* API surface drifted three
// times across the Unity 2017 → 2022 → 6000 timeline. We probe in
// priority order (most-recent-first), fall back on the next overload
// when one isn't compiled or stripped. See the per-overload comment
// in `EnumerateGameObjectAddressesAsync`.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

public sealed class SceneEnumerator
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;

    // Cached managed Type pointer for UnityEngine.GameObject. First
    // EnumerateAsync resolves it via Type.GetType; subsequent calls reuse.
    // Per-process — invalidated when pipe disconnects (UI replays the
    // whole VM on next Connect so the cache dies naturally).
    private string? _gameObjectTypeAddress;

    private const string MscorlibAssembly         = "mscorlib";
    private const string SystemNamespace          = "System";
    private const string UnityCoreModuleAssembly  = "UnityEngine.CoreModule";
    private const string UnityEngineNamespace     = "UnityEngine";
    private const string GameObjectTypeFqn        = "UnityEngine.GameObject, UnityEngine.CoreModule";

    public SceneEnumerator(BackendAdapter backend, FileLogger logger)
    {
        _backend = backend;
        _logger  = logger;
    }

    /// <summary>
    /// Returns the first <paramref name="limit"/> live <c>UnityEngine.GameObject</c>
    /// instances in the active scene as raw "0x..." addresses, in
    /// engine enumeration order. Names are NOT decoded — caller batches
    /// name fetches via <see cref="GetNameAsync"/> based on what the
    /// UI actually displays. <paramref name="limit"/> caps at 4096 (the
    /// DLL's walkArray hard cap).
    /// </summary>
    public async Task<EnumerateResult> EnumerateGameObjectAddressesAsync(
        int limit, CancellationToken ct = default)
    {
        if (limit <= 0)   limit = 100;
        if (limit > 4096) limit = 4096;

        string? typeAddr = await ResolveGameObjectTypeAsync(ct).ConfigureAwait(false);
        if (typeAddr is null)
        {
            return EnumerateResult.Failure(
                "Type.GetType(\"UnityEngine.GameObject, UnityEngine.CoreModule\") returned null. "
                + "Probably mscorlib not loaded yet or Unity hasn't initialised its assemblies.");
        }

        // Probe order — most-recent-first. Each overload's signatureMatch
        // string is exact (NOT a substring) to avoid the close-paren trap
        // (`(System.Type)` would substring-match `(System.Type, System.Boolean)`
        // because Type is followed by a comma in the haystack, never a
        // close paren — but the needle ends with ')'. Keep the exact
        // form to be unambiguous in both directions).
        var attempts = new (string Method, string Signature, CallArgRequest[] Args, string Note)[]
        {
            // Unity 2022+ replacement. The 3-arg overload is the compiled
            // one; generic FindObjectsByType<T>(...) overloads exist but
            // their JIT address is 0x0. Enums marshal as int32.
            (
                "FindObjectsByType",
                "(System.Type, UnityEngine.FindObjectsInactive, UnityEngine.FindObjectsSortMode)",
                new[]
                {
                    CallArgFactory.Address(typeAddr),
                    CallArgFactory.Int("int32", 1),   // FindObjectsInactive.Include
                    CallArgFactory.Int("int32", 0),   // FindObjectsSortMode.None
                },
                "Unity 2022+: 3-arg ByType (Type, Include=1, None=0)"
            ),
            // Unity 2018-2021: 2-arg form survives code-stripping on shipping
            // builds. `bool` = includeInactive.
            (
                "FindObjectsOfType",
                "(System.Type, System.Boolean)",
                new[]
                {
                    CallArgFactory.Address(typeAddr),
                    CallArgFactory.Bool(true),
                },
                "Unity 2018-2021: 2-arg (Type, includeInactive=true)"
            ),
            // Unity 2018-2021 1-arg fallback. Often stripped, sometimes not.
            // Active-only; misses inactive objects.
            (
                "FindObjectsOfType",
                "(System.Type)",
                new[] { CallArgFactory.Address(typeAddr) },
                "Unity 2018-2021: 1-arg (Type) — active only"
            ),
        };

        string? arrayAddress = null;
        string  usedNote     = string.Empty;
        string  lastErr      = string.Empty;
        foreach (var attempt in attempts)
        {
            var r = await _backend.CallMethodAsync(
                UnityCoreModuleAssembly, UnityEngineNamespace, "Object", attempt.Method,
                attempt.Args,
                signatureMatch: attempt.Signature,
                mainThread: true,
                ct: ct).ConfigureAwait(false);

            if (r.Ok && r.ReturnValue.ValueKind == JsonValueKind.String)
            {
                arrayAddress = r.ReturnValue.GetString();
                usedNote     = attempt.Note;
                _logger.Write(Constants.LogCategoryView,
                    $"SceneEnumerator: array via Object.{attempt.Method} {attempt.Signature} — {attempt.Note}");
                break;
            }
            lastErr = r.Exception ?? r.Error ?? "unknown failure";
        }

        if (string.IsNullOrEmpty(arrayAddress) || arrayAddress == "0x0000000000000000")
        {
            return EnumerateResult.Failure(
                $"All FindObjects* overloads failed (last error: {lastErr}). "
                + "Most likely IL2CPP code stripping removed every overload — "
                + "very rare; consider Phase 20-HEAP fallback if you see this.");
        }

        // walkArray is a pure memory read on the DLL side, no main-thread
        // pressure. Returns up to `limit` Il2CppObject* / MonoObject*
        // elements; for pointer-typed arrays (which `Object[]` is) the
        // address lives in the `ptr` field — `value` carries primitives
        // for non-pointer arrays. Coerce to a hex-string list.
        var arrayResult = await _backend.WalkArrayAsync(
            arrayAddress, limit: limit, ct: ct).ConfigureAwait(false);

        var elements = arrayResult.Elements ?? new List<WalkedArrayElement>();
        var addresses = new List<string>(elements.Count);
        foreach (var e in elements)
        {
            string? hex = e.Ptr;
            if (string.IsNullOrEmpty(hex)
                && e.Value is JsonElement v
                && v.ValueKind == JsonValueKind.String)
            {
                hex = v.GetString();
            }
            if (string.IsNullOrEmpty(hex) || hex == "0x0000000000000000") continue;
            addresses.Add(hex);
        }

        return EnumerateResult.Success(addresses, arrayResult.Count, usedNote);
    }

    /// <summary>
    /// Returns the <c>name</c> property of a GameObject (or any Unity
    /// Object subclass — same engine getter). Null on call failure or
    /// for a destroyed-but-still-referenced wrapper. Caller should
    /// treat null as "render &lt;no name&gt;", not throw.
    /// </summary>
    public async Task<string?> GetNameAsync(string gameObjectAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameObjectAddress)) return null;

        var r = await _backend.CallMethodAsync(
            UnityCoreModuleAssembly, UnityEngineNamespace, "Object", "get_name",
            new List<CallArgRequest>(),
            signatureMatch: "()",
            instanceAddress: gameObjectAddress,
            mainThread: true,
            ct: ct).ConfigureAwait(false);
        if (!r.Ok || r.ReturnValue.ValueKind != JsonValueKind.String) return null;
        return r.ReturnValue.GetString();
    }

    /// <summary>
    /// Returns the direct children of <paramref name="gameObjectAddress"/>
    /// (one level down — caller recursively expands the tree). Each
    /// child has its name pre-decoded so the UI can render immediately
    /// without a second pass.
    ///
    /// Cost: 2 + 2N icalls (get_transform + get_childCount + N × GetChild
    /// + N × get_gameObject + N × get_name). At Phase 20-MT's typical
    /// ~5-15 ms per call this is fine for 0-30 children but pessimistic
    /// past that — fortunately gameplay GameObjects rarely have more
    /// than that under one parent.
    /// </summary>
    public async Task<IReadOnlyList<GameObjectRef>> GetChildrenAsync(
        string gameObjectAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameObjectAddress)) return Array.Empty<GameObjectRef>();

        // 1. GameObject.transform (property accessor).
        var transformR = await _backend.CallMethodAsync(
            UnityCoreModuleAssembly, UnityEngineNamespace, "GameObject", "get_transform",
            new List<CallArgRequest>(),
            signatureMatch: "()",
            instanceAddress: gameObjectAddress,
            mainThread: true,
            ct: ct).ConfigureAwait(false);
        if (!transformR.Ok || transformR.ReturnValue.ValueKind != JsonValueKind.String)
            return Array.Empty<GameObjectRef>();
        string? transformAddr = transformR.ReturnValue.GetString();
        if (string.IsNullOrEmpty(transformAddr) || transformAddr == "0x0000000000000000")
            return Array.Empty<GameObjectRef>();

        // 2. Transform.childCount.
        var countR = await _backend.CallMethodAsync(
            UnityCoreModuleAssembly, UnityEngineNamespace, "Transform", "get_childCount",
            new List<CallArgRequest>(),
            signatureMatch: "()",
            instanceAddress: transformAddr,
            mainThread: true,
            ct: ct).ConfigureAwait(false);
        if (!countR.Ok || countR.ReturnValue.ValueKind != JsonValueKind.Number)
            return Array.Empty<GameObjectRef>();
        int childCount = countR.ReturnValue.GetInt32();
        if (childCount <= 0) return Array.Empty<GameObjectRef>();

        // 3. Loop GetChild(i) → child Transform → its GameObject → name.
        var children = new List<GameObjectRef>(childCount);
        for (int i = 0; i < childCount; ++i)
        {
            ct.ThrowIfCancellationRequested();

            var childTrR = await _backend.CallMethodAsync(
                UnityCoreModuleAssembly, UnityEngineNamespace, "Transform", "GetChild",
                new[] { CallArgFactory.Int("int32", i) },
                signatureMatch: "(System.Int32)",
                instanceAddress: transformAddr,
                mainThread: true,
                ct: ct).ConfigureAwait(false);
            if (!childTrR.Ok || childTrR.ReturnValue.ValueKind != JsonValueKind.String) continue;
            string? childTrAddr = childTrR.ReturnValue.GetString();
            if (string.IsNullOrEmpty(childTrAddr) || childTrAddr == "0x0000000000000000") continue;

            // Component.gameObject (the inverse of GameObject.transform).
            var childGoR = await _backend.CallMethodAsync(
                UnityCoreModuleAssembly, UnityEngineNamespace, "Component", "get_gameObject",
                new List<CallArgRequest>(),
                signatureMatch: "()",
                instanceAddress: childTrAddr,
                mainThread: true,
                ct: ct).ConfigureAwait(false);
            if (!childGoR.Ok || childGoR.ReturnValue.ValueKind != JsonValueKind.String) continue;
            string? childGoAddr = childGoR.ReturnValue.GetString();
            if (string.IsNullOrEmpty(childGoAddr) || childGoAddr == "0x0000000000000000") continue;

            string name = await GetNameAsync(childGoAddr, ct).ConfigureAwait(false) ?? string.Empty;
            children.Add(new GameObjectRef(childGoAddr, name, childCount: null));
        }
        return children;
    }

    /// <summary>
    /// Resolve and cache the managed <c>Type</c> instance pointer for
    /// <c>UnityEngine.GameObject</c>. First call goes over the wire to
    /// <c>System.Type.GetType(string)</c>; subsequent calls return the
    /// cached address (Type instances are GC-rooted for the process
    /// lifetime once created).
    /// </summary>
    private async Task<string?> ResolveGameObjectTypeAsync(CancellationToken ct)
    {
        if (_gameObjectTypeAddress is not null) return _gameObjectTypeAddress;

        var r = await _backend.CallMethodAsync(
            MscorlibAssembly, SystemNamespace, "Type", "GetType",
            new[] { CallArgFactory.String(GameObjectTypeFqn) },
            signatureMatch: "(System.String)",
            mainThread: true,
            ct: ct).ConfigureAwait(false);
        if (!r.Ok || r.ReturnValue.ValueKind != JsonValueKind.String) return null;
        string? addr = r.ReturnValue.GetString();
        if (string.IsNullOrEmpty(addr) || addr == "0x0000000000000000") return null;
        _gameObjectTypeAddress = addr;
        return addr;
    }
}

/// <summary>One row in a Scene Explorer enumeration.</summary>
public sealed class GameObjectRef
{
    public GameObjectRef(string address, string name, int? childCount)
    {
        Address    = address;
        Name       = name;
        ChildCount = childCount;
    }

    public string Address    { get; }
    public string Name       { get; }
    /// <summary>Number of direct children. Null when not yet probed
    /// (the cheap enumeration path doesn't decode this).</summary>
    public int?   ChildCount { get; }
}

/// <summary>Result envelope from
/// <see cref="SceneEnumerator.EnumerateGameObjectAddressesAsync"/>.
/// Either <see cref="Addresses"/> is populated or <see cref="Error"/> is.</summary>
public sealed class EnumerateResult
{
    public IReadOnlyList<string> Addresses { get; }
    public int                   TotalInScene { get; }
    public string                UsedApiNote { get; }
    public string?               Error { get; }

    private EnumerateResult(IReadOnlyList<string> addrs, int total, string note, string? error)
    {
        Addresses     = addrs;
        TotalInScene  = total;
        UsedApiNote   = note;
        Error         = error;
    }

    public bool Ok => Error is null;

    internal static EnumerateResult Success(IReadOnlyList<string> addrs, int total, string note)
        => new(addrs, total, note, null);

    internal static EnumerateResult Failure(string error)
        => new(Array.Empty<string>(), 0, string.Empty, error);
}
