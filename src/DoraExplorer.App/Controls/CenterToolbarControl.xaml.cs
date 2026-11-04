using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Controls;

public partial class CenterToolbarControl
{
    private const string ArrowUpGlyph = "\uE70E";
    private const string ArrowDownGlyph = "\uE70D";

    public CenterToolbarControl()
    {
        InitializeComponent();
        SearchPopup.Opened += (_, _) => SearchButton.IsMenuOpen = true;
        SearchPopup.Closed += (_, _) => SearchButton.IsMenuOpen = false;
        SortPopup.Opened += (_, _) => SortButton.IsMenuOpen = true;
        SortPopup.Closed += (_, _) => SortButton.IsMenuOpen = false;
    }

    private CenterToolbarViewModel? ViewModel => DataContext as CenterToolbarViewModel;

    private void CompressButton_MenuClick(object sender, RoutedEventArgs e)
    {
        if (CompressButton.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = CompressButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.DataContext = ViewModel;
        menu.IsOpen = true;
    }

    private void CompressMenu_Opened(object sender, RoutedEventArgs e)
    {
        CompressButton.IsMenuOpen = true;
        if (sender is ContextMenu menu)
        {
            menu.DataContext = ViewModel;
            if (menu.PlacementTarget is null)
            {
                menu.PlacementTarget = CompressButton;
            }
        }
    }

    private void CompressMenu_Closed(object sender, RoutedEventArgs e)
    {
        CompressButton.IsMenuOpen = false;
    }

    private async void CompressFormat_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not MenuItem { Tag: string formatTag })
        {
            return;
        }

        if (!Enum.TryParse<ArchiveFormat>(formatTag, out var format))
        {
            return;
        }

        if (CompressButton.ContextMenu is ContextMenu menu)
        {
            menu.IsOpen = false;
        }

        await ViewModel.CompressCommand.ExecuteAsync(format).ConfigureAwait(true);
    }

    private void DuplicatesButton_MenuClick(object sender, RoutedEventArgs e)
    {
        if (DuplicatesButton.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = DuplicatesButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.DataContext = ViewModel;
        menu.IsOpen = true;
    }

    private void DuplicatesMenu_Opened(object sender, RoutedEventArgs e)
    {
        DuplicatesButton.IsMenuOpen = true;
        if (sender is ContextMenu { PlacementTarget: null } menu)
        {
            menu.PlacementTarget = DuplicatesButton;
        }
    }

    private void DuplicatesMenu_Closed(object sender, RoutedEventArgs e) => DuplicatesButton.IsMenuOpen = false;

    private void DuplicatesMode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not MenuItem { Tag: string modeTag } ||
            !Enum.TryParse<DuplicateMode>(modeTag, out var mode))
        {
            return;
        }

        if (DuplicatesButton.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
        }

        var owner = Window.GetWindow(this);
        if (!ViewModel.TryCreateDuplicates(mode, out var model, out var error) || model is null)
        {
            DoraExplorer.App.Views.Dialogs.MessageDialog.Show(owner, error ?? "Não foi possível procurar duplicados.");
            return;
        }

        // Já começa a procurar na pasta aberta; a janela deixa trocar o tipo, a pasta e as subpastas e procurar de novo.
        DoraExplorer.App.Views.Dialogs.DuplicatesDialog.Show(owner, model, ViewModel.LocateDuplicate);
    }

    private void SearchButton_MenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            SearchPatternBox.Text = ViewModel.SearchPattern;
            ViewModel.RefreshSearchContext();
        }

        SearchPopup.PlacementTarget = SearchButton;
        SearchPopup.IsOpen = true;
        SearchPatternBox.Focus();
        SearchPatternBox.SelectAll();
    }

    private void SortButton_MenuClick(object sender, RoutedEventArgs e)
    {
        SyncSortMenuFromViewModel();
        SortPopup.PlacementTarget = SortButton;
        SortPopup.IsOpen = true;
    }

    private void SyncSortMenuFromViewModel()
    {
        if (ViewModel is null)
        {
            return;
        }

        var options = ViewModel.CurrentSortOptions;
        FoldersFirstCheckBox.IsChecked = options.FoldersFirst;
        UpdateSortArrows(options);
    }

    private void UpdateSortArrows(TreeSortOptions options)
    {
        SetSortArrow(NameSortArrow, SortNameButton, TreeSortField.Name, options);
        SetSortArrow(SizeSortArrow, SortSizeButton, TreeSortField.Size, options);
        SetSortArrow(DateSortArrow, SortDateButton, TreeSortField.Date, options);
        SetSortArrow(ExtensionSortArrow, SortExtensionButton, TreeSortField.Extension, options);
    }

    private static void SetSortArrow(
        TextBlock arrow,
        Button row,
        TreeSortField field,
        TreeSortOptions options)
    {
        var isActive = options.Field == field;
        arrow.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        arrow.Text = options.Ascending ? ArrowUpGlyph : ArrowDownGlyph;
        row.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void FoldersFirstCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || FoldersFirstCheckBox.IsChecked is not bool foldersFirst)
        {
            return;
        }

        var current = ViewModel.CurrentSortOptions;
        ViewModel.ApplySortOptions(current with { FoldersFirst = foldersFirst });
    }

    private void SortFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { Tag: string fieldTag })
        {
            return;
        }

        if (!Enum.TryParse<TreeSortField>(fieldTag, out var field))
        {
            return;
        }

        var current = ViewModel.CurrentSortOptions;
        var ascending = current.Field == field ? !current.Ascending : true;
        var updated = current with { Field = field, Ascending = ascending };
        ViewModel.ApplySortOptions(updated);
        UpdateSortArrows(updated);
    }

    private void SearchRun_Click(object sender, RoutedEventArgs e) => RunSearch();

    // Limites do menu Localizar ao arrastar a alça do canto: não fica menor que o necessário nem maior que a tela comporta.
    private const double SearchMinWidth = 360;
    private const double SearchMaxWidth = 1400;
    private const double SearchMinListHeight = 120;
    private const double SearchMaxListHeight = 900;

    private void SearchResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SearchPopupBorder.Width = SearchPopupSizing.Clamp(SearchPopupBorder.Width + e.HorizontalChange, SearchMinWidth, SearchMaxWidth);
        SearchResultsList.Height = SearchPopupSizing.Clamp(SearchResultsList.Height + e.VerticalChange, SearchMinListHeight, SearchMaxListHeight);
    }

    private void SearchNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        // A janela traz o texto digitado e os resultados que o menu mostra; o menu fecha para não ficar um por cima do outro.
        ViewModel.SearchPattern = SearchPatternBox.Text;
        SearchPopup.IsOpen = false;
        DoraExplorer.App.Views.Dialogs.SearchDialog.ShowOrActivate(Window.GetWindow(this), ViewModel);
    }

    private void SearchPatternBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunSearch();
            e.Handled = true;
        }
    }

    private void RunSearch()
    {
        if (ViewModel is null || !ViewModel.IsSearchEnabled)
        {
            return;
        }

        ViewModel.RunSearchWithPattern(SearchPatternBox.Text);
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var item = (e.OriginalSource as FrameworkElement)?.DataContext as SearchResultItem
                   ?? SearchResultsList.SelectedItem as SearchResultItem;
        if (item is null)
        {
            return;
        }

        ViewModel.OpenSearchResult(item);
    }
}
