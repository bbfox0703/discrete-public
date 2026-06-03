// AobAaScriptGenerator.cs — Phase 18c.
//
// Builds a Cheat Engine Auto-Assembler script that resolves one or more
// method overloads via `aobscanmodule(name, MODULE, AOB)` instead of the
// runtime DLL injection + JIT path used by Phase 11's unity_symbols.lua.
// The output script is standalone — paste it into a CE cheat table and
// enable; it works on every game launch until the bytes change (e.g.
// after a game patch).
//
// Two modes:
//   Single    — one overload → one symbol → one aobscanmodule line
//   Batch     — N overloads from the SAME class+method → N aobscanmodule
//               lines + a single shared {$lua} helper block at the top
//
// "不重複載入程式碼段" — the helper functions are wrapped in
// `if _G.<name> == nil then ... end` guards so multiple AA scripts on the
// same cheat table only load the helper once. Same idiom as the user's
// existing mono_registerSymbol* pattern.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Core.Services.Export;

/// <summary>
/// One overload, ready for AA-script generation. Pre-resolved AOB pattern
/// + module info comes from <c>getMethodAobPattern</c>.
/// </summary>
public sealed class AaScriptOverload
{
    public required string MethodName     { get; init; }
    public required string ClassName      { get; init; }   // e.g. "Vector3" or "Outer/Inner"
    public string?         Namespace      { get; init; }   // "" / null for global
    public required string HumanSuffix    { get; init; }   // "3float", "Vector3", "noargs", ...
    public required string Signature      { get; init; }   // display only
    public required string Pattern        { get; init; }   // "48 89 ?? ?? E8 ??..."
    public required string ModuleName     { get; init; }   // "GameAssembly.dll" / "mono-2.0-bdwgc.dll"
    public required string Address        { get; init; }   // hex, for the trailing comment
    public required string ModuleOffset   { get; init; }   // hex, for the trailing comment
    public string?         ReturnType     { get; init; }
    public bool            DecoderBailed  { get; init; }
    public int             InstructionCount { get; init; }
    public int             WildcardCount  { get; init; }

    /// <summary>
    /// CLR assembly name (e.g. "UnityEngine.CoreModule", "Assembly-CSharp").
    /// Phase 22f follow-up: required for the Mono JIT bridge-call AA
    /// script form (registerMethodSymbol(symName, ASM, ns, cls, …)).
    /// Optional for IL2CPP path — only the AOB scan uses ModuleName +
    /// pattern. Callers building Mono-targeted bags MUST set this.
    /// </summary>
    public string?         Assembly       { get; init; }

    /// <summary>
    /// Per-parameter CLR type names (full-name form preferred — e.g.
    /// <c>System.Int32</c>, <c>UnityEngine.Vector3</c>). Consumed by the
    /// DLL-free Mono register path (<c>MonoRegisterSymbolAaScriptGenerator</c>):
    /// the generated AA emits <c>mono_registerSymbolEx(…, { p1, p2, ... })</c>
    /// which does ordered substring-match per param. Full-name form is
    /// important — <c>"Int32"</c> alone would also match <c>"UInt32"</c>
    /// via the substring matcher (the signatureMatch substring trap).
    /// Null/empty = no per-param info; the DLL-free generator emits an
    /// empty-paramTypes <c>mono_registerSymbolEx</c> call (matches
    /// zero-arg overloads only) or you can prefer
    /// <c>mono_registerSymbolBySignatureMatch</c> instead. Not consumed
    /// by the AOB-scan or bridge-resolve paths.
    /// </summary>
    public IReadOnlyList<string>? ParamTypes { get; init; }

    /// <summary>
    /// Parameter-type tokens that appear in OTHER overloads of this method but
    /// NOT in this one. Consumed by <c>MonoRegisterSymbolAaScriptGenerator</c>'s
    /// signature-match path as the <c>sigExcludes</c> argument: it lets
    /// <c>discrete_mono_registerSymbolBySignatureMatch</c> reject a longer-arity
    /// sibling whose rendered signature is a super-string of this overload's
    /// (e.g. this = <c>(int)</c>, sibling = <c>(int, string)</c> — exclude
    /// <c>System.String</c> so the 1-param overload isn't matched against the
    /// 2-param one). Null/empty when there's nothing to disambiguate against.
    /// </summary>
    public IReadOnlyList<string>? SigExcludes { get; init; }
}

public static class AobAaScriptGenerator
{
    /// <summary>
    /// Produce a complete AA script (ENABLE + DISABLE blocks). The helper
    /// <c>discrete_logScan</c> is defined idempotently so multiple scripts
    /// on the same CE table cooperate.
    /// </summary>
    public static string Build(IReadOnlyList<AaScriptOverload> overloads)
    {
        if (overloads is null || overloads.Count == 0)
            throw new ArgumentException("at least one overload is required", nameof(overloads));

        // Build symbol names + check uniqueness. CE rejects duplicate
        // registersymbol calls within one ENABLE block.
        var symbols = new string[overloads.Count];
        var used    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < overloads.Count; ++i)
        {
            var ov = overloads[i];
            symbols[i] = MakeSymbolName(ov, used);
            used.Add(symbols[i]);
        }

