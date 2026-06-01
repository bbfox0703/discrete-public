// BridgeLuaResourcesTests.cs — bridge lua resource contract.
//
// Action-driven model: the bridge embeds only the core (utils + unity_dump);
// the action-dependency helpers (unity_invoke / unity_symbols / mono helpers)
// are embedded on demand by the Send-to-CE-Table / Create-AA actions. All of
// them ship in the Core assembly manifest so Read() can serve them.
//
// Catches: a renamed script whose <LogicalName> wasn't updated; a file in
// LoadOrder missing its <EmbeddedResource> (and vice versa); LoadOrder drift;
// empty/truncated resources; CRLF (breaks AOBMaker InjectTableFile).

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
        // utils first (unity_dump requires it). Then the action-dependency
        // helpers the Send-to-CE-Table / Create-AA actions embed on demand.
        // UI-only lua (dissect/scene/addrfind) is intentionally NOT here —
        // those features run over the pipe from DiscreteUI, never from a CT.
        Assert.Equal(new[]
        {
            "utils.lua",
            "unity_dump.lua",
            "unity_invoke.lua",
            "unity_symbols.lua",
            "mono_registersymbol_helpers.lua",
        }, BridgeLuaResources.LoadOrder);
    }

    [Fact]
    public void CoreFiles_AreUtilsThenUnityDump()
    {
        // "Inject Discrete Bridge" embeds exactly these (DLL inject + pipe).
        Assert.Equal(new[] { "utils.lua", "unity_dump.lua" }, BridgeLuaResources.CoreFiles);
    }

    [Fact]
    public void ListEmbeddedNames_MatchesLoadOrder_OneForOne()
    {
        // The manifest's actual contents must equal the LoadOrder list mapped
        // through the LogicalName prefix. Catches "added a file to LoadOrder
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
    [InlineData("unity_invoke.lua")]
    [InlineData("unity_symbols.lua")]
    [InlineData("mono_registersymbol_helpers.lua")]
    public void Read_ReturnsNonEmptyContent_ForEveryBridgeFile(string fileName)
    {
        string content = BridgeLuaResources.Read(fileName);
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.StartsWith("-- =", content);
    }

    [Fact]
    public void Read_RoundTripsPhysicalFile_ByteForByte()
    {
        if (!Directory.Exists(ScriptsDir))
            return; // tolerate running from a published dist

        string physical = Path.Combine(ScriptsDir, "unity_invoke.lua");
        if (!File.Exists(physical)) return;

        // EXACT byte equality also guards against CRLF sneaking in (AOBMaker's
        // CE-side InjectTableFile verifies pre-normalisation byte count).
        byte[] fromDisk     = File.ReadAllBytes(physical);
        byte[] fromManifest = System.Text.Encoding.UTF8.GetBytes(
            BridgeLuaResources.Read("unity_invoke.lua"));

        Assert.Equal(fromDisk.Length, fromManifest.Length);
        Assert.Equal(fromDisk, fromManifest);
        Assert.Contains("callUnityMethod",
            System.Text.Encoding.UTF8.GetString(fromManifest));
    }

    [Theory]
    [InlineData("utils.lua")]
    [InlineData("unity_dump.lua")]
    [InlineData("unity_invoke.lua")]
    [InlineData("unity_symbols.lua")]
    [InlineData("mono_registersymbol_helpers.lua")]
    public void Read_EmbeddedContent_IsLfOnly_NoCrlf(string fileName)
    {
        string content = BridgeLuaResources.Read(fileName);
        Assert.DoesNotContain("\r\n", content);
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
    public void ReadAll_Subset_YieldsRequestedFiles()
    {
        var pairs = BridgeLuaResources.ReadAll(BridgeLuaResources.CoreFiles);
        Assert.Equal(2, pairs.Count);
        Assert.Equal("utils.lua",      pairs[0].FileName);
        Assert.Equal("unity_dump.lua", pairs[1].FileName);
    }

    [Fact]
    public void Read_RejectsUnknownFileName()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("some_other.lua"));
        Assert.Contains("not a known bridge lua file", ex.Message);
    }

    [Theory]
    [InlineData("unity_dissect.lua")]
    [InlineData("unity_addrfind.lua")]
    [InlineData("unity_scene.lua")]
    public void Read_RejectsRemovedUiOnlyFiles(string fileName)
    {
        // These were dropped from the bridge (UI-only — run over the pipe).
        Assert.Throws<ArgumentException>(() => BridgeLuaResources.Read(fileName));
    }

    [Fact]
    public void Read_RejectsEmptyFileName()
    {
        Assert.Throws<ArgumentException>(() => BridgeLuaResources.Read(""));
    }

    [Fact]
    public void Read_IsCaseSensitive()
    {
        Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("Utils.lua"));
        Assert.Throws<ArgumentException>(
            () => BridgeLuaResources.Read("UNITY_DUMP.LUA"));
    }
}
