using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DoraExplorer.App.Services;

internal sealed record TftpClientOptions(TimeSpan ResponseTimeout, int MaxRetries, int BlockSize = TftpClientOptions.NegotiatedBlockSize)
{
    /// <summary>Cabe num pacote Ethernet sem fragmentação (1500 - IP - UDP - cabeçalho TFTP, com folga para VPN).</summary>
    public const int NegotiatedBlockSize = 1428;

    public static TftpClientOptions Default { get; } = new(TimeSpan.FromSeconds(5), 3);

    /// <summary>Teste de ligação: resposta rápida ou falha (no máximo ~4 s).</summary>
    public static TftpClientOptions Probe { get; } = new(TimeSpan.FromSeconds(2), 1);
}

/// <summary>
/// Cliente TFTP (RFC 1350, modo octet) com negociação de opções (RFC 2347/2348/2349: blksize e tsize);
/// servidores que ignoram as opções continuam a funcionar com blocos de 512 bytes. O servidor responde a partir de uma porta
/// nova (o "TID"), por isso o socket NÃO pode estar "conectado" ao endereço:porta 69, senão o sistema
/// descarta as respostas.
/// </summary>
internal static class TftpClient
{
    private const int BlockSize = 512;
    private const byte OpRead = 1;
    private const byte OpWrite = 2;
    private const byte OpData = 3;
    private const byte OpAck = 4;
    private const byte OpError = 5;
    private const byte OpOptionAck = 6;

