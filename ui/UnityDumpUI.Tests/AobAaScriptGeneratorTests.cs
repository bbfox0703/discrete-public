// AobAaScriptGeneratorTests.cs — regression coverage for the Phase 18c
// generator, hardened mid-Phase-22f-live-smoke after a P1 surfaced
// against BongoCat (Mono):
//
//   The original codepath always emitted `aobscanmodule(name, MODULE,
//   pattern)`. For Mono JIT methods the runtime VirtualAllocs each
//   compiled method's code page and getMethodAobPattern returns an
//   empty ModuleName — the generated AA script became
//   `aobscanmodule(name, , pattern)`, which CE accepts syntactically
//   but never matches, so the symbol stays unresolved and the user
//   sees `[discrete] <symbol> NOT FOUND (AOB scan failed)`.
//
// Fix: detect empty ModuleName and emit `aobscan(name, pattern)`
// instead. Tests below pin both the IL2CPP-style (module present)
// and the Mono-style (module empty) outputs.

using System;
using System.Collections.Generic;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class AobAaScriptGeneratorTests
{
    private static AaScriptOverload Il2cppOverload() => new()
    {
        MethodName       = "Abs",
        ClassName        = "Mathf",
        Namespace        = "UnityEngine",
        HumanSuffix      = "int32",
        Signature        = "(System.Int32)",
        Pattern          = "48 8B 05 ?? ?? ?? ?? 48 89 ?? ??",
        ModuleName       = "GameAssembly.dll",
        Address          = "0x00007FF876123450",
        ModuleOffset     = "0x00000000B23450",
        ReturnType       = "System.Int32",
        DecoderBailed    = false,
        InstructionCount = 4,
        WildcardCount    = 7,
    };

    private static AaScriptOverload MonoJitOverload() => new()
    {
        MethodName       = "Abs",
        ClassName        = "Mathf",
        Namespace        = "UnityEngine",
        Assembly         = "UnityEngine.CoreModule",  // Phase 22f follow-up: needed for bridge-call AA
        HumanSuffix      = "int32",
        Signature        = "(System.Int32)",
        Pattern          = "55 48 8B EC 48 83 EC 30 48 89 75 F8 48 8B F1 85 F6",
        // Mono JIT: ModuleName empty (JIT VirtualAllocs code pages; no
        // .dll module owns the address). Two fix iterations:
        //  - First fix (commit 90...): emit aobscan() instead of
        //    aobscanmodule(name, , ...). Worked for unique patterns
        //    but failed for multi-instance JIT (BongoCat live smoke).
        //  - Second fix (this commit): switch entire Mono path to
        //    bridge-call AA via registerMethodSymbol — DLL-side
        //    mono_compile_method is overload-precise.
        ModuleName       = "",
        Address          = "0x0000018C5C470E30",
        ModuleOffset     = "0x0000000000000000",
        ReturnType       = "System.Int32",
        DecoderBailed    = false,
        InstructionCount = 6,
        WildcardCount    = 0,
    };

    [Fact]
    public void Build_Il2cppOverload_UsesAobscanmodule()
    {
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload() });

        // Module present → use aobscanmodule with the module name. (This AOB
        // form is the DLL-free fallback; the relaunch+update-safe primary is
        // the signature-match register path — universal across Mono / IL2CPP.)
        Assert.Contains("aobscanmodule(Mathf_Abs_int32, GameAssembly.dll, 48 8B 05", s);
        Assert.DoesNotContain("aobscan(Mathf_Abs_int32,", s); // NOT the module-less form
        Assert.Contains("registersymbol(Mathf_Abs_int32)", s);

        // Trailing comment carries module + offset for cross-reference.
        Assert.Contains("(GameAssembly.dll+0x00000000B23450)", s);
    }

    [Fact]
    public void Build_MonoJitOverload_UsesBridgeCall_NotAobscan()
    {
        // Phase 22f live-smoke regression (second iteration). Surfaced
        // 2026-05-12 against BongoCat. First fix replaced
        // `aobscanmodule(name, , pattern)` with `aobscan(name, pattern)`
        // — worked for unique patterns but failed for multi-instance
        // JIT (Mono routinely compiles the same source method N times
        // with identical prologues). Real fix: switch the entire Mono
        // path to a bridge-call AA — DLL's mono_compile_method is
        // overload-precise.
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        // Mono JIT path must NOT emit ANY aobscan/aobscanmodule for the
        // overload — those are inherently unstable for JIT'd code.
        Assert.DoesNotContain("aobscan(Mathf_Abs_int32",       s);
        Assert.DoesNotContain("aobscanmodule(Mathf_Abs_int32", s);

        // Must call the bridge with the canonical arg order:
        //   registerMethodSymbol(symName, asm, ns, cls, method, sigFilter)
        Assert.Contains(
            "registerMethodSymbol('Mathf_Abs_int32', 'UnityEngine.CoreModule', 'UnityEngine', 'Mathf', 'Abs', '(System.Int32)')",
            s);

        // Action-driven: the Mono-JIT AA self-loads unity_symbols.lua from the
        // CT (guarded on registerMethodSymbol) instead of relying on the
        // bootstrap to pre-load it.
        Assert.Contains("if _G.registerMethodSymbol == nil then", s);
        Assert.Contains("findTableFile('unity_symbols.lua')", s);

        // Bridge-loaded guard required — without the bridge,
        // registerMethodSymbol is undefined and the entry would
        // silently no-op (worse: misleading "OK" status from CE).
        Assert.Contains("if not _G.discrete_bridgeLoaded then", s);
        Assert.Contains("Load Discrete Bridge", s);

        // Trailing comment annotates "Mono JIT — resolved via bridge".
        Assert.Contains("Mono JIT — resolved via bridge", s);
        // And NOT the misleading "(+0x000…)" form.
        Assert.DoesNotContain("(+0x", s);
    }

    [Fact]
    public void Build_MonoJitOverload_DisableUnregistersSymbolViaLua()
    {
        // [DISABLE] for bridge-registered symbols uses Lua's
        // unregisterSymbol (the symbol was set via registerSymbol from
        // the bridge, not via {$asm} aobscanmodule). pcall guards
        // against the case where the symbol was never registered (e.g.
        // [ENABLE] aborted on the bridge-loaded guard).
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        Assert.Contains("[DISABLE]", s);
        int disableIdx = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        string disableBlock = s.Substring(disableIdx);
        Assert.Contains("pcall(unregisterSymbol, 'Mathf_Abs_int32')", disableBlock);
        // The {$asm} unregistersymbol() directive is for AOB path only.
        Assert.DoesNotContain("unregistersymbol(Mathf_Abs_int32)", disableBlock);
    }

    [Fact]
    public void Build_MixedBatch_IL2cppKeepsAobMonoUsesBridge()
    {
        // Realistic batch: one IL2CPP + one Mono-style overload (this
        // doesn't happen in practice on a single-backend target, but
        // pins that the per-overload branching is per-overload).
        var monoVariant = new AaScriptOverload
        {
            MethodName       = "Sign",
            ClassName        = "Mathf",
            Namespace        = "UnityEngine",
            Assembly         = "UnityEngine.CoreModule",
            HumanSuffix      = "int32_2",
            Signature        = "(System.Int32)",
            Pattern          = "55 48 8B EC 48 83 EC 30 48 89 75 F8 48 8B F1 85 F6",
            ModuleName       = "",  // empty = Mono JIT
            Address          = "0x0000018C5C470F30",
            ModuleOffset     = "0x0000000000000000",
            ReturnType       = "System.Int32",
        };
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload(), monoVariant });

        // IL2CPP overload keeps the AOB form.
        Assert.Contains("aobscanmodule(Mathf_Abs_int32, GameAssembly.dll,", s);
        // Mono overload uses the bridge call.
        Assert.Contains(
            "registerMethodSymbol('Mathf_Sign_int32_2', 'UnityEngine.CoreModule', 'UnityEngine', 'Mathf', 'Sign', '(System.Int32)')",
            s);
        // No accidental aobscan on the Mono one.
        Assert.DoesNotContain("aobscan(Mathf_Sign_int32_2",       s);
        Assert.DoesNotContain("aobscanmodule(Mathf_Sign_int32_2", s);
    }

    [Fact]
    public void Build_BridgeHelperBlock_PresentRegardlessOfModule()
    {
        // The shared discrete_logScan helper at the top of [ENABLE]
        // doesn't depend on which scan form is used — it just reads
        // the registered symbol. Both paths should still wire it.
        string monoScript   = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });
        string il2cppScript = AobAaScriptGenerator.Build(new[] { Il2cppOverload() });

        foreach (var script in new[] { monoScript, il2cppScript })
        {
            Assert.Contains("if _G.discrete_logScan == nil then", script);
            Assert.Contains("_G.discrete_logScan(\"Mathf_Abs_int32\")", script);
        }
    }

    [Fact]
    public void Build_LogScan_DeferredAfterAsmPass()
    {
        // Regression: CE runs {$lua} blocks during the assemble phase, BEFORE
        // the {$asm} aobscanmodule/registersymbol directives execute, so an
        // inline `discrete_logScan` could not see the freshly-registered symbol
        // and printed "NOT FOUND (AOB scan failed)" even though the scan worked.
        // The confirmation must run inside a deferred one-shot timer.
        string s = AobAaScriptGenerator.Build(new[] { UniqueIl2cppOverload() });

        // The deferral helper is defined idempotently.
        Assert.Contains("if _G.discrete_runAfterAsm == nil then", s);
        Assert.Contains("createTimer(nil)", s);

        // The logScan call is wrapped in the deferred callback, not emitted
        // inline at the top level of the post-asm {$lua} block.
        Assert.Contains("_G.discrete_runAfterAsm(function()", s);
        int deferIdx = s.IndexOf("_G.discrete_runAfterAsm(function()", StringComparison.Ordinal);
        int logIdx   = s.IndexOf("_G.discrete_logScan(\"Mathf_Abs_int32\")", StringComparison.Ordinal);
        Assert.True(deferIdx >= 0 && logIdx > deferIdx,
            "logScan must appear inside the deferred runAfterAsm callback");

        // The post-asm {$lua} block guards against the syntax-check pass so the
        // timer is never scheduled during a syntax check. (Two guards total now:
        // the helper block + this one.)
        int firstAsm = s.IndexOf("{$asm}", StringComparison.Ordinal);
        string afterAsm = s.Substring(firstAsm);
        Assert.Contains("if syntaxcheck then return end", afterAsm);
    }

    [Fact]
    public void Build_OffsetRegister_DeferredAfterAsmPass()
    {
        // The offset method-entry registration depends on the aobscanmodule
        // anchor, which the asm pass registers — so it must ALSO run in the
        // deferred callback, not inline (where the anchor isn't resolvable yet).
        string s = AobAaScriptGenerator.Build(new[] { UniqueIl2cppOverload(injectionOffset: 5) });

        int deferIdx  = s.IndexOf("_G.discrete_runAfterAsm(function()", StringComparison.Ordinal);
        int regIdx    = s.IndexOf("registerSymbol(\"Mathf_Abs_int32\", b + 5)", StringComparison.Ordinal);
        Assert.True(deferIdx >= 0 && regIdx > deferIdx,
            "offset registerSymbol must appear inside the deferred runAfterAsm callback");
    }

    // ---- AOBMaker-app unique-AOB integration (front warning + offset) -------

    private static AaScriptOverload UniqueIl2cppOverload(int injectionOffset = 0) => new()
    {
        MethodName         = "Abs",
        ClassName          = "Mathf",
        Namespace          = "UnityEngine",
        HumanSuffix        = "int32",
        Signature          = "(System.Int32)",
        Pattern            = "48 89 5C 24 08 57 48 83 EC 20 48 8B 05 ?? ?? ?? ??",
        ModuleName         = "GameAssembly.dll",
        Address            = "0x00007FF876123450",
        ModuleOffset       = "0x00000000B23450",
        ReturnType         = "System.Int32",
        AobIsUnique        = true,
        AobInjectionOffset = injectionOffset,
    };

    [Fact]
    public void Build_NonUniqueIl2cpp_EmitsFrontWarning()
    {
        // Default Il2cppOverload carries the DLL method-prologue pattern
        // (AobIsUnique=false) → the script must warn loudly, right after the
        // syntaxcheck guard, that the AOB may not be unique.
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload() });

        Assert.Contains("WARNING:", s);
        Assert.Contains("NOT guaranteed unique", s);
        Assert.DoesNotContain("verified unique", s);
        // The warning sits immediately after the guard (before the helper comment).
        int guard   = s.IndexOf("if syntaxcheck then return end", StringComparison.Ordinal);
        int warning = s.IndexOf("WARNING:", StringComparison.Ordinal);
        int helper  = s.IndexOf("discrete: AA helper", StringComparison.Ordinal);
        Assert.True(guard < warning && warning < helper);
    }

    [Fact]
    public void Build_UniqueIl2cpp_EmitsUniqueNote_NoWarning()
    {
        // App-generated unique AOB → confirmation note, no scary warning.
        string s = AobAaScriptGenerator.Build(new[] { UniqueIl2cppOverload() });

        Assert.Contains("verified unique", s);
        Assert.DoesNotContain("WARNING:", s);
        // Offset 0 → match start IS the entry: simple aobscanmodule + registersymbol.
        Assert.Contains("aobscanmodule(Mathf_Abs_int32, GameAssembly.dll,", s);
        Assert.Contains("registersymbol(Mathf_Abs_int32)", s);
        Assert.DoesNotContain("Mathf_Abs_int32_aob", s);
    }

    [Fact]
    public void Build_UniqueWithInjectionOffset_RegistersEntryAtAnchorPlusOffset()
    {
        // Mode D extended the pattern backwards (offset 5): aobscan into an
        // anchor, then register the method-entry symbol at anchor+5 in {$lua}.
        string s = AobAaScriptGenerator.Build(new[] { UniqueIl2cppOverload(injectionOffset: 5) });

        Assert.Contains("aobscanmodule(Mathf_Abs_int32_aob, GameAssembly.dll,", s);
        Assert.Contains("registersymbol(Mathf_Abs_int32_aob)", s);
        Assert.Contains("getAddressSafe(\"Mathf_Abs_int32_aob\")", s);
        Assert.Contains("registerSymbol(\"Mathf_Abs_int32\", b + 5)", s);
        // DISABLE tears down both the anchor (asm) and the entry symbol (lua).
        Assert.Contains("unregistersymbol(Mathf_Abs_int32_aob)", s);
        Assert.Contains("pcall(unregisterSymbol, 'Mathf_Abs_int32')", s);
    }

    [Fact]
    public void Build_EmitsHadErrorReset_AndDeferredCloseLuaEngineTail_PerLuaLogging()
    {
        // docs/lua-logging.md: the first [ENABLE] {$lua} resets the shared flag
        // after the syntaxcheck guard. Because the AOB scan result isn't known
        // until AFTER the {$asm} pass, the close-tail lives at the END of the
        // deferred discrete_runAfterAsm callback — NOT at the end of the
        // synchronous block. discrete_logScan flags a NOT-FOUND so the window
        // stays up on a failed scan; its success line is DEBUG-gated.
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload() });

        Assert.Contains("_G.DISCRETE_HAD_ERROR = false", s);
        // NOT-FOUND path sets the flag; success is gated behind DEBUG.
        Assert.Contains("_G.DISCRETE_HAD_ERROR = true", s);
        Assert.Contains(
            "if _G.DISCRETE_DEBUG == 1 then print(string.format(\"[discrete] %s = %X\", name, addr)) end",
            s);

        // Close-tail present and INSIDE the deferred callback: it must appear
        // AFTER the logScan call and BEFORE the callback's closing "end)".
        Assert.Contains(
            "synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)", s);
        int deferIdx = s.IndexOf("_G.discrete_runAfterAsm(function()", StringComparison.Ordinal);
        int logIdx   = s.IndexOf("_G.discrete_logScan(\"Mathf_Abs_int32\")", StringComparison.Ordinal);
        int closeIdx = s.IndexOf("if e then e.Close() end", StringComparison.Ordinal);
        int endIdx   = s.IndexOf("end)", closeIdx, StringComparison.Ordinal);
        Assert.True(deferIdx >= 0 && logIdx > deferIdx && closeIdx > logIdx && endIdx > closeIdx,
            "close-tail must sit inside the deferred callback, after logScan and before end)");
    }

    [Fact]
    public void Build_DeferredActionError_SetsHadErrorFlag()
    {
        // The deferred-action error branch must set the shared flag so a thrown
        // deferred callback keeps the Lua Engine open.
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload() });
        Assert.Contains(
            "if not ok then _G.DISCRETE_HAD_ERROR = true; print('[discrete] deferred action error: ' .. tostring(err)) end",
            s);
    }

    [Fact]
    public void Build_MonoJitOnly_EmitsNeitherWarningNorUniqueNote()
    {
        // No aobscan at all (bridge-resolve) → no AOB-quality banner.
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        Assert.DoesNotContain("WARNING:", s);
        Assert.DoesNotContain("verified unique", s);
    }

    [Fact]
    public void Build_DisableLuaBlock_ResetsHadErrorFlag_AndBridgeGuardUsesError()
    {
        // E-L1: [DISABLE] {$lua} must reset _G.DISCRETE_HAD_ERROR = false so a
        // prior failed [ENABLE] does not keep CE's Lua Engine open on disable.
        // E-L2: Mono JIT precondition failure must call error() so CE rejects
        // ticking the checkbox enabled.
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        int disableIdx = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        Assert.True(disableIdx > 0);
        string enableBlock  = s.Substring(0, disableIdx);
        string disableBlock = s.Substring(disableIdx);

        Assert.Contains("error('[Discrete] Enable \"Load Discrete Bridge\" first", enableBlock);
        Assert.Contains("error('[Discrete] unity_symbols.lua missing from CT", enableBlock);
        Assert.Contains("_G.DISCRETE_HAD_ERROR = false", disableBlock);
    }
}
