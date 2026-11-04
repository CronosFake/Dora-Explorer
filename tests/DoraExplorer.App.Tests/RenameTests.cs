using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Renomear no painel: que trecho do nome já vem selecionado (o nome sem a extensão, como no Explorer), quando um clique devagar
/// no nome começa a renomear, e o caminho de confirmar (Enter ou perder o foco) e cancelar (Esc).
/// </summary>
[Collection("App statics")]
public sealed class RenameTests : IDisposable
{
    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- o trecho selecionado ao começar

    [Theory]
    [InlineData("relatorio.docx", true, 0, 9)]
    [InlineData("relatorio.final.docx", true, 0, 15)]
    [InlineData("archive.tar.gz", true, 0, 11)]
    [InlineData("sem-extensao", true, 0, 12)]
    [InlineData(".gitignore", true, 0, 10)]
    [InlineData("termina-em-ponto.", true, 0, 17)]
    [InlineData("a.b", true, 0, 1)]
    [InlineData("pasta.v2", false, 0, 8)]
    [InlineData("Nova pasta", false, 0, 10)]
    [InlineData("", true, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void A_file_starts_with_the_name_selected_but_not_the_extension_and_a_folder_with_everything(
        string name, bool isFile, int start, int length)
    {
        RenameSelection.GetRange(name, isFile).Should().Be((start, length));
    }

    [Fact]
    public void The_selected_range_never_leaves_the_text()
    {
        foreach (var name in new[] { "a", ".", "..", "a.", ".a", "...", "x.y.z", "nome com espaço.txt", "ação.pdf" })
        {
            foreach (var isFile in new[] { true, false })
            {
                var (start, length) = RenameSelection.GetRange(name, isFile);

                start.Should().BeGreaterOrEqualTo(0);
                (start + length).Should().BeLessOrEqualTo(name.Length, $"'{name}' (arquivo: {isFile})");
            }
        }
    }

    // ---- clique devagar: só no item que já era o único selecionado

    private (FilePaneViewModel Pane, string File, string Folder) PaneWithFileAndFolder()
    {
        var parent = _h.Folder("renomear");
        var file = PaneTestHarness.File(parent, "a.txt");
        var folder = Directory.CreateDirectory(Path.Combine(parent, "pasta")).FullName;
        var pane = _h.CreatePane(parent);
        pane.CompleteInitialPathRestore();
        pane.NavigateToPath(parent);
        return (pane, file, folder);
    }

    [Fact]
    public void A_slow_click_renames_the_item_that_was_already_the_only_one_selected()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(node);

        pane.CanBeginSlowRename(node).Should().BeTrue();
    }

    [Fact]
    public void A_folder_can_be_renamed_by_a_slow_click_too()
    {
        var (pane, _, folder) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, folder)!;
        pane.SetSingleItemSelection(node);

        pane.CanBeginSlowRename(node).Should().BeTrue();
    }

    [Fact]
    public void An_item_that_was_not_selected_yet_does_not_start_renaming()
    {
        var (pane, file, folder) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(PaneTestHarness.Find(pane, folder)!);

        pane.CanBeginSlowRename(node).Should().BeFalse("o primeiro clique só seleciona");
    }

    [Fact]
    public void An_item_inside_a_selection_of_several_does_not_start_renaming()
    {
        var (pane, file, folder) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(node);
        pane.ToggleItemSelection(PaneTestHarness.Find(pane, folder)!);

        pane.CanBeginSlowRename(node).Should().BeFalse();
    }

    [Fact]
    public void Nothing_starts_renaming_while_another_item_is_being_renamed()
    {
        var (pane, file, folder) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(node);
        pane.BeginRenameNode(PaneTestHarness.Find(pane, folder)!);

        pane.CanBeginSlowRename(node).Should().BeFalse();
    }

    [Fact]
    public void A_drive_or_a_recycle_bin_entry_is_never_renamed_by_a_slow_click()
    {
        var pane = _h.CreatePane(_h.Folder("qualquer"));
        var drive = new NavigationTreeNodeViewModel("C:", NavigationNodeKind.Drive, @"C:\");
        var trashed = new NavigationTreeNodeViewModel("x.txt", NavigationNodeKind.File, @"C:\$Recycle.Bin\S-1\$R123.txt") { IsRecycleBinEntry = true };
        pane.SetSingleItemSelection(drive);

        pane.CanBeginSlowRename(drive).Should().BeFalse();

        pane.SetSingleItemSelection(trashed);
        pane.CanBeginSlowRename(trashed).Should().BeFalse();
    }

    // ---- confirmar e cancelar

    [Fact]
    public void Starting_to_rename_fills_the_box_with_the_current_name_and_says_so()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        var started = 0;
        pane.RenamingStarted += (_, _) => started++;

        pane.BeginRenameNode(node);

        pane.RenamingNode.Should().BeSameAs(node);
        node.IsRenaming.Should().BeTrue();
        node.RenameText.Should().Be("a.txt");
        started.Should().Be(1);
    }

