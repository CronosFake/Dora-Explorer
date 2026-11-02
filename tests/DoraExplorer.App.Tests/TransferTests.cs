using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public sealed class NativeFileCopyTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-copy-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => FileOperationTests.DeleteQuietly(_dir);

    [Fact]
    public void Copy_keeps_the_bytes_dates_attributes_and_alternate_streams()
    {
        var source = Path.Combine(_dir, "origem.bin");
        var destination = Path.Combine(_dir, "destino.bin");
        File.WriteAllBytes(source, Pattern(300_000));
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        var stamp = new DateTime(2020, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, stamp);
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Archive);

        try
        {
            NativeFileCopy.CopyFile(source, destination, _ => { }, CancellationToken.None);

            File.ReadAllBytes(destination).Should().Equal(File.ReadAllBytes(source));
            File.GetLastWriteTimeUtc(destination).Should().Be(stamp);
            File.GetAttributes(destination).Should().HaveFlag(FileAttributes.ReadOnly);
            File.ReadAllText(destination + ":Zone.Identifier").Should().Contain("ZoneId=3",
                "a marca de arquivo baixado da internet não pode se perder na cópia");
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Progress_grows_and_ends_at_the_file_length()
    {
        var source = Path.Combine(_dir, "grande.bin");
        File.WriteAllBytes(source, Pattern(8 * 1024 * 1024));
        var seen = new List<long>();

        NativeFileCopy.CopyFile(source, Path.Combine(_dir, "copia.bin"), seen.Add, CancellationToken.None);

        seen.Should().NotBeEmpty();
        seen.Should().BeInAscendingOrder();
        seen[^1].Should().Be(8 * 1024 * 1024);
    }

    [Fact]
    public void Cancelling_in_the_middle_of_a_file_stops_it_and_leaves_no_partial_destination()
    {
        var source = Path.Combine(_dir, "enorme.bin");
        var destination = Path.Combine(_dir, "parcial.bin");
        File.WriteAllBytes(source, Pattern(64 * 1024 * 1024));
        using var cts = new CancellationTokenSource();
        long lastSeen = 0;

        var act = () => NativeFileCopy.CopyFile(
            source,
            destination,
            transferred =>
            {
                lastSeen = transferred;
                if (transferred > 0)
                {
                    cts.Cancel();
                }
            },
            cts.Token);

        act.Should().Throw<OperationCanceledException>();
        lastSeen.Should().BeLessThan(64 * 1024 * 1024, "parou antes de terminar o arquivo");
        File.Exists(destination).Should().BeFalse("o Windows apaga o destino parcial quando a cópia é cancelada");
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public void An_existing_destination_is_never_overwritten()
    {
        var source = Path.Combine(_dir, "a.txt");
        var destination = Path.Combine(_dir, "b.txt");
        File.WriteAllText(source, "novo");
        File.WriteAllText(destination, "antigo");

        var act = () => NativeFileCopy.CopyFile(source, destination, _ => { }, CancellationToken.None);

        act.Should().Throw<IOException>();
        File.ReadAllText(destination).Should().Be("antigo");
    }

    [Fact]
    public void Move_reports_progress_and_removes_the_source()
    {
        var source = Path.Combine(_dir, "mover.bin");
        var destination = Path.Combine(_dir, "movido.bin");
        File.WriteAllBytes(source, Pattern(1_000_000));

        NativeFileCopy.MoveFile(source, destination, _ => { }, CancellationToken.None);

        File.Exists(source).Should().BeFalse();
        new FileInfo(destination).Length.Should().Be(1_000_000);
    }

    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        return data;
    }
}

