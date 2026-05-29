// SnapshotDedupNormalizeTests — Phase 30a.
//
// Verifies SnapshotChunkResult.NormalizeDeduped() re-expands the deduped wire
// (per-class schema-once + per-instance value-only arrays) back into the inline
// SnapshotFieldEntry model the rest of the UI + SQLite expect — filling
// name/type/offset/from/is_enum/is_value_type from the class schema while
// preserving each instance's decoded values, and leaving old (non-deduped)
// chunks untouched.

using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotDedupNormalizeTests
{
    private static JsonElement Num(double v) =>
        JsonDocument.Parse(v.ToString(CultureInfo.InvariantCulture)).RootElement;

    [Fact]
    public void NormalizeDeduped_FillsSchemaKeys_AndPreservesValues()
    {
        var schema = new List<SnapshotFieldSchema>
        {
            new() { Name = "life_", Type = "System.Single", Offset = 64, From = "CharacterData", IsValueType = true },
            new() { Name = "name_", Type = "System.String", Offset = 40, From = "CharacterData" },
        };
        var inst1 = new SnapshotInstanceEntry
        {
            Address = "0x1",
            V = new List<SnapshotFieldEntry>
            {
                new() { Value = Num(325.0) },
                new() { StringValue = "Alice", StringLength = 5 },
            },
        };
        var inst2 = new SnapshotInstanceEntry
        {
            Address = "0x2",
            V = new List<SnapshotFieldEntry>
            {
                new() { Value = Num(120.0) },
                new() { StringValue = "Bob", StringLength = 3 },
            },
        };
        var cls = new SnapshotClassEntry
        {
            Assembly = "Assembly-CSharp", Namespace = "", Name = "CharacterData",
            Schema = schema,
            Instances = new List<SnapshotInstanceEntry> { inst1, inst2 },
        };
        var chunk = new SnapshotChunkResult { Classes = new List<SnapshotClassEntry> { cls } };

        chunk.NormalizeDeduped();

        // Transient schema/V dropped; Fields populated.
        Assert.Null(cls.Schema);
        Assert.Null(inst1.V);
        Assert.Equal(2, inst1.Fields.Count);

        var f0 = inst1.Fields[0];
        Assert.Equal("life_", f0.Name);
        Assert.Equal("System.Single", f0.Type);
        Assert.Equal(64UL, f0.Offset);
        Assert.Equal("CharacterData", f0.From);
        Assert.True(f0.IsValueType);
        Assert.Equal(325.0, f0.Value!.Value.GetDouble());

        var f1 = inst1.Fields[1];
        Assert.Equal("name_", f1.Name);
        Assert.Equal("System.String", f1.Type);
        Assert.Equal("Alice", f1.StringValue);

        // Second instance shares the schema metadata, keeps its own values.
        Assert.Equal("life_", inst2.Fields[0].Name);
        Assert.Equal(120.0, inst2.Fields[0].Value!.Value.GetDouble());
        Assert.Equal("Bob", inst2.Fields[1].StringValue);
    }

    [Fact]
    public void NormalizeDeduped_OldFormat_IsNoOp()
    {
        var inst = new SnapshotInstanceEntry
        {
            Address = "0x1",
            Fields = { new SnapshotFieldEntry { Name = "hp", Type = "System.Int32", Offset = 16 } },
        };
        var cls = new SnapshotClassEntry
        {
            Assembly = "a", Name = "C",
            Instances = new List<SnapshotInstanceEntry> { inst },
        };
        var chunk = new SnapshotChunkResult { Classes = new List<SnapshotClassEntry> { cls } };

        chunk.NormalizeDeduped();

        Assert.Single(inst.Fields);
        Assert.Equal("hp", inst.Fields[0].Name);
        Assert.Null(cls.Schema);
    }

    [Fact]
    public void NormalizeDeduped_FewerValuesThanSchema_ExpandsOnlyPresent()
    {
        var cls = new SnapshotClassEntry
        {
            Assembly = "a", Name = "C",
            Schema = new List<SnapshotFieldSchema>
            {
                new() { Name = "x", Type = "System.Int32", Offset = 0 },
                new() { Name = "y", Type = "System.Int32", Offset = 4 },
            },
            Instances = new List<SnapshotInstanceEntry>
            {
                new() { Address = "0x1", V = new List<SnapshotFieldEntry> { new() { Value = Num(1) } } },
            },
        };
        var chunk = new SnapshotChunkResult { Classes = new List<SnapshotClassEntry> { cls } };

        chunk.NormalizeDeduped();

        Assert.Single(cls.Instances![0].Fields);
        Assert.Equal("x", cls.Instances![0].Fields[0].Name);
    }
}
