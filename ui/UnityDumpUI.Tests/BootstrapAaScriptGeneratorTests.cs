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
    public void Build_FileListMatchesBridgeLuaResourcesCoreFiles()
    {
        // The production bootstrap loads ONLY the core (utils + unity_dump =
        // inject DLL + pipe). Action helpers are embedded + self-loaded by
        // their own generated AAs, not the bootstrap.
        string script = BootstrapAaScriptGenerator.Build();
        foreach (var name in BridgeLuaResources.CoreFiles)
            Assert.Contains($"'{name}'", script);

        int listStart = script.IndexOf("local BRIDGE_FILES = {", StringComparison.Ordinal);
        Assert.True(listStart > 0);
        int listEnd = script.IndexOf('}', listStart);
        Assert.True(listEnd > listStart);
        string listLiteral = script.Substring(listStart, listEnd - listStart);

        int luaTokens = System.Text.RegularExpressions.Regex
            .Matches(listLiteral, @"'[^']+\.lua'").Count;
        Assert.Equal(BridgeLuaResources.CoreFiles.Count, luaTokens);
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
    public void Build_SmokeChecksDllViaPipeHandshake()
    {
        // The smoke at the end of [ENABLE] catches "DLL not injected /
        // pipe not up" — the most common runtime failure on a fresh
        // table. Originally used U.callDLL('Discrete_GetBuildNumber',
        // 'uint32') (CE's executeCodeEx via remote thread in target).
        // That path is finicky on Windows x64 — returns nil even when
        // the DLL is fully loaded + every subsequent pipe-RPC works,
        // so the smoke spammed a "DLL not yet reachable" false negative
        // on every successful bridge load (Phase 22f live smoke
        // surfaced 2026-05-12).
        //
        // Replaced with a pipe-based handshake — same path the bridge's
        // registerMethodSymbol / callUnityMethod / whatIsAt use. If
        // those work the smoke passes; if those don't work the smoke
        // fails honestly. No more false negatives.
        string script = BootstrapAaScriptGenerator.Build();
        Assert.Contains("\\\\.\\pipe\\DiscreteBfx",    script);
        Assert.Contains("\"cmd\":\"handshake\"",        script);
        Assert.Contains("\"build\"",                    script);
        Assert.Contains("\"backend\"",                  script);
        // Old failure-mode strings MUST be gone — they were the source
        // of the false-negative noise.
        Assert.DoesNotContain("Discrete_GetBuildNumber", script);
        Assert.DoesNotContain("U.callDLL",                   script);
        Assert.DoesNotContain("executeCodeEx",               script);
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

    [Fact]
    public void Build_Disable_CallsDiscreteShutdown_ForDormantTeardown()
    {
        // The [DISABLE] block must trigger the dormant teardown (stop pipe +
        // remove hooks) via discreteShutdown(), not just clear the flag.
        // Guarded so an old CT without the global doesn't error.
        string script = BootstrapAaScriptGenerator.Build();
        int disableIdx = script.IndexOf("[DISABLE]", StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        string disableBlock = script.Substring(disableIdx);
        Assert.Contains("discreteShutdown", disableBlock);
        Assert.Contains("_G.discrete_bridgeLoaded = nil", disableBlock);
    }

    [Fact]
    public void Build_EmitsHadErrorReset_AndCloseLuaEngineTail_InBothBlocks()
    {
        // docs/lua-logging.md: both [ENABLE] and [DISABLE] {$lua} blocks reset
        // the shared did-anything-fail flag after the syntaxcheck guard and
        // close CE's Lua Engine on a clean run. Smoke failures + missing
        // discreteShutdown flag the error so the window stays up.
        string script = BootstrapAaScriptGenerator.Build();

        // Reset appears in BOTH blocks (ENABLE + DISABLE each run Lua statements).
        int resetCount = CountOccurrences(script, "_G.DISCRETE_HAD_ERROR = false");
        Assert.Equal(2, resetCount);

        // Close-tail appears in BOTH blocks.
        int tailCount = CountOccurrences(script,
            "synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)");
        Assert.Equal(2, tailCount);
        Assert.Contains("if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then", script);

        // Failure paths flag the error (pipe unreachable / handshake unparseable /
        // discreteShutdown unavailable).
        Assert.Contains("_G.DISCRETE_HAD_ERROR = true", script);

        // Close-tail in [ENABLE] runs after the bridge-loaded flag is set.
        int enableTail = script.IndexOf("if e then e.Close() end", StringComparison.Ordinal);
        int flagSet    = script.IndexOf("_G.discrete_bridgeLoaded = true", StringComparison.Ordinal);
        Assert.True(flagSet >= 0 && enableTail > flagSet);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    [Fact]
    public void Build_HasNo_AdjacentSingleQuotes_LuaSyntaxTrap()
    {
        // Phase 22f live smoke regression (2026-05-12): the soft-fail
        // print on the symbolhandler-lag path was written as
        //   print('[Discrete]   wait for CE''s symbolhandler ...')
        // — C#-style apostrophe doubling. Lua reads `''` as
        //   "close string, immediately open new string"
        // and bails with `')' expected near "s symbolhandler..."`,
        // which fails the ENTIRE script parse, so the bridge never
        // loads at all (Lua chunks are parse-then-execute — line-184
        // syntax error means nothing executes).
        //
        // The bug shape is specifically `word''word` — `''` surrounded
        // by alphanumerics, as in CE''s. A bare `''` substring can
        // legitimately appear in generated scripts (empty namespace
        // arg between commas, e.g. `, '', ` from CallMethod with no
        // namespace) — those are valid empty Lua strings. The regex
        // below threads that needle.
        string script = BootstrapAaScriptGenerator.Build();
        Assert.DoesNotMatch(@"\w''\w", script);
    }

    [Fact]
    public void Build_SmokeHandshake_UsesBoundedTimeoutPipeRpcRawUnderPcall()
    {
        // E-M4: bootstrap smoke handshake must go through U.pipeRpcRaw with a
        // finite timeout (5000 ms) wrapped in pcall so a stalled DLL cannot freeze
        // CE's GUI thread on table load.
        string script = BootstrapAaScriptGenerator.Build();

        Assert.Contains("pcall(require, 'utils')", script);
        Assert.Contains("pcall(U.pipeRpcRaw,", script);
        Assert.Contains(", 5000)", script);
    }

    [Fact]
    public void Build_Disable_ChecksDiscreteShutdownReturnAndFlagsHadError()
    {
        // E-L8: [DISABLE] must check pcall(discreteShutdown)'s ok and return value
        // and set _G.DISCRETE_HAD_ERROR = true on failure so Lua Engine stays open.
        string script = BootstrapAaScriptGenerator.Build();
        int disableIdx = script.IndexOf("[DISABLE]", StringComparison.Ordinal);
        string disableBlock = script.Substring(disableIdx);

        Assert.Contains("local ok, res = pcall(discreteShutdown)", disableBlock);
        Assert.Contains("if not ok or res == false then", disableBlock);
        Assert.Contains("discreteShutdown() failed", disableBlock);
    }
}
