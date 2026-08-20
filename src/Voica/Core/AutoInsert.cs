using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Voica;

/// <summary>
/// Delivers recognized text (spec §5). The text is ALWAYS copied to the clipboard (the fallback,
/// even in insert mode); in insert mode a Ctrl+V is synthesized into the focused field via SendInput.
/// Call on the UI (STA) thread.
/// </summary>
public static class AutoInsert
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>Copies text to the clipboard, then pastes if the mode is Insert.</summary>
    public static void Deliver(string text, OutputMode mode)
    {
        CopyToClipboard(text);
        if (mode == OutputMode.Insert)
            SendCtrlV();
    }

    /// <summary>Sets clipboard text, retrying briefly if another app is holding the clipboard open.</summary>
    public static void CopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
    }

    /// <summary>
    /// Synthesizes a Ctrl+V key chord into whatever window currently has focus.
    /// First waits (briefly) until the user's physical modifiers are released: an injected Ctrl
    /// combined with a physically held Shift/Alt would form Ctrl+Shift / Alt+Shift — the system
    /// keyboard-layout switch chords — randomly flipping the input language mid-insert.
    /// </summary>
    public static void SendCtrlV()
    {
        WaitForModifiersReleased();

        // Ctrl goes down FIRST, then any modifier the system still reports as held is released
        // inside the chord. Order matters and was measured: releasing Alt on its own reads as a
        // bare Alt tap, which activates the window menu and swallows the paste — the fix then
        // fails exactly like the bug it was meant to cure.
        var stuck = StuckModifiers();
        var inputs = new System.Collections.Generic.List<INPUT> { KeyDown(VK_CONTROL) };
        foreach (var vk in stuck) inputs.Add(KeyUp(vk));
        inputs.Add(KeyDown(VK_V));
        inputs.Add(KeyUp(VK_V));
        inputs.Add(KeyUp(VK_CONTROL));
        if (stuck.Count > 0)
            Log.Info($"insert: releasing modifiers still reported down ({string.Join(", ", stuck.ConvertAll(v => $"0x{v:X2}"))})");

        var batch = inputs.ToArray();
        uint sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
        if (sent != batch.Length)
            Log.Error($"SendInput injected {sent}/{batch.Length} events (win32 error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// Modifiers the system still reports as held (spec §5). A bare-key hotkey is swallowed whole,
    /// so the focused app can be left believing Alt is down, and the paste then arrives as
    /// Alt+Ctrl+V — not "paste": a terminal renders a bare "v" (or "м" on a Russian layout) instead
    /// of the dictation. Ctrl itself is never in this list: the chord needs it held.
    /// </summary>
    private static System.Collections.Generic.List<ushort> StuckModifiers()
    {
        ushort[] candidates = { VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN_K, VK_RWIN_K };
        var held = new System.Collections.Generic.List<ushort>();
        foreach (var vk in candidates)
            if (IsDown(vk)) held.Add(vk);
        return held;
    }

    /// <summary>Polls until Ctrl/Alt/Shift/Win are all physically up, or ~1 s passes.</summary>
    private static void WaitForModifiersReleased()
    {
        const int timeoutMs = 1000;
        const int pollMs = 15;
        for (int waited = 0; waited < timeoutMs; waited += pollMs)
        {
            if (!AnyModifierDown()) return;
            Thread.Sleep(pollMs);
        }
        Log.Info("insert: modifiers still held after 1 s — injecting anyway");
    }

    private static bool AnyModifierDown() =>
        IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Marshalled size of the native INPUT struct (must be 40 on x64, 28 on x86).</summary>
    internal static int NativeInputSize => Marshal.SizeOf<INPUT>();

    private static INPUT KeyDown(ushort vk) => MakeKey(vk, 0);
    private static INPUT KeyUp(ushort vk) => MakeKey(vk, KEYEVENTF_KEYUP);

    private static INPUT MakeKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            // Include the scan code: layout switchers/IMEs mishandle injected keys with wScan=0.
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                dwFlags = flags,
            },
        },
    };

    // --- Win32 ---

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    // Side-specific codes: an injected up must name the exact key, not the merged VK_MENU/VK_SHIFT.
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LWIN_K = 0x5B;
    private const ushort VK_RWIN_K = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // The union must be sized to its largest member (MOUSEINPUT) so that sizeof(INPUT) matches
    // what SendInput expects (40 bytes on x64). A too-small struct makes SendInput reject the
    // cbSize and inject nothing.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
