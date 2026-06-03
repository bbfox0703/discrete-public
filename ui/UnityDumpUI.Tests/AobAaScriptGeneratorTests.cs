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
}
