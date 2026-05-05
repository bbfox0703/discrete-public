// SteamGameScanner.cs — implementation of IUnityGameScanner.
//
// Discovery flow:
//   1. Find Steam install dir (HKCU\Software\Valve\Steam\SteamPath, then
//      HKLM as fallback).
//   2. Parse <SteamDir>\steamapps\libraryfolders.vdf — Valve KeyValues
//      format — to find every library folder (the user's primary install
//      + any external drives they added).
//   3. For each library, walk steamapps\common\* directly. Each direct
//      child folder is a game install root.
//   4. For each game folder, run UnityProbe (looks for UnityPlayer.dll +
//      backend markers, identifies the Unity .exe, reads the PE header
//      for bitness, then asks IProxyInstaller whether a proxy is already
//      installed).
//
// Robust to missing files (libraryfolders.vdf can be absent on first
// install, individual game folders may have weird shapes from old
// installs / partial uninstalls). Anything we can't classify is dropped
// silently — the result is "Unity games we recognise," not "every
// folder under common\".

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Platform;

public sealed class SteamGameScanner : IUnityGameScanner
{
    private readonly IProxyInstaller _proxyInstaller;

    public SteamGameScanner(IProxyInstaller proxyInstaller)
    {
        _proxyInstaller = proxyInstaller;
    }

