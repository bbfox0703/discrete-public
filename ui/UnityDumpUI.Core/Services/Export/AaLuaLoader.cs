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
        sb.Append("    print('[discrete] ").Append(fileName)
          .AppendLine(" missing from CT — re-run the DiscreteUI action that pushed this entry.')");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  local ss = createStringStream()");
        sb.AppendLine("  ss.copyFrom(f.Stream, f.Stream.Size)");
        sb.Append("  local chunk, err = load(ss.DataString, '@").Append(fileName).AppendLine("')");
        sb.AppendLine("  ss.destroy()");
        sb.AppendLine("  if not chunk then");
        sb.Append("    print('[discrete] parse error in ").Append(fileName)
          .AppendLine(": ' .. tostring(err))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  chunk()");
        sb.AppendLine("end");
    }
}
