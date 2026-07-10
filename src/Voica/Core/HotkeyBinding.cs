using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Voica;

/// <summary>
/// A dictation hotkey (spec §4), generalized to support both single dedicated keys and custom
/// combinations. Two shapes:
/// <list type="bullet">
/// <item><b>Bare key</b> (no modifiers): a key safe to dedicate/swallow — Right/Left Alt, CapsLock,
/// ScrollLock, Pause, F13–F24. The hook consumes it entirely, enabling clean PTT and Toggle.</item>
/// <item><b>Combination</b>: modifiers (Ctrl/Alt/Shift/Win) + a main key (e.g. Ctrl+Shift+Space).
/// The hook intercepts only the main key while the modifiers are held, so the modifiers keep working
/// normally and system shortcuts aren't broken.</item>
/// </list>
/// </summary>
public sealed record HotkeyBinding
{
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }

    /// <summary>Virtual-key code of the main/only key.</summary>
    public int MainVk { get; init; }

    public bool HasModifiers => Ctrl || Alt || Shift || Win;

    // Win32 virtual-key codes (winuser.h).
    public const int VK_PAUSE = 0x13;
    public const int VK_CAPITAL = 0x14;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;   // Left Alt
    public const int VK_RMENU = 0xA5;   // Right Alt
    public const int VK_SCROLL = 0x91;
    public const int VK_F13 = 0x7C;
    public const int VK_F24 = 0x87;

    /// <summary>Windows default (spec §4): Right Alt, bare.</summary>
    public static HotkeyBinding Default { get; } = new() { MainVk = VK_RMENU };

    /// <summary>Keys allowed as a bare (modifier-less) hotkey — safe to dedicate/swallow.</summary>
    private static readonly HashSet<int> BareWhitelist = BuildBareWhitelist();

    private static HashSet<int> BuildBareWhitelist()
    {
        var set = new HashSet<int> { VK_RMENU, VK_LMENU, VK_CAPITAL, VK_SCROLL, VK_PAUSE };
        for (int vk = VK_F13; vk <= VK_F24; vk++) set.Add(vk);   // F13–F24
        return set;
    }

    public static bool IsModifierVk(int vk) =>
        vk is VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU
            or VK_LSHIFT or VK_RSHIFT or VK_LWIN or VK_RWIN;

    /// <summary>
    /// A combo is valid if its main key is a real (non-modifier) key; a bare binding is valid only
    /// for whitelisted dedicated keys (so we never swallow a normal typing key).
    /// </summary>
    public bool IsValid() =>
        HasModifiers ? !IsModifierVk(MainVk) : BareWhitelist.Contains(MainVk);

    /// <summary>Compact storage form, e.g. <c>C+S+0x20</c> (Ctrl+Shift+Space) or <c>0xA5</c> (Right Alt).</summary>
    public string ToStorage()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("C");
        if (Alt) parts.Add("A");
        if (Shift) parts.Add("S");
        if (Win) parts.Add("W");
        parts.Add($"0x{MainVk:X2}");
        return string.Join("+", parts);
    }

    public static HotkeyBinding Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default;

        // Legacy names from the earlier fixed-list model.
        if (s.Equals("RightAlt", StringComparison.OrdinalIgnoreCase)) return Default;
        if (s.Equals("LeftAlt", StringComparison.OrdinalIgnoreCase)) return new() { MainVk = VK_LMENU };

        bool c = false, a = false, sh = false, w = false;
        int main = -1;
        foreach (var raw in s.Split('+'))
        {
            var tok = raw.Trim();
            switch (tok.ToUpperInvariant())
            {
                case "C": c = true; break;
                case "A": a = true; break;
                case "S": sh = true; break;
                case "W": w = true; break;
                default:
                    if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(tok[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                        main = v;
                    break;
            }
        }

        if (main < 0) return Default;
        var b = new HotkeyBinding { Ctrl = c, Alt = a, Shift = sh, Win = w, MainVk = main };
        return b.IsValid() ? b : Default;
    }

    /// <summary>Human-readable name, e.g. "Ctrl+Shift+Space" or "Right Alt".</summary>
    public string DisplayName()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(MainVk));
        return string.Join("+", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        VK_RMENU => "Right Alt",
        VK_LMENU => "Left Alt",
        VK_RCONTROL => "Right Ctrl",
        VK_LCONTROL => "Left Ctrl",
        VK_RWIN => "Right Win",
        VK_LWIN => "Left Win",
        VK_RSHIFT => "Right Shift",
        VK_LSHIFT => "Left Shift",
        VK_CAPITAL => "CapsLock",
        VK_SCROLL => "ScrollLock",
        VK_PAUSE => "Pause",
        VK_SPACE => "Space",
        VK_ESCAPE => "Esc",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),               // F1–F24
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),               // 0–9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),               // A–Z
        _ => $"0x{vk:X2}",
    };

    /// <summary>Bare-key presets offered in Settings (in display order).</summary>
    public static IReadOnlyList<HotkeyBinding> Presets { get; } = BuildPresets();

    private static IReadOnlyList<HotkeyBinding> BuildPresets() => new List<HotkeyBinding>
    {
        // Single keys present on ordinary keyboards. F13–F24 stay valid (BareWhitelist) for
        // remapped macro keys, but aren't offered here since most keyboards can't press them.
        new() { MainVk = VK_RMENU },
        new() { MainVk = VK_LMENU },
        new() { MainVk = VK_CAPITAL },
        new() { MainVk = VK_SCROLL },
        new() { MainVk = VK_PAUSE },
    };
}
