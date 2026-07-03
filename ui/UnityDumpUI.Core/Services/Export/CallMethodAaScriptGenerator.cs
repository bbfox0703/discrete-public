// CallMethodAaScriptGenerator.cs — Phase 22e.
//
// Wraps the user's filled-in MethodCallWindow inputs into an AA script
// whose [ENABLE] block fires the corresponding callUnityMethod() through
// scripts/unity_invoke.lua. Pushed into CE's address list via
// IAobMakerBridge.CreateAAScriptAsync (Phase 22a). End-user just toggles
// the entry's checkbox to repeat the call.
//
// Output shape:
//
//   [ENABLE]
//   {$lua}
//   if syntaxcheck then return end
//   if not _G.discrete_bridgeLoaded then
//     print('[Discrete] Enable "Load Discrete Bridge" first')
//     return
//   end
//   local args = {
//     argInt32(7),
//     argString('hello'),
//   }
//   local r = callUnityMethod(
//     'mscorlib', 'System', 'Math', 'Abs',
//     args,
//     { signatureMatch = '(System.Int32)' })
//   prettyPrint(r)
//
//   [DISABLE]
//   {$lua}
//   -- callMethod has no teardown — re-disable the entry to clear it from
//   -- CE's "active scripts" view.
//
// Bridge-loaded guard: every generated [ENABLE] short-circuits if
// _G.discrete_bridgeLoaded is false. Flag is set by the Phase 22c
// bootstrap script. Scripts emitted here are useless without the bridge
// (callUnityMethod / argInt32 / etc. live inside unity_invoke.lua), so
// the early return + diagnostic print is the right failure mode.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnityDumpUI.Core.Services.Export;

/// <summary>
/// One filled-in argument row for the call-method dialog. Mirrors the
/// runtime shape MethodCallViewModel uses (TypeName + ValueText +
/// IsNull) so the VM can hand its rows straight in without remapping.
/// </summary>
public sealed class CallMethodArg
{
    public required string TypeName  { get; init; }   // "System.Int32" / "System.String" / …
    public required string ValueText { get; init; }   // user's text input (ignored when IsNull)
    public          bool   IsNull    { get; init; }
}

public static class CallMethodAaScriptGenerator
{
    /// <summary>
    /// Build the AA script for one filled-in MethodCallWindow request.
    /// </summary>
    /// <param name="assembly">CLR assembly name (e.g. "Assembly-CSharp").</param>
    /// <param name="namespace">CLR namespace (empty string for global).</param>
    /// <param name="className">CLR class name (slash for nested types).</param>
    /// <param name="methodName">CLR method name.</param>
    /// <param name="signatureMatch">Optional (System.Int32) / (System.Int32, System.String) suffix to disambiguate overloads. Pass null/empty when there's only one overload.</param>
    /// <param name="instanceAddress">Hex-formatted instance pointer for instance dispatch ("0x..." or bare hex). Null/empty for static methods.</param>
    /// <param name="args">Filled-in arg rows in declaration order.</param>
    public static string Build(string assembly, string @namespace, string className,
                               string methodName, string? signatureMatch,
                               string? instanceAddress,
                               IReadOnlyList<CallMethodArg> args)
    {
        if (string.IsNullOrEmpty(assembly))   throw new ArgumentException("assembly required",   nameof(assembly));
        if (string.IsNullOrEmpty(className))  throw new ArgumentException("className required",  nameof(className));
        if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("methodName required", nameof(methodName));
        args ??= Array.Empty<CallMethodArg>();

        var sb = new StringBuilder(512);
        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        // DLL / pipe must be up (Load Discrete Bridge enabled)…
        AppendBridgeGuard(sb);
        // …then self-load our lua dependency from the CT (the Send-to-CE-Table
        // action embedded unity_invoke.lua alongside this entry).
        AaLuaLoader.AppendSelfLoad(sb, "unity_invoke.lua", "callUnityMethod");
        sb.AppendLine();

        // Args list. Empty list → `local args = {}` so the call site
        // doesn't have to special-case zero-arg methods.
        sb.AppendLine("local args = {");
        foreach (var a in args)
            sb.Append("  ").Append(BuildArgBuilder(a)).AppendLine(",");
        sb.AppendLine("}");
        sb.AppendLine();

        // The opts table is conditional — only emit when at least one
        // option is set, to keep zero-overhead common cases readable.
        bool hasSignatureMatch = !string.IsNullOrEmpty(signatureMatch);
        bool hasInstance       = !string.IsNullOrEmpty(instanceAddress);
        bool hasOpts           = hasSignatureMatch || hasInstance;

        sb.AppendLine("local r = callUnityMethod(");
        sb.Append("  '").Append(EscapeSq(assembly)).Append("', '")
                       .Append(EscapeSq(@namespace ?? string.Empty)).Append("', '")
                       .Append(EscapeSq(className)).Append("', '")
                       .Append(EscapeSq(methodName)).AppendLine("',");
        if (hasOpts)
        {
            sb.AppendLine("  args,");
            sb.Append("  { ");
            bool first = true;
            if (hasSignatureMatch)
            {
                sb.Append("signatureMatch = '").Append(EscapeSq(signatureMatch!)).Append('\'');
                first = false;
            }
            if (hasInstance)
            {
                if (!first) sb.Append(", ");
                sb.Append("instance = '").Append(EscapeSq(instanceAddress!)).Append('\'');
            }
            sb.AppendLine(" })");
        }
        else
        {
            sb.AppendLine("  args)");
        }
        sb.AppendLine("prettyPrint(r)");
        // NB: deliberately NO close-tail here. A call-method AA exists to SHOW
        // the result — prettyPrint always prints (see unity_invoke.lua) — so we
        // leave CE's Lua Engine open on this action rather than closing it like
        // the quiet register / address-lookup generators. (docs/lua-logging.md)
        sb.AppendLine();

        // [DISABLE]: nothing to tear down. CE re-fires [ENABLE] on every
        // toggle, which is exactly the "repeat the call" behaviour the
        // user wants. Empty {$lua} block keeps the entry visually
        // symmetric (every AA we generate has both blocks).
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("-- callMethod has no teardown — re-toggle to repeat the call");

        return sb.ToString();
    }

