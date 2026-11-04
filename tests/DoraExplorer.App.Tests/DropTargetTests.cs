using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Soltar itens arrastados: qual linha está sob o mouse (<see cref="RowHitTest"/>), para onde o arrasto vai (unidade aberta, pasta,
/// arquivo, espaço em branco), a pasta destacada durante o arrasto e as regras que recusam soltar na pasta de onde os itens vieram.
/// </summary>
[Collection("App statics")]
public sealed class DropTargetTests : IDisposable
{
    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- onde está o mouse: cada linha tem a sua faixa (Top, Bottom); filhos aparecem logo abaixo da linha da pasta aberta.

    private sealed record Row(string Name, double Top, double Bottom, params Row[] Kids);

    private static string? Hit(IEnumerable<Row> rows, double y) =>
        RowHitTest.FindExact(rows, y, row => (row.Top, row.Bottom), row => row.Kids)?.Name;

    private static Row[] OpenDriveWithTwoFolders() =>
    [
        new("C:", 0, 28,
            new Row("Arquivos", 28, 56),
            new Row("Usuarios", 56, 84)),
        new("D:", 84, 112)
    ];

    [Theory]
    [InlineData(10, "C:")]
    [InlineData(28, "C:")]
    [InlineData(40, "Arquivos")]
    [InlineData(70, "Usuarios")]
    [InlineData(100, "D:")]
    public void The_row_under_the_mouse_is_the_one_whose_own_band_holds_the_height(double y, string expected)
    {
        Hit(OpenDriveWithTwoFolders(), y).Should().Be(expected);
    }

    [Fact]
    public void The_line_of_an_open_drive_is_the_drive_and_not_its_nearest_folder()
    {
        // O defeito: com a unidade aberta, o mouse na linha "C:" devolvia "Arquivos" (o filho mais próximo) e soltar ia para dentro dele.
        Hit(OpenDriveWithTwoFolders(), 14).Should().Be("C:");
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(112.5)]
    [InlineData(400)]
    public void Blank_space_above_or_below_the_rows_is_no_row_at_all(double y)
    {
        Hit(OpenDriveWithTwoFolders(), y).Should().BeNull();
    }

    [Fact]
    public void An_empty_tree_has_no_row()
    {
        Hit([], 10).Should().BeNull();
    }

    // ---- para onde o arrasto vai: a linha vira pasta de destino.

    private static NavigationTreeNodeViewModel Node(NavigationNodeKind kind, string path) => new(Path.GetFileName(path), kind, path);

    [Fact]
    public void A_drive_row_receives_the_items_at_the_root_of_the_drive()
    {
        var pane = _h.CreatePane(_h.Folder("destino"));
        var root = Path.GetPathRoot(_h.Data)!;

        pane.TryResolveDropDirectory(Node(NavigationNodeKind.Drive, root), out var directory, out var error).Should().BeTrue(error);

        directory.Should().Be(root, "soltar na linha da unidade grava na raiz dela, não numa subpasta");
    }

    [Fact]
    public void A_folder_row_receives_the_items_inside_that_folder()
    {
        var pane = _h.CreatePane(_h.Folder("aberta"));
        var folder = _h.Folder("outra");

        pane.TryResolveDropDirectory(Node(NavigationNodeKind.Directory, folder), out var directory, out _).Should().BeTrue();

        directory.Should().Be(folder);
    }

    [Fact]
    public void A_file_row_sends_the_items_to_the_folder_that_holds_the_file()
    {
        var folder = _h.Folder("com-arquivo");
        var file = PaneTestHarness.File(folder, "a.txt");
        var pane = _h.CreatePane(_h.Folder("aberta"));

        pane.TryResolveDropDirectory(Node(NavigationNodeKind.File, file), out var directory, out _).Should().BeTrue();

        directory.Should().Be(folder);
    }

    [Fact]
    public void Blank_space_sends_the_items_to_the_folder_open_in_the_pane()
    {
        var open = _h.Folder("aberta");
        var pane = _h.CreatePane(open);
        pane.CompleteInitialPathRestore();

        pane.TryResolveDropDirectory(null, out var directory, out _).Should().BeTrue();

        directory.Should().Be(open);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_refused_with_a_reason()
    {
        var pane = _h.CreatePane(_h.Folder("aberta"));
        var missing = Path.Combine(_h.Data, "nao-existe");

        pane.TryResolveDropDirectory(Node(NavigationNodeKind.Directory, missing), out _, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    // ---- destino destacado durante o arrasto

    private (FilePaneViewModel Pane, string Folder) PaneShowing(string folderName)
    {
        var parent = _h.Folder("pai");
        var folder = Directory.CreateDirectory(Path.Combine(parent, folderName)).FullName;
        var pane = _h.CreatePane(parent);
        pane.CompleteInitialPathRestore();
        return (pane, folder);
    }

    [Fact]
    public void The_folder_that_would_receive_the_items_is_highlighted_and_only_that_one()
    {
        var parent = _h.Folder("pai-de-duas");
        var first = Directory.CreateDirectory(Path.Combine(parent, "primeira")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(parent, "segunda")).FullName;
        var pane = _h.CreatePane(parent);
        pane.CompleteInitialPathRestore();
        pane.NavigateToPath(parent);
        PaneTestHarness.Find(pane, first).Should().NotBeNull();
        PaneTestHarness.Find(pane, second).Should().NotBeNull();

        pane.SetDropTarget(first);
        var firstNode = PaneTestHarness.Find(pane, first)!;
        firstNode.IsDropTarget.Should().BeTrue();
        pane.DropTargetNode.Should().BeSameAs(firstNode);

        pane.SetDropTarget(second);

        firstNode.IsDropTarget.Should().BeFalse("o destaque sai da pasta anterior ao passar para outra");
        PaneTestHarness.Find(pane, second)!.IsDropTarget.Should().BeTrue();
    }

    [Fact]
    public void Clearing_the_target_removes_the_highlight_and_a_folder_the_pane_does_not_show_highlights_nothing()
    {
        var (pane, folder) = PaneShowing("destino");
        pane.NavigateToPath(Path.GetDirectoryName(folder)!);
        pane.SetDropTarget(folder);

        pane.ClearDropTarget();

        PaneTestHarness.Find(pane, folder)!.IsDropTarget.Should().BeFalse();
        pane.DropTargetNode.Should().BeNull();

        pane.SetDropTarget(Path.Combine(_h.Data, "pasta-que-o-painel-nao-mostra"));
        pane.DropTargetNode.Should().BeNull();
    }

    [Fact]
    public void A_file_is_never_highlighted_as_the_target()
    {
        var parent = _h.Folder("pai-do-arquivo");
        var file = PaneTestHarness.File(parent, "a.txt");
        var pane = _h.CreatePane(parent);
        pane.CompleteInitialPathRestore();
        pane.NavigateToPath(parent);

        pane.SetDropTarget(file);

        pane.DropTargetNode.Should().BeNull("o destino é sempre uma pasta ou unidade");
    }

    [Fact]
    public void Highlighting_the_same_folder_twice_changes_nothing()
    {
        var (pane, folder) = PaneShowing("repetida");
        pane.NavigateToPath(Path.GetDirectoryName(folder)!);

        pane.SetDropTarget(folder);
        var node = pane.DropTargetNode;
        pane.SetDropTarget(folder);

        pane.DropTargetNode.Should().BeSameAs(node);
        node!.IsDropTarget.Should().BeTrue();
    }

    // ---- soltar na raiz de uma unidade passa pelas mesmas regras de qualquer destino

    [Fact]
    public void Dropping_a_folder_on_the_root_of_its_own_drive_moves_it_and_never_into_itself()
    {
        // Não toca na raiz de verdade: só confere as regras de validação, que não escrevem nada.
        var main = _h.CreateMain();
        var folder = _h.Folder("pasta-qualquer");
        var root = Path.GetPathRoot(_h.Data)!;

        main.TryValidateTransfer([folder], root, out var error).Should().BeTrue(error, "a raiz da unidade é um destino válido para uma pasta de dentro dela");
        main.TryValidateTransfer([root], folder, out _).Should().BeFalse("a raiz não pode ir para dentro de uma subpasta dela mesma");
    }

    [Fact]
    public void Dropping_an_item_that_already_sits_at_the_root_on_that_root_is_refused()
    {
        var main = _h.CreateMain();
        var root = Path.GetPathRoot(_h.Data)!;
        var topLevel = Directory.EnumerateFileSystemEntries(root).FirstOrDefault();
        if (topLevel is null)
        {
            return; // unidade sem nada na raiz: nada a conferir
        }

        main.TryValidateTransfer([topLevel], root, out var error).Should().BeFalse();

        error.Should().Contain("já está");
    }
}
