// TeleportCoordinateParserTests.cs — Task 5. Pure tests for coordinate
// copy/paste parsing + formatting.

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class TeleportCoordinateParserTests
{
    [Theory]
    [InlineData("(1.0, 2.0, 3.0)")]     // Unity Vector3.ToString()
    [InlineData("1 2 3")]               // bare, space-separated
    [InlineData("1,2,3")]               // comma-separated
    [InlineData("X=1 Y=2 Z=3")]         // labelled (labels ignored)
    [InlineData("  1 , 2 , 3  ")]       // stray whitespace
    public void Parse_ThreeNumbers_IsPosition(string input)
    {
        ParsedCoordinates r = TeleportCoordinateParser.Parse(input);
        Assert.True(r.Ok);
        Assert.False(r.HasRotation);
        Assert.Equal(1.0, r.X, 5);
        Assert.Equal(2.0, r.Y, 5);
        Assert.Equal(3.0, r.Z, 5);
    }

    [Fact]
    public void Parse_HandlesNegativesAndDecimals()
    {
        ParsedCoordinates r = TeleportCoordinateParser.Parse("1.5, -2.3, 890");
        Assert.True(r.Ok);
        Assert.Equal(1.5,   r.X, 5);
        Assert.Equal(-2.3,  r.Y, 5);
        Assert.Equal(890.0, r.Z, 5);
    }

    [Fact]
    public void Parse_HandlesScientificNotation()
    {
        ParsedCoordinates r = TeleportCoordinateParser.Parse("1.5e1 2 3");
        Assert.True(r.Ok);
        Assert.Equal(15.0, r.X, 5);
    }

    [Theory]
    [InlineData("1 2 3 10 20 30")]
    [InlineData("(1, 2, 3) (10, 20, 30)")]
    public void Parse_SixNumbers_IsPositionPlusRotation(string input)
    {
        ParsedCoordinates r = TeleportCoordinateParser.Parse(input);
        Assert.True(r.Ok);
        Assert.True(r.HasRotation);
        Assert.Equal(3.0,  r.Z, 5);
        Assert.Equal(10.0, r.EulerX, 5);
        Assert.Equal(20.0, r.EulerY, 5);
        Assert.Equal(30.0, r.EulerZ, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("1 2")]        // too few
    [InlineData("1 2 3 4")]    // ambiguous count
    public void Parse_BadInput_ReturnsError(string? input)
    {
        ParsedCoordinates r = TeleportCoordinateParser.Parse(input);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Format_Position_RoundTripsThroughParse()
    {
        string s = TeleportCoordinateParser.Format(1.5, -2.25, 890.0);
        Assert.Equal("(1.5, -2.25, 890)", s);
        ParsedCoordinates r = TeleportCoordinateParser.Parse(s);
        Assert.True(r.Ok);
        Assert.False(r.HasRotation);
        Assert.Equal(1.5,   r.X, 5);
        Assert.Equal(-2.25, r.Y, 5);
        Assert.Equal(890.0, r.Z, 5);
    }

    [Fact]
    public void Format_PositionAndRotation_RoundTripsThroughParse()
    {
        string s = TeleportCoordinateParser.Format(1, 2, 3, 10, 20, 30);
        ParsedCoordinates r = TeleportCoordinateParser.Parse(s);
        Assert.True(r.Ok);
        Assert.True(r.HasRotation);
        Assert.Equal(1.0,  r.X, 5);
        Assert.Equal(30.0, r.EulerZ, 5);
    }
}
