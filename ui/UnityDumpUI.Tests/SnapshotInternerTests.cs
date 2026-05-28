// SnapshotInternerTests — Phase 29g-1.
//
// Verifies SnapshotInterner collapses the duplicated metadata strings (field
// Name/Type/From, class Assembly/Namespace/Name, Parent.*, array ElementType,
// Layer B element inner fields) to a single shared reference per distinct
// value, while leaving inherently-unique strings (instance Address) untouched.

using System.Collections.Generic;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotInternerTests
{
    // Force a distinct string OBJECT with a given value, so ReferenceEquals is
    // false before interning (string literals would be pooled by the compiler).
    private static string Fresh(string value) => new string(value.ToCharArray());

    private static SnapshotFieldEntry Field(string name, string type, string? from = null)
        => new() { Name = Fresh(name), Type = Fresh(type), From = from is null ? null : Fresh(from) };

    [Fact]
    public void Intern_SharesRepeatedFieldTypeAndName_AcrossInstances()
    {
        // Two instances of one class, each with an "_hp" System.Int32 field —
        // four distinct string objects pre-intern (2× "_hp", 2× "System.Int32").
        var inst1 = new SnapshotInstanceEntry { Address = "0x1", Fields = { Field("_hp", "System.Int32") } };
        var inst2 = new SnapshotInstanceEntry { Address = "0x2", Fields = { Field("_hp", "System.Int32") } };
        var cls = new SnapshotClassEntry
        {
            Assembly = "asm", Namespace = "Game", Name = "Stat",
            Instances = new List<SnapshotInstanceEntry> { inst1, inst2 },
        };

        var f1 = inst1.Fields[0];
        var f2 = inst2.Fields[0];
        Assert.False(ReferenceEquals(f1.Type, f2.Type));   // distinct before

        SnapshotInterner.Intern(new[] { cls });

        Assert.Same(f1.Type, f2.Type);                     // shared after
        Assert.Same(f1.Name, f2.Name);
        Assert.Equal("System.Int32", f1.Type);
        Assert.Equal("_hp", f1.Name);
    }

    [Fact]
    public void Intern_SharesClassMetadata_AcrossClasses()
    {
        var a = new SnapshotClassEntry { Assembly = Fresh("Assembly-CSharp"), Namespace = Fresh("Game"), Name = "A" };
        var b = new SnapshotClassEntry { Assembly = Fresh("Assembly-CSharp"), Namespace = Fresh("Game"), Name = "B" };
        Assert.False(ReferenceEquals(a.Assembly, b.Assembly));

        SnapshotInterner.Intern(new[] { a, b });

        Assert.Same(a.Assembly, b.Assembly);
        Assert.Same(a.Namespace, b.Namespace);
        Assert.Equal("Assembly-CSharp", a.Assembly);
    }

    [Fact]
    public void Intern_DoesNotBreakUniqueAddresses()
    {
        var inst1 = new SnapshotInstanceEntry { Address = "0xAAAA", Fields = { Field("_x", "System.Int32") } };
        var inst2 = new SnapshotInstanceEntry { Address = "0xBBBB", Fields = { Field("_x", "System.Int32") } };
        var cls = new SnapshotClassEntry
        {
            Assembly = "asm", Namespace = "", Name = "C",
            Instances = new List<SnapshotInstanceEntry> { inst1, inst2 },
        };

        SnapshotInterner.Intern(new[] { cls });

        Assert.Equal("0xAAAA", inst1.Address);   // distinct values preserved
        Assert.Equal("0xBBBB", inst2.Address);
        Assert.Same(inst1.Fields[0].Type, inst2.Fields[0].Type);
    }

    [Fact]
    public void Intern_SharesArrayElementType_AndLayerBInnerFields()
    {
        // Two static array fields of the same element type, each with a Layer B
        // element carrying an inner "_rate" Single field.
        SnapshotFieldEntry ArrField() => new()
        {
            Name = Fresh("_data"), Type = Fresh("StatData[]"),
            ArrayElements = new SnapshotArrayElements
            {
                ElementType = Fresh("StatData"),
                Elements = new List<SnapshotArrayElement>
                {
                    new() { Index = 0, Fields = new List<SnapshotFieldEntry> { Field("_rate", "System.Single") } },
                },
            },
        };
        var f1 = ArrField();
        var f2 = ArrField();
        var cls = new SnapshotClassEntry
        {
            Assembly = "asm", Namespace = "Game", Name = "Holder",
            Statics = new List<SnapshotFieldEntry> { f1, f2 },
        };
        Assert.False(ReferenceEquals(f1.ArrayElements!.ElementType, f2.ArrayElements!.ElementType));

        SnapshotInterner.Intern(new[] { cls });

        Assert.Same(f1.ArrayElements!.ElementType, f2.ArrayElements!.ElementType);
        Assert.Same(f1.ArrayElements!.Elements[0].Fields![0].Type,
                    f2.ArrayElements!.Elements[0].Fields![0].Type);
        Assert.Equal("System.Single", f1.ArrayElements!.Elements[0].Fields![0].Type);
    }

    [Fact]
    public void Intern_EmptyOrNull_IsNoOp()
    {
        SnapshotInterner.Intern(new List<SnapshotClassEntry>());   // must not throw
        SnapshotInterner.Intern(null!);                            // null-tolerant
    }
}
