using System.Collections.Concurrent;
using System.IO;

namespace DoraExplorer.App.Services;

/// <summary>
/// Cliente NFS versão 3 sobre TCP (RFC 1813, com MOUNT v3 e portmap v2): lê, grava e organiza arquivos num servidor NFS.
/// A autenticação é AUTH_SYS (o servidor confia no uid e no gid informados; não há senha nem Kerberos). Uma instância
/// serve a uma sessão e não é segura para várias threads.
/// </summary>
internal sealed class NfsClient : IDisposable
{
    private const uint PortmapVersion = 2;
    private const uint PortmapGetPort = 3;
    private const uint ProtocolTcp = 6;
    private const int PortmapPort = 111;
    private const int FallbackBlockBytes = 32768;
    private const int MaxBlockBytes = 1 << 20;

    // Descobrir a porta do mountd custa uma ligação com a porta 111, que muitos firewalls deixam sem resposta: guarda o resultado.
    private static readonly ConcurrentDictionary<(string Host, int Port, int PortmapPort), (int MountPort, DateTime At)> MountPorts = new();
    private static readonly TimeSpan MountPortLifetime = TimeSpan.FromMinutes(10);

    private readonly string _host;
    private readonly int _nfsPort;
    private readonly RpcIdentity _identity;
    private readonly TimeSpan _timeout;
    private readonly RpcClient _nfs;
    private RpcClient? _mount;

    private NfsClient(string host, int nfsPort, RpcIdentity identity, TimeSpan timeout, RpcClient nfs)
    {
        _host = host;
        _nfsPort = nfsPort;
        _identity = identity;
        _timeout = timeout;
        _nfs = nfs;
    }

    public int ReadBlockBytes { get; private set; } = FallbackBlockBytes;

    public int WriteBlockBytes { get; private set; } = FallbackBlockBytes;

