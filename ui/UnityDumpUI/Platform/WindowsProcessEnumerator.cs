// WindowsProcessEnumerator.cs — concrete IProcessEnumerator for Windows.
//
// Per Architecture.md "Platform Abstraction": Core declares the interface,
// platform-specific impls live in UnityDumpUI. This impl uses
// System.Diagnostics.Process which is AOT-safe — no P/Invoke, no reflection.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            string name;
            string? path;
            try
            {
                name = p.ProcessName;
                // MainModule throws AccessDenied for protected processes — we
                // want a list, not a privilege escalation, so we silently
                // record path=null in those cases.
                path = TryGetPath(p);
            }
            catch (InvalidOperationException)
            {
                // Process exited between GetProcesses and our query.
                p.Dispose();
                continue;
            }
            finally
            {
                // Process objects hold OS handles — release ASAP.
            }
            result.Add(new ProcessInfo(p.Id, name, path));
            p.Dispose();
        }
        return result;
    }

    private static string? TryGetPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }
}
