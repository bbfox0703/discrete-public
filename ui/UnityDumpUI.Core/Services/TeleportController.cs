// TeleportController.cs — Task 4 (Teleport wave 1). Stateful action layer: owns
// a marker store + a resolved-target Transform and turns UI intents (save /
// recall / undo / coords / directional) into TeleportService calls. Every jump
// (recall / coords / directional) first auto-saves the current pose into the
// one-way "last" slot so a bad teleport is undoable; RecallLast does NOT
// re-save, so repeated undo always returns to the same pre-jump spot.
//
// The resolved Transform pointer is cached (Unity has no stable re-resolution
// chain — the user-picked target IS the anchor). If the target moves in memory
// or despawns, re-run SetTargetAsync to re-resolve. The TeleportViewModel
// (Task 6) is a thin Avalonia wrapper over this controller.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Coordinates a <see cref="TeleportMarkerStore"/> and a resolved-target
/// Transform against a <see cref="TeleportService"/>. All action methods return
/// null on success or a human-readable error string.
/// </summary>
public sealed class TeleportController
{
    private readonly TeleportService _service;
    private readonly TeleportMarkerStore _markers = new();
    private string? _transform;

    public TeleportController(TeleportService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public TeleportMarkerStore Markers => _markers;

    public bool HasTarget => !string.IsNullOrEmpty(_transform);

    /// <summary>Resolve + cache the picked target's Transform. Returns null on
    /// success, or an error string (target cleared on failure).</summary>
    public async Task<string?> SetTargetAsync(
        string targetAddress, bool targetIsGameObject, CancellationToken ct = default)
    {
        (string? transform, string? error) = await _service
            .ResolveTransformAsync(targetAddress, targetIsGameObject, ct).ConfigureAwait(false);
        _transform = transform;
        return error;
    }

    public void ClearTarget() => _transform = null;

    /// <summary>Read the live pose of the resolved target.</summary>
    public Task<TeleportPose> ReadPoseAsync(CancellationToken ct = default) =>
        _transform is null
            ? Task.FromResult(new TeleportPose { Ok = false, Error = "no target resolved" })
            : _service.ReadPoseAsync(_transform, ct);

    /// <summary>Save the current pose into a marker slot.</summary>
    public async Task<string?> SaveMarkerAsync(int slot, CancellationToken ct = default)
    {
        if (_transform is null) return "no target resolved";
        TeleportPose pose = await _service.ReadPoseAsync(_transform, ct).ConfigureAwait(false);
        if (!pose.Ok) return pose.Error ?? "read failed";
        _markers.Save(slot, TeleportMarker.From(pose));
        return null;
    }

    /// <summary>Recall a marker slot (auto-saves the pre-jump pose for undo).</summary>
    public async Task<string?> RecallMarkerAsync(int slot, CancellationToken ct = default)
    {
        if (_transform is null) return "no target resolved";
        TeleportMarker m = _markers.Get(slot);
        if (!m.Valid) return "marker slot is empty";
        await SaveLastAsync(ct).ConfigureAwait(false);
        return await WriteMarkerAsync(m, ct).ConfigureAwait(false);
    }

    /// <summary>One-way undo: return to the pre-jump pose. Never overwrites the
    /// "last" slot, so repeated undo lands on the same spot.</summary>
    public async Task<string?> RecallLastAsync(CancellationToken ct = default)
    {
        if (_transform is null) return "no target resolved";
        TeleportMarker m = _markers.Last;
        if (!m.Valid) return "nothing to undo";
        return await WriteMarkerAsync(m, ct).ConfigureAwait(false);
    }

    /// <summary>Force-teleport to explicit world coordinates (auto-saves undo).</summary>
    public async Task<string?> TeleportToCoordsAsync(
        double x, double y, double z, (double x, double y, double z)? euler = null,
        CancellationToken ct = default)
    {
        if (_transform is null) return "no target resolved";
        await SaveLastAsync(ct).ConfigureAwait(false);
        (bool ok, string? error) = await _service
            .WritePoseAsync(_transform, x, y, z, euler, ct).ConfigureAwait(false);
        return ok ? null : error;
    }

    /// <summary>Teleport along the facing direction (auto-saves undo).</summary>
    public async Task<string?> TeleportDirectionalAsync(
        double distance, bool horizontal, CancellationToken ct = default)
    {
        if (_transform is null) return "no target resolved";
        await SaveLastAsync(ct).ConfigureAwait(false);
        (bool ok, string? error) = await _service
            .TeleportDirectionalAsync(_transform, distance, horizontal, ct).ConfigureAwait(false);
        return ok ? null : error;
    }

    public void ClearMarker(int slot) => _markers.Clear(slot);

    private async Task SaveLastAsync(CancellationToken ct)
    {
        TeleportPose pose = await _service.ReadPoseAsync(_transform!, ct).ConfigureAwait(false);
        if (pose.Ok) _markers.SetLast(TeleportMarker.From(pose));
    }

    private async Task<string?> WriteMarkerAsync(TeleportMarker m, CancellationToken ct)
    {
        (double x, double y, double z)? euler =
            m.HasRotation ? (m.EulerX, m.EulerY, m.EulerZ) : null;
        (bool ok, string? error) = await _service
            .WritePoseAsync(_transform!, m.X, m.Y, m.Z, euler, ct).ConfigureAwait(false);
        return ok ? null : error;
    }
}
