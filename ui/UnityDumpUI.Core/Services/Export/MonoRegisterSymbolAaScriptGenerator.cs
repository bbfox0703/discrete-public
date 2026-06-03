// MonoRegisterSymbolAaScriptGenerator.cs — DLL-free Mono symbol register.
//
// Produces an Auto-Assembler script that registers one or more Mono
// method overloads as CE symbols WITHOUT ever injecting the
// Discrete DLL. Uses CE's built-in MonoDataCollector Lua API
// (mono_findClass / mono_class_enumMethods / mono_method_get_parameters
// / mono_compile_method) via the helper layer shipped in
// scripts/mono_registersymbol_helpers.lua.
//
// Emits the discrete-namespaced helpers — `discrete_mono_registerSymbolBy
// SignatureMatch` for overloads with params (sigContains = param types,
// sigExcludes = sibling-exclusive tokens) and `discrete_mono_registerSymbolEx`
// with an empty table for the zero-arg overload (an empty sigContains would
// match any overload). Both JIT-resolve the live method and registerSymbol()
// its REAL address, so the symbol survives a game relaunch — no hardcoded
// address, no fragile prologue AOB. NOT the public `mono_*` versions: those names
// share a namespace with hand-written CE-forums autorun scripts that
// often pre-register their own helpers without an attach step; checking
// against discrete_* keeps our generator independent of whatever's in
// the user's autorun.
//
// The helper file is bundled into the cheat table by Tools → Inject
// Discrete Bridge (BridgeLuaResources.LoadOrder includes it). The
// generated AA's [ENABLE] pulls it out of the CT via findTableFile
// + chunk-load with idempotency guard — so re-enabling, or running
// multiple register AAs from the same cheat table, only loads once.
//
// Why a separate generator from AobAaScriptGenerator:
//   * Different runtime model — register-by-name vs AOB-scan
//   * Different prerequisite — needs the helper file in the CT
//   * Mono-only by definition (IL2CPP has no JIT, no mono_* Lua API)
//   * The user already had a hand-rolled mono_registerSymbol* pattern
//     in their cheat tables; this generator emits the exact same calls
//     so it slots into their existing muscle memory.
//
// Mono-only — IL2CPP overloads should never reach here. The UI layer
// is responsible for hiding the trigger button on IL2CPP targets;
// we assert in Build() so a programming error surfaces loudly rather
// than emitting a broken AA.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnityDumpUI.Core.Services.Export;

public static class MonoRegisterSymbolAaScriptGenerator
{
    /// <summary>
    /// CT-side filename of the helper bundle. Matches the
    /// <c>BridgeLuaResources.LoadOrder</c> entry one-for-one;
    /// findTableFile() is case-sensitive on AOBMaker's side.
    /// </summary>
    public const string HelpersFileName = "mono_registersymbol_helpers.lua";

    /// <summary>
    /// Build the AA script for the given overloads. Every overload must
    /// have <see cref="AaScriptOverload.ModuleName"/> empty — populated
    /// ModuleName indicates an IL2CPP method (AOT addresses live in
    /// GameAssembly.dll); the DLL-free Mono path is meaningless for
    /// IL2CPP. The UI layer gates this for us; the runtime guard catches
    /// misuse.
    /// </summary>
    public static string Build(IReadOnlyList<AaScriptOverload> overloads)
    {
        if (overloads is null || overloads.Count == 0)
            throw new ArgumentException("at least one overload is required", nameof(overloads));

        for (int i = 0; i < overloads.Count; ++i)
        {
            if (!string.IsNullOrEmpty(overloads[i].ModuleName))
                throw new ArgumentException(
                    $"overload[{i}] '{overloads[i].MethodName}' has ModuleName " +
                    $"'{overloads[i].ModuleName}' set — DLL-free Mono register only " +
                    $"applies to Mono JIT overloads (ModuleName empty).",
                    nameof(overloads));
        }

        var symbols = new string[overloads.Count];
        var used    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < overloads.Count; ++i)
        {
            symbols[i] = AobAaScriptGenerator.MakeSymbolName(overloads[i], used);
            used.Add(symbols[i]);
        }

        var sb = new StringBuilder(2048);

        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        sb.AppendLine();

