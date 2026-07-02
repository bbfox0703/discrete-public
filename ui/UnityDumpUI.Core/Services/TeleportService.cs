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

/// <summary>
/// The universal Unity teleport primitive: resolve a picked target's Transform,
/// then read / write its world position + rotation through callMethod
/// value-struct marshaling on the Unity main thread. Both backends
/// (Mono / IL2CPP) are transparent — the same engine method names resolve on
/// each.
/// </summary>
public sealed class TeleportService
{
    // UnityEngine engine types live in UnityEngine.CoreModule on modular Unity
    // (2017.2+) and in the monolithic UnityEngine assembly on older builds. Try
    // CoreModule first; fall through to UnityEngine only on class-not-found.
    private static readonly string[] EngineAssemblyCandidates =
        { "UnityEngine.CoreModule", "UnityEngine" };

    private const string EngineNamespace = "UnityEngine";
    private const int    Vector3Bytes    = 12;   // 3 little-endian floats

    private static readonly IReadOnlyList<CallArgRequest> NoArgs =
        new List<CallArgRequest>();

    private readonly BackendAdapter _adapter;
    private readonly int _mainThreadTimeoutMs;

    public TeleportService(BackendAdapter adapter, int mainThreadTimeoutMs = 8000)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _mainThreadTimeoutMs = mainThreadTimeoutMs;
    }

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
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transformAddress) || transformAddress == "0x0")
            return (false, "no Transform resolved");

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
        string transformAddress, double distance, bool horizontal, CancellationToken ct = default)
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
        return await WritePoseAsync(transformAddress, dx, dy, dz, euler: null, ct).ConfigureAwait(false);
    }

    // --- internals ---

    // Invoke an engine instance method, trying each candidate engine assembly
    // until one resolves the class. All engine calls run on the Unity main
    // thread (Transform APIs are main-thread-only).
    private async Task<CallMethodResult> CallEngineAsync(
        string className, string method, string? instanceHex,
        IReadOnlyList<CallArgRequest> args, bool wantStruct, int structSize,
        string? sigMatch, CancellationToken ct)
    {
        CallMethodResult? last = null;
        foreach (string asm in EngineAssemblyCandidates)
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

    private static string? AsAddress(CallMethodResult r) =>
        r.ReturnValue.ValueKind == JsonValueKind.String ? r.ReturnValue.GetString() : null;
}
