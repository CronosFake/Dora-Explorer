using DoraExplorer.App.Controls;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Duplo clique num resultado do Localizar ou na janela de Duplicados: a pasta abre, o ARQUIVO fica selecionado e aparece mais ou
/// menos no meio da lista. O que decide isso fora da tela: o arquivo existir como nó mesmo com a pasta ainda sendo preenchida em
/// lotes (pasta grande), só ele ficar selecionado, e a estimativa de onde ele fica na lista (altura das linhas).
/// </summary>
[Collection("App statics")]
public sealed class SearchNavigationTests : IDisposable
{
    // Acima do tamanho do primeiro lote do preenchimento (2000): sobra um lote pendente que só entra na árvore depois.
    private const int FilesInBigFolder = 2300;

    private readonly PaneTestHarness _h = new();
    private readonly Queue<Action> _pendingBatches = new();

    public SearchNavigationTests()
    {
        // Os lotes do preenchimento ficam numa fila que o teste esvazia quando quer: reproduz "a pasta ainda está sendo
        // preenchida" sem janela nem Dispatcher.
        _h.Tree.ChunkedFillScheduler = batch => _pendingBatches.Enqueue(batch);
    }

    public void Dispose() => _h.Dispose();

    private void RunPendingBatches()
    {
        while (_pendingBatches.TryDequeue(out var batch))
        {
            batch();
        }
    }

    private string BigFolderWithFiles(string name, string? uniqueFile = null)
    {
        var folder = _h.Folder(name);
        for (var i = 1; i <= FilesInBigFolder; i++)
        {
            PaneTestHarness.File(folder, $"IMG_{i:D4}.JPG", "x");
        }

        if (uniqueFile is not null)
        {
            PaneTestHarness.File(folder, uniqueFile, "dados");
        }

        return folder;
    }