    public static void Download(
        string host,
        int port,
        string remoteFile,
        string localPath,
        TftpClientOptions options,
        CancellationToken cancellationToken = default)
    {
        // Grava num temporário: uma falha a meio não destrói o ficheiro local que já existia.
        var tempPath = localPath + ".dora-part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try
            {
                DownloadTo(host, port, remoteFile, tempPath, options, negotiate: options.BlockSize != BlockSize, cancellationToken);
            }
            catch (IOException ex) when (options.BlockSize != BlockSize && IsOptionRejection(ex))
            {
                // Servidor que recusa opções (erro 8): repete o pedido simples, como fazem os clientes comuns.
                DeleteQuietly(tempPath);
                DownloadTo(host, port, remoteFile, tempPath, options, negotiate: false, cancellationToken);
            }

            File.Move(tempPath, localPath, overwrite: true);
        }
        finally
        {
            DeleteQuietly(tempPath);
        }
    }

    private static void DownloadTo(
        string host,
        int port,
        string remoteFile,
        string tempPath,
        TftpClientOptions options,
        bool negotiate,
        CancellationToken cancellationToken)
    {
        using var session = new Session(ResolveServer(host, port), options);
        using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var packet = BuildRequest(OpRead, remoteFile, negotiate ? [("blksize", options.BlockSize), ("tsize", 0)] : null);
        var blockSize = BlockSize;
        var expectedBlock = 1;
        var awaitingFirstReply = true;

        while (true)
        {
            var wanted = expectedBlock & 0xFFFF;
            var acceptOptionAck = awaitingFirstReply && negotiate;
            var data = session.Expect(
                packet,
                buffer => IsPacket(buffer, OpData, wanted) || (acceptOptionAck && IsOpcode(buffer, OpOptionAck)),
                cancellationToken);
            awaitingFirstReply = false;

            if (IsOpcode(data, OpOptionAck))
            {
                blockSize = ParseAcceptedBlockSize(data, options.BlockSize);
                packet = BuildAck(0); // confirma as opções; o servidor começa a enviar o bloco 1
                continue;
            }

            var payloadLength = data.Length - 4;
            if (payloadLength > blockSize)
            {
                throw new IOException($"Resposta TFTP inválida: bloco maior que {blockSize} bytes.");
            }

            file.Write(data, 4, payloadLength);
            packet = BuildAck(wanted);

            if (payloadLength < blockSize)
            {
                session.Send(packet); // último bloco: confirma sem esperar resposta
                return;
            }

            expectedBlock++;
        }
    }

    public static void Upload(
        string host,
        int port,
        string remoteFile,
        string localPath,
        TftpClientOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            UploadFrom(host, port, remoteFile, localPath, options, negotiate: options.BlockSize != BlockSize, cancellationToken);
        }
        catch (IOException ex) when (options.BlockSize != BlockSize && IsOptionRejection(ex))
        {
            UploadFrom(host, port, remoteFile, localPath, options, negotiate: false, cancellationToken);
        }
    }

    private static void UploadFrom(
        string host,
        int port,
        string remoteFile,
        string localPath,
        TftpClientOptions options,
        bool negotiate,
        CancellationToken cancellationToken)
    {
        using var session = new Session(ResolveServer(host, port), options);
        using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // O servidor aceita o pedido de escrita com um ACK do bloco 0 (ou com um OACK, se negociou opções).
        var request = BuildRequest(OpWrite, remoteFile, negotiate ? [("blksize", options.BlockSize), ("tsize", file.Length)] : null);
        var reply = session.Expect(
            request,
            buffer => IsPacket(buffer, OpAck, 0) || (negotiate && IsOpcode(buffer, OpOptionAck)),
            cancellationToken);
        var blockSize = IsOpcode(reply, OpOptionAck) ? ParseAcceptedBlockSize(reply, options.BlockSize) : BlockSize;

        var payload = new byte[blockSize];
        var block = 1;
        while (true)
        {
            var read = ReadFully(file, payload);
            var wanted = block & 0xFFFF;
            session.Expect(BuildData(wanted, payload, read), buffer => IsPacket(buffer, OpAck, wanted), cancellationToken);

            // Um bloco curto (ou vazio, se o tamanho for múltiplo do bloco) termina a transferência.
            if (read < blockSize)
            {
                return;
            }

            block++;
        }
    }

    // Erro 8 (RFC 2347): o servidor não aceita a negociação de opções.
    private static bool IsOptionRejection(IOException ex) =>
        ex.Message.StartsWith("Erro TFTP 8", StringComparison.Ordinal);

    /// <summary>Lê o blksize confirmado no OACK; sem a opção, vale o padrão de 512. Valor acima do pedido é inválido.</summary>
    private static int ParseAcceptedBlockSize(byte[] optionAck, int requested)
    {
        var fields = new List<string>();
        var start = 2;
        for (var i = 2; i < optionAck.Length; i++)
        {
            if (optionAck[i] == 0)
            {
                fields.Add(Encoding.ASCII.GetString(optionAck, start, i - start));
                start = i + 1;
            }
        }

        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            if (!fields[i].Equals("blksize", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(fields[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var accepted) ||
                accepted < 8 || accepted > requested)
            {
                throw new IOException("O servidor TFTP respondeu com um tamanho de bloco inválido.");
            }

            return accepted;
        }

        return BlockSize;
    }
    /// <summary>
    /// Verifica se há um servidor TFTP ativo: pede um ficheiro inexistente. Qualquer resposta
    /// (normalmente ERRO "ficheiro não encontrado") prova que o servidor está a funcionar.
    /// </summary>
    public static void Probe(string host, int port, TftpClientOptions options, CancellationToken cancellationToken = default)
    {
        using var session = new Session(ResolveServer(host, port), options);
        var request = BuildRequest(OpRead, "dora-probe-" + Guid.NewGuid().ToString("N"));
        _ = session.Exchange(request, buffer => IsOpcode(buffer, OpError) || IsOpcode(buffer, OpData), cancellationToken);
    }

    private static IPEndPoint ResolveServer(string host, int port)
    {
        var addresses = Dns.GetHostAddresses(host);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault()
                      ?? throw new IOException($"Não foi possível resolver o servidor TFTP '{host}'.");
        return new IPEndPoint(address, port);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] BuildRequest(byte opcode, string fileName, IEnumerable<(string Name, long Value)>? options = null)
    {
        var bytes = new List<byte> { 0, opcode };
        bytes.AddRange(Encoding.UTF8.GetBytes(fileName));
        bytes.Add(0);
        bytes.AddRange(Encoding.ASCII.GetBytes("octet"));
        bytes.Add(0);

        foreach (var (name, value) in options ?? [])
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(name));
            bytes.Add(0);
            bytes.AddRange(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] BuildAck(int block) => [0, OpAck, (byte)(block >> 8), (byte)block];

    private static byte[] BuildData(int block, byte[] payload, int length)
    {
        var packet = new byte[4 + length];
        packet[1] = OpData;
        packet[2] = (byte)(block >> 8);
        packet[3] = (byte)block;
        Buffer.BlockCopy(payload, 0, packet, 4, length);
        return packet;
    }

    private static bool IsOpcode(byte[] buffer, byte opcode) =>
        buffer.Length >= 4 && buffer[0] == 0 && buffer[1] == opcode;

    private static bool IsPacket(byte[] buffer, byte opcode, int block) =>
        IsOpcode(buffer, opcode) && ((buffer[2] << 8) | buffer[3]) == block;

    private static void ThrowIfError(byte[] buffer)
    {
        if (!IsOpcode(buffer, OpError))
        {
            return;
        }

        var code = (buffer[2] << 8) | buffer[3];
        var end = Array.IndexOf(buffer, (byte)0, 4);
        var message = Encoding.ASCII.GetString(buffer, 4, (end < 0 ? buffer.Length : end) - 4);
        throw new IOException($"Erro TFTP {code}: {message}");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Resíduo temporário: não impede a operação.
        }
    }

    private sealed class Session : IDisposable
    {
        private static readonly TimeSpan ReceiveSlice = TimeSpan.FromMilliseconds(250);

        private readonly UdpClient _udp;
        private readonly TftpClientOptions _options;
        private readonly IPEndPoint _server;
        private IPEndPoint? _peer;

        public Session(IPEndPoint server, TftpClientOptions options)
        {
            _server = server;
            _options = options;
            _udp = new UdpClient(server.AddressFamily);
        }

        public void Dispose() => _udp.Dispose();

        public void Send(byte[] packet) => _udp.Send(packet, packet.Length, _peer ?? _server);

        /// <summary>Como <see cref="Exchange"/>, mas um pacote de ERRO do servidor vira uma exceção.</summary>
        public byte[] Expect(byte[] packet, Func<byte[], bool> isExpected, CancellationToken cancellationToken)
        {
            var response = Exchange(packet, buffer => IsOpcode(buffer, OpError) || isExpected(buffer), cancellationToken);
            ThrowIfError(response);
            return response;
        }

        /// <summary>
        /// Envia o pacote e espera uma resposta aceite. Sem resposta dentro do tempo limite, retransmite;
        /// esgotadas as tentativas, falha. Pacotes de outra origem (TID) são ignorados.
        /// </summary>
        public byte[] Exchange(byte[] packet, Func<byte[], bool> accept, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Send(packet);

                var deadline = DateTime.UtcNow + _options.ResponseTimeout;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var received = Receive(remaining < ReceiveSlice ? remaining : ReceiveSlice);
                    if (received is not { } datagram || !IsExpectedSource(datagram.From))
                    {
                        continue;
                    }

                    if (accept(datagram.Buffer))
                    {
                        _peer ??= datagram.From;
                        return datagram.Buffer;
                    }
                }
            }

            throw new TimeoutException("O servidor TFTP não respondeu.");
        }

        // Antes da primeira resposta basta ser o mesmo servidor (a porta nova é o TID da transferência).
        private bool IsExpectedSource(IPEndPoint from) =>
            _peer is null ? from.Address.Equals(_server.Address) : from.Equals(_peer);

        private (byte[] Buffer, IPEndPoint From)? Receive(TimeSpan slice)
        {
            _udp.Client.ReceiveTimeout = Math.Max(1, (int)slice.TotalMilliseconds);
            var from = new IPEndPoint(
                _server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0);

            try
            {
                var buffer = _udp.Receive(ref from);
                return (buffer, from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "porta inacessível": não há nada a escutar nesse endereço.
                throw new IOException($"Não há servidor TFTP a escutar em {_server}.", ex);
            }
        }
    }
}
