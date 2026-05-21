// ExportNameMap — collects (friendly → raw) class-name pairs touched
// during a CT / CSX / AA render, so the exporter can prepend a comment
// block that lets a reader recover the canonical Unity runtime name
// after the friendly form is shown in CE's UI.
//
// Why this exists: once we demangle "<CreateUnitResult>d__41" into
// "CreateUnitResult [async 41]" for display, the canonical name is
// lost from the visible output. A user inspecting the .CT later — or
// trying to reproduce a lookup via the Lua bridge — needs the raw
// form. The name map is an XML comment so CE strips it on load but
// any text editor / diff tool surfaces it.

using System.Collections.Generic;
using System.Text;

namespace UnityDumpUI.Core.Services;

public sealed class ExportNameMap
{
    // Friendly → raw. Multiple raws would only happen if the demangle
    // collapsed two distinct compiler-generated names to the same
    // friendly string, which our current patterns don't do; first-write
    // wins to keep the map deterministic.
    private readonly Dictionary<string, string> _map = new();

    /// <summary>
    /// Demangle <paramref name="raw"/> and record the mapping if it
    /// actually changed. Returns the friendly form for the caller to
    /// emit in user-visible output.
    /// </summary>
    public string Register(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string friendly = CompilerNameDemangler.DemangleClassPath(raw);
        if (friendly != raw && !_map.ContainsKey(friendly))
            _map[friendly] = raw;
        return friendly;
    }

    public bool HasEntries => _map.Count > 0;

    /// <summary>
    /// Render the collected map as an XML comment block. Returns
    /// <c>string.Empty</c> when nothing was demangled — the caller can
    /// emit the result unconditionally.
    /// </summary>
    public string ToXmlComment()
    {
        if (_map.Count == 0) return string.Empty;
        var sb = new StringBuilder(_map.Count * 80 + 128);
        sb.Append("<!-- discrete: compiler-generated name map\n");
        sb.Append("     friendly form (left)  ←  Unity runtime form (right)\n");
        sb.Append("     CE shows the friendly form; the right column is\n");
        sb.Append("     what mono_findClass / findInstances expect.\n");
        // Sort to keep the block diff-stable across runs.
        var keys = new List<string>(_map.Keys);
        keys.Sort(System.StringComparer.Ordinal);
        int pad = 0;
        foreach (string k in keys) if (k.Length > pad) pad = k.Length;
        // Cap padding so a freakishly long entry doesn't push the right
        // column off-screen in the user's editor.
        if (pad > 60) pad = 60;
        foreach (string k in keys)
        {
            sb.Append("     ");
            sb.Append(k);
            int gap = pad - k.Length + 2;
            if (gap < 2) gap = 2;
            sb.Append(' ', gap);
            sb.Append("←  ");
            // Raw runtime names contain no '-' or '--', so they're safe
            // inside an XML comment without escaping. (XML forbids '--'
            // inside comments — none of our patterns produce it.)
            sb.Append(_map[k]);
            sb.Append('\n');
        }
        sb.Append("-->\n");
        return sb.ToString();
    }
}