    private static SearchResultItem Result(string path, bool isDirectory = false) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsDirectory = isDirectory
    };

    private FilePaneViewModel PaneAt(string path)
    {
        var pane = _h.CreatePane(path);
        pane.CompleteInitialPathRestore();
        return pane;
    }

    // ---- pasta grande: o arquivo existe como nó e fica selecionado

    [Fact]
    public void A_file_in_a_big_folder_still_being_filled_is_found_and_selected()
    {
        var folder = BigFolderWithFiles("fotos");
        var target = Path.Combine(folder, $"IMG_{FilesInBigFolder:D4}.JPG"); // o último: fora do primeiro lote
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(target), _h.Data);

        pane.SelectedNode.Should().NotBeNull();
        pane.SelectedNode!.TargetPath.Should().Be(target, "o arquivo é que fica selecionado, não a pasta onde ele está");
        pane.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(pane.SelectedNode);
        pane.CurrentPath.Should().Be(folder);
    }

    [Fact]
    public void Navigating_finishes_the_pending_fill_so_the_selected_node_is_the_one_that_stays_in_the_tree()
    {
        var folder = BigFolderWithFiles("fotos");
        var target = Path.Combine(folder, $"IMG_{FilesInBigFolder:D4}.JPG");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(target), _h.Data);
        var selected = pane.SelectedNode;
        var parent = PaneTestHarness.Find(pane, folder)!;

        parent.Children.Should().HaveCount(FilesInBigFolder, "a pasta inteira entrou na árvore de uma vez");

        // os lotes que ainda estavam na fila não podem duplicar nem trocar os filhos
        RunPendingBatches();

        parent.Children.Should().HaveCount(FilesInBigFolder);
        PaneTestHarness.Find(pane, target).Should().BeSameAs(selected);
    }

    [Fact]
    public void A_folder_inside_a_big_folder_still_being_filled_is_found_on_the_way_to_the_file()
    {
        // A pasta do caminho (e não só o arquivo) pode estar numa pasta grande ainda em preenchimento.
        var big = _h.Folder("muitas-pastas");
        for (var i = 1; i <= 2100; i++)
        {
            Directory.CreateDirectory(Path.Combine(big, $"pasta{i:D4}"));
        }

        var last = Path.Combine(big, "pasta2100"); // fora do primeiro lote
        var target = PaneTestHarness.File(last, "alvo.txt");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(target), _h.Data);

        pane.SelectedNode!.TargetPath.Should().Be(target);
        pane.SelectedItems.Should().ContainSingle();
        PaneTestHarness.Find(pane, big)!.Children.Should().HaveCount(2100, "a pasta grande entrou inteira, não só o filho do caminho");
    }

    [Fact]
    public void Opening_a_path_through_a_big_folder_still_being_filled_reaches_the_end_of_the_path()
    {
        var big = _h.Folder("muitas-pastas");
        for (var i = 1; i <= 2100; i++)
        {
            Directory.CreateDirectory(Path.Combine(big, $"pasta{i:D4}"));
        }

        var last = Path.Combine(big, "pasta2100"); // fora do primeiro lote
        var pane = PaneAt(_h.Data);

        pane.NavigateToPath(last);

        pane.SelectedNode.Should().NotBeNull();
        pane.SelectedNode!.TargetPath.Should().Be(last, "antes parava na pasta grande e nunca chegava na pasta do caminho");
    }

    [Fact]
    public void A_big_folder_found_as_a_result_opens_with_everything_in_it()
    {
        var folder = BigFolderWithFiles("fotos");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(folder, isDirectory: true), _h.Data);

        pane.SelectedNode!.TargetPath.Should().Be(folder);
        RunPendingBatches();
        PaneTestHarness.Find(pane, folder)!.Children.Should().HaveCount(FilesInBigFolder);
    }

    [Fact]
    public void Navigating_to_a_file_leaves_only_that_file_selected()
    {
        var folder = BigFolderWithFiles("fotos");
        var first = Path.Combine(folder, "IMG_0001.JPG");
        var second = Path.Combine(folder, "IMG_0002.JPG");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(first), _h.Data);
        pane.SelectedItems.Should().ContainSingle().Which.TargetPath.Should().Be(first);

        pane.NavigateToSearchResult(Result(second), _h.Data);

        pane.SelectedItems.Should().ContainSingle().Which.TargetPath.Should().Be(second, "o arquivo anterior sai da seleção");
        pane.SelectedNode!.TargetPath.Should().Be(second);
    }

    [Fact]
    public void Navigating_to_a_file_in_another_folder_clears_the_selection_of_the_first_folder()
    {
        var one = _h.Folder("um");
        var two = _h.Folder("dois");
        var inOne = PaneTestHarness.File(one, "a.txt");
        var inTwo = PaneTestHarness.File(two, "b.txt");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(inOne), _h.Data);
        pane.NavigateToSearchResult(Result(inTwo), _h.Data);

        pane.SelectedItems.Should().ContainSingle().Which.TargetPath.Should().Be(inTwo);
    }

    [Fact]
    public void Expanding_a_sibling_folder_clears_the_selection_of_a_file_next_to_it()
    {
        // O acordeão (abrir uma pasta fecha as outras) também limpa a seleção de um arquivo vizinho: com a pasta cheia de
        // arquivos o laço pula os arquivos, e este caso tem de continuar valendo.
        var folder = _h.Folder("acordeao");
        PaneTestHarness.File(folder, "vizinho.txt");
        var sub = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        PaneTestHarness.File(sub, "dentro.txt");
        var pane = PaneAt(folder);
        pane.NavigateToPath(folder);
        var file = PaneTestHarness.Find(pane, Path.Combine(folder, "vizinho.txt"))!;
        var subNode = PaneTestHarness.Find(pane, sub)!;
        pane.SetSingleItemSelection(file);
        pane.SelectedItems.Should().ContainSingle();

        pane.OnNavigationNodeExpanding(subNode, userInitiated: true);

        pane.SelectedItems.Should().BeEmpty();
    }

    [Fact]
    public void The_fill_of_a_small_folder_is_not_left_pending()
    {
        // Pasta pequena (abaixo do limite): é construída de uma vez, sem fila.
        var folder = _h.Folder("pequena");
        var target = PaneTestHarness.File(folder, "a.txt");
        var pane = PaneAt(_h.Data);

        pane.NavigateToSearchResult(Result(target), _h.Data);

        _pendingBatches.Should().BeEmpty();
        pane.SelectedNode!.TargetPath.Should().Be(target);
    }

    // ---- terminar o preenchimento na hora (o serviço)

    [Fact]
    public void Completing_a_chunked_fill_puts_every_child_in_the_node_at_once()
    {
        var folder = BigFolderWithFiles("fotos");
        var pane = PaneAt(_h.Data);
        pane.NavigateToPath(folder);
        var node = PaneTestHarness.Find(pane, folder)!;

        node.Children.Should().BeEmpty("os filhos só entram na árvore no fim do preenchimento em lotes");
        node.CompleteChunkedFillNow.Should().NotBeNull();

        _h.Tree.CompleteChunkedFill(node).Should().BeTrue();

        node.Children.Should().HaveCount(FilesInBigFolder);
        node.CompleteChunkedFillNow.Should().BeNull("já terminou: não há mais o que forçar");
        _h.Tree.CompleteChunkedFill(node).Should().BeFalse();
    }

    [Fact]
    public void Completing_a_fill_that_is_not_pending_does_nothing()
    {
        var folder = _h.Folder("pequena");
        PaneTestHarness.File(folder, "a.txt");
        var pane = PaneAt(_h.Data);
        pane.NavigateToPath(folder);
        var node = PaneTestHarness.Find(pane, folder)!;

        _h.Tree.CompleteChunkedFill(node).Should().BeFalse();
        node.Children.Should().ContainSingle();
    }

    [Fact]
    public void A_fill_forced_to_finish_is_not_applied_again_by_the_queued_batches()
    {
        var folder = BigFolderWithFiles("fotos");
        var pane = PaneAt(_h.Data);
        pane.NavigateToPath(folder);
        var node = PaneTestHarness.Find(pane, folder)!;
        _h.Tree.CompleteChunkedFill(node);
        var first = node.Children[0];
        var changes = 0;
        node.Children.CollectionChanged += (_, _) => changes++;

        RunPendingBatches();

        changes.Should().Be(0, "o lote atrasado não mexe mais na coleção (não há segunda troca da lista)");
        node.Children.Should().HaveCount(FilesInBigFolder);
        node.Children[0].Should().BeSameAs(first, "o lote atrasado não troca os nós que já estão na árvore");
    }

    [Fact]
    public void A_stale_forced_finish_after_the_folder_was_reloaded_does_not_touch_the_new_children()
    {
        var folder = BigFolderWithFiles("fotos");
        var pane = PaneAt(_h.Data);
        pane.NavigateToPath(folder);
        var node = PaneTestHarness.Find(pane, folder)!;
        var stale = node.CompleteChunkedFillNow!;

        node.LoadGeneration++; // a pasta foi recarregada: o preenchimento antigo perdeu a vez
        node.Children.Clear();
        stale();

        node.Children.Should().BeEmpty("o preenchimento antigo não escreve numa coleção que já é de outra carga");
        node.CompleteChunkedFillNow.Should().BeNull();
    }

    // ---- estimativa da posição do arquivo na lista (altura das linhas)

    private static (NavigationTreeNodeViewModel Root, List<NavigationTreeNodeViewModel> Files) FolderWithFiles(int count, bool withMetadata = true)
    {
        var root = new NavigationTreeNodeViewModel("pasta", NavigationNodeKind.Directory, @"C:\pasta") { IsExpanded = true };
        var files = new List<NavigationTreeNodeViewModel>();
        for (var i = 0; i < count; i++)
        {
            var file = new NavigationTreeNodeViewModel($"f{i}.jpg", NavigationNodeKind.File, $@"C:\pasta\f{i}.jpg")
            {
                SizeInBytes = withMetadata ? 1 : null,
                LastModified = withMetadata ? new DateTime(2026, 1, 1) : null
            };
            files.Add(file);
            root.Children.Add(file);
        }

        return (root, files);
    }

    private static double OffsetOf(NavigationTreeNodeViewModel root, NavigationTreeNodeViewModel target, TreeRowMetrics metrics)
    {
        TreeScrollHelper.TryEstimateTargetOffset([root], target, metrics, out var offset).Should().BeTrue();
        return offset;
    }

    [Fact]
    public void Compact_rows_are_estimated_at_the_plain_row_height_with_no_footer()
    {
        var (root, files) = FolderWithFiles(2000);

        var distance = OffsetOf(root, files[1776], new TreeRowMetrics(ComfortableRows: false)) -
                       OffsetOf(root, files[0], new TreeRowMetrics(ComfortableRows: false));

        distance.Should().Be(1776 * TreeViewHierarchy.HeaderRowHeight, "no modo compacto (o padrão) o arquivo não tem rodapé");
    }

    [Fact]
    public void Comfortable_rows_use_the_measured_height_of_a_drawn_file_row()
    {
        var (root, files) = FolderWithFiles(2000);
        var metrics = new TreeRowMetrics(ComfortableRows: true, FileRowHeight: 47.97);

        var distance = OffsetOf(root, files[1776], metrics) - OffsetOf(root, files[0], metrics);

        distance.Should().BeApproximately(1776 * 47.97, 0.001);
    }

    [Fact]
    public void Comfortable_rows_without_a_measured_row_add_the_footer_height()
    {
        var (root, files) = FolderWithFiles(2000);
        var metrics = new TreeRowMetrics(ComfortableRows: true);

        var distance = OffsetOf(root, files[1776], metrics) - OffsetOf(root, files[0], metrics);

        distance.Should().Be(1776 * (TreeViewHierarchy.HeaderRowHeight + TreeScrollHelper.MetadataFooterHeight));
        (TreeViewHierarchy.HeaderRowHeight + TreeScrollHelper.MetadataFooterHeight).Should().Be(48, "a linha confortável mede 48 px na tela");
    }

    [Fact]
    public void A_file_without_size_or_date_has_no_footer_even_with_comfortable_rows()
    {
        var (root, files) = FolderWithFiles(10, withMetadata: false);
        var metrics = new TreeRowMetrics(ComfortableRows: true, FileRowHeight: 48);

        var distance = OffsetOf(root, files[5], metrics) - OffsetOf(root, files[0], metrics);

        distance.Should().Be(5 * TreeViewHierarchy.HeaderRowHeight);
    }

    [Fact]
    public void A_folder_row_keeps_the_plain_height_in_comfortable_rows()
    {
        var folder = new NavigationTreeNodeViewModel("sub", NavigationNodeKind.Directory, @"C:\pasta\sub");

        TreeScrollHelper.GetEstimatedRowHeight(folder, new TreeRowMetrics(true, 48))
            .Should().Be(TreeViewHierarchy.HeaderRowHeight);
    }

    [Fact]
    public void A_collapsed_folder_does_not_count_the_rows_inside_it()
    {
        var (root, files) = FolderWithFiles(5);
        var sub = new NavigationTreeNodeViewModel("sub", NavigationNodeKind.Directory, @"C:\pasta\sub") { IsExpanded = false };
        sub.Children.Add(new NavigationTreeNodeViewModel("dentro.txt", NavigationNodeKind.File, @"C:\pasta\sub\dentro.txt"));
        root.Children.Insert(0, sub);
        var metrics = new TreeRowMetrics(ComfortableRows: false);

        (OffsetOf(root, files[0], metrics) - OffsetOf(root, sub, metrics))
            .Should().Be(TreeViewHierarchy.HeaderRowHeight, "só a linha da pasta fechada fica antes do primeiro arquivo");
    }
}