public sealed class FileOperationProgressTests : IDisposable
{
    private readonly string _source = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-prog-src-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _target = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-prog-dst-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        FileOperationTests.DeleteQuietly(_source);
        FileOperationTests.DeleteQuietly(_target);
    }

    [Fact]
    public async Task Copying_a_folder_reports_totals_files_and_ends_complete()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "pasta")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[100_000]);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "sub", "b.bin"), new byte[250_000]);
        var reports = new List<FileOperationProgress>();

        var result = await new FileOperationService().TryCopyAsync(
            folder, _target, new SyncProgress<FileOperationProgress>(reports.Add));

        result.Success.Should().BeTrue(result.ErrorMessage);
        var last = reports[^1];
        last.BytesTotal.Should().Be(350_000);
        last.BytesDone.Should().Be(350_000);
        last.FilesTotal.Should().Be(2);
        last.FilesDone.Should().Be(2);
        last.Fraction.Should().Be(1);
        reports.Select(r => r.BytesDone).Should().BeInAscendingOrder();
        new FileInfo(Path.Combine(_target, "pasta", "sub", "b.bin")).Length.Should().Be(250_000);
    }

    [Fact]
    public async Task A_cancelled_copy_reports_cancellation_and_leaves_nothing_behind()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "pasta")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[500_000]);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<FileOperationProgress>(_ => cts.Cancel()); // cancela já no primeiro aviso

        var result = await new FileOperationService().TryCopyAsync(folder, _target, progress, cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(FileOperationService.CancelledMessage);
        Directory.GetFileSystemEntries(_target).Should().BeEmpty("a cópia incompleta é removida");
    }

    [Fact]
    public async Task Moving_a_file_reports_progress_and_the_source_disappears()
    {
        var file = Path.Combine(_source, "m.bin");
        File.WriteAllBytes(file, new byte[200_000]);
        var reports = new List<FileOperationProgress>();

        var result = await new FileOperationService().TryMoveAsync(
            file, _target, new SyncProgress<FileOperationProgress>(reports.Add));

        result.Success.Should().BeTrue(result.ErrorMessage);
        File.Exists(file).Should().BeFalse();
        File.Exists(Path.Combine(_target, "m.bin")).Should().BeTrue();
        reports[^1].BytesDone.Should().Be(200_000);
    }

    [Fact]
    public void Scanner_totals_the_tree_and_does_not_follow_junctions()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "arvore")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "um.bin"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(folder, "filha"));
        File.WriteAllBytes(Path.Combine(folder, "filha", "dois.bin"), new byte[2000]);

        var outside = Directory.CreateDirectory(Path.Combine(_target, "fora")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "enorme.bin"), new byte[999_999]);
        CreateJunction(Path.Combine(folder, "atalho"), outside);

        var (bytes, files) = TransferScanner.ScanItem(folder, heartbeat: null, CancellationToken.None);

        bytes.Should().Be(3000, "o conteúdo apontado por uma junção não entra na conta");
        files.Should().Be(2);
    }

    [Fact]
    public void Tracker_throttles_reports_but_always_delivers_the_final_one()
    {
        var reports = new List<FileOperationProgress>();
        var tracker = new TransferProgressTracker(new SyncProgress<FileOperationProgress>(reports.Add), keepAlive: null);
        tracker.SetTotals(1_000_000, 1);
        tracker.BeginFile(@"C:\x\arquivo.bin");

        for (long done = 1; done <= 1_000_000; done += 100)
        {
            tracker.FileProgress(done);
        }

        tracker.EndFile(1_000_000);
        tracker.Complete();

        reports.Count.Should().BeLessThan(60, "10 mil avisos por segundo inundariam a interface");
        reports[^1].BytesDone.Should().Be(1_000_000);
        reports[^1].CurrentFile.Should().Be("arquivo.bin");
    }

    [Fact]
    public void Aggregator_adds_finished_items_to_the_current_one()
    {
        var reports = new List<FileOperationProgress>();
        var aggregate = new TransferProgressAggregator(new SyncProgress<FileOperationProgress>(reports.Add), grandBytes: 300, grandFiles: 3);

        aggregate.Report(new FileOperationProgress(50, 100, 0, 1, "a"));
        aggregate.CompleteItem(100, 1);
        aggregate.Report(new FileOperationProgress(30, 200, 0, 2, "b"));

        reports[0].BytesDone.Should().Be(50);
        reports[0].BytesTotal.Should().Be(300);
        reports[1].BytesDone.Should().Be(130);
        reports[1].BytesTotal.Should().Be(300);
        reports[1].FilesTotal.Should().Be(3);
        reports[1].Fraction.Should().BeApproximately(130d / 300d, 1e-9);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process!.WaitForExit(10_000);
    }
}

