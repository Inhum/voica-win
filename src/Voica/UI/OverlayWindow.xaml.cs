using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Voica.UI;

/// <summary>
/// The dictation overlay (spec §4.2): a floating capsule at the bottom center of the screen, on top
/// of other windows, that never takes focus. It lives from the start of recording until the text is
/// ready and shows two states — <c>recording</c> (level wave + «×» cancel / «✓» stop) and
/// <c>transcribing</c> (spinner + caption). Geometry/level math lives in <see cref="OverlayLayout"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    // Never activate: the capsule must not steal focus, otherwise the Ctrl+V insert (spec §5)
    // would land in the overlay instead of the field the user was typing in.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Placement talks to Win32 in physical pixels: the app is PerMonitorV2, so a monitor's work
    // area and the window's own size only line up in device pixels, whatever each monitor's scale.
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

    private readonly Rectangle[] _bars;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Func<double> _level;
    private double _phase;
    private IntPtr _monitor;

    /// <summary>Raised when the user clicks «×» — discard the recording, no transcription.</summary>
    public event Action? Cancelled;
    /// <summary>Raised when the user clicks «✓» — stop and transcribe.</summary>
    public event Action? Stopped;

    /// <param name="inputLevel">Reads the current mic peak (0..1) for the wave.</param>
    public OverlayWindow(Func<double> inputLevel)
    {
        InitializeComponent();
        _level = inputLevel;

        _bars = new Rectangle[OverlayLayout.BarWeights.Length];
        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = OverlayLayout.BarWidth,
                Height = OverlayLayout.MinBarHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Brushes.White,
                Margin = new Thickness(OverlayLayout.BarGap / 2, 0, OverlayLayout.BarGap / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _bars[i] = bar;
            WavePanel.Children.Add(bar);
        }

        _timer.Tick += (_, _) => Animate();
        SizeChanged += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Shows the capsule in its recording state (wave + buttons).</summary>
    public void ShowRecording()
    {
        RecordingPanel.Visibility = Visibility.Visible;
        TranscribingPanel.Visibility = Visibility.Collapsed;
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _phase = 0;
        // The monitor is picked once per appearance, so alt-tabbing mid-dictation doesn't make
        // the capsule hop between screens.
        if (!IsVisible) { _monitor = PickMonitor(); Show(); }
        Reposition();
        _timer.Start();
    }

    /// <summary>Switches the capsule to its transcribing state (spinner + caption).</summary>
    public void ShowTranscribing()
    {
        RecordingPanel.Visibility = Visibility.Collapsed;
        TranscribingPanel.Visibility = Visibility.Visible;
        if (!IsVisible) { _monitor = PickMonitor(); Show(); }
        Reposition();
        // The wave is gone; the spinner runs as an animation, at the compositor's frame rate
        // rather than in ticks of the wave timer.
        _timer.Stop();
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, Spin);
    }

    /// <summary>Hides the capsule (back to idle).</summary>
    public void HideOverlay()
    {
        _timer.Stop();
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (IsVisible) Hide();
    }

    private static DoubleAnimation Spin => new(0, 360, new Duration(TimeSpan.FromSeconds(1)))
    {
        RepeatBehavior = RepeatBehavior.Forever,
    };

    private void Animate()
    {
        // The wave follows the mic level; a slow phase keeps it alive during silence so the user
        // can tell recording is running (and see at a glance that the mic is picking something up).
        _phase += 0.45;
        double scale = OverlayLayout.BarScale(_level());
        for (int i = 0; i < _bars.Length; i++)
        {
            double wobble = 0.12 + 0.10 * Math.Sin(_phase + i * 0.9);
            double target = OverlayLayout.BarHeight(scale + wobble, OverlayLayout.BarWeights[i]);
            // Ease toward the target so the bars glide instead of jumping between frames.
            _bars[i].Height += (target - _bars[i].Height) * 0.5;
        }
    }

    /// <summary>
    /// Pins the capsule to the bottom center of the monitor the dictation belongs to (spec §4.2) —
    /// the one showing the focused window, so the bar appears where the text will land.
    /// </summary>
    private void Reposition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (_monitor == IntPtr.Zero || !GetMonitorInfoW(_monitor, ref info) ||
            !GetWindowRect(handle, out var window))
        {
            // Fall back to the primary work area in WPF units.
            var area = SystemParameters.WorkArea;
            Left = OverlayLayout.Left(area.Left, area.Width, ActualWidth);
            Top = OverlayLayout.Top(area.Top, area.Height, ActualHeight);
            return;
        }

        double width = window.Right - window.Left;
        double height = window.Bottom - window.Top;
        double margin = OverlayLayout.BottomMargin * GetDpiForWindow(handle) / 96.0;

        int x = (int)Math.Round(OverlayLayout.Left(info.rcWork.Left, info.rcWork.Right - info.rcWork.Left, width));
        int y = (int)Math.Round(OverlayLayout.Top(info.rcWork.Top, info.rcWork.Bottom - info.rcWork.Top, height, margin));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// The monitor the capsule belongs to: the one with the focused window. When the dictation was
    /// started from our own UI (the tray menu takes focus), the mouse decides instead.
    /// </summary>
    private static IntPtr PickMonitor()
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

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void OnStop(object sender, RoutedEventArgs e) => Stopped?.Invoke();
}
