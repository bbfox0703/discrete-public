// BulkOperationGateTests.cs — the cross-VM bulk mutual-exclusion lane.

using System.Collections.Generic;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class BulkOperationGateTests
{
    [Fact]
    public void Idle_ByDefault()
    {
        var gate = new BulkOperationGate();
        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);
    }

    [Fact]
    public void TryEnter_WhenIdle_ClaimsLane_AndFiresPropertyChanged()
    {
        var gate = new BulkOperationGate();
        var changed = new List<string?>();
        gate.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var lease = gate.TryEnter("Snapshot");

        Assert.NotNull(lease);
        Assert.True(gate.IsBusy);
        Assert.Equal("Snapshot", gate.CurrentOperation);
        Assert.Contains(nameof(BulkOperationGate.IsBusy), changed);
        Assert.Contains(nameof(BulkOperationGate.CurrentOperation), changed);
    }

    [Fact]
    public void TryEnter_WhenBusy_ReturnsNull_AndLaneUnchanged()
    {
        var gate = new BulkOperationGate();
        using var first = gate.TryEnter("Snapshot");

        var second = gate.TryEnter("Value search");

        Assert.Null(second);
        // First op still owns the lane.
        Assert.True(gate.IsBusy);
        Assert.Equal("Snapshot", gate.CurrentOperation);
    }

    [Fact]
    public void DisposingLease_ReleasesLane()
    {
        var gate = new BulkOperationGate();
        var lease = gate.TryEnter("Snapshot");
        lease!.Dispose();

        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);

        // Re-enter works after release.
        var again = gate.TryEnter("Value search");
        Assert.NotNull(again);
        Assert.Equal("Value search", gate.CurrentOperation);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var gate = new BulkOperationGate();
        var lease = gate.TryEnter("Snapshot");
        lease!.Dispose();
        lease!.Dispose(); // second dispose must not throw or re-touch the lane

        // A new op holds the lane; the stale lease's 2nd dispose mustn't clear it.
        var other = gate.TryEnter("Value search");
        Assert.NotNull(other);
        lease!.Dispose(); // stale dispose again
        Assert.True(gate.IsBusy);
        Assert.Equal("Value search", gate.CurrentOperation);
    }
}
