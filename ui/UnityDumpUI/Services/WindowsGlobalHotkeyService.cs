// WindowsGlobalHotkeyService.cs — Task 7 (Teleport wave 1). Win32
// RegisterHotKey global hotkeys on a dedicated message-loop thread. Ported
// from UE5CEDumper's WindowsGlobalHotkeyService (cursor ladder dropped — wave 1
// has no cursor teleport; only RegisterSpecific is needed).
//
// AOT-safe: classic blittable DllImports (no LibraryImport / unsafe).
// RegisterHotKey with a NULL hwnd posts WM_HOTKEY to the registering thread's
// queue, so registration AND the GetMessage loop both run on one owned thread.
// A combo already claimed elsewhere makes RegisterHotKey fail — that surfaces
// as a null registration so the caller can report the combo as unavailable.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Services;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    public IGlobalHotkeyRegistration? RegisterSpecific(uint modifiers, uint vk, string label, Action onPressed)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var reg = new Registration(modifiers, vk, label, onPressed);
            if (reg.Registered) return reg;
            reg.Dispose();       // combo not claimable — tear the thread down
            return null;
        }
        catch
        {
            // Never let hotkey setup take down the app — degrade to "no hotkey".
            return null;
        }
    }

    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;

    private sealed class Registration : IGlobalHotkeyRegistration
    {
        private const int HotkeyId = 0xB1F0;   // per-thread id; safe to reuse across registrations
        private readonly Thread _thread;
        // NOT disposed in the ctor: ManualResetEventSlim forbids Dispose()
        // concurrent with Set() (the worker Sets() right as the ctor's Wait()
        // returns). Disposed in Dispose() only after the worker has joined.
        private readonly ManualResetEventSlim _ready = new(false);
        private uint _threadId;
        private volatile bool _disposed;
        private volatile bool _registered;

        public bool   Registered => _registered;
        public string Label      { get; }

        public Registration(uint modifiers, uint vk, string label, Action onPressed)
        {
            Label = label;
            _thread = new Thread(() =>
            {
                bool ok = false;
                try
                {
                    _threadId = GetCurrentThreadId();
                    ok = RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | MOD_NOREPEAT, vk);
                    _registered = ok;
                }
                catch { /* registration failed → _registered stays false */ }
                finally { _ready.Set(); }   // always release the ctor's Wait()

                if (!ok) return;

                // Message loop: WM_HOTKEY → fire; WM_QUIT (from Dispose) → exit.
                // Wrapped so nothing here can crash the process (an uncaught
                // exception on a background thread terminates the app).
                try
                {
                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        if (msg.message == WM_HOTKEY)
                        {
                            try { onPressed(); } catch { /* keep the loop alive */ }
                        }
                    }
                    UnregisterHotKey(IntPtr.Zero, HotkeyId);
                }
                catch { /* swallow — degrade to no hotkey rather than crash */ }
            })
            {
                IsBackground = true,
                Name = "Discrete-TeleportHotkey",
            };
            _thread.Start();
            _ready.Wait(2000);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            _ready.Dispose();
        }
    }

    // --- Win32 (blittable DllImports — AOT-safe, no AllowUnsafeBlocks) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    pt_x;
        public int    pt_y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // The real user32 exports are GetMessageW / PostThreadMessageW — there is no
    // bare "GetMessage"/"PostThreadMessage" symbol, so with ExactSpelling=true
    // the bare names throw EntryPointNotFoundException on the worker thread
    // (uncaught → process crash). Pin the W entry points explicitly.
    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
