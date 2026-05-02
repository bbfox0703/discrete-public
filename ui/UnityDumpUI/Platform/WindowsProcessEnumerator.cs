// WindowsProcessEnumerator.cs — concrete IProcessEnumerator for Windows.
//
// Per Architecture.md "Platform Abstraction": Core declares the interface,
// platform-specific impls live in UnityDumpUI. AOT-safe — System.Diagnostics
// only, plus tiny P/Invokes for IsWow64Process.
//
// Backend detection mirrors tools/Test-Handshake.ps1 §"Auto" — peek the
// target's loaded modules. AccessDenied / "process protected" failures are
// silent; the Backend field stays None and the row still appears in List()
// (just gets filtered out of ListUnityProcesses()).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityDumpUI.Core.Platform;

namespace UnityDumpUI.Platform;

public sealed class WindowsProcessEnumerator : IProcessEnumerator
{
    public IReadOnlyList<ProcessInfo> List()
    {
        var processes = Process.GetProcesses();
        var result = new List<ProcessInfo>(processes.Length);
        foreach (var p in processes)
        {
            try
            {
                ProcessInfo info = Probe(p);
                result.Add(info);
            }
            catch (InvalidOperationException)
            {
                // Process exited between GetProcesses and our query.
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    public IReadOnlyList<ProcessInfo> ListUnityProcesses()
    {
        var all = List();
        var unity = new List<ProcessInfo>();
        foreach (var p in all)
        {
            if (p.Backend != UnityBackend.None) unity.Add(p);
        }
        return unity;
    }

    private static ProcessInfo Probe(Process p)
    {
        string  name    = p.ProcessName;
        string? path    = TryGetPath(p);
        bool    isWow64 = TryIsWow64(p);
        UnityBackend backend = isWow64 ? UnityBackend.None : DetectBackend(p);
        return new ProcessInfo(p.Id, name, path, backend, isWow64);
    }

    private static string? TryGetPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>
    /// Iterate the target's loaded modules looking for IL2CPP / Mono runtime
    /// DLLs. <c>Process.Modules</c> throws AccessDenied for protected
    /// processes; we swallow and report <see cref="UnityBackend.None"/>.
    /// </summary>
    private static UnityBackend DetectBackend(Process p)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                string n = m.ModuleName;
                if (string.Equals(n, "GameAssembly.dll",   StringComparison.OrdinalIgnoreCase))
                    return UnityBackend.IL2CPP;
                if (string.Equals(n, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase))
                    return UnityBackend.Mono;
                if (string.Equals(n, "mono.dll",           StringComparison.OrdinalIgnoreCase))
                    return UnityBackend.Mono;
            }
        }
        catch { /* protected / 32-bit-from-64-bit / exited */ }
        return UnityBackend.None;
    }

    private static bool TryIsWow64(Process p)
    {
        try
        {
            return IsWow64Process(p.Handle, out bool wow64) && wow64;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
