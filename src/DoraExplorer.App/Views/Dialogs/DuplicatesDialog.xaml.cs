using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// A janela "Itens duplicados" (menu central, Pasta, Duplicados). Não é modal: dá para deixá-la aberta, levar o painel até uma cópia
/// (duplo clique ou botão direito) e voltar. Só uma por vez: escolher outra verificação no menu fecha a anterior (cancelando o que ela
/// ainda procurava) e abre a nova. Fechar a janela cancela a procura em andamento.
/// </summary>
public partial class DuplicatesDialog : Window
{
    private static DuplicatesDialog? _open;

    private readonly DuplicatesViewModel _model;
    private readonly Action<string, string> _locate;

    private DuplicatesDialog(DuplicatesViewModel model, Action<string, string> locate)
    {
        InitializeComponent();
        _model = model;
        _locate = locate;
        DataContext = model;
        Closed += (_, _) =>
        {
            model.Cancel();
            if (ReferenceEquals(_open, this))
            {
                _open = null;
            }
        };
    }

    /// <summary>
    /// Abre a janela com o modelo dado e já começa a procurar (<paramref name="startNow"/>). <paramref name="locate"/> recebe o
    /// caminho da cópia e a pasta pesquisada, e leva o painel até ela.
    /// </summary>
    public static DuplicatesDialog Show(Window? owner, DuplicatesViewModel model, Action<string, string> locate, bool startNow = true)
    {
        _open?.Close();

        var dialog = new DuplicatesDialog(model, locate);
        DialogChrome.Apply(dialog, owner);
        _open = dialog;
        dialog.Show();
        if (startNow)
        {
            _ = dialog.RunAsync();
        }

        return dialog;
    }

    private async Task RunAsync()
    {
        try
        {
            await _model.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao procurar duplicados", ex);
            _model.StatusText = "Falha na procura. Veja o registro (log).";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync().ConfigureAwait(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _model.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (NativeFolderPicker.TryPickFolder(this, _model.RootPath, out var folder) &&
            !string.IsNullOrWhiteSpace(folder))
        {
            _model.RootPath = folder;
        }
    }

    private DuplicateRowViewModel? SelectedFileRow() =>
        ResultsList.SelectedItem is DuplicateRowViewModel { IsHeader: false, FullPath.Length: > 0 } row ? row : null;

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as DuplicateRowViewModel ?? SelectedFileRow();
        if (row is { IsHeader: false, FullPath.Length: > 0 })
        {
            _locate(row.FullPath, _model.RootPath);
        }
    }

    private void ResultsMenu_Opened(object sender, RoutedEventArgs e)
    {
        var enabled = SelectedFileRow() is not null;
        LocateMenuItem.IsEnabled = enabled;
        CopyPathMenuItem.IsEnabled = enabled;
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFileRow() is { } row)
        {
            _locate(row.FullPath, _model.RootPath);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFileRow() is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.FullPath);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // outro programa está com a área de transferência aberta: não vale travar a janela por isso
            AppLogService.Warning($"Não foi possível copiar o caminho: {ex.Message}");
        }
    }
}
