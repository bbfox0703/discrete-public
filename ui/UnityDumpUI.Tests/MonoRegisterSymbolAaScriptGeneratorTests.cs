// MonoRegisterSymbolAaScriptGeneratorTests.cs — pins the DLL-free
// Mono symbol register AA-script shape.
//
// Three concerns this catches:
//   1. Helper bootstrap shape — findTableFile + chunk load + idempotency
//      guard. Drift here would silently no-op on second-AA-on-same-CT
//      use, or fail loudly when the helper file isn't injected.
//   2. discrete_mono_registerSymbolEx call shape — exact arg order + Lua
//      single-quoted strings. The helper substring-matches paramTypes
//      (with string.find plain=true), so we MUST emit full-name forms
//      ("System.Int32" not "Int32") to avoid the Int32-matches-UInt32
//      substring trap. (Memory feedback_signaturematch_substring.)
//      Namespaced under `discrete_*` to dodge user-autorun overrides of
//      the public `mono_*` names.
//   3. Mono-only guard — Build() throws when ModuleName is populated
//      (IL2CPP overload accidentally routed here).

using System;
using System.Collections.Generic;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class MonoRegisterSymbolAaScriptGeneratorTests
{
    private static AaScriptOverload MonoOverload(
        string method     = "Abs",
        string cls        = "Mathf",
        string ns         = "UnityEngine",
        string suffix     = "int32",
        string sig        = "(System.Int32)",
        IReadOnlyList<string>? paramTypes = null,
        IReadOnlyList<string>? sigExcludes = null,
        string addr       = "0x0000018C5C470E30") => new()
    {
        MethodName    = method,
        ClassName     = cls,
        Namespace     = ns,
        Assembly      = "UnityEngine.CoreModule",
        HumanSuffix   = suffix,
        Signature     = sig,
        Pattern       = "",
        ModuleName    = "",                 // Mono JIT — empty by definition
        Address       = addr,
        ModuleOffset  = "0x0000000000000000",
        ReturnType    = "System.Int32",
        DecoderBailed = false,
        ParamTypes    = paramTypes ?? new[] { "System.Int32" },
        SigExcludes   = sigExcludes,
    };

    [Fact]
    public void Build_EmitsHelperLazyLoadBlock_WithIdempotencyGuard()
    {
        string s = MonoRegisterSymbolAaScriptGenerator.Build(
            new[] { MonoOverload() });

        // Helper file name from the generator's public constant — keeps
        // the test in sync if the file ever moves.
        Assert.Equal("mono_registersymbol_helpers.lua",
                     MonoRegisterSymbolAaScriptGenerator.HelpersFileName);

        Assert.Contains("[ENABLE]", s);
        Assert.Contains("if syntaxcheck then return end", s);

        // Idempotency guard keyed off the discrete-namespaced symbol —
        // a pre-existing user autorun definition of _G.mono_* (CE forums
        // pattern, often missing mono_ensureAttached) must NOT be allowed
        // to suppress our helper load. Asserting the discrete_* form
        // catches accidental drift back to the public name.
        Assert.Contains("if _G.discrete_mono_registerSymbolEx == nil then", s);
        Assert.DoesNotContain("if _G.mono_registerSymbolEx == nil then", s);

        // findTableFile + chunk-load via createStringStream — same
        // mechanism BootstrapAaScriptGenerator uses.
        Assert.Contains("findTableFile('mono_registersymbol_helpers.lua')", s);
        Assert.Contains("createStringStream()", s);
        Assert.Contains("ss.copyFrom(f.Stream, f.Stream.Size)", s);
        Assert.Contains("load(ss.DataString, '@mono_registersymbol_helpers.lua')", s);
        Assert.Contains("ss.destroy()", s);
        Assert.Contains("chunk()", s);

        // Missing-file path must abort via error() (E-L2) so CE rejects enabling
        // the entry instead of ticking the checkbox as enabled.
        Assert.Contains("error('[Discrete] mono_registersymbol_helpers.lua missing from CT", s);
        Assert.Contains("Inject Discrete Bridge", s);
        Assert.Contains("error('[Discrete] parse error in mono_registersymbol_helpers.lua", s);
    }

    [Fact]
    public void Build_SingleOverload_EmitsRegisterSymbolEx_WithFullNameParamTypes()
    {
        var ov = MonoOverload(paramTypes: new[] { "System.Int32" });
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { ov });

        // E-M5: exact positional call shape (symbol, ns, class, method, paramTypes).
        // The runtime JIT-resolves the real address at enable → relaunch-safe.
        // No hardcoded address; no unordered substring-bag BySignatureMatch.
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Mathf_Abs_int32', 'UnityEngine', 'Mathf', 'Abs', { 'System.Int32' })",
            s);
        Assert.DoesNotContain("discrete_mono_registerSymbolBySignatureMatch", s);
        // The relaunch-fragile hardcoded form must NOT appear.
        Assert.DoesNotContain("registerSymbol('Mathf_Abs_int32', 0x", s);

        // Diagnostic comment line so the user can identify which row
        // a generated symbol came from.
        Assert.Contains("UnityEngine.Mathf.Abs(System.Int32)", s);
        Assert.Contains("last-seen address 0x0000018C5C470E30", s);
    }

    [Fact]
    public void Build_SwappedAndRepeatedParamOverloads_PreserveExactOrderAndCount()
    {
        // E-M5: overloads with identical token sets (Int32, String) vs
        // (String, Int32) and repeated types (Int32) vs (Int32, Int32) are
        // unambiguously distinguished by discrete_mono_registerSymbolEx's exact
        // count + positional match.
        var intStr = MonoOverload(method: "Set", cls: "Foo", ns: "", suffix: "int_str",
            sig: "(System.Int32, System.String)",
            paramTypes: new[] { "System.Int32", "System.String" });
        var strInt = MonoOverload(method: "Set", cls: "Foo", ns: "", suffix: "str_int",
            sig: "(System.String, System.Int32)",
            paramTypes: new[] { "System.String", "System.Int32" });
        var oneInt = MonoOverload(method: "Set", cls: "Foo", ns: "", suffix: "int",
            sig: "(System.Int32)",
            paramTypes: new[] { "System.Int32" });
        var twoInt = MonoOverload(method: "Set", cls: "Foo", ns: "", suffix: "int_int",
            sig: "(System.Int32, System.Int32)",
            paramTypes: new[] { "System.Int32", "System.Int32" });

        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { intStr, strInt, oneInt, twoInt });

        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Foo_Set_int_str', '', 'Foo', 'Set', { 'System.Int32', 'System.String' })",
            s);
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Foo_Set_str_int', '', 'Foo', 'Set', { 'System.String', 'System.Int32' })",
            s);
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Foo_Set_int', '', 'Foo', 'Set', { 'System.Int32' })",
            s);
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Foo_Set_int_int', '', 'Foo', 'Set', { 'System.Int32', 'System.Int32' })",
            s);
    }

    [Fact]
    public void Build_MultipleOverloads_EmitsOneRegisterCallPerOverload_UniqueSymbols()
    {
        var byInt = MonoOverload(method: "AddItems", cls: "ItemBundle", ns: "",
            suffix: "uint_uint", sig: "(System.UInt32, System.UInt32)",
            paramTypes: new[] { "System.UInt32", "System.UInt32" });
        var byBundle = MonoOverload(method: "AddItems", cls: "ItemBundle", ns: "",
            suffix: "ItemBundle", sig: "(ItemBundle)",
            paramTypes: new[] { "ItemBundle" });

        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { byInt, byBundle });

        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('ItemBundle_AddItems_uint_uint', '', 'ItemBundle', 'AddItems', { 'System.UInt32', 'System.UInt32' })",
            s);
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('ItemBundle_AddItems_ItemBundle', '', 'ItemBundle', 'AddItems', { 'ItemBundle' })",
            s);

        // Both unregisterSymbol calls in [DISABLE], pcall-wrapped so a
        // partial [ENABLE] still tears down cleanly.
        Assert.Contains("pcall(unregisterSymbol, 'ItemBundle_AddItems_uint_uint')", s);
        Assert.Contains("pcall(unregisterSymbol, 'ItemBundle_AddItems_ItemBundle')", s);
    }

    [Fact]
    public void Build_ZeroParamOverload_EmitsEmptyTable()
    {
        var ov = MonoOverload(method: "Reset", suffix: "noargs", sig: "()",
            paramTypes: Array.Empty<string>());
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { ov });

        // Empty table — mono_registerSymbolEx will match the zero-param
        // overload (the #p.parameters ~= 0 check fires for any other).
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Mathf_Reset_noargs', 'UnityEngine', 'Mathf', 'Reset', {})",
            s);
    }

    [Fact]
    public void Build_NullParamTypes_TreatedAsEmpty()
    {
        // Construct directly so ParamTypes is left at the type-default
        // (null) — MonoOverload() helper substitutes Int32 when null
        // is passed in.
        var ov = new AaScriptOverload
        {
            MethodName    = "Abs",
            ClassName     = "Mathf",
            Namespace     = "UnityEngine",
            Assembly      = "UnityEngine.CoreModule",
            HumanSuffix   = "noargs",
            Signature     = "()",
            Pattern       = "",
            ModuleName    = "",
            Address       = "0x0000018C5C470E30",
            ModuleOffset  = "0x0000000000000000",
            ReturnType    = "System.Int32",
            DecoderBailed = false,
            ParamTypes    = null,
        };

        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { ov });
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Mathf_Abs_noargs', 'UnityEngine', 'Mathf', 'Abs', {})",
            s);
    }

    [Fact]
    public void RenderParamTypesTable_FormatsExpected()
    {
        Assert.Equal("{}",
            MonoRegisterSymbolAaScriptGenerator.RenderParamTypesTable(null));
        Assert.Equal("{}",
            MonoRegisterSymbolAaScriptGenerator.RenderParamTypesTable(Array.Empty<string>()));
        Assert.Equal("{ 'System.Int32' }",
            MonoRegisterSymbolAaScriptGenerator.RenderParamTypesTable(new[] { "System.Int32" }));
        Assert.Equal("{ 'System.Int32', 'UnityEngine.Vector3', 'System.Boolean' }",
            MonoRegisterSymbolAaScriptGenerator.RenderParamTypesTable(new[]
                { "System.Int32", "UnityEngine.Vector3", "System.Boolean" }));
    }

    [Fact]
    public void Build_EscapesLuaSingleQuotes_InClassName()
    {
        // Synthetic — real Mono class names don't carry apostrophes, but
        // defensive-escape is cheap and stops a single mis-typed input
        // from breaking the entire AA chunk parse (Phase 22 P0 bug #3).
        var ov = MonoOverload(cls: "Foo'Bar", ns: "Game'NS", method: "M'eth",
            suffix: "weird");
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { ov });

        // Each apostrophe escaped as \' in single-quoted Lua strings.
        Assert.Contains(@"'Game\'NS'", s);
        Assert.Contains(@"'Foo\'Bar'", s);
        Assert.Contains(@"'M\'eth'", s);
        // Symbol name itself sanitises non-identifier chars to _ via
        // AobAaScriptGenerator.MakeIdentifier (apostrophe → underscore).
        Assert.Contains("'Foo_Bar_M_eth_weird'", s);
    }

    [Fact]
    public void Build_DisableBlock_PcallsEveryUnregister()
    {
        var ov1 = MonoOverload(suffix: "int32");
        var ov2 = MonoOverload(suffix: "float", sig: "(System.Single)",
            paramTypes: new[] { "System.Single" });
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { ov1, ov2 });

        Assert.Contains("[DISABLE]", s);
        Assert.Contains("pcall(unregisterSymbol, 'Mathf_Abs_int32')", s);
        Assert.Contains("pcall(unregisterSymbol, 'Mathf_Abs_float')", s);
    }

    [Fact]
    public void Build_Il2cppOverload_StillEmitsRegisterSymbolEx()
    {
        // ModuleName populated = IL2CPP method. CE's Active Mono features drives
        // the mono_* API on IL2CPP too, so positional registration works here —
        // the generator must NOT throw (it used to assert Mono-only) and must
        // emit the same discrete_mono_registerSymbolEx call, ignoring the
        // GameAssembly.dll module name.
        var il2cpp = new AaScriptOverload
        {
            MethodName    = "Abs",
            ClassName     = "Mathf",
            Namespace     = "UnityEngine",
            Assembly      = "UnityEngine.CoreModule",
            HumanSuffix   = "int32",
            Signature     = "(System.Int32)",
            Pattern       = "",
            ModuleName    = "GameAssembly.dll",
            Address       = "0x00007FF876123450",
            ModuleOffset  = "0x00000000B23450",
            ReturnType    = "System.Int32",
            DecoderBailed = false,
            ParamTypes    = new[] { "System.Int32" },
        };
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { il2cpp });
        Assert.Contains(
            "_G.discrete_mono_registerSymbolEx('Mathf_Abs_int32', 'UnityEngine', 'Mathf', 'Abs', { 'System.Int32' })",
            s);
    }

    [Fact]
    public void Build_EmitsHadErrorReset_AndCloseLuaEngineTail_PerLuaLogging()
    {
        // docs/lua-logging.md: every generated {$lua} entry point resets the
        // shared did-anything-fail flag right after the syntaxcheck guard, and
        // closes CE's Lua Engine on a clean finish so a quiet register run
        // stops covering CE. Failure paths set the flag before their prints so
        // the close-tail leaves the window up on error.
        string s = MonoRegisterSymbolAaScriptGenerator.Build(new[] { MonoOverload() });

        // Reset appears in [ENABLE] (not [DISABLE] — no reset there per spec).
        Assert.Contains("_G.DISCRETE_HAD_ERROR = false", s);
        // Close-tail present in both ENABLE and DISABLE — count the guard line.
        Assert.Contains("if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then", s);
        Assert.Contains(
            "synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)", s);
        // Missing-file failure path flags the error so the window stays up.
        Assert.Contains("_G.DISCRETE_HAD_ERROR = true", s);
    }

    [Fact]
    public void Build_EmptyInput_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MonoRegisterSymbolAaScriptGenerator.Build(Array.Empty<AaScriptOverload>()));
        Assert.Contains("at least one overload is required", ex.Message);
    }

    [Fact]
    public void Build_NullInput_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => MonoRegisterSymbolAaScriptGenerator.Build(null!));
    }
}
