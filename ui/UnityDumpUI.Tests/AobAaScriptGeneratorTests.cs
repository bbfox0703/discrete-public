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
        HumanSuffix      = "int32",
        Signature        = "(System.Int32)",
        Pattern          = "55 48 8B EC 48 83 EC 30 48 89 75 F8 48 8B F1 85 F6",
        // Mono JIT: ModuleName is empty (the JIT VirtualAllocs each
        // method's code page; no .dll module owns the address). The
        // pre-fix code path threaded this through into
        // `aobscanmodule(name, , pattern)`, which never matches.
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

        // Module present → use aobscanmodule with the module name.
        Assert.Contains("aobscanmodule(Mathf_Abs_int32, GameAssembly.dll, 48 8B 05", s);
        Assert.DoesNotContain("aobscan(Mathf_Abs_int32,", s); // NOT the module-less form
        Assert.Contains("registersymbol(Mathf_Abs_int32)", s);

        // Trailing comment carries module + offset for cross-reference.
        Assert.Contains("(GameAssembly.dll+0x00000000B23450)", s);
    }

    [Fact]
    public void Build_MonoJitOverload_UsesAobscan_NotAobscanmodule()
    {
        // Phase 22f live-smoke regression. Surfaced 2026-05-12 against
        // BongoCat (Mono / PID 40700) — the generated script used
        // `aobscanmodule(name, , pattern)` (empty module) and CE
        // silently failed every scan.
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        // Mono JIT → must NOT have an empty-module aobscanmodule.
        Assert.DoesNotContain("aobscanmodule(Mathf_Abs_int32, , ", s);
        Assert.DoesNotContain("aobscanmodule(Mathf_Abs_int32, ,", s);

        // Should use the whole-process aobscan form instead.
        Assert.Contains("aobscan(Mathf_Abs_int32, 55 48 8B EC", s);
        Assert.Contains("registersymbol(Mathf_Abs_int32)", s);

        // Trailing comment annotates "Mono JIT" so a developer reading
        // the script doesn't think module info is missing — it
        // genuinely doesn't exist for JIT methods.
        Assert.Contains("Mono JIT", s);
        // And NOT the misleading "(+0x000…)" form.
        Assert.DoesNotContain("(+0x", s);
    }

    [Fact]
    public void Build_MonoJitOverload_DisableUnregistersSymbol()
    {
        // [DISABLE] block must still unregister the symbol regardless
        // of which scan form was used in [ENABLE].
        string s = AobAaScriptGenerator.Build(new[] { MonoJitOverload() });

        Assert.Contains("[DISABLE]", s);
        int disableIdx = s.IndexOf("[DISABLE]", StringComparison.Ordinal);
        string disableBlock = s.Substring(disableIdx);
        Assert.Contains("unregistersymbol(Mathf_Abs_int32)", disableBlock);
    }

    [Fact]
    public void Build_MixedBatch_PerOverloadModuleDecision()
    {
        // Realistic batch: one IL2CPP + one Mono-style overload (this
        // doesn't happen in practice on a single-backend target, but
        // pins that the per-overload branching is per-overload, not a
        // generator-wide flag).
        var monoVariant = new AaScriptOverload
        {
            MethodName       = "Sign",
            ClassName        = "Mathf",
            Namespace        = "UnityEngine",
            HumanSuffix      = "int32_2",
            Signature        = "(System.Int32)",
            Pattern          = "55 48 8B EC 48 83 EC 30 48 89 75 F8 48 8B F1 85 F6",
            ModuleName       = "",  // empty = Mono JIT
            Address          = "0x0000018C5C470F30",
            ModuleOffset     = "0x0000000000000000",
            ReturnType       = "System.Int32",
        };
        string s = AobAaScriptGenerator.Build(new[] { Il2cppOverload(), monoVariant });

        Assert.Contains("aobscanmodule(Mathf_Abs_int32, GameAssembly.dll,", s);
        Assert.Contains("aobscan(Mathf_Sign_int32_2, 55 48 8B EC", s);
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
