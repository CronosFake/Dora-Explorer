using System.IO;
using SMBLibrary;
using SMBFileAttributes = SMBLibrary.FileAttributes;

namespace DoraExplorer.App.Services;

/// <summary>
/// Pasta compartilhada do servidor SMB: implementa o <see cref="INTFileStore"/> da SMBLibrary sobre <c>System.IO</c>.
/// Todo caminho do cliente passa por <see cref="ServerVirtualPath"/> (nada sai da pasta, nem por "..", nem por atalho,
/// nem por nome reservado do Windows), o modo somente leitura recusa qualquer alteração e ainda não há travas de
/// trecho de arquivo, notificações de mudança nem segurança por item (ACL).
/// </summary>
internal sealed class SmbDirectoryFileStore(string rootPath, string shareName, bool readOnly, Action<string> log) : INTFileStore
{
    private const uint FileReadData = 0x1;
    private const uint FileWriteData = 0x2;
    private const uint FileAppendData = 0x4;
    private const uint FileExecute = 0x20;
    private const uint FileDeleteChild = 0x40;
    private const long AllocationUnit = 4096;

    private const uint WriteMask = FileWriteData | FileAppendData | FileDeleteChild
        | (uint)AccessMask.DELETE | (uint)AccessMask.GENERIC_WRITE | (uint)AccessMask.GENERIC_ALL;

    private const uint ReadMask = FileReadData | FileExecute
        | (uint)AccessMask.GENERIC_READ | (uint)AccessMask.GENERIC_EXECUTE | (uint)AccessMask.GENERIC_ALL
        | (uint)AccessMask.MAXIMUM_ALLOWED;

    private sealed class OpenItem
    {
        public required string VirtualPath { get; set; }

        public required string RealPath { get; set; }

        public required bool IsDirectory { get; init; }

        public bool CanRead { get; init; }

        public bool CanWrite { get; init; }

        public bool DeleteOnClose { get; set; }

        public FileStream? Stream { get; set; }

        public object Sync { get; } = new();
    }

    public NTStatus CreateFile(
        out object handle,
        out FileStatus fileStatus,
        string path,
        AccessMask desiredAccess,
        SMBFileAttributes fileAttributes,
        ShareAccess shareAccess,
        CreateDisposition createDisposition,
        CreateOptions createOptions,
        SecurityContext securityContext)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        if (!ServerVirtualPath.TryResolve(rootPath, path.Replace('\\', '/'), out var real, out var virtualPath))
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        var access = (uint)desiredAccess;
        var directoryExists = Directory.Exists(real);
        var fileExists = !directoryExists && File.Exists(real);
        var exists = directoryExists || fileExists;
        var wantsDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;
        var forbidsDirectory = (createOptions & CreateOptions.FILE_NON_DIRECTORY_FILE) != 0;
        var deleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0;
        var writesData = (access & WriteMask) != 0 || deleteOnClose;

        var creates = createDisposition is CreateDisposition.FILE_CREATE or CreateDisposition.FILE_OPEN_IF
            or CreateDisposition.FILE_OVERWRITE_IF or CreateDisposition.FILE_SUPERSEDE;
        var replaces = createDisposition is CreateDisposition.FILE_OVERWRITE or CreateDisposition.FILE_OVERWRITE_IF
            or CreateDisposition.FILE_SUPERSEDE;

        if (readOnly && (writesData || replaces || (creates && !exists)))
        {
            log($"Alteração recusada (somente leitura): {virtualPath}");
            return NTStatus.STATUS_MEDIA_WRITE_PROTECTED;
        }

        if (exists)
        {
            if (directoryExists && forbidsDirectory)
            {
                return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
            }

            if (fileExists && wantsDirectory)
            {
                return NTStatus.STATUS_NOT_A_DIRECTORY;
            }

            if (createDisposition == CreateDisposition.FILE_CREATE)
            {
                fileStatus = FileStatus.FILE_EXISTS;
                return NTStatus.STATUS_OBJECT_NAME_COLLISION;
            }

            if (directoryExists && replaces)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }

