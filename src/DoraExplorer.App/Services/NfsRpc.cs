using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DoraExplorer.App.Services;

/// <summary>Mensagem XDR (RFC 4506) malformada, curta demais ou grande demais.</summary>
internal sealed class XdrException(string message) : Exception(message);

/// <summary>Resposta de erro do RPC (programa ausente, versão incompatível, autenticação recusada...).</summary>
internal sealed class RpcException(string message) : IOException(message);

/// <summary>Escrita XDR: tudo em big-endian e com preenchimento até múltiplos de 4 bytes.</summary>
internal sealed class XdrWriter
{
    private byte[] _buffer;
    private int _length;

    public XdrWriter(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _length;

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => AsSpan().ToArray();

    public XdrWriter UInt32(uint value)
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public XdrWriter Int32(int value) => UInt32(unchecked((uint)value));

    public XdrWriter UInt64(ulong value)
    {
        Reserve(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
        return this;
    }

    public XdrWriter Bool(bool value) => UInt32(value ? 1u : 0u);

    /// <summary>Dados de tamanho variável: tamanho, bytes e preenchimento.</summary>
    public XdrWriter Opaque(ReadOnlySpan<byte> data)
    {
        UInt32((uint)data.Length);
        return Fixed(data);
    }

    /// <summary>Dados de tamanho fixo (sem o tamanho na frente): só os bytes e o preenchimento.</summary>
    public XdrWriter Fixed(ReadOnlySpan<byte> data)
    {
        var padded = (data.Length + 3) & ~3;
        Reserve(padded);
        data.CopyTo(_buffer.AsSpan(_length));
        _buffer.AsSpan(_length + data.Length, padded - data.Length).Clear();
        _length += padded;
        return this;
    }

    public XdrWriter String(string value) => Opaque(Encoding.UTF8.GetBytes(value));

    /// <summary>Acrescenta bytes já codificados em XDR.</summary>
    public XdrWriter Raw(ReadOnlySpan<byte> encoded)
    {
        Reserve(encoded.Length);
        encoded.CopyTo(_buffer.AsSpan(_length));
        _length += encoded.Length;
        return this;
    }

    private void Reserve(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        var size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>Leitura XDR com limites: tamanhos declarados pela rede nunca viram alocações descontroladas.</summary>
internal sealed class XdrReader(byte[] buffer, int offset, int length)
{
    private int _position = offset;
    private readonly int _end = offset + length;

    public int Remaining => _end - _position;

    public uint UInt32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(_position));
        _position += 4;
        return value;
    }

    public int Int32() => unchecked((int)UInt32());

    public ulong UInt64()
    {
        Need(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(_position));
        _position += 8;
        return value;
    }

    public bool Bool() => UInt32() != 0;

    /// <summary>Dados de tamanho variável, com no máximo <paramref name="maxLength"/> bytes.</summary>
    public byte[] Opaque(int maxLength) => OpaqueSpan(maxLength).ToArray();

    public ReadOnlySpan<byte> OpaqueSpan(int maxLength)
    {
        var declared = UInt32();
        if (declared > (uint)maxLength)
        {
            throw new XdrException($"Campo de {declared} bytes acima do limite de {maxLength}.");
        }

        return FixedSpan((int)declared);
    }

    public byte[] Fixed(int length) => FixedSpan(length).ToArray();

    public ReadOnlySpan<byte> FixedSpan(int length)
    {
        var padded = (length + 3) & ~3;
        Need(padded);
        var span = buffer.AsSpan(_position, length);
        _position += padded;
        return span;
    }

    public string String(int maxLength) => Encoding.UTF8.GetString(OpaqueSpan(maxLength));

    private void Need(int count)
    {
        if (count < 0 || count > _end - _position)
        {
            throw new XdrException("Mensagem XDR incompleta.");
        }
    }
}

/// <summary>Identidade AUTH_SYS enviada em cada chamada (o NFS confia no que o cliente diz ser).</summary>
internal sealed record RpcIdentity(uint Uid, uint Gid, string MachineName)
{
    public static RpcIdentity Anonymous { get; } = new(0, 0, "dora-explorer");

    /// <summary>"uid:gid", "uid" (o grupo repete o número) ou vazio (0:0).</summary>
    public static bool TryParse(string? text, out RpcIdentity identity)
    {
        identity = Anonymous;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 2 ||
            !uint.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid))
        {
            return false;
        }

        var gid = uid;
        if (parts.Length == 2 &&
            !uint.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gid))
        {
            return false;
        }

        identity = Anonymous with { Uid = uid, Gid = gid };
        return true;
    }
}

internal static class Rpc
{
    public const uint PortmapProgram = 100000;
    public const uint NfsProgram = 100003;
    public const uint MountProgram = 100005;

    public const uint AuthNone = 0;
    public const uint AuthSys = 1;

    public const uint MsgCall = 0;
    public const uint MsgReply = 1;

