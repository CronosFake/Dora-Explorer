using System.Text.Json;
using System.Xml.Linq;
using DoraExplorer.App.Controls;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O modo "Linhas compactas em coluna": só a coluna Nome se alarga (arrastando a borda direita dela no cabeçalho; a largura fica
/// guardada por painel e o que passar do painel vira rolagem horizontal); Tipo, Modificado e Tamanho têm largura fixa e folgada
/// (mostram sempre o conteúdo inteiro e nunca mudam); as colunas não somem quando o painel estreita; a linha de uma unidade (com
/// a barra de espaço do disco) fica parada quando a lista rola na horizontal; e a caixa do aplicativo padrão cabe dentro da
/// linha confortável. A tela em si só se confere no app real (<c>ui-capture.ps1 -Scenario columns</c>); aqui estão as regras e
/// as peças do XAML.
/// </summary>
[Collection("App statics")]
public sealed class PaneColumnsTests : IDisposable
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private FilePaneViewModel Pane(PaneDisplayOptions? options = null, Action? save = null)
    {
        var pane = _h.CreatePane(_h.Data, options, save);
        pane.CompleteInitialPathRestore();
        return pane;
    }

    // ---- as contas (puras)

    [Fact]
    public void The_fixed_columns_are_wide_enough_for_their_content()
    {
        // "Visual Studio Code" ou "7-Zip File Manager" na caixa do aplicativo padrão (até 150 px, o MaxWidth do estilo) cabe na coluna
        PaneColumnWidths.Type.Should().BeGreaterThan(150);
        // 04/11/2026 (cerca de 66 px) + 8 + 23:22 (cerca de 34 px)
        PaneColumnWidths.Date.Should().BeGreaterThanOrEqualTo(112);
        // "1.023,99 MB" (cerca de 62 px)
        PaneColumnWidths.Size.Should().BeGreaterThanOrEqualTo(72);
    }

    [Fact]
    public void The_default_application_box_can_grow_as_wide_as_the_type_column_allows()
    {
        var style = Load(@"src\DoraExplorer.App\Resources\Theme.xaml").Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "TreeItemDefaultAppButtonStyle");
        var max = double.Parse(style.Elements(Wpf + "Setter")
            .Single(s => (string?)s.Attribute("Property") == "MaxWidth").Attribute("Value")!.Value,
            System.Globalization.CultureInfo.InvariantCulture);

        max.Should().BeGreaterThanOrEqualTo(150, "antes cortava \"7-Zip File Manager\" com reticências (MaxWidth 120)");
        PaneColumnWidths.Type.Should().BeGreaterThan(max, "a caixa mais larga ainda cabe dentro da coluna Tipo");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-30)]
    [InlineData(0)]
    public void A_name_width_that_makes_no_sense_means_automatic(double stored)
    {
        PaneColumnWidths.ClampName(stored).Should().Be(0);
    }

    [Fact]
    public void The_fixed_columns_add_up_to_what_sits_to_the_right_of_the_name()
    {
        PaneColumnWidths.FixedColumnsWidth(36).Should()
            .Be(PaneColumnWidths.Type + PaneColumnWidths.Date + PaneColumnWidths.Size + (2 * PaneColumnWidths.Gap) + 36);
    }

    [Theory]
    [InlineData(0, 53)]
    [InlineData(1, 72)]
    [InlineData(2, 91)]   // medido na tela
    [InlineData(3, 110)]
    [InlineData(-2, 53)]
    public void What_sits_left_of_the_name_is_the_indent_of_the_deepest_row_plus_the_icon(int level, double expected)
    {
        PaneColumnWidths.NameOverhead(level).Should().Be(expected);
    }

    [Theory]
    [InlineData(400, 0, 347)]
    [InlineData(400, 2, 309)]
    [InlineData(400, 5, 252)]
    [InlineData(130, 3, 20)]    // a linha mais larga mal passa do recuo: sobra o resto
    [InlineData(60, 3, 0)]      // menor que o recuo: nunca negativa
    [InlineData(0, 2, 0)]       // sem largura (modo confortável): sem mínimo
    [InlineData(double.NaN, 2, 0)]
    public void Each_row_gives_its_name_the_width_that_completes_the_widest_row(double rowWidth, int depth, double expected)
    {
        PaneColumnWidths.NameCellMinWidth(rowWidth, depth).Should().Be(expected);
    }

    [Fact]
    public void The_name_width_stays_between_the_minimum_and_the_maximum_once_it_is_set()
    {
        PaneColumnWidths.ClampName(300).Should().Be(300);
        PaneColumnWidths.ClampName(1_000_000).Should().Be(PaneColumnWidths.MaxName);
        PaneColumnWidths.ClampName(30).Should().Be(PaneColumnWidths.MinName, "uma largura guardada pequena demais vira o mínimo, não o automático");
    }

    [Fact]
    public void Dragging_the_right_edge_of_the_name_widens_it_to_the_right_and_narrows_it_to_the_left()
    {
        PaneColumnWidths.AfterNameDrag(190, 100).Should().Be(290);
        PaneColumnWidths.AfterNameDrag(190, 0).Should().Be(190);
        PaneColumnWidths.AfterNameDrag(190, -50).Should().Be(140, "dá para diminuir: abaixo da largura do maior nome os nomes terminam em reticências");
        PaneColumnWidths.AfterNameDrag(190, 1_000_000).Should().Be(PaneColumnWidths.MaxName);
    }

    [Fact]
    public void Dragging_all_the_way_to_the_left_stops_at_the_minimum_and_does_not_jump_back_to_automatic()
    {
        PaneColumnWidths.AfterNameDrag(190, -500).Should().Be(PaneColumnWidths.MinName);
        PaneColumnWidths.AfterNameDrag(double.NaN, 10).Should().Be(PaneColumnWidths.MinName);
        PaneColumnWidths.AfterNameDrag(190, double.NaN).Should().Be(PaneColumnWidths.MinName);
    }

    [Theory]
    [InlineData(0, 181, 181)]       // automática: parte da largura que a coluna tem na tela
    [InlineData(300, 181, 300)]     // já ajustada: parte da ajustada
    [InlineData(150, 400, 150)]     // ajustada menor que a automática: continua dela
    [InlineData(0, 20, 100)]        // coluna menor que o mínimo: o mínimo
    [InlineData(0, double.NaN, 100)]
    public void The_drag_starts_from_the_width_the_column_has_now(double stored, double visible, double expected)
    {
        PaneColumnWidths.NameDragStart(stored, visible).Should().Be(expected);
    }

    [Theory]
    [InlineData(300, 0, 300 + 412 + 32 - 53)]     // linha do nível 0: o que a coluna tem menos o que fica à esquerda do nome
    [InlineData(300, 2, 300 + 412 + 32 - 91)]
    [InlineData(100, 5, 0 + 412 + 32)]            // coluna menor que o recuo: o nome fica sem largura, as colunas continuam inteiras
    public void The_row_content_has_a_fixed_width_when_the_name_is_set_by_the_user(double manual, int depth, double expected)
    {
        PaneColumnWidths.NameRowContentWidth(manual, PaneColumnWidths.FixedColumnsWidth(36), depth).Should().Be(expected);
    }

    [Fact]
    public void The_columns_start_at_the_same_place_in_every_row_whatever_its_level()
    {
        // a coluna Tipo começa depois de: recuo e ícone (overhead) + o nome; a grade da linha começa depois do recuo, da seta e da borda
        foreach (var depth in new[] { 1, 3, 6 })
        {
            var content = PaneColumnWidths.NameRowContentWidth(400, PaneColumnWidths.FixedColumnsWidth(36), depth);
            var typeColumnStartsAt = (PaneColumnWidths.NameOverhead(depth) - 32) + (content - PaneColumnWidths.FixedColumnsWidth(36));

            typeColumnStartsAt.Should().Be(400, "todas as linhas têm a coluna Tipo no mesmo lugar: onde termina a coluna Nome do cabeçalho");
        }
    }

    [Fact]
    public void Every_row_ends_up_with_the_same_natural_width_whatever_its_level()
    {
        // a largura natural da linha é o recuo + a célula do nome: com o mínimo acima, é a mesma em qualquer nível
        foreach (var depth in new[] { 1, 2, 4, 6 })
        {
            (PaneColumnWidths.NameOverhead(depth) + PaneColumnWidths.NameCellMinWidth(500, depth)).Should().Be(500);
        }
    }

    // ---- o painel

    [Fact]
    public void A_new_pane_has_the_fixed_widths_and_an_automatic_name()
    {
        var pane = Pane();

        pane.TypeColumnWidth.Should().Be(PaneColumnWidths.Type);
        pane.DateColumnWidth.Should().Be(PaneColumnWidths.Date);
        pane.SizeColumnWidth.Should().Be(PaneColumnWidths.Size);
        pane.NameColumnWidth.Should().Be(0);
    }

    [Fact]
    public void The_name_width_saved_in_the_pane_options_is_used_when_the_pane_opens()
    {
        var options = PaneTestHarness.BareOptions();
        options.NameColumnWidth = 320;

        Pane(options).NameColumnWidth.Should().Be(320);
    }

    [Fact]
    public void Dragging_changes_the_name_width_on_screen_but_only_saves_at_the_end_of_the_drag()
    {
        var saves = 0;
        var options = PaneTestHarness.BareOptions();
        var pane = Pane(options, () => saves++);
        var changed = new List<string?>();
        pane.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pane.ResizeNameColumn(190, 40);
        pane.ResizeNameColumn(190, 100);

        pane.NameColumnWidth.Should().Be(290);
        options.NameColumnWidth.Should().Be(290, "a largura vai para as opções do painel enquanto arrasta");
        changed.Should().Contain(nameof(FilePaneViewModel.NameColumnWidth), "a lista e o cabeçalho refazem a largura");
        saves.Should().Be(0, "gravar a cada pixel do arrasto seria escrever o arquivo de configuração dezenas de vezes");

        pane.CommitColumnWidths();

        saves.Should().Be(1);
    }

    [Fact]
    public void The_other_columns_never_change_when_the_name_is_widened()
    {
        var pane = Pane();

        pane.ResizeNameColumn(190, 250);

        pane.TypeColumnWidth.Should().Be(PaneColumnWidths.Type);
        pane.DateColumnWidth.Should().Be(PaneColumnWidths.Date);
        pane.SizeColumnWidth.Should().Be(PaneColumnWidths.Size);
    }

    [Fact]
    public void Double_clicking_the_edge_goes_back_to_automatic_and_saves()
    {
        var saves = 0;
        var options = PaneTestHarness.BareOptions();
        var pane = Pane(options, () => saves++);
        pane.ResizeNameColumn(190, 100);

        pane.ResetNameColumnWidth();

        pane.NameColumnWidth.Should().Be(0);
        options.NameColumnWidth.Should().Be(0);
        saves.Should().Be(1);
    }

    [Fact]
    public void Each_pane_keeps_its_own_name_width()
    {
        var left = PaneTestHarness.BareOptions();
        var right = PaneTestHarness.BareOptions();
        var leftPane = Pane(left);
        var rightPane = Pane(right);

        leftPane.ResizeNameColumn(190, 100);

        leftPane.NameColumnWidth.Should().Be(290);
        rightPane.NameColumnWidth.Should().Be(0);
        right.NameColumnWidth.Should().Be(0);
    }

    [Fact]
    public void The_name_width_survives_saving_and_loading_the_settings()
    {
        var options = PaneTestHarness.BareOptions();
        options.NameColumnWidth = 412;

        _h.Settings.SaveLeftPaneDisplayOptions(options);

        _h.Settings.LoadLeftPaneDisplayOptions().NameColumnWidth.Should().Be(412);
    }

    [Fact]
    public void A_settings_file_with_the_old_per_column_widths_loads_with_an_automatic_name()
    {
        // uma versão anterior guardava a largura de Tipo, Modificado e Tamanho: esses campos já não existem e são ignorados
        var options = JsonSerializer.Deserialize<PaneDisplayOptions>(
            """{"ShowFavorites":false,"TypeColumnWidth":48,"DateColumnWidth":90,"SizeColumnWidth":60}""")!;

        options.ShowFavorites.Should().BeFalse();
        options.NameColumnWidth.Should().Be(0);
        Pane(options).TypeColumnWidth.Should().Be(PaneColumnWidths.Type, "a largura de Tipo é fixa, não a que estava no arquivo");
    }

    // ---- a largura da linha mais larga da lista inteira (a barra horizontal não anda quando a lista rola)

    private static NavigationTreeNodeViewModel Folder(string name, int depth, bool expanded, params NavigationTreeNodeViewModel[] children)
    {
        var node = new NavigationTreeNodeViewModel(name, NavigationNodeKind.Directory, @"C:\" + name) { IsExpanded = expanded };
        foreach (var child in children)
        {
            node.Children.Add(child);
        }

        return node;
    }

    private static NavigationTreeNodeViewModel FileNode(string name, int depth) =>
        new(name, NavigationNodeKind.File, @"C:\" + name);

    // 7 px por letra: a conta do teste não depende da fonte do Windows
    private static double SevenPerLetter(string text) => text.Length * 7;

    [Fact]
    public void The_widest_row_is_the_longest_name_in_the_open_folders_with_its_indent_and_icon()
    {
        var root = Folder("C:", 0, true, FileNode("curto.txt", 1), FileNode("um-nome-bem-mais-comprido.txt", 1));

        NameWidthScanner.WidestRowWidth([root], SevenPerLetter)
            .Should().Be(PaneColumnWidths.NameOverhead(1) + ("um-nome-bem-mais-comprido.txt".Length * 7));
    }

    [Fact]
    public void A_deeper_row_counts_its_larger_indent()
    {
        var shallow = FileNode("abcdefghij", 1);
        var deep = FileNode("abcdefghij", 2);
        var root = Folder("C:", 0, true, shallow, Folder("pasta", 1, true, deep));

        // o nível vem da descida: a raiz é 0, a pasta e o arquivo raso são 1 e o arquivo fundo é 2
        NameWidthScanner.WidestRowWidth([root], SevenPerLetter).Should().Be(PaneColumnWidths.NameOverhead(2) + 70);
    }

    [Fact]
    public void A_closed_folder_does_not_count_what_is_inside_it()
    {
        var root = Folder("C:", 0, true, FileNode("a.txt", 1), Folder("fechada", 1, false, FileNode("nome-muito-muito-muito-comprido-escondido.txt", 2)));

        NameWidthScanner.WidestRowWidth([root], SevenPerLetter)
            .Should().Be(PaneColumnWidths.NameOverhead(1) + ("fechada".Length * 7), "só as linhas da pasta aberta existem na lista");
    }

    [Fact]
    public void The_top_level_rows_are_not_counted_because_they_only_take_the_visible_area()
    {
        var drive = Folder("uma-unidade-com-nome-gigantesco-para-testar", 0, false);

        NameWidthScanner.WidestRowWidth([drive], SevenPerLetter).Should().Be(0);
    }

    [Fact]
    public void A_closed_top_level_row_does_not_count_its_children()
    {
        var closedDrive = Folder("C:", 0, false, FileNode("um-nome-bem-comprido-dentro-da-unidade-fechada.txt", 1));

        NameWidthScanner.WidestRowWidth([closedDrive], SevenPerLetter).Should().Be(0);
    }

    [Fact]
    public void Placeholders_and_empty_names_are_ignored()
    {
        var placeholder = new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true };
        var root = Folder("C:", 0, true, placeholder, FileNode("abc", 1));

        NameWidthScanner.WidestRowWidth([root], SevenPerLetter).Should().Be(PaneColumnWidths.NameOverhead(1) + 21);
    }

    [Fact]
    public void In_a_huge_folder_only_the_longest_names_are_measured()
    {
        var files = Enumerable.Range(0, 5000).Select(i => FileNode($"arquivo{i}", 1)).ToList();
        files.Add(FileNode("este-e-o-nome-mais-comprido-de-toda-a-pasta.txt", 1));
        var root = Folder("C:", 0, true, [.. files]);
        var measured = new List<string>();

        var widest = NameWidthScanner.WidestRowWidth([root], text => { measured.Add(text); return text.Length * 7; });

        NameWidthScanner.CandidatesPerFolder.Should().BeInRange(1, 10);
        measured.Count.Should().BeLessThanOrEqualTo(10, "medir cada nome de uma pasta enorme custaria segundos");
        widest.Should().Be(PaneColumnWidths.NameOverhead(1) + ("este-e-o-nome-mais-comprido-de-toda-a-pasta.txt".Length * 7));
    }

    // todas as linhas visíveis medidas uma a uma (sem escolher as mais compridas): o que a varredura tem de achar
    private static double WidestByBruteForce(IEnumerable<NavigationTreeNodeViewModel> nodes, int depth = 0)
    {
        var widest = 0.0;
        foreach (var node in nodes.Where(n => !n.IsPlaceholder && !string.IsNullOrEmpty(n.Name)))
        {
            if (depth > 0)
            {
                widest = Math.Max(widest, PaneColumnWidths.NameOverhead(depth) + SevenPerLetter(node.Name));
            }

            if (node.IsExpanded)
            {
                widest = Math.Max(widest, WidestByBruteForce(node.Children, depth + 1));
            }
        }

        return widest;
    }

    [Fact]
    public void The_pane_row_width_is_the_widest_row_of_the_open_folders()
    {
        var folder = _h.Folder("largura");
        PaneTestHarness.File(folder, "curto.txt");
        PaneTestHarness.File(folder, "um-nome-bem-mais-comprido-do-que-os-outros-todos-os-outros-arquivos.txt");
        var pane = Pane();
        pane.NameTextMeasure = SevenPerLetter;
        pane.NavigateToPath(folder);

        pane.RefreshWidestNameRow();

        // as pastas acima da de teste (a bin do projeto, com DLLs de nome comprido) também estão abertas: vale a linha mais larga
        pane.NameRowWidth.Should().Be(WidestByBruteForce(pane.NavigationRoots));
        pane.NameRowWidth.Should().BeGreaterThan(PaneColumnWidths.NameOverhead(1));
    }

    [Fact]
    public void A_name_set_by_the_user_wins_over_the_widest_name_wider_or_narrower_and_goes_back_with_a_double_click()
    {
        var folder = _h.Folder("largura");
        PaneTestHarness.File(folder, "um-nome-bem-mais-comprido-do-que-os-outros.txt");
        var pane = Pane();
        pane.NameTextMeasure = SevenPerLetter;
        pane.NavigateToPath(folder);
        pane.RefreshWidestNameRow();
        var widest = pane.NameRowWidth;
        widest.Should().BeGreaterThan(PaneColumnWidths.MinName + 100, "abre automático: do tamanho do maior nome");
        pane.NameManualWidth.Should().Be(0);

        pane.ResizeNameColumn(widest, 150);
        pane.NameRowWidth.Should().Be(widest + 150);
        pane.NameManualWidth.Should().Be(widest + 150);

        pane.ResizeNameColumn(widest, -100);
        pane.NameRowWidth.Should().Be(widest - 100, "dá para diminuir abaixo do maior nome");
        pane.NameManualWidth.Should().Be(widest - 100);

        pane.ResetNameColumnWidth();
        pane.NameRowWidth.Should().Be(widest, "o duplo clique volta ao automático");
        pane.NameManualWidth.Should().Be(0);
    }

    [Fact]
    public void A_narrow_name_does_not_change_when_the_folders_open_or_close()
    {
        var folder = _h.Folder("largura");
        PaneTestHarness.File(folder, "um-nome-bem-mais-comprido-do-que-os-outros.txt");
        var pane = Pane();
        pane.NameTextMeasure = SevenPerLetter;
        pane.NavigateToPath(folder);
        pane.ResizeNameColumn(300, -120);

        pane.RefreshWidestNameRow();

        pane.NameRowWidth.Should().Be(180, "com a largura do usuário a varredura dos nomes não manda mais");
    }

    [Fact]
    public void The_manual_name_width_only_counts_in_columns_mode()
    {
        var pane = Pane();
        pane.ResizeNameColumn(300, -100);
        pane.NameManualWidth.Should().Be(200);

        _h.ViewSettings.SetCompactRows(false);
        pane.NameManualWidth.Should().Be(0, "nas linhas confortáveis não há colunas");
        pane.FixedColumnsWidth.Should().Be(PaneColumnWidths.FixedColumnsWidth(0));

        _h.ViewSettings.SetCompactRows(true);
        pane.NameManualWidth.Should().Be(200, "voltando ao modo em colunas a largura do usuário volta");
        pane.FixedColumnsWidth.Should().Be(PaneColumnWidths.FixedColumnsWidth(36));
    }

    [Fact]
    public void Narrowing_the_name_tells_the_rows_and_the_header()
    {
        var pane = Pane();
        var changed = new List<string?>();
        pane.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pane.ResizeNameColumn(300, -100);

        changed.Should().Contain([nameof(FilePaneViewModel.NameManualWidth), nameof(FilePaneViewModel.NameRowWidth), nameof(FilePaneViewModel.NameColumnWidth)]);
    }

    [Fact]
    public void The_row_width_is_zero_in_comfortable_rows_and_comes_back_in_columns_mode()
    {
        var folder = _h.Folder("largura");
        PaneTestHarness.File(folder, "um-nome-bem-mais-comprido-do-que-os-outros.txt");
        var pane = Pane();
        pane.NameTextMeasure = SevenPerLetter;
        pane.NavigateToPath(folder);
        pane.RefreshWidestNameRow();
        pane.NameRowWidth.Should().BeGreaterThan(0);

        _h.ViewSettings.SetCompactRows(false);
        pane.NameRowWidth.Should().Be(0, "nas linhas confortáveis não há colunas");

        _h.ViewSettings.SetCompactRows(true);
        pane.NameRowWidth.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Refreshing_tells_the_list_only_when_the_widest_row_changed()
    {
        var folder = _h.Folder("largura");
        PaneTestHarness.File(folder, "um-nome-bem-mais-comprido-do-que-os-outros.txt");
        var pane = Pane();
        pane.NameTextMeasure = SevenPerLetter;
        pane.NavigateToPath(folder);
        var changed = 0;
        pane.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(FilePaneViewModel.NameRowWidth) ? 1 : 0;

        pane.RefreshWidestNameRow();
        pane.RefreshWidestNameRow();
        pane.RefreshWidestNameRow();

        changed.Should().Be(1, "refazer com o mesmo resultado não manda a lista medir de novo");
    }

    [Fact]
    public void Widening_the_name_tells_the_list_that_the_row_width_changed()
    {
        var pane = Pane();
        var changed = new List<string?>();
        pane.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pane.ResizeNameColumn(190, 100);
        pane.ResetNameColumnWidth();

        changed.Count(n => n == nameof(FilePaneViewModel.NameRowWidth)).Should().Be(2);
    }

    // ---- a linha da unidade fica parada na rolagem horizontal (a conta, pura)

    [Fact]
    public void The_sticky_element_always_takes_the_visible_width_even_without_scroll()
    {
        // sem rolagem: a mesma largura de antes, parada (ligar e desligar conforme a lista rolasse fazia a tela piscar)
        var layout = StickyToViewport.Resolve(viewportWidth: 500, horizontalOffset: 0, currentLeft: 0, currentTranslate: 0);

        layout.Active.Should().BeTrue();
        layout.TranslateX.Should().Be(0);
        layout.Width.Should().Be(500);
    }

    [Fact]
    public void An_element_with_no_visible_area_is_left_alone()
    {
        StickyToViewport.Resolve(0, 0, 0, 0).Active.Should().BeFalse();
    }

    [Fact]
    public void With_horizontal_scroll_the_element_moves_right_by_the_offset_and_keeps_the_visible_width()
    {
        // um elemento que sem rolagem ficaria em 0: com a lista rolada 120 px ele aparece em -120
        var layout = StickyToViewport.Resolve(500, 120, currentLeft: -120, currentTranslate: 0);

        layout.Active.Should().BeTrue();
        layout.TranslateX.Should().Be(120, "anda para a direita o mesmo tanto que o conteúdo anda para a esquerda");
        layout.Width.Should().Be(500, "a largura da área visível: a barra do disco não se comprime nem se estica");
    }

    [Fact]
    public void The_sticky_width_is_the_same_before_and_after_it_was_already_moved()
    {
        var first = StickyToViewport.Resolve(500, 120, currentLeft: -120, currentTranslate: 0);
        // já deslocada por uma rolagem anterior: onde ela aparece agora, mais o que rolou, menos o que já foi deslocado
        var again = StickyToViewport.Resolve(500, 180, currentLeft: 0 - 180 + 120, currentTranslate: 120);

        again.TranslateX.Should().Be(180);
        again.Width.Should().Be(first.Width);
    }

    [Fact]
    public void An_element_that_starts_inside_the_viewport_gets_only_the_width_that_is_left()
    {
        StickyToViewport.Resolve(500, 40, currentLeft: 60 - 40, currentTranslate: 0).Width.Should().Be(440);
    }

    // ---- touchpad e roda inclinada

    [Theory]
    [InlineData(120, 120)]
    [InlineData(-120, -120)]
    [InlineData(30, 30)]
    public void The_horizontal_wheel_delta_is_the_signed_high_word_of_wParam(int delta, int expected)
    {
        var wParam = (long)(ushort)(short)delta << 16;

        TreeScrollHelper.HorizontalWheelDelta(wParam).Should().Be(expected);
    }

    [Fact]
    public void A_swipe_to_the_right_scrolls_right_and_to_the_left_scrolls_left_inside_the_scrollable_range()
    {
        TreeScrollHelper.HorizontalWheelOffset(100, 120, 400).Should().Be(100 + TreeScrollHelper.HorizontalWheelPixelsPerNotch);
        TreeScrollHelper.HorizontalWheelOffset(100, -120, 400).Should().Be(100 - TreeScrollHelper.HorizontalWheelPixelsPerNotch);
        TreeScrollHelper.HorizontalWheelOffset(10, -240, 400).Should().Be(0);
        TreeScrollHelper.HorizontalWheelOffset(390, 240, 400).Should().Be(400);
        TreeScrollHelper.HorizontalWheelOffset(50, 120, 0).Should().Be(0, "sem o que rolar");
    }

    [Fact]
    public void Small_touchpad_steps_add_up_like_one_notch()
    {
        var offset = 0.0;
        for (var i = 0; i < 4; i++)
        {
            offset = TreeScrollHelper.HorizontalWheelOffset(offset, 30, 400);
        }

        offset.Should().BeApproximately(TreeScrollHelper.HorizontalWheelPixelsPerNotch, 0.001);
    }

    // ---- as barras de rolagem: 50% mais grossas e o canto sem o quadrado branco

    [Fact]
    public void The_scroll_bars_are_half_again_as_thick_as_before()
    {
        var style = Load(@"src\DoraExplorer.App\Resources\ScrollBarStyles.xaml").Descendants(Wpf + "Style")
            .Single(st => (string?)st.Attribute("TargetType") == "ScrollBar");

        string? Setter(XContainer from, string property) => from.Descendants(Wpf + "Setter")
            .Where(x => (string?)x.Attribute("Property") == property)
            .Select(x => (string?)x.Attribute("Value")).FirstOrDefault();

        Setter(style, "Width").Should().Be("12", "eram 8 px");
        Setter(style, "MinWidth").Should().Be("12");
        var horizontal = style.Descendants(Wpf + "Trigger").Single(t => (string?)t.Attribute("Property") == "Orientation");
        Setter(horizontal, "Height").Should().Be("12");
        Setter(horizontal, "MinHeight").Should().Be("12");
    }

    [Fact]
    public void The_corner_where_the_two_scroll_bars_meet_is_not_the_white_system_color()
    {
        var style = Load(@"src\DoraExplorer.App\Resources\ScrollBarStyles.xaml").Descendants(Wpf + "Style")
            .Single(st => (string?)st.Attribute("TargetType") == "ScrollViewer");

        var corner = style.Descendants(Wpf + "Rectangle").Single(r => (string?)r.Attribute(X + "Name") == "Corner");

        ((string?)corner.Attribute("Fill")).Should().Be("Transparent", "o modelo padrão pinta o canto com a cor de controle do sistema (branco)");
        // as peças que o WPF procura
        foreach (var part in new[] { "PART_ScrollContentPresenter", "PART_VerticalScrollBar", "PART_HorizontalScrollBar" })
        {
            style.Descendants().Should().Contain(e => (string?)e.Attribute(X + "Name") == part, part);
        }
    }

    // ---- as raízes e os protocolos da Rede ficam fora das colunas

    [Theory]
    [InlineData(NavigationNodeKind.Directory, true)]
    [InlineData(NavigationNodeKind.File, true)]
    [InlineData(NavigationNodeKind.Archive, true)]
    [InlineData(NavigationNodeKind.Drive, false)]
    [InlineData(NavigationNodeKind.Favorites, false)]
    [InlineData(NavigationNodeKind.RecycleBin, false)]
    [InlineData(NavigationNodeKind.NetworkGroup, false)]
    [InlineData(NavigationNodeKind.Ftp, false)]
    [InlineData(NavigationNodeKind.Sftp, false)]
    [InlineData(NavigationNodeKind.Scp, false)]
    [InlineData(NavigationNodeKind.Tftp, false)]
    public void Only_folders_files_and_archives_take_part_in_the_columns(NavigationNodeKind kind, bool expected)
    {
        new NavigationTreeNodeViewModel("x", kind, @"C:\x").ParticipatesInColumns.Should().Be(expected);
    }

    [Fact]
    public void The_widest_name_ignores_the_names_of_the_roots_and_of_the_network_protocols()
    {
        var longName = "um-nome-gigantesco-de-uma-raiz-ou-de-um-protocolo-de-rede";
        var network = new NavigationTreeNodeViewModel("Rede", NavigationNodeKind.NetworkGroup) { IsExpanded = true };
        network.Children.Add(new NavigationTreeNodeViewModel(longName, NavigationNodeKind.Ftp));
        network.Children.Add(new NavigationTreeNodeViewModel(longName, NavigationNodeKind.Sftp));
        var drive = Folder(longName, 0, true, FileNode("a.txt", 1));

        NameWidthScanner.WidestRowWidth([network, drive], SevenPerLetter)
            .Should().Be(PaneColumnWidths.NameOverhead(1) + ("a.txt".Length * 7), "só a linha do arquivo conta: nem a unidade nem os protocolos");
    }

    [Fact]
    public void An_open_root_with_only_protocols_has_no_row_with_columns()
    {
        var network = new NavigationTreeNodeViewModel("Rede", NavigationNodeKind.NetworkGroup) { IsExpanded = true };
        network.Children.Add(new NavigationTreeNodeViewModel("FTP", NavigationNodeKind.Ftp));
        network.Children.Add(new NavigationTreeNodeViewModel("SFTP", NavigationNodeKind.Sftp));

        NameWidthScanner.HasColumnRows([network]).Should().BeFalse();
        NameWidthScanner.WidestRowWidth([network], SevenPerLetter).Should().Be(0);
    }

    [Fact]
    public void The_drive_row_converters_leave_roots_and_protocols_alone()
    {
        var minWidth = new DoraExplorer.App.Converters.NameCellMinWidthConverter();
        var content = new DoraExplorer.App.Converters.NameRowContentConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var fixedColumns = PaneColumnWidths.FixedColumnsWidth(36);

        // linha das colunas (pasta ou arquivo): recebe a largura mínima e, com o Nome ajustado, a grade de largura fixa
        minWidth.Convert([400.0, 2, true], typeof(double), null!, culture).Should().Be(309.0);
        content.Convert([300.0, fixedColumns, 2, true], typeof(double), "Width", culture)
            .Should().Be(PaneColumnWidths.NameRowContentWidth(300, fixedColumns, 2));
        content.Convert([300.0, fixedColumns, 2, true], typeof(System.Windows.HorizontalAlignment), "Alignment", culture)
            .Should().Be(System.Windows.HorizontalAlignment.Left);

        // raiz ou protocolo (não entra nas colunas): nada muda, nem com o Nome automático nem com ele ajustado
        minWidth.Convert([400.0, 0, false], typeof(double), null!, culture).Should().Be(0.0, "a largura do Nome não alarga a linha da unidade");
        double.IsNaN((double)content.Convert([300.0, fixedColumns, 1, false], typeof(double), "Width", culture)).Should().BeTrue();
        content.Convert([300.0, fixedColumns, 1, false], typeof(System.Windows.HorizontalAlignment), "Alignment", culture)
            .Should().Be(System.Windows.HorizontalAlignment.Stretch);

        // Nome automático: como sempre
        double.IsNaN((double)content.Convert([0.0, fixedColumns, 2, true], typeof(double), "Width", culture)).Should().BeTrue();
        content.Convert([0.0, fixedColumns, 2, true], typeof(System.Windows.HorizontalAlignment), "Alignment", culture)
            .Should().Be(System.Windows.HorizontalAlignment.Stretch);

        // valores que ainda não chegaram
        minWidth.Convert([System.Windows.DependencyProperty.UnsetValue], typeof(double), null!, culture).Should().Be(0.0);
        double.IsNaN((double)content.Convert([System.Windows.DependencyProperty.UnsetValue], typeof(double), "Width", culture)).Should().BeTrue();
    }

    [Fact]
    public void The_name_cell_and_the_row_grid_only_use_the_columns_when_the_row_takes_part_in_them()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");

        var minWidth = pane.Descendants(Wpf + "Grid.MinWidth").Should().ContainSingle().Subject.Element(Wpf + "MultiBinding")!;
        ((string?)minWidth.Attribute("Converter")).Should().Contain("NameCellMinWidthConverter");
        minWidth.Elements(Wpf + "Binding").Select(b => (string?)b.Attribute("Path"))
            .Should().Equal("DataContext.NameRowWidth", "TreeDepth", "ParticipatesInColumns");

        foreach (var (property, parameter) in new[] { ("Grid.Width", "Width"), ("Grid.HorizontalAlignment", "Alignment") })
        {
            var binding = pane.Descendants(Wpf + property).Should().ContainSingle(property).Subject.Element(Wpf + "MultiBinding")!;
            ((string?)binding.Attribute("Converter")).Should().Contain("NameRowContentConverter");
            ((string?)binding.Attribute("ConverterParameter")).Should().Be(parameter);
            binding.Elements(Wpf + "Binding").Select(b => (string?)b.Attribute("Path"))
                .Should().Equal("DataContext.NameManualWidth", "DataContext.FixedColumnsWidth", "TreeDepth", "ParticipatesInColumns");
        }
    }

    [Fact]
    public void The_columns_of_a_row_are_not_drawn_for_roots_and_protocols()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");

        var stack = pane.Descendants(Wpf + "StackPanel.Visibility")
            .Single(v => v.Descendants(Wpf + "Binding").Any(b => ((string?)b.Attribute("Path")) == "DataContext.CompactRows"));
        stack.Descendants(Wpf + "Binding").Select(b => (string?)b.Attribute("Path"))
            .Should().Equal("DataContext.CompactRows", "ParticipatesInColumns");
    }

    // ---- o cabeçalho fica vazio enquanto nenhuma pasta está aberta

    [Fact]
    public void With_every_root_closed_the_header_has_no_titles_and_it_comes_back_when_a_folder_opens()
    {
        var folder = _h.Folder("cabecalho");
        PaneTestHarness.File(folder, "a.txt");
        var pane = Pane();
        pane.NavigateToPath(folder);
        pane.ShowColumnHeader.Should().BeTrue("há linhas de pastas e arquivos abertas");

        var drive = pane.NavigationRoots.First(r => r.Kind == NavigationNodeKind.Drive && r.IsExpanded);
        pane.OnNavigationNodeCollapsed(drive, userInitiated: true);

        pane.ShowColumnHeader.Should().BeFalse("só as raízes aparecem");

        drive.IsExpanded = true;
        pane.OnNavigationNodeExpanded(drive);

        pane.ShowColumnHeader.Should().BeTrue();
    }

    [Fact]
    public void The_header_tells_the_screen_when_it_empties_and_when_it_fills()
    {
        var folder = _h.Folder("cabecalho");
        PaneTestHarness.File(folder, "a.txt");
        var pane = Pane();
        pane.NavigateToPath(folder);
        var changed = 0;
        pane.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(FilePaneViewModel.ShowColumnHeader) ? 1 : 0;
        var drive = pane.NavigationRoots.First(r => r.Kind == NavigationNodeKind.Drive && r.IsExpanded);

        pane.OnNavigationNodeCollapsed(drive, userInitiated: true);
        pane.RefreshColumnHeader();
        pane.RefreshColumnHeader();

        changed.Should().Be(1, "só avisa quando muda");
    }

    [Fact]
    public void The_header_is_always_there_but_hidden_while_there_are_no_column_rows()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var bar = Named(pane.Root!, "ColumnHeaderBar")!;
        var grid = Named(pane.Root!, "ColumnHeaderGrid")!;

        bar.Attribute("Visibility").Should().BeNull("a barra existe nos dois modos (a altura não pula ao abrir uma pasta)");
        var trigger = grid.Descendants(Wpf + "DataTrigger").Should().ContainSingle().Subject;
        ((string?)trigger.Attribute("Binding")).Should().Be("{Binding ShowColumnHeader}");
        ((string?)trigger.Attribute("Value")).Should().Be("False");
        ((string?)trigger.Element(Wpf + "Setter")!.Attribute("Value")).Should().Be("Hidden", "Hidden mantém a altura: o cabeçalho fica vazio");
    }

    // ---- o cabeçalho no modo padrão (linhas confortáveis): travado e com a ordenação

    [Fact]
    public void In_comfortable_rows_the_header_shows_name_modified_and_size_without_the_type_and_without_the_resize_grip()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var header = Named(pane.Root!, "ColumnHeaderGrid")!;
        string? Visibility(string tag) => (string?)header.Elements(Wpf + "Button").Single(b => (string?)b.Attribute("Tag") == tag).Attribute("Visibility");

        Visibility("Extension").Should().Contain("CompactRows", "o Tipo (aplicativo padrão e contagens) só existe em colunas");
        Visibility("Date").Should().Contain("ShowFileModifiedDate", "Modificado some quando o painel é estreito demais, como a data da linha");
        Visibility("Name").Should().BeNull("o Nome aparece nos dois modos");
        Visibility("Size").Should().BeNull("o Tamanho aparece nos dois modos");

        var grip = header.Elements(Wpf + "Thumb").Single();
        ((string?)grip.Attribute("Visibility")).Should().Contain("CompactRows", "no modo padrão as colunas são travadas: sem a borda de arrasto");
    }

    [Fact]
    public void Every_header_title_sorts_the_list_in_both_modes()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var header = Named(pane.Root!, "ColumnHeaderGrid")!;

        header.Elements(Wpf + "Button").Select(b => (string?)b.Attribute("Tag")).Should().BeEquivalentTo("Name", "Extension", "Date", "Size");
        header.Elements(Wpf + "Button").Select(b => (string?)b.Attribute("Click")).Should().OnlyContain(c => c == "ColumnHeader_Click");
    }

    [Fact]
    public void The_comfortable_footer_has_the_same_fixed_columns_as_the_header_so_the_titles_line_up()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var footer = pane.Descendants(Wpf + "StackPanel")
            .Single(p => (string?)p.Attribute("Grid.Row") == "4" && p.Elements(Wpf + "Button").Any());

        var dateCells = footer.Elements(Wpf + "Grid").Where(g => ((string?)g.Attribute("Width"))?.Contains("DateColumnWidth") == true).ToList();
        var sizeCells = footer.Elements(Wpf + "TextBlock").Where(t => ((string?)t.Attribute("Width"))?.Contains("SizeColumnWidth") == true).ToList();

        dateCells.Should().ContainSingle("a data e a hora numa coluna do tamanho da coluna Modificado");
        sizeCells.Should().ContainSingle("o tamanho numa coluna do tamanho da coluna Tamanho");
    }

    [Fact]
    public void Sorting_by_size_from_largest_to_smallest_works_in_comfortable_rows()
    {
        var left = _h.Folder("ordem-esquerda");
        var right = _h.Folder("ordem-direita");
        foreach (var folder in new[] { left, right })
        {
            PaneTestHarness.File(folder, "pequeno.txt", "x");
            PaneTestHarness.File(folder, "grande.txt", new string('x', 500));
            PaneTestHarness.File(folder, "medio.txt", new string('x', 100));
        }

        var main = _h.CreateMain();
        main.LeftPane.RestorePath(left);
        main.RightPane.RestorePath(right);
        main.LeftPane.CompleteInitialPathRestore();
        main.RightPane.CompleteInitialPathRestore();
        _h.ViewSettings.SetCompactRows(false);
        main.LeftPane.CompactRows.Should().BeFalse();

        // clicar em Tamanho: do maior para o menor; clicar de novo inverte
        main.ApplyGlobalSortOptions(DoraExplorer.App.Services.SortHeaderLogic.Toggle(main.GlobalSortOptions, TreeSortField.Size));
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(main.LeftPane, left)!.Children.Select(c => c.Name).SequenceEqual(["grande.txt", "medio.txt", "pequeno.txt"]))
            .Should().BeTrue("o maior primeiro");
        main.GlobalSortOptions.Ascending.Should().BeFalse();

        main.ApplyGlobalSortOptions(DoraExplorer.App.Services.SortHeaderLogic.Toggle(main.GlobalSortOptions, TreeSortField.Size));
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(main.LeftPane, left)!.Children.Select(c => c.Name).SequenceEqual(["pequeno.txt", "medio.txt", "grande.txt"]))
            .Should().BeTrue("o segundo clique inverte: do menor para o maior");
        main.LeftPane.SizeHeaderText.Should().Contain("▲");
    }

    // ---- as colunas não somem mais no modo em colunas

    [Theory]
    [InlineData(200)]
    [InlineData(330)]
    [InlineData(450)]
    public void In_columns_mode_no_column_is_hidden_when_the_pane_gets_narrow(double width)
    {
        var pane = Pane();
        pane.CompactRows.Should().BeTrue();

        pane.UpdateResponsiveLayout(width);

        pane.ShowFileModifiedDate.Should().BeTrue();
        pane.ShowFileModifiedTime.Should().BeTrue();
        pane.UseShortModifiedDate.Should().BeFalse();
        pane.TypeColumnWidth.Should().Be(PaneColumnWidths.Type, "a coluna Tipo antes sumia abaixo de 520 px");
        pane.DateColumnWidth.Should().Be(PaneColumnWidths.Date);
        pane.SizeColumnWidth.Should().Be(PaneColumnWidths.Size);
    }

    [Fact]
    public void Comfortable_rows_still_drop_the_date_when_the_pane_gets_narrow_and_the_columns_come_back_in_columns_mode()
    {
        var pane = Pane();
        _h.ViewSettings.SetCompactRows(false);

        pane.UpdateResponsiveLayout(200);
        pane.ShowFileModifiedDate.Should().BeFalse("na linha confortável a data vai sumindo quando o painel estreita");

        pane.UpdateResponsiveLayout(600);
        pane.ShowFileModifiedDate.Should().BeTrue();
        pane.ShowFileModifiedTime.Should().BeTrue();

        pane.UpdateResponsiveLayout(200);
        _h.ViewSettings.SetCompactRows(true);
        pane.ShowFileModifiedDate.Should().BeTrue("voltando ao modo em colunas a data volta mesmo com o painel estreito");
        pane.ShowFileModifiedTime.Should().BeTrue();
    }

    // ---- a linha da unidade fica parada na rolagem horizontal (a conta, pura)

    // ---- as peças do XAML

    private static XDocument Load(string relativePath) =>
        XDocument.Load(ThemeUsageColorsTests.LocateSource(relativePath));

    private static XElement? Named(XContainer root, string name) =>
        root.Descendants().FirstOrDefault(element => (string?)element.Attribute(X + "Name") == name);

    [Fact]
    public void Only_the_name_column_has_a_grip_in_the_header()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var header = Named(pane.Root!, "ColumnHeaderGrid")!;

        var grips = header.Elements(Wpf + "Thumb").ToList();

        grips.Should().ContainSingle("Tipo, Modificado e Tamanho não se ajustam");
        var grip = grips.Single();
        ((string?)grip.Attribute("Grid.Column")).Should().Be("0");
        ((string?)grip.Attribute("HorizontalAlignment")).Should().Be("Right", "a borda é a direita do Nome");
        ((string?)grip.Attribute("Style")).Should().Contain("ColumnGripStyle");
        grip.Attribute("DragDelta").Should().NotBeNull();
        grip.Attribute("DragCompleted").Should().NotBeNull("é aí que a largura é gravada");
        grip.Attribute("MouseDoubleClick").Should().NotBeNull("duplo clique volta ao automático");

        Load(@"src\DoraExplorer.App\Resources\Theme.xaml").Descendants(Wpf + "Style")
            .Should().Contain(style => (string?)style.Attribute(X + "Key") == "ColumnGripStyle");
    }

    [Fact]
    public void The_fixed_column_headers_take_their_widths_from_the_pane()
    {
        var header = Named(Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml").Root!, "ColumnHeaderGrid")!;

        var widths = header.Elements(Wpf + "Button")
            .ToDictionary(b => (string)b.Attribute("Tag")!, b => (string?)b.Attribute("Width"));

        widths["Extension"].Should().Be("{Binding TypeColumnWidth}");
        widths["Date"].Should().Be("{Binding DateColumnWidth}");
        widths["Size"].Should().Be("{Binding SizeColumnWidth}");
    }

    [Fact]
    public void The_header_moves_with_the_horizontal_scroll_of_the_list()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");

        // um Canvas: uma grade mais larga que o Grid que a contém ganha um recorte de layout do tamanho dele, e deslocada com
        // RenderTransform só aparecia o começo (Modificado e Tamanho sumiam); um ScrollViewer aqui entrou num laço de layout
        var viewport = Named(pane.Root!, "ColumnHeaderViewport")!;
        viewport.Name.LocalName.Should().Be("Canvas");
        ((string?)viewport.Attribute("ClipToBounds")).Should().Be("True", "o que passa da faixa é recortado");
        ((string?)viewport.Attribute("Height")).Should().Contain("ColumnHeaderGrid", "o Canvas não tem altura própria: acompanha a da grade");
        viewport.Elements(Wpf + "Grid").Should().ContainSingle(g => (string?)g.Attribute(X + "Name") == "ColumnHeaderGrid");
        Named(pane.Root!, "ColumnHeaderShift").Should().BeNull("o cabeçalho anda por Canvas.Left, não por RenderTransform");
        viewport.Descendants(Wpf + "ScrollViewer").Should().BeEmpty();
    }

    [Fact]
    public void The_list_has_a_horizontal_scroll_bar_when_needed()
    {
        var style = Load(@"src\DoraExplorer.App\Resources\Theme.xaml").Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "NavigationTreeStyle");

        string? Setter(string property) => style.Elements(Wpf + "Setter")
            .Where(s => (string?)s.Attribute("Property") == property)
            .Select(s => (string?)s.Attribute("Value")).SingleOrDefault();

        Setter("ScrollViewer.HorizontalScrollBarVisibility").Should().Be("Auto",
            "Disabled escondia o que não cabe: nomes longos e o Nome alargado");
        Setter("ScrollViewer.VerticalScrollBarVisibility").Should().Be("Auto");
    }

    [Fact]
    public void The_row_of_a_root_such_as_a_drive_is_pinned_while_the_list_scrolls_sideways()
    {
        var theme = Load(@"src\DoraExplorer.App\Resources\Theme.xaml");
        var item = theme.Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "NavigationTreeItemStyle");

        var header = item.Descendants(Wpf + "Grid")
            .Single(g => g.Elements().Any(e => e.Name.LocalName == "TreeHierarchyGuides"));

        // o cabeçalho da linha (guias, borda do item e conteúdo) inteiro: a barra do disco, o texto de espaço livre, o nome, o
        // destaque da seleção e o ícone ficam juntos
        ((string?)header.Attributes().Single(a => a.Name.LocalName == "StickyToViewport.IsSticky")).Should().Be("{Binding IsNavigationRoot}");
    }

    [Fact]
    public void The_default_application_box_fits_inside_the_comfortable_row()
    {
        var pane = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml");
        var footer = pane.Descendants(Wpf + "StackPanel")
            .Single(p => p.Elements(Wpf + "Button").Any(b => ((string?)b.Attribute("Style"))?.Contains("TreeItemDefaultAppButtonStyle") == true) &&
                         (string?)p.Attribute("Grid.Row") == "4");
        var box = footer.Elements(Wpf + "Button").Single();

        // o estilo do botão tem margem vertical negativa (certa na linha em colunas); no rodapé ela fazia a caixa passar da borda
        ((string?)box.Attribute("Margin")).Should().Be("0");

        var margin = ((string?)footer.Attribute("Margin"))!.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        margin[3].Should().BeGreaterOrEqualTo(4, "folga embaixo da caixa, dentro da borda da linha");
        (margin[1] + margin[3]).Should().BeGreaterThan(4);
    }
}