        // Lazy-load the helpers from the CT. Idempotency keys off the
        // discrete-namespaced symbol — the user-facing `mono_*` names
        // share a namespace with hand-written autorun scripts (CE forums
        // pattern), which would override my version's attach + robust
        // class-lookup logic. Checking the discrete-namespaced symbol
        // guarantees my helper code is what runs.
        // If the helper file is missing — user hasn't run "Inject Discrete
        // Bridge" yet — print a clear instruction and bail. No silent
        // no-op (the worst failure mode from Phase 22 live smoke).
        sb.AppendLine("-- Lazy-load DLL-free Mono helpers from the cheat table.");
        sb.AppendLine("-- Idempotent across multiple register AAs on the same CT.");
        sb.AppendLine("if _G.discrete_mono_registerSymbolBySignatureMatch == nil then");
        sb.Append("  local f = findTableFile('").Append(HelpersFileName).AppendLine("')");
        sb.AppendLine("  if not f then");
        sb.Append("    print('[discrete] ").Append(HelpersFileName).AppendLine(" missing from CT.')");
        sb.AppendLine("    print('[discrete] Run DiscreteUI -> Tools -> Inject Discrete Bridge into CE Table first.')");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  local ss = createStringStream()");
        sb.AppendLine("  ss.copyFrom(f.Stream, f.Stream.Size)");
        sb.Append("  local chunk, err = load(ss.DataString, '@").Append(HelpersFileName).AppendLine("')");
        sb.AppendLine("  ss.destroy()");
        sb.AppendLine("  if not chunk then");
        sb.Append("    print('[discrete] parse error in ").Append(HelpersFileName).AppendLine(": ' .. tostring(err))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  chunk()");
        sb.AppendLine("end");
        sb.AppendLine();

        // One register call per overload. Both helpers JIT-compile the live
        // method and registerSymbol() its real address, so the symbol survives
        // a game relaunch (unlike a hardcoded address) and doesn't depend on a
        // near-duplicate method-prologue AOB. The helper auto-calls
        // _discrete_ensure_attached() and uses an image-iteration fallback for
        // class lookup (Unity 2022+ split-module workaround).
        //
        // Per overload:
        //   * params present → registerSymbolBySignatureMatch with the param
        //     types as sigContains (full names dodge the Int32/UInt32 substring
        //     trap) + sibling-exclusive tokens as sigExcludes (so a 1-param
        //     overload isn't matched against a 2-param sibling).
        //   * zero-arg → registerSymbolEx with {} — an empty sigContains would
        //     match ANY overload, so the param-COUNT check is the only precise
        //     way to pin the no-arg overload.
        sb.AppendLine("-- Register one symbol per overload (JIT-resolved at enable, relaunch-safe).");
        for (int i = 0; i < overloads.Count; ++i)
        {
            var ov = overloads[i];

            // Diagnostic comment line — what this register call resolves
            // to according to our last live scan. Reading this in the
            // generated AA tells the user which overload is which.
            sb.Append("-- ")
              .Append(ClassDisplay(ov)).Append('.').Append(ov.MethodName)
              .Append(ov.Signature)
              .Append("   (last-seen address ").Append(ov.Address ?? "?").AppendLine(")");

            bool hasParams = ov.ParamTypes is { Count: > 0 };
            if (hasParams)
            {
                sb.Append("_G.discrete_mono_registerSymbolBySignatureMatch('").Append(LuaSqEscape(symbols[i])).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.Namespace ?? "")).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.ClassName)).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.MethodName)).Append("', ")
                  .Append(RenderParamTypesTable(ov.ParamTypes)).Append(", ")
                  .Append(RenderParamTypesTable(ov.SigExcludes)).AppendLine(")");
            }
            else
            {
                sb.Append("_G.discrete_mono_registerSymbolEx('").Append(LuaSqEscape(symbols[i])).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.Namespace ?? "")).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.ClassName)).Append("', ")
                  .Append('\'').Append(LuaSqEscape(ov.MethodName)).Append("', {})").AppendLine();
            }
            sb.AppendLine();
        }

        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        // unregisterSymbol may not exist if [ENABLE] bailed before
        // registering anything (helper file missing, etc.). pcall keeps
        // the disable path harmless in that case.
        for (int i = 0; i < overloads.Count; ++i)
        {
            sb.Append("pcall(unregisterSymbol, '")
              .Append(LuaSqEscape(symbols[i])).AppendLine("')");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Render the paramTypes argument as a Lua table literal:
    /// <c>{ 'System.Int32', 'UnityEngine.Vector3' }</c>. Null / empty
    /// list → <c>{}</c> (matches only zero-param overloads via
    /// <c>mono_registerSymbolEx</c>'s <c>#p.parameters ~= 0</c> check).
    /// </summary>
    public static string RenderParamTypesTable(IReadOnlyList<string>? types)
    {
        if (types is null || types.Count == 0) return "{}";

        var sb = new StringBuilder(64);
        sb.Append("{ ");
        for (int i = 0; i < types.Count; ++i)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('\'').Append(LuaSqEscape(types[i] ?? string.Empty)).Append('\'');
        }
        sb.Append(" }");
        return sb.ToString();
    }

    /// <summary>
    /// Escape a string for a Lua single-quoted literal. Same shape as
    /// <c>AobAaScriptGenerator.LuaSqEscape</c> — kept duplicated rather
    /// than exposing the AOB generator's private helper so each
    /// generator stays self-contained.
    /// </summary>
    private static string LuaSqEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\")
                .Replace("'",  "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }

    private static string ClassDisplay(AaScriptOverload ov) =>
        string.IsNullOrEmpty(ov.Namespace) ? ov.ClassName : $"{ov.Namespace}.{ov.ClassName}";
}
