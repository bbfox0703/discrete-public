// KeywordTokenizerTests.cs — locks down the KeywordTokenizer split rules
// (ported from UE5CEDumper) and the NamePrior substring-bug fix in
// KeyDiscoveryEngine that motivated the port.

using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class KeywordTokenizerTests
{
    // Rule 1+2: separators (underscore, hyphen).
    [Theory]
    [InlineData("m_Player_C",          new[] { "m", "player", "c" })]
    [InlineData("Foo_Bar_Baz",         new[] { "foo", "bar", "baz" })]
    [InlineData("__leadingTrailing__", new[] { "leading", "trailing" })]
    [InlineData("snake-case-name",     new[] { "snake", "case", "name" })]
    public void Tokenize_SeparatorsSplit(string input, string[] expected)
        => Assert.Equal(expected, KeywordTokenizer.Tokenize(input));

    // Rule 3: lower-to-upper transition.
    [Theory]
    [InlineData("AddMoney",          new[] { "add", "money" })]
    [InlineData("AddPlayerCurrency", new[] { "add", "player", "currency" })]
    [InlineData("SetMaxHealth",      new[] { "set", "max", "health" })]
    [InlineData("getHealth",         new[] { "get", "health" })]
    [InlineData("camelCase",         new[] { "camel", "case" })]
    [InlineData("m_CachedPtr",       new[] { "m", "cached", "ptr" })]
    public void Tokenize_LowerToUpperBoundary(string input, string[] expected)
        => Assert.Equal(expected, KeywordTokenizer.Tokenize(input));

    // Rule 4: run of uppers followed by lower (acronym-ending boundary).
    [Theory]
    [InlineData("HUDWidget",       new[] { "hud", "widget" })]
    [InlineData("UIPanel",         new[] { "ui", "panel" })]
    [InlineData("XMLParser",       new[] { "xml", "parser" })]
    [InlineData("HTTPSConnection", new[] { "https", "connection" })]
    public void Tokenize_AcronymEndingBoundary(string input, string[] expected)
        => Assert.Equal(expected, KeywordTokenizer.Tokenize(input));

    // Acronym-only / all-caps tokens stay whole.
    [Theory]
    [InlineData("HP",       new[] { "hp" })]
    [InlineData("MP",       new[] { "mp" })]
    [InlineData("GetHP",    new[] { "get", "hp" })]
    [InlineData("PlayerHP", new[] { "player", "hp" })]
    [InlineData("HPBar",    new[] { "hp", "bar" })]
    public void Tokenize_AcronymsKeepTogether(string input, string[] expected)
        => Assert.Equal(expected, KeywordTokenizer.Tokenize(input));

    // The substring-noise regression cases that motivated tokenization: these
    // CONTAIN "mp"/"sp"/"id" but must NOT yield those as standalone tokens.
    [Theory]
    [InlineData("Component",   new[] { "component" })]
    [InlineData("MyComponent", new[] { "my", "component" })]
    [InlineData("Spawn",       new[] { "spawn" })]
    [InlineData("SpawnActor",  new[] { "spawn", "actor" })]
    [InlineData("grid",        new[] { "grid" })]
    [InlineData("valid",       new[] { "valid" })]
    public void Tokenize_NoSubstringFalsePositives(string input, string[] expected)
        => Assert.Equal(expected, KeywordTokenizer.Tokenize(input));

    // Edge cases.
    [Fact] public void Tokenize_NullInput_ReturnsEmpty()  => Assert.Empty(KeywordTokenizer.Tokenize(null));
    [Fact] public void Tokenize_EmptyInput_ReturnsEmpty() => Assert.Empty(KeywordTokenizer.Tokenize(""));

    [Fact]
    public void Tokenize_AllLowercase_SingleToken()
        => Assert.Equal(new[] { "lowercase" }, KeywordTokenizer.Tokenize("lowercase"));

    [Fact]
    public void Tokenize_AllUppercase_SingleToken()
        => Assert.Equal(new[] { "uppercase" }, KeywordTokenizer.Tokenize("UPPERCASE"));

    [Fact]
    public void Tokenize_SingleChar_OneToken()
    {
        Assert.Equal(new[] { "x" }, KeywordTokenizer.Tokenize("x"));
        Assert.Equal(new[] { "x" }, KeywordTokenizer.Tokenize("X"));
    }

    [Fact]
    public void Tokenize_DigitsAttachToNeighbour()
    {
        // No digit-boundary split — use underscores for boundaries.
        Assert.Equal(new[] { "player100health" }, KeywordTokenizer.Tokenize("Player100Health"));
        Assert.Equal(new[] { "k2node42" }, KeywordTokenizer.Tokenize("K2Node42"));
    }

    [Fact]
    public void TokenizeAsSet_DedupesRepeatedTokens()
    {
        var set = KeywordTokenizer.TokenizeAsSet("AddHealthAddHealth");
        Assert.Equal(2, set.Count);
        Assert.Contains("add", set);
        Assert.Contains("health", set);
    }

    [Fact]
    public void TokenizeAsSet_NullInput_ReturnsEmptySet()
        => Assert.Empty(KeywordTokenizer.TokenizeAsSet(null));

    // ── KeyDiscoveryEngine.NamePrior: the substring bug the tokenizer fixes ──

    [Theory]
    // Standalone key tokens still credit (these worked before AND after).
    [InlineData("userId",    0.30)]   // {user, id}
    [InlineData("itemId",    0.30)]   // {item, id}
    [InlineData("typeId",    0.30)]   // {type, id} -> id (0.30) beats type (0.15)
    [InlineData("className", 0.20)]   // {class, name}
    [InlineData("sortKey",   0.20)]   // {sort, key}
    [InlineData("hashCode",  0.20)]   // {hash, code} -> hash (0.20) beats code (0.10)
    [InlineData("errorCode", 0.10)]   // {error, code}
    // Substring FALSE POSITIVES that the old naive .Contains credited — now 0.
    [InlineData("width",     0.00)]   // contains "id"
    [InlineData("grid",      0.00)]   // contains "id"
    [InlineData("guid",      0.00)]   // contains "id"
    [InlineData("valid",     0.00)]   // contains "id"
    [InlineData("monkey",    0.00)]   // contains "key"
    [InlineData("decoded",   0.00)]   // contains "code"
    public void NamePrior_TokenExact_NoSubstringFalsePositives(string name, double expected)
        => Assert.Equal(expected, KeyDiscoveryEngine.NamePrior(name), 3);
}
