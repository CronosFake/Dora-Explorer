using System.Diagnostics;
using System.Security.Cryptography;
using DoraExplorer.App.Infrastructure;
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

    // ---- leitura enxuta: um OpenHandle por arquivo, buffer emprestado, hash de uma vez nos pequenos e em blocos nos grandes

    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(1_048_575)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(3_145_733)]
    public void Deep_confirms_identical_files_of_every_size_around_the_read_block_boundaries(int size)
    {
        var content = Bytes(size, size);
        var a = Put(Dir("a"), "um.bin", content);
        var b = Put(Dir("b"), "dois.bin", content);

        var group = Scan(DuplicateMode.Deep).Groups.Should().ContainSingle().Subject;

        group.Files.Select(file => file.FullPath).Should().Equal(a, b);
        group.Key.Should().Be(Convert.ToHexString(SHA256.HashData(content)), "a chave é o SHA-256 do arquivo INTEIRO, em qualquer tamanho");
    }

    [Theory]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(3_145_733)]
    public void Deep_tells_apart_files_that_differ_only_in_the_last_byte_at_every_size(int size)
    {
        var first = Bytes(size, 3);
        var second = (byte[])first.Clone();
        second[^1] ^= 0x01;
        Put(Dir("a"), "um.bin", first);
        Put(Dir("b"), "dois.bin", second);

        Scan(DuplicateMode.Deep).Groups.Should().BeEmpty();
    }

    [Fact]
    public void A_file_that_only_exists_in_the_cloud_is_not_read_by_the_deep_search_but_still_seen_by_the_standard_one()
    {
        var content = Bytes(5000, 8);
        var local = Put(Dir("a"), "foto.bin", content);
        var cloud = Put(Dir("b"), "foto.bin", content);
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);

        var deep = Scan(DuplicateMode.Deep);
        var standard = Scan(DuplicateMode.Standard);

        deep.Unreadable.Should().Be(1, "ler o arquivo da nuvem o baixaria");
        deep.Groups.Should().BeEmpty();
        Paths(standard).Should().Equal(Group(local, cloud));
    }

    [Fact]
    public void The_listing_carries_the_attributes_so_no_second_lookup_is_needed()
    {
        var path = Put(Dir("a"), "so-leitura.txt", "12345");
        Put(Dir("b"), "so-leitura.txt", "12345");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var file = Scan(DuplicateMode.Standard).Groups.Single().Files.Single(f => f.FullPath == path);

            file.Attributes.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ---- leituras em paralelo

    [Theory]
    [InlineData(false, 16, 16)]
    [InlineData(false, 8, 8)]
    [InlineData(false, 4, 4)]
    [InlineData(false, 2, 4)]
    [InlineData(false, 64, 16)]
    [InlineData(true, 16, 4)]
    [InlineData(true, 64, 4)]
    [InlineData(null, 16, 4)]
    public void An_ssd_gets_one_read_per_processor_up_to_sixteen_and_a_hard_disk_or_an_unknown_disk_keeps_four(bool? seekPenalty, int processors, int expected)
    {
        DuplicateWorkers.Choose(seekPenalty, processors).Should().Be(expected);
    }

    [Fact]
    public void Asking_the_disk_kind_never_throws_and_gives_up_on_paths_it_cannot_ask_about()
    {
        var local = () => StorageKind.HasSeekPenalty(_root);
        var invalid = () => StorageKind.HasSeekPenalty("::nao e um caminho::");

        local.Should().NotThrow();
        invalid.Should().NotThrow();
        StorageKind.HasSeekPenalty(@"\\servidor-que-nao-existe\compartilhamento\pasta").Should().BeNull("caminho de rede: não dá para saber");
        StorageKind.HasSeekPenalty("").Should().BeNull();
    }

    [Fact]
    public void Many_reads_at_once_give_the_same_groups_as_one_at_a_time()
    {
        var contents = new[] { Bytes(70_000, 1), Bytes(70_000, 2), Bytes(3_000, 3) };
        for (var i = 0; i < 240; i++)
        {
            Put(Dir("pasta" + (i % 12)), $"arquivo{i}.bin", contents[i % 3]);
        }

        var serial = new DuplicateFinderService(maxParallelism: 1).Find(new DuplicateScanRequest(_root, DuplicateMode.Deep));
        var wide = new DuplicateFinderService(maxParallelism: 16).Find(new DuplicateScanRequest(_root, DuplicateMode.Deep));
        var automatic = new DuplicateFinderService().Find(new DuplicateScanRequest(_root, DuplicateMode.Deep));

        serial.Groups.Should().HaveCount(3);
        Paths(wide).Should().Equal(Paths(serial));
        Paths(automatic).Should().Equal(Paths(serial));
        wide.FilesRead.Should().Be(serial.FilesRead);
    }

    // ---- pastas do sistema na raiz de uma unidade

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"D:\Arquivos\", false)]
    [InlineData(@"\\servidor\pasta", true)]
    [InlineData(@"\\servidor\pasta\subpasta", false)]
    public void Only_the_root_of_a_drive_or_of_a_network_share_counts_as_a_drive_root(string path, bool expected)
    {
        DuplicateFinderService.IsDriveRoot(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Windows", FileAttributes.Directory, true)]
    [InlineData("windows", FileAttributes.Directory, true)]
    [InlineData("Program Files", FileAttributes.Directory, true)]
    [InlineData("Program Files (x86)", FileAttributes.Directory, true)]
    [InlineData("ProgramData", FileAttributes.Directory, true)]
    [InlineData("$Recycle.Bin", FileAttributes.Directory, true)]
    [InlineData("Qualquer", FileAttributes.Directory | FileAttributes.System | FileAttributes.Hidden, true)]
    [InlineData("Users", FileAttributes.Directory, false)]
    [InlineData("Arquivos", FileAttributes.Directory, false)]
    [InlineData("Windows Notes", FileAttributes.Directory, false)]
    public void The_system_folders_at_the_top_of_a_drive_are_recognised_by_name_or_by_the_system_attribute(string name, FileAttributes attributes, bool expected)
    {
        DuplicateFinderService.IsSystemFolder(name, attributes).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\", false, true)]
    [InlineData(@"D:\", false, true)]
    [InlineData(@"C:\", true, false)]
    [InlineData(@"C:\Users", false, false)]
    [InlineData(@"C:\Windows", false, false)]
    [InlineData("", false, false)]
    public void The_system_folders_are_left_out_only_at_a_drive_root_and_only_when_not_asked_for(string root, bool includeSystem, bool expected)
    {
        DuplicateFinderService.SkipsSystemFolders(new DuplicateScanRequest(root, DuplicateMode.Deep, true, includeSystem)).Should().Be(expected);
    }

    private (string Documents, string Windows, string ProgramFiles, string Hidden) RootWithSystemFolders()
    {
        var content = Bytes(2_000, 21);
        var documents = Dir("Documentos");
        Put(documents, "a.bin", content);
        Put(Dir("Documentos", "Windows"), "b.bin", content); // uma pasta chamada Windows DENTRO de outra não é a do sistema
        var windows = Dir("Windows");
        Put(windows, "sistema1.bin", content);
        Put(Dir("Program Files", "app"), "sistema2.bin", content);
        var hidden = Dir("PastaOcultaDoSistema");
        Put(hidden, "sistema3.bin", content);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.System | FileAttributes.Hidden);
        return (documents, windows, Path.Combine(_root, "Program Files"), hidden);
    }

    [Fact]
    public void At_a_drive_root_the_system_folders_are_left_out_and_named_in_the_result()
    {
        var (documents, windows, programFiles, hidden) = RootWithSystemFolders();
        try
        {
            var result = _finder.Find(
                new DuplicateScanRequest(_root, DuplicateMode.Deep),
                DuplicateFinderService.IsSystemFolder,
                null,
                CancellationToken.None);

            var paths = result.Groups.SelectMany(group => group.Files).Select(file => file.FullPath).ToList();
            paths.Should().Contain(p => p.StartsWith(documents, StringComparison.OrdinalIgnoreCase));
            paths.Should().NotContain(p => p.StartsWith(windows, StringComparison.OrdinalIgnoreCase));
            paths.Should().NotContain(p => p.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase));
            paths.Should().NotContain(p => p.StartsWith(hidden, StringComparison.OrdinalIgnoreCase));
            result.SkippedSystemFolders.Should().Equal("PastaOcultaDoSistema", "Program Files", "Windows");
            result.FilesScanned.Should().Be(2, "só os dois de Documentos (um deles na subpasta Windows, que não é a do sistema)");
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Directory);
        }
    }

    [Fact]
    public void Including_the_system_folders_brings_everything_back_and_reports_nothing_skipped()
    {
        var (_, _, _, hidden) = RootWithSystemFolders();
        try
        {
            var result = _finder.Find(new DuplicateScanRequest(_root, DuplicateMode.Deep), null, null, CancellationToken.None);

            result.FilesScanned.Should().Be(5);
            result.Groups.Should().ContainSingle().Which.Files.Should().HaveCount(5);
            result.SkippedSystemFolders.Should().BeEmpty();
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Directory);
        }
    }

    [Fact]
    public void A_folder_called_windows_is_searched_normally_when_the_search_does_not_start_at_a_drive_root()
    {
        var content = Bytes(1_500, 5);
        Put(Dir("Windows"), "a.bin", content);
        Put(Dir("Outra"), "b.bin", content);

        // Find público: a pasta de teste não é a raiz de uma unidade, então nada fica de fora, mesmo com o nome Windows.
        var result = Scan(DuplicateMode.Deep);

        result.Groups.Should().ContainSingle();
        result.SkippedSystemFolders.Should().BeEmpty();
    }

    [Fact]
    public void Without_subfolders_nothing_is_reported_as_skipped_because_nothing_would_have_been_entered()
    {
        RootWithSystemFolders();
        var hidden = Path.Combine(_root, "PastaOcultaDoSistema");
        try
        {
            var result = _finder.Find(
                new DuplicateScanRequest(_root, DuplicateMode.Deep, IncludeSubfolders: false),
                DuplicateFinderService.IsSystemFolder,
                null,
                CancellationToken.None);

            result.SkippedSystemFolders.Should().BeEmpty();
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Directory);
        }
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
