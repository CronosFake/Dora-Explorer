using System.Windows;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// Janela Propriedades: tudo o que o app sabe sobre o item (ou os itens) escolhido no menu do botão direito. Não é modal, como a do
/// Windows: dá para abrir várias e continuar usando os painéis. Fechar a janela cancela o que ela ainda estiver calculando.
/// </summary>
public partial class PropertiesDialog : Window
{
    private readonly ItemPropertiesViewModel _model;

    private PropertiesDialog(ItemPropertiesViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Title = model.Title;
        Closed += (_, _) => _model.Dispose();
        Loaded += OnLoaded;
    }

    /// <summary>Abre a janela para os itens dados (o ícone e os dados chegam logo depois de ela aparecer).</summary>
    public static void Show(Window? owner, IReadOnlyList<NavigationTreeNodeViewModel> nodes)
    {
        var dialog = new PropertiesDialog(ItemPropertiesViewModel.Create(nodes));
        DialogChrome.Apply(dialog, owner);
        dialog.Show();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HeaderIcon.Source = _model.IconPath is { } path
            ? ShellIconService.GetFileIcon(path, _model.IconIsDirectory, size: 40, touchDisk: false, large: true)
            : ShellIconService.GetGenericFileIcon();

        try
        {
            await _model.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao montar a janela Propriedades", ex);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_model.CopyablePath))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_model.CopyablePath);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // outro programa está com a área de transferência aberta: não vale travar a janela por isso
            AppLogService.Warning($"Não foi possível copiar o caminho: {ex.Message}");
        }
    }

    private async void Hash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _model.ComputeHashAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao calcular o SHA-256 e o MD5", ex);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
