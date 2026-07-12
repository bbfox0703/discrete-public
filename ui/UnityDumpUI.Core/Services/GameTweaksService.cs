// GameTweaksService.cs — universal world tweaks reachable through the SAME
// callMethod primitive the Teleport feature uses. Both knobs are STATIC engine
// setters (no per-game instance, no per-game reversing) so they are universal:
//
//   UnityEngine.Time.set_timeScale(System.Single)      — game speed / slow-mo
//   UnityEngine.Time.get_timeScale() -> System.Single
//   UnityEngine.Physics.set_gravity(UnityEngine.Vector3) — low-gravity / float
//   UnityEngine.Physics.get_gravity() -> UnityEngine.Vector3
//
// Unlike UE5CEDumper's per-pawn CharacterMovementComponent knobs (which are
// game-specific in Unity — no universal movement component), these two are
// engine globals with a fixed managed signature on every Unity game, so a
// single callMethod on the static class works with zero per-game config.
//
// All calls run on the Unity main thread (engine APIs are main-thread-only) and
// use the same CoreModule/PhysicsModule -> monolithic-UnityEngine assembly
// fallback as TeleportService.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Universal speed (Time.timeScale) + gravity (Physics.gravity) tweaks via the
/// static engine setters. Stateless — the caller owns any captured baseline.
/// </summary>
public sealed class GameTweaksService
{
    // Time lives in UnityEngine.CoreModule (modular Unity) / monolithic UnityEngine.
    private static readonly string[] EngineAssemblyCandidates =
        { "UnityEngine.CoreModule", "UnityEngine" };

    // Physics lives in UnityEngine.PhysicsModule (module-split) / monolithic UnityEngine.
    private static readonly string[] PhysicsAssemblyCandidates =
        { "UnityEngine.PhysicsModule", "UnityEngine" };

    private const string EngineNamespace = "UnityEngine";
    private const int    Vector3Bytes    = 12;   // 3 little-endian floats

    private static readonly IReadOnlyList<CallArgRequest> NoArgs = new List<CallArgRequest>();

    private readonly BackendAdapter _adapter;
    private readonly int _mainThreadTimeoutMs;
    private readonly FileLogger? _logger;

    public GameTweaksService(BackendAdapter adapter, int mainThreadTimeoutMs = 8000, FileLogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _mainThreadTimeoutMs = mainThreadTimeoutMs;
        _logger = logger;
    }

    private void Log(string message) => _logger?.Write("GameTweaks", message);

    // --- Time.timeScale (game speed / slow-mo; 1 = normal, 0 = paused) ---------

    /// <summary>Set <c>Time.timeScale</c>. 1 = normal, 0 = paused, &gt;1 = faster.</summary>
    public async Task<(bool Ok, string? Error)> SetTimeScaleAsync(float scale, CancellationToken ct = default)
    {
        CallMethodResult r = await CallEngineAsync(
            "Time", "set_timeScale", new[] { CallArgFactory.Float("float", scale) },
            wantStruct: false, structSize: 0, sigMatch: "(System.Single)",
            assemblies: EngineAssemblyCandidates, ct).ConfigureAwait(false);
        Log($"set_timeScale({scale}) ok={r.Ok} err={r.Error ?? r.Exception ?? "-"}");
        return (r.Ok && string.IsNullOrEmpty(r.Error), r.Error ?? r.Exception);
    }

    /// <summary>Read <c>Time.timeScale</c>, or null if the call didn't answer.</summary>
    public async Task<float?> GetTimeScaleAsync(CancellationToken ct = default)
    {
        CallMethodResult r = await CallEngineAsync(
            "Time", "get_timeScale", NoArgs, wantStruct: false, structSize: 0,
            sigMatch: null, assemblies: EngineAssemblyCandidates, ct).ConfigureAwait(false);
        if (!r.Ok || !string.IsNullOrEmpty(r.Error)) return null;
        return r.ReturnValue.ValueKind == JsonValueKind.Number ? r.ReturnValue.GetSingle() : null;
    }

    // --- Physics.gravity (low-gravity / float / heavy) -------------------------

    /// <summary>Set <c>Physics.gravity</c> to the given world vector.</summary>
    public async Task<(bool Ok, string? Error)> SetGravityAsync(float x, float y, float z, CancellationToken ct = default)
    {
        CallMethodResult r = await CallEngineAsync(
            "Physics", "set_gravity", new[] { CallArgFactory.Vector3(x, y, z) },
            wantStruct: false, structSize: 0, sigMatch: "(UnityEngine.Vector3)",
            assemblies: PhysicsAssemblyCandidates, ct).ConfigureAwait(false);
        Log($"set_gravity({x},{y},{z}) ok={r.Ok} err={r.Error ?? r.Exception ?? "-"}");
        return (r.Ok && string.IsNullOrEmpty(r.Error), r.Error ?? r.Exception);
    }

    /// <summary>Read <c>Physics.gravity</c>, or null if the call didn't answer.</summary>
    public async Task<(float X, float Y, float Z)?> GetGravityAsync(CancellationToken ct = default)
    {
        CallMethodResult r = await CallEngineAsync(
            "Physics", "get_gravity", NoArgs, wantStruct: true, structSize: Vector3Bytes,
            sigMatch: null, assemblies: PhysicsAssemblyCandidates, ct).ConfigureAwait(false);
        if (!r.Ok || !string.IsNullOrEmpty(r.Error)) return null;
        float[] g = CallArgFactory.DecodeStructFloats(r.ReturnStructBytes, 3);
        return (g[0], g[1], g[2]);
    }

    // --- internals -------------------------------------------------------------

    // Invoke a STATIC engine method (no instance), trying each candidate engine
    // assembly until one resolves the class. Mirrors TeleportService.CallEngineAsync.
    private async Task<CallMethodResult> CallEngineAsync(
        string className, string method, IReadOnlyList<CallArgRequest> args,
        bool wantStruct, int structSize, string? sigMatch, string[] assemblies,
        CancellationToken ct)
    {
        CallMethodResult? last = null;
        foreach (string asm in assemblies)
        {
            CallMethodResult r = await _adapter.CallMethodAsync(
                asm, EngineNamespace, className, method, args,
                signatureMatch: sigMatch, instanceAddress: null,
                mainThread: true, mainThreadTimeoutMs: _mainThreadTimeoutMs,
                returnStruct: wantStruct, returnStructSize: structSize,
                ct: ct).ConfigureAwait(false);
            last = r;
            if (string.IsNullOrEmpty(r.Error) || !IsClassNotFound(r.Error))
                return r;
        }
        return last!;
    }

    private static bool IsClassNotFound(string error) =>
        error.Contains("class not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("class_not_found", StringComparison.OrdinalIgnoreCase);
}
