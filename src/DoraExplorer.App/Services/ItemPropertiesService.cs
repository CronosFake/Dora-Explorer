using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// O que a janela Propriedades mostra: lê o que o Windows e o disco sabem sobre um arquivo, uma pasta ou uma unidade locais, soma o
/// que há dentro de pastas (com cancelamento) e calcula o SHA-256 e o MD5 de um arquivo sob demanda. Nada aqui grava no disco.
/// </summary>
public static class ItemPropertiesService
{
    private const int FileAttributeDirectory = 0x10;
    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiUseFileAttributes = 0x10;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".jxr", ".wdp"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".ocx", ".scr", ".cpl", ".msi", ".drv", ".mui"
    };

    /// <summary>As propriedades de um arquivo ou de uma pasta locais, ou <see langword="null"/> se o caminho não existe mais.</summary>
    public static ItemProperties? Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var isDirectory = Directory.Exists(path);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var full = info.FullName.TrimEnd('\\', '/');
            var attributes = info.Attributes;
            var file = info as FileInfo;
            var extension = isDirectory ? string.Empty : info.Extension;
            var length = file?.Length;

            return new ItemProperties(
                Name: info.Name,
                Path: info.FullName,
                Location: isDirectory ? Path.GetDirectoryName(full) : Path.GetDirectoryName(info.FullName),
                IsDirectory: isDirectory,
                Extension: extension,
                TypeDescription: TypeDescriptionOf(info.FullName, isDirectory),
                SizeBytes: length,
                SizeOnDiskBytes: file is null ? null : SizeOnDisk(file.FullName, file.Length, attributes, ClusterSizeOf(file.FullName)),
                Created: SafeTime(() => info.CreationTime),
                Modified: SafeTime(() => info.LastWriteTime),
                Accessed: SafeTime(() => info.LastAccessTime),
                Attributes: attributes,
                LinkTarget: SafeText(() => info.LinkTarget),
                Owner: OwnerOf(info),
                Executable: file is not null && ExecutableExtensions.Contains(extension) ? ExecutableDetailsOf(file.FullName) : null,
                ImageSize: file is not null && ImageExtensions.Contains(extension) ? ImageSizeOf(file.FullName) : null);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    /// <summary>Rótulo, sistema de arquivos e espaço de uma unidade (ou <see langword="null"/> se ela não está pronta).</summary>
    public static DriveDetails? ReadDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return new DriveDetails(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.DriveType.ToString(), drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Quanto há dentro das pastas dadas (e os arquivos soltos que estiverem na lista): arquivos, pastas e bytes. Atalhos de pasta (links
    /// simbólicos e junções) contam como pasta mas não são abertos, para não contar duas vezes nem entrar em ciclo. Pastas sem
    /// permissão entram em <see cref="FolderTotals.Unreadable"/>. <paramref name="progress"/> recebe o parcial de tempos em tempos.
    /// </summary>
    public static Task<FolderTotals> ComputeTotalsAsync(
        IReadOnlyList<string> paths,
        IProgress<FolderTotals>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Totals(paths, progress, cancellationToken), cancellationToken);

    private static FolderTotals Totals(IReadOnlyList<string> paths, IProgress<FolderTotals>? progress, CancellationToken cancellationToken)
    {
        long files = 0, folders = 0, bytes = 0, onDisk = 0, unreadable = 0;
        var clusters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        FolderTotals Snapshot() => new(files, folders, bytes, onDisk, unreadable);

        void CountFile(FileInfo file)
        {
            files++;
            try
            {
                var length = file.Length;
                bytes += length;
                var root = Path.GetPathRoot(file.FullName) ?? string.Empty;
                if (!clusters.TryGetValue(root, out var cluster))
                {
                    cluster = ClusterSizeOf(file.FullName);
                    clusters[root] = cluster;
                }

                onDisk += SizeOnDisk(file.FullName, length, file.Attributes, cluster);
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                unreadable++;
            }
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                CountFile(new FileInfo(path));
            }
            else if (Directory.Exists(path))
            {
                pending.Push(path);
            }
            else
            {
                unreadable++;
            }
        }

        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo subdirectory)
                    {
                        folders++;
                        if (!subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(subdirectory.FullName);
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        CountFile(file);
                    }

                    if (progress is not null && clock.ElapsedMilliseconds - lastReport >= 250)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress.Report(Snapshot());
                    }
                }
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                unreadable++;
            }
        }

        return Snapshot();
    }

    /// <summary>SHA-256 do arquivo, em hexadecimal minúsculo. <paramref name="progress"/> recebe a fração lida (0 a 1).</summary>
    public static async Task<string> ComputeSha256Async(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        (await HashFileAsync(path, [HashAlgorithmName.SHA256], progress, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// SHA-256 e MD5 do arquivo, os dois em hexadecimal minúsculo, lendo o arquivo UMA vez só. O MD5 está aqui por pedido do usuário,
    /// só como soma de conferência (muitos sites publicam o MD5 de um download); não serve para segurança, e por isso a janela
    /// Propriedades mostra o SHA-256 primeiro. <paramref name="progress"/> recebe a fração lida (0 a 1).
    /// </summary>
    public static async Task<FileHashes> ComputeHashesAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hashes = await HashFileAsync(path, [HashAlgorithmName.SHA256, HashAlgorithmName.MD5], progress, cancellationToken).ConfigureAwait(false);
        return new FileHashes(hashes[0], hashes[1]);
    }

    private static async Task<string[]> HashFileAsync(
        string path,
        HashAlgorithmName[] algorithms,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hashes = algorithms.Select(IncrementalHash.CreateHash).ToArray();
        try
        {
            var buffer = new byte[81920];
            var length = stream.Length;
            long read = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var hash in hashes)
                {
                    hash.AppendData(buffer, 0, count);
                }

                read += count;
                progress?.Report(length > 0 ? (double)read / length : 1);
            }

            progress?.Report(1);
            return hashes.Select(hash => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()).ToArray();
        }
        finally
        {
            foreach (var hash in hashes)
            {
                hash.Dispose();
            }
        }
    }

    /// <summary>O nome do tipo que o Windows mostra ("Documento de Texto", "Pasta de arquivos"), sem tocar no disco.</summary>
    public static string TypeDescriptionOf(string path, bool isDirectory)
    {
        try
        {
            var info = new ShFileInfo();
            var attributes = isDirectory ? (uint)FileAttributeDirectory : 0x80u;
            _ = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes);
            if (!string.IsNullOrWhiteSpace(info.TypeName))
            {
                return info.TypeName;
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            // sem o nome do Windows, cai no genérico abaixo
        }

        if (isDirectory)
        {
            return "Pasta de arquivos";
        }

        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length > 0 ? $"Arquivo {extension.ToUpperInvariant()}" : "Arquivo";
    }

    /// <summary>O espaço que o arquivo ocupa no disco: o tamanho arredondado para cima ao cluster, ou o real se for compactado ou esparso.</summary>
    public static long SizeOnDisk(string path, long length, FileAttributes attributes, long clusterSize)
    {
        if (attributes.HasFlag(FileAttributes.Compressed) || attributes.HasFlag(FileAttributes.SparseFile))
        {
            var low = GetCompressedFileSizeW(path, out var high);
            if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0)
            {
                return ((long)high << 32) | low;
            }
        }

        return clusterSize > 0 ? (length + clusterSize - 1) / clusterSize * clusterSize : length;
    }

    /// <summary>O tamanho do cluster do volume onde o caminho está (0 se o Windows não disser).</summary>
    public static long ClusterSizeOf(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return 0;
        }

        return GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 0;
    }

    private static DateTime? SafeTime(Func<DateTime> read)
    {
        try
        {
            var value = read();
            return value.Year < 1700 ? null : value;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    private static string? OwnerOf(FileSystemInfo info)
    {
        try
        {
            FileSystemSecurity security = info is DirectoryInfo directory
                ? directory.GetAccessControl(AccessControlSections.Owner)
                : ((FileInfo)info).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(NTAccount))?.ToString();
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex) || ex is IdentityNotMappedException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static ExecutableDetails? ExecutableDetailsOf(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var details = new ExecutableDetails(
                Clean(version.FileDescription),
                Clean(version.FileVersion),
                Clean(version.ProductName),
                Clean(version.ProductVersion),
                Clean(version.CompanyName),
                Clean(version.LegalCopyright),
                Clean(version.OriginalFilename));

            return details.Description is null && details.FileVersion is null && details.ProductName is null &&
                   details.ProductVersion is null && details.Company is null && details.Copyright is null && details.OriginalFileName is null
                ? null
                : details;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }

        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static (int Width, int Height)? ImageSizeOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex) || ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr HIcon;
        public int IIcon;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
