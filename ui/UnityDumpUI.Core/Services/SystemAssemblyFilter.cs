// SystemAssemblyFilter.cs — Phase 25 multi-assembly Symbol Finder helper.
//
// Heuristic that classifies an assembly name as "system" (Unity engine
// modules, BCL, Mono support libs, Microsoft.*) vs "user" (Assembly-CSharp,
// third-party gameplay DLLs).
//
// Used by SymbolFinderViewModel's "Search all user assemblies" toggle to
// compute the multi-assembly list it ships to the DLL via the `assemblies`
// array RPC arg.
//
// Conservative-by-design: matches only the well-known prefixes the user
// explicitly chose in the Phase 25 multi-assembly design Q&A. Third-party
// libs like DOTween / Newtonsoft.Json / Spine / TextMeshPro / Cinemachine
// stay in the user set so accidental "I was looking for that!" cases
// don't happen.
//
// AOT-safe: pure string ops, no reflection.

using System;
using System.Collections.Generic;

namespace UnityDumpUI.Core.Services;

public static class SystemAssemblyFilter
{
    // Prefixes that fully or namespace-prefix match a system assembly name.
    // Order doesn't matter — first hit wins. Comparison is ordinal so the
    // user can't slip a "system." namespace through with locale tricks.
    private static readonly string[] s_systemPrefixes = new[]
    {
        // BCL / standard library
        "System",          // matches "System", "System.Core", "System.Xml", …
        "mscorlib",
        "netstandard",
        "Microsoft",       // matches "Microsoft.CSharp", "Microsoft.Win32.Primitives", …
        "Mono",            // matches "Mono.Security", "Mono.Posix", …

        // Unity engine modules — all UnityEngine.* split-modules + the
        // umbrella "UnityEngine" itself. Unity 2017+ split into many
        // CoreModule / UIModule / PhysicsModule / etc. — all start with
        // "UnityEngine". The newer "Unity.*" namespace covers packages
        // like Unity.Burst, Unity.Mathematics, Unity.TextMeshPro,
        // Unity.InputSystem, Unity.Addressables…
        "UnityEngine",
        "Unity.",
    };

    /// <summary>
    /// True if <paramref name="assemblyName"/> looks like a system / Unity
    /// engine / BCL assembly. False for user gameplay assemblies like
    /// <c>Assembly-CSharp</c> or third-party packages.
    /// </summary>
    /// <remarks>
    /// Matches the prefix at a name boundary — either the full name equals
    /// the prefix (e.g. "System" == "System") or the next char is a '.'
    /// (e.g. "System.Core" matches "System."). Prevents false positives
    /// like "SystemicGame" being classed as System.
    /// </remarks>
    public static bool IsSystemAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return false;

        foreach (var prefix in s_systemPrefixes)
        {
            // Prefix already ends with '.' (e.g. "Unity.") — pure prefix
            // match is sufficient.
            if (prefix.EndsWith('.'))
            {
                if (assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
                continue;
            }

            // Prefix is a bare name (e.g. "System" / "UnityEngine"). Must
            // match either the full assembly name OR a name followed by '.'
            // to avoid swallowing "SystemicGame" / "UnityEngineHelper".
            if (!assemblyName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (assemblyName.Length == prefix.Length) return true;
            if (assemblyName[prefix.Length] == '.')   return true;
        }

        return false;
    }

    /// <summary>
    /// Filters <paramref name="assemblies"/> down to the user-assembly set
    /// (anything <see cref="IsSystemAssembly"/> returns false for).
    /// Returns a fresh list — does not mutate the input.
    /// </summary>
    public static List<string> SelectUserAssemblies(IEnumerable<string> assemblies)
    {
        var result = new List<string>();
        foreach (var name in assemblies)
        {
            if (!IsSystemAssembly(name)) result.Add(name);
        }
        return result;
    }
}
