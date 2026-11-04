using System.Diagnostics;
using System.Security.Cryptography;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Procura de arquivos duplicados: o modo padrão (mesmo nome e mesmo tamanho, sem ler o conteúdo) e o profundo (mesmo tamanho e mesmo
/// SHA-256). Cada teste monta uma pasta temporária de verdade; nada toca nas pastas do usuário.
/// </summary>
public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "dora-dup-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly DuplicateFinderService _finder = new(maxParallelism: 2);

    public void Dispose() => SshTestHelpers.DeleteDirectory(_root);

    private string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static string Put(string folder, string name, byte[] content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Put(string folder, string name, string text) => Put(folder, name, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Bytes repetíveis: a mesma semente dá o mesmo conteúdo, outra semente dá outro.</summary>
    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private DuplicateScanResult Scan(DuplicateMode mode, bool subfolders = true, string? root = null) =>
        _finder.Find(new DuplicateScanRequest(root ?? _root, mode, subfolders));

    /// <summary>Um texto por grupo, com os caminhos na ordem: facilita comparar grupos inteiros.</summary>
    private static string[] Paths(DuplicateScanResult result) =>
        result.Groups.Select(group => Group(group.Files.Select(file => file.FullPath).ToArray())).ToArray();

    private static string Group(params string[] paths) => string.Join(" | ", paths);

    // ---- padrão: nome e tamanho

    [Fact]
    public void Standard_groups_files_with_the_same_name_and_size_in_different_folders()
    {
        var a = Put(Dir("a"), "foto.jpg", "12345");
        var b = Put(Dir("b"), "foto.jpg", "abcde");

        var result = Scan(DuplicateMode.Standard);

        Paths(result).Should().Equal(Group(a, b)).And.HaveCount(1, "o modo padrão só olha nome e tamanho, não o conteúdo");
        result.FilesScanned.Should().Be(2);
        result.FilesRead.Should().Be(0, "o modo padrão não lê arquivo nenhum");
    }

    [Fact]
    public void Standard_ignores_the_same_name_with_a_different_size()
    {
        Put(Dir("a"), "foto.jpg", "12345");
        Put(Dir("b"), "foto.jpg", "123456");

        Scan(DuplicateMode.Standard).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Standard_ignores_the_same_size_under_a_different_name()
    {
        Put(Dir("a"), "um.txt", "12345");
        Put(Dir("b"), "dois.txt", "12345");

        Scan(DuplicateMode.Standard).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Standard_does_not_tell_capitals_apart_in_the_name()
    {
        var a = Put(Dir("a"), "RELATORIO.PDF", "12345");
        var b = Put(Dir("b"), "relatorio.pdf", "12345");

        Paths(Scan(DuplicateMode.Standard)).Should().Equal(Group(a, b));
    }

    [Fact]
    public void Standard_reports_three_copies_as_one_group_and_the_wasted_space_of_two()
    {
        var a = Put(Dir("a"), "x.bin", Bytes(1000, 1));
        var b = Put(Dir("b"), "x.bin", Bytes(1000, 1));
        var c = Put(Dir("c"), "x.bin", Bytes(1000, 1));

        var result = Scan(DuplicateMode.Standard);

        var group = result.Groups.Should().ContainSingle().Subject;
        group.Files.Select(file => file.FullPath).Should().Equal(a, b, c);
        group.SizeInBytes.Should().Be(1000);
        group.WastedBytes.Should().Be(2000, "guardando uma cópia só, sobram duas");
        result.TotalWastedBytes.Should().Be(2000);
        result.TotalDuplicateFiles.Should().Be(3);
    }

    [Fact]
    public void Empty_files_are_never_duplicates_of_each_other()
    {
        Put(Dir("a"), "vazio.txt", Array.Empty<byte>());
        Put(Dir("b"), "vazio.txt", Array.Empty<byte>());

        var standard = Scan(DuplicateMode.Standard);
        var deep = Scan(DuplicateMode.Deep);

        standard.Groups.Should().BeEmpty();
        standard.FilesScanned.Should().Be(0, "arquivo vazio não conta");
        deep.Groups.Should().BeEmpty();
    }

    [Fact]
    public void The_groups_that_waste_the_most_come_first()
    {
        Put(Dir("a"), "pequeno.txt", Bytes(10, 1));
        Put(Dir("b"), "pequeno.txt", Bytes(10, 1));
        Put(Dir("a"), "grande.bin", Bytes(5000, 2));
        Put(Dir("b"), "grande.bin", Bytes(5000, 2));
        Put(Dir("c"), "grande.bin", Bytes(5000, 2));

        var names = Scan(DuplicateMode.Standard).Groups.Select(group => group.Files[0].Name);

        names.Should().Equal("grande.bin", "pequeno.txt");
    }

    [Fact]
    public void Without_subfolders_only_the_chosen_folder_is_searched()
    {
        var top1 = Put(Dir(), "t.txt", "12345");
        Put(Dir("fundo"), "t.txt", "12345");
        Put(Dir(), "u.txt", "ab");

        Scan(DuplicateMode.Standard, subfolders: false).Groups.Should().BeEmpty("a outra cópia está numa subpasta");
        Paths(Scan(DuplicateMode.Standard, subfolders: true)).Should().HaveCount(1);
        File.Exists(top1).Should().BeTrue();
    }

    [Fact]
    public void A_folder_that_does_not_exist_gives_an_empty_result_instead_of_failing()
    {
        var result = Scan(DuplicateMode.Deep, root: Path.Combine(_root, "nao-existe"));

        result.Groups.Should().BeEmpty();
        result.FilesScanned.Should().Be(0);
    }

    [Fact]
    public void A_blank_folder_gives_an_empty_result()
    {
        _finder.Find(new DuplicateScanRequest("  ", DuplicateMode.Standard)).Groups.Should().BeEmpty();
    }

    // ---- profundo: tamanho e hash

    [Fact]
    public void Deep_finds_the_same_content_under_different_names_and_reports_the_sha256()
    {
        var content = Bytes(2000, 7);
        var a = Put(Dir("a"), "original.dat", content);
        var b = Put(Dir("b"), "copia com outro nome.dat", content);

        var result = Scan(DuplicateMode.Deep);

        var group = result.Groups.Should().ContainSingle().Subject;
        group.Files.Select(file => file.FullPath).Should().Equal(a, b);
        group.Key.Should().Be(Convert.ToHexString(SHA256.HashData(content)), "a chave do grupo é o SHA-256 do conteúdo");
        group.SizeInBytes.Should().Be(2000);
    }

    [Fact]
    public void Deep_separates_files_that_have_the_same_name_and_size_but_different_content()
    {
        Put(Dir("a"), "dados.bin", Bytes(2000, 1));
        Put(Dir("b"), "dados.bin", Bytes(2000, 2));

        Scan(DuplicateMode.Standard).Groups.Should().HaveCount(1, "o padrão se engana: nome e tamanho iguais");
        Scan(DuplicateMode.Deep).Groups.Should().BeEmpty("o profundo compara o conteúdo");
    }

    [Fact]
    public void Deep_catches_a_difference_in_the_very_last_byte_of_a_large_file()
    {
        // O começo dos dois é igual: só o arquivo inteiro os distingue (o que prova que o hash final é feito).
        var size = DuplicateFinderService.PrefixBytes * 3;
        var first = Bytes(size, 5);
        var second = (byte[])first.Clone();
        second[^1] ^= 0xFF;
        Put(Dir("a"), "grande1.bin", first);
        Put(Dir("b"), "grande2.bin", second);

        var result = Scan(DuplicateMode.Deep);

        result.Groups.Should().BeEmpty();
        result.FilesRead.Should().Be(2, "os dois têm o tamanho um do outro e foram lidos");
    }

    [Fact]
    public void Deep_catches_a_difference_in_the_first_byte_without_reading_the_whole_file()
    {
        var size = DuplicateFinderService.PrefixBytes * 3;
        var first = Bytes(size, 6);
        var second = (byte[])first.Clone();
        second[0] ^= 0xFF;
        Put(Dir("a"), "g1.bin", first);
        Put(Dir("b"), "g2.bin", second);

        Scan(DuplicateMode.Deep).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Deep_confirms_a_large_file_that_is_really_identical()
    {
        var content = Bytes(DuplicateFinderService.PrefixBytes * 4 + 123, 9);
        var a = Put(Dir("a"), "um.bin", content);
        var b = Put(Dir("b"), "dois.bin", content);

        var group = Scan(DuplicateMode.Deep).Groups.Should().ContainSingle().Subject;

        group.Files.Select(file => file.FullPath).Should().Equal(a, b);
        group.Key.Should().Be(Convert.ToHexString(SHA256.HashData(content)));
    }

    [Fact]
    public void Deep_never_reads_a_file_whose_size_nobody_else_has()
    {
        // Um arquivo de tamanho único travado por outro programa: se o profundo o lesse, contaria como ilegível.
        var lonely = Put(Dir("a"), "sozinho.bin", Bytes(777, 1));
        Put(Dir("a"), "par1.bin", Bytes(500, 2));
        Put(Dir("b"), "par2.bin", Bytes(500, 2));

        using var locked = new FileStream(lonely, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Scan(DuplicateMode.Deep);

        result.Unreadable.Should().Be(0, "só se lê o que tem o tamanho de outro arquivo");
        result.FilesRead.Should().Be(2);
        result.Groups.Should().ContainSingle();
    }

    [Fact]
    public void Deep_counts_a_locked_candidate_as_unreadable_and_still_finds_the_other_duplicates()
    {
        var content = Bytes(900, 3);
        var locked = Put(Dir("a"), "travado.bin", content);
        var free1 = Put(Dir("b"), "livre1.bin", content);
        var free2 = Put(Dir("c"), "livre2.bin", content);

        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Scan(DuplicateMode.Deep);

        result.Unreadable.Should().Be(1);
        Paths(result).Should().Equal(Group(free1, free2));
    }

    [Fact]
    public void Deep_does_not_group_files_of_different_sizes_even_if_one_is_the_start_of_the_other()
    {
        var content = Bytes(3000, 4);
        Put(Dir("a"), "completo.bin", content);
        Put(Dir("b"), "cortado.bin", content[..2000]);

        Scan(DuplicateMode.Deep).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Deep_handles_many_candidates_in_one_group_and_several_groups_at_once()
    {
        var one = Bytes(DuplicateFinderService.PrefixBytes + 10, 11);
        var two = Bytes(DuplicateFinderService.PrefixBytes + 10, 12);
        for (var i = 0; i < 6; i++)
        {
            Put(Dir("g" + i), "um.bin", one);
            Put(Dir("g" + i), "dois.bin", two);
        }

        var result = Scan(DuplicateMode.Deep);

        result.Groups.Should().HaveCount(2);
        result.Groups.Should().OnlyContain(group => group.Files.Count == 6);
        result.TotalWastedBytes.Should().Be(2 * 5 * (DuplicateFinderService.PrefixBytes + 10));
    }

    // ---- cancelar, andamento, atalhos

    [Fact]
    public void A_cancelled_search_stops_with_an_operation_cancelled_exception()
    {
        Put(Dir("a"), "x.txt", "12345");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var act = () => _finder.Find(new DuplicateScanRequest(_root, DuplicateMode.Deep), null, cancel.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task The_async_search_runs_off_the_calling_thread_and_honours_cancellation()
    {
        Put(Dir("a"), "x.txt", "12345");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var act = async () => await _finder.FindAsync(new DuplicateScanRequest(_root, DuplicateMode.Standard), null, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_search_reports_listing_and_then_reading_progress_ending_with_everything_read()
    {
        var content = Bytes(300, 8);
        Put(Dir("a"), "p.bin", content);
        Put(Dir("b"), "q.bin", content);
        var reports = new List<DuplicateScanProgress>();
        var progress = new SyncProgress<DuplicateScanProgress>(reports.Add);

        await _finder.FindAsync(new DuplicateScanRequest(_root, DuplicateMode.Deep), progress);

        reports.Should().NotBeEmpty();
        reports.First().Phase.Should().Be(DuplicateScanPhase.Listing);
        var last = reports.Last();
        last.Phase.Should().Be(DuplicateScanPhase.Reading);
        last.FilesToRead.Should().Be(2);
        last.FilesRead.Should().Be(2);
        last.FilesScanned.Should().Be(2);
    }

    [Fact]
    public void A_junction_to_another_folder_is_not_followed_so_nothing_is_counted_twice()
    {
        var real = Dir("real");
        Put(real, "a.txt", "12345");
        var link = Path.Combine(_root, "atalho");
        if (!TryCreateJunction(link, real))
        {
            return; // sem junção neste ambiente: nada a conferir
        }

        var result = Scan(DuplicateMode.Standard);

        result.FilesScanned.Should().Be(1, "a junção aponta para a mesma pasta e não deve ser percorrida");
        result.Groups.Should().BeEmpty();
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process!.WaitForExit(10_000);
            return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public void The_modified_date_and_folder_of_each_copy_are_reported_for_the_list()
    {
        var folder = Dir("onde");
        Put(folder, "d.txt", "12345");
        Put(Dir("outra"), "d.txt", "12345");

        var file = Scan(DuplicateMode.Standard).Groups.Single().Files.First(f => f.Folder == folder);

        file.LastModified.Should().NotBeNull();
        file.Name.Should().Be("d.txt");
        file.SizeInBytes.Should().Be(5);
    }

    /// <summary>Como <see cref="Progress{T}"/>, mas chama na hora e na mesma thread (o de verdade posta na thread que o criou).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                handler(value);
            }
        }
    }
}
