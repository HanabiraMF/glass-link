using System.Runtime.InteropServices;
using GlassLink.Protocol;

namespace GlassLink.Input;

public readonly record struct KeyStrokeInfo(int VirtualKey, int ScanCode, bool Extended, string DisplayName);

/// <summary>
/// Browser KeyboardEvent.code -> Windows virtual-key + Set-1 scan code mapping.
/// The mapping is deliberately explicit: the virtual-hardware layer needs scan codes,
/// while the software layer benefits from the same scan-code path for games.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<string, KeyStrokeInfo> Codes = Build();

    /// <summary>
    /// Resolves a printable character to a scan-code stroke plus the modifier keys
    /// required by the active Windows keyboard layout. Used by the mobile virtual
    /// keyboard for the hardware injection layer without falling back to SendInput.
    /// </summary>
    public static bool TryResolveCharacter(char character, out KeyStrokeInfo info, out byte layoutModifiers)
    {
        info = default;
        layoutModifiers = 0;
        var result = NativeKeyLookup.VkKeyScan(character);
        if (result == -1) return false;

        layoutModifiers = (byte)((result >> 8) & 0xFF);
        var vk = result & 0xFF;
        var scan = NativeKeyLookup.VirtualKeyToScanCode(vk, out var extended);
        info = new KeyStrokeInfo(vk, scan, extended, character.ToString());
        return scan > 0;
    }

    public static bool TryResolve(InputEvent evt, out KeyStrokeInfo info)
    {
        info = default;

        if (evt.ScanCode is > 0 and <= 0x1FF)
        {
            var vk = evt.VirtualKey is > 0 and <= 0xFF
                ? evt.VirtualKey.Value
                : NativeKeyLookup.ScanCodeToVirtualKey(evt.ScanCode.Value);
            info = new KeyStrokeInfo(vk, evt.ScanCode.Value & 0xFF, evt.Extended == true, evt.Code ?? evt.Key ?? $"Scan{evt.ScanCode}");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(evt.Code) && Codes.TryGetValue(evt.Code, out info))
        {
            if (evt.VirtualKey is > 0 and <= 0xFF)
                info = info with { VirtualKey = evt.VirtualKey.Value };
            if (evt.Extended is { } ext)
                info = info with { Extended = ext };
            return true;
        }

        if (evt.VirtualKey is > 0 and <= 0xFF)
        {
            var vk = evt.VirtualKey.Value;
            var scan = NativeKeyLookup.VirtualKeyToScanCode(vk, out var extended);
            info = new KeyStrokeInfo(vk, scan, extended, evt.Code ?? evt.Key ?? $"VK_{vk:X2}");
            return scan != 0;
        }

        if (!string.IsNullOrEmpty(evt.Key) && evt.Key.Length == 1)
        {
            var ch = evt.Key[0];
            var vkScan = NativeKeyLookup.VkKeyScan(ch);
            var vk = vkScan & 0xFF;
            var scan = NativeKeyLookup.VirtualKeyToScanCode(vk, out var extended);
            info = new KeyStrokeInfo(vk, scan, extended, evt.Key);
            return scan != 0;
        }

        return false;
    }

    public static bool TryResolveCode(string code, out KeyStrokeInfo info) =>
        Codes.TryGetValue(code, out info);

    private static Dictionary<string, KeyStrokeInfo> Build()
    {
        var map = new Dictionary<string, KeyStrokeInfo>(StringComparer.Ordinal);

        // Letters (Set-1 scancodes).
        Add(map, "KeyA", 0x41, 0x1E); Add(map, "KeyB", 0x42, 0x30); Add(map, "KeyC", 0x43, 0x2E);
        Add(map, "KeyD", 0x44, 0x20); Add(map, "KeyE", 0x45, 0x12); Add(map, "KeyF", 0x46, 0x21);
        Add(map, "KeyG", 0x47, 0x22); Add(map, "KeyH", 0x48, 0x23); Add(map, "KeyI", 0x49, 0x17);
        Add(map, "KeyJ", 0x4A, 0x24); Add(map, "KeyK", 0x4B, 0x25); Add(map, "KeyL", 0x4C, 0x26);
        Add(map, "KeyM", 0x4D, 0x32); Add(map, "KeyN", 0x4E, 0x31); Add(map, "KeyO", 0x4F, 0x18);
        Add(map, "KeyP", 0x50, 0x19); Add(map, "KeyQ", 0x51, 0x10); Add(map, "KeyR", 0x52, 0x13);
        Add(map, "KeyS", 0x53, 0x1F); Add(map, "KeyT", 0x54, 0x14); Add(map, "KeyU", 0x55, 0x16);
        Add(map, "KeyV", 0x56, 0x2F); Add(map, "KeyW", 0x57, 0x11); Add(map, "KeyX", 0x58, 0x2D);
        Add(map, "KeyY", 0x59, 0x15); Add(map, "KeyZ", 0x5A, 0x2C);

        // Digits and punctuation.
        Add(map, "Digit1", 0x31, 0x02); Add(map, "Digit2", 0x32, 0x03); Add(map, "Digit3", 0x33, 0x04);
        Add(map, "Digit4", 0x34, 0x05); Add(map, "Digit5", 0x35, 0x06); Add(map, "Digit6", 0x36, 0x07);
        Add(map, "Digit7", 0x37, 0x08); Add(map, "Digit8", 0x38, 0x09); Add(map, "Digit9", 0x39, 0x0A);
        Add(map, "Digit0", 0x30, 0x0B);
        Add(map, "Backquote", 0xC0, 0x29); Add(map, "Minus", 0xBD, 0x0C); Add(map, "Equal", 0xBB, 0x0D);
        Add(map, "BracketLeft", 0xDB, 0x1A); Add(map, "BracketRight", 0xDD, 0x1B);
        Add(map, "Backslash", 0xDC, 0x2B); Add(map, "Semicolon", 0xBA, 0x27);
        Add(map, "Quote", 0xDE, 0x28); Add(map, "Comma", 0xBC, 0x33);
        Add(map, "Period", 0xBE, 0x34); Add(map, "Slash", 0xBF, 0x35);
        Add(map, "IntlBackslash", 0xE2, 0x56);

        // Function row.
        for (var i = 1; i <= 12; i++)
        {
            var vk = 0x6F + i; // VK_F1 = 0x70
            var scan = i <= 10 ? 0x3A + i : i == 11 ? 0x57 : 0x58;
            Add(map, $"F{i}", vk, scan);
        }

        Add(map, "Escape", 0x1B, 0x01); Add(map, "Tab", 0x09, 0x0F); Add(map, "CapsLock", 0x14, 0x3A);
        Add(map, "Space", 0x20, 0x39); Add(map, "Enter", 0x0D, 0x1C); Add(map, "Backspace", 0x08, 0x0E);
        Add(map, "ScrollLock", 0x91, 0x46); Add(map, "Pause", 0x13, 0x45, extended: false);
        Add(map, "PrintScreen", 0x2C, 0x37, extended: true);

        // Modifiers.
        Add(map, "ShiftLeft", 0xA0, 0x2A); Add(map, "ShiftRight", 0xA1, 0x36);
        Add(map, "ControlLeft", 0xA2, 0x1D); Add(map, "ControlRight", 0xA3, 0x1D, extended: true);
        Add(map, "AltLeft", 0xA4, 0x38); Add(map, "AltRight", 0xA5, 0x38, extended: true);
        Add(map, "MetaLeft", 0x5B, 0x5B, extended: true); Add(map, "MetaRight", 0x5C, 0x5C, extended: true);
        Add(map, "ContextMenu", 0x5D, 0x5D, extended: true);

        // Navigation.
        Add(map, "Insert", 0x2D, 0x52, extended: true); Add(map, "Delete", 0x2E, 0x53, extended: true);
        Add(map, "Home", 0x24, 0x47, extended: true); Add(map, "End", 0x23, 0x4F, extended: true);
        Add(map, "PageUp", 0x21, 0x49, extended: true); Add(map, "PageDown", 0x22, 0x51, extended: true);
        Add(map, "ArrowLeft", 0x25, 0x4B, extended: true); Add(map, "ArrowUp", 0x26, 0x48, extended: true);
        Add(map, "ArrowRight", 0x27, 0x4D, extended: true); Add(map, "ArrowDown", 0x28, 0x50, extended: true);

        // Numpad.
        Add(map, "NumLock", 0x90, 0x45, extended: true);
        Add(map, "NumpadDivide", 0x6F, 0x35, extended: true);
        Add(map, "NumpadMultiply", 0x6A, 0x37);
        Add(map, "NumpadSubtract", 0x6D, 0x4A);
        Add(map, "NumpadAdd", 0x6B, 0x4E);
        Add(map, "NumpadEnter", 0x0D, 0x1C, extended: true);
        Add(map, "NumpadDecimal", 0x6E, 0x53);
        Add(map, "Numpad0", 0x60, 0x52); Add(map, "Numpad1", 0x61, 0x4F);
        Add(map, "Numpad2", 0x62, 0x50); Add(map, "Numpad3", 0x63, 0x51);
        Add(map, "Numpad4", 0x64, 0x4B); Add(map, "Numpad5", 0x65, 0x4C);
        Add(map, "Numpad6", 0x66, 0x4D); Add(map, "Numpad7", 0x67, 0x47);
        Add(map, "Numpad8", 0x68, 0x48); Add(map, "Numpad9", 0x69, 0x49);

        // Media / browser keys useful for remote desktop.
        Add(map, "BrowserBack", 0xA6, 0x6A, extended: true);
        Add(map, "BrowserForward", 0xA7, 0x69, extended: true);
        Add(map, "BrowserRefresh", 0xA8, 0x67, extended: true);
        Add(map, "BrowserStop", 0xA9, 0x68, extended: true);
        Add(map, "BrowserSearch", 0xAA, 0x65, extended: true);
        Add(map, "BrowserFavorites", 0xAB, 0x66, extended: true);
        Add(map, "BrowserHome", 0xAC, 0x32, extended: true);
        Add(map, "VolumeMute", 0xAD, 0x20, extended: true);
        Add(map, "VolumeDown", 0xAE, 0x2E, extended: true);
        Add(map, "VolumeUp", 0xAF, 0x30, extended: true);
        Add(map, "MediaTrackNext", 0xB0, 0x19, extended: true);
        Add(map, "MediaTrackPrevious", 0xB1, 0x10, extended: true);
        Add(map, "MediaStop", 0xB2, 0x24, extended: true);
        Add(map, "MediaPlayPause", 0xB3, 0x22, extended: true);

        // Key-name fallbacks produced by some mobile browsers.
        Add(map, " ", 0x20, 0x39); Add(map, "Shift", 0xA0, 0x2A); Add(map, "Control", 0xA2, 0x1D);
        Add(map, "Alt", 0xA4, 0x38); Add(map, "Meta", 0x5B, 0x5B, extended: true);
        Add(map, "Enter", 0x0D, 0x1C); Add(map, "Escape", 0x1B, 0x01);
        Add(map, "ArrowUp", 0x26, 0x48, extended: true); Add(map, "ArrowDown", 0x28, 0x50, extended: true);
        Add(map, "ArrowLeft", 0x25, 0x4B, extended: true); Add(map, "ArrowRight", 0x27, 0x4D, extended: true);

        return map;
    }

    private static void Add(Dictionary<string, KeyStrokeInfo> map, string code, int vk, int scan, bool extended = false) =>
        map[code] = new KeyStrokeInfo(vk, scan, extended, code);
}

internal static class NativeKeyLookup
{
    private const uint MapVkToVsc = 0;
    private const uint MapVkToVscEx = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    public static short VkKeyScan(char ch) => VkKeyScanW(ch);

    public static int VirtualKeyToScanCode(int virtualKey, out bool extended)
    {
        var value = (int)MapVirtualKeyW((uint)virtualKey, MapVkToVscEx);
        extended = (value & 0xE000) != 0;
        var scan = value & 0xFF;
        if (scan == 0)
        {
            value = (int)MapVirtualKeyW((uint)virtualKey, MapVkToVsc);
            scan = value & 0xFF;
        }
        return scan;
    }

    public static int ScanCodeToVirtualKey(int scanCode)
    {
        var vk = (int)MapVirtualKeyW((uint)(scanCode & 0xFF), 1 /* MAPVK_VSC_TO_VK */);
        if (vk == 0)
            vk = (int)MapVirtualKeyW((uint)(scanCode & 0xFF), 3 /* MAPVK_VSC_TO_VK_EX */);
        return vk;
    }
}
