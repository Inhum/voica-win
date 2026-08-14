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
    /// <param name="monitor">The dictation's monitor; <c>default</c> picks it here.</param>
    public void ShowRecording(IntPtr monitor = default)
    {
        RecordingPanel.Visibility = Visibility.Visible;
        TranscribingPanel.Visibility = Visibility.Collapsed;
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _phase = 0;
        // The monitor is picked once per appearance, so alt-tabbing mid-dictation doesn't make
        // the capsule hop between screens.
        if (!IsVisible) { _monitor = monitor == IntPtr.Zero ? ScreenPlacement.PickMonitor() : monitor; Show(); }
        Reposition();
        _timer.Start();
    }

    /// <summary>Switches the capsule to its transcribing state (spinner + caption).</summary>
    /// <param name="monitor">The dictation's monitor; <c>default</c> picks it here.</param>
    public void ShowTranscribing(IntPtr monitor = default)
    {
        RecordingPanel.Visibility = Visibility.Collapsed;
        TranscribingPanel.Visibility = Visibility.Visible;
        if (!IsVisible) { _monitor = monitor == IntPtr.Zero ? ScreenPlacement.PickMonitor() : monitor; Show(); }
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
        if (ScreenPlacement.BottomCenter(this, _monitor, OverlayLayout.BottomMargin)) return;

        // No monitor info — fall back to the primary work area in WPF units.
        var area = SystemParameters.WorkArea;
        Left = OverlayLayout.Left(area.Left, area.Width, ActualWidth);
        Top = OverlayLayout.Top(area.Top, area.Height, ActualHeight);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void OnStop(object sender, RoutedEventArgs e) => Stopped?.Invoke();
}
