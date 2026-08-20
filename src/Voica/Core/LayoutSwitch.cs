using System;
using Microsoft.Win32;

namespace Voica;

/// <summary>
/// The system's keyboard-layout switch (spec §4, Windows only). By default it sits on the
/// <b>left</b> Alt+Shift; the right Alt does not touch it.
///
/// A bare hotkey is swallowed by the hook whole (§4), so choosing a bare <b>left</b> Alt stops the
/// layout from switching at all — and double tap does not save it either, the first tap is
/// swallowed too. Two people hit this, a live user and the project owner reproducing it.
///
/// The key is not removed from the list for that: whoever switches layouts on Ctrl+Shift or
/// Win+Space has no conflict, and settings store the key code, so the entry would merely vanish
/// from the menu while still working — stranger than the problem. The app warns instead.
/// </summary>
public static class LayoutSwitch
{
    /// <summary>Where Windows keeps the layout-switch combination.</summary>
    private const string ToggleKey = @"Keyboard Layout\Toggle";

    /// <summary>
    /// Whether the registry value means Alt+Shift. Windows stores the combination as a string:
    /// <c>"1"</c> is Alt+Shift, <c>"2"</c> is Ctrl+Shift, <c>"3"</c> the grave accent, <c>"4"</c>
    /// none.
    ///
    /// <b>A missing value counts as Alt+Shift</b>, because that is the Windows default and the
    /// machine that reproduced the conflict has exactly that: the Toggle key present and empty.
    /// Reading absence as "no conflict" would silence the warning precisely where it is needed.
    /// </summary>
    public static bool MeansAltShift(object? toggleValue)
    {
        var value = toggleValue?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) || value == "1";
    }

    /// <summary>
    /// Whether the system currently switches layouts on Alt+Shift. A registry that cannot be read
    /// at all counts as "no" — a warning nobody can act on is worse than a missing one.
    /// </summary>
    public static bool AltShiftSwitchesLayout()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ToggleKey);
            // "Language Hotkey" is what the modern settings UI writes; "Hotkey" is the older name
            // left on machines that never opened the dialog. Neither present means the default.
            return MeansAltShift(key?.GetValue("Language Hotkey") ?? key?.GetValue("Hotkey"));
        }
        catch (Exception ex)
        {
            Log.Error("could not read the layout-switch setting", ex);
            return false;
        }
    }

    /// <summary>
    /// Whether this binding is the one that collides: a bare left Alt, no modifiers of its own.
    /// </summary>
    public static bool CollidesWithLayoutSwitch(HotkeyBinding binding) =>
        !binding.HasModifiers && binding.MainVk == HotkeyBinding.VK_LMENU;

    /// <summary>Whether the warning should be shown for this binding on this machine.</summary>
    public static bool ShouldWarn(HotkeyBinding binding) =>
        CollidesWithLayoutSwitch(binding) && AltShiftSwitchesLayout();
}
