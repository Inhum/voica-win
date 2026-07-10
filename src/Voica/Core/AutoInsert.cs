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

    /// <summary>Synthesizes a Ctrl+V key chord into whatever window currently has focus.</summary>
    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyDown(VK_CONTROL),
            KeyDown(VK_V),
            KeyUp(VK_V),
            KeyUp(VK_CONTROL),
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Error($"SendInput injected {sent}/{inputs.Length} events (win32 error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Marshalled size of the native INPUT struct (must be 40 on x64, 28 on x86).</summary>
    internal static int NativeInputSize => Marshal.SizeOf<INPUT>();

    private static INPUT KeyDown(ushort vk) => MakeKey(vk, 0);
    private static INPUT KeyUp(ushort vk) => MakeKey(vk, KEYEVENTF_KEYUP);

    private static INPUT MakeKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    // --- Win32 ---

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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
}