            if (virtualPath == "/" && deleteOnClose)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }

            try
            {
                if (fileExists && replaces)
                {
                    using var truncated = new FileStream(real, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fileStatus = createDisposition == CreateDisposition.FILE_SUPERSEDE
                        ? FileStatus.FILE_SUPERSEDED
                        : FileStatus.FILE_OVERWRITTEN;
                }
                else
                {
                    fileStatus = FileStatus.FILE_OPENED;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Map(ex);
            }
        }
        else
        {
            if (!creates)
            {
                return Directory.Exists(Path.GetDirectoryName(real)) ? NTStatus.STATUS_OBJECT_NAME_NOT_FOUND : NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            }

            var parent = Path.GetDirectoryName(real);
            if (parent is null || !Directory.Exists(parent))
            {
                return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            }

            try
            {
                if (wantsDirectory)
                {
                    Directory.CreateDirectory(real);
                    log($"Pasta criada: {virtualPath}");
                }
                else
                {
                    using (new FileStream(real, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }

                    log($"Arquivo criado: {virtualPath}");
                }

                fileStatus = FileStatus.FILE_CREATED;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Map(ex);
            }
        }

        var isDirectory = wantsDirectory || directoryExists;
        handle = new OpenItem
        {
            VirtualPath = virtualPath,
            RealPath = real,
            IsDirectory = isDirectory,
            CanRead = !isDirectory && (access & ReadMask) != 0,
            CanWrite = !isDirectory && !readOnly && (access & (FileWriteData | FileAppendData | (uint)AccessMask.GENERIC_WRITE
                | (uint)AccessMask.GENERIC_ALL | (uint)AccessMask.MAXIMUM_ALLOWED)) != 0,
            DeleteOnClose = deleteOnClose
        };
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus CloseFile(object handle)
    {
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        lock (item.Sync)
        {
            item.Stream?.Dispose();
            item.Stream = null;

            if (item.DeleteOnClose && !readOnly)
            {
                try
                {
                    if (item.IsDirectory)
                    {
                        Directory.Delete(item.RealPath, recursive: false);
                    }
                    else
                    {
                        File.Delete(item.RealPath);
                    }

                    log($"Apagado: {item.VirtualPath}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"Não foi possível apagar {item.VirtualPath}: {ex.Message}");
                }
            }
        }

        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = [];
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        if (item.IsDirectory)
        {
            return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!item.CanRead)
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        try
        {
            lock (item.Sync)
            {
                var stream = Stream(item);
                if (offset < 0)
                {
                    return NTStatus.STATUS_INVALID_PARAMETER;
                }

                if (offset >= stream.Length)
                {
                    return maxCount == 0 ? NTStatus.STATUS_SUCCESS : NTStatus.STATUS_END_OF_FILE;
                }

                var buffer = new byte[(int)Math.Min(maxCount, stream.Length - offset)];
                stream.Position = offset;
                var read = 0;
                while (read < buffer.Length)
                {
                    var got = stream.Read(buffer, read, buffer.Length - read);
                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                }

                data = read == buffer.Length ? buffer : buffer[..read];
                return NTStatus.STATUS_SUCCESS;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        if (item.IsDirectory)
        {
            return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!item.CanWrite)
        {
            return readOnly ? NTStatus.STATUS_MEDIA_WRITE_PROTECTED : NTStatus.STATUS_ACCESS_DENIED;
        }

        if (offset < 0)
        {
            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        try
        {
            lock (item.Sync)
            {
                var stream = Stream(item);
                stream.Position = offset;
                stream.Write(data, 0, data.Length);
                numberOfBytesWritten = data.Length;
                return NTStatus.STATUS_SUCCESS;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }
    }

    public NTStatus FlushFileBuffers(object handle)
    {
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        try
        {
            lock (item.Sync)
            {
                item.Stream?.Flush(flushToDisk: true);
            }

            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }
    }

    // Sem travas de trecho: aceita o pedido (um servidor que recusa quebra programas que travam o arquivo que abrem).
    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) =>
        handle is OpenItem ? NTStatus.STATUS_SUCCESS : NTStatus.STATUS_INVALID_HANDLE;

    public NTStatus UnlockFile(object handle, long byteOffset, long length) =>
        handle is OpenItem ? NTStatus.STATUS_SUCCESS : NTStatus.STATUS_INVALID_HANDLE;

    private FileStream Stream(OpenItem item)
    {
        if (item.Stream is not null)
        {
            return item.Stream;
        }

        item.Stream = new FileStream(
            item.RealPath,
            FileMode.Open,
            item.CanWrite ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1,
            FileOptions.RandomAccess);
        return item.Stream;
    }

    // ---- Pastas ------------------------------------------------------------------------------

    public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle,
        string fileName,
        FileInformationClass informationClass)
    {
        result = [];
        if (handle is not OpenItem { IsDirectory: true } item)
        {
            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        if (informationClass is not (FileInformationClass.FileDirectoryInformation
            or FileInformationClass.FileFullDirectoryInformation
            or FileInformationClass.FileBothDirectoryInformation
            or FileInformationClass.FileNamesInformation
            or FileInformationClass.FileIdBothDirectoryInformation
            or FileInformationClass.FileIdFullDirectoryInformation))
        {
            return NTStatus.STATUS_INVALID_INFO_CLASS;
        }

        var pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
        try
        {
            var directory = new DirectoryInfo(item.RealPath);
            var entries = new List<FileSystemInfo>();
            entries.AddRange(directory.EnumerateFileSystemInfos().Where(IsVisible).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

            if (SmbWildcard.IsMatch(pattern, "."))
            {
                result.Add(Entry(informationClass, ".", directory, item.VirtualPath));
            }

            if (SmbWildcard.IsMatch(pattern, ".."))
            {
                result.Add(Entry(informationClass, "..", directory.Parent ?? directory, ParentOf(item.VirtualPath)));
            }

            foreach (var entry in entries.Where(e => SmbWildcard.IsMatch(pattern, e.Name)))
            {
                result.Add(Entry(informationClass, entry.Name, entry, Combine(item.VirtualPath, entry.Name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }

        return result.Count == 0 ? NTStatus.STATUS_NO_SUCH_FILE : NTStatus.STATUS_SUCCESS;
    }

    private bool IsVisible(FileSystemInfo entry)
    {
        if (!ServerVirtualPath.IsSafeSegment(entry.Name))
        {
            return false;
        }

        // Atalho e junção: só aparecem se o destino também estiver dentro da pasta compartilhada.
        return (entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0
               || ServerPathGuard.IsInside(rootPath, entry.FullName, allowRoot: false);
    }

    private static QueryDirectoryFileInformation Entry(FileInformationClass informationClass, string name, FileSystemInfo info, string virtualPath)
    {
        var isDirectory = info is DirectoryInfo;
        var length = isDirectory ? 0 : ((FileInfo)info).Length;
        var attributes = AttributesOf(info);
        var created = Utc(info.CreationTimeUtc);
        var accessed = Utc(info.LastAccessTimeUtc);
        var written = Utc(info.LastWriteTimeUtc);
        var allocation = Allocation(length);
        var fileId = StableId(virtualPath);

        switch (informationClass)
        {
            case FileInformationClass.FileNamesInformation:
                return new FileNamesInformation { FileName = name };
            case FileInformationClass.FileFullDirectoryInformation:
                return new FileFullDirectoryInformation
                {
                    CreationTime = created, LastAccessTime = accessed, LastWriteTime = written, ChangeTime = written,
                    EndOfFile = length, AllocationSize = allocation, FileAttributes = attributes, FileName = name
                };
            case FileInformationClass.FileBothDirectoryInformation:
                return new FileBothDirectoryInformation
                {
                    CreationTime = created, LastAccessTime = accessed, LastWriteTime = written, ChangeTime = written,
                    EndOfFile = length, AllocationSize = allocation, FileAttributes = attributes, FileName = name,
                    ShortName = string.Empty
                };
            case FileInformationClass.FileIdBothDirectoryInformation:
                return new FileIdBothDirectoryInformation
                {
                    CreationTime = created, LastAccessTime = accessed, LastWriteTime = written, ChangeTime = written,
                    EndOfFile = length, AllocationSize = allocation, FileAttributes = attributes, FileName = name,
                    ShortName = string.Empty, FileId = fileId
                };
            case FileInformationClass.FileIdFullDirectoryInformation:
                return new FileIdFullDirectoryInformation
                {
                    CreationTime = created, LastAccessTime = accessed, LastWriteTime = written, ChangeTime = written,
                    EndOfFile = length, AllocationSize = allocation, FileAttributes = attributes, FileName = name,
                    FileId = fileId
                };
            default:
                return new FileDirectoryInformation
                {
                    CreationTime = created, LastAccessTime = accessed, LastWriteTime = written, ChangeTime = written,
                    EndOfFile = length, AllocationSize = allocation, FileAttributes = attributes, FileName = name
                };
        }
    }

    // ---- Informações de arquivo --------------------------------------------------------------

    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
    {
        result = null!;
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        FileSystemInfo? info = item.IsDirectory ? new DirectoryInfo(item.RealPath) : new FileInfo(item.RealPath);
        if (!info.Exists)
        {
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }

        var length = info is FileInfo file ? file.Length : 0;
        switch (informationClass)
        {
            case FileInformationClass.FileBasicInformation:
                result = BasicOf(info);
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileStandardInformation:
                result = StandardOf(item, length);
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileInternalInformation:
                result = new FileInternalInformation { IndexNumber = (long)StableId(item.VirtualPath) };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileEaInformation:
                result = new FileEaInformation { EaSize = 0 };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileAccessInformation:
                result = new FileAccessInformation { AccessFlags = AccessFor(item) };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FilePositionInformation:
                result = new FilePositionInformation { CurrentByteOffset = 0 };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileModeInformation:
                result = new FileModeInformation { FileMode = 0 };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileAlignmentInformation:
                result = new FileAlignmentInformation { AlignmentRequirement = 0 };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileNameInformation:
                result = new FileNameInformation { FileName = ToWindowsPath(item.VirtualPath) };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileAllInformation:
                result = new FileAllInformation
                {
                    BasicInformation = BasicOf(info),
                    StandardInformation = StandardOf(item, length),
                    InternalInformation = new FileInternalInformation { IndexNumber = (long)StableId(item.VirtualPath) },
                    EaInformation = new FileEaInformation(),
                    AccessInformation = new FileAccessInformation { AccessFlags = AccessFor(item) },
                    PositionInformation = new FilePositionInformation(),
                    ModeInformation = new FileModeInformation(),
                    AlignmentInformation = new FileAlignmentInformation(),
                    NameInformation = new FileNameInformation { FileName = ToWindowsPath(item.VirtualPath) }
                };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileNetworkOpenInformation:
                result = new FileNetworkOpenInformation
                {
                    CreationTime = Utc(info.CreationTimeUtc),
                    LastAccessTime = Utc(info.LastAccessTimeUtc),
                    LastWriteTime = Utc(info.LastWriteTimeUtc),
                    ChangeTime = Utc(info.LastWriteTimeUtc),
                    AllocationSize = Allocation(length),
                    EndOfFile = length,
                    FileAttributes = AttributesOf(info)
                };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileAttributeTagInformation:
                result = new FileAttributeTagInformation { FileAttributes = AttributesOf(info), ReparsePointTag = 0 };
                return NTStatus.STATUS_SUCCESS;
            case FileInformationClass.FileAlternateNameInformation:
                return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND; // sem nomes curtos (8.3)
            default:
                return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        if (handle is not OpenItem item)
        {
            return NTStatus.STATUS_INVALID_HANDLE;
        }

        switch (information)
        {
            case FileBasicInformation basic:
                if (readOnly)
                {
                    return NTStatus.STATUS_MEDIA_WRITE_PROTECTED;
                }

                try
                {
                    if (basic.LastWriteTime is { Time: { } time, MustNotChange: false })
                    {
                        if (item.IsDirectory)
                        {
                            Directory.SetLastWriteTimeUtc(item.RealPath, time.ToUniversalTime());
                        }
                        else
                        {
                            File.SetLastWriteTimeUtc(item.RealPath, time.ToUniversalTime());
                        }
                    }

                    return NTStatus.STATUS_SUCCESS;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Map(ex);
                }

            case FileDispositionInformation disposition:
                return SetDisposition(item, disposition.DeletePending);

            case FileRenameInformationType2 rename:
                return Rename(item, rename);

            case FileEndOfFileInformation endOfFile:
                return SetLength(item, endOfFile.EndOfFile);

            case FileAllocationInformation or FilePositionInformation or FileModeInformation:
                return NTStatus.STATUS_SUCCESS; // sem efeito aqui

            default:
                return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    private NTStatus SetDisposition(OpenItem item, bool deletePending)
    {
        if (!deletePending)
        {
            item.DeleteOnClose = false;
            return NTStatus.STATUS_SUCCESS;
        }

        if (readOnly)
        {
            return NTStatus.STATUS_MEDIA_WRITE_PROTECTED;
        }

        if (item.VirtualPath == "/")
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        if (item.IsDirectory && Directory.EnumerateFileSystemEntries(item.RealPath).Any())
        {
            return NTStatus.STATUS_DIRECTORY_NOT_EMPTY;
        }

        item.DeleteOnClose = true;
        return NTStatus.STATUS_SUCCESS;
    }

    private NTStatus SetLength(OpenItem item, long length)
    {
        if (item.IsDirectory)
        {
            return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!item.CanWrite || length < 0)
        {
            return readOnly ? NTStatus.STATUS_MEDIA_WRITE_PROTECTED : NTStatus.STATUS_ACCESS_DENIED;
        }

        try
        {
            lock (item.Sync)
            {
                Stream(item).SetLength(length);
            }

            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }
    }

    private NTStatus Rename(OpenItem item, FileRenameInformationType2 rename)
    {
        if (readOnly)
        {
            return NTStatus.STATUS_MEDIA_WRITE_PROTECTED;
        }

        if (item.VirtualPath == "/")
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        if (!ServerVirtualPath.TryResolve(rootPath, rename.FileName.Replace('\\', '/'), out var targetReal, out var targetVirtual)
            || targetVirtual == "/")
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (item.IsDirectory && targetVirtual.StartsWith(item.VirtualPath + "/", comparison))
        {
            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        var parent = Path.GetDirectoryName(targetReal);
        if (parent is null || !Directory.Exists(parent))
        {
            return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
        }

        var targetExists = File.Exists(targetReal) || Directory.Exists(targetReal);
        var sameItem = string.Equals(targetReal, item.RealPath, comparison);
        if (targetExists && !sameItem && !rename.ReplaceIfExists)
        {
            return NTStatus.STATUS_OBJECT_NAME_COLLISION;
        }

        try
        {
            lock (item.Sync)
            {
                item.Stream?.Dispose();
                item.Stream = null;

                if (item.IsDirectory)
                {
                    if (targetExists && !sameItem)
                    {
                        if (File.Exists(targetReal) || Directory.EnumerateFileSystemEntries(targetReal).Any())
                        {
                            return NTStatus.STATUS_ACCESS_DENIED;
                        }

                        Directory.Delete(targetReal, recursive: false);
                    }

                    Directory.Move(item.RealPath, targetReal);
                }
                else
                {
                    if (Directory.Exists(targetReal))
                    {
                        return NTStatus.STATUS_ACCESS_DENIED;
                    }

                    File.Move(item.RealPath, targetReal, overwrite: rename.ReplaceIfExists || sameItem);
                }

                log($"Renomeado: {item.VirtualPath} -> {targetVirtual}");
                item.VirtualPath = targetVirtual;
                item.RealPath = targetReal;
            }

            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Map(ex);
        }
    }

    // ---- Sistema de arquivos -----------------------------------------------------------------

    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
    {
        result = null!;
        switch (informationClass)
        {
            case FileSystemInformationClass.FileFsSizeInformation:
            {
                var (total, free) = DiskSpace();
                result = new FileFsSizeInformation
                {
                    TotalAllocationUnits = total / AllocationUnit,
                    AvailableAllocationUnits = free / AllocationUnit,
                    SectorsPerAllocationUnit = 8,
                    BytesPerSector = 512
                };
                return NTStatus.STATUS_SUCCESS;
            }

            case FileSystemInformationClass.FileFsFullSizeInformation:
            {
                var (total, free) = DiskSpace();
                result = new FileFsFullSizeInformation
                {
                    TotalAllocationUnits = total / AllocationUnit,
                    CallerAvailableAllocationUnits = free / AllocationUnit,
                    ActualAvailableAllocationUnits = free / AllocationUnit,
                    SectorsPerAllocationUnit = 8,
                    BytesPerSector = 512
                };
                return NTStatus.STATUS_SUCCESS;
            }

            case FileSystemInformationClass.FileFsVolumeInformation:
                result = new FileFsVolumeInformation
                {
                    VolumeSerialNumber = (uint)StableId(shareName),
                    SupportsObjects = false,
                    VolumeLabel = shareName
                };
                return NTStatus.STATUS_SUCCESS;
            case FileSystemInformationClass.FileFsAttributeInformation:
                result = new FileFsAttributeInformation
                {
                    FileSystemAttributes = FileSystemAttributes.CaseSensitiveSearch | FileSystemAttributes.CasePreservedNames
                        | FileSystemAttributes.UnicodeOnDisk | (readOnly ? FileSystemAttributes.ReadOnlyVolume : 0),
                    MaximumComponentNameLength = 255,
                    FileSystemName = "NTFS"
                };
                return NTStatus.STATUS_SUCCESS;
            case FileSystemInformationClass.FileFsDeviceInformation:
                result = new FileFsDeviceInformation
                {
                    DeviceType = DeviceType.Disk,
                    Characteristics = DeviceCharacteristics.IsMounted | (readOnly ? DeviceCharacteristics.ReadOnlyDevice : 0)
                };
                return NTStatus.STATUS_SUCCESS;
            default:
                return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation)
    {
        result = null!;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) =>
        NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus NotifyChange(
        out object ioRequest,
        object handle,
        NotifyChangeFilter completionFilter,
        bool watchTree,
        int outputBufferSize,
        OnNotifyChangeCompleted onNotifyChangeCompleted,
        object context)
    {
        ioRequest = null!;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_SUCCESS;

    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
    {
        output = [];
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    // ---- Auxiliares --------------------------------------------------------------------------

    private (long Total, long Free) DiskSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(rootPath)!);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private AccessMask AccessFor(OpenItem item)
    {
        var mask = (uint)AccessMask.SYNCHRONIZE | FileReadData | FileExecute | 0x80 /* ler atributos */;
        if (!readOnly)
        {
            mask |= FileWriteData | FileAppendData | 0x100 | (uint)AccessMask.DELETE;
        }

        return (AccessMask)mask;
    }

    private static FileBasicInformation BasicOf(FileSystemInfo info) =>
        new()
        {
            CreationTime = new SetFileTime(Utc(info.CreationTimeUtc)),
            LastAccessTime = new SetFileTime(Utc(info.LastAccessTimeUtc)),
            LastWriteTime = new SetFileTime(Utc(info.LastWriteTimeUtc)),
            ChangeTime = new SetFileTime(Utc(info.LastWriteTimeUtc)),
            FileAttributes = AttributesOf(info)
        };

    private static FileStandardInformation StandardOf(OpenItem item, long length) =>
        new()
        {
            AllocationSize = Allocation(length),
            EndOfFile = length,
            NumberOfLinks = 1,
            DeletePending = item.DeleteOnClose,
            Directory = item.IsDirectory
        };

    private static SMBFileAttributes AttributesOf(FileSystemInfo info)
    {
        if (info is DirectoryInfo)
        {
            return SMBFileAttributes.Directory | (info.Attributes.HasFlag(System.IO.FileAttributes.Hidden) ? SMBFileAttributes.Hidden : 0);
        }

        var attributes = (SMBFileAttributes)((int)info.Attributes & (int)(SMBFileAttributes.ReadOnly | SMBFileAttributes.Hidden
            | SMBFileAttributes.System | SMBFileAttributes.Archive));
        return attributes == 0 ? SMBFileAttributes.Normal : attributes;
    }

    private static long Allocation(long length) => (length + AllocationUnit - 1) / AllocationUnit * AllocationUnit;

    private static DateTime Utc(DateTime time) =>
        time.Year < 1601 ? new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc) : DateTime.SpecifyKind(time, DateTimeKind.Utc);

    private static ulong StableId(string text)
    {
        // FNV-1a: o mesmo caminho dá sempre o mesmo número (o Windows não tem um para oferecer).
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    private static string ParentOf(string virtualPath)
    {
        var slash = virtualPath.LastIndexOf('/');
        return slash <= 0 ? "/" : virtualPath[..slash];
    }

    private static string Combine(string directory, string name) => (directory == "/" ? string.Empty : directory) + "/" + name;

    private static string ToWindowsPath(string virtualPath) => virtualPath.Replace('/', '\\');

    private static NTStatus Map(Exception ex) =>
        ex switch
        {
            FileNotFoundException => NTStatus.STATUS_OBJECT_NAME_NOT_FOUND,
            DirectoryNotFoundException => NTStatus.STATUS_OBJECT_PATH_NOT_FOUND,
            UnauthorizedAccessException => NTStatus.STATUS_ACCESS_DENIED,
            IOException io when (io.HResult & 0xFFFF) is 112 or 39 || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                => NTStatus.STATUS_DISK_FULL,
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => NTStatus.STATUS_SHARING_VIOLATION,
            IOException io when (io.HResult & 0xFFFF) is 145 => NTStatus.STATUS_DIRECTORY_NOT_EMPTY,
            _ => NTStatus.STATUS_ACCESS_DENIED
        };
}

/// <summary>Curingas do SMB nas buscas de pasta: "*" (qualquer coisa), "?" (um caractere) e "*.*" (tudo).</summary>
internal static class SmbWildcard
{
    public static bool IsMatch(string pattern, string name)
    {
        if (pattern is "*" or "*.*" or "")
        {
            return true;
        }

        if (pattern.IndexOfAny(['*', '?', '<', '>', '"']) < 0)
        {
            return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        }

        return Match(pattern.AsSpan(), name.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        // Algoritmo clássico com recuo no último "*".
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] is '?' or '>' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] is '*' or '<')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] is '*' or '<')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