    /// <summary>
    /// Suggested CE memory-record description. Stable per call site so
    /// users can recognise the entry across CT versions, and so a future
    /// AOBMaker duplicate-detection feature has a stable key.
    /// </summary>
    public static string SuggestDescription(string className, string methodName,
                                            string? signatureMatch)
    {
        // "ClassName.Method (System.Int32)" — matches the shape Phase 18c
        // uses for AOB AA descriptions, just with the call-method tag.
        string sig = string.IsNullOrEmpty(signatureMatch) ? "()" : signatureMatch!;
        return $"call: {className}.{methodName}{sig}";
    }

    // ---------- Internals ----------------------------------------------

    private static void AppendBridgeGuard(StringBuilder sb)
    {
        sb.AppendLine("if not _G.discrete_bridgeLoaded then");
        sb.AppendLine("  print('[Discrete] Enable \"Load Discrete Bridge\" first')");
        sb.AppendLine("  return");
        sb.AppendLine("end");
    }

    /// <summary>
    /// Map one arg row to its <c>argTYPE(v)</c> Lua call. The arg
    /// builders live in <c>scripts/unity_invoke.lua</c> — keep this
    /// table in sync if/when new ones land.
    /// </summary>
    private static string BuildArgBuilder(CallMethodArg a)
    {
        if (a.IsNull) return "argNull()";
        string raw = a.ValueText ?? string.Empty;
        var inv = CultureInfo.InvariantCulture;

        switch (a.TypeName)
        {
        case "System.Boolean":
            // Lua bool literal: lower-case true/false. Be lenient on input.
            return $"argBool({(bool.TryParse(raw, out bool bv) && bv ? "true" : "false")})";

        case "System.Byte":   return $"argByte({UInt(raw,  byte.MaxValue)})";
        case "System.SByte":  return $"argSByte({Int(raw,  sbyte.MinValue, sbyte.MaxValue)})";
        case "System.Int16":  return $"argInt16({Int(raw,  short.MinValue, short.MaxValue)})";
        case "System.UInt16": return $"argUInt16({UInt(raw, ushort.MaxValue)})";
        case "System.Int32":  return $"argInt32({Int(raw,  int.MinValue,   int.MaxValue)})";
        case "System.UInt32": return $"argUInt32({UInt(raw, uint.MaxValue)})";
        case "System.Int64":  return $"argInt64({(long.TryParse(raw, NumberStyles.Integer, inv, out long lv) ? lv : 0L).ToString(inv)})";
        case "System.UInt64": return $"argUInt64({(ulong.TryParse(raw, NumberStyles.Integer, inv, out ulong uv) ? uv : 0UL).ToString(inv)})";

        case "System.Single":
            return $"argFloat({(double.TryParse(raw, NumberStyles.Float, inv, out double f) ? f : 0.0).ToString("R", inv)})";
        case "System.Double":
            return $"argDouble({(double.TryParse(raw, NumberStyles.Float, inv, out double d) ? d : 0.0).ToString("R", inv)})";

        case "System.Char":
            // Single character → its UTF-16 code unit. Numeric input is
            // also accepted (matches MethodCallViewModel.TryChar).
            if (raw.Length == 1) return $"argChar({(int)raw[0]})";
            if (ushort.TryParse(raw, NumberStyles.Integer, inv, out ushort cv))
                return $"argChar({cv})";
            return "argChar(0)";

        case "System.String":
            // Empty input becomes "" — distinguish from null (which only
            // fires when IsNull is checked).
            return $"argString('{EscapeSq(raw)}')";

        case "System.IntPtr":
        case "System.UIntPtr":
            return $"argAddress('{EscapeSq(raw)}')";
        }

        // Unknown / non-primitive formal type. Same fallback as
        // MethodCallViewModel.TryBuildArg: empty input → null reference,
        // otherwise treat as an address literal (typical for reference-type
        // params where the user pastes a findInstances address).
        if (string.IsNullOrWhiteSpace(raw)) return "argNull()";
        return $"argAddress('{EscapeSq(raw)}')";
    }

    private static string Int(string raw, long min, long max)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!long.TryParse(raw, NumberStyles.Integer, inv, out long v)) return "0";
        if (v < min) v = min;
        if (v > max) v = max;
        return v.ToString(inv);
    }

    private static string UInt(string raw, ulong max)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!ulong.TryParse(raw, NumberStyles.Integer, inv, out ulong v)) return "0";
        if (v > max) v = max;
        return v.ToString(inv);
    }

    /// <summary>
    /// Escape a string for embedding inside a Lua single-quoted literal
    /// (<c>'...'</c>). Conservative — backslash, single quote, CR, LF,
    /// NUL. Other bytes pass through.
    /// </summary>
    internal static string EscapeSq(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
            case '\\': sb.Append("\\\\"); break;
            case '\'': sb.Append("\\'");  break;
            case '\n': sb.Append("\\n");  break;
            case '\r': sb.Append("\\r");  break;
            case '\0': sb.Append("\\0");  break;
            default:   sb.Append(c);      break;
            }
        }
        return sb.ToString();
    }
}
