// MonoRegisterSymbolAaScriptGenerator.cs — DLL-free Mono symbol register.
//
// Produces an Auto-Assembler script that registers one or more Mono
// method overloads as CE symbols WITHOUT ever injecting the
// Discrete DLL. Uses CE's built-in MonoDataCollector Lua API
// (mono_findClass / mono_class_enumMethods / mono_method_get_parameters
// / mono_compile_method) via the helper layer shipped in
// scripts/mono_registersymbol_helpers.lua.
//
// Emits the discrete-namespaced `discrete_mono_registerSymbolEx` helper for
// every overload (both zero-arg `{}` and N-arg ordered `{ 'System.Int32', ... }`).
// `discrete_mono_registerSymbolEx` checks exact parameter count and exact
// parameter order via typename + positional signature fallback with primitive
// alias normalization (`System.Int32` ↔ `int`, `System.Single` ↔ `single`,
// `System.String` ↔ `string`, exact array/byref suffixes) and refuses ambiguous
// multi-overload matches. Both JIT-resolve the live method and registerSymbol()
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
// UNIVERSAL across Unity backends. CE's "Active Mono features" dissector
// drives the mono_* Lua API on BOTH Mono AND IL2CPP games, so signature-match
// registration resolves the live method address on either — no module name
// needed (the prerequisite is purely "Active Mono features enabled" in CE).
// Earlier revisions asserted ModuleName=="" here ("Mono JIT only"); that was
// wrong — IL2CPP overloads carry a GameAssembly.dll module name but still
// register fine via the runtime signature match.

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
    /// Build the AA script for the given overloads. Works for BOTH Mono and
    /// IL2CPP overloads — signature-match registration goes through CE's Active
    /// Mono features dissector, which handles either. <see cref="AaScriptOverload.ModuleName"/>
    /// is ignored here (the script resolves by class+method+signature, not by
    /// module/offset), so an IL2CPP method's GameAssembly.dll module name is
    /// harmless.
    /// </summary>
    public static string Build(IReadOnlyList<AaScriptOverload> overloads)
    {
        if (overloads is null || overloads.Count == 0)
            throw new ArgumentException("at least one overload is required", nameof(overloads));

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
        // Reset the shared did-anything-fail flag so the close-tail below judges
        // THIS run (docs/lua-logging.md). Any failure path sets it true again.
        AaLuaLoader.AppendHadErrorReset(sb);
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
        sb.AppendLine("if _G.discrete_mono_registerSymbolEx == nil then");
        sb.Append("  local f = findTableFile('").Append(HelpersFileName).AppendLine("')");
        sb.AppendLine("  if not f then");
        sb.AppendLine("    _G.DISCRETE_HAD_ERROR = true");
        sb.Append("    error('[Discrete] ").Append(HelpersFileName)
          .AppendLine(" missing from CT. Run DiscreteUI -> Tools -> Inject Discrete Bridge into CE Table first.')");
        sb.AppendLine("  end");
        sb.AppendLine("  local ss = createStringStream()");
        sb.AppendLine("  ss.copyFrom(f.Stream, f.Stream.Size)");
        sb.Append("  local chunk, err = load(ss.DataString, '@").Append(HelpersFileName).AppendLine("')");
        sb.AppendLine("  ss.destroy()");
        sb.AppendLine("  if not chunk then");
        sb.AppendLine("    _G.DISCRETE_HAD_ERROR = true");
        sb.Append("    error('[Discrete] parse error in ").Append(HelpersFileName)
          .AppendLine(": ' .. tostring(err))");
        sb.AppendLine("  end");
        sb.AppendLine("  chunk()");
        sb.AppendLine("end");
        sb.AppendLine();

        // One register call per overload. `discrete_mono_registerSymbolEx`
        // JIT-compiles the live method and registerSymbol()s its real address,
        // checking exact parameter count AND exact parameter order via
        // typename + positional signature fallback (with primitive alias
        // normalization for Mono's `int`/`single`/`string` shorthands). This
        // cleanly distinguishes swapped-order overloads like (Int32, String)
        // vs (String, Int32), repeated-type overloads like (Int32) vs
        // (Int32, Int32), and array suffixes like Int32 vs Int32[].
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

            sb.Append("_G.discrete_mono_registerSymbolEx('").Append(LuaSqEscape(symbols[i])).Append("', ")
              .Append('\'').Append(LuaSqEscape(ov.Namespace ?? "")).Append("', ")
              .Append('\'').Append(LuaSqEscape(ov.ClassName)).Append("', ")
              .Append('\'').Append(LuaSqEscape(ov.MethodName)).Append("', ")
              .Append(RenderParamTypesTable(ov.ParamTypes)).AppendLine(")");
            sb.AppendLine();
        }

        // Quiet success closes CE's Lua Engine; a helper failure set
        // _G.DISCRETE_HAD_ERROR (via its eprint) so the window stays up.
        AaLuaLoader.AppendCloseLuaEngineTail(sb);
        sb.AppendLine();

        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        // Reset here too. _G.DISCRETE_HAD_ERROR lives in CE's process-lifetime
        // Lua state, so without this the close-tail below reads whatever a
        // PREVIOUS run left behind: one failed [ENABLE] and every later disable
        // sees a stale `true` and refuses to close the Lua Engine, leaving it
        // covering CE's main window for the rest of the session. It is not
        // enough that this block cannot itself fail (the pcalls below see to
        // that) — see AaLuaLoader.AppendHadErrorReset, whose contract is that
        // EVERY [ENABLE]/[DISABLE] block clears the flag so the close-tail
        // judges this run, not an earlier one.
        AaLuaLoader.AppendHadErrorReset(sb);
        // unregisterSymbol may not exist if [ENABLE] bailed before
        // registering anything (helper file missing, etc.). pcall keeps
        // the disable path harmless in that case.
        for (int i = 0; i < overloads.Count; ++i)
        {
            sb.Append("pcall(unregisterSymbol, '")
              .Append(LuaSqEscape(symbols[i])).AppendLine("')");
        }

        // Close the Lua Engine on a clean disable too. The unregister failures
        // above are swallowed by pcall, so this run is always quiet — and the
        // reset at the top of the block means "quiet" is now actually what the
        // close-tail observes.
        AaLuaLoader.AppendCloseLuaEngineTail(sb);

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
