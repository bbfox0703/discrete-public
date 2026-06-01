// BridgeLuaResourcesTests.cs — Phase 22b verification.
//
// Pin the contract that all 8 CE Lua bridge scripts ship in the Core
// assembly's manifest with the expected LogicalName, in the
// dependency-correct LoadOrder, and that ReadAll round-trips the
// physical files on disk byte-for-byte.
//
// Catches:
//   * A renamed script whose <LogicalName> wasn't updated.
//   * A new script added to scripts/ but missing from
//     UnityDumpUI.Core.csproj's <EmbeddedResource> list.
//   * LoadOrder drift (bootstrap loader expects utils first, and the
//     order list must match the manifest one-for-one).
//   * Empty / truncated resources (would fool a manual smoke run but
//     break Phase 22d's inject loop).

using System;
using System.IO;
using System.Linq;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class BridgeLuaResourcesTests
{
    private static readonly string ScriptsDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "scripts"));

    [Fact]
    public void LoadOrder_HasExpectedFiles_InDependencyOrder()
    {
        // utils.lua must come first because unity_dump.lua does
        // `require 'utils'`. The other scripts are independent (no
        // require/dofile cross-references) so their relative order is
        // convention, not required — but the test pins it so future
        // edits can't accidentally swap them without a code review.
        // mono_registersymbol_helpers.lua is the DLL-free helper file
        // (Methods overload picker "Copy mono_register* AA script");
        // listed last because the bootstrap order doesn't depend on it.
        Assert.Equal(new[]
        {
            "utils.lua",
            "unity_dump.lua",
            "unity_dissect.lua",
            "unity_symbols.lua",
            "unity_addrfind.lua",
            "unity_invoke.lua",
            "unity_scene.lua",
            "mono_registersymbol_helpers.lua",
        }, BridgeLuaResources.LoadOrder);
    }

    [Fact]
    public void ListEmbeddedNames_MatchesLoadOrder_OneForOne()
    {
        // The manifest's actual contents (whatever ended up in the DLL
        // at build time) must equal the LoadOrder list mapped through
        // the LogicalName prefix. Catches "added a file to LoadOrder
        // but forgot the <EmbeddedResource>" and vice versa.
        var manifest = BridgeLuaResources.ListEmbeddedNames();
        var expected = BridgeLuaResources.LoadOrder
            .Select(f => "UnityDumpUI.Core.Resources.Bridge." + f)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, manifest);
    }

    [Theory]
    [InlineData("utils.lua")]
    [InlineData("unity_dump.lua")]
    [InlineData("unity_dissect.lua")]
    [InlineData("unity_symbols.lua")]
    [InlineData("unity_addrfind.lua")]
    [InlineData("unity_invoke.lua")]
    [InlineData("unity_scene.lua")]
    [InlineData("mono_registersymbol_helpers.lua")]
    public void Read_ReturnsNonEmptyContent_ForEveryBridgeFile(string fileName)
    {
        string content = BridgeLuaResources.Read(fileName);
        Assert.False(string.IsNullOrWhiteSpace(content));
        // Every bridge file starts with a Lua comment banner. Cheap
        // smoke that the resource didn't get truncated to a single
        // newline at extract time.
        Assert.StartsWith("-- =", content);
    }

    [Fact]
    public void Read_RoundTripsPhysicalFile_ByteForByte()
    {
        // Pick the largest file (unity_scene.lua, ~36 KB) — the most
        // likely to surface a stream truncation bug if one exists.
        if (!Directory.Exists(ScriptsDir))
            return; // tolerate the script being run from a published
                    // dist where ../../../../scripts isn't reachable.

        string physical = Path.Combine(ScriptsDir, "unity_scene.lua");
        if (!File.Exists(physical)) return;

        // Phase 22 live smoke (2026-05-12) caught CRLF in the working
        // copy of one file (unity_scene.lua). AOBMaker plugin's CE-side
        // InjectTableFile normalises CRLF→LF and verifies the post-write
        // Stream.Size against the pre-normalisation byte count, which
        // mismatches by exactly one byte per CRLF pair and aborts the
        // production inject pipeline. Asserting EXACT byte equality (no
        // slack) is the only way this test catches that regression.
        // .gitattributes (added in the same fix) enforces eol=lf for
        // *.lua, so the physical file should be LF-only too.
        byte[] fromDisk     = File.ReadAllBytes(physical);
        byte[] fromManifest = System.Text.Encoding.UTF8.GetBytes(
            BridgeLuaResources.Read("unity_scene.lua"));

        Assert.Equal(fromDisk.Length, fromManifest.Length);
        Assert.Equal(fromDisk, fromManifest);
        Assert.Contains("findByName",
            System.Text.Encoding.UTF8.GetString(fromManifest));
    }

    [Theory]
    [InlineData("utils.lua")]
    [InlineData("unity_dump.lua")]
    [InlineData("unity_dissect.lua")]
    [InlineData("unity_symbols.lua")]
    [InlineData("unity_addrfind.lua")]
    [InlineData("unity_invoke.lua")]
    [InlineData("unity_scene.lua")]
    [InlineData("mono_registersymbol_helpers.lua")]
    public void Read_EmbeddedContent_IsLfOnly_NoCrlf(string fileName)
    {
        // Hard regression bar for Phase 22 live smoke — every embedded
        // bridge lua MUST be LF-only. CRLF anywhere fails the AOBMaker
        // InjectTableFile verification (one byte per CRLF pair lost in
        // CE's StringStream normalisation; verification compares the
        // pre-normalisation byte count). Catching it at unit-test time
        // is much cheaper than catching it at the user's first inject
        // attempt.
        string content = BridgeLuaResources.Read(fileName);
        Assert.DoesNotContain("\r\n", content);
        // Also reject bare \r — would suggest a Mac-classic line ending
        // sneaked in (we've only ever seen \r\n in practice, but
        // defence-in-depth costs nothing here).
        Assert.DoesNotContain("\r", content);
    }

    [Fact]
    public void ReadAll_YieldsPairsInLoadOrder()
    {
        var pairs = BridgeLuaResources.ReadAll();
        Assert.Equal(BridgeLuaResources.LoadOrder.Count, pairs.Count);

        for (int i = 0; i < pairs.Count; ++i)
        {
            Assert.Equal(BridgeLuaResources.LoadOrder[i], pairs[i].FileName);
            Assert.False(string.IsNullOrEmpty(pairs[i].Content));
        }
    }

    [Fact]
    public void Read_RejectsUnknownFileName()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("some_other.lua"));
        Assert.Contains("not a known bridge lua file", ex.Message);
    }

    [Fact]
    public void Read_RejectsEmptyFileName()
    {
        Assert.Throws<ArgumentException>(() => BridgeLuaResources.Read(""));
    }

    [Fact]
    public void Read_IsCaseSensitive()
    {
        // LogicalName entries in csproj are case-sensitive; the API
        // surfaces that to callers explicitly via the ArgumentException.
        Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("Utils.lua"));
        Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("UNITY_DUMP.LUA"));
    }

    // ---------- ResolveFiles (feature-picker selection) -----------------

    [Fact]
    public void ResolveFiles_NoFeatures_ReturnsCoreOnly()
    {
        // Core is mandatory: an empty selection still injects the DLL +
        // pipe entry point. Nothing else.
        var files = BridgeLuaResources.ResolveFiles(
            Array.Empty<BridgeLuaResources.BridgeFeature>());
        Assert.Equal(new[] { "utils.lua", "unity_dump.lua" }, files);
    }

    [Fact]
    public void ResolveFiles_AlwaysIncludesCore_LeadingAndInOrder()
    {
        // Regardless of selection, utils.lua then unity_dump.lua lead.
        var files = BridgeLuaResources.ResolveFiles(
            new[] { BridgeLuaResources.BridgeFeature.Scene });
        Assert.Equal("utils.lua",      files[0]);
        Assert.Equal("unity_dump.lua", files[1]);
        Assert.Contains("unity_scene.lua", files);
        // Unrelated helpers are excluded.
        Assert.DoesNotContain("unity_invoke.lua", files);
        Assert.DoesNotContain("unity_dissect.lua", files);
    }

    [Fact]
    public void ResolveFiles_Symbols_PullsBothSymbolFiles()
    {
        // Symbols maps to unity_symbols.lua AND the DLL-free Mono helper.
        var files = BridgeLuaResources.ResolveFiles(
            new[] { BridgeLuaResources.BridgeFeature.Symbols });
        Assert.Contains("unity_symbols.lua",                 files);
        Assert.Contains("mono_registersymbol_helpers.lua",   files);
    }

    [Fact]
    public void ResolveFiles_OutputIsSubsequenceOfLoadOrder()
    {
        // ResolveFiles projects onto LoadOrder, so the result must be a
        // subsequence (same relative order) of it for ANY selection.
        var files = BridgeLuaResources.ResolveFiles(new[]
        {
            BridgeLuaResources.BridgeFeature.Scene,
            BridgeLuaResources.BridgeFeature.Dissect,   // deliberately out of LoadOrder order
        });
        int last = -1;
        foreach (var f in files)
        {
            int idx = BridgeLuaResources.LoadOrder.ToList().IndexOf(f);
            Assert.True(idx > last, $"{f} out of LoadOrder order");
            last = idx;
        }
    }

    [Fact]
    public void ResolveFiles_AllFeatures_EqualsFullLoadOrder_NoDuplicates()
    {
        // Selecting every feature must reconstruct the full LoadOrder
        // exactly — same files, same order, no dup from Symbols' two-file
        // mapping overlapping anything.
        var all = new[]
        {
            BridgeLuaResources.BridgeFeature.Dissect,
            BridgeLuaResources.BridgeFeature.Symbols,
            BridgeLuaResources.BridgeFeature.AddressFinder,
            BridgeLuaResources.BridgeFeature.Invoke,
            BridgeLuaResources.BridgeFeature.Scene,
        };
        var files = BridgeLuaResources.ResolveFiles(all);
        Assert.Equal(BridgeLuaResources.LoadOrder, files);
    }

    [Fact]
    public void ResolveFiles_NullSelection_TreatedAsEmpty()
    {
        var files = BridgeLuaResources.ResolveFiles(null!);
        Assert.Equal(new[] { "utils.lua", "unity_dump.lua" }, files);
    }
}
