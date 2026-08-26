using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Voica.UI;

/// <summary>
/// Modal messages for a tray-only app (spec §11.4).
///
/// Two things go wrong when a background app shows a message box, and both look like "nothing
/// happened":
///
/// ⚠️ <b>An unowned box does not come to the front.</b> Voica has no main window, so the dialog has
/// no owner; Windows will not give foreground to a process that does not already hold it, and the
/// box lands behind whatever the person is working in — at best the taskbar button blinks. The fix
/// is an owner: a 1×1 transparent window, shown for the life of the dialog. It also decides where
/// the box appears, since a message box centres on its owner — so the owner is placed on the
/// monitor the person is actually looking at, the same one the dictation bar uses (spec §5).
///
/// ⚠️ <b>Identical warnings must not stack.</b> In PTT mode every key press starts a dictation, so
/// holding the key twice used to put two identical dialogs on top of each other. A message box runs
/// its own message loop, so the second request really does arrive while the first is still up.
/// </summary>
public static class DialogHost
{
    private static readonly HashSet<string> Showing = new();

    /// <summary>
    /// Shows <paramref name="message"/> modally, at most one copy at a time.
    /// Returns null when the same message is already on screen.
    /// </summary>
    public static MessageBoxResult? ShowOnce(string message, MessageBoxButton buttons, MessageBoxImage icon)
    {
        if (!Showing.Add(message)) return null;

        var anchor = Anchor();
        try
        {
            anchor.Show();
            return MessageBox.Show(anchor, message, AppInfo.Name, buttons, icon);
        }
        finally
        {
            anchor.Close();
            Showing.Remove(message);
        }
    }

    /// <summary>The invisible owner: 1×1, transparent, off the taskbar, centred where the person is.</summary>
    private static Window Anchor()
    {
        var anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        // Placed before it is shown, so the dialog never appears on the wrong monitor first.
        ScreenPlacement.Center(anchor, ScreenPlacement.PickMonitor());
        return anchor;
    }
}
