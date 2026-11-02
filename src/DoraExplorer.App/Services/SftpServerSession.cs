using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DoraExplorer.App.Services;

/// <summary>
/// Servidor do protocolo SFTP versão 3 (a que todos os clientes falam) sobre um canal já aberto: lê pedidos de
/// <paramref name="channel"/>, opera dentro de <paramref name="rootPath"/> e responde. Tudo que o cliente manda é
/// tratado como não confiável: caminhos passam por <see cref="ServerVirtualPath"/>, pacotes têm tamanho máximo,
/// o número de arquivos abertos é limitado e o que ficou pela metade num envio interrompido é apagado.
/// </summary>
internal sealed class SftpServerSession(Stream channel, string rootPath, bool readOnly, string peer, Action<string> log)
{
    private const int MaxPacketLength = 1 << 20;
    private const int MaxReadLength = 1 << 18;
    private const int MaxOpenHandles = 64;
    private const int EntriesPerReadDir = 100;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    // Tipos de pacote (draft-ietf-secsh-filexfer-02).
    private const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, Lstat = 7, Fstat = 8, Setstat = 9,
        Fsetstat = 10, Opendir = 11, Readdir = 12, Remove = 13, Mkdir = 14, Rmdir = 15, Realpath = 16, Stat = 17,
        Rename = 18, Readlink = 19, Symlink = 20, Extended = 200;

    private const byte StatusReply = 101, HandleReply = 102, DataReply = 103, NameReply = 104, AttrsReply = 105;

    // Códigos de status.
    private const uint Ok = 0, Eof = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5, OpUnsupported = 8;

    // Opções do OPEN.
    private const uint FlagRead = 0x01, FlagWrite = 0x02, FlagAppend = 0x04, FlagCreate = 0x08, FlagTruncate = 0x10, FlagExclusive = 0x20;

    // Campos dos atributos.
    private const uint AttrSize = 0x01, AttrUidGid = 0x02, AttrPermissions = 0x04, AttrTimes = 0x08, AttrExtended = 0x80000000;

    private readonly Dictionary<string, object> _handles = new(StringComparer.Ordinal);
    private int _handleSequence;

    private sealed class FileHandle(FileStream stream, string virtualPath, bool writable, bool append, bool createdNew)
    {
        public FileStream Stream { get; } = stream;

        public string VirtualPath { get; } = virtualPath;

        public bool Writable { get; } = writable;

        public bool Append { get; } = append;

        public bool CreatedNew { get; } = createdNew;

        public long Bytes { get; set; }
    }

    private sealed class DirectoryHandle(IEnumerator<FileSystemInfo> entries, string virtualPath)
    {
        public IEnumerator<FileSystemInfo> Entries { get; } = entries;

        public string VirtualPath { get; } = virtualPath;
    }

