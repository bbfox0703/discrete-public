// WindowsProxyInstaller.cs — Phase 12c implementation of IProxyInstaller.
//
// Source files come from the directory containing DiscreteUI.exe (resolved
// via Environment.ProcessPath, same as WindowsDllInjector — works under
// both Native AOT and self-extracting single-file builds). Target files
// land next to the game .exe.
//
// AOT-safe: pure file IO + Environment APIs, no reflection.

using System;
using System.Collections.Generic;
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

    private readonly Func<bool, string>    _sourceDirProvider;
    private readonly Func<string, bool>    _isOurDll;
    private readonly Func<string, string?> _readFileVersion;

    public WindowsProxyInstaller()
        : this(DefaultSourceDir, DefaultIsOurDll, DefaultReadFileVersion)
    { }

    /// <summary>
    /// Internal test seam allowing unit tests to exercise real filesystem
    /// detect/install/uninstall logic in temporary directories with simulated
    /// Discrete vs third-party proxy DLLs.
    /// </summary>
    internal WindowsProxyInstaller(
        Func<bool, string>    sourceDirProvider,
        Func<string, bool>    isOurDll,
        Func<string, string?> readFileVersion)
    {
        _sourceDirProvider = sourceDirProvider;
        _isOurDll          = isOurDll;
        _readFileVersion   = readFileVersion;
    }

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

    private ProxyInstallStatus DetectSync(string gameExePath)
    {
        if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
        {
            return new ProxyInstallStatus(
                gameExePath ?? string.Empty, ProxyKind.None,
                false, false, "game .exe not found");
        }
        string dir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        string versionPath = Path.Combine(dir, VersionDll);
        string winmmPath   = Path.Combine(dir, WinmmDll);

        bool hasVersionFile = File.Exists(versionPath);
        bool hasWinmmFile   = File.Exists(winmmPath);
        bool ourVersion     = hasVersionFile && _isOurDll(versionPath);
        bool ourWinmm       = hasWinmmFile   && _isOurDll(winmmPath);
        bool foreignVersion = hasVersionFile && !ourVersion;
        bool foreignWinmm   = hasWinmmFile   && !ourWinmm;
        bool hasMono        = File.Exists(Path.Combine(dir, MonoDll));
        bool hasIl2cpp      = File.Exists(Path.Combine(dir, Il2cppDll));

        string? foreignProxy = (foreignVersion, foreignWinmm) switch
        {
            (true, true)   => $"{VersionDll}, {WinmmDll}",
            (true, false)  => VersionDll,
            (false, true)  => WinmmDll,
            (false, false) => null,
        };

        // Only OUR two proxies are mutually exclusive. A third-party version.dll
        // (ReShade/ENB/SpecialK/mod loader) coexisting with OUR winmm.dll — or
        // vice versa — is an explicitly supported deployment (see InstallSync).
        if (ourVersion && ourWinmm)
        {
            return new ProxyInstallStatus(
                gameExePath, ProxyKind.None, hasMono, hasIl2cpp,
                "both Discrete version.dll and winmm.dll present — uninstall manually first",
                foreignProxyPresent: foreignProxy);
        }
        ProxyKind kind = ourVersion ? ProxyKind.Version
                       : ourWinmm   ? ProxyKind.Winmm
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
            : _readFileVersion(Path.Combine(dir, proxyName));
        // 32-bit Mono games take the x86 build from dist/x86/, so the
        // source-version comparison must read the matching proxy there —
        // otherwise an x86 game would always read "outdated" against the
        // x64 dist proxy (different build chain, different version stamp).
        string? sourceVersion = _readFileVersion(
            Path.Combine(_sourceDirProvider(TargetIsX86(gameExePath)), proxyName));

        return new ProxyInstallStatus(gameExePath, kind, hasMono, hasIl2cpp,
            errorMessage: null,
            installedProxyVersion: installedVersion,
            sourceProxyVersion:    sourceVersion,
            foreignProxyPresent:   foreignProxy);
    }

    /// <summary>
    /// Read the PE FileVersion string ("major.minor.patch.build") from a
    /// DLL or exe via System.Diagnostics.FileVersionInfo. Returns null
    /// when the file is missing or has no version resource (rare; would
    /// indicate a build that didn't pick up version.rc).
    /// </summary>
    private static string? DefaultReadFileVersion(string path)
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

    /// <summary>
    /// True when the file at <paramref name="path"/> is one of OUR binaries.
    /// Every Discrete DLL is stamped ProductName="Discrete" by version.rc.in
    /// (hard-coded in the template, so it holds for every target).
    ///
    /// This is the guard that keeps install/uninstall from touching a
    /// third-party version.dll / winmm.dll — ReShade, ENB, SpecialK, mod
    /// loaders and anti-cheat shims all use exactly these two filenames, and
    /// clobbering one loses the user's setup silently: our proxy forwards
    /// every export to System32, so the game still launches and behaves
    /// normally while their mods simply stop existing.
    ///
    /// Returns FALSE for anything it cannot positively identify (missing
    /// version resource, unreadable file). "Unknown" must never be treated as
    /// "ours" — the whole point is to refuse to destroy what we did not create.
    /// </summary>
    private static bool DefaultIsOurDll(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "Discrete", StringComparison.Ordinal);
        }
        catch
        {
            return false;   // unreadable → treat as foreign, i.e. do not touch
        }
    }

    private ProxyOperationResult InstallSync(string gameExePath, ProxyKind kind)
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
        string srcDir = _sourceDirProvider(isX86);
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

        // Refuse to overwrite a proxy slot occupied by somebody else's DLL.
        // version.dll / winmm.dll are the two filenames every other injector
        // uses too; silently replacing one costs the user their ReShade / ENB /
        // mod loader with no error and no way back.
        string targetPath = Path.Combine(dstDir, proxyName);
        if (File.Exists(targetPath) && !_isOurDll(targetPath))
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false,
                $"{proxyName} already exists in the game folder and was not installed by Discrete " +
                "(no Discrete version resource). Refusing to overwrite it — it is most likely " +
                "ReShade, ENB, SpecialK or a mod loader. Remove or rename it yourself if you " +
                $"really want the {proxyName} proxy, or install the other proxy kind instead.", st0);
        }

        // A-L17: Atomic staged install. Copy every required DLL to `<name>.discrete.tmp`
        // first, commit each via File.Move(overwrite: true) (backing up existing files
        // for rollback if any move fails — e.g. game is running and holds a file lock),
        // and only remove the mutually-exclusive other Discrete proxy AFTER all new
        // files have landed cleanly.
        var stagedFiles = new List<(string Name, string TmpPath, string DstPath)>(required.Length);
        var backups     = new List<(string BakPath, string DstPath)>(required.Length);
        var createdNew  = new List<string>(required.Length);
        try
        {
            // Phase 1: stage all files into the target directory. Any source/disk/ACL
            // failure aborts before a single live DLL in the game directory is modified.
            foreach (string f in required)
            {
                string srcPath = Path.Combine(srcDir, f);
                string dstPath = Path.Combine(dstDir, f);
                string tmpPath = dstPath + ".discrete.tmp";
                File.Copy(srcPath, tmpPath, overwrite: true);
                stagedFiles.Add((f, tmpPath, dstPath));
            }

            // Phase 2: commit staged files (move existing file to .bak first so any
            // mid-install failure can roll back cleanly without leaving mixed versions).
            foreach (var (_, tmpPath, dstPath) in stagedFiles)
            {
                if (File.Exists(dstPath))
                {
                    string bakPath = dstPath + ".discrete.bak";
                    File.Move(dstPath, bakPath, overwrite: true);
                    backups.Add((bakPath, dstPath));
                }
                else
                {
                    createdNew.Add(dstPath);
                }
                File.Move(tmpPath, dstPath, overwrite: true);
            }

            // Phase 3: all required files are in place. Clean up backups and drop the
            // mutually-exclusive OTHER Discrete proxy (never touching a foreign DLL).
            foreach (var (bakPath, _) in backups)
            {
                try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { /* best-effort */ }
            }
            string otherPath = Path.Combine(dstDir, otherProxy);
            if (File.Exists(otherPath) && _isOurDll(otherPath)) File.Delete(otherPath);
        }
        catch (Exception ex)
        {
            // Roll back any committed files and clean up leftover .tmp files so the
            // game directory is never left with mixed old/new DLLs.
            foreach (var (_, tmpPath, _) in stagedFiles)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort */ }
            }
            foreach (string added in createdNew)
            {
                try { if (File.Exists(added)) File.Delete(added); } catch { /* best-effort */ }
            }
            for (int i = backups.Count - 1; i >= 0; --i)
            {
                var (bakPath, dstPath) = backups[i];
                try
                {
                    if (File.Exists(bakPath))
                        File.Move(bakPath, dstPath, overwrite: true);
                }
                catch { /* best-effort */ }
            }
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, $"copy failed: {ex.Message}", st0);
        }

        var st = DetectSync(gameExePath);
        string backendList = isX86 ? MonoDll : "Discrete-{Mono,IL2CPP}.dll";
        return new ProxyOperationResult(true,
            $"Installed {proxyName} + {backendList} in {dstDir}", st);
    }

    private ProxyOperationResult UninstallSync(string gameExePath)
    {
        if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, "game .exe not found", st0);
        }
        string dstDir = Path.GetDirectoryName(gameExePath) ?? string.Empty;
        string[] toRemove = { VersionDll, WinmmDll, MonoDll, Il2cppDll };
        int removed = 0;
        var skipped = new List<string>();
        try
        {
            foreach (string f in toRemove)
            {
                string p = Path.Combine(dstDir, f);
                if (!File.Exists(p)) continue;
                // Only delete what we installed. version.dll / winmm.dll may
                // belong to ReShade / ENB / SpecialK / a mod loader — deleting
                // one of those on "uninstall Discrete" is unrecoverable data
                // loss from the user's point of view.
                if (!_isOurDll(p)) { skipped.Add(f); continue; }
                File.Delete(p);
                ++removed;
            }
        }
        catch (Exception ex)
        {
            var st0 = DetectSync(gameExePath);
            return new ProxyOperationResult(false, $"delete failed: {ex.Message}", st0);
        }
        var st = DetectSync(gameExePath);
        string skipNote = skipped.Count == 0
            ? string.Empty
            : $" Left {string.Join(", ", skipped)} alone — not installed by Discrete.";
        return new ProxyOperationResult(true,
            removed == 0
                ? (skipped.Count == 0
                    ? "Nothing to uninstall (no proxy files present)"
                    : $"Nothing removed.{skipNote}")
                : $"Removed {removed} file(s) from {dstDir}.{skipNote}", st);
    }

    /// <summary>Directory the backend + proxy DLLs are copied FROM. x64 build
    /// ships next to DiscreteUI.exe (dist/); the x86 build lives in the
    /// <c>x86/</c> sub-directory (dist/x86/) — see build.ps1 <c>-Arch x86</c>.</summary>
    private static string DefaultSourceDir(bool isX86)
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
