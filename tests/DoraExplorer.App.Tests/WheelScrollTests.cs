using DoraExplorer.App.Controls;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// A roda do mouse anda sempre o mesmo número de linhas por clique, 2, no modo compacto e no confortável. Antes o passo era
/// fixo em 64 px (2 linhas de 32 px): no modo confortável a linha de arquivo mede 54 px, então os mesmos 64 px eram só 1,2
/// linha e a lista parecia rolar mais devagar.
/// </summary>
public sealed class WheelScrollTests
{
    private const double CompactRow = 32;
    private const double ComfortableFileRow = 54;
    private const double TopPadding = 4;

    [Theory]
    [InlineData(120, 32, -64)]
    [InlineData(-120, 32, 64)]
    [InlineData(120, 54, -108)]
    [InlineData(-120, 54, 108)]
    [InlineData(240, 32, -128)]
    [InlineData(60, 54, -54)]
    [InlineData(-30, 32, 16)]
    public void A_notch_moves_two_rows_and_the_sign_follows_the_wheel(int delta, double rowHeight, double expected)
    {
        WheelScroll.StepPixels(delta, rowHeight).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(CompactRow)]
    [InlineData(ComfortableFileRow)]
    [InlineData(40.5)]
    public void Every_row_height_moves_the_same_number_of_rows_per_notch(double rowHeight)
    {
        (Math.Abs(WheelScroll.StepPixels(120, rowHeight)) / rowHeight).Should().BeApproximately(WheelScroll.RowsPerNotch, 1e-9);
        WheelScroll.RowsPerNotch.Should().Be(2, "o compacto sempre andou 2 linhas por clique");
    }

    [Fact]
    public void The_compact_step_is_the_same_64_pixels_as_before()
    {
        WheelScroll.StepPixels(-120, CompactRow).Should().Be(64);
    }

    [Fact]
    public void The_row_at_the_top_is_found_by_adding_up_the_heights_of_the_rows_above_it()
    {
        // pasta (32), pasta (32), arquivo (54), arquivo (54), pasta (32)
        double[] rows = [32, 32, 54, 54, 32];

        double At(double offset) => WheelScroll.RowHeightAtOffset(rows, offset, TopPadding, fallback: 99);

        At(0).Should().Be(32, "o começo da lista é a primeira linha");
        At(TopPadding + 31.9).Should().Be(32);
        At(TopPadding + 32).Should().Be(32, "a segunda pasta");
        At(TopPadding + 63.9).Should().Be(32);
        At(TopPadding + 64).Should().Be(54, "o primeiro arquivo começa depois de duas pastas");
        At(TopPadding + 117.9).Should().Be(54);
        At(TopPadding + 118).Should().Be(54, "o segundo arquivo");
        At(TopPadding + 172).Should().Be(32, "a última pasta");
    }

    [Fact]
    public void Past_the_end_of_the_list_the_last_row_height_is_used_and_an_empty_list_uses_the_fallback()
    {
        WheelScroll.RowHeightAtOffset([32, 54], 10_000, TopPadding, fallback: 99).Should().Be(54);
        WheelScroll.RowHeightAtOffset([], 100, TopPadding, fallback: 99).Should().Be(99);
        WheelScroll.RowHeightAtOffset([32, 54], -50, TopPadding, fallback: 99).Should().Be(32, "um deslocamento negativo fica na primeira linha");
    }

    [Fact]
    public void Only_the_rows_up_to_the_offset_are_read_so_a_huge_folder_does_not_cost_a_full_pass()
    {
        var read = 0;
        IEnumerable<double> Rows()
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                read++;
                yield return 54;
            }
        }

        WheelScroll.RowHeightAtOffset(Rows(), TopPadding + (54 * 10) + 1, TopPadding, fallback: 99).Should().Be(54);

        read.Should().BeLessThan(20);
    }
}

/// <summary>A altura da linha do topo, tirada da árvore de verdade: pasta 32 px, arquivo com rodapé 54 px no confortável.</summary>
public sealed class WheelRowHeightTests
{
    private const double TreePaddingTop = 4;
    private static readonly double FolderRow = TreeViewHierarchy.HeaderRowHeight;
    private static readonly double FileRow = TreeViewHierarchy.HeaderRowHeight + TreeScrollHelper.MetadataFooterHeight;