    public static NfsClient Connect(string host, int port, RpcIdentity identity, TimeSpan timeout)
    {
        var nfs = RpcClient.Connect(host, port, identity, timeout);
        try
        {
            _ = nfs.Call(Rpc.NfsProgram, NfsProtocol.NfsVersion, NfsProtocol.ProcNull);
            return new NfsClient(host, port, identity, timeout, nfs);
        }
        catch
        {
            nfs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Porta do MOUNT: a que o portmap (porta 111) informar. Sem portmap (um servidor que atende tudo numa porta só, como o
    /// do próprio Dora Explorer) usa a porta do NFS.
    /// </summary>
    internal static int DiscoverMountPort(string host, int nfsPort, RpcIdentity identity, TimeSpan timeout, int portmapPort = PortmapPort)
    {
        var key = (host.ToUpperInvariant(), nfsPort, portmapPort);
        if (MountPorts.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < MountPortLifetime)
        {
            return known.MountPort;
        }

        var mountPort = nfsPort;
        try
        {
            using var portmap = RpcClient.Connect(host, portmapPort, identity, TimeSpan.FromSeconds(Math.Min(2, timeout.TotalSeconds)));
            var reply = portmap.Call(Rpc.PortmapProgram, PortmapVersion, PortmapGetPort, w =>
                w.UInt32(Rpc.MountProgram).UInt32(NfsProtocol.MountVersion).UInt32(ProtocolTcp).UInt32(0));
            var port = reply.UInt32();
            if (port is > 0 and <= 65535)
            {
                mountPort = (int)port;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or XdrException or RpcException
            or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // sem portmap: tudo na mesma porta
        }

        MountPorts[key] = (mountPort, DateTime.UtcNow);
        return mountPort;
    }

    // ---- MOUNT -------------------------------------------------------------------------------

    private RpcClient MountClient()
    {
        if (_mount is not null)
        {
            return _mount;
        }

        var mountPort = DiscoverMountPort(_host, _nfsPort, _identity, _timeout);
        _mount = mountPort == _nfsPort ? _nfs : RpcClient.Connect(_host, mountPort, _identity, _timeout);
        return _mount;
    }

    /// <summary>Pastas que o servidor exporta (as que podem ser montadas).</summary>
    public IReadOnlyList<string> ListExports()
    {
        var reply = MountClient().Call(Rpc.MountProgram, NfsProtocol.MountVersion, NfsProtocol.MountProcExport);
        var exports = new List<string>();
        while (reply.Bool())
        {
            exports.Add(reply.String(NfsProtocol.PathMaxBytes));
            while (reply.Bool())
            {
                _ = reply.String(NfsProtocol.NameMaxBytes); // grupos ou redes com permissão: não usado
            }
        }

        return exports;
    }

    /// <summary>"Monta" uma pasta exportada e devolve o identificador (file handle) da raiz dela.</summary>
    public byte[] Mount(string export)
    {
        var reply = MountClient().Call(Rpc.MountProgram, NfsProtocol.MountVersion, NfsProtocol.MountProcMnt,
            w => w.String(export));
        var status = reply.UInt32();
        if (status != 0)
        {
            throw NfsException.From(MapMountStatus(status), $"Não foi possível abrir \"{export}\"");
        }

        var handle = reply.Opaque(NfsProtocol.FileHandleMaxBytes);

        // O servidor lista os tipos de autenticação aceitos; só o AUTH_SYS (1) é suportado aqui.
        var flavors = reply.UInt32();
        if (flavors is > 0 and < 64)
        {
            var acceptsSys = false;
            for (var i = 0; i < flavors; i++)
            {
                acceptsSys |= reply.UInt32() == Rpc.AuthSys;
            }

            if (!acceptsSys)
            {
                throw new NfsException(NfsStatus.Access, $"\"{export}\" exige autenticação Kerberos, que este cliente não suporta.");
            }
        }

        LoadTransferSizes(handle);
        return handle;
    }

    public void Unmount(string export)
    {
        try
        {
            _ = MountClient().Call(Rpc.MountProgram, NfsProtocol.MountVersion, NfsProtocol.MountProcUmnt, w => w.String(export));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or XdrException)
        {
            // só informativo para o servidor
        }
    }

    private static NfsStatus MapMountStatus(uint status) =>
        Enum.IsDefined(typeof(NfsStatus), status) ? (NfsStatus)status : NfsStatus.ServerFault;

    private void LoadTransferSizes(byte[] handle)
    {
        try
        {
            var reply = _nfs.Call(Rpc.NfsProgram, NfsProtocol.NfsVersion, NfsProtocol.ProcFsInfo, w => w.Opaque(handle));
            if ((NfsStatus)reply.UInt32() != NfsStatus.Ok)
            {
                return;
            }

            _ = NfsProtocol.ReadPostOpAttributes(reply);
            _ = reply.UInt32(); // rtmax
            var rtpref = reply.UInt32();
            _ = reply.UInt32(); // rtmult
            _ = reply.UInt32(); // wtmax
            var wtpref = reply.UInt32();
            ReadBlockBytes = ClampBlock(rtpref);
            WriteBlockBytes = ClampBlock(wtpref);
        }
        catch (Exception ex) when (ex is IOException or XdrException)
        {
            // fica com o tamanho de bloco padrão
        }
    }

    private static int ClampBlock(uint preferred) =>
        preferred < 4096 ? FallbackBlockBytes : (int)Math.Min(preferred, MaxBlockBytes);

    // ---- Arquivos e pastas -------------------------------------------------------------------

    public NfsAttributes GetAttributes(byte[] handle)
    {
        var reply = Call(NfsProtocol.ProcGetAttr, w => w.Opaque(handle));
        Check(reply, "Ler os dados do item");
        return NfsProtocol.ReadAttributes(reply);
    }

    /// <summary>Procura <paramref name="name"/> dentro da pasta; nulo se não existir.</summary>
    public (byte[] Handle, NfsAttributes Attributes)? Lookup(byte[] directory, string name)
    {
        var reply = Call(NfsProtocol.ProcLookup, w => w.Opaque(directory).String(name));
        var status = (NfsStatus)reply.UInt32();
        if (status == NfsStatus.NoEnt)
        {
            return null;
        }

        if (status != NfsStatus.Ok)
        {
            throw NfsException.From(status, $"Procurar \"{name}\"");
        }

        var handle = reply.Opaque(NfsProtocol.FileHandleMaxBytes);
        var attributes = NfsProtocol.ReadPostOpAttributes(reply) ?? GetAttributes(handle);
        return (handle, attributes);
    }

    /// <summary>Lista a pasta com READDIRPLUS (atributos junto) e cai no READDIR simples se o servidor não o tem.</summary>
    /// <param name="directory">Identificador da pasta.</param>
    /// <param name="usePlus">Falso pula o READDIRPLUS e usa o READDIR direto (os testes cobrem os dois).</param>
    public IReadOnlyList<NfsDirectoryEntry> ReadDirectory(byte[] directory, bool usePlus = true)
    {
        if (!usePlus)
        {
            return ReadDirectoryCore(directory, plus: false);
        }

        try
        {
            return ReadDirectoryCore(directory, plus: true);
        }
        catch (NfsException ex) when (ex.Status == NfsStatus.NotSupp)
        {
            return ReadDirectoryCore(directory, plus: false);
        }
    }

    private List<NfsDirectoryEntry> ReadDirectoryCore(byte[] directory, bool plus)
    {
        var entries = new List<NfsDirectoryEntry>();
        ulong cookie = 0;
        var verifier = new byte[NfsProtocol.VerifierBytes];
        const int maxEntries = 5_000_000; // proteção contra servidor que nunca marca o fim

        while (entries.Count < maxEntries)
        {
            var currentCookie = cookie;
            var currentVerifier = verifier;
            var reply = Call(plus ? NfsProtocol.ProcReadDirPlus : NfsProtocol.ProcReadDir, w =>
            {
                w.Opaque(directory).UInt64(currentCookie).Fixed(currentVerifier);
                if (plus)
                {
                    w.UInt32(16384).UInt32(65536);
                }
                else
                {
                    w.UInt32(16384);
                }
            });
            Check(reply, "Listar a pasta");
            _ = NfsProtocol.ReadPostOpAttributes(reply);
            verifier = reply.Fixed(NfsProtocol.VerifierBytes);

            var before = entries.Count;
            while (reply.Bool())
            {
                var fileId = reply.UInt64();
                var name = reply.String(NfsProtocol.NameMaxBytes);
                cookie = reply.UInt64();
                NfsAttributes? attributes = null;
                byte[]? handle = null;
                if (plus)
                {
                    attributes = NfsProtocol.ReadPostOpAttributes(reply);
                    if (reply.Bool())
                    {
                        handle = reply.Opaque(NfsProtocol.FileHandleMaxBytes);
                    }
                }

                if (name is not ("." or ".."))
                {
                    entries.Add(new NfsDirectoryEntry(name, fileId, attributes, handle));
                }
            }

            if (reply.Bool())
            {
                break; // eof
            }

            if (entries.Count == before && cookie == currentCookie)
            {
                throw new IOException("O servidor não avançou na listagem da pasta.");
            }
        }

        return entries;
    }

    /// <summary>Lê até <paramref name="buffer"/>.Length bytes a partir de <paramref name="offset"/>; <paramref name="eof"/> marca o fim.</summary>
    public int Read(byte[] handle, ulong offset, byte[] buffer, int count, out bool eof)
    {
        var reply = Call(NfsProtocol.ProcRead, w => w.Opaque(handle).UInt64(offset).UInt32((uint)count));
        Check(reply, "Ler o arquivo");
        _ = NfsProtocol.ReadPostOpAttributes(reply);
        _ = reply.UInt32(); // count
        eof = reply.Bool();
        var data = reply.OpaqueSpan(MaxBlockBytes + 4096);
        if (data.Length > count)
        {
            throw new IOException("O servidor devolveu mais dados do que o pedido.");
        }

        data.CopyTo(buffer);
        return data.Length;
    }

    /// <summary>Grava com FILE_SYNC (o servidor só responde depois de gravar em disco) e devolve quantos bytes foram aceitos.</summary>
    public int Write(byte[] handle, ulong offset, byte[] buffer, int count)
    {
        var reply = Call(NfsProtocol.ProcWrite, w =>
            w.Opaque(handle).UInt64(offset).UInt32((uint)count).UInt32(NfsProtocol.StableFileSync).Opaque(buffer.AsSpan(0, count)));
        Check(reply, "Gravar o arquivo");
        NfsProtocol.SkipWccData(reply);
        var written = reply.UInt32();
        return (int)Math.Min(written, (uint)count);
    }

    /// <summary>Cria o arquivo (ou o esvazia, se já existir) e devolve o identificador dele.</summary>
    public byte[] CreateOrTruncate(byte[] directory, string name, uint mode)
    {
        var reply = Call(NfsProtocol.ProcCreate, w =>
        {
            w.Opaque(directory).String(name).UInt32(0 /* UNCHECKED */);
            NfsProtocol.WriteSetAttributes(w, new NfsSetAttributes(Mode: mode, Size: 0));
        });
        Check(reply, $"Criar \"{name}\"");
        var handle = reply.Bool() ? reply.Opaque(NfsProtocol.FileHandleMaxBytes) : null;
        _ = NfsProtocol.ReadPostOpAttributes(reply);
        if (handle is null)
        {
            handle = Lookup(directory, name)?.Handle
                ?? throw new IOException($"O servidor criou \"{name}\" mas não informou o identificador.");
        }

        // Alguns servidores ignoram o tamanho do CREATE sem verificação quando o arquivo já existia.
        if (GetAttributes(handle).Size != 0)
        {
            SetAttributes(handle, new NfsSetAttributes(Size: 0));
        }

        return handle;
    }

    public void SetAttributes(byte[] handle, NfsSetAttributes attributes)
    {
        var reply = Call(NfsProtocol.ProcSetAttr, w =>
        {
            w.Opaque(handle);
            NfsProtocol.WriteSetAttributes(w, attributes);
            w.Bool(false); // sem verificação de ctime
        });
        Check(reply, "Alterar o item");
    }

    public byte[] MakeDirectory(byte[] directory, string name, uint mode)
    {
        var reply = Call(NfsProtocol.ProcMkDir, w =>
        {
            w.Opaque(directory).String(name);
            NfsProtocol.WriteSetAttributes(w, new NfsSetAttributes(Mode: mode));
        });
        Check(reply, $"Criar a pasta \"{name}\"");
        var handle = reply.Bool() ? reply.Opaque(NfsProtocol.FileHandleMaxBytes) : null;
        return handle ?? Lookup(directory, name)?.Handle
            ?? throw new IOException($"O servidor criou \"{name}\" mas não informou o identificador.");
    }

    public void Remove(byte[] directory, string name)
    {
        var reply = Call(NfsProtocol.ProcRemove, w => w.Opaque(directory).String(name));
        Check(reply, $"Apagar \"{name}\"");
    }

    public void RemoveDirectory(byte[] directory, string name)
    {
        var reply = Call(NfsProtocol.ProcRmDir, w => w.Opaque(directory).String(name));
        Check(reply, $"Apagar a pasta \"{name}\"");
    }

    public void Rename(byte[] fromDirectory, string fromName, byte[] toDirectory, string toName)
    {
        var reply = Call(NfsProtocol.ProcRename,
            w => w.Opaque(fromDirectory).String(fromName).Opaque(toDirectory).String(toName));
        Check(reply, $"Renomear \"{fromName}\"");
    }

    private XdrReader Call(uint procedure, Action<XdrWriter> arguments) =>
        _nfs.Call(Rpc.NfsProgram, NfsProtocol.NfsVersion, procedure, arguments);

    private static void Check(XdrReader reply, string operation)
    {
        var status = (NfsStatus)reply.UInt32();
        if (status != NfsStatus.Ok)
        {
            throw NfsException.From(status, operation);
        }
    }

    public void Dispose()
    {
        if (_mount is not null && !ReferenceEquals(_mount, _nfs))
        {
            _mount.Dispose();
        }

        _nfs.Dispose();
    }
}
