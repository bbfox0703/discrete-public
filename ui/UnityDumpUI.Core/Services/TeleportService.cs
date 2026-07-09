// TeleportService.cs — Task 3 (Teleport wave 1). The universal Unity
// "teleport primitive": read / write a Transform's world position + rotation
// via callMethod value-struct marshaling on the Unity main thread.
//
// Unity has NO engine-level player chain (unlike UE's GWorld -> ... -> Pawn),
// so WHICH object is the player is picked by the user upstream (findInstances /
// Scene explorer / Inspector handoff). Given that target's managed pointer,
// this service resolves its UnityEngine.Transform and drives position/rotation
// through the stable engine property accessors, identical on Mono and IL2CPP:
//   Component.get_transform      -> Transform*        (reference return)
//   Transform.get_position       -> Vector3 (12B)     (returnStruct)
//   Transform.get_eulerAngles    -> Vector3 (12B)     (returnStruct)
//   Transform.set_position(Vector3)                   (value-struct arg)
//   Transform.set_eulerAngles(Vector3)
// Every call runs main_thread:true — Transform APIs touch the scene graph and
// deadlock / misbehave off the Unity main thread (Phase 20-MT dispatcher).
//
// Transform position lives in the NATIVE C++ transform, not managed fields, so
// this MUST go through the property setter — a managed-field write wouldn't
// move the object. See the project_ue5_teleport_port_assessment assessment.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// World pose (position + Euler rotation) of a Unity Transform, read via the
/// engine property accessors. Values are Unity floats widened to double for
/// display / marker storage (double represents every float exactly).
/// </summary>
public sealed class TeleportPose
{
    public bool    Ok    { get; init; }
    public string? Error { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    /// <summary>True when eulerAngles were read (position can succeed alone).</summary>
    public bool   HasRotation { get; init; }
    public double EulerX { get; init; }
    public double EulerY { get; init; }
    public double EulerZ { get; init; }
}

/// <summary>How the target character is moved — decides which write path a
/// teleport must use. <c>Transform</c> (None) writes position directly;
/// physics/controller drivers re-drive the transform every frame and need a
/// driver-aware write. This is the first member of that family (the most
/// common); Rigidbody / NavMeshAgent are future additions to the same
/// dispatch.</summary>
public enum MovementDriverKind
{
    /// <summary>Plain transform write works (no component re-drives it).</summary>
    Transform = 0,
    /// <summary>UnityEngine.CharacterController — overrides transform writes
    /// while enabled, so the write is bracketed by disable/enable.</summary>
    CharacterController = 1,
}

/// <summary>The resolved movement driver for a teleport target.</summary>
public sealed class MovementDriver
{
    public MovementDriverKind Kind { get; init; } = MovementDriverKind.Transform;

    /// <summary>The driver component to disable during a write (the
    /// CharacterController). Null for <see cref="MovementDriverKind.Transform"/>.</summary>
    public string? ComponentAddress { get; init; }

    public static readonly MovementDriver Transform = new();

    public bool IsCharacterController =>
        Kind == MovementDriverKind.CharacterController
        && !TeleportService.IsNullAddress(ComponentAddress);   // rejects "" / "0x0" / full-width zero

    /// <summary>Short human label for the UI ("CharacterController" / "Transform").</summary>
    public string Label => Kind switch
    {
        MovementDriverKind.CharacterController => "CharacterController",
        _                                      => "Transform",
    };
}

/// <summary>
/// The universal Unity teleport primitive: resolve a picked target's Transform,
/// then read / write its world position + rotation through callMethod
/// value-struct marshaling on the Unity main thread. Both backends
/// (Mono / IL2CPP) are transparent — the same engine method names resolve on
/// each. Driver-aware: a CharacterController-driven character is written with
/// the controller disabled so the write isn't fought every frame.
/// </summary>
public sealed class TeleportService
{
    // UnityEngine engine types live in UnityEngine.CoreModule on modular Unity
    // (2017.2+) and in the monolithic UnityEngine assembly on older builds. Try
    // CoreModule first; fall through to UnityEngine only on class-not-found.
    private static readonly string[] EngineAssemblyCandidates =
        { "UnityEngine.CoreModule", "UnityEngine" };

