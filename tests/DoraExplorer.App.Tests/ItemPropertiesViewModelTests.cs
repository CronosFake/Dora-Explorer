using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O texto da janela Propriedades: o que aparece para um arquivo, uma pasta, vários itens, uma unidade e um item que não está no
/// disco, e o que chega depois (totais da pasta, SHA-256). Os favoritos apontam para um arquivo temporário, nunca para o do usuário.
/// </summary>
[Collection("App statics")]
public sealed class ItemPropertiesViewModelTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-propsvm-" + Guid.NewGuid().ToString("N"))).FullName;

    public ItemPropertiesViewModelTests() => FavoritesService.UseStore(Path.Combine(_work, "favoritos", "favorites.json"));

    public void Dispose()
    {
        FavoritesService.UseStore(null);
        FileOperationTests.DeleteQuietly(_work);
    }

    private string NewFile(string relative, string content)
    {
        var path = Path.Combine(_work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static NavigationTreeNodeViewModel FileNode(string path) => new(Path.GetFileName(path), NavigationNodeKind.File, path);

    private static NavigationTreeNodeViewModel FolderNode(string path) => new(Path.GetFileName(path), NavigationNodeKind.Directory, path);

    private static async Task<ItemPropertiesViewModel> Loaded(params NavigationTreeNodeViewModel[] nodes)
    {
        var model = ItemPropertiesViewModel.Create(nodes);
        await model.LoadAsync();
        return model;
    }

    private static string Row(ItemPropertiesViewModel model, string section, string label) =>
        model.Sections.Single(s => s.Title == section).Rows.Single(r => r.Label == label).Value;

    // ---- um arquivo ----

    [Fact]
    public async Task A_file_shows_the_general_dates_attributes_security_and_verification_sections()
    {
        var path = NewFile("pasta\\relatorio.txt", "0123456789");

        var model = await Loaded(FileNode(path));

        model.Title.Should().Be("Propriedades de relatorio.txt");
        model.Name.Should().Be("relatorio.txt");
        model.Sections.Select(s => s.Title).Should().Equal("Geral", "Datas", "Atributos", "Segurança", "Verificação");
        Row(model, "Geral", "Caminho").Should().Be(path);
        Row(model, "Geral", "Local").Should().Be(Path.GetDirectoryName(path));
        Row(model, "Geral", "Tamanho").Should().Be("10 B (10 bytes)");
        Row(model, "Geral", "Tipo").Should().EndWith("(.txt)");
        Row(model, "Datas", "Modificado").Should().NotBe("—");
        Row(model, "Segurança", "Proprietário").Should().Contain(Environment.UserName);
        model.CanComputeHash.Should().BeTrue();
        model.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Sizes_are_written_in_the_app_format_followed_by_the_exact_bytes()
    {
        var model = await Loaded(FileNode(NewFile("dois-kb.bin", new string('x', 2048))));

        Row(model, "Geral", "Tamanho").Should().Be("2 KB (2.048 bytes)");
        Row(model, "Geral", "Tamanho em disco").Should().MatchRegex(@"^\d+(,\d+)? (KB|MB) \(\d{1,3}(\.\d{3})* bytes\)$");
    }

    [Fact]
    public async Task A_file_that_is_a_favorite_says_so()
    {
        var path = NewFile("favorito.txt", "x");
        FavoritesService.TryAdd(path, out _).Should().BeTrue();

        Row(await Loaded(FileNode(path)), "Atributos", "Favorito").Should().Be("Sim");
        Row(await Loaded(FileNode(NewFile("comum.txt", "x"))), "Atributos", "Favorito").Should().Be("Não");
    }

    [Fact]
    public async Task Attributes_are_listed_by_name_in_portuguese()
    {
        var path = NewFile("travado.txt", "x");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
        try
        {
            var text = Row(await Loaded(FileNode(path)), "Atributos", "Atributos");

            text.Should().Contain("Somente leitura").And.Contain("Oculto");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(FileAttributes.Normal, "Nenhum")]
    [InlineData(FileAttributes.Archive, "Pronto para arquivamento")]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.System, "Somente leitura, Sistema")]
    [InlineData(FileAttributes.Directory, "Nenhum")]
    public void Attribute_names(FileAttributes attributes, string expected) =>
        ItemPropertiesViewModel.AttributesText(attributes).Should().Be(expected);

    [Fact]
    public async Task A_library_shows_a_details_section_with_its_version()
    {
        var model = await Loaded(FileNode(typeof(ItemPropertiesService).Assembly.Location));

        model.Sections.Select(s => s.Title).Should().Contain("Detalhes");
        Row(model, "Detalhes", "Versão do arquivo").Should().MatchRegex(@"^\d+\.\d+");
        Row(model, "Geral", "Abre com").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_file_that_is_gone_says_so_instead_of_showing_empty_rows()
    {
        var model = await Loaded(FileNode(Path.Combine(_work, "sumiu.txt")));

        Row(model, "Geral", "Situação").Should().Contain("não existe mais");
        model.CanComputeHash.Should().BeFalse();
    }

    // ---- uma pasta ----

    [Fact]
    public async Task A_folder_shows_what_is_inside_it_once_the_count_finishes()
    {
        NewFile("docs\\a.txt", new string('a', 10));
        NewFile("docs\\sub\\b.txt", new string('b', 20));

        var model = await Loaded(FolderNode(Path.Combine(_work, "docs")));

        Row(model, "Geral", "Conteúdo").Should().Be("2 arquivos, 1 pasta");
        Row(model, "Geral", "Tamanho").Should().Be("30 B (30 bytes)");
        model.Sections.Select(s => s.Title).Should().NotContain("Verificação", "o SHA-256 é de arquivo, não de pasta");
        model.CanComputeHash.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_folder_says_it_is_empty()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_work, "vazia")).FullName;

        Row(await Loaded(FolderNode(empty)), "Geral", "Conteúdo").Should().Be("Vazia");
    }

    [Fact]
    public async Task Closing_the_window_while_a_folder_is_being_counted_stops_quietly()
    {
        NewFile("grande\\a.txt", "x");
        var model = ItemPropertiesViewModel.Create([FolderNode(Path.Combine(_work, "grande"))]);

        var loading = model.LoadAsync();
        model.Dispose();

        await loading; // não pode lançar: fechar a janela cancela o que estava rodando
        model.IsLoading.Should().BeFalse();
    }

    // ---- vários itens ----

    [Fact]
    public async Task Several_items_are_summed_and_the_common_folder_is_shown()
    {
        var a = NewFile("lote\\a.txt", new string('a', 10));
        var b = NewFile("lote\\b.txt", new string('b', 5));
        NewFile("lote\\pasta\\c.txt", new string('c', 20));
        var folder = Path.Combine(_work, "lote", "pasta");
        FavoritesService.TryAdd(a, out _).Should().BeTrue();

        var model = await Loaded(FileNode(a), FileNode(b), FolderNode(folder));

        model.Title.Should().Be("Propriedades de 3 itens");
        model.Subtitle.Should().Be("2 arquivos e 1 pasta");
        Row(model, "Geral", "Local").Should().Be(Path.Combine(_work, "lote"));
        Row(model, "Geral", "Contém").Should().Be("3 arquivos, 0 pastas");
        Row(model, "Geral", "Tamanho").Should().Be("35 B (35 bytes)");
        Row(model, "Geral", "Favoritos").Should().Be("1 de 3");
        model.CopyablePath.Should().Be(string.Join(Environment.NewLine, a, b, folder));
    }

    [Fact]
    public async Task Items_from_different_folders_say_there_are_several_locations()
    {
        var model = await Loaded(FileNode(NewFile("um\\a.txt", "x")), FileNode(NewFile("dois\\b.txt", "x")));

        Row(model, "Geral", "Local").Should().Be("Vários locais");
    }

    // ---- unidade e itens fora do disco ----

    [Fact]
    public async Task A_drive_shows_its_format_and_how_full_it_is()
    {
        var root = Path.GetPathRoot(_work)!;

        var model = await Loaded(new NavigationTreeNodeViewModel("Disco Local", NavigationNodeKind.Drive, root));

        model.Subtitle.Should().Be("Unidade");
        Row(model, "Geral", "Sistema de arquivos").Should().NotBeNullOrWhiteSpace();
        Row(model, "Geral", "Capacidade").Should().Contain("bytes");
        Row(model, "Geral", "Em uso").Should().MatchRegex(@"\(\d+ ?%\)");
        Row(model, "Geral", "Livre").Should().Contain("bytes");
    }

    [Fact]
    public async Task An_item_inside_an_archive_shows_only_what_the_listing_knows_and_explains_why()
    {
        var zip = Path.Combine(_work, "pacote.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("x.txt");
        }

        var entry = new NavigationTreeNodeViewModel("x.txt", NavigationNodeKind.File, ArchivePathHelper.Combine(zip, "x.txt"))
        {
            SizeInBytes = 1536,
            LastModified = new DateTime(2026, 11, 2, 3, 41, 12)
        };

        var model = await Loaded(entry);

        Row(model, "Geral", "Tamanho").Should().Be("1,5 KB (1.536 bytes)");
        Row(model, "Geral", "Modificado").Should().Be("segunda-feira, 2 de novembro de 2026, 03:41:12");
        Row(model, "Geral", "Observação").Should().Contain("não está no disco");
        model.CanComputeHash.Should().BeFalse();
        model.Sections.Should().ContainSingle();
    }

    // ---- SHA-256 ----

    [Fact]
    public async Task Computing_the_sha256_fills_its_row_and_gives_the_button_back()
    {
        var model = await Loaded(FileNode(NewFile("abc.txt", "abc")));
        Row(model, "Verificação", "SHA-256").Should().Be(ItemPropertiesViewModel.HashIdleText);

        await model.ComputeHashAsync();

        Row(model, "Verificação", "SHA-256").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        model.HashButtonText.Should().Be(ItemPropertiesViewModel.HashButtonIdle);
    }

    [Fact]
    public async Task Asking_again_while_the_sha256_runs_cancels_it()
    {
        var path = Path.Combine(_work, "enorme.bin");
        using (var big = new FileStream(path, FileMode.Create))
        {
            big.SetLength(512L * 1024 * 1024);
        }

        var model = await Loaded(FileNode(path));

        var running = model.ComputeHashAsync();
        model.HashButtonText.Should().Be(ItemPropertiesViewModel.HashButtonRunning);
        await model.ComputeHashAsync(); // o segundo pedido cancela o primeiro
        await running;

        Row(model, "Verificação", "SHA-256").Should().Be("Cálculo cancelado");
        model.HashButtonText.Should().Be(ItemPropertiesViewModel.HashButtonIdle);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_explains_it_in_the_sha256_row()
    {
        var path = NewFile("em-uso.bin", "conteudo");
        var model = await Loaded(FileNode(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        await model.ComputeHashAsync();

        Row(model, "Verificação", "SHA-256").Should().StartWith("Não foi possível ler o arquivo");
        model.HashButtonText.Should().Be(ItemPropertiesViewModel.HashButtonIdle);
    }
}
