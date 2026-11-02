using System.Diagnostics;
using System.Security.AccessControl;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// A janela Propriedades mostra o que este serviço lê do disco: tamanho (e o espaço que ocupa de verdade), datas, atributos, o que há
/// dentro de uma pasta e o SHA-256. Tudo com arquivos e pastas temporários; nada fora da pasta de teste é tocado.
/// </summary>
public sealed class ItemPropertiesServiceTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-props-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly AccessDenyScope _deny = new();

    public void Dispose()
    {
        _deny.Dispose();
        FileOperationTests.DeleteQuietly(_work);
    }

    private string NewFile(string relative, string content)
    {
        var path = Path.Combine(_work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_file_reports_its_name_location_size_and_dates()
    {
        var path = NewFile("pasta\\relatorio.txt", "0123456789");

        var properties = ItemPropertiesService.Read(path);

        properties.Should().NotBeNull();
        properties!.Name.Should().Be("relatorio.txt");
        properties.Extension.Should().Be(".txt");
        properties.IsDirectory.Should().BeFalse();
        properties.Path.Should().Be(path);
        properties.Location.Should().Be(Path.GetDirectoryName(path));
        properties.SizeBytes.Should().Be(10);
        properties.TypeDescription.Should().NotBeNullOrWhiteSpace();
        properties.Modified.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(2));
        properties.Created.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(2));
        properties.Accessed.Should().NotBeNull();
    }

    [Fact]
    public void The_size_on_disk_is_the_size_rounded_up_to_whole_clusters()
    {
        var path = NewFile("dados.bin", new string('x', 5000));
        var cluster = ItemPropertiesService.ClusterSizeOf(path);

        var properties = ItemPropertiesService.Read(path)!;

        cluster.Should().BeGreaterThan(0);
        properties.SizeOnDiskBytes.Should().BeGreaterThanOrEqualTo(5000);
        (properties.SizeOnDiskBytes % cluster).Should().Be(0, "o disco aloca o arquivo em clusters inteiros");
    }

    [Theory]
    [InlineData(0L, 4096L, 0L)]
    [InlineData(1L, 4096L, 4096L)]
    [InlineData(4096L, 4096L, 4096L)]
    [InlineData(4097L, 4096L, 8192L)]
    [InlineData(5000L, 0L, 5000L)]
    public void Rounding_a_size_up_to_the_cluster(long length, long cluster, long expected) =>
        ItemPropertiesService.SizeOnDisk("qualquer.bin", length, FileAttributes.Normal, cluster).Should().Be(expected);

    [Fact]
    public void Attributes_reflect_what_is_set_on_the_file()
    {
        var path = NewFile("protegido.txt", "x");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);

        try
        {
            var attributes = ItemPropertiesService.Read(path)!.Attributes;

            attributes.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
            attributes.HasFlag(FileAttributes.Hidden).Should().BeTrue();
            attributes.HasFlag(FileAttributes.Directory).Should().BeFalse();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_folder_has_no_size_of_its_own_and_is_flagged_as_a_directory()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_work, "documentos")).FullName;

        var properties = ItemPropertiesService.Read(folder)!;

        properties.IsDirectory.Should().BeTrue();
        properties.Name.Should().Be("documentos");
        properties.SizeBytes.Should().BeNull("o tamanho de uma pasta é a soma do que há dentro dela, calculada à parte");
        properties.SizeOnDiskBytes.Should().BeNull();
        properties.Location.Should().Be(_work);
        properties.Extension.Should().BeEmpty();
        properties.Attributes.HasFlag(FileAttributes.Directory).Should().BeTrue();
    }

    [Fact]
    public void A_path_that_no_longer_exists_has_no_properties()
    {
        ItemPropertiesService.Read(Path.Combine(_work, "sumiu.txt")).Should().BeNull();
        ItemPropertiesService.Read("").Should().BeNull();
    }

    [Fact]
    public void The_owner_of_a_file_created_by_this_user_is_this_user()
    {
        var path = NewFile("meu.txt", "x");

        var owner = ItemPropertiesService.Read(path)!.Owner;

        owner.Should().NotBeNullOrWhiteSpace();
        owner!.Should().Contain(Environment.UserName, "o dono vem como DOMÍNIO\\usuário");
    }

    [Fact]
    public void An_executable_or_library_reports_its_version_details()
    {
        var library = typeof(ItemPropertiesService).Assembly.Location;

        var details = ItemPropertiesService.Read(library)!.Executable;

        details.Should().NotBeNull("uma .dll carrega dados de versão");
        details!.FileVersion.Should().MatchRegex(@"^\d+\.\d+");
        details.ProductName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_plain_text_file_has_no_executable_details_and_no_image_size()
    {
        var properties = ItemPropertiesService.Read(NewFile("nota.txt", "texto"))!;

        properties.Executable.Should().BeNull();
        properties.ImageSize.Should().BeNull();
    }

    [Fact]
    public void An_image_reports_its_width_and_height_in_pixels()
    {
        var path = Path.Combine(_work, "pequena.png");
        var pixels = new byte[3 * 3 * 2];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgr24, null, pixels, 9)));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        ItemPropertiesService.Read(path)!.ImageSize.Should().Be((3, 2));
    }

    [Fact]
    public void A_broken_image_does_not_break_reading_the_other_properties()
    {
        var path = NewFile("quebrada.png", "isto não é uma imagem");

        var properties = ItemPropertiesService.Read(path);

        properties.Should().NotBeNull();
        properties!.ImageSize.Should().BeNull();
        properties.SizeBytes.Should().BeGreaterThan(0);
    }

    // ---- o que há dentro das pastas ----

    private string BuildTree()
    {
        NewFile("raiz\\a.txt", new string('a', 10));
        NewFile("raiz\\sub\\b.txt", new string('b', 20));
        NewFile("raiz\\sub\\fundo\\c.txt", new string('c', 30));
        Directory.CreateDirectory(Path.Combine(_work, "raiz", "vazia"));
        return Path.Combine(_work, "raiz");
    }

    [Fact]
    public async Task A_folder_counts_every_file_and_folder_inside_it_and_their_bytes()
    {
        var totals = await ItemPropertiesService.ComputeTotalsAsync([BuildTree()]);

        totals.Files.Should().Be(3);
        totals.Folders.Should().Be(3, "sub, fundo e vazia (a pasta da seleção não conta)");
        totals.Bytes.Should().Be(60);
        totals.BytesOnDisk.Should().BeGreaterThanOrEqualTo(60);
        totals.Unreadable.Should().Be(0);
    }

    [Fact]
    public async Task Loose_files_in_the_selection_count_as_files_next_to_the_folder_contents()
    {
        var loose = NewFile("solto.txt", new string('s', 5));

        var totals = await ItemPropertiesService.ComputeTotalsAsync([BuildTree(), loose]);

        totals.Files.Should().Be(4);
        totals.Bytes.Should().Be(65);
    }

    [Fact]
    public async Task An_empty_folder_has_nothing_inside()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_work, "nada")).FullName;

        (await ItemPropertiesService.ComputeTotalsAsync([empty])).Should().Be(new FolderTotals(0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task A_cancelled_count_stops_instead_of_finishing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => ItemPropertiesService.ComputeTotalsAsync([BuildTree()], null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_folder_the_user_may_not_list_is_counted_as_unreadable_and_the_rest_still_adds_up()
    {
        var root = BuildTree();
        var locked = Directory.CreateDirectory(Path.Combine(root, "trancada")).FullName;
        File.WriteAllText(Path.Combine(locked, "escondido.txt"), "x");
        _deny.Deny(locked, FileSystemRights.ListDirectory);

        var totals = await ItemPropertiesService.ComputeTotalsAsync([root]);

        totals.Unreadable.Should().Be(1);
        totals.Files.Should().Be(3, "o que está na pasta trancada não dá para contar");
        totals.Folders.Should().Be(4, "a pasta trancada em si conta como pasta");
    }

    [Fact]
    public async Task A_junction_is_counted_as_a_folder_but_is_not_followed_so_nothing_is_counted_twice_or_forever()
    {
        var root = BuildTree();
        var link = Path.Combine(root, "atalho");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            process.WaitForExit(10_000).Should().BeTrue();
        }

        Directory.Exists(link).Should().BeTrue("a junção foi criada");
        var totals = await ItemPropertiesService.ComputeTotalsAsync([root]);

        totals.Files.Should().Be(3);
        totals.Folders.Should().Be(4, "sub, fundo, vazia e a própria junção");

        Directory.Delete(link); // apaga só a junção, nunca o conteúdo para onde ela aponta
    }

    // ---- SHA-256 ----

    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public async Task The_sha256_of_known_content_matches_the_published_value(string content, string expected)
    {
        var path = NewFile("hash.txt", content);

        (await ItemPropertiesService.ComputeSha256Async(path)).Should().Be(expected);
    }

    [Fact]
    public async Task The_sha256_reports_progress_up_to_the_end()
    {
        var path = NewFile("grande.bin", new string('z', 300_000));
        var steps = new List<double>();

        await ItemPropertiesService.ComputeSha256Async(path, new SynchronousProgress(steps.Add));

        steps.Should().NotBeEmpty();
        steps.Should().BeInAscendingOrder();
        steps[^1].Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_sha256_stops_instead_of_finishing()
    {
        var path = NewFile("cancelado.bin", new string('z', 300_000));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => ItemPropertiesService.ComputeSha256Async(path, null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- unidade ----

    [Fact]
    public void A_drive_reports_its_format_and_space()
    {
        var root = Path.GetPathRoot(_work)!;

        var drive = ItemPropertiesService.ReadDrive(root);

        drive.Should().NotBeNull();
        drive!.Format.Should().NotBeNullOrWhiteSpace();
        drive.TotalBytes.Should().BeGreaterThan(0);
        drive.FreeBytes.Should().BeInRange(0, drive.TotalBytes);
    }

    [Fact]
    public void A_drive_that_is_not_a_valid_root_has_no_details()
    {
        ItemPropertiesService.ReadDrive("").Should().BeNull();
    }

    /// <summary>Entrega o progresso na hora, na mesma thread (o <see cref="Progress{T}"/> de verdade posta para outra e o teste não esperaria).</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
