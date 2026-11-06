using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Servidor NFS versão 3 sobre TCP (RFC 1813): o portmap, o MOUNT e o NFS atendem na mesma porta, e uma única pasta é
/// exportada. O NFS não tem senha: por isso ele só atende endereços de rede local (privados, de link local e loopback),
/// aceita o modo somente leitura e todos os caminhos passam por <see cref="ServerVirtualPath"/> (nada sai da pasta
/// exportada, nem por ".." nem por atalho). Quem escreve é sempre o usuário do Windows que abriu o programa.
/// </summary>
internal sealed class NfsServer : ProtocolServerBase
{
    private const int MaxConnections = 32;
    private const int MaxIoBytes = 1 << 20;
    private const int PreferredIoBytes = 1 << 16;
    private const int DirectoryPageHeaderBytes = 128;
    private const uint DirectoryPermissions = 0x1ED; // 0755
    private const uint FilePermissions = 0x1A4; // 0644
    private const ulong FileSystemId = 0x446F7261;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ListingCacheLifetime = TimeSpan.FromSeconds(3);

    private readonly byte[] _writeVerifier = RandomNumberGenerator.GetBytes(NfsProtocol.VerifierBytes);
    private readonly NfsHandleTable _handles = new();
    private readonly ConcurrentDictionary<string, DirectoryListing> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _nextClientId;

    public NfsServer(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs)
        : base(config, rootPath, logs)
    {
        ExportName = ShareNames.FromFolder(rootPath);
        ExportPath = "/" + ExportName;
    }

    /// <summary>Nome da pasta exportada, sem a barra ("Dados").</summary>
    public string ExportName { get; }

    /// <summary>Caminho que o cliente monta ("/Dados").</summary>
    public string ExportPath { get; }

    public override void Start()
    {
        var address = IPAddress.Parse(Config.Host);
        _listener = new TcpListener(address, Config.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;

        Log($"NFS a escutar em {Config.Host}:{Config.Port}" + (ReadOnly ? " (somente leitura)" : string.Empty));
        Log($"Exportação: {ExportPath}. No Linux: mount -t nfs -o vers=3,proto=tcp,port={Config.Port},mountport={Config.Port},nolock {Config.Host}:{ExportPath} /mnt/dora");
        if (!ReadOnly)
        {
            Log("Atenção: o NFS não tem senha; com gravação liberada, qualquer máquina da rede local pode alterar esta pasta.");
        }

        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
    }

    protected override void StopCore(bool closeListener)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // ignored
        }

