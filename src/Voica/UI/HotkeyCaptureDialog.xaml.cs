using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace Voica.UI;

/// <summary>
/// Captures a custom hotkey (spec §4 extension). Commits on the first non-modifier key press,
/// combining the modifiers held at that moment; a bare non-modifier key is accepted only if it is a
/// safe dedicated key (CapsLock, ScrollLock, Pause, F13–F24).
/// </summary>
public partial class HotkeyCaptureDialog : Window
{
    /// <summary>The captured binding (valid only when <see cref="Window.DialogResult"/> is true).</summary>
    public HotkeyBinding? Result { get; private set; }

    public HotkeyCaptureDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        // A lone modifier keypress: show a live preview and wait for the main key.
        if (HotkeyBinding.IsModifierVk(vk))
        {
            PreviewText.Text = ComposePreview(ctrl, alt, shift, win, mainVk: null);
            HintText.Text = S.CaptureHintMainKey;
            return;
        }

        var binding = new HotkeyBinding { Ctrl = ctrl, Alt = alt, Shift = shift, Win = win, MainVk = vk };
        PreviewText.Text = binding.DisplayName();

        if (!binding.IsValid())
        {
            HintText.Text = S.CaptureHintNeedModifier;
            return;
        }

        Result = binding;
        DialogResult = true;
        Close();
    }

    private static string ComposePreview(bool ctrl, bool alt, bool shift, bool win, int? mainVk)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(mainVk is { } vk ? HotkeyBinding.KeyName(vk) : "…");
        return string.Join("+", parts);
    }
}
