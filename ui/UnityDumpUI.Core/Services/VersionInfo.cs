// VersionInfo.cs — runtime accessor for the assembly version metadata
// stamped in by MSBuild from the root CMakeLists.txt's project() VERSION
// + build_number.txt.
//
// Major/Minor/Patch/Build are kept in sync with the C++ side
// (DISCRETE_FILE_VERSION_STRING in dll/src/shared/BuildInfo.h.in)
// so a UI log entry's version string is directly comparable to the DLL
// log entry's version string for the same session.
//
// AOT-safe: AssemblyName.Version is a property on metadata, no reflection
// path the trimmer can drop. We read off `typeof(...).Assembly` rather
// than `Assembly.GetEntryAssembly()` so this also works inside test runs
// where the entry assembly is xUnit's, not ours.

using System.Reflection;

namespace UnityDumpUI.Core.Services;

public static class VersionInfo
{
    /// <summary>
    /// Four-part version string e.g. "0.1.0.135". Matches the PE
    /// FileVersion shown in File Explorer's Properties pane and the DLL
    /// log banner's first three components.
    /// </summary>
    public static string FileVersion { get; } = ResolveFileVersion();

    /// <summary>
    /// One-line banner suitable for the head of each fresh log file:
    /// <c>"DiscreteUI 0.1.0.135"</c>. Appended to by callers if they
    /// want to add their own scope ("ui session start", "VM created", …).
    /// </summary>
    public static string Banner => $"DiscreteUI {FileVersion}";

    private static string ResolveFileVersion()
    {
        Assembly asm = typeof(VersionInfo).Assembly;
        // Prefer AssemblyInformationalVersion if set (matches what MSBuild
        // assigns from <InformationalVersion> in the csproj); fall back to
        // the simpler AssemblyVersion. Both come from the same source in
        // our build, so the values are identical — the lookup just guards
        // against one being trimmed in some future config.
        string? info = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return asm.GetName().Version?.ToString() ?? "0.0.0.0";
        }

        // .NET's Microsoft.NET.Sdk appends "+<full-git-sha>" to
        // AssemblyInformationalVersion via SourceRevisionId by default —
        // useful but noisy in logs ("DiscreteUI 0.1.0.139+261b0f2dcefc2efa0..."
        // dominates the line). Shorten to 7 chars to match git short.
        int plus = info.IndexOf('+');
        if (plus < 0 || plus == info.Length - 1) return info;
        string semver = info.Substring(0, plus);
        string sha    = info.Substring(plus + 1);
        if (sha.Length > 7) sha = sha.Substring(0, 7);
        return $"{semver}+{sha}";
    }
}
