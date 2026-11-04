using System.Windows;
using System.Windows.Input;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// Os resultados do Localizar numa janela própria, maior e redimensionável (o menu do botão Localizar é pequeno: caminhos fundos não
/// cabem). Usa o mesmo modelo do menu (<see cref="CenterToolbarViewModel"/>): a lista, o texto de andamento e a pasta pesquisada são
/// os mesmos, então pesquisar aqui ou lá atualiza os dois. Não é modal e fica aberta ao localizar um resultado (duplo clique leva o
/// painel até ele). Só uma janela por vez: abrir de novo traz a que já existe.
/// </summary>
public partial class SearchDialog : Window
{
    private static SearchDialog? _open;

    private readonly CenterToolbarViewModel _model;

    private SearchDialog(CenterToolbarViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        PatternTextBox.Text = model.SearchPattern;
        Closed += (_, _) => _open = null;
        Loaded += (_, _) =>
        {
            PatternTextBox.Focus();
            PatternTextBox.SelectAll();
        };
    }

    /// <summary>Abre a janela (ou traz a que já está aberta) com o texto procurado e os resultados de agora.</summary>
    public static SearchDialog ShowOrActivate(Window? owner, CenterToolbarViewModel model)
    {
        if (_open is { } existing)
        {
            existing.PatternTextBox.Text = model.SearchPattern;
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return existing;
        }

        var dialog = new SearchDialog(model);
        DialogChrome.Apply(dialog, owner);
        _open = dialog;
        dialog.Show();
        return dialog;
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void PatternTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunSearch();
            e.Handled = true;
        }
    }

    private void RunSearch()
    {
        if (!_model.IsSearchEnabled)
        {
            return;
        }

        // Pesquisa na pasta aberta no painel ativo agora, como o botão Localizar faz ao abrir o menu.
        _model.RefreshSearchContext();
        _model.RunSearchWithPattern(PatternTextBox.Text);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as SearchResultItem
                   ?? ResultsList.SelectedItem as SearchResultItem;
        if (item is not null)
        {
            _model.OpenSearchResult(item);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
