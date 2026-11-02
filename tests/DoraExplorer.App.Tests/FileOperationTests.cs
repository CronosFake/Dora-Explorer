using System.IO.Compression;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class FileOperationTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ops-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => DeleteQuietly(_work);

    [Fact]
    public async Task Compress_async_does_not_fail_with_a_disposed_cancellation_source()
    {
        var source = Directory.CreateDirectory(Path.Combine(_work, "src")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "conteudo");
        var service = new FileOperationService();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var destination = Directory.CreateDirectory(Path.Combine(_work, "out" + attempt)).FullName;

            var result = await service.TryCompressAsync([source], destination, ArchiveFormat.Zip);

            result.Success.Should().BeTrue($"tentativa {attempt}: {result.ErrorMessage}");
        }
    }

    [Fact]
    public void Creating_a_folder_makes_it_inside_the_parent()
    {
        new FileOperationService().TryCreateDirectory(_work, "nova", out var error).Should().BeTrue(error);

        Directory.Exists(Path.Combine(_work, "nova")).Should().BeTrue();
    }

    [Fact]
    public void Creating_a_folder_that_already_exists_is_refused_and_leaves_it_untouched()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_work, "existente")).FullName;
        File.WriteAllText(Path.Combine(existing, "dentro.txt"), "conteudo");

        new FileOperationService().TryCreateDirectory(_work, "existente", out var error).Should().BeFalse();

        error.Should().Contain("Já existe");
        File.ReadAllText(Path.Combine(existing, "dentro.txt")).Should().Be("conteudo");
    }

    [Fact]
    public void A_name_that_differs_only_by_letter_case_counts_as_existing()
    {
        Directory.CreateDirectory(Path.Combine(_work, "Documentos"));

        new FileOperationService().TryCreateDirectory(_work, "DOCUMENTOS", out var error).Should().BeFalse();

        error.Should().Contain("Já existe");
    }

    [Fact]
    public void Creating_a_folder_with_the_name_of_an_existing_file_is_refused()
    {
        var file = Path.Combine(_work, "arquivo");
        File.WriteAllText(file, "conteudo");

        new FileOperationService().TryCreateDirectory(_work, "arquivo", out var error).Should().BeFalse();

        error.Should().Contain("Já existe");
        File.ReadAllText(file).Should().Be("conteudo");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void The_names_dot_and_dot_dot_are_not_new_folders(string name)
    {
        new FileOperationService().TryCreateDirectory(_work, name, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Move_folder_to_another_volume_copies_then_removes_the_source()
    {
        var other = DriveInfo.GetDrives().FirstOrDefault(drive =>
            drive.DriveType == DriveType.Fixed &&
            drive.IsReady &&
            !string.Equals(drive.RootDirectory.FullName, Path.GetPathRoot(_work), StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            return; // máquina com uma única unidade fixa: não há como testar mover entre volumes.
        }

        var destination = Path.Combine(other.RootDirectory.FullName, "dora-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(_work, "pasta", "sub")).Parent!.FullName;
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");

            var moved = new FileOperationService().TryMove(folder, destination, out var error);

            moved.Should().BeTrue(error);
            Directory.Exists(folder).Should().BeFalse("a origem deve ser removida depois da cópia");
            File.ReadAllText(Path.Combine(destination, "pasta", "a.txt")).Should().Be("a");
            File.ReadAllText(Path.Combine(destination, "pasta", "sub", "b.txt")).Should().Be("b");
        }
        finally
        {
            DeleteQuietly(destination);
        }
    }

    [Fact]
    public void Zip_folder_with_explicit_directory_entries_is_extracted_as_directories()
    {
        var archive = Path.Combine(_work, "dirs.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("docs/");
            zip.CreateEntry("docs/sub/");
            var entry = zip.CreateEntry("docs/sub/a.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("conteudo");
        }

        var destination = Directory.CreateDirectory(Path.Combine(_work, "dest")).FullName;

        new ArchiveService().TryExtractEntry(archive, "docs", destination, out var error).Should().BeTrue(error);

        Directory.Exists(Path.Combine(destination, "docs", "sub")).Should().BeTrue();
        File.ReadAllText(Path.Combine(destination, "docs", "sub", "a.txt")).Should().Be("conteudo");
    }

    [Fact]
    public async Task RunAsync_returns_at_the_timeout_even_when_the_work_ignores_the_token()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => OperationTimeouts.RunAsync<int>(
            _ =>
            {
                Thread.Sleep(3000);
                return 1;
            },
            TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunAsync_hands_the_work_a_token_that_is_cancelled_on_timeout()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var act = () => OperationTimeouts.RunAsync<int>(
            token =>
            {
                var cancelled = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                observed.SetResult(cancelled);

                // Um trabalho real leva um instante para notar o cancelamento; devolver no mesmo
                // microssegundo faria a espera ver a tarefa concluída antes do cancelamento (teste instável).
                Thread.Sleep(150);
                return 1;
            },
            TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await observed.Task).Should().BeTrue("o trabalho em segundo plano deve ser avisado do cancelamento");
    }

    [Fact]
    public async Task Cancelled_copy_never_leaves_a_partial_folder_behind()
    {
        const int fileCount = 3000;
        var source = Directory.CreateDirectory(Path.Combine(_work, "big")).FullName;
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(source, $"f{i}.txt"), "x");
        }

        var destination = Directory.CreateDirectory(Path.Combine(_work, "copy-dest")).FullName;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(15));

        try
        {
            await new FileOperationService().TryCopyAsync(source, destination, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // esperado quando o cancelamento chega antes do fim
        }

        // O trabalho em segundo plano termina de limpar logo a seguir ao cancelamento.
        await Task.Delay(1500);
        var copy = Path.Combine(destination, "big");
        if (Directory.Exists(copy))
        {
            Directory.GetFiles(copy).Length.Should().Be(fileCount, "ou a cópia terminou, ou não existe");
        }
    }

    [Fact]
    public void Failed_compression_removes_the_partial_archive()
    {
        var source = Directory.CreateDirectory(Path.Combine(_work, "to-zip")).FullName;
        File.WriteAllText(Path.Combine(source, "ok.txt"), "ok");
        using var locked = new FileStream(Path.Combine(source, "locked.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var destination = Directory.CreateDirectory(Path.Combine(_work, "zip-dest")).FullName;

        var success = new FileOperationService().TryCompress([source], destination, ArchiveFormat.Zip, out var archivePath, out var error);

        success.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        archivePath.Should().BeNull();
        Directory.GetFiles(destination).Should().BeEmpty("um arquivo parcial seria um ZIP corrompido");
    }

    [Fact]
    public void Copying_a_folder_does_not_follow_a_junction_loop()
    {
        var source = Directory.CreateDirectory(Path.Combine(_work, "with-loop")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        if (!TryCreateJunction(Path.Combine(source, "loop"), source))
        {
            return; // sem permissão para criar junções neste ambiente
        }

        var destination = Directory.CreateDirectory(Path.Combine(_work, "loop-dest")).FullName;

        new FileOperationService().TryCopy(source, destination, out var error).Should().BeTrue(error);

        File.ReadAllText(Path.Combine(destination, "with-loop", "a.txt")).Should().Be("a");
        Directory.Exists(Path.Combine(destination, "with-loop", "loop")).Should().BeFalse("junções são ignoradas");
    }

    [Fact]
    public void Moving_a_folder_with_a_junction_across_volumes_fails_and_keeps_the_source()
    {
        var other = FindOtherFixedDrive();
        if (other is null)
        {
            return;
        }

        var destination = Path.Combine(other.RootDirectory.FullName, "dora-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(_work, "pasta-link")).FullName;
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            var target = Directory.CreateDirectory(Path.Combine(_work, "alvo")).FullName;
            if (!TryCreateJunction(Path.Combine(folder, "atalho"), target))
            {
                return;
            }

            var moved = new FileOperationService().TryMove(folder, destination, out var error);

            moved.Should().BeFalse();
            error.Should().Contain("link");
            Directory.Exists(folder).Should().BeTrue("a origem deve continuar intacta");
            Directory.Exists(Path.Combine(destination, "pasta-link")).Should().BeFalse("o destino parcial é removido");
        }
        finally
        {
            DeleteQuietly(destination);
        }
    }

    private DriveInfo? FindOtherFixedDrive() =>
        DriveInfo.GetDrives().FirstOrDefault(drive =>
            drive.DriveType == DriveType.Fixed &&
            drive.IsReady &&
            !string.Equals(drive.RootDirectory.FullName, Path.GetPathRoot(_work), StringComparison.OrdinalIgnoreCase));

    private static bool TryCreateJunction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    internal static void DeleteQuietly(string directory)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
