using System.Security.AccessControl;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O índice de pastas (SQLite) roda numa pasta temporária: nunca toca no index.db do usuário.
/// A gravação acontece em segundo plano, então os testes esperam a condição (com limite) em vez de dormir.
/// </summary>
public sealed class DirectoryIndexTests : IDisposable
{
    private static readonly ExplorerViewOptions ShowAll = new() { ShowHiddenFiles = true, ShowSystemFiles = true };

    private static readonly ExplorerViewOptions HideHiddenAndSystem = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dora-index-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _database;

    private readonly string _data;

    public DirectoryIndexTests()
    {
        _database = Path.Combine(_root, "banco", "index.db");
        _data = Path.Combine(_root, "dados");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        // O pool de conexões do SQLite mantém o arquivo aberto depois do Close; sem isto a pasta não apaga.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza de pasta temporária: não deve reprovar o teste.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private DirectoryIndexService NewIndex() => new(_database);

    private string CreateSampleFolder(string name = "amostra")
    {
        var folder = Path.Combine(_data, name);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "a.txt"), new byte[3]);
        File.WriteAllBytes(Path.Combine(folder, "b.log"), new byte[10]);
        return folder;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    private static void ReadAndWaitUntilStored(DirectoryIndexService index, string folder)
    {
        _ = index.ReadFromDiskAndQueueStore(folder, ShowAll);

        WaitFor(() => index.IsIndexedAndCurrent(folder))
            .Should().BeTrue("a gravação do índice roda em segundo plano");
    }

    [Fact]
    public void A_folder_the_user_cannot_list_is_reported_and_is_never_stored_as_empty()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder("trancada");
        using var deny = new AccessDenyScope();
        deny.Deny(folder, FileSystemRights.ListDirectory);

        index.ReadFromDiskAndQueueStore(folder, ShowAll).Should().BeEmpty();
        index.WasAccessDenied(folder).Should().BeTrue();

        // A gravação é uma fila: ler outra pasta depois e esperar ela entrar garante que o que veio antes já foi processado.
        ReadAndWaitUntilStored(index, CreateSampleFolder("marcador"));
        index.TryGetEntries(folder, ShowAll).Should().BeNull("vazia só por falta de permissão não pode virar \"vazia\" no cache");
        index.IsIndexedAndCurrent(folder).Should().BeFalse();

