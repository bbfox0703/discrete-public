// TeleportMarkersTests.cs — Task 4. Pure (no-I/O) tests for the marker store
// and directional geometry.

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class TeleportMarkersTests
{
    [Fact]
    public void MarkerStore_SaveGetClear_RoundTrips()
    {
        var store = new TeleportMarkerStore();
        Assert.False(store.Get(0).Valid);

        var m = TeleportMarker.From(new TeleportPose
        {
            Ok = true, X = 1, Y = 2, Z = 3,
            HasRotation = true, EulerX = 10, EulerY = 20, EulerZ = 30,
        });
        store.Save(0, m);

        TeleportMarker got = store.Get(0);
        Assert.True(got.Valid);
        Assert.Equal(2.0, got.Y);
        Assert.True(got.HasRotation);
        Assert.Equal(20.0, got.EulerY);

        store.Clear(0);
        Assert.False(store.Get(0).Valid);
    }

    [Fact]
    public void MarkerStore_OutOfRange_IsSafe()
    {
        var store = new TeleportMarkerStore();
        Assert.False(store.Get(-1).Valid);
        Assert.False(store.Get(TeleportMarkerStore.SlotCount).Valid);
        store.Save(99, TeleportMarker.From(new TeleportPose { Ok = true, X = 5 }));  // no throw / no-op
        store.Clear(-3);                                                            // no throw
    }

    [Fact]
    public void MarkerStore_LastSlot_TracksSeparately()
    {
        var store = new TeleportMarkerStore();
        Assert.False(store.Last.Valid);
        store.SetLast(TeleportMarker.From(new TeleportPose { Ok = true, X = 7, Y = 8, Z = 9 }));
        Assert.True(store.Last.Valid);
        Assert.Equal(8.0, store.Last.Y);
        Assert.False(store.Get(0).Valid);   // user slots untouched
    }

    [Fact]
    public void Directional_3D_MovesAlongForward()
    {
        var (x, y, z) = TeleportMath.Directional(0, 0, 0, 0, 0, 1, 5.0, horizontal: false);
        Assert.Equal(0.0, x, 5);
        Assert.Equal(0.0, y, 5);
        Assert.Equal(5.0, z, 5);
    }

    [Fact]
    public void Directional_Horizontal_DropsYComponent()
    {
        // Forward tilted up (Y=0.8) but XZ points +X — horizontal move stays level.
        var (x, y, z) = TeleportMath.Directional(0, 0, 0, 0.6, 0.8, 0, 5.0, horizontal: true);
        Assert.Equal(5.0, x, 5);
        Assert.Equal(0.0, y, 5);
        Assert.Equal(0.0, z, 5);
    }

    [Fact]
    public void Directional_Horizontal_StraightUp_DoesNotMove()
    {
        var (x, y, z) = TeleportMath.Directional(1, 2, 3, 0, 1, 0, 5.0, horizontal: true);
        Assert.Equal(1.0, x, 5);
        Assert.Equal(2.0, y, 5);
        Assert.Equal(3.0, z, 5);
    }

    [Fact]
    public void Directional_ZeroForward_DoesNotMove()
    {
        var (x, y, z) = TeleportMath.Directional(1, 2, 3, 0, 0, 0, 5.0, horizontal: false);
        Assert.Equal(1.0, x, 5);
        Assert.Equal(2.0, y, 5);
        Assert.Equal(3.0, z, 5);
    }
}
