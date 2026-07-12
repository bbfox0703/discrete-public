// SetFieldValueParserTests.cs — the single-string typed-value parser behind the
// class-wide Freeze feature (and reusable for any one-textbox field write).

using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class SetFieldValueParserTests
{
    private static bool Parse(string clrType, string input, out JsonElement value, out string? error) =>
        SetFieldValueParser.TryParse(InspectorWriteTypeMap.Resolve(clrType), input, out value, out error);

    [Fact]
    public void Integer_Parses()
    {
        Assert.True(Parse("System.Int32", "100", out JsonElement v, out _));
        Assert.Equal(JsonValueKind.Number, v.ValueKind);
        Assert.Equal(100, v.GetInt64());
    }

    [Fact]
    public void UnsignedInteger_RejectsNegative()
    {
        Assert.False(Parse("System.UInt32", "-5", out _, out string? err));
        Assert.NotNull(err);
        Assert.True(Parse("System.UInt32", "40000", out JsonElement v, out _));
        Assert.Equal(40000UL, v.GetUInt64());
    }

    [Fact]
    public void Float_Parses()
    {
        Assert.True(Parse("System.Single", "1.5", out JsonElement v, out _));
        Assert.Equal(1.5, v.GetDouble(), 3);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("0")]
    public void Bool_ParsesCommonForms(string input)
        => Assert.True(Parse("System.Boolean", input, out _, out _));

    [Fact]
    public void BadValues_Fail()
    {
        Assert.False(Parse("System.Int32", "abc", out _, out _));
        Assert.False(Parse("System.Boolean", "xyz", out _, out _));
        Assert.False(Parse("System.Single", "notanumber", out _, out _));
    }

    [Fact]
    public void UnsupportedType_Fails()
    {
        // Reference types (System.String) aren't a writable scalar.
        Assert.False(Parse("System.String", "hello", out _, out string? err));
        Assert.NotNull(err);
    }
}
