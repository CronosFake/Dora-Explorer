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

    private static ImageSource[] AllTreeIcons() =>
        Enum.GetValues<NetworkProtocol>().Select(protocol => NetworkProtocolIcons.TreeIconFor(protocol)!).ToArray();

    /// <summary>Todas as cores visíveis do desenho (preenchimentos e traços), sem o retângulo transparente que fixa o tamanho.</summary>
    private static List<Color> VisibleColors(ImageSource icon)
    {
        var colors = new List<Color>();
        foreach (var drawing in ((DrawingGroup)((DrawingImage)icon).Drawing).Children.OfType<GeometryDrawing>())
        {
            if ((drawing.Brush as SolidColorBrush)?.Color is { A: > 0 } fill)
            {
                colors.Add(fill);
            }

            if ((drawing.Pen?.Brush as SolidColorBrush)?.Color is { A: > 0 } stroke)
            {
                colors.Add(stroke);
            }
        }

        return colors;
    }

    private static double Luminance(Color color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    [Fact]
    public void The_tree_icons_are_frozen_16_by_16_images_inside_the_grid()
    {
        foreach (var icon in AllTreeIcons())
        {
            icon.IsFrozen.Should().BeTrue();
            icon.Width.Should().Be(16);
            icon.Height.Should().Be(16);
            ((DrawingImage)icon).Drawing.Bounds.Left.Should().BeGreaterThanOrEqualTo(0);
            ((DrawingImage)icon).Drawing.Bounds.Top.Should().BeGreaterThanOrEqualTo(0);
            ((DrawingImage)icon).Drawing.Bounds.Right.Should().BeLessThanOrEqualTo(16);
            ((DrawingImage)icon).Drawing.Bounds.Bottom.Should().BeLessThanOrEqualTo(16);
        }
    }

    [Fact]
    public void The_smb_window_uses_blue_panes_and_not_the_logo_colors()
    {
        var colors = VisibleColors(NetworkProtocolIcons.SmbIcon);

        colors.Should().HaveCount(4);
        colors.Should().NotContain(Color.FromRgb(0xF2, 0x50, 0x22), "o vermelho do logotipo da Microsoft não é usado");
        colors.Should().NotContain(Color.FromRgb(0x7F, 0xBA, 0x00));
        colors.Should().NotContain(Color.FromRgb(0xFF, 0xB9, 0x00));
        colors.Should().OnlyContain(color => color.B > color.R, "as vidraças são azuis");
    }

    [Fact]
    public void Every_protocol_has_its_own_distinct_tree_icon()
    {
        var icons = AllTreeIcons();

        icons.Should().HaveCount(6).And.OnlyContain(icon => icon != null);
        icons.Distinct().Should().HaveCount(icons.Length, "dois protocolos nunca dividem o mesmo ícone");

        // Cores e tamanho do desenho também diferem: não é o mesmo desenho copiado em outro objeto.
        icons.Select(icon => string.Join(";", VisibleColors(icon)) + "|" + ((DrawingImage)icon).Drawing.Bounds)
            .Distinct().Should().HaveCount(icons.Length);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Ftp).Should().BeSameAs(NetworkProtocolIcons.FtpIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Sftp).Should().BeSameAs(NetworkProtocolIcons.SftpIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Scp).Should().BeSameAs(NetworkProtocolIcons.ScpIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Tftp).Should().BeSameAs(NetworkProtocolIcons.TftpIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Smb).Should().BeSameAs(NetworkProtocolIcons.SmbIcon);
        NetworkProtocolIcons.TreeIconFor(NetworkProtocol.Nfs).Should().BeSameAs(NetworkProtocolIcons.NfsIcon);
    }

    [Fact]
    public void Every_tree_icon_has_a_light_part_for_the_dark_theme_and_a_dark_part_for_the_light_theme()
    {
        foreach (var protocol in Enum.GetValues<NetworkProtocol>())
        {
            var luminances = VisibleColors(NetworkProtocolIcons.TreeIconFor(protocol)!).Select(Luminance).ToList();

            luminances.Should().NotBeEmpty();
            luminances.Max().Should().BeGreaterThanOrEqualTo(0.45, $"{protocol} precisa de uma parte clara para se ver no tema escuro");
            luminances.Min().Should().BeLessThanOrEqualTo(0.55, $"{protocol} precisa de uma parte escura para se ver no tema claro");
        }
    }

    [Fact]
    public void The_dark_shapes_have_a_light_rim_so_they_do_not_vanish_on_the_dark_theme()
    {
        // O corpo do pinguim e a janela do terminal são escuros: sem a borda clara sumiriam no fundo escuro.
        foreach (var icon in new[] { NetworkProtocolIcons.NfsIcon, NetworkProtocolIcons.ScpIcon })
        {
            var body = ((DrawingGroup)((DrawingImage)icon).Drawing).Children.OfType<GeometryDrawing>()
                .First(drawing => drawing.Brush is SolidColorBrush { Color.A: > 0 });

            body.Pen.Should().NotBeNull();
            Luminance(((SolidColorBrush)body.Pen!.Brush).Color).Should().BeGreaterThanOrEqualTo(0.45);
        }
    }

    [Fact]
    public void The_ftp_family_icons_share_the_folder_and_the_secure_one_adds_a_lock()
    {
        // FTP e SFTP são a mesma pasta; o SFTP acrescenta o cadeado verde (o que o diferencia à primeira vista).
        var ftp = VisibleColors(NetworkProtocolIcons.FtpIcon);
        var sftp = VisibleColors(NetworkProtocolIcons.SftpIcon);

        sftp.Take(2).Should().Equal(ftp.Take(2), "a pasta é a mesma");
        sftp.Should().Contain(Color.FromRgb(0x2F, 0xA8, 0x4F), "o cadeado do SFTP é verde");
        ftp.Should().NotContain(Color.FromRgb(0x2F, 0xA8, 0x4F));
    }
}
