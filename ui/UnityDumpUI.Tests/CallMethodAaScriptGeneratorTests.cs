// CallMethodAaScriptGeneratorTests.cs — Phase 22e verification.
//
// Pin the lua-side shape of the generated AA script. We don't actually
// run the script through a Lua interpreter — Phase 22f's E2E smoke
// will do that live in CE — but we do assert every load-bearing
// substring is present + correctly substituted: the bridge-loaded guard
// prepends the [ENABLE], the args[] table is built from arg* helpers,
// the call site uses the right namespace/class/method, the opts table
// is conditional on signatureMatch + instance presence, and string-arg
// values are escaped for Lua single-quoted literals.

using System;
using System.Collections.Generic;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class CallMethodAaScriptGeneratorTests
{
    private static IReadOnlyList<CallMethodArg> NoArgs() => Array.Empty<CallMethodArg>();

    [Fact]
    public void Build_HasEnableAndDisable_AndBridgeGuard()
    {
        string s = CallMethodAaScriptGenerator.Build(
            "mscorlib", "System", "Math", "Abs",
            signatureMatch: null, instanceAddress: null, args: NoArgs());

        Assert.Contains("[ENABLE]", s);
        Assert.Contains("[DISABLE]", s);
        // Order: ENABLE precedes DISABLE.
        Assert.True(s.IndexOf("[ENABLE]", StringComparison.Ordinal)
                  < s.IndexOf("[DISABLE]", StringComparison.Ordinal));

        // Bridge-loaded guard — every generated script aborts with a
        // clear message when the user hasn't enabled Load Discrete
        // Bridge first.
        Assert.Contains("if not _G.discrete_bridgeLoaded then", s);
        // Lua uses literal double-quotes inside the single-quoted print
        // — no backslash escape needed in the rendered output.
        Assert.Contains("Enable \"Load Discrete Bridge\" first", s);
        Assert.Contains("return", s);

        // Action-driven: the AA self-loads its lua dependency from the CT
        // (the Send-to-CE-Table action embedded unity_invoke.lua), guarded on
        // the callUnityMethod global so it loads once per table.
        Assert.Contains("if _G.callUnityMethod == nil then", s);
        Assert.Contains("findTableFile('unity_invoke.lua')", s);
    }

    [Fact]
    public void Build_NoArgs_NoOpts_EmitsBareCallSite()
    {
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "ns", "Cls", "Mth",
            signatureMatch: null, instanceAddress: null, args: NoArgs());

        // local args = {} ... callUnityMethod(...,args)  (no { … } trailing)
        Assert.Contains("local args = {", s);
        Assert.Contains("callUnityMethod(", s);
        Assert.Contains("'asm', 'ns', 'Cls', 'Mth'", s);
        Assert.DoesNotContain("signatureMatch", s);
        Assert.DoesNotContain("instance =",     s);
        Assert.Contains("prettyPrint(r)", s);
    }

    [Fact]
    public void Build_StaticWithSignature_EmitsOptsWithSignatureMatchOnly()
    {
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "Math", "Abs",
            signatureMatch: "(System.Int32)", instanceAddress: null,
            args: new[] { new CallMethodArg { TypeName = "System.Int32", ValueText = "-7" } });

        Assert.Contains("argInt32(-7)", s);
        Assert.Contains("signatureMatch = '(System.Int32)'", s);
        Assert.DoesNotContain("instance =", s);
    }

    [Fact]
    public void Build_InstanceCall_EmitsBothSignatureAndInstance()
    {
        string s = CallMethodAaScriptGenerator.Build(
            "Assembly-CSharp", "MainGame", "PLY_WORK", "AddGrowHpMax",
            signatureMatch: "(System.Int32)",
            instanceAddress: "0x000002201761E3D0",
            args: new[] { new CallMethodArg { TypeName = "System.Int32", ValueText = "100" } });

        Assert.Contains("'Assembly-CSharp', 'MainGame', 'PLY_WORK', 'AddGrowHpMax'", s);
        Assert.Contains("signatureMatch = '(System.Int32)'", s);
        Assert.Contains("instance = '0x000002201761E3D0'", s);
        Assert.Contains("argInt32(100)", s);
    }

    [Theory]
    // Numeric / boolean / address types map to specific arg* builders
    // (mirrors scripts/unity_invoke.lua's arg builder set).
    [InlineData("System.Boolean", "true",   "argBool(true)")]
    [InlineData("System.Boolean", "false",  "argBool(false)")]
    [InlineData("System.Byte",    "200",    "argByte(200)")]
    [InlineData("System.SByte",   "-50",    "argSByte(-50)")]
    [InlineData("System.Int16",   "-30000", "argInt16(-30000)")]
    [InlineData("System.UInt16",  "60000",  "argUInt16(60000)")]
    [InlineData("System.Int32",   "-7",     "argInt32(-7)")]
    [InlineData("System.UInt32",  "4000000000", "argUInt32(4000000000)")]
    [InlineData("System.Int64",   "9000000000", "argInt64(9000000000)")]
    [InlineData("System.IntPtr",  "0xDEADBEEF", "argAddress('0xDEADBEEF')")]
    public void Build_PrimitiveArg_MapsToCorrectArgBuilder(string clrType, string raw, string expected)
    {
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = clrType, ValueText = raw } });
        Assert.Contains(expected, s);
    }

    [Fact]
    public void Build_NullCheckbox_OverridesValueText_WithArgNull()
    {
        // The null checkbox short-circuits — value text is ignored.
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "System.String",
                                        ValueText = "ignored", IsNull = true } });
        Assert.Contains("argNull()", s);
        Assert.DoesNotContain("argString", s);
        Assert.DoesNotContain("'ignored'", s);
    }

    [Fact]
    public void Build_StringArg_EscapesSingleQuotesAndBackslashes()
    {
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "System.String",
                                        ValueText = "it's a \\test\\\nwith newline" } });
        // Single-quote is escaped as \'  •  backslash as \\  •  newline as \n
        Assert.Contains(@"argString('it\'s a \\test\\\nwith newline')", s);
    }

    [Fact]
    public void Build_FloatArg_UsesInvariantCulture()
    {
        // Even on CJK / EU locales the lua literal must use '.' decimal,
        // never ',' — a comma would be parsed as the next argument.
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "System.Single", ValueText = "3.14159" } });
        Assert.Contains("argFloat(3.14159)", s);
    }

    [Fact]
    public void Build_OutOfRangeIntegerArg_ClampsRatherThanFails()
    {
        // The Send button is post-validation by design — clipping at the
        // typed range avoids emitting a script CE will reject when the
        // user pre-typed a value the Call button would have caught.
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "System.Int16", ValueText = "999999" } });
        Assert.Contains("argInt16(32767)", s); // short.MaxValue
    }

    [Fact]
    public void Build_UnknownTypeWithEmptyValue_BecomesNull()
    {
        // Reference-type fallback path: empty input => argNull.
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "MyNs.MyClass", ValueText = "" } });
        Assert.Contains("argNull()", s);
    }

    [Fact]
    public void Build_UnknownTypeWithAddress_BecomesArgAddress()
    {
        // Reference-type fallback path: non-empty input => treated as address.
        string s = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "MyNs.MyClass", ValueText = "0xDEADBEEF" } });
        Assert.Contains("argAddress('0xDEADBEEF')", s);
    }

    [Fact]
    public void SuggestDescription_StableAcrossCallSites()
    {
        // Description format is the AOBMaker memory-record description —
        // keep stable so a future duplicate-detect feature has a reliable key.
        Assert.Equal("call: Math.Abs(System.Int32)",
            CallMethodAaScriptGenerator.SuggestDescription("Math", "Abs", "(System.Int32)"));
        Assert.Equal("call: Math.PI()",
            CallMethodAaScriptGenerator.SuggestDescription("Math", "PI", null));
    }

    [Fact]
    public void Build_RejectsMissingRequiredArgs()
    {
        Assert.Throws<ArgumentException>(
            () => CallMethodAaScriptGenerator.Build("", "ns", "C", "M", null, null, NoArgs()));
        Assert.Throws<ArgumentException>(
            () => CallMethodAaScriptGenerator.Build("asm", "ns", "", "M", null, null, NoArgs()));
        Assert.Throws<ArgumentException>(
            () => CallMethodAaScriptGenerator.Build("asm", "ns", "C", "", null, null, NoArgs()));
    }

    [Fact]
    public void Build_HasNo_AdjacentSingleQuotes_LuaSyntaxTrap()
    {
        // See BootstrapAaScriptGeneratorTests for full story. Bug
        // shape: `word''word` (apostrophe pair inside what was meant
        // to be one string). Bare `''` between commas / parens is a
        // legitimate empty Lua string (e.g. empty namespace arg) and
        // not a bug. Regex check threads the needle.
        const string bugPattern = @"\w''\w";

        string s1 = CallMethodAaScriptGenerator.Build(
            "asm", "ns", "C", "M", null, null, NoArgs());
        Assert.DoesNotMatch(bugPattern, s1);

        string s2 = CallMethodAaScriptGenerator.Build(
            "asm", "", "C", "M", null, null,
            new[] { new CallMethodArg { TypeName = "System.String",
                                        ValueText = "can't won't" } });
        Assert.DoesNotMatch(bugPattern, s2);
        // Sanity: the EscapeSq path did produce a `\'` form.
        Assert.Contains(@"can\'t won\'t", s2);
    }
}
