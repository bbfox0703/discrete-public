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

        // Wire the UI-side runtime metadata token into Core's aggregator so
        // Core consumers (CT export, etc.) can OR all three slot factors
        // together. Without this, Core would only see slots A + B and slot C
        // would only guard UI-side consumers.
        TokenAggregator.RegisterExternal(WindowsRuntimeMetadata.Read);

        // Phase 29c — initialise the SQLitePCLRaw provider so
        // Microsoft.Data.Sqlite finds the bundled native sqlite3
        // implementation. Idempotent; safe to call multiple times.
        // Must happen BEFORE any SnapshotStore.OpenAsync — putting it
        // in Program.Main ensures every UI startup is covered.
        SQLitePCL.Batteries_V2.Init();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