    /// <summary>Atende pedidos até o cliente fechar o canal, errar o protocolo ou ficar parado por 5 minutos.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var initialized = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    break;
                }

                if (packet.Length < 1)
                {
                    break;
                }

                var type = packet[0];
                if (!initialized)
                {
                    if (type != Init)
                    {
                        log($"{peer}: o cliente SFTP não começou com INIT.");
                        break;
                    }

                    initialized = true;
                    await SendAsync(BuildVersion(), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var response = Dispatch(type, packet);
                if (response is null)
                {
                    break;
                }

                await SendAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Servidor parando, cliente parado por muito tempo ou canal fechado.
        }
        catch (IOException)
        {
            // O canal caiu no meio de um pedido.
        }
        finally
        {
            CloseAllHandles();
        }
    }

    // ---- Entrada e saída de pacotes --------------------------------------------------------------

    private async Task<byte[]?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);

        var header = new byte[4];
        try
        {
            if (!await ReadExactlyAsync(header, idle.Token).ConfigureAwait(false))
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > MaxPacketLength)
            {
                log($"{peer}: pacote SFTP com tamanho inválido ({length}).");
                return null;
            }

            var body = new byte[length];
            return await ReadExactlyAsync(body, idle.Token).ConfigureAwait(false) ? body : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log($"{peer}: sessão SFTP encerrada por inatividade.");
            return null;
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await channel.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var framed = new byte[4 + packet.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)packet.Length);
        packet.CopyTo(framed, 4);
        await channel.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await channel.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildVersion()
    {
        var writer = new PacketWriter();
        writer.Byte(Version);
        writer.UInt32(3);
        // Extensão do OpenSSH que renomeia sobrescrevendo (usada pelo cliente "sftp" e pelo rsync).
        writer.String("posix-rename@openssh.com");
        writer.String("1");
        return writer.ToArray();
    }

    // ---- Pedidos -----------------------------------------------------------------------------------

    /// <summary>Resposta ao pedido, ou nulo se o pacote veio malformado (a sessão é encerrada).</summary>
    private byte[]? Dispatch(byte type, byte[] packet)
    {
        var reader = new PacketReader(packet.AsMemory(1));
        try
        {
            return Handle(type, ref reader);
        }
        catch (FormatException)
        {
            log($"{peer}: pedido SFTP malformado (tipo {type}).");
            return null;
        }
    }

    private byte[] Handle(byte type, ref PacketReader reader)
    {
        var id = reader.UInt32();
        try
        {
            return type switch
            {
                Open => OpenFile(id, ref reader),
                Close => CloseHandle(id, reader.String()),
                Read => ReadFile(id, ref reader),
                Write => WriteFile(id, ref reader),
                Lstat or Stat => StatPath(id, reader.String()),
                Fstat => StatHandle(id, reader.String()),
                Setstat => SetStat(id, ref reader),
                Fsetstat => SetStatHandle(id, ref reader),
                Opendir => OpenDirectory(id, reader.String()),
                Readdir => ReadDirectory(id, reader.String()),
                Remove => RemoveFile(id, reader.String()),
                Mkdir => MakeDirectory(id, reader.String()),
                Rmdir => RemoveDirectory(id, reader.String()),
                Realpath => RealPath(id, reader.String()),
                Rename => RenamePath(id, reader.String(), reader.String(), overwrite: false),
                Extended => HandleExtended(id, ref reader),
                Readlink or Symlink => StatusPacket(id, OpUnsupported, "Links are not supported"),
                _ => StatusPacket(id, OpUnsupported, "Operation not supported")
            };
        }
        catch (UnauthorizedAccessException)
        {
            return StatusPacket(id, PermissionDenied, "Permission denied");
        }
        catch (FileNotFoundException)
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }
        catch (DirectoryNotFoundException)
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }
        catch (IOException ex)
        {
            return StatusPacket(id, Failure, ex.Message);
        }
    }

    private byte[] HandleExtended(uint id, ref PacketReader reader)
    {
        var name = reader.String();
        return name == "posix-rename@openssh.com"
            ? RenamePath(id, reader.String(), reader.String(), overwrite: true)
            : StatusPacket(id, OpUnsupported, "Extension not supported");
    }

    private bool TryResolve(uint id, string clientPath, out string real, out string virtualPath, out byte[]? denied)
    {
        denied = null;
        if (ServerVirtualPath.TryResolve(rootPath, clientPath, out real, out virtualPath))
        {
            return true;
        }

        log($"{peer}: caminho recusado '{clientPath}'.");
        denied = StatusPacket(id, PermissionDenied, "Permission denied");
        return false;
    }

    private byte[] OpenFile(uint id, ref PacketReader reader)
    {
        var path = reader.String();
        var flags = reader.UInt32();
        reader.SkipAttributes();

        if (!TryResolve(id, path, out var real, out var virtualPath, out var denied))
        {
            return denied!;
        }

        var writes = (flags & (FlagWrite | FlagAppend | FlagCreate | FlagTruncate)) != 0;
        if (writes && readOnly)
        {
            log($"{peer}: gravação recusada em '{virtualPath}' (servidor somente leitura).");
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (_handles.Count >= MaxOpenHandles)
        {
            return StatusPacket(id, Failure, "Too many open files");
        }

        if (Directory.Exists(real))
        {
            return StatusPacket(id, Failure, "Is a directory");
        }

        var write = (flags & (FlagWrite | FlagAppend)) != 0;
        var readFlag = (flags & FlagRead) != 0 || !write;
        var existed = File.Exists(real);
        var create = (flags & FlagCreate) != 0;
        var truncate = (flags & FlagTruncate) != 0;

        FileMode mode;
        if (create)
        {
            mode = (flags & FlagExclusive) != 0 ? FileMode.CreateNew : truncate ? FileMode.Create : FileMode.OpenOrCreate;
        }
        else
        {
            mode = truncate ? FileMode.Truncate : FileMode.Open;
        }

        if (mode == FileMode.Truncate && !write)
        {
            return StatusPacket(id, Failure, "Truncate needs write access");
        }

        var access = readFlag && write ? FileAccess.ReadWrite : write ? FileAccess.Write : FileAccess.Read;
        var share = write ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete;
        var stream = new FileStream(real, mode, access, share, 81920, FileOptions.None);
        var handle = new FileHandle(stream, virtualPath, write, (flags & FlagAppend) != 0, !existed && create);
        return HandlePacket(id, Register(handle));
    }

    private byte[] CloseHandle(uint id, string handleName)
    {
        if (!_handles.Remove(handleName, out var handle))
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        Release(handle, completed: true);
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] ReadFile(uint id, ref PacketReader reader)
    {
        var handleName = reader.String();
        var offset = reader.UInt64();
        var length = (int)Math.Min(reader.UInt32(), MaxReadLength);

        if (!_handles.TryGetValue(handleName, out var handle) || handle is not FileHandle file)
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        if (offset > long.MaxValue)
        {
            return StatusPacket(id, BadMessage, "Bad offset");
        }

        if (file.Stream.Position != (long)offset)
        {
            _ = file.Stream.Seek((long)offset, SeekOrigin.Begin);
        }

        var buffer = new byte[length];
        var read = file.Stream.Read(buffer, 0, length);
        if (read == 0 && length > 0)
        {
            return StatusPacket(id, Eof, "EOF");
        }

        file.Bytes += read;
        var writer = new PacketWriter();
        writer.Byte(DataReply);
        writer.UInt32(id);
        writer.Bytes(buffer.AsSpan(0, read));
        return writer.ToArray();
    }

    private byte[] WriteFile(uint id, ref PacketReader reader)
    {
        var handleName = reader.String();
        var offset = reader.UInt64();
        var data = reader.BytesView();

        if (!_handles.TryGetValue(handleName, out var handle) || handle is not FileHandle file || !file.Writable)
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (file.Append)
        {
            _ = file.Stream.Seek(0, SeekOrigin.End);
        }
        else if (offset > long.MaxValue)
        {
            return StatusPacket(id, BadMessage, "Bad offset");
        }
        else if (file.Stream.Position != (long)offset)
        {
            _ = file.Stream.Seek((long)offset, SeekOrigin.Begin);
        }

        file.Stream.Write(data);
        file.Bytes += data.Length;
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] StatPath(uint id, string path)
    {
        if (!TryResolve(id, path, out var real, out _, out var denied))
        {
            return denied!;
        }

        var info = GetInfo(real);
        return info is null ? StatusPacket(id, NoSuchFile, "No such file") : AttrsPacket(id, info);
    }

    private byte[] StatHandle(uint id, string handleName)
    {
        if (!_handles.TryGetValue(handleName, out var handle))
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        FileSystemInfo? info = handle switch
        {
            FileHandle file => new FileInfo(file.Stream.Name),
            DirectoryHandle directory when ServerVirtualPath.TryResolve(rootPath, directory.VirtualPath, out var real, out _) => GetInfo(real),
            _ => null
        };

        if (info is FileInfo fileInfo && handle is FileHandle open)
        {
            open.Stream.Flush();
            fileInfo.Refresh();
        }

        return info is null ? StatusPacket(id, NoSuchFile, "No such file") : AttrsPacket(id, info);
    }

    private byte[] SetStat(uint id, ref PacketReader reader)
    {
        var path = reader.String();
        var attributes = reader.Attributes();
        if (!TryResolve(id, path, out var real, out _, out var denied))
        {
            return denied!;
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (GetInfo(real) is null)
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }

        ApplyAttributes(real, attributes, openStream: null);
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] SetStatHandle(uint id, ref PacketReader reader)
    {
        var handleName = reader.String();
        var attributes = reader.Attributes();
        if (!_handles.TryGetValue(handleName, out var handle) || handle is not FileHandle file)
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        if (readOnly || !file.Writable)
        {
            return StatusPacket(id, PermissionDenied, "Permission denied");
        }

        ApplyAttributes(file.Stream.Name, attributes, file.Stream);
        return StatusPacket(id, Ok, "OK");
    }

    private static void ApplyAttributes(string realPath, SftpAttributes attributes, FileStream? openStream)
    {
        if (attributes.Size is { } size && File.Exists(realPath))
        {
            if (openStream is not null)
            {
                openStream.SetLength((long)Math.Min(size, long.MaxValue));
            }
            else
            {
                using var stream = new FileStream(realPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength((long)Math.Min(size, long.MaxValue));
            }
        }

        if (attributes.ModifiedTime is { } modified)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(modified).UtcDateTime;
            if (Directory.Exists(realPath))
            {
                Directory.SetLastWriteTimeUtc(realPath, time);
            }
            else
            {
                File.SetLastWriteTimeUtc(realPath, time);
            }
        }

        // Dono e permissões do Unix não existem no Windows: são aceitos e ignorados, como fazem os servidores de mercado.
    }

    private byte[] OpenDirectory(uint id, string path)
    {
        if (!TryResolve(id, path, out var real, out var virtualPath, out var denied))
        {
            return denied!;
        }

        if (!Directory.Exists(real))
        {
            return StatusPacket(id, File.Exists(real) ? Failure : NoSuchFile, File.Exists(real) ? "Not a directory" : "No such file");
        }

        if (_handles.Count >= MaxOpenHandles)
        {
            return StatusPacket(id, Failure, "Too many open files");
        }

        // Links e junções ficam fora da listagem (como nas cópias): não dá para provar para onde levam.
        var entries = new DirectoryInfo(real)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .GetEnumerator();
        return HandlePacket(id, Register(new DirectoryHandle(entries, virtualPath)));
    }

    private byte[] ReadDirectory(uint id, string handleName)
    {
        if (!_handles.TryGetValue(handleName, out var handle) || handle is not DirectoryHandle directory)
        {
            return StatusPacket(id, Failure, "Invalid handle");
        }

        var batch = new List<FileSystemInfo>();
        while (batch.Count < EntriesPerReadDir && directory.Entries.MoveNext())
        {
            batch.Add(directory.Entries.Current);
        }

        if (batch.Count == 0)
        {
            return StatusPacket(id, Eof, "EOF");
        }

        var writer = new PacketWriter();
        writer.Byte(NameReply);
        writer.UInt32(id);
        writer.UInt32((uint)batch.Count);
        foreach (var entry in batch)
        {
            writer.String(entry.Name);
            writer.String(LongName(entry));
            WriteAttributes(writer, entry);
        }

        return writer.ToArray();
    }

    private byte[] RemoveFile(uint id, string path)
    {
        if (!TryResolve(id, path, out var real, out var virtualPath, out var denied))
        {
            return denied!;
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (Directory.Exists(real))
        {
            return StatusPacket(id, Failure, "Is a directory");
        }

        if (!File.Exists(real))
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }

        File.Delete(real);
        log($"{peer} apagou {virtualPath}");
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] MakeDirectory(uint id, string path)
    {
        if (!TryResolve(id, path, out var real, out var virtualPath, out var denied))
        {
            return denied!;
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (File.Exists(real) || Directory.Exists(real))
        {
            return StatusPacket(id, Failure, "Already exists");
        }

        _ = Directory.CreateDirectory(real);
        log($"{peer} criou a pasta {virtualPath}");
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] RemoveDirectory(uint id, string path)
    {
        if (!TryResolve(id, path, out var real, out var virtualPath, out var denied))
        {
            return denied!;
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (virtualPath == "/")
        {
            return StatusPacket(id, PermissionDenied, "Cannot remove the root");
        }

        if (!Directory.Exists(real))
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }

        Directory.Delete(real, recursive: false);
        log($"{peer} apagou a pasta {virtualPath}");
        return StatusPacket(id, Ok, "OK");
    }

    private byte[] RealPath(uint id, string path)
    {
        var normalized = ServerVirtualPath.Normalize(path);
        var writer = new PacketWriter();
        writer.Byte(NameReply);
        writer.UInt32(id);
        writer.UInt32(1);
        writer.String(normalized);
        writer.String(normalized);
        writer.UInt32(0); // atributos vazios
        return writer.ToArray();
    }

    private byte[] RenamePath(uint id, string from, string to, bool overwrite)
    {
        if (!TryResolve(id, from, out var source, out var sourceVirtual, out var denied) ||
            !TryResolve(id, to, out var target, out _, out denied))
        {
            return denied!;
        }

        if (readOnly)
        {
            return StatusPacket(id, PermissionDenied, "Read-only server");
        }

        if (sourceVirtual == "/")
        {
            return StatusPacket(id, PermissionDenied, "Cannot rename the root");
        }

        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            return StatusPacket(id, NoSuchFile, "No such file");
        }

        var targetExists = File.Exists(target) || Directory.Exists(target);
        if (targetExists && (!overwrite || isDirectory || Directory.Exists(target)))
        {
            return StatusPacket(id, Failure, "Target already exists");
        }

        if (isDirectory)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target, overwrite);
        }

        log($"{peer} renomeou {sourceVirtual}");
        return StatusPacket(id, Ok, "OK");
    }

    // ---- Montagem de respostas ----------------------------------------------------------------------

    private string Register(object handle)
    {
        var name = (++_handleSequence).ToString(CultureInfo.InvariantCulture);
        _handles[name] = handle;
        return name;
    }

    private void CloseAllHandles()
    {
        foreach (var handle in _handles.Values)
        {
            Release(handle, completed: false);
        }

        _handles.Clear();
    }

    /// <summary>Fecha um arquivo ou pasta aberta. Se a sessão acabou sem fechar um arquivo que ela criou, ele é apagado.</summary>
    private void Release(object handle, bool completed)
    {
        switch (handle)
        {
            case FileHandle file:
                var path = file.Stream.Name;
                file.Stream.Dispose();
                if (completed && file.Bytes > 0)
                {
                    log($"{peer} {(file.Writable ? "gravou" : "leu")} {file.VirtualPath} ({file.Bytes} bytes)");
                }
                else if (!completed && file.Writable && file.CreatedNew)
                {
                    TryDelete(path);
                    log($"{peer}: envio incompleto de {file.VirtualPath} apagado.");
                }

                break;
            case DirectoryHandle directory:
                directory.Entries.Dispose();
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpeza de melhor esforço.
        }
    }

    private static FileSystemInfo? GetInfo(string realPath)
    {
        if (Directory.Exists(realPath))
        {
            return new DirectoryInfo(realPath);
        }

        return File.Exists(realPath) ? new FileInfo(realPath) : null;
    }

    private static byte[] StatusPacket(uint id, uint code, string message)
    {
        var writer = new PacketWriter();
        writer.Byte(StatusReply);
        writer.UInt32(id);
        writer.UInt32(code);
        writer.String(message);
        writer.String("en");
        return writer.ToArray();
    }

    private static byte[] HandlePacket(uint id, string handle)
    {
        var writer = new PacketWriter();
        writer.Byte(HandleReply);
        writer.UInt32(id);
        writer.String(handle);
        return writer.ToArray();
    }

    private static byte[] AttrsPacket(uint id, FileSystemInfo info)
    {
        var writer = new PacketWriter();
        writer.Byte(AttrsReply);
        writer.UInt32(id);
        WriteAttributes(writer, info);
        return writer.ToArray();
    }

    private static void WriteAttributes(PacketWriter writer, FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        writer.UInt32(AttrSize | AttrPermissions | AttrTimes);
        writer.UInt64(isDirectory ? 0UL : (ulong)Math.Max(0, ((FileInfo)info).Length));
        writer.UInt32(isDirectory ? 0x41EDu : 0x81A4u); // drwxr-xr-x / -rw-r--r--
        var modified = ToUnixTime(info.LastWriteTimeUtc);
        writer.UInt32(modified);
        writer.UInt32(modified);
    }

    private static uint ToUnixTime(DateTime utc) =>
        (uint)Math.Clamp(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds(), 0, uint.MaxValue);

    /// <summary>Linha no estilo de "ls -l": alguns clientes (como o FileZilla) montam a listagem a partir dela.</summary>
    private static string LongName(FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        var size = isDirectory ? 0 : ((FileInfo)info).Length;
        var modified = info.LastWriteTimeUtc;
        var recent = DateTime.UtcNow - modified < TimeSpan.FromDays(180);
        var date = recent
            ? modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : modified.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(isDirectory ? "drwxr-xr-x" : "-rw-r--r--")}    1 dora     dora     {size,12} {date} {info.Name}");
    }

    // ---- Leitura e escrita de campos -------------------------------------------------------------------

    internal readonly record struct SftpAttributes(ulong? Size, uint? ModifiedTime);

    private ref struct PacketReader(ReadOnlyMemory<byte> data)
    {
        private ReadOnlyMemory<byte> _data = data;

        public uint UInt32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Span);
            _data = _data[4..];
            return value;
        }

        public ulong UInt64()
        {
            Need(8);
            var value = BinaryPrimitives.ReadUInt64BigEndian(_data.Span);
            _data = _data[8..];
            return value;
        }

        public ReadOnlySpan<byte> BytesView()
        {
            var length = UInt32();
            if (length > _data.Length)
            {
                throw new FormatException("Campo maior que o pacote.");
            }

            var view = _data.Span[..(int)length];
            _data = _data[(int)length..];
            return view;
        }

        public string String() => Encoding.UTF8.GetString(BytesView());

        public SftpAttributes Attributes()
        {
            var flags = UInt32();
            ulong? size = null;
            uint? modified = null;
            if ((flags & AttrSize) != 0)
            {
                size = UInt64();
            }

            if ((flags & AttrUidGid) != 0)
            {
                _ = UInt32();
                _ = UInt32();
            }

            if ((flags & AttrPermissions) != 0)
            {
                _ = UInt32();
            }

            if ((flags & AttrTimes) != 0)
            {
                _ = UInt32();
                modified = UInt32();
            }

            if ((flags & AttrExtended) != 0)
            {
                var count = UInt32();
                for (var i = 0; i < count; i++)
                {
                    _ = BytesView();
                    _ = BytesView();
                }
            }

            return new SftpAttributes(size, modified);
        }

        public void SkipAttributes() => _ = Attributes();

        private readonly void Need(int count)
        {
            if (_data.Length < count)
            {
                throw new FormatException("Pacote curto demais.");
            }
        }
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _buffer = new();

        public void Byte(byte value) => _buffer.WriteByte(value);

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void Bytes(ReadOnlySpan<byte> value)
        {
            UInt32((uint)value.Length);
            _buffer.Write(value);
        }

        public void String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        public byte[] ToArray() => _buffer.ToArray();
    }
}
