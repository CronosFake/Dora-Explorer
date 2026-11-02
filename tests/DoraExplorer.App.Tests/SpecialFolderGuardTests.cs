using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Lixeira e Favoritos são pastas especiais da árvore. O que importa aqui são as travas: excluir ou restaurar só vale
/// para o que está mesmo na lixeira, e a lixeira e o próprio "favorites:" não entram nos favoritos. Nenhum destes testes
/// grava em <c>%AppData%\DoraExplorer</c>: todas as recusas acontecem antes de qualquer acesso ao armazenamento.
/// </summary>
public sealed class SpecialFolderGuardTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-special-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => SshTestHelpers.DeleteDirectory(_directory);

    [Theory]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1\$R1A2B3C.txt", true)]
    [InlineData(@"c:\$RECYCLE.BIN\S-1-5-21-1\$Rabc", true)]
    [InlineData(@"D:\$Recycle.Bin\S-1-5-21-1\", true)]
    [InlineData(@"C:\Users\ana\documento.txt", false)]
    [InlineData(@"C:\$Recycle.Bin", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Only_paths_inside_the_recycle_bin_count_as_recycle_bin_data(string? path, bool expected) =>
        RecycleBinService.IsRecycleBinDataPath(path).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Users\ana\documento.txt")]
    [InlineData(@"C:\$Recycle.Bin\S-0-0-0-sem-info\$Rinexistente.txt")]
    public void The_original_path_is_unknown_outside_the_bin_or_without_its_info_file(string? dataPath) =>
        RecycleBinService.TryResolveOriginalPath(dataPath).Should().BeNull();

    [Fact]
    public void Permanent_delete_refuses_anything_that_is_not_in_the_recycle_bin()
    {
        var outsider = Path.Combine(_directory, "importante.txt");
        File.WriteAllText(outsider, "nao pode sumir");

        RecycleBinService.TryDeletePermanently([outsider], out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        File.Exists(outsider).Should().BeTrue("excluir da lixeira nunca pode apagar um arquivo comum");
    }

    [Fact]
    public void Restore_refuses_anything_that_is_not_in_the_recycle_bin()
    {
        var outsider = Path.Combine(_directory, "importante.txt");
        File.WriteAllText(outsider, "nao pode mexer");

        RecycleBinService.TryRestoreItems([outsider], out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        File.ReadAllText(outsider).Should().Be("nao pode mexer");
    }

    [Fact]
    public void Restore_and_delete_ask_for_a_selection_when_there_is_none()
    {
        RecycleBinService.TryRestoreItems([], out var restoreError).Should().BeFalse();
        RecycleBinService.TryDeletePermanently([], out var deleteError).Should().BeFalse();

        restoreError.Should().NotBeNullOrWhiteSpace();
        deleteError.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("favorites:")]
    [InlineData("FAVORITES:algo")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1\$R1A2B3C.txt")]
    [InlineData(@"C:\dados\$Recycle.Bin\coisa")]
    [InlineData("")]
    [InlineData("   ")]
    public void Favorites_refuse_the_recycle_bin_themselves_and_blank_paths(string path)
    {
        FavoritesService.TryAdd(path, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Removing_a_blank_favorite_is_refused()
    {
        FavoritesService.TryRemove("  ", out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Toggling_favorites_needs_at_least_one_real_path()
    {
        FavoritesService.TryToggle([], out _, out var emptyError).Should().BeFalse();
        FavoritesService.TryToggle(["", "  "], out _, out var blankError).Should().BeFalse();

        emptyError.Should().NotBeNullOrWhiteSpace();
        blankError.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_never_a_favorite(string? path) =>
        FavoritesService.IsFavorite(path).Should().BeFalse();
}
