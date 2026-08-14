using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Voica.UI;

/// <summary>
/// Places Voica's windows on the monitor a dictation belongs to (spec §4.2/§5): the one showing
/// the focused window — where the text will land — not the one the mouse happens to hover over.
/// Everything here works in <b>device pixels</b>: the app is PerMonitorV2, so WPF's Left/Top and a
/// monitor's work area only agree on the primary display.
/// </summary>
public static class ScreenPlacement
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// The monitor the dictation belongs to: the one with the focused window. When dictation was
    /// started from our own UI (the tray menu takes focus), the mouse decides instead.
    /// </summary>
    public static IntPtr PickMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid != (uint)Environment.ProcessId)
                return MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        }
        return GetCursorPos(out var cursor) ? MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
    }

    /// <summary>Pins a window to the bottom center of the monitor's work area (the dictation bar).</summary>
    public static bool BottomCenter(Window window, IntPtr monitor, double bottomMarginDip)
    {
        if (!Measure(window, monitor, out var work, out var handle, out double width, out double height))
            return false;

        double margin = bottomMarginDip * GetDpiForWindow(handle) / 96.0;
        Move(handle,
            OverlayLayout.Left(work.Left, work.Right - work.Left, width),
            OverlayLayout.Top(work.Top, work.Bottom - work.Top, height, margin));
        return true;
    }

    /// <summary>Centers a window on the monitor's work area (the result window, spec §5).</summary>
    public static bool Center(Window window, IntPtr monitor)
    {
        if (!Measure(window, monitor, out var work, out var handle, out double width, out double height))
            return false;

        Move(handle,
            OverlayLayout.Left(work.Left, work.Right - work.Left, width),
            work.Top + (work.Bottom - work.Top - height) / 2);
        return true;
    }

    private static bool Measure(Window window, IntPtr monitor, out RECT work, out IntPtr handle,
        out double width, out double height)
    {
        work = default; width = 0; height = 0;
        // EnsureHandle lets us place a window before it is shown, so it never flashes on the
        // wrong monitor first.
        handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero || monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(handle, out var rect)) return false;

        work = info.rcWork;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return true;
    }

    private static void Move(IntPtr handle, double x, double y) =>
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}
