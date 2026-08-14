using System;
using System.Windows;

namespace Voica.UI;

/// <summary>Editable result window for the "window" output mode (spec §5).</summary>
public partial class ResultWindow : Window
{
    /// <param name="text">The recognized text.</param>
    /// <param name="monitor">
    /// The monitor the dictation belonged to (spec §5): the window opens on the same screen as the
    /// dictation bar — the one with the focused window — not on the one the mouse hovers over.
    /// That is why the startup location is Manual: WPF's CenterScreen means "the screen holding the
    /// cursor", which is a different screen whenever the mouse was parked elsewhere.
    /// </param>
    public ResultWindow(string text, IntPtr monitor = default)
    {
        InitializeComponent();
        TextArea.Text = text;
        Place(monitor == IntPtr.Zero ? ScreenPlacement.PickMonitor() : monitor);
        Loaded += (_, _) =>
        {
            TextArea.Focus();
            TextArea.SelectAll();
        };
    }

    /// <summary>
    /// Centers the window on its monitor before it is shown. Repeated once the window is laid out,
    /// because a monitor with a different scale resizes it and shifts the center.
    /// </summary>
    private void Place(IntPtr monitor)
    {
        if (!ScreenPlacement.Center(this, monitor)) return;
        SizeChanged += (_, _) => ScreenPlacement.Center(this, monitor);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        AutoInsert.CopyToClipboard(TextArea.Text);
        CopyButton.Content = S.ResultCopied;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
