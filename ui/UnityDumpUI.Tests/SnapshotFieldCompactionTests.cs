// SnapshotFieldCompactionTests — object-footprint compaction of SnapshotFieldEntry
// / SnapshotArrayElement.
//
// The rarely-set members moved into a lazily-allocated side-object (FieldExtras /
// ElementExtras) and the two tri-state bools (is_enum / is_value_type) packed into
// a flags byte. The public property API + wire keys are unchanged, so these tests
// lock in: (1) JSON round-trip preserves every moved key for each field-kind,
// (2) the forwarding accessors get/set correctly incl. null-clear, (3) the
// tri-state packing is lossless, (4) a plain scalar field never allocates extras.

using System.Collections.Generic;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotFieldCompactionTests
{
    private static SnapshotClassEntry RoundTrip(SnapshotClassEntry c)
    {
        var json = JsonSerializer.Serialize(c, ProtocolJsonContext.Default.SnapshotClassEntry);
        return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SnapshotClassEntry)!;
    }

    private static SnapshotClassEntry WrapField(SnapshotFieldEntry f) => new()
    {
        Assembly = "a", Name = "C",
        Instances = new List<SnapshotInstanceEntry>
        {
            new() { Address = "0x1", Fields = { f } },
        },
    };

    [Fact]
    public void AllFieldKinds_RoundTripPreservesEveryMovedKey()
    {
        var f = new SnapshotFieldEntry
        {
            Name = "f", Type = "T", Offset = 64, From = "Base",
            IsEnum = true, IsValueType = true,
            Ptr = "0xdead", StringValue = "hello", StringLength = 5, StringError = "trunc",
            Error = "oops", ArrayError = "bad", ArrayErrorLength = 9999999999UL,
            Address = "0xfeed",
            Fields = new List<SnapshotFieldEntry>
            {
                new() { Name = "x", Type = "System.Single", ValueRaw = "1.5" },
            },
            ArrayElements = new SnapshotArrayElements
            {
                Length = 1, Count = 1, ElementType = "System.Int32",
                Elements = { new SnapshotArrayElement { Index = 0, ValueRaw = "7" } },
            },
        };

        var r = RoundTrip(WrapField(f)).Instances![0].Fields[0];

        Assert.Equal("f", r.Name);
        Assert.Equal("T", r.Type);
        Assert.Equal(64UL, r.Offset);
        Assert.Equal("Base", r.From);
        Assert.True(r.IsEnum);
        Assert.True(r.IsValueType);
        Assert.Equal("0xdead", r.Ptr);
        Assert.Equal("hello", r.StringValue);
        Assert.Equal(5, r.StringLength);
        Assert.Equal("trunc", r.StringError);
        Assert.Equal("oops", r.Error);
        Assert.Equal("bad", r.ArrayError);
        Assert.Equal(9999999999UL, r.ArrayErrorLength);
        Assert.Equal("0xfeed", r.Address);
        Assert.Equal("x", r.Fields![0].Name);
        Assert.Equal("1.5", r.Fields![0].ValueRaw);
        Assert.Equal("7", r.ArrayElements!.Elements[0].ValueRaw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void TriStateBools_PackAndRoundTripLosslessly(bool? state)
    {
        var f = new SnapshotFieldEntry { Name = "f", Type = "T", IsEnum = state, IsValueType = state };

        // In-memory packing is exact for all three states.
        Assert.Equal(state, f.IsEnum);
        Assert.Equal(state, f.IsValueType);

        var r = RoundTrip(WrapField(f)).Instances![0].Fields[0];

        // Round-trip is fully lossless for all three states: WhenWritingNull omits
        // only null (read back as null); a non-null false serializes as
        // "is_enum": false and reads back as false; true as true. (The DLL emit
        // side only ever produces true, but the C# save/load path — what
        // SnapshotStore round-trips — preserves false too.)
        Assert.Equal(state, r.IsEnum);
        Assert.Equal(state, r.IsValueType);
    }

    [Fact]
    public void Forwarding_SetThenNullClears()
    {
        var f = new SnapshotFieldEntry { Name = "f", Type = "T" };

        f.Error = "x";
        Assert.Equal("x", f.Error);
        f.Error = null;
        Assert.Null(f.Error);

        f.StringLength = 3;
        Assert.Equal(3, f.StringLength);
        f.StringLength = null;
        Assert.Null(f.StringLength);

        f.ArrayErrorLength = 1234;
        Assert.Equal(1234UL, f.ArrayErrorLength);
        f.ArrayErrorLength = null;
        Assert.Null(f.ArrayErrorLength);
    }

    [Fact]
    public void ScalarField_MovedMembersDefaultNull()
    {
        // A plain scalar field touches none of the extras-backed members.
        var f = new SnapshotFieldEntry { Name = "hp", Type = "System.Int32", ValueRaw = "100" };

        Assert.Null(f.Address);
        Assert.Null(f.StringLength);
        Assert.Null(f.StringError);
        Assert.Null(f.Error);
        Assert.Null(f.ArrayElements);
        Assert.Null(f.Fields);
        Assert.Null(f.ArrayError);
        Assert.Null(f.ArrayErrorLength);
        Assert.Equal("100", f.ValueRaw);
    }

    [Fact]
    public void ArrayElement_MovedMembers_RoundTripAndForward()
    {
        var el = new SnapshotArrayElement
        {
            Index = 3, Ptr = "0xabc",
            Error = "elerr", Address = "0x123",
            SnapshotElementClassError = "not-an-object",
            Fields = new List<SnapshotFieldEntry>
            {
                new() { Name = "inner", Type = "System.Int32", ValueRaw = "5" },
            },
        };

        var c = new SnapshotClassEntry
        {
            Assembly = "a", Name = "C",
            Instances = new List<SnapshotInstanceEntry>
            {
                new()
                {
                    Address = "0x1",
                    Fields =
                    {
                        new SnapshotFieldEntry
                        {
                            Name = "arr", Type = "T[]",
                            ArrayElements = new SnapshotArrayElements
                            {
                                Length = 1, Count = 1, ElementType = "T",
                                Elements = { el },
                            },
                        },
                    },
                },
            },
        };

        var r = RoundTrip(c).Instances![0].Fields[0].ArrayElements!.Elements[0];
        Assert.Equal(3, r.Index);
        Assert.Equal("0xabc", r.Ptr);
        Assert.Equal("elerr", r.Error);
        Assert.Equal("0x123", r.Address);
        Assert.Equal("not-an-object", r.SnapshotElementClassError);
        Assert.Equal("inner", r.Fields![0].Name);

        // Forwarding null-clear on the element too.
        r.Error = null;
        Assert.Null(r.Error);
    }
}
