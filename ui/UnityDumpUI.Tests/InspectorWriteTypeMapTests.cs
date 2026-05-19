// InspectorWriteTypeMapTests.cs — Phase 21b. Pure-logic coverage for the
// CLR-type → wire-type routing helper. No I/O, no pipes — these are AAA
// classifier tests so a regression in the routing rules surfaces fast.

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class InspectorWriteTypeMapTests
{
    [Theory]
    [InlineData("System.Boolean", "bool",   FieldWriteEditorKind.Bool,    1)]
    [InlineData("System.SByte",   "int8",   FieldWriteEditorKind.Integer, 1)]
    [InlineData("System.Byte",    "uint8",  FieldWriteEditorKind.Integer, 1)]
    [InlineData("System.Int16",   "int16",  FieldWriteEditorKind.Integer, 2)]
    [InlineData("System.UInt16",  "uint16", FieldWriteEditorKind.Integer, 2)]
    [InlineData("System.Char",    "char",   FieldWriteEditorKind.Char,    2)]
    [InlineData("System.Int32",   "int32",  FieldWriteEditorKind.Integer, 4)]
    [InlineData("System.UInt32",  "uint32", FieldWriteEditorKind.Integer, 4)]
    [InlineData("System.Int64",   "int64",  FieldWriteEditorKind.Integer, 8)]
    [InlineData("System.UInt64",  "uint64", FieldWriteEditorKind.Integer, 8)]
    [InlineData("System.Single",  "float",  FieldWriteEditorKind.Float,   4)]
    [InlineData("System.Double",  "double", FieldWriteEditorKind.Float,   8)]
    [InlineData("System.IntPtr",  "address", FieldWriteEditorKind.Address, 8)]
    [InlineData("System.UIntPtr", "address", FieldWriteEditorKind.Address, 8)]
    public void Resolve_PrimitiveOrPointer_RoutesToExpectedWireType(
        string clr, string expectedWire, FieldWriteEditorKind expectedKind, int expectedSize)
    {
        var info = InspectorWriteTypeMap.Resolve(clr);
        Assert.True(info.Supported);
        Assert.Equal(expectedWire, info.WireType);
        Assert.Equal(expectedKind, info.EditorKind);
        Assert.Equal(expectedSize, info.ByteSize);
    }

    [Theory]
    [InlineData("System.Byte")]
    [InlineData("System.UInt16")]
    [InlineData("System.UInt32")]
    [InlineData("System.UInt64")]
    [InlineData("System.Char")]
    [InlineData("System.IntPtr")]
    [InlineData("System.UIntPtr")]
    public void Resolve_UnsignedTypes_MarkedUnsigned(string clr)
    {
        Assert.True(InspectorWriteTypeMap.Resolve(clr).IsUnsigned);
    }

    [Theory]
    [InlineData("System.SByte")]
    [InlineData("System.Int16")]
    [InlineData("System.Int32")]
    [InlineData("System.Int64")]
    [InlineData("System.Single")]
    [InlineData("System.Double")]
    [InlineData("System.Boolean")]
    public void Resolve_SignedTypes_NotMarkedUnsigned(string clr)
    {
        Assert.False(InspectorWriteTypeMap.Resolve(clr).IsUnsigned);
    }

    [Fact]
    public void Resolve_Int32_RangeMatchesIntBounds()
    {
        var info = InspectorWriteTypeMap.Resolve("System.Int32");
        Assert.Equal(int.MinValue, info.MinValue);
        Assert.Equal(int.MaxValue, info.MaxValue);
    }

    [Fact]
    public void Resolve_Byte_RangeIsZeroTo255()
    {
        var info = InspectorWriteTypeMap.Resolve("System.Byte");
        Assert.Equal(0m,   info.MinValue);
        Assert.Equal(255m, info.MaxValue);
    }

    [Theory]
    [InlineData("System.String")]
    [InlineData("UnityEngine.Vector3")]      // user value-struct deferred to v2
    [InlineData("UnityEngine.Transform")]    // pointer-to-class deferred to v2
    [InlineData("System.Int32[]")]           // arrays deferred
    [InlineData("System.Collections.Generic.List`1<System.Int32>")]
    [InlineData("MainGame.PARTY_WORK")]      // unknown custom class
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_NonPrimitive_NotSupported(string? clr)
    {
        Assert.False(InspectorWriteTypeMap.Resolve(clr).Supported);
    }

    [Fact]
    public void Resolve_TrimsWhitespace()
    {
        // The walkInstance wire shape doesn't pad type names, but defensive
        // trim guards us against future surprises (e.g. CLI parsing).
        Assert.True(InspectorWriteTypeMap.Resolve("  System.Int32  ").Supported);
    }
}