        if (closeListener)
        {
            try
            {
                _listener?.Stop();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // ignored
            }
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // ignored
            }
        }

        _clients.Clear();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    // ---- Conexões ----------------------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Log($"ERRO ao aceitar conexão: {ex.Message}");
                continue;
            }

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null || !NetworkAddressPolicy.IsLocalNetwork(remote.Address))
            {
                Log($"Conexão de {remote} recusada: o NFS só atende endereços de rede local.");
                client.Dispose();
                continue;
            }

            if (_clients.Count >= MaxConnections)
            {
                Log($"Conexão de {remote} recusada: limite de {MaxConnections} conexões simultâneas.");
                client.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextClientId);
            _clients[id] = client;
            _ = Task.Run(() => ServeConnectionAsync(id, client, remote, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeConnectionAsync(int id, TcpClient client, IPEndPoint remote, CancellationToken cancellationToken)
    {
        Log($"Conexão de {remote}");
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? record;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    idle.CancelAfter(IdleTimeout);
                    record = await Rpc.ReadRecordAsync(stream, idle.Token).ConfigureAwait(false);
                }

                if (record is null)
                {
                    break;
                }

                var reply = HandleRecord(record, remote);
                if (reply is null)
                {
                    break; // mensagem sem como responder: encerra a conexão
                }

                await Rpc.WriteRecordAsync(stream, reply, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // servidor parado ou conexão ociosa
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or XdrException)
        {
            // o cliente foi embora ou mandou lixo
        }
        catch (Exception ex)
        {
            Log($"ERRO na conexão de {remote}: {ex.Message}");
            AppLogService.Error("NFS conexão falhou", ex);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            Log($"Conexão de {remote} encerrada");
        }
    }

    // ---- RPC ---------------------------------------------------------------------------------

    /// <summary>Decodifica a chamada e devolve a resposta; nulo quando nem o número da chamada dá para ler.</summary>
    internal byte[]? HandleRecord(byte[] record, IPEndPoint remote)
    {
        if (record.Length < 4)
        {
            return null;
        }

        var xid = (uint)((record[0] << 24) | (record[1] << 16) | (record[2] << 8) | record[3]);
        Rpc.CallMessage call;
        try
        {
            call = Rpc.ParseCall(record);
        }
        catch (XdrException)
        {
            return Rpc.AcceptedReply(xid, Rpc.AcceptGarbageArgs);
        }

        if (call.RpcVersion != 2)
        {
            return Rpc.DeniedReply(xid, null);
        }

        if (call.CredentialFlavor is not (Rpc.AuthNone or Rpc.AuthSys))
        {
            return Rpc.DeniedReply(xid, 1 /* AUTH_BADCRED */);
        }

        var identity = call.Identity ?? new RpcIdentity(65534, 65534, "anonimo");
        try
        {
            XdrWriter? body = call.Program switch
            {
                Rpc.PortmapProgram => HandlePortmap(call),
                Rpc.MountProgram => HandleMount(call, remote),
                Rpc.NfsProgram => HandleNfs(call, identity),
                _ => throw new RpcRejection(Rpc.AcceptProgUnavail)
            };
            return Rpc.AcceptedReply(xid, Rpc.AcceptSuccess, body);
        }
        catch (RpcRejection rejection)
        {
            return Rpc.AcceptedReply(xid, rejection.Status, null, rejection.Low, rejection.High);
        }
        catch (XdrException)
        {
            return Rpc.AcceptedReply(xid, Rpc.AcceptGarbageArgs);
        }
        catch (Exception ex)
        {
            Log($"ERRO ao processar pedido de {remote}: {ex.Message}");
            AppLogService.Error("NFS pedido falhou", ex);
            return Rpc.AcceptedReply(xid, Rpc.AcceptSystemErr);
        }
    }

    private sealed class RpcRejection(uint status, uint low = 0, uint high = 0) : Exception
    {
        public uint Status { get; } = status;

        public uint Low { get; } = low;

        public uint High { get; } = high;
    }

    // ---- Portmap (programa 100000, versão 2) -------------------------------------------------

    private XdrWriter HandlePortmap(Rpc.CallMessage call)
    {
        if (call.Version != 2)
        {
            throw new RpcRejection(Rpc.AcceptProgMismatch, 2, 2);
        }

        var body = new XdrWriter();
        switch (call.Procedure)
        {
            case 0:
                return body;
            case 3: // GETPORT
            {
                var program = call.Arguments.UInt32();
                var version = call.Arguments.UInt32();
                var protocol = call.Arguments.UInt32();
                var served = protocol == 6 && IsServedProgram(program, version);
                return body.UInt32(served ? (uint)Config.Port : 0u);
            }

            case 4: // DUMP
                WritePortmapEntry(body, Rpc.PortmapProgram, 2);
                WritePortmapEntry(body, Rpc.NfsProgram, NfsProtocol.NfsVersion);
                WritePortmapEntry(body, Rpc.MountProgram, NfsProtocol.MountVersion);
                return body.Bool(false);
            default:
                throw new RpcRejection(Rpc.AcceptProcUnavail);
        }
    }

    private static bool IsServedProgram(uint program, uint version) =>
        (program == Rpc.PortmapProgram && version == 2)
        || (program == Rpc.NfsProgram && version == NfsProtocol.NfsVersion)
        || (program == Rpc.MountProgram && version == NfsProtocol.MountVersion);

    private void WritePortmapEntry(XdrWriter body, uint program, uint version) =>
        body.Bool(true).UInt32(program).UInt32(version).UInt32(6).UInt32((uint)Config.Port);

    // ---- MOUNT (programa 100005, versão 3) ---------------------------------------------------

    private XdrWriter HandleMount(Rpc.CallMessage call, IPEndPoint remote)
    {
        if (call.Version != NfsProtocol.MountVersion)
        {
            throw new RpcRejection(Rpc.AcceptProgMismatch, NfsProtocol.MountVersion, NfsProtocol.MountVersion);
        }

        var body = new XdrWriter();
        switch (call.Procedure)
        {
            case NfsProtocol.MountProcNull:
            case NfsProtocol.MountProcUmnt:
            case NfsProtocol.MountProcUmntAll:
                return body;
            case NfsProtocol.MountProcDump:
                return body.Bool(false);
            case NfsProtocol.MountProcExport:
                return body.Bool(true).String(ExportPath).Bool(false).Bool(false);
            case NfsProtocol.MountProcMnt:
            {
                var requested = call.Arguments.String(NfsProtocol.PathMaxBytes);
                var status = ResolveMountPath(requested, out var virtualPath);
                if (status != NfsStatus.Ok)
                {
                    Log($"Montagem de \"{requested}\" por {remote.Address} recusada: {NfsException.Describe(status)}");
                    return body.UInt32((uint)status);
                }

                Log($"Montagem de \"{requested}\" por {remote.Address}");
                return body.UInt32(0).Opaque(_handles.HandleFor(virtualPath)).UInt32(1).UInt32(Rpc.AuthSys);
            }

            default:
                throw new RpcRejection(Rpc.AcceptProcUnavail);
        }
    }

    /// <summary>A exportação ou uma pasta dentro dela ("/Dados" ou "/Dados/fotos"); o resto não existe para o cliente.</summary>
    private NfsStatus ResolveMountPath(string requested, out string virtualPath)
    {
        virtualPath = "/";
        var normalized = ServerVirtualPath.Normalize(requested);
        var exportSegments = ExportPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < exportSegments.Length ||
            !segments.Take(exportSegments.Length).SequenceEqual(exportSegments, PathComparer))
        {
            return NfsStatus.NoEnt;
        }

        virtualPath = "/" + string.Join('/', segments.Skip(exportSegments.Length));
        if (!ServerVirtualPath.TryResolve(RootPath, virtualPath, out var real, out virtualPath))
        {
            return NfsStatus.Access;
        }

        if (File.Exists(real))
        {
            return NfsStatus.NotDir;
        }

        return Directory.Exists(real) ? NfsStatus.Ok : NfsStatus.NoEnt;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // ---- NFS (programa 100003, versão 3) -----------------------------------------------------

    private XdrWriter HandleNfs(Rpc.CallMessage call, RpcIdentity identity)
    {
        if (call.Version != NfsProtocol.NfsVersion)
        {
            throw new RpcRejection(Rpc.AcceptProgMismatch, NfsProtocol.NfsVersion, NfsProtocol.NfsVersion);
        }

        var args = call.Arguments;
        var body = new XdrWriter(256);
        switch (call.Procedure)
        {
            case NfsProtocol.ProcNull:
                return body;
            case NfsProtocol.ProcGetAttr:
                return GetAttr(args, body, identity);
            case NfsProtocol.ProcSetAttr:
                return SetAttr(args, body, identity);
            case NfsProtocol.ProcLookup:
                return Lookup(args, body, identity);
            case NfsProtocol.ProcAccess:
                return Access(args, body, identity);
            case NfsProtocol.ProcRead:
                return Read(args, body, identity);
            case NfsProtocol.ProcWrite:
                return Write(args, body, identity);
            case NfsProtocol.ProcCreate:
                return Create(args, body, identity);
            case NfsProtocol.ProcMkDir:
                return MakeDirectory(args, body, identity);
            case NfsProtocol.ProcRemove:
                return RemoveEntry(args, body, directory: false);
            case NfsProtocol.ProcRmDir:
                return RemoveEntry(args, body, directory: true);
            case NfsProtocol.ProcRename:
                return Rename(args, body);
            case NfsProtocol.ProcReadDir:
                return ReadDirectory(args, body, identity, plus: false);
            case NfsProtocol.ProcReadDirPlus:
                return ReadDirectory(args, body, identity, plus: true);
            case NfsProtocol.ProcFsStat:
                return FileSystemStatus(args, body, identity);
            case NfsProtocol.ProcFsInfo:
                return FileSystemInformation(args, body, identity);
            case NfsProtocol.ProcPathConf:
                return PathConfiguration(args, body, identity);
            case NfsProtocol.ProcCommit:
                return Commit(args, body);
            case NfsProtocol.ProcReadLink:
                return NotSupported(body.UInt32((uint)NfsStatus.NotSupp).Bool(false));
            case NfsProtocol.ProcSymlink:
            case NfsProtocol.ProcMkNod:
                return NotSupported(body.UInt32((uint)NfsStatus.NotSupp).Bool(false).Bool(false));
            case NfsProtocol.ProcLink:
                return NotSupported(body.UInt32((uint)NfsStatus.NotSupp).Bool(false).Bool(false).Bool(false));
            default:
                throw new RpcRejection(Rpc.AcceptProcUnavail);
        }
    }

    private static XdrWriter NotSupported(XdrWriter body) => body;

    // Respostas de erro: cada procedimento devolve o status e a estrutura de falha do seu tipo.
    private static XdrWriter FailNone(XdrWriter body, NfsStatus status) => body.UInt32((uint)status);

    private static XdrWriter FailPostOp(XdrWriter body, NfsStatus status) => body.UInt32((uint)status).Bool(false);

    private static XdrWriter FailWcc(XdrWriter body, NfsStatus status) => body.UInt32((uint)status).Bool(false).Bool(false);

    // ---- Caminhos e atributos ----------------------------------------------------------------

    private NfsStatus ResolveHandle(XdrReader args, out string virtualPath, out string realPath)
    {
        realPath = string.Empty;
        virtualPath = "/";
        var handle = args.OpaqueSpan(NfsProtocol.FileHandleMaxBytes);
        if (!_handles.TryGetPath(handle, out var path))
        {
            return NfsStatus.Stale;
        }

        return ResolvePath(path, out virtualPath, out realPath);
    }

    private NfsStatus ResolvePath(string path, out string virtualPath, out string realPath) =>
        ServerVirtualPath.TryResolve(RootPath, path, out realPath, out virtualPath) ? NfsStatus.Ok : NfsStatus.Access;

    /// <summary>Caminho de um item novo dentro de uma pasta: valida o nome e a pasta.</summary>
    private NfsStatus ResolveChild(XdrReader args, out string parentVirtual, out string parentReal, out string name,
        out string childVirtual, out string childReal)
    {
        childVirtual = string.Empty;
        childReal = string.Empty;
        name = string.Empty;
        var status = ResolveHandle(args, out parentVirtual, out parentReal);
        name = args.String(NfsProtocol.NameMaxBytes);
        if (status != NfsStatus.Ok)
        {
            return status;
        }

        if (!Directory.Exists(parentReal))
        {
            return File.Exists(parentReal) ? NfsStatus.NotDir : NfsStatus.Stale;
        }

        if (!ServerVirtualPath.IsSafeSegment(name))
        {
            return name.Length > NfsProtocol.NameMaxBytes ? NfsStatus.NameTooLong : NfsStatus.Access;
        }

        childVirtual = (parentVirtual == "/" ? string.Empty : parentVirtual) + "/" + name;
        return ResolvePath(childVirtual, out childVirtual, out childReal);
    }

    private static FileSystemInfo? Info(string realPath)
    {
        if (Directory.Exists(realPath))
        {
            return new DirectoryInfo(realPath);
        }

        return File.Exists(realPath) ? new FileInfo(realPath) : null;
    }

    private void WriteAttributes(XdrWriter body, string virtualPath, FileSystemInfo info, RpcIdentity identity)
    {
        var isDirectory = info is DirectoryInfo;
        var size = isDirectory ? 4096UL : (ulong)((FileInfo)info).Length;
        var mode = isDirectory ? DirectoryPermissions : FilePermissions;
        if (ReadOnly)
        {
            mode &= ~0x92u; // tira as permissões de escrita
        }

        NfsProtocol.WriteAttributes(
            body,
            isDirectory ? NfsFileType.Directory : NfsFileType.Regular,
            mode,
            identity.Uid,
            identity.Gid,
            size,
            (size + 4095) / 4096 * 4096,
            _handles.IdFor(virtualPath),
            info.LastWriteTimeUtc,
            info.CreationTimeUtc,
            FileSystemId);
    }

    private void PostOpAttributes(XdrWriter body, string virtualPath, string realPath, RpcIdentity identity)
    {
        var info = Info(realPath);
        if (info is null)
        {
            body.Bool(false);
            return;
        }

        body.Bool(true);
        WriteAttributes(body, virtualPath, info, identity);
    }

    private static NfsStatus Map(Exception ex) =>
        ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => NfsStatus.NoEnt,
            UnauthorizedAccessException => NfsStatus.Access,
            PathTooLongException => NfsStatus.NameTooLong,
            IOException io when IsDiskFull(io) => NfsStatus.NoSpc,
            _ => NfsStatus.Io
        };

    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 112 or 39 || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    // ---- Procedimentos -----------------------------------------------------------------------

    private XdrWriter GetAttr(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        if (status != NfsStatus.Ok)
        {
            return FailNone(body, status);
        }

        var info = Info(real);
        if (info is null)
        {
            return FailNone(body, NfsStatus.Stale);
        }

        body.UInt32(0);
        WriteAttributes(body, virtualPath, info, identity);
        return body;
    }

    private XdrWriter SetAttr(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out _, out var real);
        var attributes = NfsProtocol.ReadSetAttributes(args);
        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        if (ReadOnly)
        {
            return FailWcc(body, NfsStatus.RoFs);
        }

        var info = Info(real);
        if (info is null)
        {
            return FailWcc(body, NfsStatus.Stale);
        }

        try
        {
            if (attributes.Size is { } size)
            {
                if (info is DirectoryInfo)
                {
                    return FailWcc(body, NfsStatus.IsDir);
                }

                using var file = new FileStream(real, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.SetLength((long)Math.Min(size, long.MaxValue));
            }

            if (attributes.ModifiedUtc is { } modified)
            {
                if (info is DirectoryInfo)
                {
                    Directory.SetLastWriteTimeUtc(real, modified);
                }
                else
                {
                    File.SetLastWriteTimeUtc(real, modified);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }

        return body.UInt32(0).Bool(false).Bool(false);
    }

    private XdrWriter Lookup(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var directoryVirtual, out var directoryReal);
        var name = args.String(NfsProtocol.NameMaxBytes);
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        if (!Directory.Exists(directoryReal))
        {
            return FailPostOp(body, File.Exists(directoryReal) ? NfsStatus.NotDir : NfsStatus.Stale);
        }

        string targetVirtual;
        if (name == ".")
        {
            targetVirtual = directoryVirtual;
        }
        else if (name == "..")
        {
            targetVirtual = ParentOf(directoryVirtual);
        }
        else if (!ServerVirtualPath.IsSafeSegment(name))
        {
            return Fail(body, NfsStatus.NoEnt, directoryVirtual, directoryReal, identity);
        }
        else
        {
            targetVirtual = (directoryVirtual == "/" ? string.Empty : directoryVirtual) + "/" + name;
        }

        if (ResolvePath(targetVirtual, out targetVirtual, out var targetReal) != NfsStatus.Ok)
        {
            return Fail(body, NfsStatus.Access, directoryVirtual, directoryReal, identity);
        }

        var info = Info(targetReal);
        if (info is null)
        {
            return Fail(body, NfsStatus.NoEnt, directoryVirtual, directoryReal, identity);
        }

        body.UInt32(0).Opaque(_handles.HandleFor(targetVirtual)).Bool(true);
        WriteAttributes(body, targetVirtual, info, identity);
        PostOpAttributes(body, directoryVirtual, directoryReal, identity);
        return body;
    }

    private XdrWriter Fail(XdrWriter body, NfsStatus status, string directoryVirtual, string directoryReal, RpcIdentity identity)
    {
        body.UInt32((uint)status);
        PostOpAttributes(body, directoryVirtual, directoryReal, identity);
        return body;
    }

    private XdrWriter Access(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        var requested = args.UInt32();
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        var info = Info(real);
        if (info is null)
        {
            return FailPostOp(body, NfsStatus.Stale);
        }

        var granted = NfsProtocol.AccessRead | NfsProtocol.AccessLookup | NfsProtocol.AccessExecute;
        if (!ReadOnly)
        {
            granted |= NfsProtocol.AccessModify | NfsProtocol.AccessExtend | NfsProtocol.AccessDelete;
        }

        body.UInt32(0).Bool(true);
        WriteAttributes(body, virtualPath, info, identity);
        return body.UInt32(granted & requested);
    }

    private XdrWriter Read(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        var offset = args.UInt64();
        var count = (int)Math.Min(args.UInt32(), (uint)MaxIoBytes);
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        if (Directory.Exists(real))
        {
            return FailPostOp(body, NfsStatus.IsDir);
        }

        try
        {
            using var file = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            var length = file.Length;
            var buffer = new byte[(int)Math.Max(0, Math.Min(count, length - (long)Math.Min(offset, (ulong)length)))];
            file.Seek((long)Math.Min(offset, (ulong)length), SeekOrigin.Begin);
            var read = 0;
            while (read < buffer.Length)
            {
                var got = file.Read(buffer, read, buffer.Length - read);
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            var eof = (long)Math.Min(offset, (ulong)length) + read >= length;
            body.UInt32(0);
            PostOpAttributes(body, virtualPath, real, identity);
            return body.UInt32((uint)read).Bool(eof).Opaque(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailPostOp(body, Map(ex));
        }
    }

    private XdrWriter Write(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out _, out var real);
        var offset = args.UInt64();
        _ = args.UInt32(); // count (o tamanho vem no opaque)
        var stable = args.UInt32();
        var data = args.OpaqueSpan(MaxIoBytes);
        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        if (ReadOnly)
        {
            return FailWcc(body, NfsStatus.RoFs);
        }

        if (Directory.Exists(real))
        {
            return FailWcc(body, NfsStatus.IsDir);
        }

        if (offset > long.MaxValue - (ulong)data.Length)
        {
            return FailWcc(body, NfsStatus.FBig);
        }

        try
        {
            using var file = new FileStream(real, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
            file.Seek((long)offset, SeekOrigin.Begin);
            file.Write(data);
            if (stable != NfsProtocol.StableUnstable)
            {
                file.Flush(flushToDisk: true);
            }

            return body.UInt32(0).Bool(false).Bool(false)
                .UInt32((uint)data.Length)
                .UInt32(stable != NfsProtocol.StableUnstable ? NfsProtocol.StableFileSync : NfsProtocol.StableUnstable)
                .Fixed(_writeVerifier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }
    }

    private XdrWriter Commit(XdrReader args, XdrWriter body)
    {
        var status = ResolveHandle(args, out _, out var real);
        _ = args.UInt64();
        _ = args.UInt32();
        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        try
        {
            if (File.Exists(real))
            {
                using var file = new FileStream(real, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                file.Flush(flushToDisk: true);
            }

            return body.UInt32(0).Bool(false).Bool(false).Fixed(_writeVerifier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }
    }

    private XdrWriter Create(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveChild(args, out _, out _, out _, out var childVirtual, out var childReal);
        var mode = args.UInt32();
        NfsSetAttributes attributes = new();
        if (mode is 0 or 1)
        {
            attributes = NfsProtocol.ReadSetAttributes(args);
        }
        else
        {
            _ = args.Fixed(NfsProtocol.VerifierBytes);
        }

        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        if (ReadOnly)
        {
            Log($"Criar \"{childVirtual}\" recusado: somente leitura");
            return FailWcc(body, NfsStatus.RoFs);
        }

        if (Directory.Exists(childReal))
        {
            return FailWcc(body, NfsStatus.IsDir);
        }

        try
        {
            var exists = File.Exists(childReal);
            if (exists && mode != 0)
            {
                return FailWcc(body, NfsStatus.Exist);
            }

            using (var file = new FileStream(childReal, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
            {
                if (attributes.Size is { } size && (exists || size == 0))
                {
                    file.SetLength((long)Math.Min(size, long.MaxValue));
                }
            }

            if (!exists)
            {
                Log($"Arquivo criado: {childVirtual}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }

        body.UInt32(0).Bool(true).Opaque(_handles.HandleFor(childVirtual)).Bool(true);
        WriteAttributes(body, childVirtual, Info(childReal)!, identity);
        return body.Bool(false).Bool(false);
    }

    private XdrWriter MakeDirectory(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveChild(args, out _, out _, out _, out var childVirtual, out var childReal);
        _ = NfsProtocol.ReadSetAttributes(args);
        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        if (ReadOnly)
        {
            Log($"Criar a pasta \"{childVirtual}\" recusado: somente leitura");
            return FailWcc(body, NfsStatus.RoFs);
        }

        if (Directory.Exists(childReal) || File.Exists(childReal))
        {
            return FailWcc(body, NfsStatus.Exist);
        }

        try
        {
            Directory.CreateDirectory(childReal);
            Log($"Pasta criada: {childVirtual}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }

        body.UInt32(0).Bool(true).Opaque(_handles.HandleFor(childVirtual)).Bool(true);
        WriteAttributes(body, childVirtual, Info(childReal)!, identity);
        return body.Bool(false).Bool(false);
    }

    private XdrWriter RemoveEntry(XdrReader args, XdrWriter body, bool directory)
    {
        var status = ResolveChild(args, out _, out _, out _, out var childVirtual, out var childReal);
        if (status != NfsStatus.Ok)
        {
            return FailWcc(body, status);
        }

        if (ReadOnly)
        {
            Log($"Apagar \"{childVirtual}\" recusado: somente leitura");
            return FailWcc(body, NfsStatus.RoFs);
        }

        try
        {
            if (directory)
            {
                if (File.Exists(childReal))
                {
                    return FailWcc(body, NfsStatus.NotDir);
                }

                if (!Directory.Exists(childReal))
                {
                    return FailWcc(body, NfsStatus.NoEnt);
                }

                if (Directory.EnumerateFileSystemEntries(childReal).Any())
                {
                    return FailWcc(body, NfsStatus.NotEmpty);
                }

                Directory.Delete(childReal, recursive: false);
            }
            else
            {
                if (Directory.Exists(childReal))
                {
                    return FailWcc(body, NfsStatus.IsDir);
                }

                if (!File.Exists(childReal))
                {
                    return FailWcc(body, NfsStatus.NoEnt);
                }

                File.Delete(childReal);
            }

            Log($"{(directory ? "Pasta apagada" : "Arquivo apagado")}: {childVirtual}");
            _listings.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailWcc(body, Map(ex));
        }

        return body.UInt32(0).Bool(false).Bool(false);
    }

    private XdrWriter Rename(XdrReader args, XdrWriter body)
    {
        var fromStatus = ResolveChild(args, out _, out _, out _, out var fromVirtual, out var fromReal);
        var toStatus = ResolveChild(args, out _, out _, out _, out var toVirtual, out var toReal);
        var status = fromStatus != NfsStatus.Ok ? fromStatus : toStatus;
        if (status != NfsStatus.Ok)
        {
            return FailRename(body, status);
        }

        if (ReadOnly)
        {
            Log($"Renomear \"{fromVirtual}\" recusado: somente leitura");
            return FailRename(body, NfsStatus.RoFs);
        }

        try
        {
            var isDirectory = Directory.Exists(fromReal);
            if (!isDirectory && !File.Exists(fromReal))
            {
                return FailRename(body, NfsStatus.NoEnt);
            }

            if (string.Equals(fromVirtual, toVirtual, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && string.Equals(fromReal, toReal, StringComparison.Ordinal))
            {
                return body.UInt32(0).Bool(false).Bool(false).Bool(false).Bool(false);
            }

            if (isDirectory)
            {
                if (toVirtual.StartsWith(fromVirtual + "/", PathComparison))
                {
                    return FailRename(body, NfsStatus.Inval);
                }

                if (Directory.Exists(toReal) && Directory.EnumerateFileSystemEntries(toReal).Any())
                {
                    return FailRename(body, NfsStatus.NotEmpty);
                }

                if (File.Exists(toReal))
                {
                    return FailRename(body, NfsStatus.NotDir);
                }

                if (Directory.Exists(toReal))
                {
                    Directory.Delete(toReal, recursive: false);
                }

                Directory.Move(fromReal, toReal);
            }
            else
            {
                if (Directory.Exists(toReal))
                {
                    return FailRename(body, NfsStatus.IsDir);
                }

                File.Move(fromReal, toReal, overwrite: true);
            }

            _handles.Rename(fromVirtual, toVirtual);
            _listings.Clear();
            Log($"Renomeado: {fromVirtual} -> {toVirtual}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailRename(body, Map(ex));
        }

        return body.UInt32(0).Bool(false).Bool(false).Bool(false).Bool(false);
    }

    private static XdrWriter FailRename(XdrWriter body, NfsStatus status) =>
        body.UInt32((uint)status).Bool(false).Bool(false).Bool(false).Bool(false);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private XdrWriter ReadDirectory(XdrReader args, XdrWriter body, RpcIdentity identity, bool plus)
    {
        var status = ResolveHandle(args, out var directoryVirtual, out var directoryReal);
        var cookie = args.UInt64();
        _ = args.Fixed(NfsProtocol.VerifierBytes);
        var dirCount = (int)Math.Min(args.UInt32(), 1u << 20);
        var maxCount = plus ? (int)Math.Min(args.UInt32(), 1u << 20) : dirCount;
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        if (!Directory.Exists(directoryReal))
        {
            return FailPostOp(body, File.Exists(directoryReal) ? NfsStatus.NotDir : NfsStatus.Stale);
        }

        string[] names;
        try
        {
            names = ListNames(directoryReal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailPostOp(body, Map(ex));
        }

        body.UInt32(0);
        PostOpAttributes(body, directoryVirtual, directoryReal, identity);
        body.Fixed(new byte[NfsProtocol.VerifierBytes]);

        var parentVirtual = ParentOf(directoryVirtual);
        var used = DirectoryPageHeaderBytes;
        var usedNames = DirectoryPageHeaderBytes;
        var emitted = 0;
        var total = names.Length + 2;
        var position = (int)Math.Min(cookie, (ulong)total);

        for (; position < total; position++)
        {
            string entryName;
            string entryVirtual;
            if (position == 0)
            {
                entryName = ".";
                entryVirtual = directoryVirtual;
            }
            else if (position == 1)
            {
                entryName = "..";
                entryVirtual = parentVirtual;
            }
            else
            {
                entryName = names[position - 2];
                entryVirtual = (directoryVirtual == "/" ? string.Empty : directoryVirtual) + "/" + entryName;
            }

            var nameBytes = 4 + ((System.Text.Encoding.UTF8.GetByteCount(entryName) + 3) & ~3);
            var entryBytes = 4 + 8 + nameBytes + 8 + (plus ? 4 + NfsProtocol.AttributesBytes + 4 + 4 + 16 : 0);
            var nameOnlyBytes = 4 + 8 + nameBytes + 8;
            if (emitted > 0 && (used + entryBytes > maxCount || usedNames + nameOnlyBytes > dirCount))
            {
                break;
            }

            if (emitted == 0 && used + entryBytes > maxCount)
            {
                return FailPostOp(body = new XdrWriter(16), NfsStatus.TooSmall);
            }

            FileSystemInfo? info = null;
            if (plus)
            {
                if (ResolvePath(entryVirtual, out entryVirtual, out var entryReal) != NfsStatus.Ok)
                {
                    continue; // link que sai da pasta: não aparece
                }

                info = Info(entryReal);
                if (info is null)
                {
                    continue; // sumiu desde a listagem
                }
            }

            body.Bool(true).UInt64(_handles.IdFor(entryVirtual)).String(entryName).UInt64((ulong)position + 1);
            if (plus)
            {
                body.Bool(true);
                WriteAttributes(body, entryVirtual, info!, identity);
                body.Bool(true).Opaque(_handles.HandleFor(entryVirtual));
            }

            used += entryBytes;
            usedNames += nameOnlyBytes;
            emitted++;
        }

        body.Bool(false); // fim da lista de entradas
        return body.Bool(position >= total);
    }

    private string[] ListNames(string directoryReal)
    {
        var modified = Directory.GetLastWriteTimeUtc(directoryReal).Ticks;
        var now = DateTime.UtcNow;
        if (_listings.TryGetValue(directoryReal, out var cached) && cached.Modified == modified && now - cached.At < ListingCacheLifetime)
        {
            return cached.Names;
        }

        // Nome seguro e, se for atalho ou junção, só quando o destino também está dentro da pasta exportada: o nome de um
        // link que sai da pasta não pode aparecer nem na listagem simples (READDIR), que só traz nomes.
        var names = new DirectoryInfo(directoryReal).EnumerateFileSystemInfos()
            .Where(entry => ServerVirtualPath.IsSafeSegment(entry.Name)
                            && ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0
                                || ServerPathGuard.IsInside(RootPath, entry.FullName, allowRoot: false)))
            .Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (_listings.Count > 16)
        {
            _listings.Clear();
        }

        _listings[directoryReal] = new DirectoryListing(modified, now, names);
        return names;
    }

    private sealed record DirectoryListing(long Modified, DateTime At, string[] Names);

    private XdrWriter FileSystemStatus(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        ulong total = 0;
        ulong free = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(RootPath)!);
            total = (ulong)Math.Max(0, drive.TotalSize);
            free = (ulong)Math.Max(0, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // sem informação de espaço: zeros
        }

        body.UInt32(0);
        PostOpAttributes(body, virtualPath, real, identity);
        return body.UInt64(total).UInt64(free).UInt64(free).UInt64(1 << 24).UInt64(1 << 24).UInt64(1 << 24).UInt32(0);
    }

    private XdrWriter FileSystemInformation(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        body.UInt32(0);
        PostOpAttributes(body, virtualPath, real, identity);
        return body
            .UInt32(MaxIoBytes).UInt32(PreferredIoBytes).UInt32(4096)
            .UInt32(MaxIoBytes).UInt32(PreferredIoBytes).UInt32(4096)
            .UInt32(32768)
            .UInt64(long.MaxValue)
            .UInt32(0).UInt32(100)
            .UInt32(0x18); // FSF3_HOMOGENEOUS | FSF3_CANSETTIME
    }

    private XdrWriter PathConfiguration(XdrReader args, XdrWriter body, RpcIdentity identity)
    {
        var status = ResolveHandle(args, out var virtualPath, out var real);
        if (status != NfsStatus.Ok)
        {
            return FailPostOp(body, status);
        }

        body.UInt32(0);
        PostOpAttributes(body, virtualPath, real, identity);
        return body.UInt32(1).UInt32(NfsProtocol.NameMaxBytes).Bool(true).Bool(true).Bool(OperatingSystem.IsWindows()).Bool(true);
    }

    private static string ParentOf(string virtualPath)
    {
        var slash = virtualPath.LastIndexOf('/');
        return slash <= 0 ? "/" : virtualPath[..slash];
    }
}

/// <summary>
/// Tabela de identificadores (file handles) do servidor NFS. O NFS identifica arquivos por um valor opaco de até 64
/// bytes, mas o Windows não oferece um número estável para um caminho: o identificador é o número de um caminho
/// virtual guardado aqui (16 bytes: marca do servidor + número). Reiniciar o servidor invalida todos (o cliente recebe
/// "STALE" e monta de novo).
/// </summary>
internal sealed class NfsHandleTable
{
    private const int HandleBytes = 16;
    private const int MaxEntries = 2_000_000;

    private readonly object _sync = new();
    private readonly byte[] _serverMark = RandomNumberGenerator.GetBytes(8);
    private readonly Dictionary<ulong, string> _paths = new();
    private readonly Dictionary<string, ulong> _ids;
    private ulong _nextId = 2;

    public NfsHandleTable()
    {
        _ids = new Dictionary<string, ulong>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["/"] = 1
        };
        _paths[1] = "/";
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _ids.Count;
            }
        }
    }

    public ulong IdFor(string virtualPath)
    {
        lock (_sync)
        {
            if (_ids.TryGetValue(virtualPath, out var id))
            {
                return id;
            }

            if (_ids.Count >= MaxEntries)
            {
                // Passou do limite: esquece tudo menos a raiz. Os clientes recebem STALE e procuram de novo.
                _ids.Clear();
                _paths.Clear();
                _ids["/"] = 1;
                _paths[1] = "/";
            }

            id = _nextId++;
            _ids[virtualPath] = id;
            _paths[id] = virtualPath;
            return id;
        }
    }

    public byte[] HandleFor(string virtualPath)
    {
        var handle = new byte[HandleBytes];
        _serverMark.CopyTo(handle, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(handle.AsSpan(8), IdFor(virtualPath));
        return handle;
    }

    public bool TryGetPath(ReadOnlySpan<byte> handle, out string virtualPath)
    {
        virtualPath = "/";
        if (handle.Length != HandleBytes || !handle[..8].SequenceEqual(_serverMark))
        {
            return false;
        }

        var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(handle[8..]);
        lock (_sync)
        {
            if (!_paths.TryGetValue(id, out var path))
            {
                return false;
            }

            virtualPath = path;
            return true;
        }
    }

    /// <summary>Depois de renomear, os identificadores de tudo que estava embaixo seguem o item para o novo caminho.</summary>
    public void Rename(string fromPath, string toPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        lock (_sync)
        {
            var moved = _ids
                .Where(pair => pair.Key.Equals(fromPath, comparison) || pair.Key.StartsWith(fromPath + "/", comparison))
                .ToList();
            foreach (var (path, id) in moved)
            {
                _ids.Remove(path);
                var renamed = toPath + path[fromPath.Length..];

                // Um identificador que já apontava para o destino (arquivo sobrescrito) deixa de valer.
                if (_ids.TryGetValue(renamed, out var overwritten) && overwritten != id)
                {
                    _paths.Remove(overwritten);
                }

                _ids[renamed] = id;
                _paths[id] = renamed;
            }
        }
    }
}

/// <summary>Nome do compartilhamento (SMB) ou da exportação (NFS) a partir da pasta raiz.</summary>
internal static class ShareNames
{
    public static string FromFolder(string rootPath)
    {
        var trimmed = rootPath.TrimEnd('\\', '/');
        var name = trimmed[(Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/')) + 1)..];
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        return cleaned.Length == 0 ? "compartilhado" : cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}

/// <summary>Quais endereços de cliente os servidores sem senha (NFS) atendem: só a rede local.</summary>
internal static class NetworkAddressPolicy
{
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
               && (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }
}
