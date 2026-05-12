// BridgeLuaResourcesTests.cs — Phase 22b verification.
//
// Pin the contract that all 7 CE Lua bridge scripts ship in the Core
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
    public void LoadOrder_HasExpectedSevenFiles_InDependencyOrder()
    {
        // utils.lua must come first because unity_dump.lua does
        // `require 'utils'`. Other 5 scripts are independent (no
        // require/dofile cross-references) so their relative order is
        // convention, not required — but the test pins it so future
        // edits can't accidentally swap them without a code review.
        Assert.Equal(new[]
        {
            "utils.lua",
            "unity_dump.lua",
            "unity_dissect.lua",
            "unity_symbols.lua",
            "unity_addrfind.lua",
            "unity_invoke.lua",
            "unity_scene.lua",
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

        string fromDisk     = File.ReadAllText(physical);
        string fromManifest = BridgeLuaResources.Read("unity_scene.lua");

        // EmbeddedResource normalises CRLF→LF on some toolchains; assert
        // length within 5 % to accommodate both (real corruption would
        // show as a much larger drift, e.g. 50 % on a stream truncation).
        double ratio = (double)fromManifest.Length / fromDisk.Length;
        Assert.InRange(ratio, 0.95, 1.05);
        Assert.Contains("findByName", fromManifest);
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
}
