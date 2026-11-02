using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class FileOperationService : IFileOperationService
{
    public const string CancelledMessage = "Operação cancelada.";

    /// <summary>Sem nenhum byte copiado por tanto tempo, a operação é considerada travada (disco ou rede parou).</summary>
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(2);

    public bool TryCreateDirectory(string parentPath, string folderName, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(folderName))
        {
            errorMessage = "O nome da pasta não pode estar vazio.";
            return false;
        }

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errorMessage = "O nome da pasta contém caracteres inválidos.";
            return false;
        }

        try
        {
            var fullPath = Path.Combine(parentPath, folderName);

            // Directory.CreateDirectory devolve sucesso quando a pasta já existe; sem esta checagem "criar" uma pasta que já
            // existe (ou "." e "..") parecia dar certo, sem criar nada, e o chamador seguia como se a pasta fosse nova.
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                errorMessage = $"Já existe um item chamado \"{folderName}\" nesta pasta.";
                return false;
            }

            Directory.CreateDirectory(fullPath);
            AppLogService.Info($"Pasta criada: {fullPath}");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = FileErrorText.Describe(ex, Path.Combine(parentPath, folderName));
            AppLogService.Error($"Falha ao criar pasta em {parentPath}", ex);
            return false;
        }
    }

    public bool TryRename(string sourcePath, string newName, out string? newFullPath, out string? errorMessage)
    {
        newFullPath = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(newName))
        {
            errorMessage = "O novo nome não pode estar vazio.";
            return false;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errorMessage = "O nome contém caracteres inválidos.";
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(parent))
            {
                errorMessage = "Não é possível renomear este item.";
                return false;
            }

            newFullPath = Path.Combine(parent, newName);
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, newFullPath);
            }
            else if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, newFullPath);
            }
            else
            {
                errorMessage = "O item selecionado não existe.";
                return false;
            }

            AppLogService.Info($"Renomeado: {sourcePath} -> {newFullPath}");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = FileErrorText.Describe(ex, sourcePath);
            AppLogService.Error($"Falha ao renomear {sourcePath}", ex);
            return false;
        }
    }

    public bool TryCopy(string sourcePath, string destinationDirectory, out string? errorMessage) =>
        CopyCore(sourcePath, destinationDirectory, new TransferProgressTracker(null, null), CancellationToken.None, out errorMessage);

    public bool TryMove(string sourcePath, string destinationDirectory, out string? errorMessage) =>
        MoveCore(sourcePath, destinationDirectory, new TransferProgressTracker(null, null), CancellationToken.None, out errorMessage);

    public Task<(bool Success, string? ErrorMessage)> TryCopyAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        TryCopyAsync(sourcePath, destinationDirectory, progress: null, cancellationToken);

    public Task<(bool Success, string? ErrorMessage)> TryCopyAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default) =>
        RunFileOperationAsync(
            (token, keepAlive) =>
            {
                var success = CopyCore(sourcePath, destinationDirectory, new TransferProgressTracker(progress, keepAlive), token, out var error);
                return (success, error);
            },
            cancellationToken);

    public Task<(bool Success, string? ErrorMessage)> TryMoveAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        TryMoveAsync(sourcePath, destinationDirectory, progress: null, cancellationToken);

    public Task<(bool Success, string? ErrorMessage)> TryMoveAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default) =>
        RunFileOperationAsync(
            (token, keepAlive) =>
            {
                var success = MoveCore(sourcePath, destinationDirectory, new TransferProgressTracker(progress, keepAlive), token, out var error);
                return (success, error);
            },
            cancellationToken);

    public Task<(bool Success, string? ArchivePath, string? ErrorMessage)> TryCompressAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        TryCompressAsync(sourcePaths, destinationDirectory, format, progress: null, cancellationToken);

    public async Task<(bool Success, string? ArchivePath, string? ErrorMessage)> TryCompressAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await OperationTimeouts.RunWithInactivityAsync(
                (token, keepAlive) =>
                {
                    var success = CompressCore(
                        sourcePaths, destinationDirectory, format, new TransferProgressTracker(progress, keepAlive), token,
                        out var archivePath, out var error);
                    return (success, archivePath, error);
                },
                InactivityTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null, $"A operação parou de responder: nenhum dado foi processado por {InactivityTimeout.TotalMinutes:0} minutos.");
        }
        catch (OperationCanceledException)
        {
            return (false, null, CancelledMessage);
        }
    }
    private static async Task<(bool Success, string? ErrorMessage)> RunFileOperationAsync(
        Func<CancellationToken, Action, (bool Success, string? ErrorMessage)> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            // O limite vale para a falta de progresso, não para a duração: uma cópia de 100 GB pode levar horas,
            // desde que os bytes continuem andando.
            return await OperationTimeouts.RunWithInactivityAsync(operation, InactivityTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"A operação parou de responder: nenhum dado foi copiado por {InactivityTimeout.TotalMinutes:0} minutos.");
        }
        catch (OperationCanceledException)
        {
            return (false, CancelledMessage);
        }
    }

    private static bool CopyCore(
        string sourcePath,
        string destinationDirectory,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        errorMessage = null;
        string? destinationPath = null;

        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                errorMessage = "A pasta de destino não existe.";
                return false;
            }

            destinationPath = GetUniqueDestinationPath(destinationDirectory, Path.GetFileName(sourcePath));

            if (File.Exists(sourcePath))
            {
                var length = new FileInfo(sourcePath).Length;
                tracker.SetTotals(length, 1);
                CopySingleFile(sourcePath, destinationPath, length, tracker, cancellationToken);
            }
            else if (Directory.Exists(sourcePath))
            {
                if (tracker.WantsTotals)
                {
                    var (bytes, files) = TransferScanner.ScanItem(sourcePath, tracker.Heartbeat, cancellationToken);
                    tracker.SetTotals(bytes, files);
                }

                CopyDirectory(sourcePath, destinationPath, LinkPolicy.Skip, tracker, cancellationToken);
            }
            else
            {
                errorMessage = "O item de origem não existe.";
                return false;
            }

            tracker.Complete();
            AppLogService.Info($"Copiado: {sourcePath} -> {destinationPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancelado (ou parado por inatividade): não deixa uma cópia incompleta para trás.
            DeletePartial(destinationPath);
            errorMessage = CancelledMessage;
            AppLogService.Warning($"Cópia cancelada: {sourcePath}");
            return false;
        }
        catch (Exception ex)
        {
            DeletePartial(destinationPath);
            errorMessage = FileErrorText.Describe(ex, sourcePath);
            AppLogService.Error($"Falha ao copiar {sourcePath}", ex);
            return false;
        }
    }

    private static void CopySingleFile(
        string source,
        string destination,
        long length,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.BeginFile(source);
        NativeFileCopy.CopyFile(source, destination, tracker.FileProgress, cancellationToken);
        tracker.EndFile(length);
    }

    private static bool MoveCore(
        string sourcePath,
        string destinationDirectory,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        errorMessage = null;
        string? destinationPath = null;

        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                errorMessage = "A pasta de destino não existe.";
                return false;
            }

            destinationPath = GetUniqueDestinationPath(destinationDirectory, Path.GetFileName(sourcePath));

            if (File.Exists(sourcePath))
            {
                var length = new FileInfo(sourcePath).Length;
                tracker.SetTotals(length, 1);
                tracker.BeginFile(sourcePath);
                NativeFileCopy.MoveFile(sourcePath, destinationPath, tracker.FileProgress, cancellationToken);
                tracker.EndFile(length);
            }
            else if (Directory.Exists(sourcePath))
            {
                if (OnSameVolume(sourcePath, destinationPath))
                {
                    Directory.Move(sourcePath, destinationPath);
                }
                else
                {
                    if (tracker.WantsTotals)
                    {
                        var (bytes, files) = TransferScanner.ScanItem(sourcePath, tracker.Heartbeat, cancellationToken);
                        tracker.SetTotals(bytes, files);
                    }

                    MoveDirectoryAcrossVolumes(sourcePath, destinationPath, tracker, cancellationToken);
                }
            }
            else
            {
                errorMessage = "O item de origem não existe.";
                return false;
            }

            tracker.Complete();
            AppLogService.Info($"Movido: {sourcePath} -> {destinationPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Mover entre unidades copia primeiro: se foi interrompido, a origem continua inteira e o destino parcial sai.
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                DeletePartial(destinationPath);
            }

            errorMessage = CancelledMessage;
            AppLogService.Warning($"Movimentação cancelada: {sourcePath}");
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex is SourceNotRemovedException ? ex.Message : FileErrorText.Describe(ex, sourcePath);
            AppLogService.Error($"Falha ao mover {sourcePath}", ex);
            return false;
        }
    }
    private static bool OnSameVolume(string first, string second) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(first)),
            Path.GetPathRoot(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="Directory.Move(string, string)"/> não funciona entre unidades: copia a pasta e só apaga
    /// a origem depois de a cópia terminar. Se a cópia falhar, o destino parcial é removido e a origem
    /// fica intacta.
    /// </summary>
    private static void MoveDirectoryAcrossVolumes(
        string sourcePath,
        string destinationPath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        try
        {
            CopyDirectory(sourcePath, destinationPath, LinkPolicy.Fail, tracker, cancellationToken);
        }
        catch (Exception)
        {
            DeletePartial(destinationPath);
            throw;
        }

        tracker.Heartbeat();
        try
        {
            Directory.Delete(sourcePath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceNotRemovedException(
                $"A pasta foi copiada para '{FileErrorText.DisplayPath(destinationPath)}', mas não foi possível apagar a origem. " +
                FileErrorText.Describe(ex, sourcePath),
                ex);
        }
    }

    /// <summary>A cópia entre unidades deu certo, mas apagar a origem falhou: a mensagem já está pronta, em português.</summary>
    private sealed class SourceNotRemovedException(string message, Exception inner) : IOException(message, inner);

    public bool TryDelete(IReadOnlyList<string> paths, FileDeleteMode mode, out string? errorMessage)
    {
        return mode switch
        {
            FileDeleteMode.RecycleBin => TrySendToRecycleBin(paths, out errorMessage),
            FileDeleteMode.Permanent => TryDeletePermanently(paths, out errorMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    public bool TrySendToRecycleBin(IReadOnlyList<string> paths, out string? errorMessage)
    {
        errorMessage = null;

        var existing = GetExistingPaths(paths);
        if (existing.Count == 0)
        {
            errorMessage = "Os itens selecionados não existem.";
            return false;
        }

        var from = string.Join('\0', existing) + "\0\0";
        var fileOp = new SHFILEOPSTRUCT
        {
            wFunc = FileOperationType.FoDelete,
            pFrom = from,
            fFlags = FileOperationFlags.FofAllowUndo |
                     FileOperationFlags.FofNoConfirmation |
                     FileOperationFlags.FofSilent |
                     FileOperationFlags.FofNoErrorUi
        };

        var result = SHFileOperation(ref fileOp);
        if (result != 0 || fileOp.fAnyOperationsAborted)
        {
            errorMessage = FileErrorText.IsAccessDeniedCode(result)
                ? existing.Count == 1
                    ? FileErrorText.AccessDenied(existing[0])
                    : FileErrorText.AccessDeniedForSome("Não foi possível mover para a lixeira")
                : "Não foi possível mover para a lixeira.";
            AppLogService.Error($"Falha ao mover para a lixeira: {string.Join(", ", existing)} (código {result})");
            return false;
        }

        AppLogService.Info($"Movido para a lixeira: {string.Join(", ", existing)}");
        return true;
    }

    public bool TryDeletePermanently(IReadOnlyList<string> paths, out string? errorMessage)
    {
        errorMessage = null;

        var existing = GetExistingPaths(paths);
        if (existing.Count == 0)
        {
            errorMessage = "Os itens selecionados não existem.";
            return false;
        }

        var failures = new List<string>();
        foreach (var path in existing)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    continue;
                }

                AppLogService.Info($"Excluído permanentemente: {path}");
            }
            catch (Exception ex)
            {
                failures.Add(FileErrorText.Describe(ex, path));
                AppLogService.Error($"Falha ao excluir permanentemente {path}", ex);
            }
        }

        if (failures.Count == existing.Count)
        {
            errorMessage = failures.Count == 1
                ? failures[0]
                : string.Join(Environment.NewLine, failures);
            return false;
        }

        if (failures.Count > 0)
        {
            errorMessage = "Alguns itens não puderam ser excluídos:" + Environment.NewLine +
                           string.Join(Environment.NewLine, failures);
            return false;
        }

        return true;
    }

    public bool TryCompress(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        out string? archivePath,
        out string? errorMessage) =>
        CompressCore(sourcePaths, destinationDirectory, format, new TransferProgressTracker(null, null), CancellationToken.None, out archivePath, out errorMessage);

    private static bool CompressCore(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken,
        out string? archivePath,
        out string? errorMessage)
    {
        archivePath = null;
        errorMessage = null;

        var existing = GetExistingPaths(sourcePaths);
        if (existing.Count == 0)
        {
            errorMessage = "Os itens selecionados não existem.";
            return false;
        }

        if (!Directory.Exists(destinationDirectory))
        {
            errorMessage = "A pasta de destino não existe.";
            return false;
        }

        string? createdArchive = null;
        try
        {
            var archiveName = BuildArchiveName(existing, format);
            createdArchive = GetUniqueDestinationPath(destinationDirectory, archiveName);

            if (tracker.WantsTotals)
            {
                var (bytes, files) = TransferScanner.Scan(existing, tracker.Heartbeat, cancellationToken);
                tracker.SetTotals(bytes, files);
            }

            switch (format)
            {
                case ArchiveFormat.Zip:
                    CreateZipArchive(existing, createdArchive, tracker, cancellationToken);
                    break;
                case ArchiveFormat.TarGz:
                    CreateTarGzArchive(existing, createdArchive, tracker, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }

            tracker.Complete();
            archivePath = createdArchive;
            AppLogService.Info($"Compactado: {string.Join(", ", existing)} -> {archivePath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            DeletePartial(createdArchive);
            errorMessage = CancelledMessage;
            AppLogService.Warning($"Compactação cancelada: {string.Join(", ", existing)}");
            return false;
        }
        catch (Exception ex)
        {
            // Um arquivo parcial seria um ZIP/TAR corrompido: remove-o.
            DeletePartial(createdArchive);
            errorMessage = FileErrorText.Describe(ex, createdArchive ?? destinationDirectory);
            AppLogService.Error($"Falha ao compactar {string.Join(", ", existing)}", ex);
            return false;
        }
    }

    private static string BuildArchiveName(IReadOnlyList<string> sourcePaths, ArchiveFormat format)
    {
        var extension = format switch
        {
            ArchiveFormat.Zip => ".zip",
            ArchiveFormat.TarGz => ".tar.gz",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

        var baseName = sourcePaths.Count == 1
            ? Path.GetFileName(sourcePaths[0])
            : "Arquivo";

        return $"{baseName}{extension}";
    }

    private const UnixFileMode DefaultTarFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static void CreateZipArchive(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(sourcePath);

            if (File.Exists(sourcePath))
            {
                AddFileToZip(archive, sourcePath, entryName, tracker, cancellationToken);
            }
            else
            {
                AddDirectoryToZip(archive, sourcePath, entryName + "/", tracker, cancellationToken);
            }
        }
    }

    // Lê o arquivo em blocos: o andamento e o cancelamento valem no meio de um arquivo grande
    // (CreateEntryFromFile compactaria o arquivo inteiro sem dar sinal).
    private static void AddFileToZip(
        ZipArchive archive,
        string file,
        string entryName,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.BeginFile(file);
        using var input = new ProgressReadStream(OpenForArchive(file), tracker, cancellationToken);
        var entry = archive.CreateEntry(NormalizeArchiveEntryPath(entryName), CompressionLevel.Optimal);
        entry.LastWriteTime = ClampZipTime(File.GetLastWriteTime(file));

        using (var output = entry.Open())
        {
            input.CopyTo(output, 1 << 17);
        }

        tracker.EndFile(input.Length);
    }

    // O formato ZIP só guarda datas entre 1980 e 2107.
    private static DateTimeOffset ClampZipTime(DateTime time)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        return time < min ? min : time > max ? max : time;
    }

    private static FileStream OpenForArchive(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static void AddDirectoryToZip(
        ZipArchive archive,
        string directoryPath,
        string entryPrefix,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        archive.CreateEntry(NormalizeArchiveEntryPath(entryPrefix));

        foreach (var file in Directory.GetFiles(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFileToZip(archive, file, entryPrefix + Path.GetFileName(file), tracker, cancellationToken);
        }

        foreach (var directory in Directory.GetDirectories(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsLink(directory))
            {
                continue; // sem isto, uma junção que aponta para um ancestral compactaria para sempre
            }

            AddDirectoryToZip(archive, directory, entryPrefix + Path.GetFileName(directory) + "/", tracker, cancellationToken);
        }
    }

    private static bool IsLink(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        AppLogService.Warning($"Link simbólico/junção ignorado ao compactar: {directory}");
        return true;
    }

    private static void CreateTarGzArchive(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        using var fileStream = File.Create(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Gnu, leaveOpen: false);

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(sourcePath);

            if (File.Exists(sourcePath))
            {
                AddFileToTar(tarWriter, sourcePath, entryName, tracker, cancellationToken);
            }
            else
            {
                AddDirectoryToTar(tarWriter, sourcePath, entryName, tracker, cancellationToken);
            }
        }
    }

    private static void AddFileToTar(
        TarWriter tarWriter,
        string file,
        string entryName,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.BeginFile(file);
        using var input = new ProgressReadStream(OpenForArchive(file), tracker, cancellationToken);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, NormalizeArchiveEntryPath(entryName))
        {
            ModificationTime = File.GetLastWriteTimeUtc(file),
            Mode = DefaultTarFileMode,
            DataStream = input
        };

        tarWriter.WriteEntry(entry);
        tracker.EndFile(input.Length);
    }

    private static void AddDirectoryToTar(
        TarWriter tarWriter,
        string directoryPath,
        string entryPrefix,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var directoryEntry = new GnuTarEntry(TarEntryType.Directory, NormalizeArchiveEntryPath(entryPrefix))
        {
            ModificationTime = Directory.GetLastWriteTimeUtc(directoryPath)
        };
        tarWriter.WriteEntry(directoryEntry);

        foreach (var file in Directory.GetFiles(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFileToTar(tarWriter, file, entryPrefix + "/" + Path.GetFileName(file), tracker, cancellationToken);
        }

        foreach (var directory in Directory.GetDirectories(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsLink(directory))
            {
                continue;
            }

            AddDirectoryToTar(tarWriter, directory, entryPrefix + "/" + Path.GetFileName(directory), tracker, cancellationToken);
        }
    }
    private static string NormalizeArchiveEntryPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string GetUniqueDestinationPath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        SplitArchiveName(name, out var baseName, out var extension);
        var index = 1;

        while (true)
        {
            var numbered = string.IsNullOrEmpty(extension)
                ? $"{baseName} ({index})"
                : $"{baseName} ({index}){extension}";

            candidate = Path.Combine(directory, numbered);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static void SplitArchiveName(string name, out string baseName, out string extension)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            baseName = name[..^7];
            extension = ".tar.gz";
            return;
        }

        baseName = Path.GetFileNameWithoutExtension(name);
        extension = Path.GetExtension(name);
    }

    private static List<string> GetExistingPaths(IReadOnlyList<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public FileOperationType wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public FileOperationFlags fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    private enum FileOperationType : uint
    {
        FoDelete = 0x0003
    }

    [Flags]
    private enum FileOperationFlags : ushort
    {
        FofSilent = 0x0004,
        FofNoConfirmation = 0x0010,
        FofAllowUndo = 0x0040,
        FofNoErrorUi = 0x0400
    }

    private enum LinkPolicy
    {
        /// <summary>Ignora links simbólicos e junções (evita ciclos e cópias gigantes).</summary>
        Skip,

        /// <summary>Falha: usado ao mover, para não perder o link ao apagar a origem.</summary>
        Fail
    }

    private static void CopyDirectory(
        string sourceDir,
        string destinationDir,
        LinkPolicy links,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destinationDir, Path.GetFileName(file));
            CopySingleFile(file, target, new FileInfo(file).Length, tracker, cancellationToken);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                if (links == LinkPolicy.Fail)
                {
                    throw new IOException(
                        $"A pasta contém um link simbólico ou junção que não pode ser movido entre unidades: {directory}");
                }

                AppLogService.Warning($"Link simbólico/junção ignorado ao copiar: {directory}");
                continue;
            }

            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)), links, tracker, cancellationToken);
        }
    }
    private static void DeletePartial(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Não foi possível remover o resultado parcial '{path}': {ex.Message}");
        }
    }
}
