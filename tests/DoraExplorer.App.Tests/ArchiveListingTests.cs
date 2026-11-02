using System.Formats.Tar;
using System.IO.Compression;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Pastas dentro de arquivos compactados: o conteúdo de cada pasta vem da própria listagem, porque o índice do disco não as
/// conhece. Sem isso a pasta aparecia sem seta de expandir e com "0 pastas, 0 arquivos".
/// </summary>
public sealed class ArchiveListingTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-archive-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // o banco temporário do teste da árvore ficaria preso e a pasta não seria apagada
        SshTestHelpers.DeleteDirectory(_directory);
    }

    private string CreateZip(params string[] entries)
    {
        var path = Path.Combine(_directory, "teste.zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            var entry = zip.CreateEntry(name);
            if (!name.EndsWith('/') && !name.EndsWith('\\'))
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }
        }

        return path;
    }

    private static IReadOnlyList<FileSystemEntry> List(string archive, string directory = "")
    {
        new ArchiveService().TryListEntries(archive, directory, out var entries, out var error).Should().BeTrue(error);
        return entries;
    }

    private static (int? Folders, int? Files) CountsOf(IReadOnlyList<FileSystemEntry> entries, string name)
    {
        var entry = entries.Single(e => e.Name == name);
        return (entry.ChildFolderCount, entry.ChildFileCount);
    }

    [Fact]
    public void Zip_folders_report_what_they_hold_directly()
    {
        var zip = CreateZip(
            "raiz.txt",
            "pasta/a.txt",
            "pasta/b.txt",
            "pasta/sub/c.txt",
            "vazia/",
            "explicita/",
            "explicita/x.txt",
            "so-subpastas/n1/n2/arquivo.txt");

        var root = List(zip);

        CountsOf(root, "pasta").Should().Be((1, 2));
        CountsOf(root, "vazia").Should().Be((0, 0));
        CountsOf(root, "explicita").Should().Be((0, 1));
        CountsOf(root, "so-subpastas").Should().Be((1, 0));
    }

    [Fact]
    public void Files_have_no_folder_counts_and_the_counts_follow_the_level_being_listed()
    {
        var zip = CreateZip("raiz.txt", "pasta/a.txt", "pasta/sub/c.txt", "pasta/sub/d.txt");

        var root = List(zip);
        root.Single(e => e.Name == "raiz.txt").ChildFolderCount.Should().BeNull();
        root.Single(e => e.Name == "raiz.txt").ChildFileCount.Should().BeNull();

        var inside = List(zip, "pasta");
        CountsOf(inside, "sub").Should().Be((0, 2));
        inside.Single(e => e.Name == "a.txt").ChildFileCount.Should().BeNull();
    }

    [Fact]
    public void Backslash_names_are_counted_like_slash_names()
    {
        var zip = CreateZip("antiga\\a.txt", "antiga\\sub\\b.txt");

        var root = List(zip);

        CountsOf(root, "antiga").Should().Be((1, 1));
    }

    [Fact]
    public void Tar_folders_report_what_they_hold_directly()
    {
        // .tar simples; o .tar.gz tem testes próprios abaixo.
        var path = Path.Combine(_directory, "teste.tar");
        using (var file = File.Create(path))
        using (var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "pasta/"));
            foreach (var name in new[] { "raiz.txt", "pasta/a.txt", "pasta/sub/c.txt" })
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("x"u8.ToArray()) });
            }
        }

        var root = List(path);

        CountsOf(root, "pasta").Should().Be((1, 1));
        root.Single(e => e.Name == "raiz.txt").ChildFolderCount.Should().BeNull();
    }

    [Fact]
    public void Tar_gz_with_files_can_be_listed_with_sizes_and_counts()
    {
        // Arquivos de 1 byte: pular o conteúdo de uma entrada num fluxo gzip falhava com "Unable to parse number". Um arquivo
        // maior no meio garante que a listagem também funciona quando há mais de um bloco de dados.
        var path = Path.Combine(_directory, "teste.tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: false))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "pasta/"));
            foreach (var (name, size) in new[] { ("raiz.txt", 1), ("pasta/a.txt", 5000), ("pasta/sub/c.txt", 1) })
            {
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(new byte[size]) });
            }
        }

        var root = List(path);

        CountsOf(root, "pasta").Should().Be((1, 1));
        root.Single(e => e.Name == "raiz.txt").SizeInBytes.Should().Be(1);
        List(path, "pasta").Single(e => e.Name == "a.txt").SizeInBytes.Should().Be(5000);
    }

    [Fact]
    public async Task Tar_gz_made_by_the_app_can_be_listed()
    {
        var source = Directory.CreateDirectory(Path.Combine(_directory, "origem")).FullName;
        var pasta = Directory.CreateDirectory(Path.Combine(source, "pasta")).FullName;
        File.WriteAllText(Path.Combine(source, "raiz.txt"), "r");
        File.WriteAllText(Path.Combine(pasta, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(pasta, "sub"));
        File.WriteAllText(Path.Combine(pasta, "sub", "c.txt"), "c");
        var target = Directory.CreateDirectory(Path.Combine(_directory, "destino")).FullName;

        var made = await new FileOperationService().TryCompressAsync([pasta, Path.Combine(source, "raiz.txt")], target, ArchiveFormat.TarGz, progress: null);
        made.Success.Should().BeTrue(made.ErrorMessage);

        var root = List(made.ArchivePath!);

        root.Select(e => e.Name).Should().BeEquivalentTo(["pasta", "raiz.txt"]);
        CountsOf(root, "pasta").Should().Be((1, 1));
    }

    [Fact]
    public void Zip_with_repeated_names_still_lists()
    {
        // Duas entradas com o mesmo nome e uma que só difere em maiúsculas: o zip é válido e deve abrir.
        var zip = CreateZip("dup/a.txt", "dup/a.txt", "DUP/A.TXT", "outra.txt");

        var root = List(zip);

        root.Select(e => e.Name).Should().BeEquivalentTo(["dup", "outra.txt"]);
        CountsOf(root, "dup").Should().Be((0, 1));
        root.Single(e => e.Name == "outra.txt").SizeInBytes.Should().Be(1);
    }

    [Fact]
    public void Archive_node_gives_its_folders_the_expander_arrow_and_the_right_counts()
    {
        var zip = CreateZip("raiz.txt", "pasta/a.txt", "pasta/b.txt", "pasta/sub/c.txt", "vazia/");
        var settings = new AppSettingsService(Path.Combine(_directory, "settings.json"));
        var viewSettings = new ExplorerViewSettingsService(settings);
        using var index = new DirectoryIndexService(Path.Combine(_directory, "index.db"));
        var tree = new NavigationTreeService(index, viewSettings, new ArchiveService());
        var files = new LocalFileSystemService(viewSettings, index);
        var archiveNode = new NavigationTreeNodeViewModel("teste.zip", NavigationNodeKind.Archive, zip);

        tree.LoadDirectoryChildren(archiveNode, files, new NetworkSessionStore(), new NetworkFileService(), TreeSortOptions.Default);

        var pasta = archiveNode.Children.Single(c => c.Name == "pasta");
        pasta.FolderCount.Should().Be(1);
        pasta.FileCount.Should().Be(2);
        pasta.Children.Should().ContainSingle().Which.IsPlaceholder.Should().BeTrue("é o marcador que faz a seta aparecer");

        var vazia = archiveNode.Children.Single(c => c.Name == "vazia");
        vazia.FolderCount.Should().Be(0);
        vazia.FileCount.Should().Be(0);
        vazia.Children.Should().BeEmpty("pasta vazia não tem seta");

        // Ao expandir, o conteúdo real entra e a subpasta também já nasce com a seta.
        tree.LoadDirectoryChildren(pasta, files, new NetworkSessionStore(), new NetworkFileService(), TreeSortOptions.Default);
        pasta.Children.Select(c => c.Name).Should().BeEquivalentTo(["sub", "a.txt", "b.txt"]);
        var sub = pasta.Children.Single(c => c.Name == "sub");
        sub.FileCount.Should().Be(1);
        sub.Children.Should().ContainSingle().Which.IsPlaceholder.Should().BeTrue();
    }

    // Arquivo .7z de 240 bytes criado com o 7-Zip: pasta/{a.txt (aaaa), b.txt (bb), sub/c.txt (c)}, vazia/ e raiz.txt (r).
    private const string SevenZipFixture =
        "N3q8ryccAATZw+f3rgAAAAAAAAAiAAAAAAAAAF5A708BAAdhYWFhYmJjcgAAAIEzB64PzpwGxQkqrtOqhSYoExi7A1moui1f8OgF9fU6vjAnkGBXRmEKoAdgk2yygcjcr08WILarZgtXSHKBPYRMUOA6uALxuxJVho4kIaUKV1DjDqzfwyPB85XcDTojl5j0VpBODh6XemhmJUGJ6MwpbJ8gwI3/fBuWx8+Vl1BOxDHnbqgrZdaMY8qOgt96FxAEJSRS0PEUlZONbAE0OyIXBgwBCYCiAAcLAQABIwMBAQVdABAAAAyBMgoBVzIATAAA";

    private string CreateSevenZip()
    {
        var path = Path.Combine(_directory, "teste.7z");
        File.WriteAllBytes(path, Convert.FromBase64String(SevenZipFixture));
        return path;
    }

    private string CreateGzip(string fileName, string content)
    {
        var path = Path.Combine(_directory, fileName);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(System.Text.Encoding.UTF8.GetBytes(content));
        return path;
    }

    private string ExtractTo(string archive, string entry)
    {
        var destination = Directory.CreateDirectory(Path.Combine(_directory, "saida-" + Guid.NewGuid().ToString("N"))).FullName;
        new ArchiveService().TryExtractEntry(archive, entry, destination, out var error).Should().BeTrue(error);
        return destination;
    }

    [Theory]
    [InlineData("a.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("a.tgz", ArchiveFormat.TarGz)]
    [InlineData("a.tar", ArchiveFormat.Tar)]
    [InlineData("a.gz", ArchiveFormat.Gzip)]
    [InlineData("A.GZ", ArchiveFormat.Gzip)]
    [InlineData("a.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.zip", ArchiveFormat.Zip)]
    [InlineData("a.rar", ArchiveFormat.Rar)]
    public void The_format_comes_from_the_name_and_tar_gz_is_not_a_plain_gz(string name, ArchiveFormat expected)
    {
        ArchivePathHelper.TryGetFormat(name, out var format).Should().BeTrue();
        format.Should().Be(expected);
    }

    [Fact]
    public void Gz_and_7z_are_read_only_like_rar()
    {
        ArchivePathHelper.SupportsRewrite(ArchiveFormat.Gzip).Should().BeFalse();
        ArchivePathHelper.SupportsRewrite(ArchiveFormat.SevenZip).Should().BeFalse();
        ArchivePathHelper.SupportsRewrite(ArchiveFormat.Zip).Should().BeTrue();
    }

    [Fact]
    public void SevenZip_folders_report_what_they_hold_directly()
    {
        var root = List(CreateSevenZip());

        root.Select(e => e.Name).Should().BeEquivalentTo(["pasta", "vazia", "raiz.txt"]);
        CountsOf(root, "pasta").Should().Be((1, 2));
        CountsOf(root, "vazia").Should().Be((0, 0));
        root.Single(e => e.Name == "raiz.txt").SizeInBytes.Should().Be(1);
    }

    [Fact]
    public void SevenZip_inner_folder_can_be_listed()
    {
        var inside = List(CreateSevenZip(), "pasta");

        inside.Select(e => e.Name).Should().BeEquivalentTo(["sub", "a.txt", "b.txt"]);
        inside.Single(e => e.Name == "a.txt").SizeInBytes.Should().Be(4);
    }

    [Fact]
    public void SevenZip_files_and_folders_can_be_extracted()
    {
        var zip = CreateSevenZip();

        File.ReadAllText(Path.Combine(ExtractTo(zip, "pasta/a.txt"), "a.txt")).Should().Be("aaaa");

        var folder = ExtractTo(zip, "pasta");
        File.ReadAllText(Path.Combine(folder, "pasta", "b.txt")).Should().Be("bb");
        File.ReadAllText(Path.Combine(folder, "pasta", "sub", "c.txt")).Should().Be("c");
    }

    [Theory]
    [InlineData("7z")]
    [InlineData("gz")]
    public void Entries_cannot_be_removed_from_read_only_archives(string kind)
    {
        var path = kind == "7z" ? CreateSevenZip() : CreateGzip("dados.txt.gz", "abc");
        var entry = kind == "7z" ? "raiz.txt" : "dados.txt";

        new ArchiveService().TryRemoveEntries(path, [entry], out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Gzip_opens_as_a_folder_with_the_one_file_inside()
    {
        var path = CreateGzip("dados.txt.gz", "conteudo de teste");

        var root = List(path);

        var entry = root.Should().ContainSingle().Which;
        entry.Name.Should().Be("dados.txt");
        entry.IsDirectory.Should().BeFalse();
        entry.SizeInBytes.Should().Be(17);
        List(path, "pasta").Should().BeEmpty("um .gz só tem o arquivo da raiz");
    }

    [Theory]
    [InlineData("relatorio.sql.gz", "relatorio.sql")]
    [InlineData("LOG.GZ", "LOG")]
    [InlineData("a.b.c.gz", "a.b.c")]
    public void Gzip_entry_name_drops_only_the_last_extension(string archiveName, string expected)
    {
        var root = List(CreateGzip(archiveName, "x"));

        root.Should().ContainSingle().Which.Name.Should().Be(expected);
    }

    [Fact]
    public void Gzip_file_can_be_extracted_with_its_original_content()
    {
        var path = CreateGzip("dados.txt.gz", "conteudo de teste");

        var destination = ExtractTo(path, "dados.txt");

        File.ReadAllText(Path.Combine(destination, "dados.txt")).Should().Be("conteudo de teste");
    }

    [Fact]
    public void Gzip_extraction_does_not_overwrite_an_existing_file()
    {
        var path = CreateGzip("dados.txt.gz", "novo");
        var destination = Directory.CreateDirectory(Path.Combine(_directory, "ja-existe")).FullName;
        File.WriteAllText(Path.Combine(destination, "dados.txt"), "antigo");

        new ArchiveService().TryExtractEntry(path, "dados.txt", destination, out var error).Should().BeTrue(error);

        File.ReadAllText(Path.Combine(destination, "dados.txt")).Should().Be("antigo");
        File.ReadAllText(Path.Combine(destination, "dados (1).txt")).Should().Be("novo");
    }

    [Fact]
    public void A_gz_that_is_not_gzip_is_reported_instead_of_listed()
    {
        var path = Path.Combine(_directory, "falso.gz");
        File.WriteAllText(path, "isto nao e gzip, mas e comprido o bastante para passar do tamanho minimo");
        var service = new ArchiveService();

        service.TryListEntries(path, string.Empty, out var entries, out var error).Should().BeFalse();
        entries.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();

        service.TryExtractEntry(path, "falso", _directory, out var extractError).Should().BeFalse();
        extractError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_gz_shorter_than_its_own_header_does_not_crash_the_listing()
    {
        var path = Path.Combine(_directory, "curto.gz");
        File.WriteAllBytes(path, [0x1F]);

        new ArchiveService().TryListEntries(path, string.Empty, out _, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Folders_of_a_7z_node_get_the_arrow_and_gz_and_7z_files_become_archive_nodes()
    {
        var sevenZip = CreateSevenZip();
        CreateGzip("dados.txt.gz", "x");
        var settings = new AppSettingsService(Path.Combine(_directory, "settings.json"));
        var viewSettings = new ExplorerViewSettingsService(settings);
        using var index = new DirectoryIndexService(Path.Combine(_directory, "index.db"));
        var tree = new NavigationTreeService(index, viewSettings, new ArchiveService());
        var files = new LocalFileSystemService(viewSettings, index);
        var folder = new NavigationTreeNodeViewModel("tmp", NavigationNodeKind.Directory, _directory);

        tree.LoadDirectoryChildren(folder, files, new NetworkSessionStore(), new NetworkFileService(), TreeSortOptions.Default);

        folder.Children.Single(c => c.Name == "teste.7z").Kind.Should().Be(NavigationNodeKind.Archive);
        folder.Children.Single(c => c.Name == "dados.txt.gz").Kind.Should().Be(NavigationNodeKind.Archive);

        var archiveNode = new NavigationTreeNodeViewModel("teste.7z", NavigationNodeKind.Archive, sevenZip);
        tree.LoadDirectoryChildren(archiveNode, files, new NetworkSessionStore(), new NetworkFileService(), TreeSortOptions.Default);
        var pasta = archiveNode.Children.Single(c => c.Name == "pasta");
        pasta.FolderCount.Should().Be(1);
        pasta.FileCount.Should().Be(2);
        pasta.Children.Should().ContainSingle().Which.IsPlaceholder.Should().BeTrue();
        archiveNode.Children.Single(c => c.Name == "vazia").Children.Should().BeEmpty();
    }
}
