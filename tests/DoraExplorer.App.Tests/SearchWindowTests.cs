using System.Xml.Linq;
using DoraExplorer.App.Controls;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O Localizar: as colunas do resultado podem ser ajustadas (a alça do cabeçalho), o menu pode ser aumentado, existe um botão para abrir
/// os resultados numa janela própria, e a pesquisa acha itens em subpastas fundas. A tela em si só se confere no app real (as
/// peças do XAML são lidas aqui, sem abrir janela).
/// </summary>
[Collection("App statics")]
public sealed class SearchWindowTests : IDisposable
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static XDocument Load(string relativePath) =>
        XDocument.Load(ThemeUsageColorsTests.LocateSource(relativePath));

    private static XElement? Named(XContainer root, string name) =>
        root.Descendants().FirstOrDefault(element => (string?)element.Attribute(X + "Name") == name);

    // ---- colunas ajustáveis

    [Fact]
    public void The_column_header_template_has_the_gripper_the_gridview_looks_for()
    {
        // Sem um Thumb chamado PART_HeaderGripper o GridView não deixa mudar a largura da coluna arrastando a borda do cabeçalho.
        var style = Load(@"src\DoraExplorer.App\Resources\Theme.xaml").Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(X + "Key") == "GridViewColumnHeaderStyle");

        var gripper = Named(style, "PART_HeaderGripper");

        gripper.Should().NotBeNull("sem a alça as colunas não se ajustam");
        gripper!.Name.Should().Be(Wpf + "Thumb");
        ((string?)gripper.Attribute("Cursor")).Should().Be("SizeWE", "o cursor de ajuste de largura avisa que dá para arrastar");
    }

    [Fact]
    public void The_filler_column_after_the_last_one_gets_no_gripper()
    {
        var style = Load(@"src\DoraExplorer.App\Resources\Theme.xaml").Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(X + "Key") == "GridViewColumnHeaderStyle");

        var trigger = style.Descendants(Wpf + "Trigger").Single(t => (string?)t.Attribute("Property") == "Role");

        ((string?)trigger.Attribute("Value")).Should().Be("Padding");
        var setter = trigger.Element(Wpf + "Setter")!;
        ((string?)setter.Attribute("TargetName")).Should().Be("PART_HeaderGripper");
        ((string?)setter.Attribute("Value")).Should().Be("Collapsed");
    }

    private static void AssertColumnsAreAdjustable(XElement list, params string[] headers)
    {
        var gridView = list.Descendants(Wpf + "GridView").Single();

        ((string?)gridView.Attribute("ColumnHeaderContainerStyle")).Should().Contain("GridViewColumnHeaderStyle");
        ((string?)gridView.Attribute("AllowsColumnReorder")).Should().Be("False", "arrastar o cabeçalho serve para ajustar, não para trocar a ordem");
        gridView.Elements(Wpf + "GridViewColumn").Select(column => (string?)column.Attribute("Header"))
            .Should().Equal(headers);
    }

    [Fact]
    public void The_menu_results_list_uses_the_adjustable_header_and_shows_name_and_path()
    {
        var toolbar = Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml");

        AssertColumnsAreAdjustable(Named(toolbar.Root!, "SearchResultsList")!, "Nome", "Caminho");
    }

    [Fact]
    public void The_new_window_lists_name_path_and_size_with_adjustable_columns()
    {
        var window = Load(@"src\DoraExplorer.App\Views\Dialogs\SearchDialog.xaml");

        AssertColumnsAreAdjustable(Named(window.Root!, "ResultsList")!, "Nome", "Caminho", "Tamanho");
    }

    [Fact]
    public void A_long_path_can_be_read_in_full_from_the_tooltip()
    {
        foreach (var file in new[]
                 {
                     @"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml",
                     @"src\DoraExplorer.App\Views\Dialogs\SearchDialog.xaml"
                 })
        {
            var pathCell = Load(file).Descendants(Wpf + "TextBlock")
                .Single(block => ((string?)block.Attribute("Text"))?.Contains("{Binding FullPath}") == true);

            ((string?)pathCell.Attribute("ToolTip")).Should().Contain("{Binding FullPath}", file);
            ((string?)pathCell.Attribute("TextTrimming")).Should().Be("CharacterEllipsis", file);
        }
    }

    // ---- aumentar o menu e abrir em nova janela

    [Fact]
    public void The_menu_has_a_grip_to_make_it_bigger_and_a_button_to_open_a_new_window()
    {
        var toolbar = Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml");

        var grip = Named(toolbar.Root!, "SearchResizeGrip");
        var button = Named(toolbar.Root!, "SearchNewWindowButton");

        grip.Should().NotBeNull();
        grip!.Name.Should().Be(Wpf + "Thumb");
        ((string?)grip.Attribute("DragDelta")).Should().Be("SearchResizeGrip_DragDelta");
        button.Should().NotBeNull();
        ((string?)button!.Attribute("Content")).Should().Be("Abrir em Janela");
        ((string?)button.Attribute("Click")).Should().Be("SearchNewWindow_Click");
    }

    [Fact]
    public void The_menu_is_not_capped_at_a_small_height_any_more()
    {
        var border = Named(Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml").Root!, "SearchPopupBorder")!;

        border.Attribute("MaxHeight").Should().BeNull("o teto de 420 impedia aumentar o menu");
        ((string?)border.Attribute("Width")).Should().NotBeNullOrEmpty("a largura é ajustada pela alça");
    }

    [Fact]
    public void The_new_window_can_be_resized_and_shows_the_same_search_text_and_status()
    {
        var window = Load(@"src\DoraExplorer.App\Views\Dialogs\SearchDialog.xaml").Root!;

        ((string?)window.Attribute("ResizeMode")).Should().Be("CanResize");
        window.Descendants(Wpf + "TextBlock").Select(block => (string?)block.Attribute("Text"))
            .Should().Contain("{Binding SearchStatusText}").And.Contain("{Binding SearchRootPath}");
        ((string?)Named(window, "ResultsList")!.Attribute("ItemsSource")).Should().Be("{Binding SearchResults}");
    }

    [Theory]
    [InlineData(500, 360, 1400, 500)]
    [InlineData(100, 360, 1400, 360)]
    [InlineData(5000, 360, 1400, 1400)]
    [InlineData(360, 360, 1400, 360)]
    [InlineData(1400, 360, 1400, 1400)]
    public void Enlarging_the_menu_stays_inside_the_limits(double value, double min, double max, double expected)
    {
        SearchPopupSizing.Clamp(value, min, max).Should().Be(expected);
    }

    [Fact]
    public void A_size_not_measured_yet_falls_back_to_the_minimum()
    {
        SearchPopupSizing.Clamp(double.NaN, 360, 1400).Should().Be(360);
    }

    // ---- a pesquisa acha itens em subpastas fundas (o caso que tornava os caminhos difíceis de ler)

    [Fact]
    public void A_search_finds_an_item_buried_in_many_subfolders_and_reports_its_full_path()
    {
        var root = _h.Folder("pesquisa-funda");
        var deep = Directory.CreateDirectory(Path.Combine(root, "a", "b", "c", "d", "e")).FullName;
        var target = PaneTestHarness.File(deep, "relatorio-raro.txt");
        PaneTestHarness.File(root, "outro.txt");

        var results = new FileSearchService(_h.Index).Search(root, "relatorio-raro", recursive: true);

        results.Items.Should().ContainSingle().Which.FullPath.Should().Be(target);
        results.Items[0].Name.Should().Be("relatorio-raro.txt");
        results.LimitReached.Should().BeFalse();
    }

    [Fact]
    public void The_toolbar_search_fills_the_list_the_new_window_shows()
    {
        var root = _h.Folder("pesquisa-barra");
        var target = PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "x", "y")).FullName, "achado-na-barra.txt");
        var main = _h.CreateMain();
        main.LeftPane.RestorePath(root);
        main.LeftPane.CompleteInitialPathRestore();
        main.CenterToolbar.RefreshSearchContext();

        main.CenterToolbar.RunSearchWithPattern("achado-na-barra");

        PaneTestHarness.Eventually(() => main.CenterToolbar.SearchResults.Count == 1).Should().BeTrue(main.CenterToolbar.SearchStatusText);
        main.CenterToolbar.SearchResults[0].FullPath.Should().Be(target);
        main.CenterToolbar.SearchStatusText.Should().Be("1 resultado(s)");
    }
}
