// TeleportMarkers.cs — Task 4 (Teleport wave 1). Pure app-side teleport state
// and geometry: marker slots + one-way "last" undo slot, and the directional
// move math. No I/O — unit-tested without a backend. The stateful action layer
// that combines this with TeleportService lives in TeleportController.

using System;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// One stored teleport marker: world position + optional Euler rotation.
/// <c>default</c> (Valid = false) is the empty slot.
/// </summary>
public readonly record struct TeleportMarker(
    bool Valid,
    double X, double Y, double Z,
    bool HasRotation,
    double EulerX, double EulerY, double EulerZ)
{
    /// <summary>Capture a live pose into a valid marker.</summary>
    public static TeleportMarker From(TeleportPose p) =>
        new(true, p.X, p.Y, p.Z, p.HasRotation, p.EulerX, p.EulerY, p.EulerZ);
}

/// <summary>
/// In-memory teleport marker state: <see cref="SlotCount"/> user slots plus a
/// system "last" slot auto-captured before each jump for one-way undo. Pure
/// state — no I/O — so it unit-tests without a backend. Markers store
/// coordinates + rotation only; the scene-name mismatch guard (UE's map check)
/// is deferred to v2.
/// </summary>
public sealed class TeleportMarkerStore
{
    public const int SlotCount = 3;

    private readonly TeleportMarker[] _slots = new TeleportMarker[SlotCount];
    private TeleportMarker _last;

    public TeleportMarker Get(int slot) =>
        (uint)slot < SlotCount ? _slots[slot] : default;

    public void Save(int slot, TeleportMarker marker)
    {
        if ((uint)slot < SlotCount) _slots[slot] = marker;
    }

    public void Clear(int slot)
    {
        if ((uint)slot < SlotCount) _slots[slot] = default;
    }

    /// <summary>The pre-jump pose auto-saved before recall / coords / directional
    /// (the one-way undo target). Invalid until the first jump.</summary>
    public TeleportMarker Last => _last;

    public void SetLast(TeleportMarker marker) => _last = marker;
}

/// <summary>Pure teleport geometry (no I/O). Unity is Y-up.</summary>
public static class TeleportMath
{
    /// <summary>
    /// Destination when moving from (px,py,pz) along a facing vector
    /// (fx,fy,fz) by a signed distance. <paramref name="horizontal"/> keeps the
    /// move on the ground plane (moves along the XZ facing, Y unchanged); if
    /// there is no horizontal facing (looking straight up/down) the position is
    /// returned unchanged. 3D mode uses the full normalized forward. A
    /// zero-length forward yields no movement.
    /// </summary>
    public static (double x, double y, double z) Directional(
        double px, double py, double pz,
        double fx, double fy, double fz,
        double distance, bool horizontal)
    {
        double dx, dy, dz;
        if (horizontal)
        {
            double lenXZ = Math.Sqrt((fx * fx) + (fz * fz));
            if (lenXZ <= 1e-6) return (px, py, pz);   // no ground-plane facing
            dx = fx / lenXZ; dy = 0.0; dz = fz / lenXZ;
        }
        else
        {
            double len = Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
            if (len <= 1e-6) return (px, py, pz);
            dx = fx / len; dy = fy / len; dz = fz / len;
        }
        return (px + (dx * distance), py + (dy * distance), pz + (dz * distance));
    }
}
