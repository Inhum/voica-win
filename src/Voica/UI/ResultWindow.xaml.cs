using System.Windows;

namespace Voica.UI;

/// <summary>Editable result window for the "window" output mode (spec §5).</summary>
public partial class ResultWindow : Window
{
    public ResultWindow(string text)
    {
        InitializeComponent();
        TextArea.Text = text;
        Loaded += (_, _) =>
        {
            TextArea.Focus();
            TextArea.SelectAll();
        };
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        AutoInsert.CopyToClipboard(TextArea.Text);
        CopyButton.Content = S.ResultCopied;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
