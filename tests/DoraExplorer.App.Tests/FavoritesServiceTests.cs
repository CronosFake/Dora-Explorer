using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Favoritos: a lista fica num arquivo JSON e vale entre execuções do app. O teste aponta o armazenamento para uma pasta
/// temporária (<see cref="FavoritesService.UseStore"/>), então nunca toca em <c>%AppData%\DoraExplorer</c>. Roda fora do
/// paralelismo porque o serviço guarda o estado em campos estáticos.
/// </summary>
[Collection("App statics")]
public sealed class FavoritesServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-fav-" + Guid.NewGuid().ToString("N"))).FullName;

    private string StoreFile => Path.Combine(_directory, "dados", "favorites.json");

    private string Item(string name) => Path.Combine(_directory, name);

    public FavoritesServiceTests() => FavoritesService.UseStore(StoreFile);

    public void Dispose()
    {
        FavoritesService.UseStore(null);
        SshTestHelpers.DeleteDirectory(_directory);
    }

    /// <summary>Simula fechar e abrir o app: esquece a memória e relê o arquivo.</summary>
    private void Restart() => FavoritesService.UseStore(StoreFile);

    [Fact]
    public void A_new_store_has_no_favorites()
    {
        FavoritesService.HasItems().Should().BeFalse();
        FavoritesService.GetCounts().Should().Be((0, 0));
        FavoritesService.GetEntries().Should().BeEmpty();
        FavoritesService.IsFavorite(Item("qualquer")).Should().BeFalse();
        File.Exists(StoreFile).Should().BeFalse("só se cria o arquivo quando há o que guardar");
    }

    [Fact]
    public void An_added_favorite_is_found_ignoring_case_and_survives_a_restart()
    {
        var folder = Directory.CreateDirectory(Item("Projetos")).FullName;

        FavoritesService.TryAdd(folder, out var error).Should().BeTrue(error);

        FavoritesService.IsFavorite(folder.ToUpperInvariant()).Should().BeTrue();
        Restart();
        FavoritesService.IsFavorite(folder).Should().BeTrue("o arquivo guarda a lista entre execuções");
        FavoritesService.HasItems().Should().BeTrue();
    }

    [Fact]
    public void Adding_the_same_path_again_keeps_a_single_entry()
    {
        var folder = Directory.CreateDirectory(Item("Projetos")).FullName;

        FavoritesService.TryAdd(folder, out _).Should().BeTrue();
        FavoritesService.TryAdd(folder.ToLowerInvariant(), out _).Should().BeTrue();

        FavoritesService.GetEntries().Should().ContainSingle();
    }

    [Fact]
    public void Removing_a_favorite_forgets_it_for_good()
    {
        var folder = Directory.CreateDirectory(Item("Projetos")).FullName;
        FavoritesService.TryAdd(folder, out _).Should().BeTrue();

        FavoritesService.TryRemove(folder, out var error).Should().BeTrue(error);

        FavoritesService.IsFavorite(folder).Should().BeFalse();
        Restart();
        FavoritesService.IsFavorite(folder).Should().BeFalse();
        FavoritesService.HasItems().Should().BeFalse();
    }

    [Fact]
    public void Removing_something_that_is_not_a_favorite_is_harmless()
    {
        var folder = Directory.CreateDirectory(Item("Projetos")).FullName;
        FavoritesService.TryAdd(folder, out _).Should().BeTrue();

        FavoritesService.TryRemove(Item("outro"), out var error).Should().BeTrue(error);

        FavoritesService.IsFavorite(folder).Should().BeTrue();
    }

    [Fact]
    public void Toggling_adds_everything_unless_everything_is_already_a_favorite()
    {
        var a = Directory.CreateDirectory(Item("a")).FullName;
        var b = Directory.CreateDirectory(Item("b")).FullName;
        FavoritesService.TryAdd(a, out _).Should().BeTrue();

        FavoritesService.TryToggle([a, b], out var added, out var error).Should().BeTrue(error);
        added.Should().BeTrue("um deles ainda não era favorito");
        FavoritesService.IsFavorite(a).Should().BeTrue();
        FavoritesService.IsFavorite(b).Should().BeTrue();

        FavoritesService.TryToggle([a, b], out added, out error).Should().BeTrue(error);
        added.Should().BeFalse("os dois já eram favoritos, então saem");
        FavoritesService.HasItems().Should().BeFalse();
    }

    [Fact]
    public void Toggling_repeated_paths_counts_each_one_once()
    {
        var a = Directory.CreateDirectory(Item("a")).FullName;

        FavoritesService.TryToggle([a, a.ToUpperInvariant(), a], out var added, out _).Should().BeTrue();

        added.Should().BeTrue();
        FavoritesService.GetEntries().Should().ContainSingle();
    }

    [Fact]
    public void Entries_tell_folders_from_files_and_carry_name_size_and_date()
    {
        var folder = Directory.CreateDirectory(Item("Projetos")).FullName;
        var file = Item("notas.txt");
        File.WriteAllText(file, "12345");
        FavoritesService.TryAdd(folder, out _).Should().BeTrue();
        FavoritesService.TryAdd(file, out _).Should().BeTrue();

        var entries = FavoritesService.GetEntries();

        var folderEntry = entries.Single(e => e.FullPath == folder);
        folderEntry.IsDirectory.Should().BeTrue();
        folderEntry.Name.Should().Be("Projetos");
        folderEntry.SizeInBytes.Should().BeNull();
        var fileEntry = entries.Single(e => e.FullPath == file);
        fileEntry.IsDirectory.Should().BeFalse();
        fileEntry.Name.Should().Be("notas.txt");
        fileEntry.SizeInBytes.Should().Be(5);
        fileEntry.LastModified.Should().NotBeNull();
        FavoritesService.GetCounts().Should().Be((1, 1));
    }

    [Fact]
    public void A_favorite_that_disappeared_stays_listed_instead_of_vanishing()
    {
        var gone = Item("Pasta que sumiu");
        var goneFile = Item("arquivo.txt");
        FavoritesService.TryAdd(gone, out _).Should().BeTrue();
        FavoritesService.TryAdd(goneFile, out _).Should().BeTrue();

        var entries = FavoritesService.GetEntries();

        entries.Should().HaveCount(2, "uma unidade desligada não pode apagar o favorito");
        entries.Single(e => e.FullPath == gone).IsDirectory.Should().BeTrue("sem extensão parece uma pasta");
        entries.Single(e => e.FullPath == goneFile).IsDirectory.Should().BeFalse("com extensão parece um arquivo");
    }

    [Fact]
    public void The_storage_folder_is_created_when_saving()
    {
        Directory.Exists(Path.GetDirectoryName(StoreFile)).Should().BeFalse();

        FavoritesService.TryAdd(Directory.CreateDirectory(Item("a")).FullName, out _).Should().BeTrue();

        File.Exists(StoreFile).Should().BeTrue();
    }

    [Fact]
    public void A_corrupt_file_loads_as_empty_and_is_replaced_by_the_next_save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(StoreFile, "{ isto nao e json ");
        Restart();

        FavoritesService.HasItems().Should().BeFalse("um arquivo estragado não pode derrubar o app");

        var folder = Directory.CreateDirectory(Item("a")).FullName;
        FavoritesService.TryAdd(folder, out _).Should().BeTrue();
        Restart();
        FavoritesService.IsFavorite(folder).Should().BeTrue();
    }

    [Fact]
    public void Blank_and_repeated_entries_in_the_file_are_ignored_on_load()
    {
        var folder = Directory.CreateDirectory(Item("a")).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(StoreFile, System.Text.Json.JsonSerializer.Serialize(new[] { "", folder, "  ", folder.ToUpperInvariant() }));
        Restart();

        FavoritesService.GetEntries().Should().ContainSingle().Which.FullPath.Should().Be(folder);
    }

    [Fact]
    public void Switching_the_store_never_leaks_favorites_between_files()
    {
        var folder = Directory.CreateDirectory(Item("a")).FullName;
        FavoritesService.TryAdd(folder, out _).Should().BeTrue();

        FavoritesService.UseStore(Path.Combine(_directory, "outro", "favorites.json"));

        FavoritesService.IsFavorite(folder).Should().BeFalse();
    }
}