    private static NavigationTreeNodeViewModel Folder(string name, bool expanded = true) =>
        new(name, NavigationNodeKind.Directory, @"C:\" + name) { IsExpanded = expanded };

    private static NavigationTreeNodeViewModel File(string name, bool withMetadata = true) =>
        new(name, NavigationNodeKind.File, @"C:\x\" + name)
        {
            SizeInBytes = withMetadata ? 1 : null,
            LastModified = withMetadata ? new DateTime(2026, 1, 1) : null
        };

    /// <summary>pasta, subpasta (fechada, com 3 arquivos), 2 arquivos, outra pasta.</summary>
    private static NavigationTreeNodeViewModel Sample()
    {
        var root = Folder("raiz");
        var closed = Folder("fechada", expanded: false);
        closed.Children.Add(File("escondido1.txt"));
        closed.Children.Add(File("escondido2.txt"));
        closed.Children.Add(File("escondido3.txt"));
        root.Children.Add(closed);
        root.Children.Add(File("a.txt"));
        root.Children.Add(File("b.txt"));
        root.Children.Add(Folder("ultima"));
        return root;
    }

    private static readonly TreeRowMetrics Comfortable = new(ComfortableRows: true);
    private static readonly TreeRowMetrics Compact = new(ComfortableRows: false);

    [Fact]
    public void In_compact_rows_every_position_uses_the_plain_row_height_even_over_files_with_metadata()
    {
        var root = Sample();

        foreach (var offset in new[] { 0.0, 40, 120, 200, 400 })
        {
            TreeScrollHelper.WheelRowHeight([root], offset, Compact).Should().Be(FolderRow);
        }
    }

    [Fact]
    public void In_comfortable_rows_the_height_follows_whether_the_row_at_the_top_is_a_folder_or_a_file()
    {
        var root = Sample();

        double At(double rowsAboveHeight) => TreeScrollHelper.WheelRowHeight([root], TreePaddingTop + rowsAboveHeight + 1, Comfortable);

        At(0).Should().Be(FolderRow, "a pasta raiz");
        At(FolderRow).Should().Be(FolderRow, "a subpasta fechada");
        At(FolderRow * 2).Should().Be(FileRow, "a.txt: os arquivos da pasta fechada não contam");
        At((FolderRow * 2) + FileRow).Should().Be(FileRow, "b.txt");
        At((FolderRow * 2) + (FileRow * 2)).Should().Be(FolderRow, "a última pasta");
    }

    [Fact]
    public void An_expanded_folder_adds_its_children_rows_and_a_placeholder_is_skipped()
    {
        var root = Folder("raiz");
        var open = Folder("aberta");
        root.Children.Add(open);
        open.Children.Add(new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
        open.Children.Add(File("dentro.txt"));
        root.Children.Add(File("fora.txt"));

        // raiz (32), aberta (32), placeholder (nada), dentro.txt (54), fora.txt (54)
        double At(double above) => TreeScrollHelper.WheelRowHeight([root], TreePaddingTop + above + 1, Comfortable);

        At(FolderRow * 2).Should().Be(FileRow, "dentro.txt vem logo depois da pasta aberta");
        At((FolderRow * 2) + FileRow).Should().Be(FileRow, "fora.txt");
    }

    [Fact]
    public void A_file_without_size_or_date_has_no_footer_so_its_row_is_the_plain_height()
    {
        var root = Folder("raiz");
        root.Children.Add(File("sem-dados.txt", withMetadata: false));

        TreeScrollHelper.WheelRowHeight([root], TreePaddingTop + FolderRow + 1, Comfortable).Should().Be(FolderRow);
    }

    [Fact]
    public void Both_modes_move_two_rows_per_notch_over_a_list_of_files()
    {
        var root = Folder("raiz");
        for (var i = 0; i < 50; i++)
        {
            root.Children.Add(File($"f{i}.txt"));
        }

        var offset = TreePaddingTop + FolderRow + (FileRow * 10) + 1;
        var compact = Math.Abs(WheelScroll.StepPixels(-120, TreeScrollHelper.WheelRowHeight([root], offset, Compact)));
        var comfortable = Math.Abs(WheelScroll.StepPixels(-120, TreeScrollHelper.WheelRowHeight([root], offset, Comfortable)));

        compact.Should().Be(2 * FolderRow, "o compacto continua andando os mesmos 64 px de sempre");
        comfortable.Should().Be(2 * FileRow, "no confortável o clique agora cobre duas linhas de arquivo, não 1,2");
        (comfortable / FileRow).Should().Be(compact / FolderRow);
    }
}
