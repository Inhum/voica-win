using System.Windows;

namespace Voica.UI;

/// <summary>
/// Random-phrase confirmation for "Delete all data" (spec §11). The Delete button enables only when
/// the user types the exact phrase (e.g. <c>delete-a1b2</c>).
/// </summary>
public partial class DeleteDataDialog : Window
{
    private readonly string _phrase;

    public DeleteDataDialog(string phrase)
    {
        _phrase = phrase;
        InitializeComponent();
        InstructionText.Text = string.Format(S.DeleteDataConfirmFmt, phrase);
    }

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        DeleteButton.IsEnabled = ConfirmBox.Text == _phrase;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
