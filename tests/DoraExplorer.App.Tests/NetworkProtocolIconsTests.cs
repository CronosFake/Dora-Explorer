using System.Windows.Media;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Os desenhos do SMB (janela) e do NFS (pinguim): formas dentro da grade de 16x16, uma cor só e uma versão colorida.</summary>
public sealed class NetworkProtocolIconsTests
{
    [Fact]
    public void The_marks_are_frozen_and_stay_inside_the_16_by_16_grid()
    {
        foreach (var mark in new[] { NetworkProtocolIcons.SmbMark, NetworkProtocolIcons.NfsMark })
        {
            mark.IsFrozen.Should().BeTrue("congelado vale em qualquer thread");
            var bounds = mark.Bounds;
            bounds.Left.Should().BeGreaterThanOrEqualTo(0);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0);
            bounds.Right.Should().BeLessThanOrEqualTo(16);
            bounds.Bottom.Should().BeLessThanOrEqualTo(16);
            bounds.Width.Should().BeGreaterThan(10, "tem que ocupar a grade para ficar legível em 15 px");
            bounds.Height.Should().BeGreaterThan(12);
        }
    }

    [Fact]
    public void The_penguin_is_filled_even_odd_so_the_belly_eyes_and_beak_are_cut_out()
    {
        // Geometry.Parse devolve um StreamGeometry.
        ((StreamGeometry)NetworkProtocolIcons.NfsMark).FillRule.Should().Be(FillRule.EvenOdd);
        var mark = NetworkProtocolIcons.NfsMark;
        mark.FillContains(new System.Windows.Point(8, 10.5)).Should().BeFalse("a barriga é vazada");
        mark.FillContains(new System.Windows.Point(8, 4.8)).Should().BeFalse("o bico é vazado");
        mark.FillContains(new System.Windows.Point(6.7, 2.9)).Should().BeFalse("o olho esquerdo é vazado");
        mark.FillContains(new System.Windows.Point(9.3, 2.9)).Should().BeFalse("o olho direito é vazado");
        mark.FillContains(new System.Windows.Point(8, 2.0)).Should().BeTrue("a cabeça é cheia");
        mark.FillContains(new System.Windows.Point(4.2, 11)).Should().BeTrue("o corpo ao redor da barriga é cheio");
    }

    [Fact]
    public void The_window_is_four_separate_panes()
    {
        var window = NetworkProtocolIcons.SmbMark;

        window.GetFlattenedPathGeometry().Figures.Should().HaveCount(4);
        foreach (var point in new[] { (4.0, 4.5), (11.8, 4.0), (4.0, 12.0), (11.8, 11.5) })
        {
            window.FillContains(new System.Windows.Point(point.Item1, point.Item2)).Should().BeTrue();
        }

        window.FillContains(new System.Windows.Point(8, 8)).Should().BeFalse("o espaço entre as vidraças é vazio");
    }

    [Fact]
    public void The_tree_icons_are_frozen_16_by_16_images_and_are_not_the_logo_colors()
    {
        foreach (var icon in new[] { NetworkProtocolIcons.SmbIcon, NetworkProtocolIcons.NfsIcon })
        {
            icon.IsFrozen.Should().BeTrue();
            icon.Width.Should().Be(16);
            icon.Height.Should().Be(16);
        }

        var colors = ((DrawingGroup)((DrawingImage)NetworkProtocolIcons.SmbIcon).Drawing).Children
            .OfType<GeometryDrawing>()
            .Select(drawing => (drawing.Brush as SolidColorBrush)?.Color)
            .Where(color => color is { A: > 0 })
            .Select(color => color!.Value)
            .ToList();
        colors.Should().HaveCount(4);
        colors.Should().NotContain(Color.FromRgb(0xF2, 0x50, 0x22), "o vermelho do logotipo da Microsoft não é usado");
        colors.Should().NotContain(Color.FromRgb(0x7F, 0xBA, 0x00));
        colors.Should().NotContain(Color.FromRgb(0xFF, 0xB9, 0x00));
        colors.Should().OnlyContain(color => color.B > color.R, "as vidraças são azuis");
    }

    [Fact]
    public void Only_smb_and_nfs_have_their_own_tree_icon()
    {
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Smb).Should().BeSameAs(NetworkProtocolIcons.SmbIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Nfs).Should().BeSameAs(NetworkProtocolIcons.NfsIcon);
        foreach (var other in new[] { NetworkProtocol.Ftp, NetworkProtocol.Sftp, NetworkProtocol.Scp, NetworkProtocol.Tftp })
        {
            NetworkProtocolIcons.TreeIconFor(other).Should().BeNull();
        }
    }
}
