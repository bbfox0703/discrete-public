using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace UnityDumpUI.Views;

/// <summary>
/// App-wide "soft disable" of the Windows IME on text-input focus. Ported from
/// UE5CEDumper's platform-service IME guard.
///
/// Every input field in this app takes ASCII content — hex addresses, AOB
/// patterns, numbers, class / field / method names, signatures, file paths — so
/// a CJK IME left in composition mode only gets in the way. When focus lands on
/// any text field we close the IME's open status, which drops it out of
/// composition and lets keystrokes flow through as direct alphanumeric input.
///
/// This is deliberately a SOFT / one-directional disable: we never force the IME
/// off at the OS level and we never re-open it on focus-out. A user who genuinely
/// wants CJK input can flip the IME back on manually (Win+Space) and keep typing.
///
/// Wiring: a single tunnel-to-bubble <see cref="InputElement.GotFocusEvent"/>
/// handler at each window root (with <c>handledEventsToo</c> so it still fires
/// when a child control marks the event handled) beats wiring 170+ TextBox /
/// AutoCompleteBox / NumericUpDown controls one by one. <see cref="Attach"/> is
/// called once per top-level window: from <see cref="DialogWindowGeometry"/>
/// (which every dialog already funnels through) and directly from MainWindow.
/// </summary>
internal static partial class ImeSoftDisable
{
    /// <summary>
    /// Register the focus-in IME guard on <paramref name="window"/>. Call once
    /// from the window's constructor after the XAML is loaded. Safe to call on
    /// any <see cref="Window"/>; the platform handle is read lazily at focus time
    /// (when the window is shown), never in the constructor.
    /// </summary>
    public static void Attach(Window? window)
    {
        if (window is null)
        {
            return;
        }

        // Bubble + handledEventsToo: the handler sits at the window root and still
        // fires even when a focused child (e.g. an inner TextBox of a NumericUpDown)
        // marks GotFocus handled on the way up. Capture the window so the IME we
        // close is unambiguously this window's, independent of routed-event sender.
        window.AddHandler(InputElement.GotFocusEvent,
            (_, e) => OnGotFocus(window, e),
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Per-window focus-in handler. Closes the IME when focus lands on a text
    /// control. AutoCompleteBox and NumericUpDown route keyboard focus to an inner
    /// <see cref="TextBox"/>, so the single TextBox check covers all three field
    /// kinds without per-control wiring; non-text controls (buttons, grid cells,
    /// list items) are left alone.
    /// </summary>
    private static void OnGotFocus(Window window, FocusChangedEventArgs e)
    {
        if (e.NewFocusedElement is not TextBox)
        {
            return;
        }

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Defer one dispatcher tick: Avalonia's Win32 backend associates the IME
        // context as part of its own focus handling for the SAME input event.
        // Closing the IME after that association has settled avoids it being
        // re-opened underneath us.
        Dispatcher.UIThread.Post(
            () => CloseImeForWindow(hwnd),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Close the IME open status for <paramref name="hwnd"/> so composition is
    /// dismissed and keystrokes flow through as direct alphanumeric input. Never
    /// throws — IME control must not crash the UI over a focus change.
    /// </summary>
    private static void CloseImeForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // A window with no IME loaded (e.g. user is on a pure-ASCII keyboard
        // layout) returns NULL — nothing to do.
        IntPtr himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ImmSetOpenStatus(himc, false);
        }
        catch
        {
            // Best-effort: never let an IME quirk take down the UI.
        }
        finally
        {
            ImmReleaseContext(hwnd, himc);
        }
    }

    // --- imm32 IME control (P/Invoke) ------------------------------------
    // LibraryImport source-gen (project convention, AOT-friendly). Blittable-only
    // signatures — IntPtr + Win32 BOOL. imm32 has no A/W variants for these, so the
    // literal entry-point names resolve directly.

    [LibraryImport("imm32.dll")]
    private static partial IntPtr ImmGetContext(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmSetOpenStatus(IntPtr hIMC, [MarshalAs(UnmanagedType.Bool)] bool fOpen);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}