    public const uint AcceptSuccess = 0;
    public const uint AcceptProgUnavail = 1;
    public const uint AcceptProgMismatch = 2;
    public const uint AcceptProcUnavail = 3;
    public const uint AcceptGarbageArgs = 4;
    public const uint AcceptSystemErr = 5;

    /// <summary>Maior registro (uma chamada ou resposta) aceito: um WRITE ou READ de 1 MiB mais o cabeçalho.</summary>
    public const int MaxRecordBytes = (1 << 20) + 4096;

    /// <summary>Lê um registro RPC sobre TCP (RFC 5531, 11): fragmentos com cabeçalho de 4 bytes, o bit mais alto marca o último.</summary>
    public static byte[]? ReadRecord(Stream stream)
    {
        using var record = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        var first = true;
        while (true)
        {
            var got = ReadFully(stream, header);
            if (got == 0 && first)
            {
                return null; // o outro lado fechou entre dois registros
            }

            if (got < 4)
            {
                throw new EndOfStreamException("Conexão encerrada no meio de um registro RPC.");
            }

            first = false;
            var word = BinaryPrimitives.ReadUInt32BigEndian(header);
            var last = (word & 0x80000000u) != 0;
            var size = (int)(word & 0x7FFFFFFFu);
            if (size > MaxRecordBytes || record.Length + size > MaxRecordBytes)
            {
                throw new XdrException("Registro RPC acima do limite.");
            }

            var chunk = new byte[size];
            if (ReadFully(stream, chunk) < size)
            {
                throw new EndOfStreamException("Conexão encerrada no meio de um registro RPC.");
            }

            record.Write(chunk);
            if (last)
            {
                return record.ToArray();
            }
        }
    }

    public static async Task<byte[]?> ReadRecordAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var record = new MemoryStream();
        var header = new byte[4];
        var first = true;
        while (true)
        {
            var got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (got == 0 && first)
            {
                return null;
            }

            if (got < 4)
            {
                throw new EndOfStreamException("Conexão encerrada no meio de um registro RPC.");
            }

            first = false;
            var word = BinaryPrimitives.ReadUInt32BigEndian(header);
            var last = (word & 0x80000000u) != 0;
            var size = (int)(word & 0x7FFFFFFFu);
            if (size > MaxRecordBytes || record.Length + size > MaxRecordBytes)
            {
                throw new XdrException("Registro RPC acima do limite.");
            }

            var chunk = new byte[size];
            if (await ReadFullyAsync(stream, chunk, cancellationToken).ConfigureAwait(false) < size)
            {
                throw new EndOfStreamException("Conexão encerrada no meio de um registro RPC.");
            }

            record.Write(chunk);
            if (last)
            {
                return record.ToArray();
            }
        }
    }

    /// <summary>Envia a mensagem como um único fragmento final.</summary>
    public static void WriteRecord(Stream stream, ReadOnlySpan<byte> message)
    {
        var framed = Frame(message);
        stream.Write(framed);
        stream.Flush();
    }

    public static async Task WriteRecordAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var framed = Frame(message.Span);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Frame(ReadOnlySpan<byte> message)
    {
        var framed = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, 0x80000000u | (uint)message.Length);
        message.CopyTo(framed.AsSpan(4));
        return framed;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Cabeçalho de uma resposta aceita e, em seguida, o corpo do resultado.</summary>
    public static byte[] AcceptedReply(uint xid, uint acceptStatus, XdrWriter? body = null, uint low = 0, uint high = 0)
    {
        var writer = new XdrWriter(64 + (body?.Length ?? 0));
        writer.UInt32(xid).UInt32(MsgReply).UInt32(0 /* MSG_ACCEPTED */);
        writer.UInt32(AuthNone).UInt32(0); // verificador vazio
        writer.UInt32(acceptStatus);
        if (acceptStatus == AcceptProgMismatch)
        {
            writer.UInt32(low).UInt32(high);
        }

        if (body is not null && acceptStatus == AcceptSuccess)
        {
            writer.Raw(body.AsSpan());
        }

        return writer.ToArray();
    }

    /// <summary>Resposta negada: versão do RPC diferente de 2 (<paramref name="authError"/> nulo) ou autenticação recusada.</summary>
    public static byte[] DeniedReply(uint xid, uint? authError)
    {
        var writer = new XdrWriter(32);
        writer.UInt32(xid).UInt32(MsgReply).UInt32(1 /* MSG_DENIED */);
        if (authError is { } stat)
        {
            writer.UInt32(1 /* AUTH_ERROR */).UInt32(stat);
        }
        else
        {
            writer.UInt32(0 /* RPC_MISMATCH */).UInt32(2).UInt32(2);
        }

        return writer.ToArray();
    }

    /// <summary>Uma chamada RPC decodificada (o corpo dos argumentos fica no <see cref="Arguments"/>).</summary>
    public sealed record CallMessage(
        uint Xid,
        uint RpcVersion,
        uint Program,
        uint Version,
        uint Procedure,
        uint CredentialFlavor,
        RpcIdentity? Identity,
        XdrReader Arguments);

    public static CallMessage ParseCall(byte[] record)
    {
        var reader = new XdrReader(record, 0, record.Length);
        var xid = reader.UInt32();
        var type = reader.UInt32();
        if (type != MsgCall)
        {
            throw new XdrException("Mensagem RPC que não é uma chamada.");
        }

        var rpcVersion = reader.UInt32();
        var program = reader.UInt32();
        var version = reader.UInt32();
        var procedure = reader.UInt32();

        var flavor = reader.UInt32();
        var credential = reader.OpaqueSpan(400);
        RpcIdentity? identity = null;
        if (flavor == AuthSys)
        {
            identity = ParseAuthSys(credential);
        }

        _ = reader.UInt32(); // sabor do verificador
        _ = reader.OpaqueSpan(400);
        return new CallMessage(xid, rpcVersion, program, version, procedure, flavor, identity, reader);
    }

    private static RpcIdentity? ParseAuthSys(ReadOnlySpan<byte> body)
    {
        try
        {
            var copy = body.ToArray();
            var reader = new XdrReader(copy, 0, copy.Length);
            _ = reader.UInt32(); // carimbo de hora
            var machine = reader.String(255);
            var uid = reader.UInt32();
            var gid = reader.UInt32();
            return new RpcIdentity(uid, gid, machine);
        }
        catch (XdrException)
        {
            return null;
        }
    }

    /// <summary>Corpo da credencial AUTH_SYS para uma chamada.</summary>
    public static byte[] BuildAuthSys(RpcIdentity identity, uint stamp)
    {
        var writer = new XdrWriter(64);
        writer.UInt32(stamp).String(identity.MachineName).UInt32(identity.Uid).UInt32(identity.Gid).UInt32(0);
        return writer.ToArray();
    }
}

