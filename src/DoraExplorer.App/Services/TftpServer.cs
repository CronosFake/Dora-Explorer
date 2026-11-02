using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Servidor TFTP: RFC 1350 (RRQ/WRQ, octet), RFC 2347/2348/2349 (opções blksize, timeout e tsize) e
/// retransmissão por tempo limite. Como o TFTP não tem login, a gravação pode ser desligada (somente leitura).
/// </summary>
internal sealed class TftpServer : ProtocolServerBase
{
    private const int MaxConcurrentTransfers = 8;
    private const long MaxUploadBytes = 256L * 1024 * 1024;
    private const int DefaultBlockSize = 512;
    private const int MinBlockSize = 8;
    private const int MaxBlockSize = 65464;
    private const int MaxRetries = 5;
    private const string TempMarker = ".dora-part-";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private const byte OpRead = 1;
    private const byte OpWrite = 2;
    private const byte OpData = 3;
    private const byte OpAck = 4;
    private const byte OpError = 5;
    private const byte OpOptionAck = 6;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private int _activeTransfers;

    public TftpServer(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs)
        : base(config, rootPath, logs)
    {
    }

    public override void Start()
    {
        var address = IPAddress.Parse(Config.Host);
        _udp = new UdpClient(new IPEndPoint(address, Config.Port));
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Log($"TFTP a escutar em {Config.Host}:{Config.Port}" + (ReadOnly ? " (somente leitura)" : string.Empty));
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
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
                _udp?.Close();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // ignored
            }
        }

        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udp is { } udp)
        {
            try
            {
                var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _activeTransfers) > MaxConcurrentTransfers)
                {
                    Interlocked.Decrement(ref _activeTransfers);
                    Log($"Pedido de {result.RemoteEndPoint} recusado: limite de {MaxConcurrentTransfers} transferências simultâneas.");
                    await RejectAsync(result.RemoteEndPoint, 0, "Server busy.").ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await HandleRequestAsync(result.Buffer, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activeTransfers);
                        }
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "porta inacessível" de um cliente que já foi embora: não afeta o servidor.
            }
            catch (Exception ex)
            {
                Log($"ERRO receive: {ex.Message}");
                AppLogService.Error("TFTP receive falhou", ex);
            }
        }
    }

    private async Task HandleRequestAsync(byte[] buffer, IPEndPoint remote, CancellationToken cancellationToken)
    {
        try
        {
            if (buffer.Length < 2 || buffer[0] != 0 || buffer[1] is not (OpRead or OpWrite))
            {
                return; // só pedidos de leitura/escrita abrem uma transferência
            }

            if (!TryParseRequest(buffer, out var request))
            {
                Log($"Pedido malformado de {remote}");
                await RejectAsync(remote, 4, "Illegal TFTP operation.").ConfigureAwait(false);
                return;
            }

            if (!request.Mode.Equals("octet", StringComparison.OrdinalIgnoreCase) &&
                !request.Mode.Equals("netascii", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Pedido de {remote}: modo '{request.Mode}' não suportado");
                await RejectAsync(remote, 4, "Illegal TFTP operation.").ConfigureAwait(false);
                return;
            }

            if (request.IsRead)
            {
                await ServeReadAsync(request, remote, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ServeWriteAsync(request, remote, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // servidor parado
        }
        catch (Exception ex)
        {
            Log($"ERRO pedido TFTP de {remote}: {ex.Message}");
            AppLogService.Error("TFTP pedido falhou", ex);
        }
    }

    // ---- Leitura (RRQ) -----------------------------------------------------------------------

    private async Task ServeReadAsync(TftpRequest request, IPEndPoint remote, CancellationToken cancellationToken)
    {
        using var socket = CreateTransferSocket();

        if (!TryResolvePath(request.FileName, out var path) || !File.Exists(path))
        {
            Log($"RRQ de {remote}: ficheiro não encontrado ({request.FileName})");
            await SendErrorAsync(socket, remote, 1, "File not found.").ConfigureAwait(false);
            return;
        }

        FileStream file;
        try
        {
            file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"RRQ de {remote}: não foi possível abrir {request.FileName} ({ex.Message})");
            await SendErrorAsync(socket, remote, 2, "Access violation.").ConfigureAwait(false);
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            var negotiated = Negotiate(request, tsizeForRead: file.Length);
            var channel = new TftpChannel(socket, remote, negotiated.Timeout);

            Log($"RRQ de {remote}: {request.FileName} ({file.Length} bytes, blocos de {negotiated.BlockSize})");

            if (negotiated.Accepted.Count > 0)
            {
                var reply = await channel.ExchangeAsync(BuildOptionAck(negotiated.Accepted), b => IsAck(b, 0), cancellationToken)
                    .ConfigureAwait(false);
                if (!ReplyIsUsable(reply, "RRQ", remote, request.FileName))
                {
                    return;
                }
            }

            var buffer = new byte[4 + negotiated.BlockSize];
            buffer[1] = OpData;
            ushort block = 1;
            while (true)
            {
                var read = await ReadBlockAsync(file, buffer.AsMemory(4, negotiated.BlockSize), cancellationToken).ConfigureAwait(false);
                buffer[2] = (byte)(block >> 8);
                buffer[3] = (byte)block;

                var wanted = block;
                var reply = await channel.ExchangeAsync(buffer.AsSpan(0, 4 + read).ToArray(), b => IsAck(b, wanted), cancellationToken)
                    .ConfigureAwait(false);
                if (!ReplyIsUsable(reply, "RRQ", remote, request.FileName))
                {
                    return;
                }

                if (read < negotiated.BlockSize)
                {
                    break; // bloco curto (ou vazio): fim do ficheiro
                }

                block = unchecked((ushort)(block + 1)); // o contador reinicia depois de 65535, como nos servidores comuns
            }

            Log($"RRQ de {remote}: {request.FileName} enviado");
        }
    }

    // ---- Escrita (WRQ) -----------------------------------------------------------------------

    private async Task ServeWriteAsync(TftpRequest request, IPEndPoint remote, CancellationToken cancellationToken)
    {
        using var socket = CreateTransferSocket();

        if (ReadOnly)
        {
            Log($"WRQ de {remote}: recusado, servidor em modo somente leitura ({request.FileName})");
            await SendErrorAsync(socket, remote, 2, "Access violation: server is read-only.").ConfigureAwait(false);
            return;
        }

        if (!TryResolvePath(request.FileName, out var path) || Directory.Exists(path))
        {
            Log($"WRQ de {remote}: acesso negado ({request.FileName})");
            await SendErrorAsync(socket, remote, 2, "Access violation.").ConfigureAwait(false);
            return;
        }

        if (request.Options.TryGetValue("tsize", out var declared) &&
            long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredSize) &&
            declaredSize > MaxUploadBytes)
        {
            Log($"WRQ de {remote}: {request.FileName} declara {declaredSize} bytes, acima do limite de {MaxUploadBytes / (1024 * 1024)} MB");
            await SendErrorAsync(socket, remote, 3, "Disk full or allocation exceeded.").ConfigureAwait(false);
            return;
        }

        var negotiated = Negotiate(request, tsizeForRead: null);
        var channel = new TftpChannel(socket, remote, negotiated.Timeout);
        Log($"WRQ de {remote}: {request.FileName} (blocos de {negotiated.BlockSize})");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Recebe num ficheiro temporário e só substitui o destino quando a transferência termina:
        // um pedido abortado ou malicioso não apaga o ficheiro existente.
        var tempPath = path + TempMarker + Guid.NewGuid().ToString("N")[..8];
        var completed = false;
        var finalAck = BuildAck(0);
        try
        {
            long totalBytes = 0;
            var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            try
            {
                var pending = negotiated.Accepted.Count > 0 ? BuildOptionAck(negotiated.Accepted) : BuildAck(0);
                ushort expected = 1;
                while (true)
                {
                    var wanted = expected;
                    var previous = unchecked((ushort)(expected - 1));

                    // Bloco repetido (o ACK anterior se perdeu): reenvia o ACK na hora, sem gravar de novo.
                    var data = await channel.ExchangeAsync(
                        pending,
                        b => IsData(b, wanted),
                        cancellationToken,
                        isRepeat: expected > 1 ? b => IsData(b, previous) : null).ConfigureAwait(false);
                    if (data is null)
                    {
                        Log($"WRQ de {remote}: {request.FileName} sem resposta do cliente — cancelado");
                        return;
                    }

                    if (IsError(data))
                    {
                        Log($"WRQ de {remote}: {request.FileName} cancelado pelo cliente ({DescribeError(data)})");
                        return;
                    }

                    var payload = data.Length - 4;
                    if (payload > negotiated.BlockSize)
                    {
                        await SendErrorAsync(socket, remote, 4, "Illegal TFTP operation.").ConfigureAwait(false);
                        return;
                    }

                    totalBytes += payload;
                    if (totalBytes > MaxUploadBytes)
                    {
                        Log($"WRQ de {remote}: {request.FileName} excede o limite de {MaxUploadBytes / (1024 * 1024)} MB — cancelado");
                        await SendErrorAsync(socket, remote, 3, "Disk full or allocation exceeded.").ConfigureAwait(false);
                        return;
                    }

                    if (payload > 0)
                    {
                        await file.WriteAsync(data.AsMemory(4, payload), cancellationToken).ConfigureAwait(false);
                    }

                    pending = BuildAck(expected);
                    if (payload < negotiated.BlockSize)
                    {
                        finalAck = pending;
                        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    expected = unchecked((ushort)(expected + 1));
                }
            }
            finally
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            // Só confirma o último bloco depois de o ficheiro estar no lugar: o cliente não vê "sucesso" em falso.
            File.Move(tempPath, path, overwrite: true);
            completed = true;
            await channel.SendAsync(finalAck).ConfigureAwait(false);
            Log($"WRQ de {remote}: {request.FileName} recebido ({totalBytes} bytes)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"WRQ de {remote}: falha ao gravar {request.FileName} ({ex.Message})");
            await SendErrorAsync(socket, remote, 3, "Disk full or allocation exceeded.").ConfigureAwait(false);
        }
        finally
        {
            if (!completed)
            {
                DeleteQuietly(tempPath);
            }
        }
    }

    // ---- Negociação de opções (RFC 2347/2348/2349) -------------------------------------------

    private static Negotiated Negotiate(TftpRequest request, long? tsizeForRead)
    {
        var blockSize = DefaultBlockSize;
        var timeout = DefaultTimeout;
        var accepted = new List<(string Name, string Value)>();

        if (request.Options.TryGetValue("blksize", out var blk) &&
            int.TryParse(blk, NumberStyles.None, CultureInfo.InvariantCulture, out var requested) &&
            requested >= MinBlockSize)
        {
            blockSize = Math.Min(requested, MaxBlockSize);
            accepted.Add(("blksize", blockSize.ToString(CultureInfo.InvariantCulture)));
        }

        if (request.Options.TryGetValue("timeout", out var tmo) &&
            int.TryParse(tmo, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 1 and <= 255)
        {
            timeout = TimeSpan.FromSeconds(seconds);
            accepted.Add(("timeout", seconds.ToString(CultureInfo.InvariantCulture)));
        }

        if (request.Options.TryGetValue("tsize", out var tsize))
        {
            // Leitura: o servidor informa o tamanho do ficheiro. Escrita: confirma o tamanho declarado.
            accepted.Add(("tsize", tsizeForRead?.ToString(CultureInfo.InvariantCulture) ?? tsize));
        }

        // windowsize (RFC 7440) e opções desconhecidas não são aceitas: o cliente usa o padrão (1 bloco por vez).
        return new Negotiated(blockSize, timeout, accepted);
    }

    private sealed record Negotiated(int BlockSize, TimeSpan Timeout, List<(string Name, string Value)> Accepted);

    // ---- Pacotes -----------------------------------------------------------------------------

    private sealed record TftpRequest(bool IsRead, string FileName, string Mode, Dictionary<string, string> Options);

    private static bool TryParseRequest(byte[] buffer, out TftpRequest request)
    {
        request = null!;

        var fields = new List<string>();
        var start = 2;
        for (var i = 2; i < buffer.Length; i++)
        {
            if (buffer[i] != 0)
            {
                continue;
            }

            fields.Add(Encoding.UTF8.GetString(buffer, start, i - start));
            start = i + 1;
        }

        // Todos os campos terminam em NUL; depois do modo vêm pares nome/valor.
        if (start != buffer.Length || fields.Count < 2 || fields.Count % 2 != 0 || fields[0].Length == 0)
        {
            return false;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < fields.Count; i += 2)
        {
            options[fields[i]] = fields[i + 1];
        }

        request = new TftpRequest(buffer[1] == OpRead, fields[0], fields[1], options);
        return true;
    }

    private static byte[] BuildAck(ushort block) => [0, OpAck, (byte)(block >> 8), (byte)block];

    private static byte[] BuildOptionAck(IEnumerable<(string Name, string Value)> options)
    {
        var bytes = new List<byte> { 0, OpOptionAck };
        foreach (var (name, value) in options)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(name));
            bytes.Add(0);
            bytes.AddRange(Encoding.ASCII.GetBytes(value));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static bool IsAck(byte[] buffer, ushort block) =>
        buffer.Length >= 4 && buffer[0] == 0 && buffer[1] == OpAck && ((buffer[2] << 8) | buffer[3]) == block;

    private static bool IsData(byte[] buffer, ushort block) =>
        buffer.Length >= 4 && buffer[0] == 0 && buffer[1] == OpData && ((buffer[2] << 8) | buffer[3]) == block;

    private static bool IsError(byte[] buffer) =>
        buffer.Length >= 4 && buffer[0] == 0 && buffer[1] == OpError;

    private static string DescribeError(byte[] buffer)
    {
        var code = (buffer[2] << 8) | buffer[3];
        var end = Array.IndexOf(buffer, (byte)0, 4);
        var message = Encoding.ASCII.GetString(buffer, 4, (end < 0 ? buffer.Length : end) - 4);
        return $"erro {code}: {message}";
    }

    /// <summary>Resposta nula é tempo esgotado; erro do cliente encerra a transferência.</summary>
    private bool ReplyIsUsable(byte[]? reply, string kind, IPEndPoint remote, string fileName)
    {
        if (reply is null)
        {
            Log($"{kind} de {remote}: {fileName} sem resposta do cliente — cancelado");
            return false;
        }

        if (IsError(reply))
        {
            Log($"{kind} de {remote}: {fileName} cancelado pelo cliente ({DescribeError(reply)})");
            return false;
        }

        return true;
    }

    private static async Task SendErrorAsync(UdpClient socket, IPEndPoint remote, ushort code, string message)
    {
        var text = Encoding.ASCII.GetBytes(message);
        var packet = new byte[5 + text.Length];
        packet[1] = OpError;
        packet[2] = (byte)(code >> 8);
        packet[3] = (byte)code;
        text.CopyTo(packet, 4);

        try
        {
            await socket.SendAsync(packet, packet.Length, remote).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // O cliente pode já ter ido embora.
        }
    }

    /// <summary>Recusa um pedido a partir de um socket novo (o TID da resposta), sem depender do estado do servidor.</summary>
    private async Task RejectAsync(IPEndPoint remote, ushort code, string message)
    {
        try
        {
            using var socket = CreateTransferSocket();
            await SendErrorAsync(socket, remote, code, message).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Sem resposta possível: o pedido já foi registrado no log.
        }
    }

    private static async Task<int> ReadBlockAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O temporário será ignorado; não impede novas transferências.
        }
    }

    // ---- Caminhos e sockets ------------------------------------------------------------------

    private bool TryResolvePath(string fileName, out string path)
    {
        path = string.Empty;

        // ':' só aparece em streams alternativos NTFS ou letras de unidade; o marcador evita expor temporários.
        if (fileName.Contains(':') || fileName.Contains(TempMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // Clientes TFTP costumam enviar "/config.txt": o caminho é sempre relativo à raiz.
            var relative = fileName
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(RootPath, relative));
            if (!ServerPathGuard.IsInside(RootPath, candidate, allowRoot: false))
            {
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }
    }

    /// <summary>Socket de dados da transferência, ligado ao mesmo IP em que o servidor escuta.</summary>
    private UdpClient CreateTransferSocket() =>
        new(new IPEndPoint(IPAddress.Parse(Config.Host), 0));

    /// <summary>
    /// Canal de uma transferência: envia um pacote e espera a resposta certa, retransmitindo a cada
    /// tempo limite. Pacotes de outra origem recebem erro 5 e não afetam a transferência.
    /// </summary>
    private sealed class TftpChannel(UdpClient socket, IPEndPoint peer, TimeSpan timeout)
    {
        public Task<int> SendAsync(byte[] packet) => socket.SendAsync(packet, packet.Length, peer);

        public async Task<byte[]?> ExchangeAsync(
            byte[] packet,
            Func<byte[], bool> accept,
            CancellationToken cancellationToken,
            Func<byte[], bool>? isRepeat = null)
        {
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                await SendAsync(packet).ConfigureAwait(false);

                var deadline = DateTime.UtcNow + timeout;
                while (true)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break; // tempo esgotado: retransmite
                    }

                    var received = await ReceiveAsync(remaining, cancellationToken).ConfigureAwait(false);
                    if (received is not { } datagram)
                    {
                        break;
                    }

                    if (!datagram.RemoteEndPoint.Equals(peer))
                    {
                        await SendErrorAsync(socket, datagram.RemoteEndPoint, 5, "Unknown transfer ID.").ConfigureAwait(false);
                        continue;
                    }

                    if (IsError(datagram.Buffer) || accept(datagram.Buffer))
                    {
                        return datagram.Buffer;
                    }

                    if (isRepeat?.Invoke(datagram.Buffer) == true)
                    {
                        await SendAsync(packet).ConfigureAwait(false);
                        continue;
                    }

                    // ACK repetido ou fora de ordem: ignora (retransmitir aqui duplicaria pacotes).
                }
            }

            return null;
        }

        private async Task<UdpReceiveResult?> ReceiveAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(wait);

            try
            {
                return await socket.ReceiveAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "porta inacessível": o cliente desapareceu — deixa as retransmissões esgotarem.
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
    }
}
