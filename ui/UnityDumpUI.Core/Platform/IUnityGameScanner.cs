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
// The 32-bit Mono path itself isn't supported in the dumper yet — see
// Phase 12d in STATUS.md — but the scanner still surfaces 32-bit games
// so the UI can show a clear "(32-bit, not yet supported)" label.

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
                           string source)
    {
        ExePath           = exePath;
        GameDirectory     = gameDirectory;
        DisplayName       = displayName;
        Backend           = backend;
        Bitness           = bitness;
        DataDirectoryName = dataDirectoryName;
        InstalledProxy    = installedProxy;
        Source            = source;
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

    /// <summary>True when the dumper actually supports this target today.</summary>
    public bool IsSupported =>
        Bitness == ExeBitness.X64 &&
        Backend != UnityBackend.None;
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
