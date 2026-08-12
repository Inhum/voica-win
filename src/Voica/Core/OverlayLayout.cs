using System;

namespace Voica;

/// <summary>
/// Pure geometry/level math for the dictation overlay (spec §4.2). Kept out of the window class
/// so the self-test can cover it without any WPF/GUI init.
/// </summary>
public static class OverlayLayout
{
    /// <summary>Gap between the window and the bottom of the work area, in DIPs (the window
    /// carries a transparent margin for its shadow, so the capsule itself sits a bit higher).</summary>
    public const double BottomMargin = 40;

    /// <summary>Wave bar geometry (DIPs), matching the macOS HUD: 7 bars, 3 wide, 5 apart.</summary>
    public const double MinBarHeight = 3;
    public const double MaxBarHeight = 22;
    public const double BarWidth = 3;
    public const double BarGap = 5;

    /// <summary>Relative weights of the wave bars — the middle ones swing widest.</summary>
    public static readonly double[] BarWeights = { 0.45, 0.65, 0.85, 1.0, 0.85, 0.65, 0.45 };

    /// <summary>Horizontal position that centers a window of <paramref name="width"/> in the work area.</summary>
    public static double Left(double workLeft, double workWidth, double width) =>
        workLeft + (workWidth - width) / 2;

    /// <summary>Vertical position that pins a window of <paramref name="height"/> above the work area's bottom.</summary>
    public static double Top(double workTop, double workHeight, double height, double margin = BottomMargin) =>
        workTop + workHeight - height - margin;

    /// <summary>
    /// Maps a linear peak amplitude (0..1) to a 0..1 bar scale. Speech peaks sit low in the linear
    /// range, so a square-root curve with a little gain makes normal speech fill the wave.
    /// </summary>
    public static double BarScale(double linearPeak)
    {
        if (double.IsNaN(linearPeak) || linearPeak <= 0) return 0;
        return Math.Clamp(Math.Sqrt(Math.Min(linearPeak, 1.0)) * 1.3, 0, 1);
    }

    /// <summary>Height of one wave bar for the given scale (0..1) and bar weight.</summary>
    public static double BarHeight(double scale, double weight) =>
        MinBarHeight + (MaxBarHeight - MinBarHeight) * Math.Clamp(scale, 0, 1) * weight;
}
