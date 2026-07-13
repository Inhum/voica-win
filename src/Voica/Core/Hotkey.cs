using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Voica;

/// <summary>
/// Global hotkey via a low-level keyboard hook (spec §4). Detects hold/release for PTT and press
/// edges for Toggle. Supports both a bare dedicated key and a modifier combination
/// (see <see cref="HotkeyBinding"/>):
/// <list type="bullet">
/// <item>Bare key → the key is fully <b>swallowed</b> (dedicated to dictation).</item>
/// <item>Combo → only the main key is swallowed, and only while the required modifiers are held,
/// so the modifiers keep working normally and system shortcuts aren't broken.</item>
/// </list>
/// Must be created and disposed on a thread with a message loop (the WPF UI thread); its callbacks
/// fire there, so handlers can touch the UI.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>PTT: hotkey pressed.</summary>
    public event Action? Started;
    /// <summary>PTT: hotkey released.</summary>
    public event Action? Stopped;
    /// <summary>Toggle: one press flips recording on/off.</summary>
    public event Action? Toggled;

    public DictationMode Mode { get; set; } = DictationMode.Toggle;
    public HotkeyBinding Binding { get; set; } = HotkeyBinding.Default;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;   // KBDLLHOOKSTRUCT.flags: event came from SendInput

    private readonly LowLevelKeyboardProc _proc;   // kept alive to prevent GC of the callback
    private IntPtr _hook = IntPtr.Zero;
    private System.Windows.Threading.Dispatcher? _dispatcher;

    private bool _isDown;         // logical: recording (PTT) / toggle armed
    private bool _engagedMain;    // combo: the current main-key press is being intercepted

    // Live modifier states, tracked from the hook stream (left/right combined).
    private bool _ctrl, _alt, _shift, _win;

    public HotkeyManager()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to install keyboard hook (error {Marshal.GetLastWin32Error()}).");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _isDown = false;
        _engagedMain = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);                    // KBDLLHOOKSTRUCT.vkCode
            uint flags = (uint)Marshal.ReadInt32(lParam, 8);       // KBDLLHOOKSTRUCT.flags

            // Ignore injected input (e.g. our own synthesized Ctrl+V): it must neither update the
            // modifier tracking nor be able to trigger/satisfy the hotkey.
            if ((flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            UpdateModifierState(vk, down, up);

            var b = Binding;
            if (vk == b.MainVk)
            {
                if (!b.HasModifiers)
                {
                    // Bare dedicated key: intercept fully.
                    HandlePress(down, up);
                    return 1;
                }

                // Combo: intercept the main key only while the modifiers are held.
                if (down)
                {
                    if (_engagedMain)
                        return 1;                 // auto-repeat while engaged — swallow, no re-fire
                    if (ModifiersSatisfied(b))
                    {
                        _engagedMain = true;
                        HandlePress(down: true, up: false);
                        return 1;
                    }
                    // Modifiers not held → this is a normal key press; let it through.
                }
                else if (up && _engagedMain)
                {
                    _engagedMain = false;
                    HandlePress(down: false, up: true);
                    return 1;                     // consume the up matching the swallowed down
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandlePress(bool down, bool up)
    {
        // Dispatch asynchronously: subscriber work (e.g. opening the mic) must not run inside the
        // low-level hook callback — a callback slower than the system's LowLevelHooksTimeout
        // (~300 ms) gets the hook silently removed, and it stalls keyboard input system-wide.
        if (down && !_isDown)
        {
            _isDown = true;
            var handler = Mode == DictationMode.Ptt ? Started : Toggled;
            if (handler is not null) _dispatcher?.BeginInvoke(handler);
        }
        else if (up && _isDown)
        {
            _isDown = false;
            if (Mode == DictationMode.Ptt && Stopped is { } stopped)
                _dispatcher?.BeginInvoke(stopped);
        }
    }

    private bool ModifiersSatisfied(HotkeyBinding b) =>
        (!b.Ctrl || _ctrl) && (!b.Alt || _alt) && (!b.Shift || _shift) && (!b.Win || _win);

    private void UpdateModifierState(int vk, bool down, bool up)
    {
        if (!down && !up) return;
        switch (vk)
        {
            case HotkeyBinding.VK_LCONTROL:
            case HotkeyBinding.VK_RCONTROL: _ctrl = down; break;
            case HotkeyBinding.VK_LMENU:
            case HotkeyBinding.VK_RMENU: _alt = down; break;
            case HotkeyBinding.VK_LSHIFT:
            case HotkeyBinding.VK_RSHIFT: _shift = down; break;
            case HotkeyBinding.VK_LWIN:
            case HotkeyBinding.VK_RWIN: _win = down; break;
        }
    }

    public void Dispose() => Stop();

    // --- Win32 ---

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
