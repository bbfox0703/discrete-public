// ValueSearchAndStringDecodeTests — offline coverage for the helpers added
// for the 2026-05-30 Inspector/Value-Search round:
//   * LiveInspectionViewModel.TryParseLeInt32 / DecodeUtf16LeHex — the
//     client-side managed-String decoder used when the DLL's array-element
//     path returns a String slot as a raw pointer (no decoded `str`).
//   * ValueSearchViewModel.IsSystemName / IsSystemRow — the "ignore system
//     assemblies" predicate driving the results-grid filter.
//
// Pure functions, no VM state / no pipe — safe to run in the xUnit harness.

using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class StringDecodeHelperTests
{
    // ---- TryParseLeInt32 -------------------------------------------------

    [Theory]
    [InlineData("0D000000", 13)]      // little-endian 13 (the "len 13" case)
    [InlineData("00000000", 0)]
    [InlineData("01000000", 1)]
    [InlineData("FF000000", 255)]
    [InlineData("00010000", 256)]
    public void TryParseLeInt32_DecodesLittleEndian(string hex, int expected)
    {
        Assert.True(LiveInspectionViewModel.TryParseLeInt32(hex, out int v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0D00")]              // too short (only 2 bytes)
    [InlineData("ZZ000000")]          // non-hex
    public void TryParseLeInt32_RejectsBadInput(string hex)
    {
        Assert.False(LiveInspectionViewModel.TryParseLeInt32(hex, out _));
    }

    [Fact]
    public void TryParseLeInt32_IgnoresTrailingBytes()
    {
        // readMemory may return more than 4 bytes; only the first 4 count.
        Assert.True(LiveInspectionViewModel.TryParseLeInt32("0D000000AABBCC", out int v));
        Assert.Equal(13, v);
    }

    // ---- DecodeUtf16LeHex ------------------------------------------------

    [Fact]
    public void DecodeUtf16LeHex_DecodesAscii()
    {
        // "Hi" = U+0048 U+0069, little-endian UTF-16 = 48 00 69 00.
        Assert.Equal("Hi", LiveInspectionViewModel.DecodeUtf16LeHex("48006900", 2));
    }

    [Fact]
    public void DecodeUtf16LeHex_DecodesPCharaSample()
    {
        // "P_Chara_20514" — the clothesID_ value from the screenshot (len 13).
        const string text = "P_Chara_20514";
        var sb = new System.Text.StringBuilder();
        foreach (char c in text)
            sb.Append(((byte)(c & 0xFF)).ToString("X2")).Append(((byte)(c >> 8)).ToString("X2"));
        Assert.Equal(text, LiveInspectionViewModel.DecodeUtf16LeHex(sb.ToString(), text.Length));
    }

    [Fact]
    public void DecodeUtf16LeHex_DecodesNonAsciiBmp()
    {
        // "あ" = U+3042, little-endian = 42 30.
        Assert.Equal("あ", LiveInspectionViewModel.DecodeUtf16LeHex("4230", 1));
    }

    [Fact]
    public void DecodeUtf16LeHex_ZeroLengthIsEmpty()
    {
        Assert.Equal(string.Empty, LiveInspectionViewModel.DecodeUtf16LeHex("", 0));
    }

    [Fact]
    public void DecodeUtf16LeHex_ShortBlobReturnsNull()
    {
        // Asked for 3 chars (needs 12 hex chars) but only 4 supplied → null.
        Assert.Null(LiveInspectionViewModel.DecodeUtf16LeHex("4800", 3));
    }

    [Fact]
    public void DecodeUtf16LeHex_NonHexReturnsNull()
    {
        Assert.Null(LiveInspectionViewModel.DecodeUtf16LeHex("48XX", 1));
    }

    // ---- StringFieldOffsets (32-bit / 64-bit) ----------------------------

    [Fact]
    public void StringFieldOffsets_X64()
    {
        var (len, chars) = LiveInspectionViewModel.StringFieldOffsets(8);
        Assert.Equal(0x10, len);
        Assert.Equal(0x14, chars);
    }

    [Fact]
    public void StringFieldOffsets_X86()
    {
        var (len, chars) = LiveInspectionViewModel.StringFieldOffsets(4);
        Assert.Equal(0x8, len);
        Assert.Equal(0xC, chars);
    }

    [Fact]
    public void StringFieldOffsets_UnknownPointerSizeFallsBackToX64()
    {
        // 0 (not yet connected) and any non-4 value are treated as x64.
        Assert.Equal((0x10, 0x14), LiveInspectionViewModel.StringFieldOffsets(0));
    }
}

public class ValueSearchSystemFilterTests
{
    [Theory]
    [InlineData("UnityEngine.CoreModule")]
    [InlineData("UnityEngine")]
    [InlineData("System")]
    [InlineData("System.Collections")]
    [InlineData("mscorlib")]
    [InlineData("netstandard")]
    [InlineData("Mono.Security")]
    [InlineData("Cinemachine")]
    [InlineData("Newtonsoft.Json")]
    public void IsSystemName_FlagsEngineAndFramework(string name)
        => Assert.True(ValueSearchViewModel.IsSystemName(name));

    [Theory]
    [InlineData("Assembly-CSharp")]
    [InlineData("CharacterData")]
    [InlineData("MainGame.PlayerState")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSystemName_LeavesGameCode(string? name)
        => Assert.False(ValueSearchViewModel.IsSystemName(name));

    [Fact]
    public void IsSystemRow_CatchesNamespacedEngineTypeWithBlankAssembly()
    {
        // Mirrors the screenshot: namespace/class carries "Cinemachine.*"
        // even when the Assembly column is blank.
        var c = new ValueScanCandidate
        {
            Address   = "0x1",
            Assembly  = "",
            Namespace = "Cinemachine",
            Class     = "CinemachineBrain",
        };
        Assert.True(ValueSearchViewModel.IsSystemRow(new ValueSearchRow(c)));
    }

    [Fact]
    public void IsSystemRow_KeepsGameClass()
    {
        var c = new ValueScanCandidate
        {
            Address   = "0x1",
            Assembly  = "Assembly-CSharp",
            Namespace = "",
            Class     = "CharacterData",
        };
        Assert.False(ValueSearchViewModel.IsSystemRow(new ValueSearchRow(c)));
    }
}