public sealed class InactivityTimeoutTests
{
    [Fact]
    public async Task Work_that_stops_making_progress_is_cancelled_after_the_idle_time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var act = async () => await OperationTimeouts.RunWithInactivityAsync<bool>(
            (token, _) =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)); // trava sem avisar
                Thread.Sleep(150); // um trabalho real demora um instante a notar o cancelamento
                return true;
            },
            TimeSpan.FromMilliseconds(300));

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Work_that_keeps_reporting_is_never_cut_off_even_when_it_runs_longer_than_the_idle_time()
    {
        var result = await OperationTimeouts.RunWithInactivityAsync(
            (token, keepAlive) =>
            {
                for (var i = 0; i < 12; i++)
                {
                    Thread.Sleep(100);
                    token.ThrowIfCancellationRequested();
                    keepAlive();
                }

                return "terminou";
            },
            TimeSpan.FromMilliseconds(400));

        result.Should().Be("terminou", "1,2 s no total, mas nunca 400 ms sem avisar");
    }

    [Fact]
    public async Task The_user_can_still_cancel_at_any_time()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await OperationTimeouts.RunWithInactivityAsync<bool>(
            (token, keepAlive) =>
            {
                while (!token.IsCancellationRequested)
                {
                    keepAlive();
                    Thread.Sleep(20);
                }

                Thread.Sleep(150);
                token.ThrowIfCancellationRequested();
                return true;
            },
            TimeSpan.FromSeconds(30),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public sealed class TransferProgressFormatterTests
{
    [Fact]
    public void Title_names_the_action_the_count_and_the_destination_folder()
    {
        TransferProgressFormatter.Title(move: false, 1, @"D:\Backup\Fotos").Should().Be("Copiando 1 item para Fotos");
        TransferProgressFormatter.Title(move: true, 12, @"D:\Backup\").Should().Be("Movendo 12 itens para Backup");
        TransferProgressFormatter.Title(move: false, 2, @"E:\").Should().Be(@"Copiando 2 itens para E:\");
    }

    [Fact]
    public void Description_shows_bytes_files_speed_and_remaining_time()
    {
        var text = TransferProgressFormatter.Describe(
            new FileOperationProgress(50L * 1024 * 1024, 150L * 1024 * 1024, 2, 5, "x"),
            bytesPerSecond: 10L * 1024 * 1024);

        text.Should().Contain("de").And.Contain("2 de 5 arquivos").And.Contain("/s").And.Contain("restam 00:10");
    }

    [Fact]
    public void Description_says_calculating_until_the_total_is_known()
    {
        TransferProgressFormatter.Describe(new FileOperationProgress(0, 0, 0, 0, string.Empty), 0)
            .Should().Be("Calculando…");
    }

    [Theory]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(3725, "1:02:05")]
    public void Durations_are_shown_as_clock_time(int seconds, string expected)
    {
        TransferProgressFormatter.FormatDuration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }
}

public sealed class CompressionProgressTests : IDisposable
{
    private readonly string _source = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-zip-src-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _target = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-zip-dst-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        FileOperationTests.DeleteQuietly(_source);
        FileOperationTests.DeleteQuietly(_target);
    }

    [Fact]
    public async Task Zip_keeps_content_folders_and_dates_and_reports_progress_to_the_end()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "docs")).FullName;
        var big = Random(300_000);
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), big);
        File.WriteAllText(Path.Combine(folder, "vazio.txt"), string.Empty);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "conteudo");
        var stamp = new DateTime(2021, 5, 17, 10, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(Path.Combine(folder, "a.bin"), stamp);
        var reports = new List<FileOperationProgress>();

        var result = await new FileOperationService().TryCompressAsync(
            [folder], _target, ArchiveFormat.Zip, new SyncProgress<FileOperationProgress>(reports.Add));

        result.Success.Should().BeTrue(result.ErrorMessage);
        using var zip = ZipFile.OpenRead(result.ArchivePath!);
        zip.Entries.Select(e => e.FullName).Should().Contain(["docs/", "docs/a.bin", "docs/vazio.txt", "docs/sub/", "docs/sub/b.txt"]);
        using (var stream = zip.GetEntry("docs/a.bin")!.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            copy.ToArray().Should().Equal(big);
        }

        (zip.GetEntry("docs/a.bin")!.LastWriteTime.LocalDateTime - stamp).Duration().Should().BeLessThan(TimeSpan.FromSeconds(3));
        reports[^1].BytesTotal.Should().Be(300_008);
        reports[^1].BytesDone.Should().Be(300_008);
        reports[^1].FilesDone.Should().Be(3);
        reports.Select(r => r.BytesDone).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task TarGz_keeps_content_and_folders()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "pacote")).FullName;
        var data = Random(120_000);
        File.WriteAllBytes(Path.Combine(folder, "dados.bin"), data);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "nota.txt"), "olá");

        var result = await new FileOperationService().TryCompressAsync([folder], _target, ArchiveFormat.TarGz, progress: null);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ArchivePath.Should().EndWith(".tar.gz");
        using var gzip = new GZipStream(File.OpenRead(result.ArchivePath!), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var found = new Dictionary<string, byte[]>();
        while (reader.GetNextEntry() is { } entry)
        {
            using var copy = new MemoryStream();
            entry.DataStream?.CopyTo(copy);
            found[entry.Name] = copy.ToArray();
        }

        found.Keys.Should().Contain(["pacote", "pacote/dados.bin", "pacote/sub", "pacote/sub/nota.txt"]);
        found["pacote/dados.bin"].Should().Equal(data);
        Encoding.UTF8.GetString(found["pacote/sub/nota.txt"]).Should().Be("olá");
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.TarGz)]
    public async Task Cancelling_in_the_middle_of_a_big_file_leaves_no_partial_archive(ArchiveFormat format)
    {
        var file = Path.Combine(_source, "enorme.bin");
        File.WriteAllBytes(file, Random(96 * 1024 * 1024)); // dados aleatórios: compactam devagar, dão tempo de cancelar
        using var cts = new CancellationTokenSource();
        var cancelledAt = 0L;
        var progress = new SyncProgress<FileOperationProgress>(p =>
        {
            if (p.BytesDone > 0 && !cts.IsCancellationRequested)
            {
                cancelledAt = p.BytesDone;
                cts.Cancel();
            }
        });

        var result = await new FileOperationService().TryCompressAsync([file], _target, format, progress, cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(FileOperationService.CancelledMessage);
        cancelledAt.Should().BeInRange(1, (96 * 1024 * 1024) - 1, "parou no meio do arquivo, não no fim");
        Directory.GetFileSystemEntries(_target).Should().BeEmpty("um ZIP/TAR pela metade seria um arquivo corrompido");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task A_junction_pointing_back_at_its_own_folder_does_not_make_compression_loop_forever()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_source, "ciclo")).FullName;
        File.WriteAllText(Path.Combine(folder, "real.txt"), "x");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{Path.Combine(folder, "volta")}\" \"{folder}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            process!.WaitForExit(10_000);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new FileOperationService().TryCompressAsync([folder], _target, ArchiveFormat.Zip, progress: null, cts.Token);

        result.Success.Should().BeTrue(result.ErrorMessage);
        using var zip = ZipFile.OpenRead(result.ArchivePath!);
        zip.Entries.Select(e => e.FullName).Should().Equal("ciclo/", "ciclo/real.txt");
    }

    [Fact]
    public async Task Files_older_than_1980_do_not_break_zip_creation()
    {
        var file = Path.Combine(_source, "antigo.txt");
        File.WriteAllText(file, "velho");
        File.SetLastWriteTime(file, new DateTime(1975, 6, 1));

        var result = await new FileOperationService().TryCompressAsync([file], _target, ArchiveFormat.Zip, progress: null);

        result.Success.Should().BeTrue(result.ErrorMessage);
        using var zip = ZipFile.OpenRead(result.ArchivePath!);
        zip.GetEntry("antigo.txt")!.LastWriteTime.Year.Should().BeGreaterThanOrEqualTo(1980);
    }

    [Fact]
    public void Compress_title_names_count_format_and_destination()
    {
        TransferProgressFormatter.CompressTitle(3, ArchiveFormat.Zip, @"D:\Backup\Fotos").Should().Be("Compactando 3 itens (ZIP) em Fotos");
        TransferProgressFormatter.CompressTitle(1, ArchiveFormat.TarGz, @"E:\").Should().Be(@"Compactando 1 item (TAR.GZ) em E:\");
    }

    private static byte[] Random(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }
}