        var sb = new StringBuilder(1024);

        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine("-- discrete: AA helper, idempotent across scripts on the same cheat table.");
        sb.AppendLine("if _G.discrete_logScan == nil then");
        sb.AppendLine("  _G.discrete_logScan = function(name)");
        sb.AppendLine("    local addr = getAddressSafe(name)");
        sb.AppendLine("    if addr and addr ~= 0 then");
        sb.AppendLine("      print(string.format(\"[discrete] %s = %X\", name, addr))");
        sb.AppendLine("    else");
        sb.AppendLine("      print(string.format(\"[discrete] %s NOT FOUND (AOB scan failed)\", name))");
        sb.AppendLine("    end");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();

        // Mono JIT methods don't live inside any DLL module — the JIT
        // VirtualAllocs each method's code page on first compile, so
        // ModuleName comes back empty from getMethodAobPattern. Two
        // failure modes we already burnt through:
        //   1. `aobscanmodule(name, MODULE, pattern)` with empty MODULE
        //      — CE accepts the syntax but never matches. Silent fail.
        //      (Phase 22f live smoke #1 — fixed by falling back to
        //      `aobscan` in commit 90...)
        //   2. `aobscan(name, pattern)` — works for unique patterns
        //      but Mono JIT routinely produces MULTIPLE instances of
        //      the same source method (different generic instantiations,
        //      optimization tiers). Their prologues match, CE picks the
        //      first hit which is rarely the right one. Silent wrong-
        //      answer fail. (Phase 22f live smoke #2 against BongoCat
        //      UnityEngine.Mathf.Abs returned 3 hits, only 1 real.)
        //
        // Real fix: for Mono JIT, don't AOB-scan at all. The DLL already
        // resolves method addresses via mono_compile_method (server-side,
        // server-authoritative, overload-precise). Phase 18d shipped
        // `registerMethodSymbol` (in scripts/unity_symbols.lua, the
        // single-overload variant added Phase 22f follow-up) which wraps
        // findMethodInstances + signature filter + registerSymbol. The
        // AA's [ENABLE] block calls into the bridge instead of aobscan.
        //
        // Trade-off: the AA now requires the bridge to be loaded first
        // ("Load Discrete Bridge" enabled). The early-return guard
        // surfaces a clear error if not. For IL2CPP overloads
        // (ModuleName populated) we keep the AOB form — it's
        // self-contained and doesn't pay the bridge round-trip.
        bool anyMonoJit = false;
        for (int i = 0; i < overloads.Count; ++i)
            if (string.IsNullOrEmpty(overloads[i].ModuleName)) { anyMonoJit = true; break; }

        sb.AppendLine("{$asm}");
        for (int i = 0; i < overloads.Count; ++i)
        {
            var ov = overloads[i];
            bool isMonoJit = string.IsNullOrEmpty(ov.ModuleName);

            // Trailing comment carries the pre-scan address + module
            // offset for cross-reference. For Mono JIT we annotate
            // "Mono JIT (bridge resolve)" so a developer reading the
            // script knows the missing module info is intentional.
            sb.Append("// ")
              .Append(ClassDisplay(ov)).Append('.')
              .Append(CompilerNameDemangler.Demangle(ov.MethodName))
              .Append(ov.Signature).Append("  ")
              .Append("→ ").Append(ov.Address);
            if (isMonoJit)
                sb.AppendLine("  (Mono JIT — resolved via bridge findMethodInstances on enable)");
            else
                sb.Append("  (").Append(ov.ModuleName).Append('+').Append(ov.ModuleOffset).AppendLine(")");

            if (isMonoJit)
            {
                // Bridge-call form lives in [$lua] below; nothing to
                // emit here in the [$asm] block for Mono JIT entries.
                // Leave a placeholder comment so the script flow is
                // obvious to a reader.
                sb.AppendLine("// (no aobscan — symbol registration happens in the {$lua} block below)");
                sb.AppendLine();
                continue;
            }

            if (ov.DecoderBailed)
            {
                sb.AppendLine("//   ⚠ x64 decoder bailed mid-stream — pattern's tail is literal bytes (less version-stable).");
            }
            sb.Append("aobscanmodule(")
              .Append(symbols[i]).Append(", ")
              .Append(ov.ModuleName).Append(", ")
              .Append(ov.Pattern).AppendLine(")");
            sb.Append("registersymbol(").Append(symbols[i]).AppendLine(")");
            sb.AppendLine();
        }

        sb.AppendLine("{$lua}");