    public Task<IReadOnlyList<UnityGameEntry>> ScanAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<UnityGameEntry>>(() =>
        {
            List<UnityGameEntry> result = new();
            foreach (string libRoot in EnumerateSteamLibraries())
            {
                ct.ThrowIfCancellationRequested();
                string commonDir = Path.Combine(libRoot, "steamapps", "common");
                if (!Directory.Exists(commonDir)) continue;
                string libLabel = $"Steam library: {libRoot}";
                IEnumerable<string> gameDirs;
                try { gameDirs = Directory.EnumerateDirectories(commonDir); }
                catch { continue; }
                foreach (string gameDir in gameDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    UnityGameEntry? entry = ProbeUnityGame(gameDir, libLabel);
                    if (entry is not null) result.Add(entry);
                }
            }
            return (IReadOnlyList<UnityGameEntry>)result;
        }, ct);
    }

    // ---------- Steam library enumeration --------------------------------

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        string? steamDir = ResolveSteamDir();
        if (string.IsNullOrEmpty(steamDir)) yield break;
        // The Steam install itself is always library #0.
        yield return steamDir;

        string vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        // libraryfolders.vdf is Valve KeyValues. We don't need a full
        // parser — extract every "path" entry's value with a tiny
        // tokeniser. The format guarantees `"path"\t\t"<path>"` on its
        // own line within each numbered library block.
        foreach (string path in ExtractVdfPathValues(text))
        {
            // Skip the primary install if listed (we already yielded it).
            if (string.Equals(path, steamDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? ResolveSteamDir()
    {
        // HKCU is the user-scoped install — most common.
        string? p = ReadRegString(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrEmpty(p)) return NormalizeSlashes(p);
        // HKLM fallback — needed for system-wide installs.
        p = ReadRegString(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrEmpty(p)) return NormalizeSlashes(p);
        p = ReadRegString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath");
        return string.IsNullOrEmpty(p) ? null : NormalizeSlashes(p);
    }

    private static string? ReadRegString(string keyPath, string valueName)
    {
        try
        {
            object? v = Registry.GetValue(keyPath, valueName, null);
            return v as string;
        }
        catch { return null; }
    }

    private static string NormalizeSlashes(string p) =>
        p.Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// Extract every <c>"path"\t\t"…"</c> value from a libraryfolders.vdf
    /// blob. Tolerant to whitespace and the surrounding library blocks.
    /// </summary>
    internal static IEnumerable<string> ExtractVdfPathValues(string text)
    {
        // Hand-rolled scan: find each occurrence of "path" as a quoted
        // key, then take the next quoted string on the same / next line
        // as the value.
        int i = 0;
        while (i < text.Length)
        {
            int keyStart = text.IndexOf("\"path\"", i, StringComparison.Ordinal);
            if (keyStart < 0) yield break;
            int afterKey = keyStart + "\"path\"".Length;
            // Skip whitespace / quotes until the value's opening quote.
            int valOpen = text.IndexOf('"', afterKey);
            if (valOpen < 0) yield break;
            int valClose = text.IndexOf('"', valOpen + 1);
            if (valClose < 0) yield break;
            string raw = text.Substring(valOpen + 1, valClose - valOpen - 1);
            // VDF escapes path separators as `\\`; normalise to `\`.
            string path = raw.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
            i = valClose + 1;
        }
    }

    // ---------- Per-game probing -----------------------------------------

    private UnityGameEntry? ProbeUnityGame(string gameDir, string source)
    {
        // First gate: UnityPlayer.dll OR an *_Data folder. Either alone is
        // enough to call this a Unity build (mod-loader-only installs may
        // hide UnityPlayer.dll behind a renamed .exe).
        bool hasUnityPlayer = File.Exists(Path.Combine(gameDir, "UnityPlayer.dll"));
        string? exePath = null;
        string? dataDirName = null;

        // Walk every .exe in the game root (one or two usually, never
        // many). Pick the first one whose stem has a sibling *_Data dir.
        IEnumerable<string> exes;
        try { exes = Directory.EnumerateFiles(gameDir, "*.exe"); }
        catch { return null; }

        foreach (string e in exes)
        {
            string stem = Path.GetFileNameWithoutExtension(e);
            string candidate = Path.Combine(gameDir, stem + "_Data");
            if (Directory.Exists(candidate))
            {
                exePath = e;
                dataDirName = stem + "_Data";
                break;
            }
        }
        if (exePath is null && !hasUnityPlayer) return null;
        // Edge case: UnityPlayer.dll present but no *_Data folder matched.
        // Fall back to the first .exe so we still surface something.
        if (exePath is null)
        {
            exePath = Directory.EnumerateFiles(gameDir, "*.exe").FirstOrDefault();
            if (exePath is null) return null;
        }

        // Backend detection from filesystem markers.
        UnityBackend backend = UnityBackend.None;
        if (File.Exists(Path.Combine(gameDir, "GameAssembly.dll")))
        {
            backend = UnityBackend.IL2CPP;
        }
        else if (Directory.Exists(Path.Combine(gameDir, "MonoBleedingEdge")))
        {
            backend = UnityBackend.Mono;
        }
        else if (dataDirName is not null)
        {
            string dataDir = Path.Combine(gameDir, dataDirName);
            if (Directory.Exists(Path.Combine(dataDir, "il2cpp_data")))
                backend = UnityBackend.IL2CPP;
            else if (Directory.Exists(Path.Combine(dataDir, "Managed")))
                backend = UnityBackend.Mono;
        }

        // Filter games that aren't actually Unity (no backend marker AND
        // no UnityPlayer.dll). UnityPlayer.dll alone with no backend is
        // possible — we surface it as Unknown so the user can decide.
        if (backend == UnityBackend.None && !hasUnityPlayer) return null;

        ExeBitness bitness = ReadPeBitness(exePath);

        // Existing-proxy check is synchronous via the installer's
        // detection helper. Soft-fail to "no proxy" on errors.
        ProxyKind installedProxy = ProxyKind.None;
        try
        {
            ProxyInstallStatus s = _proxyInstaller.DetectAsync(exePath).GetAwaiter().GetResult();
            installedProxy = s.Kind;
        }
        catch { /* tolerate */ }

        string display = Path.GetFileName(gameDir);
        return new UnityGameEntry(
            exePath, gameDir, display,
            backend, bitness, dataDirName, installedProxy, source);
    }

    // ---------- PE header bitness ----------------------------------------

    /// <summary>
    /// Read just enough of the PE header to extract IMAGE_FILE_MACHINE.
    /// Returns <see cref="ExeBitness.Unknown"/> for unreadable / corrupt
    /// files, ARM64, IA64 — anything we don't actively support.
    /// </summary>
    internal static ExeBitness ReadPeBitness(string exePath)
    {
        try
        {
            using FileStream fs = File.OpenRead(exePath);
            // DOS header: 64 bytes, e_lfanew at offset 0x3C points to PE.
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) != 64) return ExeBitness.Unknown;
            if (dos[0] != 'M' || dos[1] != 'Z') return ExeBitness.Unknown;
            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.Slice(0x3C, 4));
            if (peOffset <= 0 || peOffset > 0x10_000) return ExeBitness.Unknown;

            fs.Seek(peOffset, SeekOrigin.Begin);
            Span<byte> peSig = stackalloc byte[6];  // "PE\0\0" + IMAGE_FILE_MACHINE
            if (fs.Read(peSig) != 6) return ExeBitness.Unknown;
            if (peSig[0] != 'P' || peSig[1] != 'E' || peSig[2] != 0 || peSig[3] != 0)
                return ExeBitness.Unknown;
            ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(peSig.Slice(4, 2));
            return machine switch
            {
                0x8664 => ExeBitness.X64,   // IMAGE_FILE_MACHINE_AMD64
                0x014C => ExeBitness.X86,   // IMAGE_FILE_MACHINE_I386
                _      => ExeBitness.Unknown,
            };
        }
        catch
        {
            return ExeBitness.Unknown;
        }
    }
}
