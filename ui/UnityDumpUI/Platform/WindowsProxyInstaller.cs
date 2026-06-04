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
    private const string MonoDll    = "Discrete-Mono.dll";
    private const string Il2cppDll  = "Discrete-IL2CPP.dll";

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
        // 32-bit Mono games take the x86 build from dist/x86/, so the
        // source-version comparison must read the matching proxy there —
        // otherwise an x86 game would always read "outdated" against the
        // x64 dist proxy (different build chain, different version stamp).
        string? sourceVersion = ReadFileVersion(
            Path.Combine(SourceDir(TargetIsX86(gameExePath)), proxyName));

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

        bool   isX86  = TargetIsX86(gameExePath);
        string srcDir = SourceDir(isX86);
        string dstDir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        string proxyName  = (kind == ProxyKind.Version) ? VersionDll : WinmmDll;
        string otherProxy = (kind == ProxyKind.Version) ? WinmmDll   : VersionDll;

        // Validate source files exist before touching the game dir at all.
        // 32-bit targets get the x86 proxy + x86 Mono backend only — there is
        // no 32-bit IL2CPP backend, so don't require/copy Discrete-IL2CPP.dll
        // (the proxy loader only ever detects Mono on an x86 game anyway).
        string[] required = isX86
            ? new[] { proxyName, MonoDll }
            : new[] { proxyName, MonoDll, Il2cppDll };
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
        string backendList = isX86 ? MonoDll : "Discrete-{Mono,IL2CPP}.dll";
        return new ProxyOperationResult(true,
            $"Installed {proxyName} + {backendList} in {dstDir}", st);
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

    /// <summary>Directory the backend + proxy DLLs are copied FROM. x64 build
    /// ships next to DiscreteUI.exe (dist/); the x86 build lives in the
    /// <c>x86/</c> sub-directory (dist/x86/) — see build.ps1 <c>-Arch x86</c>.</summary>
    private static string SourceDir(bool isX86)
    {
        string baseDir = BaseDir();
        return isX86 ? Path.Combine(baseDir, "x86") : baseDir;
    }

    private static string BaseDir()
    {
        string? dir = Environment.ProcessPath is { Length: > 0 } pp
                      ? Path.GetDirectoryName(pp) : null;
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    /// <summary>
    /// Read the target .exe's PE machine field to pick the dist sub-dir.
    /// Returns true only for a confirmed 32-bit (IMAGE_FILE_MACHINE_I386)
    /// image; x64 / unknown / unreadable all fall back to the x64 dist/.
    /// Kept local so the installer stays self-contained (no bitness threaded
    /// through IProxyInstaller); mirrors SteamGameScanner.ReadPeBitness.
    /// </summary>
    private static bool TargetIsX86(string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) return false;
            fs.Position = 0x3C;
            int peOff = br.ReadInt32();                 // e_lfanew
            if (peOff <= 0 || peOff + 6 > fs.Length) return false;
            fs.Position = peOff;
            if (br.ReadUInt32() != 0x0000_4550u) return false;  // "PE\0\0"
            ushort machine = br.ReadUInt16();
            return machine == 0x014C;                   // IMAGE_FILE_MACHINE_I386
        }
        catch
        {
            return false;
        }
    }
}
