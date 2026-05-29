// SnapshotValueCompactionTests — Phase 30a-2 (value compaction).
//
// Verifies SnapshotFieldEntry / SnapshotArrayElement store their value as raw
// JSON text (ValueRaw) instead of a retained JsonElement, that the computed
// Value get/set accessor still behaves, and that the JSON round-trip (the shape
// SnapshotStore persists to SQLite) is byte-compatible with the old "value" key.

using System.Collections.Generic;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotValueCompactionTests
{
    private static SnapshotClassEntry RoundTrip(SnapshotClassEntry c)
    {
        var json = JsonSerializer.Serialize(c, ProtocolJsonContext.Default.SnapshotClassEntry);
        return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SnapshotClassEntry)!;
    }

    [Fact]
    public void Value_NumberStringBool_RoundTripAsRawJson()
    {
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
                        new SnapshotFieldEntry { Name = "n", Type = "System.Int32",   ValueRaw = "325" },
                        new SnapshotFieldEntry { Name = "s", Type = "System.String",  Value = JsonDocument.Parse("\"hi\"").RootElement },
                        new SnapshotFieldEntry { Name = "b", Type = "System.Boolean", ValueRaw = "true" },
                        new SnapshotFieldEntry { Name = "empty", Type = "System.Object" },   // no value
                    },
                },
            },
        };

        var fields = RoundTrip(c).Instances![0].Fields;

        Assert.Equal("325", fields[0].ValueRaw);
        Assert.Equal(325, fields[0].Value!.Value.GetInt32());

        // A string value is retained as raw JSON (quoted); computed Value unwraps it.
        Assert.Equal("\"hi\"", fields[1].ValueRaw);
        Assert.Equal("hi", fields[1].Value!.Value.GetString());

        Assert.Equal("true", fields[2].ValueRaw);
        Assert.Equal(JsonValueKind.True, fields[2].Value!.Value.ValueKind);

        Assert.Null(fields[3].ValueRaw);
        Assert.Null(fields[3].Value);
    }

    [Fact]
    public void Value_NullRaw_OmittedFromJson()
    {
        var c = new SnapshotClassEntry
        {
            Assembly = "a", Name = "C",
            Instances = new List<SnapshotInstanceEntry>
            {
                new() { Address = "0x1", Fields = { new SnapshotFieldEntry { Name = "n", Type = "System.Int32" } } },
            },
        };

        var json = JsonSerializer.Serialize(c, ProtocolJsonContext.Default.SnapshotClassEntry);

        // WhenWritingNull omits the value-less field's "value" key entirely
        // (matches the pre-compaction JsonElement? null behaviour).
        Assert.DoesNotContain("\"value\"", json);
    }

    [Fact]
    public void Value_GetSet_BridgesJsonElementAndRawText()
    {
        var f = new SnapshotFieldEntry { Value = JsonDocument.Parse("42").RootElement };
        Assert.Equal("42", f.ValueRaw);
        Assert.Equal(42, f.Value!.Value.GetInt32());

        f.Value = null;
        Assert.Null(f.ValueRaw);
        Assert.Null(f.Value);
    }

    [Fact]
    public void ArrayElement_Value_RoundTrips()
    {
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
                            Name = "arr", Type = "System.Int32[]",
                            ArrayElements = new SnapshotArrayElements
                            {
                                Length = 2, Count = 2, ElementType = "System.Int32",
                                Elements =
                                {
                                    new SnapshotArrayElement { Index = 0, ValueRaw = "7" },
                                    new SnapshotArrayElement { Index = 1, ValueRaw = "9" },
                                },
                            },
                        },
                    },
                },
            },
        };

        var arr = RoundTrip(c).Instances![0].Fields[0].ArrayElements!;
        Assert.Equal("7", arr.Elements[0].ValueRaw);
        Assert.Equal(9, arr.Elements[1].Value!.Value.GetInt32());
    }
}
