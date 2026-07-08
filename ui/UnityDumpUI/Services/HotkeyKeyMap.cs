// HotkeyKeyMap.cs — Teleport hotkey rebinding (wave 2). Converts an Avalonia
// key press (Key + KeyModifiers) captured in the UI into the Win32
// (modifier-mask, virtual-key) pair that RegisterHotKey wants, plus a
// human-readable label ("Ctrl+F7", "Alt+Num1").
//
// Avalonia's Key enum mirrors WPF's — it is a SEQUENTIAL enum, NOT the Win32
// virtual-key codes — so the bindable ranges (A-Z, digits, numpad, F-keys) are
// converted by offset from a known anchor, and a handful of named keys map
// explicitly. Modifier-only presses (Ctrl/Alt/Shift/Win alone) and unsupported
// keys return false so the capture UI keeps listening for a real key.

using System.Text;
using Avalonia.Input;
using UnityDumpUI.Core.Services;   // HotkeyModifiers

namespace UnityDumpUI.Services;

public static class HotkeyKeyMap
{
    /// <summary>
    /// Convert a captured key press to a Win32 hotkey. Returns false for a
    /// modifier-only press or an unsupported key (the caller keeps capturing);
    /// true with <paramref name="winMods"/>/<paramref name="vk"/>/<paramref name="label"/>
    /// otherwise. Note a bare key (no modifier) is allowed — the caller decides
    /// whether to accept it — but a global hotkey with no modifier claims that
    /// key OS-wide, so the UI should steer users toward a modified combo.
    /// </summary>
    public static bool TryConvert(Key key, KeyModifiers mods, out uint winMods, out uint vk, out string label)
    {
        winMods = ModifiersToWin(mods);
        label = string.Empty;
        if (!TryVirtualKey(key, out vk)) { vk = 0; return false; }
        label = LabelFor(winMods, vk);
        return true;
    }

    /// <summary>Avalonia <see cref="KeyModifiers"/> → Win32 MOD_* mask.</summary>
    public static uint ModifiersToWin(KeyModifiers mods)
    {
        uint m = 0;
        if (mods.HasFlag(KeyModifiers.Control)) m |= HotkeyModifiers.Control;
        if (mods.HasFlag(KeyModifiers.Alt))     m |= HotkeyModifiers.Alt;
        if (mods.HasFlag(KeyModifiers.Shift))   m |= HotkeyModifiers.Shift;
        if (mods.HasFlag(KeyModifiers.Meta))    m |= HotkeyModifiers.Win;
        return m;
    }

    /// <summary>Avalonia <see cref="Key"/> → Win32 virtual-key code for the
    /// bindable set. False = modifier-only / unsupported (Escape, Tab, Enter,
    /// the bare Ctrl/Alt/Shift/Win keys, etc.).</summary>
    public static bool TryVirtualKey(Key key, out uint vk)
    {
        // Contiguous Avalonia enum ranges → contiguous Win32 VK ranges.
        if (key >= Key.A && key <= Key.Z)               { vk = 0x41 + (uint)(key - Key.A);       return true; }
        if (key >= Key.D0 && key <= Key.D9)             { vk = 0x30 + (uint)(key - Key.D0);      return true; }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)   { vk = 0x60 + (uint)(key - Key.NumPad0); return true; }
        if (key >= Key.F1 && key <= Key.F24)            { vk = 0x70 + (uint)(key - Key.F1);      return true; }

        switch (key)
        {
            case Key.Left:     vk = 0x25; return true;
            case Key.Up:       vk = 0x26; return true;
            case Key.Right:    vk = 0x27; return true;
            case Key.Down:     vk = 0x28; return true;
            case Key.Home:     vk = 0x24; return true;
            case Key.End:      vk = 0x23; return true;
            case Key.PageUp:   vk = 0x21; return true;
            case Key.PageDown: vk = 0x22; return true;
            case Key.Insert:   vk = 0x2D; return true;
            case Key.Delete:   vk = 0x2E; return true;
            case Key.Space:    vk = 0x20; return true;
            case Key.OemMinus: vk = 0xBD; return true;
            case Key.OemPlus:  vk = 0xBB; return true;
            case Key.Multiply: vk = 0x6A; return true;
            case Key.Add:      vk = 0x6B; return true;
            case Key.Subtract: vk = 0x6D; return true;
            case Key.Decimal:  vk = 0x6E; return true;
            case Key.Divide:   vk = 0x6F; return true;
            default:           vk = 0;    return false;
        }
    }

    /// <summary>Human label for a Win32 (mask, vk) pair, e.g. "Ctrl+Num1".</summary>
    public static string LabelFor(uint winMods, uint vk)
    {
        var sb = new StringBuilder();
        if ((winMods & HotkeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((winMods & HotkeyModifiers.Alt)     != 0) sb.Append("Alt+");
        if ((winMods & HotkeyModifiers.Shift)   != 0) sb.Append("Shift+");
        if ((winMods & HotkeyModifiers.Win)     != 0) sb.Append("Win+");
        sb.Append(VkName(vk));
        return sb.ToString();
    }

    private static string VkName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();                 // A-Z
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();                 // 0-9
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (char)('0' + (vk - 0x60));     // Num0-9
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);                 // F1-24
        return vk switch
        {
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            0x2D => "Insert", 0x2E => "Delete", 0x20 => "Space",
            0xBD => "-", 0xBB => "=",
            0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-", 0x6E => "Num.", 0x6F => "Num/",
            _ => $"VK_0x{vk:X2}",
        };
    }
}
