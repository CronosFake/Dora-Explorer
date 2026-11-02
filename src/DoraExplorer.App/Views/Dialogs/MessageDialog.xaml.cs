using System.Windows;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DoraExplorer.App.Views.Dialogs;

public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, bool confirm, string? yesText = null, string? noText = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        if (confirm)
        {
            OkPanel.Visibility = Visibility.Collapsed;
            YesNoPanel.Visibility = Visibility.Visible;
            SetButtonText(YesButton, yesText);
            SetButtonText(NoButton, noText);
        }
    }

    /// <summary>
    /// Mostra uma mensagem. Se ela é de "acesso negado" (<see cref="FileErrorText"/>) e o app não está como administrador, a
    /// janela pergunta se o usuário quer reiniciar como administrador; já como administrador, explica o motivo.
    /// </summary>
    public static void Show(Window? owner, string message, string title = "Dora Explorer")
    {
        var elevation = App.Services?.GetService<IElevationService>();

        switch (ElevationPrompt.For(message, elevation?.IsElevated ?? false))
        {
            case ElevationPromptKind.Offer when elevation is not null:
                if (Ask(owner, ElevationPrompt.OfferText(message), title, ElevationPrompt.RestartButton, ElevationPrompt.NotNowButton) &&
                    !elevation.TryRestartElevated(out var error) &&
                    error is not null)
                {
                    ShowPlain(owner, error, title);
                }

                return;

            case ElevationPromptKind.AlreadyElevated:
                ShowPlain(owner, ElevationPrompt.AlreadyElevatedText(message), title);
                return;

            default:
                ShowPlain(owner, FileErrorText.WithoutMarker(message), title);
                return;
        }
    }

    public static bool Confirm(Window? owner, string message, string title = "Dora Explorer")
    {
        var dialog = new MessageDialog(title, message, confirm: true);
        DialogChrome.Apply(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>Pergunta com texto próprio nos dois botões (o primeiro é a resposta que confirma).</summary>
    public static bool Ask(Window? owner, string message, string title, string yesText, string noText)
    {
        var dialog = new MessageDialog(title, message, confirm: true, yesText, noText);
        DialogChrome.Apply(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    private static void ShowPlain(Window? owner, string message, string title)
    {
        var dialog = new MessageDialog(title, message, confirm: false);
        DialogChrome.Apply(dialog, owner);
        dialog.ShowDialog();
    }

    private static void SetButtonText(System.Windows.Controls.Button button, string? text)
    {
        if (text is null)
        {
            return;
        }

        button.Content = text;
        button.Width = double.NaN; // acompanha o texto: "Reiniciar como administrador" não cabe em 90
        button.MinWidth = 90;
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
