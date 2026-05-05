// IProxyInstaller.cs — Phase 12c. UI-side service that copies the
// version.dll / winmm.dll proxy + both UnityDumper-{Backend}.dll files
// next to the game .exe, so the next game launch auto-loads our pipe
// server with no runtime injection needed.
//
// Mutex install: only one of {version.dll, winmm.dll} can live next to
// the game at a time. Installing one removes any existing copy of the
// other. Uninstalling removes both proxy variants + both backend DLLs.

using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Platform;

public enum ProxyKind
{
    /// <summary>No proxy currently installed.</summary>
    None,
    /// <summary>version.dll proxy — preferred (Unity always imports it).</summary>
    Version,
    /// <summary>winmm.dll proxy — alternative when version.dll is taken.</summary>
    Winmm,
}

/// <summary>
/// Snapshot of what's currently in the game directory next to
/// <see cref="ProxyInstallStatus.GameExePath"/>. Drives the UI's
/// "currently installed: …" label.
/// </summary>
public sealed class ProxyInstallStatus
{
    public ProxyInstallStatus(string gameExePath, ProxyKind kind,
                              bool hasMonoBackend, bool hasIl2cppBackend,
                              string? errorMessage)
    {
        GameExePath      = gameExePath;
        Kind             = kind;
        HasMonoBackend   = hasMonoBackend;
        HasIl2cppBackend = hasIl2cppBackend;
        ErrorMessage     = errorMessage;
    }

    public string    GameExePath      { get; }
    public ProxyKind Kind             { get; }
    public bool      HasMonoBackend   { get; }
    public bool      HasIl2cppBackend { get; }
    public string?   ErrorMessage     { get; }

    /// <summary>True when at least one proxy file lives next to the .exe.</summary>
    public bool IsInstalled => Kind != ProxyKind.None;
}

/// <summary>
/// Result of an install / uninstall operation. <see cref="Ok"/> is the
/// summary; <see cref="Status"/> is a fresh detection after the op so
/// the UI can refresh its label without a follow-up call.
/// </summary>
public sealed class ProxyOperationResult
{
    public ProxyOperationResult(bool ok, string message, ProxyInstallStatus status)
    {
        Ok = ok; Message = message; Status = status;
    }
    public bool                Ok      { get; }
    public string              Message { get; }
    public ProxyInstallStatus  Status  { get; }
}

public interface IProxyInstaller
{
    /// <summary>
    /// Inspect the directory containing <paramref name="gameExePath"/> for
    /// our proxy and backend DLLs. Returns a status snapshot. Soft-fails
    /// when the game .exe doesn't exist.
    /// </summary>
    Task<ProxyInstallStatus> DetectAsync(string gameExePath, CancellationToken ct = default);

    /// <summary>
    /// Copy the chosen proxy DLL + both UnityDumper-{Backend}.dll files
    /// next to the game .exe. Removes the other proxy variant if present
    /// (mutex install). Source files come from the
    /// <c>dist/</c>-equivalent directory next to the running
    /// <c>DiscreteUI.exe</c>.
    /// </summary>
    Task<ProxyOperationResult> InstallAsync(
        string gameExePath, ProxyKind kind, CancellationToken ct = default);

    /// <summary>
    /// Remove every proxy + backend DLL we may have written next to the
    /// game .exe. Best-effort: missing files are ignored.
    /// </summary>
    Task<ProxyOperationResult> UninstallAsync(
        string gameExePath, CancellationToken ct = default);
}
