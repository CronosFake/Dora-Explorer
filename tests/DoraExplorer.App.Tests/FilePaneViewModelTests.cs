using System.IO.Compression;
using System.Security.AccessControl;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Painel de arquivos (um dos dois lados da janela): abrir numa pasta, navegar, criar pasta, renomear, excluir, copiar e
/// mover, inclusive de dentro de arquivos compactados (ZIP pode ser alterado; .gz, .7z e .rar são somente leitura).
/// Usa serviços de verdade numa pasta temporária, sem janela.
/// </summary>
[Collection("App statics")]
public sealed class FilePaneViewModelTests : IDisposable
{
    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static List<string> ZipEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void The_pane_opens_on_the_initial_folder()
    {
        var folder = _h.Folder("inicio");

        var pane = _h.CreatePane(folder);

        pane.SelectedPath.Should().Be(folder);
        pane.CurrentPath.Should().Be(folder);
        pane.GetWorkingDirectory().Should().Be(folder);
        pane.BreadcrumbSegments.Last().Name.Should().Be("inicio");
        pane.BreadcrumbSegments.Last().IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Navigating_to_another_folder_changes_where_the_pane_is()
    {
        var first = _h.Folder("primeira");
        var second = _h.Folder("segunda");
        var pane = _h.CreatePane(first);

        pane.NavigateToPath(second);

        pane.CurrentPath.Should().Be(second);
        pane.GetWorkingDirectory().Should().Be(second);
        pane.BreadcrumbSegments.Last().Name.Should().Be("segunda");
    }

    [Fact]
    public void Navigating_to_a_file_leaves_the_pane_in_the_folder_that_holds_it()
    {
        var folder = _h.Folder("docs");
        var file = PaneTestHarness.File(folder, "nota.txt");
        var pane = _h.CreatePane(_h.Folder("outra"));

        pane.NavigateToPath(file);

        pane.CurrentPath.Should().Be(folder);
        pane.SelectedPath.Should().Be(file);
    }

    [Fact]
    public void The_tree_lists_folders_before_files_and_everything_that_is_on_disk()
    {
        var folder = _h.Folder("conteudo");
        Directory.CreateDirectory(Path.Combine(folder, "b-pasta"));
        Directory.CreateDirectory(Path.Combine(folder, "a-pasta"));
        PaneTestHarness.File(folder, "z.txt");
        PaneTestHarness.File(folder, "c.txt");
        var pane = _h.CreatePane(folder);

        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, folder)?.Children.Count == 4).Should().BeTrue();
        PaneTestHarness.Find(pane, folder)!.Children.Select(c => c.Name).Should().Equal("a-pasta", "b-pasta", "c.txt", "z.txt");
    }

    [Fact]
    public void Creating_a_folder_makes_it_on_disk_and_shows_it_in_the_tree()
    {
        var folder = _h.Folder("base");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolder("nova", out var error).Should().BeTrue(error);

        Directory.Exists(Path.Combine(folder, "nova")).Should().BeTrue();
        var parentNode = PaneTestHarness.Find(pane, folder);
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, Path.Combine(folder, "nova")) is not null)
            .Should().BeTrue($"a árvore passa a mostrar a pasta nova (nó da pasta: encontrado={parentNode is not null}, " +
                             $"expandido={parentNode?.IsExpanded}, carregado={parentNode?.ChildrenLoaded}, filhos={parentNode?.Children.Count}, " +
                             $"selecionado={ReferenceEquals(pane.SelectedNode, parentNode)}, caminho atual={pane.CurrentPath})");
    }

    [Fact]
    public void Deleting_a_protected_item_reports_access_denied_in_portuguese_and_marked()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "protegido.txt");
        var pane = _h.CreatePane(folder);
        using var deny = new AccessDenyScope();
        deny.MakeUndeletable(file);

        pane.TryDeleteItems([file], FileDeleteMode.Permanent, out var error).Should().BeFalse();

        error.Should().StartWith("protegido.txt: acesso negado a '");
        FileErrorText.IsAccessDenied(error!).Should().BeTrue("o aviso usa a marca para oferecer reiniciar como administrador");
        File.Exists(file).Should().BeTrue();
    }

    // Depois de apagar pelo teclado a seleção tem de ir para um vizinho, senão o foco se perde e as setas deixam de funcionar.
    // O vizinho é escolhido antes de apagar, porque depois a pasta é recarregada e os nós são outros.
    private FilePaneViewModel PaneWithFiles(out string folder, out string[] files)
    {
        var root = _h.Folder("base");
        string[] names = ["a.txt", "b.txt", "c.txt"];
        folder = root;
        files = names.Select(name => PaneTestHarness.File(root, name)).ToArray();
        var pane = _h.CreatePane(root);
        pane.NavigateToPath(root);
        var last = files[^1];
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, last) is not null).Should().BeTrue("a pasta abriu e listou os arquivos");
        return pane;
    }

    [Fact]
    public void After_removing_an_item_the_next_one_in_the_list_is_the_one_to_select()
    {
        var pane = PaneWithFiles(out _, out var files);

        pane.GetPathToSelectAfterRemoving([files[1]]).Should().Be(files[2]);
        pane.GetPathToSelectAfterRemoving([files[0]]).Should().Be(files[1]);
    }

    [Fact]
    public void After_removing_the_last_item_the_previous_one_is_the_one_to_select()
    {
        var pane = PaneWithFiles(out _, out var files);

        pane.GetPathToSelectAfterRemoving([files[2]]).Should().Be(files[1]);
    }

    [Fact]
    public void After_removing_several_items_the_neighbour_is_outside_the_removed_group()
    {
        var pane = PaneWithFiles(out _, out var files);

        pane.GetPathToSelectAfterRemoving([files[0], files[1]]).Should().Be(files[2]);
        pane.GetPathToSelectAfterRemoving([files[1], files[2]]).Should().Be(files[0]);
    }

    [Fact]
    public void After_removing_every_item_the_folder_that_held_them_is_the_one_to_select()
    {
        var pane = PaneWithFiles(out var folder, out var files);

        pane.GetPathToSelectAfterRemoving(files).Should().Be(folder);
    }

    [Fact]
    public void After_removing_an_open_folder_the_items_inside_it_are_skipped()
    {
        var folder = _h.Folder("base");
        var sub = Directory.CreateDirectory(Path.Combine(folder, "d")).FullName;
        var inside = PaneTestHarness.File(sub, "dentro.txt");
        var after = PaneTestHarness.File(folder, "f.txt");
        var pane = _h.CreatePane(folder);
        pane.NavigateToPath(sub);
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, inside) is not null).Should().BeTrue("a subpasta abriu");

        pane.GetPathToSelectAfterRemoving([sub]).Should().Be(after, "o que estava dentro da pasta apagada some junto");
    }

    [Fact]
    public void A_path_that_is_not_in_the_list_gives_no_neighbour()
    {
        var pane = PaneWithFiles(out var folder, out _);

        pane.GetPathToSelectAfterRemoving([Path.Combine(folder, "nao-existe.txt")]).Should().BeNull();
        pane.GetPathToSelectAfterRemoving([]).Should().BeNull();
    }

    [Fact]
    public void Opening_a_folder_the_user_may_not_list_marks_the_node_instead_of_showing_it_as_plainly_empty()
    {
        var folder = _h.Folder("base");
        var locked = Directory.CreateDirectory(Path.Combine(folder, "trancada")).FullName;
        PaneTestHarness.File(locked, "dentro.txt");
        using var deny = new AccessDenyScope();
        deny.Deny(locked, FileSystemRights.ListDirectory);
        var pane = _h.CreatePane(folder);

        pane.NavigateToPath(locked);

        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, locked) is { ChildrenLoaded: true })
            .Should().BeTrue("abrir a pasta carrega os filhos");
        var node = PaneTestHarness.Find(pane, locked)!;
        node.ListingDenied.Should().BeTrue();
        node.Children.Where(child => !child.IsPlaceholder).Should().BeEmpty();
    }

    // O que a revalidação em segundo plano faz depois de listar uma pasta: conta o conteúdo de cada subpasta e aplica nos nós.
    // Sem janela não há Dispatcher, então o passo de aplicar roda aqui direto.
    private void ApplyBackgroundCounts(NavigationTreeNodeViewModel parent)
    {
        var view = _h.ViewSettings.Options;
        var counts = parent.Children
            .Where(child => child is { Kind: NavigationNodeKind.Directory, IsPlaceholder: false })
            .ToDictionary(
                child => child.TargetPath!,
                child => _h.Index.ComputeCountsFromDisk(child.TargetPath!, view),
                StringComparer.OrdinalIgnoreCase);

        _h.Tree.ApplyBackgroundResults(parent, null, counts, parent.LoadGeneration);
    }

    [Fact]
    public void A_subfolder_the_user_may_not_list_is_marked_from_the_parent_listing_without_opening_it()
    {
        var folder = _h.Folder("base");
        var locked = Directory.CreateDirectory(Path.Combine(folder, "trancada")).FullName;
        PaneTestHarness.File(locked, "dentro.txt");
        var open = Directory.CreateDirectory(Path.Combine(folder, "aberta")).FullName;
        PaneTestHarness.File(open, "dentro.txt");
        using var deny = new AccessDenyScope();
        deny.Deny(locked, FileSystemRights.ListDirectory);
        var pane = _h.CreatePane(folder);

        pane.NavigateToPath(folder);
        var parent = PaneTestHarness.Find(pane, folder)!;
        ApplyBackgroundCounts(parent);

        PaneTestHarness.Find(pane, locked)!.ListingDenied.Should().BeTrue("a linha mostra o cadeado sem o usuário abrir a pasta");
        PaneTestHarness.Find(pane, open)!.ListingDenied.Should().BeFalse();
    }

    [Fact]
    public void The_mark_goes_away_when_the_subfolder_can_be_listed_again()
    {
        var folder = _h.Folder("base");
        var locked = Directory.CreateDirectory(Path.Combine(folder, "trancada")).FullName;
        PaneTestHarness.File(locked, "dentro.txt");
        using var deny = new AccessDenyScope();
        deny.Deny(locked, FileSystemRights.ListDirectory);
        var pane = _h.CreatePane(folder);
        pane.NavigateToPath(folder);
        var parent = PaneTestHarness.Find(pane, folder)!;
        ApplyBackgroundCounts(parent);
        PaneTestHarness.Find(pane, locked)!.ListingDenied.Should().BeTrue();

        deny.Dispose();
        ApplyBackgroundCounts(parent);

        var node = PaneTestHarness.Find(pane, locked)!;
        node.ListingDenied.Should().BeFalse("depois de virar administrador (ou de liberar a pasta) o cadeado some");
        node.FileCount.Should().Be(1);
    }

    [Fact]
    public void The_status_bar_of_a_selected_folder_follows_the_lock_mark()
    {
        var folder = _h.Folder("base");
        var open = Directory.CreateDirectory(Path.Combine(folder, "aberta")).FullName;
        PaneTestHarness.File(open, "dentro.txt");
        var pane = _h.CreatePane(folder);
        pane.NavigateToPath(folder);
        var parent = PaneTestHarness.Find(pane, folder)!;
        ApplyBackgroundCounts(parent);
        var node = PaneTestHarness.Find(pane, open)!;
        pane.SelectedNode = node;
        pane.StatusText.Should().Be("aberta · 0 pastas · 1 arquivo");

        node.ListingDenied = true;
        pane.StatusText.Should().Be("aberta · sem permissão para abrir", "a marca chega depois de a pasta já estar selecionada");

        node.ListingDenied = false;
        pane.StatusText.Should().Be("aberta · 0 pastas · 1 arquivo");
    }

    [Fact]
    public void Selecting_a_subfolder_the_user_may_not_list_shows_no_permission_in_the_status_bar()
    {
        var folder = _h.Folder("base");
        var locked = Directory.CreateDirectory(Path.Combine(folder, "trancada")).FullName;
        PaneTestHarness.File(locked, "dentro.txt");
        using var deny = new AccessDenyScope();
        deny.Deny(locked, FileSystemRights.ListDirectory);
        var pane = _h.CreatePane(folder);
        pane.NavigateToPath(folder);
        var parent = PaneTestHarness.Find(pane, folder)!;
        ApplyBackgroundCounts(parent);

        pane.SelectedNode = PaneTestHarness.Find(pane, locked)!;

        pane.StatusText.Should().Be("trancada · sem permissão para abrir");
    }

    [Fact]
    public void A_folder_that_can_be_listed_is_not_marked()
    {
        var folder = _h.Folder("base");
        var open = Directory.CreateDirectory(Path.Combine(folder, "aberta")).FullName;
        PaneTestHarness.File(open, "dentro.txt");
        var pane = _h.CreatePane(folder);

        pane.NavigateToPath(open);

        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, open) is { ChildrenLoaded: true }).Should().BeTrue();
        PaneTestHarness.Find(pane, open)!.ListingDenied.Should().BeFalse();
    }

    [Fact]
    public void The_new_folder_shortcut_creates_Nova_pasta_when_the_name_is_free()
    {
        var folder = _h.Folder("base");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolderWithInlineRename(out var error).Should().BeTrue(error);

        Directory.GetDirectories(folder).Select(Path.GetFileName).Should().Equal("Nova pasta");
    }

    [Fact]
    public void The_new_folder_shortcut_skips_a_Nova_pasta_that_already_exists()
    {
        var folder = _h.Folder("base");
        var existing = Directory.CreateDirectory(Path.Combine(folder, "Nova pasta")).FullName;
        var inside = PaneTestHarness.File(existing, "dentro.txt");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolderWithInlineRename(out var error).Should().BeTrue(error);

        Directory.GetDirectories(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["Nova pasta", "Nova pasta (2)"]);
        File.Exists(inside).Should().BeTrue();
    }

    [Fact]
    public void The_new_folder_shortcut_skips_a_file_that_is_called_Nova_pasta()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "Nova pasta");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolderWithInlineRename(out var error).Should().BeTrue(error);

        Directory.GetDirectories(folder).Select(Path.GetFileName).Should().Equal("Nova pasta (2)");
        File.Exists(file).Should().BeTrue("o arquivo continua sendo um arquivo");
    }

    [Fact]
    public void The_new_folder_shortcut_finds_the_first_free_number_among_folders_and_files()
    {
        var folder = _h.Folder("base");
        Directory.CreateDirectory(Path.Combine(folder, "Nova pasta"));
        PaneTestHarness.File(folder, "Nova pasta (2)");
        Directory.CreateDirectory(Path.Combine(folder, "Nova pasta (3)"));
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolderWithInlineRename(out var error).Should().BeTrue(error);

        Directory.Exists(Path.Combine(folder, "Nova pasta (4)")).Should().BeTrue();
    }

    [Fact]
    public void Creating_a_folder_that_already_exists_is_refused_and_does_not_touch_it()
    {
        var folder = _h.Folder("base");
        var existing = Directory.CreateDirectory(Path.Combine(folder, "ja-existe")).FullName;
        var inside = PaneTestHarness.File(existing, "dentro.txt");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolder("ja-existe", out var error).Should().BeFalse();

        error.Should().Contain("Já existe");
        File.Exists(inside).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_folder_name_that_is_empty_or_cannot_exist_is_refused(string name)
    {
        var folder = _h.Folder("base");
        var pane = _h.CreatePane(folder);

        pane.TryCreateFolder(name, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        Directory.GetDirectories(folder).Should().BeEmpty();
    }

    [Fact]
    public void Folders_cannot_be_created_inside_an_archive()
    {
        var zip = _h.Zip("pacote.zip", ("a.txt", "x"));
        var pane = _h.CreatePane(_h.Folder("base"));
        pane.NavigateToPath(zip);

        pane.TryCreateFolder("nova", out var error).Should().BeFalse();

        error.Should().Contain("arquivo compactado");
        ZipEntries(zip).Should().Equal("a.txt");
    }

    [Fact]
    public void Renaming_a_file_renames_it_on_disk_and_follows_it_in_the_selection()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "antigo.txt");
        var pane = _h.CreatePane(folder);

        pane.TryRenamePath(file, "novo.txt", out var newPath, out var error).Should().BeTrue(error);

        newPath.Should().Be(Path.Combine(folder, "novo.txt"));
        File.Exists(file).Should().BeFalse();
        File.Exists(newPath).Should().BeTrue();
        pane.SelectedPath.Should().Be(newPath);
    }

    [Fact]
    public void Renaming_onto_a_name_that_is_taken_is_refused_and_changes_nothing()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "a.txt", "do-a");
        PaneTestHarness.File(folder, "b.txt", "do-b");
        var pane = _h.CreatePane(folder);

        pane.TryRenamePath(file, "b.txt", out var newPath, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        File.ReadAllText(Path.Combine(folder, "a.txt")).Should().Be("do-a");
        File.ReadAllText(Path.Combine(folder, "b.txt")).Should().Be("do-b");
    }

    [Fact]
    public void Renaming_with_nothing_selected_asks_for_a_selection()
    {
        var pane = _h.CreatePane(_h.Folder("base"));
        pane.ClearItemSelection();

        pane.TryRenamePath("", "x", out _, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Deleting_nothing_asks_for_a_selection()
    {
        var pane = _h.CreatePane(_h.Folder("base"));

        pane.TryDeleteItems([], FileDeleteMode.Permanent, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Deleting_permanently_removes_files_and_folders_and_updates_the_tree()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "apagar.txt");
        var subfolder = Directory.CreateDirectory(Path.Combine(folder, "pasta")).FullName;
        PaneTestHarness.File(subfolder, "dentro.txt");
        var keeper = PaneTestHarness.File(folder, "fica.txt");
        var pane = _h.CreatePane(folder);

        pane.TryDeleteItems([file, subfolder], FileDeleteMode.Permanent, out var error).Should().BeTrue(error);

        File.Exists(file).Should().BeFalse();
        Directory.Exists(subfolder).Should().BeFalse();
        File.Exists(keeper).Should().BeTrue();
        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, file) is null && PaneTestHarness.Find(pane, subfolder) is null)
            .Should().BeTrue("a árvore deixa de mostrar o que foi excluído");
        PaneTestHarness.Find(pane, keeper).Should().NotBeNull();
    }

    [Fact]
    public void An_entry_of_a_zip_can_be_deleted_and_leaves_the_rest()
    {
        var zip = _h.Zip("pacote.zip", ("a.txt", "A"), ("b.txt", "B"));
        var pane = _h.CreatePane(_h.Folder("base"));

        pane.TryDeleteItems([ArchivePathHelper.Combine(zip, "a.txt")], FileDeleteMode.Permanent, out var error)
            .Should().BeTrue(error);

        ZipEntries(zip).Should().Equal("b.txt");
    }

    [Fact]
    public void An_entry_of_a_read_only_archive_cannot_be_deleted_and_the_archive_stays_intact()
    {
        var gz = _h.Gzip("dados.txt.gz", "conteudo");
        var before = File.ReadAllBytes(gz);
        var pane = _h.CreatePane(_h.Folder("base"));

        pane.TryDeleteItems([ArchivePathHelper.Combine(gz, "dados.txt")], FileDeleteMode.Permanent, out var error)
            .Should().BeFalse();

        error.Should().Contain(".gz");
        File.ReadAllBytes(gz).Should().Equal(before);
    }

    [Fact]
    public void Only_files_that_can_really_be_deleted_are_offered_for_deletion()
    {
        var folder = _h.Folder("base");
        var plain = PaneTestHarness.File(folder, "comum.txt");
        var zip = _h.Zip("pacote.zip", ("a.txt", "A"));
        var gz = _h.Gzip("dados.txt.gz", "x");
        var pane = _h.CreatePane(folder);

        pane.NavigateToPath(plain);
        pane.SetSingleItemSelection(PaneTestHarness.Find(pane, plain)!);
        pane.GetDeletableItemPaths().Should().ContainSingle().Which.Should().Be(plain);

        var zipEntry = ArchivePathHelper.Combine(zip, "a.txt");
        pane.NavigateToPath(zipEntry);
        pane.SetSingleItemSelection(PaneTestHarness.Find(pane, zipEntry)!);
        pane.GetDeletableItemPaths().Should().ContainSingle("dá para excluir de dentro de um .zip").Which.Should().Be(zipEntry);

        var gzEntry = ArchivePathHelper.Combine(gz, "dados.txt");
        pane.NavigateToPath(gzEntry);
        pane.SetSingleItemSelection(PaneTestHarness.Find(pane, gzEntry)!);
        pane.GetDeletableItemPaths().Should().BeEmpty("um .gz é somente leitura");
    }

    [Fact]
    public void Copying_a_file_leaves_the_original_and_never_overwrites_the_destination()
    {
        var source = _h.Folder("origem");
        var destination = _h.Folder("destino");
        var file = PaneTestHarness.File(source, "a.txt", "novo");
        PaneTestHarness.File(destination, "a.txt", "antigo");
        var pane = _h.CreatePane(source);

        pane.TryCopyItem(file, destination, out var error).Should().BeTrue(error);

        File.ReadAllText(file).Should().Be("novo");
        File.ReadAllText(Path.Combine(destination, "a.txt")).Should().Be("antigo", "uma cópia nunca sobrescreve");
        Directory.GetFiles(destination).Should().HaveCount(2);
    }

    [Fact]
    public void Moving_a_file_takes_it_out_of_the_source()
    {
        var source = _h.Folder("origem");
        var destination = _h.Folder("destino");
        var file = PaneTestHarness.File(source, "a.txt", "conteudo");
        var pane = _h.CreatePane(source);

        pane.TryMoveItem(file, destination, out var error).Should().BeTrue(error);

        File.Exists(file).Should().BeFalse();
        File.ReadAllText(Path.Combine(destination, "a.txt")).Should().Be("conteudo");
    }

    [Fact]
    public void Copying_or_moving_into_an_archive_is_refused()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "a.txt");
        var zip = _h.Zip("pacote.zip", ("x.txt", "x"));
        var pane = _h.CreatePane(folder);

        pane.TryCopyItem(file, zip, out var copyError).Should().BeFalse();
        pane.TryMoveItem(file, ArchivePathHelper.Combine(zip, "pasta"), out var moveError).Should().BeFalse();

        copyError.Should().Contain("arquivo compactado");
        moveError.Should().Contain("arquivo compactado");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void Moving_out_of_a_zip_extracts_the_entry_and_removes_it_from_the_zip()
    {
        var zip = _h.Zip("pacote.zip", ("a.txt", "A"), ("b.txt", "B"));
        var destination = _h.Folder("destino");
        var pane = _h.CreatePane(_h.Folder("base"));

        pane.TryMoveItem(ArchivePathHelper.Combine(zip, "a.txt"), destination, out var error).Should().BeTrue(error);

        File.ReadAllText(Path.Combine(destination, "a.txt")).Should().Be("A");
        ZipEntries(zip).Should().Equal("b.txt");
    }

    [Fact]
    public void Moving_out_of_a_read_only_archive_only_copies_and_the_archive_keeps_the_entry()
    {
        var gz = _h.Gzip("dados.txt.gz", "conteudo");
        var before = File.ReadAllBytes(gz);
        var destination = _h.Folder("destino");
        var pane = _h.CreatePane(_h.Folder("base"));

        pane.TryMoveItem(ArchivePathHelper.Combine(gz, "dados.txt"), destination, out var error).Should().BeTrue(error);

        File.ReadAllText(Path.Combine(destination, "dados.txt")).Should().Be("conteudo");
        File.ReadAllBytes(gz).Should().Equal(before);
    }

    [Fact]
    public void Sorting_by_name_descending_reorders_what_is_listed()
    {
        var folder = _h.Folder("ordem");
        PaneTestHarness.File(folder, "a.txt");
        PaneTestHarness.File(folder, "b.txt");
        PaneTestHarness.File(folder, "c.txt");
        var pane = _h.CreatePane(folder);

        pane.ApplySortOptions(new TreeSortOptions(FoldersFirst: true, Field: TreeSortField.Name, Ascending: false));

        PaneTestHarness.Eventually(() => PaneTestHarness.Find(pane, folder)!.Children.Select(c => c.Name).SequenceEqual(["c.txt", "b.txt", "a.txt"]))
            .Should().BeTrue("a lista é reordenada");
        PaneTestHarness.Find(pane, folder)!.Children.Select(c => c.Name).Should().Equal("c.txt", "b.txt", "a.txt");
    }

    [Fact]
    public void Marking_favorites_from_the_pane_adds_and_removes_them()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "a.txt");
        var pane = _h.CreatePane(folder);

        pane.TryToggleFavorites([file], out var added, out var error).Should().BeTrue(error);
        added.Should().BeTrue();
        Services.FavoritesService.IsFavorite(file).Should().BeTrue();

        pane.TryToggleFavorites([file], out added, out error).Should().BeTrue(error);
        added.Should().BeFalse();
        Services.FavoritesService.IsFavorite(file).Should().BeFalse();
    }

    [Fact]
    public void Only_paths_that_exist_on_this_computer_count_as_local_paths()
    {
        var folder = _h.Folder("base");
        var file = PaneTestHarness.File(folder, "a.txt");
        var zip = _h.Zip("pacote.zip", ("x.txt", "x"));

        FilePaneViewModel.IsLocalFileSystemPath(folder).Should().BeTrue();
        FilePaneViewModel.IsLocalFileSystemPath(file).Should().BeTrue();
        FilePaneViewModel.IsLocalFileSystemPath(Path.Combine(folder, "nao-existe.txt")).Should().BeFalse();
        FilePaneViewModel.IsLocalFileSystemPath(ArchivePathHelper.Combine(zip, "x.txt")).Should().BeFalse("é uma entrada de arquivo compactado");
        FilePaneViewModel.IsLocalFileSystemPath("").Should().BeFalse();
    }
}
