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
    // Walk up from the test output dir to the repo's scripts\ folder, so the
    // depth of bin\<platform?>\<config>\<tfm>\<rid>\ does not matter.
    private static string? FindScriptsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "scripts");
            if (File.Exists(Path.Combine(candidate, "unity_invoke.lua")))
                return candidate;
        }
        return null;
    }

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
        string? scriptsDir = FindScriptsDir();
        if (scriptsDir is null)
            Assert.Fail(@"scripts\unity_invoke.lua not found above " + AppContext.BaseDirectory);

        string physical = Path.Combine(scriptsDir!, "unity_invoke.lua");

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

    [Fact]
    public void CeTableBootstrap_ChecksInjectDllAsBoolean_AndMatchesUtilsBackendDetection()
    {
        // E-M3 + E-L9 regression test for scripts/ce_table_bootstrap.lua (Mode-B CT template):
        // - Must NOT check `injectDLL(...) == nil` (CE returns boolean `false` on failure).
        // - Must use basename extraction + full IL2CPP/Mono module list matching utils.lua.
        // - Must reset DISCRETE_HAD_ERROR on bootstrap() and teardown() entry.
        // - Must stay LF-only (pre-commit hook / CE TableFile safety).
        string? scriptsDir = FindScriptsDir();
        if (scriptsDir is null)
            Assert.Fail(@"scripts\ directory not found above " + AppContext.BaseDirectory);

        string content = File.ReadAllText(Path.Combine(scriptsDir!, "ce_table_bootstrap.lua"));

        Assert.DoesNotContain("\r", content);
        Assert.DoesNotContain("injectDLL(dllPath) == nil", content);
        Assert.Contains("if not injectDLL(dllPath) then", content);

        // Backend detection parity with utils.lua M.detectBackend()
        Assert.Contains("userassembly.dll", content);
        Assert.Contains("mono-2.0-sgen.dll", content);
        Assert.Contains("mono-2.0-boehm.dll", content);
        Assert.Contains("reinitializeSymbolhandler", content);
        Assert.Contains("raw:match(\"([^\\\\/]+)$\")", content);

        // E-L9: both public entry points reset DISCRETE_HAD_ERROR; teardown does not call _ensureDir
        int bootstrapIdx = content.IndexOf("function bootstrap()", StringComparison.Ordinal);
        int teardownIdx  = content.IndexOf("function teardown()",  StringComparison.Ordinal);
        Assert.True(bootstrapIdx >= 0 && teardownIdx > bootstrapIdx);

        string bootstrapBody = content.Substring(bootstrapIdx, teardownIdx - bootstrapIdx);
        string teardownBody  = content.Substring(teardownIdx);
        Assert.Contains("rawset(_G, \"DISCRETE_HAD_ERROR\", false)", bootstrapBody);
        Assert.Contains("rawset(_G, \"DISCRETE_HAD_ERROR\", false)", teardownBody);
        Assert.DoesNotContain("_ensureDir(", teardownBody);
    }

    [Fact]
    public void MonoRegisterSymbolHelpers_HasPrimitiveAliases_PositionalSigSplit_AndAmbiguityReject()
    {
        // E-M5 regression check on the embedded mono_registersymbol_helpers.lua:
        // - Primitive alias table maps Mono shorthands (int/single/string/bool) to System.*.
        // - Positional signature parser splits parameter lists instead of unordered substring matching.
        // - Both discrete_mono_registerSymbolEx and discrete_mono_registerSymbolBySignatureMatch
        //   reject ambiguous multi-overload hits via eprint rather than silently picking the first.
        string content = BridgeLuaResources.Read("mono_registersymbol_helpers.lua");

        Assert.DoesNotContain("\r", content);
        Assert.Contains("_DISCRETE_PRIMITIVE_ALIASES", content);
        Assert.Contains("[\"int\"]", content);
        Assert.Contains("[\"single\"]", content);
        Assert.Contains("[\"string\"]", content);
        Assert.Contains("_discrete_split_sig_params", content);
        Assert.Contains("ambiguous overload match", content);
        Assert.Contains("ambiguous signature match", content);
    }

    [Theory]
    [InlineData("utils.lua")]
    [InlineData("unity_symbols.lua")]
    [InlineData("unity_invoke.lua")]
    public void PipeRpcScripts_UseBoundedTimeout_ConnectToPipe_AndWorkerThreadPump(string fileName)
    {
        // E-M4 regression check: CE Lua bridge pipe RPC must never block CE's GUI
        // thread without a finite timeout. Verify all three pipe-speaking scripts
        // use CE's connectToPipe (native overlapped timeout + OnTimeout + destroy),
        // createThread + pump polling, and explicit timeout errors.
        string content = BridgeLuaResources.Read(fileName);

        Assert.DoesNotContain("\r", content);
        Assert.Contains("connectToPipe", content);
        Assert.Contains("createThread", content);
        Assert.Contains("OnTimeout", content);
        Assert.Contains("_closePipeOnce", content);
        Assert.Contains("p.destroy()", content);
        Assert.Contains("pipe RPC timed out after", content);
        Assert.Contains("kDefaultRpcTimeoutMs", content);
    }

    [Fact]
    public void UnityInvoke_ScalesRpcTimeout_WithMainThreadTimeoutMs()
    {
        // E-M4: callMethod with main_thread=true must grant mainThreadTimeoutMs + headroom
        // (and clamp main_thread_timeout_ms to [1, 60000]) so legitimate main-thread
        // waits don't spuriously time out on the Lua side while stalled games still abort.
        string content = BridgeLuaResources.Read("unity_invoke.lua");

        Assert.Contains("opts.rpcTimeoutMs", content);
        Assert.Contains("if mt < 1 then mt = 1 elseif mt > 60000 then mt = 60000 end", content);
        Assert.Contains("math.max(kDefaultRpcTimeoutMs, (mt or 5000) + 5000)", content);
        Assert.Contains("rpc(\"callMethod\", body, rpcTimeoutMs)", content);
    }
}
