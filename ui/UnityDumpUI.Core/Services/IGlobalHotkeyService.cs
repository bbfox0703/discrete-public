// IGlobalHotkeyService.cs — Task 7 (Teleport wave 1). OS-global hotkey seam.
//
// Registers a Win32 hotkey so a teleport action (marker save / recall / undo)
// fires while ANOTHER window — the game — has focus, without alt-tabbing back
// to the dumper. The RegisterHotKey P/Invoke stays behind this interface
// (Platform Abstraction) so Core / ViewModels never touch user32 directly; the
// concrete WindowsGlobalHotkeyService lives in the app project.

using System;

namespace UnityDumpUI.Core.Services;

public interface IGlobalHotkeyService
{
    /// <summary>
    /// Register a specific global hotkey. Returns a handle on success, or null
    /// when the combo is already claimed elsewhere or the platform is
    /// unsupported. <paramref name="modifiers"/> uses the Win32 MOD_* flags (see
    /// <see cref="HotkeyModifiers"/>); <paramref name="vk"/> is the virtual-key
    /// code. <paramref name="onPressed"/> fires on a BACKGROUND thread — marshal
    /// to the UI thread before touching ViewModel state.
    /// </summary>
    IGlobalHotkeyRegistration? RegisterSpecific(uint modifiers, uint vk, string label, Action onPressed);
}

/// <summary>Win32 modifier bit flags for <see cref="IGlobalHotkeyService.RegisterSpecific"/>.</summary>
public static class HotkeyModifiers
{
    public const uint Alt     = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift   = 0x0004;
    public const uint Win     = 0x0008;
}

/// <summary>An active global-hotkey registration. Dispose to unregister.</summary>
public interface IGlobalHotkeyRegistration : IDisposable
{
    /// <summary>Human-readable combo that was claimed, e.g. "Ctrl+Num1".</summary>
    string Label { get; }
}
