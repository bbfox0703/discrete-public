// WindowsProxyInstaller.cs — Phase 12c implementation of IProxyInstaller.
//
// Source files come from the directory containing DiscreteUI.exe (resolved
// via Environment.ProcessPath, same as WindowsDllInjector — works under
// both Native AOT and self-extracting single-file builds). Target files
// land next to the game .exe.
//
// AOT-safe: pure file IO + Environment APIs, no reflection.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Platform;

public sealed class WindowsProxyInstaller : IProxyInstaller
{
    // The four files we ship in dist/ that this installer copies. The
    // proxy filenames are also what the game looks up at startup, so
    // they cannot be renamed.
    private const string VersionDll = "version.dll";
    private const string WinmmDll   = "winmm.dll";
    private const string MonoDll    = "UnityDumper-Mono.dll";
    private const string Il2cppDll  = "UnityDumper-IL2CPP.dll";

    public Task<ProxyInstallStatus> DetectAsync(string gameExePath, CancellationToken ct = default)
    {
        return Task.Run(() => DetectSync(gameExePath), ct);
    }

    public Task<ProxyOperationResult> InstallAsync(
        string gameExePath, ProxyKind kind, CancellationToken ct = default)
    {
        return Task.Run(() => InstallSync(gameExePath, kind), ct);
    }

    public Task<ProxyOperationResult> UninstallAsync(
        string gameExePath, CancellationToken ct = default)
    {
        return Task.Run(() => UninstallSync(gameExePath), ct);
    }

    // ---------- Sync implementations -------------------------------------

    private static ProxyInstallStatus DetectSync(string gameExePath)
    {
        if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
        {
            return new ProxyInstallStatus(
                gameExePath ?? string.Empty, ProxyKind.None,
                false, false, "game .exe not found");
        }
        string dir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        bool hasVersion = File.Exists(Path.Combine(dir, VersionDll));
        bool hasWinmm   = File.Exists(Path.Combine(dir, WinmmDll));
        bool hasMono    = File.Exists(Path.Combine(dir, MonoDll));
        bool hasIl2cpp  = File.Exists(Path.Combine(dir, Il2cppDll));

        // Mutex constraint says version XOR winmm; if both present we
        // surface this as an error so the user can clean up manually.
        if (hasVersion && hasWinmm)
        {
            return new ProxyInstallStatus(
                gameExePath, ProxyKind.None, hasMono, hasIl2cpp,
                "both version.dll and winmm.dll present — uninstall manually first");
        }
        ProxyKind kind = hasVersion ? ProxyKind.Version
                       : hasWinmm   ? ProxyKind.Winmm
                       :              ProxyKind.None;

        // Read PE FileVersion off both sides so the deploy UI can flag
        // outdated installs. We always compare against the SAME proxy
        // kind that's currently in place — `dist/version.dll` vs game's
        // version.dll, etc. — because the two dist proxies share the
        // same build number anyway.
        string proxyName = kind switch
        {
            ProxyKind.Version => VersionDll,
            ProxyKind.Winmm   => WinmmDll,
            _                 => VersionDll,   // sensible default for the source-side read
        };
        string? installedVersion = (kind == ProxyKind.None)
            ? null
            : ReadFileVersion(Path.Combine(dir, proxyName));
        string? sourceVersion = ReadFileVersion(Path.Combine(SourceDir(), proxyName));

        return new ProxyInstallStatus(gameExePath, kind, hasMono, hasIl2cpp,
            errorMessage: null,
            installedProxyVersion: installedVersion,
            sourceProxyVersion:    sourceVersion);
    }

    /// <summary>
    /// Read the PE FileVersion string ("major.minor.patch.build") from a
    /// DLL or exe via System.Diagnostics.FileVersionInfo. Returns null
    /// when the file is missing or has no version resource (rare; would
    /// indicate a build that didn't pick up version.rc).
    /// </summary>
    private static string? ReadFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrEmpty(info.FileVersion) ? null : info.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static ProxyOperationResult InstallSync(string gameExePath, ProxyKind kind)
    {
        if (kind == ProxyKind.None)
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, "ProxyKind.None passed to Install", st0);
        }
        if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, "game .exe not found", st0);
        }

        string srcDir = SourceDir();
        string dstDir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        string proxyName  = (kind == ProxyKind.Version) ? VersionDll : WinmmDll;
        string otherProxy = (kind == ProxyKind.Version) ? WinmmDll   : VersionDll;

        // Validate source files exist before touching the game dir at all.
        string[] required = { proxyName, MonoDll, Il2cppDll };
        foreach (string f in required)
        {
            string src = Path.Combine(srcDir, f);
            if (!File.Exists(src))
            {
                var st0 = DetectSync(gameExePath);
                return new ProxyOperationResult(false,
                    $"source missing: {f} (looked in {srcDir})", st0);
            }
        }

        try
        {
            // Mutex install: drop the OTHER proxy first so the directory
            // never has both at any moment.
            string otherPath = Path.Combine(dstDir, otherProxy);
            if (File.Exists(otherPath)) File.Delete(otherPath);

            foreach (string f in required)
            {
                File.Copy(Path.Combine(srcDir, f), Path.Combine(dstDir, f), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, $"copy failed: {ex.Message}", st0);
        }

        var st = DetectSync(gameExePath);
        return new ProxyOperationResult(true,
            $"Installed {proxyName} + UnityDumper-{{Mono,IL2CPP}}.dll in {dstDir}", st);
    }

    private static ProxyOperationResult UninstallSync(string gameExePath)
    {
        if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, "game .exe not found", st0);
        }
        string dstDir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        string[] toRemove = { VersionDll, WinmmDll, MonoDll, Il2cppDll };
        int removed = 0;
        try
        {
            foreach (string f in toRemove)
            {
                string p = Path.Combine(dstDir, f);
                if (File.Exists(p)) { File.Delete(p); ++removed; }
            }
        }
        catch (Exception ex)
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, $"delete failed: {ex.Message}", st0);
        }
        var st = DetectSync(gameExePath);
        return new ProxyOperationResult(true,
            removed == 0 ? "Nothing to uninstall (no proxy files present)"
                         : $"Removed {removed} file(s) from {dstDir}", st);
    }

    private static string SourceDir()
    {
        string? dir = Environment.ProcessPath is { Length: > 0 } pp
                      ? Path.GetDirectoryName(pp) : null;
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }
}
