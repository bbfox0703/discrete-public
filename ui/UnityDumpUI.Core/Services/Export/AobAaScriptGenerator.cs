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

        sb.AppendLine("{$asm}");
        for (int i = 0; i < overloads.Count; ++i)
        {
            var ov = overloads[i];
            // Trailing comment carries the pre-scan address + module offset
            // for cross-reference / quick sanity checking.
            sb.Append("// ")
              .Append(ClassDisplay(ov)).Append('.').Append(ov.MethodName)
              .Append(ov.Signature).Append("  ")
              .Append("→ ").Append(ov.Address)
              .Append("  (").Append(ov.ModuleName).Append('+').Append(ov.ModuleOffset).AppendLine(")");
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
        for (int i = 0; i < overloads.Count; ++i)
        {
            sb.Append("_G.discrete_logScan(\"").Append(symbols[i]).AppendLine("\")");
        }
        sb.AppendLine();

        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$asm}");
        for (int i = 0; i < overloads.Count; ++i)
        {
            sb.Append("unregistersymbol(").Append(symbols[i]).AppendLine(")");
        }

        return sb.ToString();
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

    private static string ClassDisplay(AaScriptOverload ov) =>
        string.IsNullOrEmpty(ov.Namespace) ? ov.ClassName : $"{ov.Namespace}.{ov.ClassName}";

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