        deny.Dispose(); // devolve a permissão
        index.ReadFromDiskAndQueueStore(folder, ShowAll).Select(e => e.Name)
            .Should().BeEquivalentTo(["a.txt", "b.log", "sub"], "com permissão a pasta mostra o que tem");
        index.WasAccessDenied(folder).Should().BeFalse();
    }

    [Fact]
    public void Counting_a_folder_the_user_cannot_list_does_not_store_it_either()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder("trancada");
        using var deny = new AccessDenyScope();
        deny.Deny(folder, FileSystemRights.ListDirectory);

        index.ComputeCountsFromDisk(folder, ShowAll).Should().Be((0, 0));
        index.WasAccessDenied(folder).Should().BeTrue();

        ReadAndWaitUntilStored(index, CreateSampleFolder("marcador"));
        index.TryGetCounts(folder, ShowAll, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_listing_stored_empty_by_an_older_session_is_forgotten_on_startup()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_data, "vazia")).FullName;
        var full = CreateSampleFolder();
        using (var first = NewIndex())
        {
            ReadAndWaitUntilStored(first, empty);
            ReadAndWaitUntilStored(first, full);
            first.TryGetEntries(empty, ShowAll).Should().BeEmpty("a sessão antiga guardou a pasta como vazia");
        }

        using var second = NewIndex();

        second.TryGetEntries(empty, ShowAll).Should().BeNull("pode ter ficado vazia só por falta de permissão: é lida de novo");
        second.IsIndexedAndCurrent(empty).Should().BeFalse();
        second.TryGetEntries(full, ShowAll).Should().NotBeNull("o que tem conteúdo continua em cache");
    }

    [Fact]
    public void A_folder_that_was_never_read_is_not_in_the_index()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();

        index.TryGetEntries(folder, ShowAll).Should().BeNull();
        index.TryGetCounts(folder, ShowAll, out _, out _).Should().BeFalse();
        index.IsIndexedAndCurrent(folder).Should().BeFalse();
        index.TryGetSearchListing(folder, out _).Should().BeFalse();
    }

    [Fact]
    public void Reading_a_folder_stores_it_and_the_cached_listing_matches_the_disk()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();

        var fromDisk = index.ReadFromDiskAndQueueStore(folder, ShowAll);
        fromDisk.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);

        WaitFor(() => index.TryGetEntries(folder, ShowAll) is not null).Should().BeTrue();
        var cached = index.TryGetEntries(folder, ShowAll)!;

        cached.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);
        cached.Single(e => e.Name == "sub").IsDirectory.Should().BeTrue();
        cached.Single(e => e.Name == "a.txt").SizeInBytes.Should().Be(3);
        cached.Single(e => e.Name == "b.log").SizeInBytes.Should().Be(10);
        cached.Single(e => e.Name == "a.txt").FullPath.Should().Be(Path.Combine(folder, "a.txt"));
    }

    [Fact]
    public void Hidden_and_system_files_follow_the_view_options()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        File.WriteAllBytes(Path.Combine(folder, "oculto.txt"), new byte[1]);
        File.SetAttributes(Path.Combine(folder, "oculto.txt"), FileAttributes.Hidden);
        File.WriteAllBytes(Path.Combine(folder, "sistema.dat"), new byte[1]);
        File.SetAttributes(Path.Combine(folder, "sistema.dat"), FileAttributes.System);

        ReadAndWaitUntilStored(index, folder);

        index.TryGetEntries(folder, HideHiddenAndSystem)!.Select(e => e.Name)
            .Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);
        index.TryGetEntries(folder, ShowAll)!.Select(e => e.Name)
            .Should().BeEquivalentTo(["a.txt", "b.log", "sub", "oculto.txt", "sistema.dat"]);

        index.TryGetCounts(folder, HideHiddenAndSystem, out var folders, out var files).Should().BeTrue();
        (folders, files).Should().Be((1, 2));
        index.TryGetCounts(folder, ShowAll, out folders, out files).Should().BeTrue();
        (folders, files).Should().Be((1, 4));
    }

    [Fact]
    public void Counts_and_listing_are_served_from_the_index_even_after_the_disk_changes()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        ReadAndWaitUntilStored(index, folder);

        File.Delete(Path.Combine(folder, "a.txt"));

        index.TryGetCounts(folder, ShowAll, out var folders, out var files).Should().BeTrue();
        (folders, files).Should().Be((1, 2), "o índice só muda quando a pasta é lida de novo");
    }

    [Fact]
    public void The_index_is_current_until_the_folder_changes_and_becomes_current_again_after_a_new_read()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        ReadAndWaitUntilStored(index, folder);
        index.TryGetSearchListing(folder, out _).Should().BeTrue();

        // Muda a data da pasta de forma determinística (o NTFS atualiza a data da pasta de forma preguiçosa).
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddMinutes(5));

        index.IsIndexedAndCurrent(folder).Should().BeFalse();
        index.TryGetSearchListing(folder, out _).Should().BeFalse("a listagem de pesquisa só vale com o índice atualizado");

        ReadAndWaitUntilStored(index, folder);
        index.TryGetSearchListing(folder, out _).Should().BeTrue();
    }

    [Fact]
    public void The_search_listing_has_names_kinds_and_sizes()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        ReadAndWaitUntilStored(index, folder);

        index.TryGetSearchListing(folder, out var listing).Should().BeTrue();

        listing.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);
        listing.Single(e => e.Name == "sub").IsDirectory.Should().BeTrue();
        listing.Single(e => e.Name == "b.log").Size.Should().Be(10);
    }

    [Fact]
    public void Invalidate_forgets_the_folder()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        ReadAndWaitUntilStored(index, folder);

        index.Invalidate(folder);

        index.TryGetEntries(folder, ShowAll).Should().BeNull();
        index.IsIndexedAndCurrent(folder).Should().BeFalse();
        index.TryGetCounts(folder, ShowAll, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_write_still_waiting_in_the_queue_cannot_bring_back_a_folder_that_was_invalidated()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        var marker = CreateSampleFolder("marcador");

        // Pasta com bastante coisa dentro: gravar cada listagem custa mais do que ler a pasta, então a fila de gravação
        // fica atrasada. Quando o arquivo some e o índice é invalidado, ainda sobram pedidos antigos esperando, e eles não
        // podem recolocar a listagem de antes.
        for (var i = 0; i < 1500; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"arquivo-{i:D4}.txt"), []);
        }

        for (var i = 0; i < 150; i++)
        {
            _ = index.ReadFromDiskAndQueueStore(folder, ShowAll);
        }

        File.Delete(Path.Combine(folder, "a.txt"));
        index.Invalidate(folder);
        _ = index.ReadFromDiskAndQueueStore(marker, ShowAll); // entra depois de todos os pedidos antigos

        WaitFor(() => index.TryGetEntries(marker, ShowAll) is not null).Should().BeTrue("a fila inteira foi processada");

        index.TryGetEntries(folder, ShowAll).Should().BeNull("o que foi lido antes de invalidar não volta para o índice");
    }

    [Fact]
    public void A_read_made_after_invalidating_is_stored_as_usual()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        for (var i = 0; i < 100; i++)
        {
            _ = index.ReadFromDiskAndQueueStore(folder, ShowAll);
        }

        File.Delete(Path.Combine(folder, "a.txt"));
        index.Invalidate(folder);
        var fresh = index.ReadFromDiskAndQueueStore(folder, ShowAll);

        fresh.Select(e => e.Name).Should().BeEquivalentTo(["b.log", "sub"]);
        WaitFor(() => index.TryGetEntries(folder, ShowAll) is not null).Should().BeTrue();
        index.TryGetEntries(folder, ShowAll)!.Select(e => e.Name).Should().BeEquivalentTo(["b.log", "sub"],
            "a leitura nova, feita depois de invalidar, é a que fica no índice");
    }

    [Fact]
    public void Computing_counts_from_the_disk_returns_them_and_indexes_the_folder()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();

        var counts = index.ComputeCountsFromDisk(folder, ShowAll);

        counts.Should().Be((1, 2));
        WaitFor(() => index.TryGetCounts(folder, ShowAll, out _, out _)).Should().BeTrue();
    }

    [Fact]
    public void The_index_survives_closing_and_reopening_the_database()
    {
        var folder = CreateSampleFolder();
        using (var first = NewIndex())
        {
            ReadAndWaitUntilStored(first, folder);
        }

        using var second = NewIndex();

        second.TryGetEntries(folder, ShowAll)!.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);
        second.IsIndexedAndCurrent(folder).Should().BeTrue();
    }

    [Fact]
    public void Folder_paths_match_regardless_of_case_and_trailing_separator()
    {
        using var index = NewIndex();
        var folder = CreateSampleFolder();
        ReadAndWaitUntilStored(index, folder);

        index.TryGetEntries(folder.ToUpperInvariant() + Path.DirectorySeparatorChar, ShowAll).Should().NotBeNull();
    }

    [Fact]
    public void The_database_is_created_in_wal_mode_with_the_expected_schema_version()
    {
        using (var index = NewIndex())
        {
            ReadAndWaitUntilStored(index, CreateSampleFolder());
        }

        using var connection = new SqliteConnection($"Data Source={_database};Pooling=False");
        connection.Open();

        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        ((string)mode.ExecuteScalar()!).Should().Be("wal");

        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
    }

    [Fact]
    public void Maintenance_purges_folders_not_indexed_for_more_than_ninety_days_and_keeps_recent_ones()
    {
        using var index = NewIndex();
        var oldFolder = CreateSampleFolder("antiga");
        var recentFolder = CreateSampleFolder("recente");
        ReadAndWaitUntilStored(index, oldFolder);
        ReadAndWaitUntilStored(index, recentFolder);

        using (var connection = new SqliteConnection($"Data Source={_database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE directories SET last_indexed = $old WHERE path = $path;";
            command.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-100).Ticks);
            command.Parameters.AddWithValue("$path", Path.GetFullPath(oldFolder).ToLowerInvariant());
            command.ExecuteNonQuery().Should().Be(1);
        }

        index.ScheduleMaintenance();

        WaitFor(() => index.TryGetEntries(oldFolder, ShowAll) is null).Should().BeTrue();
        index.TryGetEntries(recentFolder, ShowAll).Should().NotBeNull();
    }

    [Fact]
    public void A_large_folder_is_stored_and_read_back_completely()
    {
        using var index = NewIndex();
        var folder = Path.Combine(_data, "grande");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 2000; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"arquivo_{i:D4}.txt"), [1]);
        }

        _ = index.ReadFromDiskAndQueueStore(folder, ShowAll);

        WaitFor(() => index.TryGetEntries(folder, ShowAll)?.Count == 2000).Should().BeTrue();
    }

    [Fact]
    public void When_the_database_cannot_be_created_the_index_is_disabled_but_reading_still_works()
    {
        // Um arquivo no lugar da pasta do banco faz a criação falhar.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "bloqueio");
        File.WriteAllText(blocker, "arquivo, não pasta");
        var folder = CreateSampleFolder();

        using var index = new DirectoryIndexService(Path.Combine(blocker, "index.db"));

        index.ReadFromDiskAndQueueStore(folder, ShowAll).Select(e => e.Name)
            .Should().BeEquivalentTo(["a.txt", "b.log", "sub"]);
        index.TryGetEntries(folder, ShowAll).Should().BeNull();
        index.IsIndexedAndCurrent(folder).Should().BeFalse();
        index.ComputeCountsFromDisk(folder, ShowAll).Should().Be((1, 2));
    }

    [Fact]
    public void An_empty_or_blank_path_never_reaches_the_database()
    {
        using var index = NewIndex();

        index.TryGetEntries("  ", ShowAll).Should().BeNull();
        index.IsIndexedAndCurrent("").Should().BeFalse();
        index.TryGetCounts("  ", ShowAll, out _, out _).Should().BeFalse();
        FluentActions.Invoking(() => index.Invalidate("  ")).Should().NotThrow();
    }
}
