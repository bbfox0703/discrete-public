// IUnityGameScanner.cs — Phase 12c follow-up. Scans the host machine's
// Steam libraries for installed Unity games, identifies engine backend
// (Mono / IL2CPP) + executable bitness, and returns enough info for the
// proxy-install dialog to skip the manual file-pick step.
//
// Detection patterns mirror UE5CEDumper's deploy-proxy tab:
//   - <gameDir>\UnityPlayer.dll                  → Unity engine marker
//   - <gameDir>\GameAssembly.dll                 → IL2CPP backend
//   - <gameDir>\MonoBleedingEdge\               → Mono backend (newer Unity)
//   - <gameDir>\<exeStem>_Data\Managed\         → Mono managed assemblies
//   - <gameDir>\<exeStem>_Data\il2cpp_data\     → IL2CPP metadata folder
//
// Bitness comes from the .exe's PE header (IMAGE_FILE_MACHINE field).
// Mono is supported on BOTH x64 and x86 (x86 backend + proxy ship in
// dist/x86/; the installer auto-picks the matching build). IL2CPP is
// x64-only — there is no 32-bit IL2CPP backend — so an x86 IL2CPP game
// stays unsupported and the UI surfaces it as such. See Phase 12d / 12e.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Platform;

/// <summary>
/// Bitness reported by the .exe's PE header. <see cref="Unknown"/>
/// covers ARM64 / IA64 / corrupt PE headers / unreadable files — all
/// non-actionable for the dumper today.
/// </summary>
public enum ExeBitness
{
    Unknown,
    X64,
    X86,
}

/// <summary>
/// One row in the scan result. Backend / Bitness / DataDirectoryName
/// come from filesystem probes — no PE-import-table parsing needed.
/// </summary>
public sealed class UnityGameEntry
{
    public UnityGameEntry(string exePath, string gameDirectory, string displayName,
                           UnityBackend backend, ExeBitness bitness,
                           string? dataDirectoryName,
                           ProxyKind installedProxy,
                           string source,
                           string? installedProxyVersion = null,
                           string? sourceProxyVersion    = null,
                           string? foreignProxyPresent   = null)
    {
        ExePath               = exePath;
        GameDirectory         = gameDirectory;
        DisplayName           = displayName;
        Backend               = backend;
        Bitness               = bitness;
        DataDirectoryName     = dataDirectoryName;
        InstalledProxy        = installedProxy;
        Source                = source;
        InstalledProxyVersion = installedProxyVersion;
        SourceProxyVersion    = sourceProxyVersion;
        ForeignProxyPresent   = foreignProxyPresent;
    }

    public string       ExePath           { get; }
    public string       GameDirectory     { get; }
    public string       DisplayName       { get; }
    public UnityBackend Backend           { get; }
    public ExeBitness   Bitness           { get; }
    /// <summary>e.g. <c>"SaGa Frontier 2 Remastered_Data"</c>; null if not detected.</summary>
    public string?      DataDirectoryName { get; }
    public ProxyKind    InstalledProxy    { get; }
    /// <summary>Free-form provenance string ("Steam library: D:\…", "Manual", …).</summary>
    public string       Source            { get; }
    /// <summary>PE FileVersion of the installed proxy DLL, or null.</summary>
    public string?      InstalledProxyVersion { get; }
    /// <summary>PE FileVersion of the proxy DLL in <c>dist/</c> we'd install,
    /// or null if the source DLL is missing. Same string for every row in a
    /// given scan — surfaced per-row so the deploy grid can show the
    /// installed/source comparison directly.</summary>
    public string?      SourceProxyVersion    { get; }
    /// <summary>Third-party proxy DLL filename(s) present next to the game .exe
    /// (e.g. ReShade's <c>version.dll</c>), or null when none.</summary>
    public string?      ForeignProxyPresent   { get; }

    public string BackendLabel => Backend switch
    {
        UnityBackend.IL2CPP => "IL2CPP",
        UnityBackend.Mono   => "Mono",
        _                   => "Unknown",
    };

    public string BitnessLabel => Bitness switch
    {
        ExeBitness.X64     => "x64",
        ExeBitness.X86     => "x86",
        _                  => "?",
    };

    public string ProxyLabel => InstalledProxy switch
    {
        ProxyKind.Version  => "version.dll",
        ProxyKind.Winmm    => "winmm.dll",
        _                  => "—",
    };

    /// <summary>True when the dumper actually supports this target today.
    /// x64 covers both Mono and IL2CPP. x86 covers Mono only — there is no
    /// 32-bit IL2CPP backend (no shipping IL2CPP target is 32-bit), so an
    /// x86 IL2CPP game stays unsupported.</summary>
    public bool IsSupported =>
        Backend != UnityBackend.None &&
        (Bitness == ExeBitness.X64 ||
         (Bitness == ExeBitness.X86 && Backend == UnityBackend.Mono));

    /// <summary>The one supported 32-bit case — x86 Mono. (There is no
    /// 32-bit IL2CPP backend, so every other 32-bit target is unsupported.)
    /// Surfaced so the Proxy Deploy filter can gate these rows behind their
    /// own "Show 32-bit Mono" toggle — on by default — separately from the
    /// truly-unsupported rows gated by "Show unsupported".</summary>
    public bool Is32BitMono =>
        Backend == UnityBackend.Mono && Bitness == ExeBitness.X86;
}

public interface IUnityGameScanner
{
    /// <summary>
    /// Walk every Steam library on this machine and return one entry per
    /// detected Unity game. Cancellable. Soft-fails to an empty list when
    /// Steam isn't installed.
    /// </summary>
    Task<IReadOnlyList<UnityGameEntry>> ScanAsync(CancellationToken ct = default);
}