        // Mono JIT registrations go through the bridge. Guard with
        // discrete_bridgeLoaded — without the bridge, registerMethodSymbol
        // is undefined and the entry would silently no-op.
        if (anyMonoJit)
        {
            sb.AppendLine("if not _G.discrete_bridgeLoaded then");
            sb.AppendLine("  print('[Discrete] Enable \"Load Discrete Bridge\" first (Mono JIT methods resolve via the bridge)')");
            sb.AppendLine("  return");
            sb.AppendLine("end");
            // Self-load registerMethodSymbol from the CT (the Create-AA action
            // embedded unity_symbols.lua alongside this entry).
            AaLuaLoader.AppendSelfLoad(sb, "unity_symbols.lua", "registerMethodSymbol");
            for (int i = 0; i < overloads.Count; ++i)
            {
                var ov = overloads[i];
                if (string.IsNullOrEmpty(ov.ModuleName))
                {
                    // registerMethodSymbol(symbolName, assembly, ns, class, method, sigFilter?)
                    // Quote arguments. ov.Signature is the formal-param-list rendering
                    // (e.g. "(System.Int32)") — passed verbatim as the filter; sufficient
                    // when there's a unique formal-param-list-named overload.
                    sb.Append("registerMethodSymbol('").Append(LuaSqEscape(symbols[i])).Append("', ")
                      .Append('\'').Append(LuaSqEscape(ov.Assembly ?? "")).Append("', ")
                      .Append('\'').Append(LuaSqEscape(ov.Namespace ?? "")).Append("', ")
                      .Append('\'').Append(LuaSqEscape(ov.ClassName)).Append("', ")
                      .Append('\'').Append(LuaSqEscape(ov.MethodName)).Append("', ")
                      .Append('\'').Append(LuaSqEscape(ov.Signature ?? "")).AppendLine("')");
                }
            }
        }
        // Final discrete_logScan calls report each symbol's resolved
        // address (covers both AOB-resolved and bridge-resolved
        // entries — registerSymbol made the name visible to
        // getAddressSafe regardless of how it got there).
        for (int i = 0; i < overloads.Count; ++i)
        {
            sb.Append("_G.discrete_logScan(\"").Append(symbols[i]).AppendLine("\")");
        }
        sb.AppendLine();

        sb.AppendLine("[DISABLE]");
        // unregisterSymbol is a Lua function in CE (not an AA directive)
        // when the symbol was set via registerSymbol; the matching path
        // for aobscanmodule-registered symbols is the {$asm}
        // `unregistersymbol` directive. Cover both — use the {$asm}
        // form for AOB-registered, {$lua} for bridge-registered.
        if (anyMonoJit)
        {
            sb.AppendLine("{$lua}");
            sb.AppendLine("if syntaxcheck then return end");
            for (int i = 0; i < overloads.Count; ++i)
            {
                if (string.IsNullOrEmpty(overloads[i].ModuleName))
                    sb.Append("pcall(unregisterSymbol, '").Append(LuaSqEscape(symbols[i])).AppendLine("')");
            }
        }
        bool anyAob = false;
        for (int i = 0; i < overloads.Count; ++i)
            if (!string.IsNullOrEmpty(overloads[i].ModuleName)) { anyAob = true; break; }
        if (anyAob)
        {
            sb.AppendLine("{$asm}");
            for (int i = 0; i < overloads.Count; ++i)
            {
                if (!string.IsNullOrEmpty(overloads[i].ModuleName))
                    sb.Append("unregistersymbol(").Append(symbols[i]).AppendLine(")");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escape a string for a Lua single-quoted literal. Defence against
    /// any apostrophe / backslash / newline a class name / namespace
    /// might contain (unlikely in practice but cheap to harden).
    /// </summary>
    private static string LuaSqEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\")
                .Replace("'",  "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }

    /// <summary>
    /// Build a CE-friendly symbol name following the convention
    /// <c>{Class}_{Method}_{HumanSuffix}</c>. <see cref="MakeIdentifier"/>
    /// sanitises non-ASCII / nested-type slashes. If the result collides
    /// with an existing entry in <paramref name="taken"/> (e.g. user has
    /// two methods that share both class+method+suffix — rare but
    /// possible after suffix mapping), a numeric tail is appended.
    /// </summary>
    public static string MakeSymbolName(AaScriptOverload ov, ISet<string> taken)
    {
        string baseName = string.Format(
            CultureInfo.InvariantCulture, "{0}_{1}_{2}",
            MakeIdentifier(ov.ClassName),
            MakeIdentifier(ov.MethodName),
            MakeIdentifier(ov.HumanSuffix));

        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; n < 1000; ++n)
        {
            string candidate = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName + "_" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string ClassDisplay(AaScriptOverload ov)
    {
        // Friendly form for the script comment ONLY. Symbol-name
        // generation (MakeIdentifier / MakeSymbolName) still consumes
        // the raw ov.ClassName so CE-level identifiers match the
        // runtime metadata 1:1.
        string friendly = CompilerNameDemangler.DemangleClassPath(ov.ClassName);
        return string.IsNullOrEmpty(ov.Namespace) ? friendly : $"{ov.Namespace}.{friendly}";
    }

    /// <summary>Replace any non-[A-Za-z0-9_] with '_'. Empty input → "T".</summary>
    public static string MakeIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return "T";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "T" : sb.ToString();
    }
}
