using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Arquivos sólidos (RAR e 7z) guardam as entradas como um fluxo só: cada arquivo só sai certo se as entradas anteriores forem
/// lidas antes. Pedir uma entrada com o arquivo recém-aberto dava conteúdo errado no RAR.
/// </summary>
public sealed class SolidArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-solid-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => SshTestHelpers.DeleteDirectory(_directory);

    // Arquivo RAR5 sólido criado com o WinRAR (-m5 -s): f1.txt, f2.txt, f3.txt e pasta/f4.txt, f5.txt, f6.txt.
    private const string SolidRar =
        "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";

    // Arquivo 7z sólido criado com o 7-Zip (-mx=5 -ms=on) com os mesmos seis arquivos.
    private const string SolidSevenZip =
        "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" +
        "MgoBoeWgWAAA";

    private string CreateArchive(string kind)
    {
        var path = Path.Combine(_directory, kind == "rar" ? "solido.rar" : "solido.7z");
        File.WriteAllBytes(path, Convert.FromBase64String(kind == "rar" ? SolidRar : SolidSevenZip));
        return path;
    }

    /// <summary>Conteúdo do arquivo n, gerado por fórmula (a mesma usada para criar os arquivos de teste).</summary>
    private static string Expected(int n)
    {
        var lines = Enumerable.Range(0, 80).Select(i => $"{n}:{i}:" + new string('x', ((i * 7) + n) % 23) + ((i * i * n) % 9973));
        return string.Join("\n", lines) + "\n";
    }

    private static string EntryOf(int n) => n >= 4 ? $"pasta/f{n}.txt" : $"f{n}.txt";

    private string ExtractTo(string archive, string entry)
    {
        var destination = Directory.CreateDirectory(Path.Combine(_directory, "saida-" + Guid.NewGuid().ToString("N"))).FullName;
        new ArchiveService().TryExtractEntry(archive, entry, destination, out var error).Should().BeTrue(error);
        return destination;
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("7z")]
    public void Every_file_of_a_solid_archive_comes_out_with_its_own_content(string kind)
    {
        var archive = CreateArchive(kind);

        // Na ordem do arquivo e na ordem inversa: cada pedido abre o arquivo do zero.
        foreach (var n in Enumerable.Range(1, 6).Concat(Enumerable.Range(1, 6).Reverse()))
        {
            var destination = ExtractTo(archive, EntryOf(n));

            File.ReadAllText(Path.Combine(destination, $"f{n}.txt")).Should().Be(Expected(n), $"f{n}.txt");
        }
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("7z")]
    public void A_folder_of_a_solid_archive_is_extracted_whole_and_correct(string kind)
    {
        var destination = ExtractTo(CreateArchive(kind), "pasta");

        foreach (var n in new[] { 4, 5, 6 })
        {
            File.ReadAllText(Path.Combine(destination, "pasta", $"f{n}.txt")).Should().Be(Expected(n), $"f{n}.txt");
        }
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("7z")]
    public void A_missing_entry_of_a_solid_archive_is_reported(string kind)
    {
        var destination = Directory.CreateDirectory(Path.Combine(_directory, "vazio")).FullName;

        new ArchiveService().TryExtractEntry(CreateArchive(kind), "nao-existe.txt", destination, out var error)
            .Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        Directory.GetFileSystemEntries(destination).Should().BeEmpty();
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("7z")]
    public void A_solid_archive_lists_its_folders_with_counts(string kind)
    {
        new ArchiveService().TryListEntries(CreateArchive(kind), string.Empty, out var entries, out var error)
            .Should().BeTrue(error);

        entries.Select(e => e.Name).Should().BeEquivalentTo(["f1.txt", "f2.txt", "f3.txt", "pasta"]);
        var pasta = entries.Single(e => e.Name == "pasta");
        (pasta.ChildFolderCount, pasta.ChildFileCount).Should().Be((0, 3));
    }
}
