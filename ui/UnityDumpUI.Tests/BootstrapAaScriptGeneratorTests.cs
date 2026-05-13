// BootstrapAaScriptGeneratorTests.cs — Phase 22c verification.
//
// Pin the structural contract of the generated AA script. Tests the
// production overload (hooked to BridgeLuaResources.LoadOrder) +
// the internal test-only overload (custom file lists).
//
// We deliberately do NOT spin up a Lua interpreter to evaluate the
// script — the round-trip is exercised live by Phase 22f's E2E smoke
// in CE 7.6. Here we only verify shape + substitution + idempotency
// guards + the must-have lua API calls (findTableFile, package.preload,
// load) appear in the right order and aren't accidentally typoed.

using System;
using System.Linq;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class BootstrapAaScriptGeneratorTests
{
    [Fact]
    public void Build_HasEnableAndDisableBlocks()
    {
        string script = BootstrapAaScriptGenerator.Build();

        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);
        // ENABLE must come before DISABLE — CE parses sequentially.
        Assert.True(script.IndexOf("[ENABLE]", StringComparison.Ordinal)
                  < script.IndexOf("[DISABLE]", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_GuardsIdempotency_OnReEnable()
    {
        string script = BootstrapAaScriptGenerator.Build();
        // Bare-minimum: the early-return guard must be present in
        // [ENABLE], and the flag must be cleared in [DISABLE].
        Assert.Contains("if _G.discrete_bridgeLoaded then", script);
        Assert.Contains("_G.discrete_bridgeLoaded = true",  script);
        Assert.Contains("_G.discrete_bridgeLoaded = nil",   script);
    }

    [Fact]
    public void Build_PreloadsUtilsBeforeOtherFiles()
    {
        string script = BootstrapAaScriptGenerator.Build();

        // The preload trick is load-bearing: unity_dump.lua's
        // `require 'utils'` would otherwise hit package.path (which
        // doesn't include our table files) and fail.
        Assert.Contains("package.preload['utils']", script);

        // Preload must happen BEFORE the for-loop that runs each
        // remaining file (otherwise unity_dump.lua's require would
        // fail on the first iteration).
        int preloadIdx = script.IndexOf("package.preload['utils']", StringComparison.Ordinal);
        int loopIdx    = script.IndexOf("for _, name in ipairs(BRIDGE_FILES)", StringComparison.Ordinal);
        Assert.True(preloadIdx > 0);
        Assert.True(loopIdx    > 0);
        Assert.True(preloadIdx < loopIdx);

        // The loop skips utils.lua — otherwise we'd run the chunk twice
        // (once for preload, once via the loop's load+exec).
        Assert.Contains("if name ~= 'utils.lua' then", script);
    }

    [Fact]
    public void Build_UsesFindTableFileAndStreamCopyFrom()
    {
        // CE's documented pattern for reading a TableFile back out is
        // findTableFile + Stream.copyFrom(createStringStream) +
        // Stream.DataString. Direct findTableFile():getStream():read()
        // works on some CE builds but not others — the copyFrom dance
        // is the portable form.
        string script = BootstrapAaScriptGenerator.Build();
        Assert.Contains("findTableFile",        script);
        Assert.Contains("createStringStream",   script);
        Assert.Contains("ss.copyFrom",          script);
        Assert.Contains("ss.DataString",        script);
        Assert.Contains("ss.destroy",           script);
    }

    [Fact]
    public void Build_FileListMatchesBridgeLuaResourcesLoadOrder()
    {
        // The lua-side BRIDGE_FILES literal must contain exactly the
        // 7 filenames BridgeLuaResources.LoadOrder advertises. Catches
        // "added a file to LoadOrder but the bootstrap won't load it"
        // (and vice versa).
        string script = BootstrapAaScriptGenerator.Build();
        foreach (var name in BridgeLuaResources.LoadOrder)
            Assert.Contains($"'{name}'", script);

        // No surprise extras — the literal should be exactly the LoadOrder.
        // Crude but effective check: count single-quoted *.lua tokens
        // inside the BRIDGE_FILES line.
        int listStart = script.IndexOf("local BRIDGE_FILES = {", StringComparison.Ordinal);
        Assert.True(listStart > 0);
        int listEnd = script.IndexOf('}', listStart);
        Assert.True(listEnd > listStart);
        string listLiteral = script.Substring(listStart, listEnd - listStart);

        int luaTokens = System.Text.RegularExpressions.Regex
            .Matches(listLiteral, @"'[^']+\.lua'").Count;
        Assert.Equal(BridgeLuaResources.LoadOrder.Count, luaTokens);
    }

    [Fact]
    public void Build_SubstitutesEntryDescriptionInLogLines()
    {
        string script = BootstrapAaScriptGenerator.Build();
        // The description appears in 3 print() lines (success, soft-warn,
        // teardown). Verify it actually got substituted (no stray
        // __ENTRY_DESCRIPTION__ placeholder left behind).
        Assert.DoesNotContain("__ENTRY_DESCRIPTION__", script);
        Assert.DoesNotContain("__BRIDGE_FILES_LIST__", script);
        Assert.Contains(BootstrapAaScriptGenerator.EntryDescription, script);
    }

    [Fact]
    public void Build_SmokeChecksDllBuildNumber()
    {
        // The smoke at the end of [ENABLE] catches "DLL not injected"
        // — the most common runtime failure on a fresh table. Must
        // call UnityDumper_GetBuildNumber via U.callDLL (not directly,
        // which would skip the executeCodeEx safety wrapper).
        string script = BootstrapAaScriptGenerator.Build();
        Assert.Contains("UnityDumper_GetBuildNumber", script);
        Assert.Contains("U.callDLL",                   script);
        Assert.Contains("require('utils')",            script);
    }

    [Fact]
    public void Build_RejectsMissingUtilsInList()
    {
        // The preload trick requires utils.lua. Without it, unity_dump.lua's
        // require would silently fall through to package.path and fail —
        // worth surfacing as a build-time error, not a runtime mystery.
        var ex = Assert.Throws<ArgumentException>(
            () => BootstrapAaScriptGenerator.Build(new[] { "unity_dump.lua" }));
        Assert.Contains("utils.lua", ex.Message);
    }

    [Fact]
    public void Build_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(
            () => BootstrapAaScriptGenerator.Build(Array.Empty<string>()));
    }

    [Fact]
    public void Build_TestOverload_RoundTripsCustomList()
    {
        // The test-only overload lets us verify substitution mechanics
        // without depending on the production LoadOrder shape.
        string script = BootstrapAaScriptGenerator.Build(new[] { "utils.lua", "unity_dump.lua" });

        Assert.Contains("'utils.lua'",      script);
        Assert.Contains("'unity_dump.lua'", script);
        // Production-only files must NOT appear in this build.
        Assert.DoesNotContain("'unity_scene.lua'",  script);
        Assert.DoesNotContain("'unity_invoke.lua'", script);
    }

    [Fact]
    public void EntryDescription_IsStable()
    {
        // Description is what AOBMaker's CreateAAScript will use as the
        // memory-record description. Keep it stable so future
        // duplicate-detection (when AOBMaker gains it) keys correctly
        // and so users can recognise the entry across CT versions.
        Assert.Equal("Load Discrete Bridge", BootstrapAaScriptGenerator.EntryDescription);
    }
}
