using System;
using System.Threading;
using Avalonia;
using UnityDumpUI.Core;

namespace UnityDumpUI;

internal static class Program
{
    // Single-instance guard per Rule "Single Instance" in Architecture.md.
    // Held for the lifetime of the process; released by GC + Mutex finalizer on exit.
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, Constants.SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already running. Exit silently — production version may bring it to front.
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // Windows-only build (see UnityDumpUI.csproj — Avalonia.Desktop
            // metapackage is intentionally NOT referenced). UsePlatformDetect
            // lives in Avalonia.Desktop, so call UseWin32 directly instead.
            .UseWin32()
            .WithInterFont();
}
