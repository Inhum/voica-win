using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Voica.UI;

/// <summary>About window (spec §12): version and privacy note.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = string.Format(S.AboutVersionFmt, AppInfo.Version);
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
