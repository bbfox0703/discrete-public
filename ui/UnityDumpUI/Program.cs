using System;
using System.Threading;
using Avalonia;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Platform;

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

        // Phase 29c — initialise the SQLitePCLRaw provider so
        // Microsoft.Data.Sqlite finds the bundled native sqlite3
        // implementation. Idempotent; safe to call multiple times.
        // Must happen BEFORE any SnapshotStore.OpenAsync — putting it
        // in Program.Main ensures every UI startup is covered.
        SQLitePCL.Batteries_V2.Init();

        // 2026-11 app-data rename (Discrete_Dumper -> discrete): move the user
        // state files BEFORE any store reads them. Renames only, never throws;
        // MainWindowViewModel writes the report to the init log.
        AppDataMigration.Run();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // AOT stack traces are gutted and a WinExe has no console, so an
            // unhandled startup exception otherwise dies silently — the user
            // sees the process vanish with no signal. Persist it next to the
            // other app-data files so startup crashes are diagnosable. Best
            // effort: never let the crash reporter throw over the real fault.
            WriteCrashLog(ex);
            return 1;
        }
    }

    /// <summary>
    /// Write an unhandled-startup-exception dump to
    /// <c>%LOCALAPPDATA%\discrete\crash.log</c> (same app-data root as
    /// window-state.txt / teleport-hotkeys.txt / the FileLogger). AOT-safe: no
    /// reflection, best-effort IO. See docs/aot-pitfalls.md §8.3.
    /// </summary>
    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string subRoot = Constants.LogRootSubpath;      // "discrete\Logs"
            int slash = subRoot.IndexOf('\\');
            string appRoot = slash > 0 ? subRoot[..slash] : subRoot;
            string dir = System.IO.Path.Combine(localAppData, appRoot);
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Startup crash:{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // best effort — nothing more we can safely do here
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // Windows-only build (see UnityDumpUI.csproj — Avalonia.Desktop
            // metapackage is intentionally NOT referenced). The three pieces
            // UsePlatformDetect used to wire up automatically:
            //   * UseWin32     — windowing / input (Avalonia.Win32)
            //   * UseSkia      — rendering backend (Avalonia.Skia)
            //   * UseHarfBuzz  — text shaping (without this, AppBuilder.Setup
            //                    throws "No text shaping system configured.
            //                    Consider calling UseHarfBuzz()." and the
            //                    process exits with 0xC0000409)
            // Without HarfBuzz the window never appears — the exception
            // happens during Setup, before any UI is shown or any log written.
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont();
}
