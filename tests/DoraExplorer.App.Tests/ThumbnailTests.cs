using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Controls;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DoraExplorer.App.Tests;

public sealed class FileThumbnailServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-thumb-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => SshTestHelpers.DeleteDirectory(_directory);

    private string CreatePng(string name, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_directory, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.png", true)]
    [InlineData("a.webp", true)]
    [InlineData("relatorio.PDF", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.docx", false)]
    [InlineData("a.exe", false)]
    [InlineData("semextensao", false)]
    public void Only_images_and_pdfs_have_previews(string name, bool expected) =>
        FileThumbnailService.CanLoadThumbnail(name).Should().Be(expected);

    [Fact]
    public void Wide_images_are_limited_by_their_width()
    {
        var path = CreatePng("wide.png", 1000, 500);

        var thumbnail = (BitmapSource)FileThumbnailService.TryLoadThumbnail(path, 100)!;

        thumbnail.PixelWidth.Should().Be(100);
        thumbnail.PixelHeight.Should().Be(50);
        thumbnail.IsFrozen.Should().BeTrue("precisa poder ser usada por outra thread");
    }

    [Fact]
    public void Tall_images_are_limited_by_their_height_not_just_their_width()
    {
        var path = CreatePng("tall.png", 200, 1000);

        var thumbnail = (BitmapSource)FileThumbnailService.TryLoadThumbnail(path, 100)!;

        thumbnail.PixelHeight.Should().Be(100);
        thumbnail.PixelWidth.Should().Be(20);
    }

    [Fact]
    public void Small_images_are_not_enlarged()
    {
        var path = CreatePng("small.png", 50, 40);

        var thumbnail = (BitmapSource)FileThumbnailService.TryLoadThumbnail(path, 320)!;

        thumbnail.PixelWidth.Should().Be(50);
        thumbnail.PixelHeight.Should().Be(40);
    }

    [Fact]
    public void Missing_unsupported_and_corrupt_files_give_nothing_instead_of_failing()
    {
        FileThumbnailService.TryLoadThumbnail(Path.Combine(_directory, "nao-existe.png")).Should().BeNull();

        var text = Path.Combine(_directory, "nota.txt");
        File.WriteAllText(text, "oi");
        FileThumbnailService.TryLoadThumbnail(text).Should().BeNull();

        var corrupt = Path.Combine(_directory, "quebrada.png");
        File.WriteAllBytes(corrupt, new byte[2000]);
        FileThumbnailService.TryLoadThumbnail(corrupt).Should().BeNull();

        var empty = Path.Combine(_directory, "vazia.jpg");
        File.WriteAllBytes(empty, []);
        FileThumbnailService.TryLoadThumbnail(empty).Should().BeNull();
    }

    [Fact]
    public void Huge_image_files_are_skipped_without_reading_them()
    {
        var path = Path.Combine(_directory, "enorme.png");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(65L * 1024 * 1024);
        }

        FileThumbnailService.TryLoadThumbnail(path, 100).Should().BeNull();
    }

    [Fact]
    public async Task Async_loading_works_off_the_calling_thread()
    {
        var path = CreatePng("async.png", 400, 400);

        var thumbnail = await FileThumbnailService.TryLoadThumbnailAsync(path, 100, CancellationToken.None);

        thumbnail.Should().NotBeNull();
        ((BitmapSource)thumbnail!).PixelWidth.Should().Be(100);
    }

    [Fact]
    public async Task Async_loading_honours_cancellation()
    {
        var path = CreatePng("cancel.png", 100, 100);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var load = () => FileThumbnailService.TryLoadThumbnailAsync(path, 100, cancelled.Token);

        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Pdf_preview_either_works_or_gives_nothing_but_never_hangs_or_throws()
    {
        // O resultado depende de haver um provedor de miniaturas de PDF instalado (Edge, Adobe...).
        var path = Path.Combine(_directory, "teste.pdf");
        File.WriteAllText(path, MinimalPdf);

        var load = FileThumbnailService.TryLoadThumbnailAsync(path, 200, CancellationToken.None);
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(30)));
        finished.Should().BeSameAs(load, "o serviço tem limite de tempo próprio");

        var thumbnail = await load;
        output.WriteLine(thumbnail is BitmapSource b ? $"miniatura de PDF gerada: {b.PixelWidth}x{b.PixelHeight}" : "sem miniatura de PDF neste computador");
        if (thumbnail is BitmapSource bitmap)
        {
            bitmap.PixelWidth.Should().BeGreaterThan(0);
            bitmap.IsFrozen.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Garbage_with_a_pdf_name_does_not_throw()
    {
        var path = Path.Combine(_directory, "lixo.pdf");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        var load = () => FileThumbnailService.TryLoadThumbnailAsync(path, 100, CancellationToken.None);

        await load.Should().NotThrowAsync();
    }

    private const string MinimalPdf =
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
        "4 0 obj<</Length 44>>stream\nBT /F1 24 Tf 20 100 Td (Dora PDF) Tj ET\nendstream endobj\n" +
        "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n" +
        "trailer<</Root 1 0 R/Size 6>>\n%%EOF\n";
}

public sealed class ThumbnailHoverRulesTests
{
    private static NavigationTreeNodeViewModel Node(NavigationNodeKind kind, string? path, bool recycle = false) =>
        new("nome", kind, path) { IsRecycleBinEntry = recycle };

    [Theory]
    [InlineData(NavigationNodeKind.File, "C:\\fotos\\a.png", true)]
    [InlineData(NavigationNodeKind.File, "D:\\docs\\b.PDF", true)]
    [InlineData(NavigationNodeKind.File, "\\\\servidor\\share\\c.jpg", true)]
    [InlineData(NavigationNodeKind.File, "C:\\docs\\notas.txt", false)]
    [InlineData(NavigationNodeKind.File, "relativo\\a.png", false)]
    [InlineData(NavigationNodeKind.File, "dora-ftp://host/a.png", false)]
    [InlineData(NavigationNodeKind.File, null, false)]
    [InlineData(NavigationNodeKind.Directory, "C:\\fotos\\pasta.png", false)]
    public void Preview_only_applies_to_local_images_and_pdfs(NavigationNodeKind kind, string? path, bool expected) =>
        ThumbnailHoverPreview.ShouldPreview(Node(kind, path)).Should().Be(expected);

    [Fact]
    public void Recycle_bin_entries_are_never_previewed() =>
        ThumbnailHoverPreview.ShouldPreview(Node(NavigationNodeKind.File, "C:\\$Recycle.Bin\\x\\$R1.png", recycle: true)).Should().BeFalse();
}
