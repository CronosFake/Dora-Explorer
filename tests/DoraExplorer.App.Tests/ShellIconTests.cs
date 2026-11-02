using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Arquivos compactados (.tar.gz, .tgz, .tar, .gz, .7z...) ficavam com o ícone genérico quando o Windows não tem ícone
/// registrado para eles (acontece quando o WinRAR registra essas extensões sem ícone). O app passa a desenhar o seu, só
/// nesse caso.
/// </summary>
public sealed class ShellIconTests
{
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("Falhou na thread STA.", error);
        }

        return result;
    }

    /// <summary>Desenha o ícone em 16x16 e devolve os pixels (B, G, R, A por pixel).</summary>
    private static byte[] Render(ImageSource icon) => OnStaThread(() =>
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(icon, new Rect(0, 0, 16, 16));
        }

        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[16 * 16 * 4];
        bitmap.CopyPixels(pixels, 16 * 4, 0);
        return pixels;
    });

    private static (byte R, byte G, byte B, byte A) PixelAt(byte[] pixels, int x, int y)
    {
        var i = ((y * 16) + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }

    [Theory]
    [InlineData("backup.tar.gz", true)]
    [InlineData("BACKUP.TAR.GZ", true)]
    [InlineData("backup.tgz", true)]
    [InlineData("backup.tar", true)]
    [InlineData(@"C:\pasta\backup.tar.gz", true)]
    [InlineData("dora-sftp://servidor/pasta/backup.tar.gz", true)]
    [InlineData("backup.gz", true)]
    [InlineData("backup.7z", true)]
    [InlineData("backup.zip", true)]
    [InlineData("backup.rar", true)]
    [InlineData("backup.tar.gz.txt", false)]
    [InlineData("backup.tar.bak", false)]
    [InlineData("notas.txt", false)]
    [InlineData("tar", false)]
    [InlineData("", false)]
    public void Only_archives_the_app_opens_can_get_the_drawn_icon(string name, bool expected) =>
        ShellIconService.IsArchiveName(name).Should().Be(expected);

    [Fact]
    public void The_icon_from_windows_is_kept_when_it_is_a_real_one()
    {
        var windowsIcon = new DrawingImage();

        ShellIconService.ChooseArchiveIcon(windowsIcon, windowsIconIsGeneric: false).Should().BeSameAs(windowsIcon);
    }

    [Fact]
    public void The_drawn_icon_replaces_a_generic_or_missing_windows_icon()
    {
        var generic = new DrawingImage();

        ShellIconService.ChooseArchiveIcon(generic, windowsIconIsGeneric: true)
            .Should().BeSameAs(ShellIconService.ArchiveIcon);
        ShellIconService.ChooseArchiveIcon(null, windowsIconIsGeneric: false)
            .Should().BeSameAs(ShellIconService.ArchiveIcon);
    }

    [Fact]
    public void The_drawn_icon_is_frozen_and_16_by_16()
    {
        var icon = ShellIconService.ArchiveIcon;

        icon.IsFrozen.Should().BeTrue("precisa poder ser usado por qualquer thread, como os ícones do Windows");
        icon.Width.Should().Be(16);
        icon.Height.Should().Be(16);
    }

    [Fact]
    public void The_drawn_icon_has_the_page_the_band_and_the_zipper_pull()
    {
        var pixels = Render(ShellIconService.ArchiveIcon);

        // Fora da folha fica transparente.
        PixelAt(pixels, 0, 0).A.Should().Be(0);
        PixelAt(pixels, 15, 15).A.Should().Be(0);

        // Folha clara, no meio da parte de cima, longe do zíper.
        var page = PixelAt(pixels, 4, 6);
        page.A.Should().Be(255);
        page.R.Should().BeGreaterThan(230);
        page.G.Should().BeGreaterThan(230);

        // Faixa azul-esverdeada na parte de baixo.
        var band = PixelAt(pixels, 8, 14);
        band.A.Should().Be(255);
        band.R.Should().BeLessThan(80);
        band.G.Should().BeGreaterThan(100);
        band.B.Should().BeGreaterThan(100);

        // Cursor do zíper, alaranjado.
        var pull = PixelAt(pixels, 7, 10);
        pull.R.Should().BeGreaterThan(200);
        pull.B.Should().BeLessThan(150);
    }

    [Theory]
    [InlineData(@"C:\x\backup.tar.gz")]
    [InlineData(@"C:\x\backup.tgz")]
    [InlineData(@"C:\x\backup.tar")]
    [InlineData(@"C:\x\dados.sql.gz")]
    [InlineData(@"C:\x\pacote.7z")]
    [InlineData(@"C:\x\normal.zip")]
    [InlineData(@"C:\x\antigo.rar")]
    public void Archives_always_get_an_icon(string path) =>
        ShellIconService.GetCachedFileIcon(path).Should().NotBeNull();

    [Fact]
    public void Files_that_are_not_archives_never_get_the_drawn_icon()
    {
        ShellIconService.GetCachedFileIcon(@"C:\x\notas.txt").Should().NotBeSameAs(ShellIconService.ArchiveIcon);
        ShellIconService.GetCachedFileIcon(@"C:\x\sem-extensao").Should().NotBeSameAs(ShellIconService.ArchiveIcon);
    }
}