/// <summary>Conexão TCP com um serviço ONC RPC; uma chamada de cada vez, esperando a resposta.</summary>
internal sealed class RpcClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly RpcIdentity _identity;
    private uint _xid;

    private RpcClient(TcpClient tcp, RpcIdentity identity)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _identity = identity;
        _xid = (uint)Random.Shared.Next();
    }

    public static RpcClient Connect(string host, int port, RpcIdentity identity, TimeSpan timeout)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var connect = tcp.ConnectAsync(host, port);
            if (!connect.Wait(timeout))
            {
                throw new TimeoutException($"Tempo esgotado ao ligar a {host}:{port}.");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException socket)
        {
            tcp.Dispose();
            throw new IOException($"Não foi possível ligar a {host}:{port}: {socket.Message}", socket);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        tcp.SendTimeout = (int)timeout.TotalMilliseconds;
        return new RpcClient(tcp, identity);
    }

    /// <summary>Faz a chamada e devolve o leitor posicionado no resultado.</summary>
    public XdrReader Call(uint program, uint version, uint procedure, Action<XdrWriter>? arguments = null)
    {
        var xid = ++_xid;
        var message = new XdrWriter(128);
        message.UInt32(xid).UInt32(Rpc.MsgCall).UInt32(2).UInt32(program).UInt32(version).UInt32(procedure);
        message.UInt32(Rpc.AuthSys).Opaque(Rpc.BuildAuthSys(_identity, xid));
        message.UInt32(Rpc.AuthNone).UInt32(0);
        arguments?.Invoke(message);

        try
        {
            Rpc.WriteRecord(_stream, message.AsSpan());
            while (true)
            {
                var record = Rpc.ReadRecord(_stream)
                    ?? throw new IOException("O servidor encerrou a conexão sem responder.");
                var reader = new XdrReader(record, 0, record.Length);
                var replyXid = reader.UInt32();
                if (replyXid != xid)
                {
                    continue; // resposta atrasada de uma chamada anterior
                }

                return ParseReply(reader);
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException("O servidor não respondeu a tempo.", ex);
        }
    }

    private static XdrReader ParseReply(XdrReader reader)
    {
        if (reader.UInt32() != Rpc.MsgReply)
        {
            throw new RpcException("Resposta RPC inválida.");
        }

        if (reader.UInt32() != 0)
        {
            var reject = reader.UInt32();
            throw new RpcException(reject == 0
                ? "O servidor não aceita esta versão do RPC."
                : $"O servidor recusou a autenticação (código {reader.UInt32()}).");
        }

        _ = reader.UInt32(); // sabor do verificador
        _ = reader.OpaqueSpan(400);
        var accept = reader.UInt32();
        switch (accept)
        {
            case Rpc.AcceptSuccess:
                return reader;
            case Rpc.AcceptProgUnavail:
                throw new RpcException("O servidor não oferece este serviço.");
            case Rpc.AcceptProgMismatch:
                throw new RpcException($"O servidor não aceita esta versão do protocolo (aceita de {reader.UInt32()} a {reader.UInt32()}).");
            case Rpc.AcceptProcUnavail:
                throw new RpcException("O servidor não implementa esta operação.");
            default:
                throw new RpcException("O servidor não conseguiu processar o pedido.");
        }
    }

    public void Dispose() => _tcp.Dispose();
}
