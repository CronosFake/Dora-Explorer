using System.Windows;

namespace DoraExplorer.App.Views.Dialogs;

public partial class PromptDialog : Window
{
    public PromptDialog(string title, string message, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputTextBox.Text = defaultValue;
        InputTextBox.SelectAll();
        Loaded += (_, _) => InputTextBox.Focus();
    }

    public string ResultText => InputTextBox.Text;

    public static bool TryPrompt(Window owner, string title, string message, string defaultValue, out string result)
    {
        var dialog = new PromptDialog(title, message, defaultValue);
        DialogChrome.Apply(dialog, owner);

        var accepted = dialog.ShowDialog() == true;
        result = dialog.ResultText;
        return accepted;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            MessageDialog.Show(this, "Introduza um valor.", Title);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