    // Collider / CharacterController live in PhysicsModule (module-split) or the
    // monolithic UnityEngine assembly (older builds).
    private static readonly string[] PhysicsAssemblyCandidates =
        { "UnityEngine.PhysicsModule", "UnityEngine" };

    private const string MscorlibAssembly = "mscorlib";
    private const string SystemNamespace  = "System";
    private const string EngineNamespace  = "UnityEngine";
    private const int    Vector3Bytes     = 12;   // 3 little-endian floats

    // CharacterController lives in PhysicsModule (5.4+) / monolithic UnityEngine.
    private static readonly string[] CharacterControllerTypeFqns =
    {
        "UnityEngine.CharacterController, UnityEngine.PhysicsModule",
        "UnityEngine.CharacterController, UnityEngine",
    };

    private static readonly IReadOnlyList<CallArgRequest> NoArgs =
        new List<CallArgRequest>();

    private readonly BackendAdapter _adapter;
    private readonly int _mainThreadTimeoutMs;

    // Cached managed Type* for UnityEngine.CharacterController (per process).
    private string? _ccTypeAddress;
    private bool    _ccTypeResolved;

    private readonly FileLogger? _logger;

    public TeleportService(BackendAdapter adapter, int mainThreadTimeoutMs = 8000, FileLogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _mainThreadTimeoutMs = mainThreadTimeoutMs;
        _logger = logger;
    }

    private void Log(string message) => _logger?.Write("Teleport", message);

    /// <summary>
    /// Resolve the UnityEngine.Transform of a picked target. Pass
    /// <paramref name="targetIsGameObject"/>=true when the target is a
    /// GameObject (uses GameObject.get_transform); otherwise the target is
    /// treated as a Component / MonoBehaviour / Transform
    /// (Component.get_transform, which every Component inherits and a Transform
    /// returns as itself).
    /// </summary>
    public async Task<(string? transformAddress, string? error)> ResolveTransformAsync(
        string targetAddress, bool targetIsGameObject, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(targetAddress) || targetAddress == "0x0")
            return (null, "no target selected");

        string cls = targetIsGameObject ? "GameObject" : "Component";
        CallMethodResult r = await CallEngineAsync(
            cls, "get_transform", targetAddress, NoArgs,
            wantStruct: false, structSize: 0, sigMatch: null, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(r.Error)) return (null, r.Error);
        if (!r.Ok)                          return (null, r.Exception ?? "get_transform failed");

        string? addr = AsAddress(r);
        if (string.IsNullOrEmpty(addr) || addr == "0x0")
            return (null, "target has no Transform");
        return (addr, null);
    }

