// AddressLookupAaScriptGeneratorTests.cs — Phase 22e verification.
//
// Pin every load-bearing piece of the parent AA script + the child
// records: bridge guard, registersymbol/dq/dealloc shape, sanitized
// symbol name (dots/slashes from class name don't leak through), CLR
// → CE TVariableType mapping for primitives + enums + unknown,
// exact-vs-nearest field labelling.

using System;
using System.Linq;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class AddressLookupAaScriptGeneratorTests
{
    private static AddressLookupRequest BasicReq(AddressLookupField? field = null,
                                                 AddressLookupField? nearest = null,
                                                 string instance = "0x000002201761E3D0",
                                                 string className = "PLY_WORK",
                                                 string? @namespace = "MainGame") =>
        new()
        {
            Instance     = instance,
            ClassName    = className,
            Namespace    = @namespace,
            Field        = field,
            NearestField = nearest,
        };

    [Fact]
    public void Build_ParentScript_HasBridgeGuard_AndAaSymbolPlumbing()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq());

        // Bridge-loaded guard at the top of [ENABLE].
        Assert.Contains("if not _G.discrete_bridgeLoaded then", p.ParentScript);
        // alloc + registersymbol + dq + matching unregister/dealloc on DISABLE.
        Assert.Contains("alloc(DiscreteAt_PLY_WORK_000002201761E3D0, 8)", p.ParentScript);
        Assert.Contains("registersymbol(DiscreteAt_PLY_WORK_000002201761E3D0)", p.ParentScript);
        Assert.Contains("dq 0x000002201761E3D0", p.ParentScript);
        Assert.Contains("unregistersymbol(DiscreteAt_PLY_WORK_000002201761E3D0)", p.ParentScript);
        Assert.Contains("dealloc(DiscreteAt_PLY_WORK_000002201761E3D0)", p.ParentScript);

        // ENABLE precedes DISABLE.
        Assert.True(p.ParentScript.IndexOf("[ENABLE]", StringComparison.Ordinal)
                  < p.ParentScript.IndexOf("[DISABLE]", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ParentDescription_IncludesNamespaceClassAndAddress()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq());
        Assert.Equal("Discrete: MainGame.PLY_WORK @ 0x000002201761E3D0",
                     p.ParentDescription);
    }

    [Fact]
    public void Build_ParentDescription_OmitsNamespaceWhenEmpty()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(@namespace: ""));
        Assert.Equal("Discrete: PLY_WORK @ 0x000002201761E3D0", p.ParentDescription);
    }

    [Fact]
    public void Build_StripsHexPrefix_AndUppercases()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(instance: "0xdeadbeef"));
        Assert.Contains("dq 0xDEADBEEF", p.ParentScript);
        Assert.Contains("DiscreteAt_PLY_WORK_DEADBEEF", p.ParentScript);
    }

    [Fact]
    public void Build_SanitizesNonIdentifierChars_InClassName()
    {
        // Nested type "Outer/Inner" + namespace dots would crash CE's
        // assembler if they leaked into the symbol name. Sanitize maps
        // every non-[A-Za-z0-9_] to '_'.
        var p = AddressLookupAaScriptGenerator.Build(
            BasicReq(className: "Outer/Inner"));
        Assert.Contains("DiscreteAt_Outer_Inner_", p.ParentScript);
        Assert.DoesNotContain("/Inner_", p.ParentScript); // no slashes
    }

    [Fact]
    public void Build_NoFieldAtAll_YieldsZeroChildRecords()
    {
        // whatIsAt found a class but no field info available — register
        // the symbol and let the user add their own children in CE.
        var p = AddressLookupAaScriptGenerator.Build(BasicReq());
        Assert.Empty(p.ChildRecords);
    }

    [Fact]
    public void Build_ExactFieldMatch_YieldsOneChildWithCorrectMapping()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(field: new AddressLookupField
        {
            Name   = "HP",
            Type   = "System.Int32",
            Offset = 0x10,
            Size   = 4,
        }));

        var child = Assert.Single(p.ChildRecords);
        Assert.Equal("HP @+0x10  (System.Int32, 4B)", child.Description);
        Assert.Equal("DiscreteAt_PLY_WORK_000002201761E3D0", child.Address);
        Assert.Equal(CeValueType.Dword, child.ValueType);
        Assert.True(child.IsSigned);
        Assert.Equal(new[] { 0x10 }, child.Offsets);
        // Description must NOT carry the "[closest below]" tag — that's
        // reserved for the nearestField fallback path.
        Assert.DoesNotContain("[closest below]", child.Description);
    }

    [Fact]
    public void Build_NoExactFieldButNearest_LabelsAsClosestBelow()
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(nearest: new AddressLookupField
        {
            Name   = "MP",
            Type   = "System.Single",
            Offset = 0x14,
            Size   = 4,
        }));

        var child = Assert.Single(p.ChildRecords);
        Assert.Contains("MP @+0x14",      child.Description);
        Assert.Contains("[closest below]", child.Description);
        Assert.Equal(CeValueType.Single,  child.ValueType);
        Assert.False(child.IsSigned);
    }

    [Fact]
    public void Build_PrefersExactFieldOverNearest()
    {
        // When both Field and NearestField are present (rare but
        // possible), the exact match wins — emitting both would
        // confuse the user with two rows for one offset.
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(
            field: new AddressLookupField { Name = "exact", Type = "System.Byte", Offset = 1, Size = 1 },
            nearest: new AddressLookupField { Name = "nearest", Type = "System.Byte", Offset = 0, Size = 1 }));
        var child = Assert.Single(p.ChildRecords);
        Assert.Contains("exact", child.Description);
        Assert.DoesNotContain("nearest", child.Description);
    }

    [Theory]
    [InlineData("System.Boolean", CeValueType.Byte,           false)]
    [InlineData("System.Byte",    CeValueType.Byte,           false)]
    [InlineData("System.SByte",   CeValueType.Byte,           true)]
    [InlineData("System.Int16",   CeValueType.Word,           true)]
    [InlineData("System.UInt16",  CeValueType.Word,           false)]
    [InlineData("System.Char",    CeValueType.Word,           false)]
    [InlineData("System.Int32",   CeValueType.Dword,          true)]
    [InlineData("System.UInt32",  CeValueType.Dword,          false)]
    [InlineData("System.Int64",   CeValueType.Qword,          true)]
    [InlineData("System.UInt64",  CeValueType.Qword,          false)]
    [InlineData("System.IntPtr",  CeValueType.Qword,          false)]
    [InlineData("System.Single",  CeValueType.Single,         false)]
    [InlineData("System.Double",  CeValueType.Double,         false)]
    [InlineData("System.String",  CeValueType.UnicodeString,  false)]
    [InlineData("MyNs.MyClass",   CeValueType.Qword,          false)] // pointer fallback
    public void Build_MapsClrTypesToExpectedCeValueType(string clrType, CeValueType expected, bool expectSigned)
    {
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(field: new AddressLookupField
        {
            Name = "f", Type = clrType, Offset = 0, Size = 4,
        }));
        var child = Assert.Single(p.ChildRecords);
        Assert.Equal(expected,     child.ValueType);
        Assert.Equal(expectSigned, child.IsSigned);
    }

    [Fact]
    public void Build_EnumField_UsesUnderlyingTypeForCeMapping()
    {
        // Enums of underlying System.UInt16 should render as CE Word
        // unsigned (NOT default to Qword from the unrecognised enum
        // type name).
        var p = AddressLookupAaScriptGenerator.Build(BasicReq(field: new AddressLookupField
        {
            Name           = "state",
            Type           = "MyNs.MyEnum",
            Offset         = 0x20,
            Size           = 2,
            IsEnum         = true,
            UnderlyingType = "System.UInt16",
        }));
        var child = Assert.Single(p.ChildRecords);
        Assert.Equal(CeValueType.Word, child.ValueType);
        Assert.False(child.IsSigned);
    }

    [Fact]
    public void Build_RejectsMissingRequiredFields()
    {
        Assert.Throws<ArgumentNullException>(() => AddressLookupAaScriptGenerator.Build(null!));
        Assert.Throws<ArgumentException>(() => AddressLookupAaScriptGenerator.Build(
            new AddressLookupRequest { Instance = "", ClassName = "C" }));
        Assert.Throws<ArgumentException>(() => AddressLookupAaScriptGenerator.Build(
            new AddressLookupRequest { Instance = "0x10", ClassName = "" }));
    }
}
