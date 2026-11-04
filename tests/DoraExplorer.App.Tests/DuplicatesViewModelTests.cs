using System.Xml.Linq;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// A janela "Itens duplicados": o modelo (estado, linhas, textos, cancelar), a entrada no menu central (Pasta, Duplicados, com um ícone
/// próprio da mesma fonte dos outros) e a estrutura da janela, lida do XAML.
/// </summary>
[Collection("App statics")]
public sealed class DuplicatesViewModelTests : IDisposable
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Folder(string name) => _h.Folder(name);

    private static DuplicatesViewModel Model(string root, DuplicateMode mode = DuplicateMode.Standard, IDuplicateFinderService? finder = null) =>
        new(finder ?? new DuplicateFinderService(maxParallelism: 2), root, mode);

    // ---- procurar e mostrar

    [Fact]
    public async Task A_search_lists_each_group_with_a_header_followed_by_its_copies()
    {
        var root = Folder("dup-lista");
        var a = PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "a")).FullName, "x.txt", "12345");
        var b = PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "b")).FullName, "x.txt", "12345");
        var model = Model(root);

        await model.StartAsync();

        model.Rows.Should().HaveCount(3);
        model.Rows[0].IsHeader.Should().BeTrue();
        model.Rows[0].Name.Should().StartWith("2 cópias").And.Contain("recuperáveis");
        model.Rows[0].Folder.Should().Be("Nome e tamanho iguais");
        model.Rows.Skip(1).Select(row => row.FullPath).Should().Equal(a, b);
        model.Rows.Skip(1).Should().OnlyContain(row => !row.IsHeader && row.Name == "x.txt");
        model.SummaryText.Should().StartWith("1 grupo(s) · 2 arquivos");
        model.StatusText.Should().StartWith("Concluído: 2 arquivo(s) vistos");
        model.IsScanning.Should().BeFalse();
    }

    [Fact]
    public async Task The_deep_mode_shows_the_start_of_the_hash_in_the_group_header()
    {
        var root = Folder("dup-profundo");
        PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "a")).FullName, "um.txt", "mesmo conteudo");
        PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "b")).FullName, "dois.txt", "mesmo conteudo");
        var model = Model(root, DuplicateMode.Deep);

        await model.StartAsync();

        model.Rows[0].Folder.Should().StartWith("SHA-256 ").And.EndWith("…");
        model.Rows.Should().HaveCount(3);
        model.StatusText.Should().Contain("2 lidos");
    }

    [Fact]
    public async Task A_folder_without_duplicates_says_so()
    {
        var root = Folder("dup-nenhum");
        PaneTestHarness.File(root, "so-um.txt", "a");
        var model = Model(root);

        await model.StartAsync();

        model.Rows.Should().BeEmpty();
        model.SummaryText.Should().Be("Nenhum arquivo duplicado.");
    }

    [Fact]
    public async Task The_subfolder_option_is_passed_to_the_search()
    {
        var root = Folder("dup-sub");
        PaneTestHarness.File(root, "t.txt", "12345");
        PaneTestHarness.File(Directory.CreateDirectory(Path.Combine(root, "fundo")).FullName, "t.txt", "12345");
        var model = Model(root);
        model.IncludeSubfolders = false;

        await model.StartAsync();

        model.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_search_replaces_the_old_rows()
    {
        var root = Folder("dup-de-novo");
        var other = Directory.CreateDirectory(Path.Combine(root, "o")).FullName;
        PaneTestHarness.File(root, "a.txt", "12345");
        PaneTestHarness.File(other, "a.txt", "12345");
        var model = Model(root);
        await model.StartAsync();
        model.Rows.Should().HaveCount(3);

        File.Delete(Path.Combine(other, "a.txt"));
        await model.StartAsync();

        model.Rows.Should().BeEmpty();
    }

    // ---- modo e opções

    [Fact]
    public void Choosing_a_mode_flips_the_other_option_and_the_description()
    {
        var model = Model(Folder("dup-modo"));
        model.IsStandardMode.Should().BeTrue();
        model.IsDeepMode.Should().BeFalse();
        var standardText = model.ModeDescription;

        model.IsDeepMode = true;

        model.Mode.Should().Be(DuplicateMode.Deep);
        model.IsStandardMode.Should().BeFalse();
        model.ModeDescription.Should().NotBe(standardText).And.Contain("SHA-256");
        standardText.Should().Contain("nome e o tamanho");
    }

    [Theory]
    [InlineData(DuplicateMode.Standard)]
    [InlineData(DuplicateMode.Deep)]
    public void Unchecking_a_radio_button_does_not_change_the_mode(DuplicateMode start)
    {
        var model = Model(Folder("dup-radio"), start);

        model.IsDeepMode = false;
        model.IsStandardMode = false;

        model.Mode.Should().Be(start);
    }

    [Fact]
    public void The_model_notifies_when_the_mode_changes_so_both_radio_buttons_update()
    {
        var model = Model(Folder("dup-aviso"));
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.IsDeepMode = true;

        changed.Should().Contain(["Mode", "IsStandardMode", "IsDeepMode", "ModeDescription"]);
    }

    [Fact]
    public void A_search_cannot_start_without_a_folder()
    {
        Model("").CanStart.Should().BeFalse();
        Model("   ").CanStart.Should().BeFalse();
        Model(Folder("dup-ok")).CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task Starting_without_a_folder_asks_for_one_and_searches_nothing()
    {
        var model = Model("");

        await model.StartAsync();

        model.StatusText.Should().Contain("Escolha uma pasta");
        model.IsScanning.Should().BeFalse();
    }

    // ---- cancelar e falhar

    /// <summary>Procura que nunca termina sozinha: só acaba cancelando. Avisa quando começou.</summary>
    private sealed class NeverEndingFinder : IDuplicateFinderService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public async Task<DuplicateScanResult> FindAsync(
            DuplicateScanRequest request,
            IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return DuplicateScanResult.Empty;
        }
    }

    [Fact]
    public async Task Cancelling_stops_the_search_and_leaves_the_list_empty()
    {
        var finder = new NeverEndingFinder();
        var model = Model(Folder("dup-cancela"), finder: finder);

        var running = model.StartAsync();
        await finder.Started.Task;
        model.IsScanning.Should().BeTrue();
        model.CanStart.Should().BeFalse("não se começa outra procura por cima de uma em andamento pelo botão");
        model.CanChangeOptions.Should().BeFalse();

        model.Cancel();
        await running;

        model.IsScanning.Should().BeFalse();
        model.StatusText.Should().Be("Procura cancelada.");
        model.Rows.Should().BeEmpty();
        model.LastResult.Should().BeNull();
        model.CanStart.Should().BeTrue();
    }

    [Fact]
    public void Cancelling_with_nothing_running_does_nothing()
    {
        var act = () => Model(Folder("dup-sem-nada")).Cancel();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task A_failure_reading_the_folder_becomes_a_message_and_not_a_crash()
    {
        var model = Model(Folder("dup-falha"), finder: new FailingFinder());

        await model.StartAsync();

        model.StatusText.Should().StartWith("Não foi possível concluir a procura");
        model.IsScanning.Should().BeFalse();
    }

    private sealed class FailingFinder : IDuplicateFinderService
    {
        public Task<DuplicateScanResult> FindAsync(
            DuplicateScanRequest request,
            IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new IOException("disco desligou");
    }

    // ---- textos

    [Theory]
    [InlineData(DuplicateScanPhase.Listing, 12, 0, 0, "Listando as pastas... 12 arquivo(s) vistos")]
    [InlineData(DuplicateScanPhase.Reading, 50, 8, 3, "Comparando o conteúdo... 3 de 8 arquivo(s)")]
    public void The_progress_text_names_the_phase_and_the_counts(DuplicateScanPhase phase, int scanned, int toRead, int read, string expected)
    {
        DuplicateText.Progress(new DuplicateScanProgress(phase, scanned, toRead, read, "x")).Should().Be(expected);
    }

    [Fact]
    public void The_final_status_mentions_files_that_could_not_be_read()
    {
        var text = DuplicateText.Finished(new DuplicateScanResult([], FilesScanned: 40, FilesRead: 6, Unreadable: 2));

        text.Should().Contain("40 arquivo(s) vistos").And.Contain("6 lidos").And.Contain("2 não puderam ser lidos");
    }

    [Fact]
    public void The_final_status_stays_short_when_everything_was_read()
    {
        DuplicateText.Finished(new DuplicateScanResult([], 10, 0, 0)).Should().Be("Concluído: 10 arquivo(s) vistos.");
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Sizes_are_written_like_everywhere_else_in_the_app(long bytes, string expected)
    {
        DuplicateText.Size(bytes).Should().Be(expected);
    }

    // ---- entrada pelo menu central

    [Fact]
    public void The_toolbar_prepares_the_window_for_the_folder_open_in_the_active_pane()
    {
        var root = Folder("dup-painel");
        var main = _h.CreateMain();
        main.LeftPane.RestorePath(root);
        main.LeftPane.CompleteInitialPathRestore();
        main.IsLeftPaneActive = true;

        main.CenterToolbar.TryCreateDuplicates(DuplicateMode.Deep, out var model, out var error).Should().BeTrue(error);

        model!.RootPath.Should().Be(root);
        model.Mode.Should().Be(DuplicateMode.Deep);
        model.IncludeSubfolders.Should().BeTrue("por padrão procura também nas subpastas");
    }

    [Fact]
    public void Without_a_local_folder_the_toolbar_refuses_and_says_why()
    {
        var main = _h.CreateMain();
        main.LeftPane.CompleteInitialPathRestore();
        main.LeftPane.ClearItemSelection();
        main.LeftPane.SelectedNode = null;
        main.LeftPane.CurrentPath = "ftp://servidor/pasta";
        main.IsLeftPaneActive = true;

        main.CenterToolbar.TryCreateDuplicates(DuplicateMode.Standard, out var model, out var error).Should().BeFalse();

        model.Should().BeNull();
        error.Should().Contain("pasta local");
    }

    // ---- botão, ícone e janela (lidos do XAML)

    private static XDocument Load(string relativePath) =>
        XDocument.Load(ThemeUsageColorsTests.LocateSource(relativePath));

    private static XElement PastaGrid(XDocument toolbar)
    {
        var header = toolbar.Descendants(Wpf + "TextBlock").First(block => (string?)block.Attribute("Text") == "PASTA");
        return header.ElementsAfterSelf(Wpf + "UniformGrid").First();
    }

    [Fact]
    public void The_Folder_group_has_the_Duplicates_button_with_the_two_checks_in_a_menu()
    {
        var grid = PastaGrid(Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml"));

        var button = grid.Elements().Single(element => (string?)element.Attribute("Label") == "Duplicados");

        ((string?)button.Attribute("OpensMenu")).Should().Be("True");
        ((string?)button.Attribute("MenuClick")).Should().Be("DuplicatesButton_MenuClick");
        var items = button.Descendants(Wpf + "MenuItem").ToList();
        items.Select(item => (string?)item.Attribute("Header")).Should().Equal("Padrão: nome e tamanho", "Profundo: tamanho e hash");
        items.Select(item => (string?)item.Attribute("Tag")).Should().Equal("Standard", "Deep");
        items.Should().OnlyContain(item => (string?)item.Attribute("Click") == "DuplicatesMode_Click");
    }

    [Fact]
    public void The_menu_tags_are_the_names_of_the_two_modes()
    {
        Enum.GetNames<DuplicateMode>().Should().BeEquivalentTo("Standard", "Deep");
    }

    [Fact]
    public void The_Duplicates_icon_comes_from_the_same_icon_font_and_no_other_button_uses_the_same_glyph()
    {
        var icons = Load(@"src\DoraExplorer.App\Resources\CenterToolbarIcons.xaml").Root!;
        var glyphs = icons.Elements().Where(e => ((string?)e.Attribute(X + "Key"))?.StartsWith("Glyph", StringComparison.Ordinal) == true)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value);

        glyphs.Should().ContainKey("GlyphDuplicates");
        glyphs["GlyphDuplicates"].Should().HaveLength(1).And.Match(value => value[0] >= '' && value[0] <= '', "é um glifo da área privada da fonte de ícones");
        glyphs.Where(pair => pair.Key != "GlyphDuplicates").Select(pair => pair.Value)
            .Should().NotContain(glyphs["GlyphDuplicates"], "o ícone novo tem de ser distinto dos outros");
        icons.Elements().Any(e => (string?)e.Attribute(X + "Key") == "FluentIconFontFamily").Should().BeTrue();

        var button = PastaGrid(Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml"))
            .Elements().Single(element => (string?)element.Attribute("Label") == "Duplicados");
        ((string?)button.Attribute("IconGlyph")).Should().Be("{StaticResource GlyphDuplicates}");
    }

    [Fact]
    public void The_window_has_both_checks_the_subfolder_option_and_adjustable_columns()
    {
        var window = Load(@"src\DoraExplorer.App\Views\Dialogs\DuplicatesDialog.xaml").Root!;

        window.Descendants(Wpf + "RadioButton").Select(radio => (string?)radio.Attribute("Content"))
            .Should().Equal("Padrão (nome e tamanho)", "Profundo (tamanho e hash)");
        window.Descendants(Wpf + "CheckBox").Select(box => (string?)box.Attribute("Content")).Should().Equal("Incluir subpastas");
        var gridView = window.Descendants(Wpf + "GridView").Single();
        ((string?)gridView.Attribute("ColumnHeaderContainerStyle")).Should().Contain("GridViewColumnHeaderStyle");
        gridView.Elements(Wpf + "GridViewColumn").Select(column => (string?)column.Attribute("Header"))
            .Should().Equal("Nome", "Pasta", "Tamanho", "Modificado");
        ((string?)window.Attribute("ResizeMode")).Should().Be("CanResize");
    }
}