    /// <summary>
    /// Detect how the target's character is moved so writes take the right path.
    /// Walks the target + ancestors for the NEAREST
    /// <c>UnityEngine.CharacterController</c> (a picked child bone's controller
    /// lives above it). Retargets to that controller's OWN transform so the write
    /// lands on the object the game positions. NOTE: a character can nest several
    /// CharacterControllers (model / logical-root / higher rig); the *nearest*
    /// one to the picked object is used, so picking the right movement candidate
    /// matters — the ranking promotes the controllable root. When none is found
    /// (or the type is stripped), returns <see cref="MovementDriver.Transform"/>.
    /// </summary>
    public async Task<(MovementDriver driver, string effectiveTransform, string? note)> DetectMovementDriverAsync(
        string transformAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transformAddress) || transformAddress == "0x0")
            return (MovementDriver.Transform, transformAddress ?? string.Empty, null);

        string? ccType = await ResolveCharacterControllerTypeAsync(ct).ConfigureAwait(false);
        if (ccType is null)
            return (MovementDriver.Transform, transformAddress,
                "CharacterController type not loaded — plain Transform write.");

        // Manual walk — GetComponentInParent is stripped on many IL2CPP builds;
        // GetComponent + get_parent both survive stripping. Returns the nearest CC.
        (string? cc, int depth) = await FindNearestControllerAsync(transformAddress, ccType, ct)
            .ConfigureAwait(false);
        string via = depth == 0 ? "self" : $"ancestor +{depth}";
        if (string.IsNullOrEmpty(cc))
            return (MovementDriver.Transform, transformAddress,
                "No CharacterController on the target or its ancestors — plain Transform write.");

        // The CharacterController's own transform is the authoritative position
        // node — retarget to it (the picked child would just snap back).
        string effective = transformAddress;
        CallMethodResult tr = await CallEngineAsync(
            "Component", "get_transform", cc, NoArgs,
            wantStruct: false, structSize: 0, sigMatch: null, ct).ConfigureAwait(false);
        string? ccTransform = tr.Ok ? AsAddress(tr) : null;
        if (!string.IsNullOrEmpty(ccTransform) && ccTransform != "0x0")
            effective = ccTransform;

        var driver = new MovementDriver
        {
            Kind = MovementDriverKind.CharacterController,
            ComponentAddress = cc,
        };
        return (driver, effective,
            $"CharacterController-driven (found via {via}) — teleport disables it during the write.");
    }

    /// <summary>Read a Transform's world position + Euler rotation.</summary>
    public async Task<TeleportPose> ReadPoseAsync(
        string transformAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transformAddress) || transformAddress == "0x0")
            return new TeleportPose { Ok = false, Error = "no Transform resolved" };

        CallMethodResult pos = await CallEngineAsync(
            "Transform", "get_position", transformAddress, NoArgs,
            wantStruct: true, structSize: Vector3Bytes, sigMatch: null, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(pos.Error)) return new TeleportPose { Ok = false, Error = pos.Error };
        if (!pos.Ok)                          return new TeleportPose { Ok = false, Error = pos.Exception ?? "get_position failed" };
        float[] p = CallArgFactory.DecodeStructFloats(pos.ReturnStructBytes, 3);

        // Rotation is best-effort: a failed eulerAngles read still yields a
        // usable position-only pose.
        double ex = 0, ey = 0, ez = 0;
        bool hasRot = false;
        CallMethodResult rot = await CallEngineAsync(
            "Transform", "get_eulerAngles", transformAddress, NoArgs,
            wantStruct: true, structSize: Vector3Bytes, sigMatch: null, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(rot.Error) && rot.Ok)
        {
            float[] e = CallArgFactory.DecodeStructFloats(rot.ReturnStructBytes, 3);
            ex = e[0]; ey = e[1]; ez = e[2];
            hasRot = true;
        }

        return new TeleportPose
        {
            Ok = true,
            X = p[0], Y = p[1], Z = p[2],
            HasRotation = hasRot, EulerX = ex, EulerY = ey, EulerZ = ez,
        };
    }

    /// <summary>
    /// Write a Transform's world position, and optionally its Euler rotation.
    /// Position is set first; rotation only when <paramref name="euler"/> is
    /// supplied. Returns the first failure, if any (position may already have
    /// moved when a subsequent rotation write fails).
    /// </summary>
    public async Task<(bool ok, string? error)> WritePoseAsync(
        string transformAddress,
        double x, double y, double z,
        (double x, double y, double z)? euler = null,
        MovementDriver? driver = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transformAddress) || transformAddress == "0x0")
            return (false, "no Transform resolved");

        MovementDriver d = driver ?? MovementDriver.Transform;
        if (!d.IsCharacterController)
            return await SetPoseRawAsync(transformAddress, x, y, z, euler, ct).ConfigureAwait(false);

        // CharacterController-aware write: the controller overrides transform
        // writes while enabled, so disable it, write, then re-enable from the new
        // position. Standard Unity teleport idiom.
        //
        // The disable is INSIDE the try and every re-enable runs with
        // CancellationToken.None: restoring control is cleanup that must survive
        // cancellation. Otherwise a cancelled `ct` (the disable RPC can commit on
        // the game then throw a post-read OCE; a re-enable on a cancelled token
        // never reaches the wire) would leave the controller disabled = the
        // character frozen. Re-enabling an already-enabled CC is a harmless no-op.
        string cc = d.ComponentAddress!;

        // Disable is best-effort: if set_enabled can't be resolved (stripped on
        // some IL2CPP builds), we STILL write — a plain set_position may be
        // overridden by the live controller, but silently doing nothing is worse,
        // and the log records what happened. Only when the disable actually took
        // do we re-enable afterwards.
        CallMethodResult disable = await SetCharacterControllerEnabledAsync(cc, false, ct).ConfigureAwait(false);
        bool disabled = disable.Ok && string.IsNullOrEmpty(disable.Error);
        if (!disabled)
            Log($"teleport: CharacterController disable failed ({disable.Error ?? disable.Exception ?? "?"}) "
                + "— writing position anyway (may be overridden by the live controller).");

        (bool ok, string? error) result;
        try
        {
            result = await SetPoseRawAsync(transformAddress, x, y, z, euler, ct).ConfigureAwait(false);
        }
        catch
        {
            // Restore control on any throw (uncancellable — cleanup must survive).
            if (disabled)
                try { await SetCharacterControllerEnabledAsync(cc, true, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }

        string? reEnableWarn = null;
        if (disabled)
        {
            CallMethodResult enable = await SetCharacterControllerEnabledAsync(cc, true, CancellationToken.None).ConfigureAwait(false);
            if (!enable.Ok || !string.IsNullOrEmpty(enable.Error))
                reEnableWarn = "teleported, but FAILED to re-enable the CharacterController "
                    + $"({enable.Error ?? enable.Exception ?? "set_enabled(true) failed"}) — "
                    + "the character may be frozen; toggle it in-game or re-run.";
        }

        Log($"teleport CC-write to {transformAddress}: disabled={disabled}, "
            + $"set_position ok={result.ok} err={result.error ?? "-"}"
            + (reEnableWarn is null ? "" : " | re-enable FAILED"));

        if (reEnableWarn is not null) return (false, reEnableWarn);
        return result;
    }

    // The raw transform write (position + optional Euler), driver-agnostic.
    private async Task<(bool ok, string? error)> SetPoseRawAsync(
        string transformAddress, double x, double y, double z,
        (double x, double y, double z)? euler, CancellationToken ct)
    {
        var posArgs = new List<CallArgRequest>
        {
            CallArgFactory.Vector3((float)x, (float)y, (float)z),
        };
        CallMethodResult setPos = await CallEngineAsync(
            "Transform", "set_position", transformAddress, posArgs,
            wantStruct: false, structSize: 0, sigMatch: "Vector3", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(setPos.Error)) return (false, setPos.Error);
        if (!setPos.Ok)                          return (false, setPos.Exception ?? "set_position failed");

        if (euler is { } e)
        {
            var rotArgs = new List<CallArgRequest>
            {
                CallArgFactory.Vector3((float)e.x, (float)e.y, (float)e.z),
            };
            CallMethodResult setRot = await CallEngineAsync(
                "Transform", "set_eulerAngles", transformAddress, rotArgs,
                wantStruct: false, structSize: 0, sigMatch: "Vector3", ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(setRot.Error)) return (false, setRot.Error);
            if (!setRot.Ok)                          return (false, setRot.Exception ?? "set_eulerAngles failed");
        }

        return (true, null);
    }

    /// <summary>
    /// Teleport along the Transform's facing direction by a signed distance
    /// (negative = backward). <paramref name="horizontal"/> keeps the move on
    /// the ground plane (Unity is Y-up, so the forward Y component is dropped);
    /// 3D mode uses the full forward including pitch (fly / noclip feel).
    /// Rotation is preserved. Reads position + forward, then writes.
    /// </summary>
    public async Task<(bool ok, string? error)> TeleportDirectionalAsync(
        string transformAddress, double distance, bool horizontal,
        MovementDriver? driver = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transformAddress) || transformAddress == "0x0")
            return (false, "no Transform resolved");

        CallMethodResult pos = await CallEngineAsync(
            "Transform", "get_position", transformAddress, NoArgs,
            wantStruct: true, structSize: Vector3Bytes, sigMatch: null, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(pos.Error)) return (false, pos.Error);
        if (!pos.Ok)                          return (false, pos.Exception ?? "get_position failed");
        float[] p = CallArgFactory.DecodeStructFloats(pos.ReturnStructBytes, 3);

        CallMethodResult fwd = await CallEngineAsync(
            "Transform", "get_forward", transformAddress, NoArgs,
            wantStruct: true, structSize: Vector3Bytes, sigMatch: null, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fwd.Error)) return (false, fwd.Error);
        if (!fwd.Ok)                          return (false, fwd.Exception ?? "get_forward failed");
        float[] f = CallArgFactory.DecodeStructFloats(fwd.ReturnStructBytes, 3);

        var (dx, dy, dz) = TeleportMath.Directional(
            p[0], p[1], p[2], f[0], f[1], f[2], distance, horizontal);
        return await WritePoseAsync(transformAddress, dx, dy, dz, euler: null, driver, ct).ConfigureAwait(false);
    }

    // --- driver internals ---

    // Resolve + cache the managed Type* for UnityEngine.CharacterController.
    // Null (cached) when the type isn't loaded — a game with no CharacterController.
    private async Task<string?> ResolveCharacterControllerTypeAsync(CancellationToken ct)
    {
        if (_ccTypeResolved) return _ccTypeAddress;
        // Latch the cache ONLY on a clean (non-throwing) finish. Setting the flag
        // up-front would poison the cache on a transient failure (pipe
        // disconnect / cancellation mid-probe): the flag would stay latched with
        // a null address, permanently disabling CharacterController detection for
        // the life of this service. On a throw we leave it unresolved so the next
        // call re-probes.
        foreach (string fqn in CharacterControllerTypeFqns)
        {
            CallMethodResult r = await _adapter.CallMethodAsync(
                MscorlibAssembly, SystemNamespace, "Type", "GetType",
                new[] { CallArgFactory.String(fqn) },
                signatureMatch: "(System.String)",
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                ct: ct).ConfigureAwait(false);
            string? addr = r.Ok ? AsAddress(r) : null;
            if (!string.IsNullOrEmpty(addr) && addr != "0x0")
            {
                _ccTypeAddress  = addr;
                _ccTypeResolved = true;
                return addr;
            }
        }
        // Both probes ran cleanly and the type genuinely isn't loaded — cache the
        // negative so we don't re-probe every SetTarget.
        _ccTypeAddress  = null;
        _ccTypeResolved = true;
        return null;
    }

    // Component.GetComponent[InParent](System.Type) on a transform — returns the
    // component address, or null when absent / the overload is stripped.
    private async Task<string?> FindComponentAsync(
        string transformAddress, string typeAddress, string method, CancellationToken ct)
    {
        CallMethodResult r = await CallEngineAsync(
            "Component", method, transformAddress,
            new[] { CallArgFactory.Address(typeAddress) },
            wantStruct: false, structSize: 0, sigMatch: "(System.Type)", ct).ConfigureAwait(false);
        if (!r.Ok || !string.IsNullOrEmpty(r.Error)) return null;
        string? addr = AsAddress(r);
        return string.IsNullOrEmpty(addr) || addr == "0x0" ? null : addr;
    }

    // Walk the target's GameObject and each ancestor (GetComponent + get_parent
    // — both survive IL2CPP stripping) and return the NEAREST CharacterController.
    // Returns (component, depth): depth 0 = on the target itself, N = on the Nth
    // ancestor. Stops at the first match (climbing past it to an outer rig CC
    // over-shoots the object the game actually drives — the controllable root is
    // chosen by the movement RANKING, not by climbing the whole chain here).
    private const int MaxAncestorWalk = 16;

    private async Task<(string? cc, int depth)> FindNearestControllerAsync(
        string transformAddress, string typeAddress, CancellationToken ct)
    {
        string cur = transformAddress;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cur };
        for (int depth = 0; depth <= MaxAncestorWalk; depth++)
        {
            string? cc = await FindComponentAsync(cur, typeAddress, "GetComponent", ct).ConfigureAwait(false);
            if (cc is not null) return (cc, depth);

            CallMethodResult pr = await CallEngineAsync(
                "Transform", "get_parent", cur, NoArgs,
                wantStruct: false, structSize: 0, sigMatch: null, ct).ConfigureAwait(false);
            string? parent = pr.Ok ? AsAddress(pr) : null;
            if (string.IsNullOrEmpty(parent) || parent == "0x0" || !seen.Add(parent)) break;
            cur = parent;
        }
        return (null, -1);
    }

    // CharacterController.enabled is declared on Collider (PhysicsModule), but
    // which class name resolves the set_enabled method varies by build/backend,
    // so try the declaring class (Collider) then the runtime class
    // (CharacterController). Fall through only on a resolution failure (class /
    // method not found); a real invoke error is returned as-is.
    private static readonly (string Class, string[] Asms)[] SetEnabledTargets =
    {
        ("Collider",            PhysicsAssemblyCandidates),
        ("CharacterController", PhysicsAssemblyCandidates),
    };

    private async Task<CallMethodResult> SetCharacterControllerEnabledAsync(
        string cc, bool enabled, CancellationToken ct)
    {
        CallMethodResult? last = null;
        foreach ((string cls, string[] asms) in SetEnabledTargets)
        {
            CallMethodResult r = await CallEngineAsync(
                cls, "set_enabled", cc,
                new[] { CallArgFactory.Bool(enabled) },
                wantStruct: false, structSize: 0, sigMatch: "(System.Boolean)", ct,
                assemblies: asms).ConfigureAwait(false);
            last = r;
            if (r.Ok && string.IsNullOrEmpty(r.Error)) return r;
            if (!IsResolutionFailure(r.Error)) return r;   // real invoke error — surface it
        }
        return last ?? new CallMethodResult { Ok = false, Error = "set_enabled: no class candidate resolved" };
    }

    private static bool IsResolutionFailure(string? error) =>
        !string.IsNullOrEmpty(error)
        && (IsClassNotFound(error)
            || error.Contains("method_not_found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("method not found", StringComparison.OrdinalIgnoreCase));

    // --- internals ---

    // Invoke an engine instance method, trying each candidate engine assembly
    // until one resolves the class. All engine calls run on the Unity main
    // thread (Transform APIs are main-thread-only).
    private async Task<CallMethodResult> CallEngineAsync(
        string className, string method, string? instanceHex,
        IReadOnlyList<CallArgRequest> args, bool wantStruct, int structSize,
        string? sigMatch, CancellationToken ct, string[]? assemblies = null)
    {
        CallMethodResult? last = null;
        foreach (string asm in assemblies ?? EngineAssemblyCandidates)
        {
            CallMethodResult r = await _adapter.CallMethodAsync(
                asm, EngineNamespace, className, method, args,
                signatureMatch: sigMatch, instanceAddress: instanceHex,
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                returnStruct: wantStruct, returnStructSize: structSize,
                ct: ct).ConfigureAwait(false);
            last = r;
            // Only fall through to the next candidate assembly on class-not-found;
            // a real invoke error / exception is returned as-is.
            if (string.IsNullOrEmpty(r.Error) || !IsClassNotFound(r.Error))
                return r;
        }
        return last!;
    }

    private static bool IsClassNotFound(string error) =>
        error.Contains("class not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("class_not_found", StringComparison.OrdinalIgnoreCase);

    // A managed reference return. Any all-zero pointer — short "0x0" OR the
    // full-width "0x0000000000000000" the DLL emits for a managed null (e.g.
    // GetComponent finding nothing) — is normalized to null. Missing this was a
    // real bug: FindComponent treated the full-width zero as a FOUND component,
    // so driver detection stopped at a phantom CharacterController (instance 0),
    // set_enabled failed with instance_required_for_non_static, AND the walk
    // never climbed to the real CC on an ancestor rig — the character's visual
    // node moved but the logical/camera root didn't.
    private static string? AsAddress(CallMethodResult r)
    {
        string? addr = r.ReturnValue.ValueKind == JsonValueKind.String
            ? r.ReturnValue.GetString() : null;
        return IsNullAddress(addr) ? null : addr;
    }

    /// <summary>True for a missing or all-zero address ("0x0",
    /// "0x0000000000000000", "0x", ""). Matches PlayerLocator's null handling so
    /// the two services agree on "no object".</summary>
    internal static bool IsNullAddress(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return true;
        int i = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (i >= hex.Length) return true;   // "0x" with no digits
        for (; i < hex.Length; i++)
            if (hex[i] != '0') return false;
        return true;
    }
}
