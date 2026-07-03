// AaLuaLoader.cs — shared "self-load a bridge lua file from the CT" snippet
// for generated AA scripts.
//
// Action-driven model: each "Send to CE Table" / "Create AA" action embeds
// the one lua helper its generated AA needs (via InjectTableFile) and the AA
// self-loads it at runtime — instead of relying on a global bootstrap to
// pre-load every lua file. This factors the proven findTableFile + load +
// chunk pattern (first used by MonoRegisterSymbolAaScriptGenerator) so every
// generator emits the same robust, idempotent loader.

using System.Text;

namespace UnityDumpUI.Core.Services.Export;

internal static class AaLuaLoader
{
    /// <summary>
    /// Append a lua block that lazy-loads <paramref name="fileName"/> out of
    /// the cheat table and runs it (defining its globals). Guarded on
    /// <paramref name="sentinelGlobal"/> being nil so it loads once per CT
    /// even across many AAs. On a missing file it prints a clear instruction
    /// and <c>return</c>s from the enclosing AA [ENABLE] block (so the caller
    /// should emit this near the top, after the syntaxcheck guard).
    /// </summary>
    /// <param name="sb">Target builder (the [ENABLE] block being built).</param>
    /// <param name="fileName">CT table-file name, e.g. <c>unity_invoke.lua</c>.</param>
    /// <param name="sentinelGlobal">A global the file defines (e.g.
    /// <c>callUnityMethod</c>); when already present the load is skipped.</param>
    public static void AppendSelfLoad(StringBuilder sb, string fileName, string sentinelGlobal)
    {
        sb.Append("if _G.").Append(sentinelGlobal).AppendLine(" == nil then");
        sb.Append("  local f = findTableFile('").Append(fileName).AppendLine("')");
        sb.AppendLine("  if not f then");
        sb.AppendLine("    _G.DISCRETE_HAD_ERROR = true");
        sb.Append("    print('[discrete] ").Append(fileName)
          .AppendLine(" missing from CT — re-run the DiscreteUI action that pushed this entry.')");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  local ss = createStringStream()");
        sb.AppendLine("  ss.copyFrom(f.Stream, f.Stream.Size)");
        sb.Append("  local chunk, err = load(ss.DataString, '@").Append(fileName).AppendLine("')");
        sb.AppendLine("  ss.destroy()");
        sb.AppendLine("  if not chunk then");
        sb.AppendLine("    _G.DISCRETE_HAD_ERROR = true");
        sb.Append("    print('[discrete] parse error in ").Append(fileName)
          .AppendLine(": ' .. tostring(err))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  chunk()");
        sb.AppendLine("end");
    }

    /// <summary>
    /// Emit the shared "reset the did-anything-fail flag" line at the start of
    /// a generated AA {$lua} entry point (right after the syntaxcheck guard).
    /// See docs/lua-logging.md — every [ENABLE]/[DISABLE] block clears
    /// <c>_G.DISCRETE_HAD_ERROR</c> so the close-tail decides on THIS run's
    /// outcome, not a prior one's.
    /// </summary>
    public static void AppendHadErrorReset(StringBuilder sb)
    {
        sb.AppendLine("_G.DISCRETE_HAD_ERROR = false");
    }

    /// <summary>
    /// Emit the shared "close CE's Lua Engine on a clean run" tail as the last
    /// statements of a generated AA {$lua} block. Closes the engine only when
    /// DEBUG is off and no error was flagged, so a quiet success stops covering
    /// CE while a failed / debug run leaves the window up. See docs/lua-logging.md.
    /// </summary>
    public static void AppendCloseLuaEngineTail(StringBuilder sb)
    {
        sb.AppendLine("-- Close CE's Lua Engine on a clean run so it stops covering CE (docs/lua-logging.md).");
        sb.AppendLine("if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then");
        sb.AppendLine("  synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)");
        sb.AppendLine("end");
    }
}
