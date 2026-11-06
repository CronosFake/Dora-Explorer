using System.Windows.Media;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Os desenhos do SMB (janela) e do NFS (pinguim): formas dentro da grade de 16x16, uma cor só e uma versão colorida.</summary>
public sealed class NetworkProtocolIconsTests
{
    private static Geometry[] AllMarks() =>
    [
        NetworkProtocolIcons.FtpMark, NetworkProtocolIcons.SftpMark, NetworkProtocolIcons.ScpMark,
        NetworkProtocolIcons.TftpMark, NetworkProtocolIcons.SmbMark, NetworkProtocolIcons.NfsMark
    ];

    private static readonly Pen Hairline = new(Brushes.Black, 1);

    private static bool Stroked(Geometry geometry, double x, double y) => geometry.StrokeContains(Hairline, new System.Windows.Point(x, y));

    [Fact]
    public void The_marks_are_frozen_and_stay_inside_the_grid_with_room_for_the_stroke()
    {
        foreach (var mark in AllMarks())
        {
            mark.IsFrozen.Should().BeTrue("congelado vale em qualquer thread");
            var bounds = mark.Bounds;

            // O traço tem 1 de espessura (0,5 para cada lado): o desenho fica a 0,5 das bordas para não ser cortado.
            bounds.Left.Should().BeGreaterThanOrEqualTo(0.5);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0.5);
            bounds.Right.Should().BeLessThanOrEqualTo(15.5);
            bounds.Bottom.Should().BeLessThanOrEqualTo(15.5);
            bounds.Width.Should().BeGreaterThanOrEqualTo(9, "tem que ocupar a grade para ficar legível em 15 px");
            bounds.Height.Should().BeGreaterThanOrEqualTo(9);
        }
    }

    /// <summary>Quais pontos de uma grade de meio em meio pixel o traço toca: um desenho tem sempre a mesma sequência de 0 e 1.</summary>
    private static string Fingerprint(Geometry mark)
    {
        var bits = new System.Text.StringBuilder();
        for (var y = 0.0; y <= 16; y += 0.5)
        {
            for (var x = 0.0; x <= 16; x += 0.5)
            {
                bits.Append(Stroked(mark, x, y) ? '1' : '0');
            }
        }

        return bits.ToString();
    }

    [Fact]
    public void The_six_marks_are_different_drawings()
    {
        var marks = AllMarks();

        marks.Distinct().Should().HaveCount(6);
        var prints = marks.Select(Fingerprint).ToList();
        prints.Distinct().Should().HaveCount(6, "cada protocolo tem o seu contorno");
        prints.Should().OnlyContain(print => print.Count(bit => bit == '1') > 40, "um contorno tem linha de verdade, não é vazio");

        // O FTP e o SFTP são a mesma pasta; o cadeado e a seta única do SFTP é que os separam.
        var ftp = prints[0];
        var sftp = prints[1];
        ftp.Zip(sftp, (a, b) => a != b).Count(different => different).Should().BeGreaterThan(10);
    }

    [Fact]
    public void The_window_is_four_separate_hollow_panes()
    {
        var window = NetworkProtocolIcons.SmbMark;

        var figures = window.GetFlattenedPathGeometry().Figures;
        figures.Should().HaveCount(4);
        figures.Should().OnlyContain(figure => figure.IsClosed, "cada vidraça é um contorno fechado");

        Stroked(window, 1.5, 5.25).Should().BeTrue("a borda esquerda da primeira vidraça");
        Stroked(window, 4.15, 5.2).Should().BeFalse("o centro da vidraça é vazio: só o contorno");
        Stroked(window, 11.8, 4.0).Should().BeFalse("o centro da vidraça de cima à direita é vazio");
        Stroked(window, 8, 8).Should().BeFalse("o espaço entre as vidraças é vazio");
    }

    [Fact]
    public void The_penguin_is_an_outline_with_a_hollow_belly()
    {
        var penguin = NetworkProtocolIcons.NfsMark;

        Stroked(penguin, 8, 0.8).Should().BeTrue("o topo da cabeça");
        Stroked(penguin, 2.9, 10).Should().BeTrue("a lateral do corpo");
        Stroked(penguin, 8, 7.3).Should().BeTrue("o alto da barriga");
        Stroked(penguin, 8, 10.4).Should().BeFalse("dentro da barriga é vazio: só o contorno");
        Stroked(penguin, 8, 14.1).Should().BeTrue("a base do corpo");
    }

    [Fact]
    public void The_terminal_and_folder_marks_are_outlines_too()
    {
        Stroked(NetworkProtocolIcons.ScpMark, 8, 5.3).Should().BeTrue("a barra de título do terminal");
        Stroked(NetworkProtocolIcons.ScpMark, 8, 8).Should().BeFalse("dentro da janela do terminal é vazio");
        Stroked(NetworkProtocolIcons.FtpMark, 8, 13).Should().BeTrue("a base da pasta");
        Stroked(NetworkProtocolIcons.FtpMark, 8, 6).Should().BeFalse("dentro da pasta, entre as setas, é vazio");
        Stroked(NetworkProtocolIcons.SftpMark, 10.8, 8.7).Should().BeTrue("o alto do corpo do cadeado");
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