    [Fact]
    public void Committing_a_new_name_renames_the_file_on_disk()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.BeginRenameNode(node);
        node.RenameText = "b.txt";

        pane.CommitRename();

        File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "b.txt")).Should().BeTrue();
        File.Exists(file).Should().BeFalse();
        pane.RenamingNode.Should().BeNull();
    }

    // ---- depois de confirmar, o item com o NOVO nome fica selecionado (e nada mais, nem a pasta que o contém)

    [Fact]
    public void After_renaming_a_file_the_item_with_the_new_name_is_the_only_one_selected()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(node);
        pane.BeginRenameNode(node);
        node.RenameText = "novo.txt";
        var newPath = Path.Combine(Path.GetDirectoryName(file)!, "novo.txt");

        pane.CommitRename();

        var renamed = PaneTestHarness.Find(pane, newPath);
        renamed.Should().NotBeNull("a pasta foi recarregada e o item novo está na árvore");
        pane.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(renamed);
        renamed!.IsItemSelected.Should().BeTrue();
        pane.SelectedNode.Should().BeSameAs(renamed, "a seleção não pode ir para a pasta que contém o item");
        pane.GetSelectedItemPath().Should().Be(newPath);
    }

    [Fact]
    public void After_renaming_a_folder_the_folder_with_the_new_name_is_selected()
    {
        var (pane, _, folder) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, folder)!;
        pane.SetSingleItemSelection(node);
        pane.BeginRenameNode(node);
        node.RenameText = "pasta-nova";
        var newPath = Path.Combine(Path.GetDirectoryName(folder)!, "pasta-nova");

        pane.CommitRename();

        var renamed = PaneTestHarness.Find(pane, newPath);
        renamed.Should().NotBeNull();
        pane.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(renamed);
        pane.GetSelectedItemPath().Should().Be(newPath);
    }

    [Fact]
    public void The_old_name_is_not_left_selected_after_renaming()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.SetSingleItemSelection(node);
        pane.BeginRenameNode(node);
        node.RenameText = "outro.txt";

        pane.CommitRename();

        pane.SelectedItems.Select(item => item.TargetPath).Should().NotContain(file);
        PaneTestHarness.Find(pane, file).Should().BeNull("o nome antigo não existe mais");
    }

    [Fact]
    public void Renaming_announces_the_new_path_so_the_screen_can_give_it_the_focus()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        var announced = new List<string>();
        pane.ItemRenamed += (_, e) => announced.Add(e.NewPath);
        pane.BeginRenameNode(node);
        node.RenameText = "avisado.txt";

        pane.CommitRename();

        announced.Should().Equal(Path.Combine(Path.GetDirectoryName(file)!, "avisado.txt"));
    }

    [Fact]
    public void Cancelling_or_keeping_the_same_name_announces_nothing()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        var announced = 0;
        pane.ItemRenamed += (_, _) => announced++;

        pane.BeginRenameNode(node);
        node.RenameText = "descartado.txt";
        pane.CancelRename();
        pane.BeginRenameNode(node);
        pane.CommitRename();

        announced.Should().Be(0);
    }

    [Fact]
    public void Cancelling_leaves_the_name_untouched()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.BeginRenameNode(node);
        node.RenameText = "outro.txt";

        pane.CancelRename();

        File.Exists(file).Should().BeTrue();
        node.IsRenaming.Should().BeFalse();
        node.RenameText.Should().Be("a.txt");
        pane.RenamingNode.Should().BeNull();
    }

    [Fact]
    public void Confirming_the_same_name_changes_nothing()
    {
        var (pane, file, _) = PaneWithFileAndFolder();
        var node = PaneTestHarness.Find(pane, file)!;
        pane.BeginRenameNode(node);

        pane.CommitRename();

        File.Exists(file).Should().BeTrue();
        pane.RenamingNode.Should().BeNull();
    }
}
