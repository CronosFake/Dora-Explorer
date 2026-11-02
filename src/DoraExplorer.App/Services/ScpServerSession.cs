using System.Globalization;
using System.IO;
using System.Text;

namespace DoraExplorer.App.Services;

/// <summary>Comando "scp -t|-f [-r] [-d] [-p] caminho" que o cliente manda ao abrir o canal.</summary>
internal sealed record ScpCommand(bool Sink, bool Recursive, bool TargetMustBeDirectory, bool PreserveTimes, string Path)
{
    /// <summary>Lê o comando; falso para qualquer coisa que não seja o scp (o servidor não executa programas).</summary>
    public static bool TryParse(string? commandLine, out ScpCommand command)
    {
        command = null!;
        var tokens = Tokenize(commandLine);
        if (tokens.Count < 3 || tokens[0] != "scp")
        {
            return false;
        }

        bool sink = false, source = false, recursive = false, directory = false, times = false;
        string? path = null;
        var optionsEnded = false;
        foreach (var token in tokens.Skip(1))
        {
            if (!optionsEnded && token == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && token.Length > 1 && token[0] == '-')
            {
                foreach (var flag in token.AsSpan(1))
                {
                    switch (flag)
                    {
                        case 't': sink = true; break;
                        case 'f': source = true; break;
                        case 'r': recursive = true; break;
                        case 'd': directory = true; break;
                        case 'p': times = true; break;
                        case 'v' or 'q' or 'C': break; // detalhado, silencioso, compressão: sem efeito aqui
                        default: return false;
                    }
                }

                continue;
            }

            if (path is not null)
            {
                return false; // vários caminhos: o scp só manda um para cada lado
            }

            path = token;
        }

        if (sink == source || path is null)
        {
            return false;
        }

        command = new ScpCommand(sink, recursive, directory, times, path);
        return true;
    }

    /// <summary>Divide como um shell: aspas simples, aspas duplas e barra invertida (é assim que o "scp" do OpenSSH protege o caminho).</summary>
    private static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var started = false;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                started = true;
                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (started || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started || current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>
/// Servidor do protocolo SCP (o "RCP sobre SSH" que o <c>scp</c> do OpenSSH e o WinSCP falam) sobre um canal já aberto.
/// Recebe (<c>scp -t</c>) e envia (<c>scp -f</c>) arquivos e pastas dentro de <paramref name="rootPath"/>; nomes vindos do
/// cliente nunca são confiados (ver <see cref="ServerVirtualPath"/>), e um envio interrompido não deixa arquivo pela metade.
/// </summary>
internal sealed class ScpServerSession(Stream channel, string rootPath, bool readOnly, string peer, Action<string> log)
{
    private const int MaxLineLength = 4096;
    private const int MaxDepth = 64;
    private const int ChunkSize = 64 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private sealed class ScpProtocolException(string message) : Exception(message);

    /// <summary>Atende o comando até o fim. Devolve o código de saída (0 = tudo certo).</summary>
    public async Task<int> RunAsync(ScpCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (command.Sink)
            {
                await SinkAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SourceAsync(command, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (ScpProtocolException ex)
        {
            log($"{peer}: SCP recusado: {ex.Message}");
            await TrySendErrorAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            log($"{peer}: SCP sem permissão: {ex.Message}");
            await TrySendErrorAsync("Permission denied").ConfigureAwait(false);
            return 1;
        }
        catch (IOException ex)
        {
            log($"{peer}: SCP interrompido: {ex.Message}");
            await TrySendErrorAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    // ---- Receber (scp -t) ----------------------------------------------------------------------------

    private async Task SinkAsync(ScpCommand command, CancellationToken cancellationToken)
    {
        if (!ServerVirtualPath.TryResolve(rootPath, command.Path, out var targetReal, out var targetVirtual))
        {
            throw new ScpProtocolException("Permission denied");
        }

        if (readOnly)
        {
            throw new ScpProtocolException("Read-only server");
        }

        var targetIsDirectory = Directory.Exists(targetReal);
        if (command.TargetMustBeDirectory && !targetIsDirectory)
        {
            throw new ScpProtocolException($"{command.Path}: Not a directory");
        }

        await AckAsync(cancellationToken).ConfigureAwait(false);

        var baseDirectory = targetIsDirectory ? targetVirtual : null;
        var stack = new Stack<(string Path, DateTime? Time)>();
        DateTime? pendingTime = null;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return; // o cliente terminou
            }

            if (line.Length == 0)
            {
                continue;
            }

            var current = stack.Count > 0 ? stack.Peek().Path : baseDirectory;
            switch (line[0])
            {
                case 'T':
                    pendingTime = ParseTime(line);
                    await AckAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case 'C':
                    await ReceiveFileAsync(line, current, targetVirtual, pendingTime, cancellationToken).ConfigureAwait(false);
                    pendingTime = null;
                    break;
                case 'D':
                    if (stack.Count >= MaxDepth)
                    {
                        throw new ScpProtocolException("Directories nested too deeply");
                    }

                    stack.Push((CreateDirectory(line, current, targetVirtual), pendingTime));
                    pendingTime = null;
                    await AckAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case 'E':
                    if (stack.Count == 0)
                    {
                        // Como o scp do OpenSSH: um "E" sem pasta aberta encerra a transferência (o SSH.NET manda um ao final).
                        await AckAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    ApplyDirectoryTime(stack.Pop());
                    await AckAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case '\u0001' or '\u0002':
                    log($"{peer}: aviso do cliente SCP: {line[1..]}");
                    break;
                default:
                    throw new ScpProtocolException("Protocol error");
            }
        }
    }

    private async Task ReceiveFileAsync(string header, string? currentDirectory, string targetVirtual, DateTime? time, CancellationToken cancellationToken)
    {
        var (size, name) = ParseHeader(header);

        string fileVirtual;
        if (currentDirectory is null)
        {
            fileVirtual = targetVirtual; // "scp arquivo destino": o nome vem do destino, não do cliente
        }
        else
        {
            fileVirtual = ChildOf(currentDirectory, name);
        }

        if (!ServerVirtualPath.TryResolve(rootPath, fileVirtual, out var real, out fileVirtual))
        {
            throw new ScpProtocolException("Permission denied");
        }

        if (Directory.Exists(real))
        {
            throw new ScpProtocolException($"{fileVirtual}: Is a directory");
        }

        var parent = Path.GetDirectoryName(real);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new ScpProtocolException($"{fileVirtual}: No such file or directory");
        }

        await AckAsync(cancellationToken).ConfigureAwait(false);

        // Grava num arquivo temporário e só troca pelo nome final ao terminar: uma queda no meio não deixa
        // um arquivo pela metade nem estraga a versão que já existia.
        var temp = Path.Combine(parent, $".dora-part-{Guid.NewGuid():N}");
        var completed = false;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.None))
            {
                var buffer = new byte[ChunkSize];
                var remaining = size;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(remaining, buffer.Length);
                    await ReadExactlyAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    remaining -= want;
                }
            }

            // Depois dos dados o cliente manda um byte de status (0 = ok).
            var status = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (status != 0)
            {
                throw new ScpProtocolException("Client reported a transfer error");
            }

            File.Move(temp, real, overwrite: true);
            completed = true;
            if (time is { } modified)
            {
                File.SetLastWriteTimeUtc(real, modified);
            }

            log($"{peer} gravou {fileVirtual} ({size} bytes)");
            await AckAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!completed)
            {
                TryDelete(temp);
            }
        }
    }

    private string CreateDirectory(string header, string? currentDirectory, string targetVirtual)
    {
        var (_, name) = ParseHeader(header);

        string directoryVirtual;
        if (currentDirectory is null)
        {
            directoryVirtual = targetVirtual; // "scp -r pasta destino" com destino novo: o destino vira a pasta
        }
        else
        {
            directoryVirtual = ChildOf(currentDirectory, name);
        }

        if (!ServerVirtualPath.TryResolve(rootPath, directoryVirtual, out var real, out directoryVirtual))
        {
            throw new ScpProtocolException("Permission denied");
        }

        if (File.Exists(real))
        {
            throw new ScpProtocolException($"{directoryVirtual}: Not a directory");
        }

        _ = Directory.CreateDirectory(real);
        log($"{peer} criou a pasta {directoryVirtual}");
        return directoryVirtual;
    }

    /// <summary>A hora da pasta só vale depois que o último arquivo entrou (criar arquivos dentro dela a altera).</summary>
    private void ApplyDirectoryTime((string Path, DateTime? Time) directory)
    {
        if (directory.Time is { } modified && ServerVirtualPath.TryResolve(rootPath, directory.Path, out var real, out _))
        {
            Directory.SetLastWriteTimeUtc(real, modified);
        }
    }

    private static string ChildOf(string parentVirtual, string name)
    {
        if (!ServerVirtualPath.IsSafeSegment(name))
        {
            throw new ScpProtocolException($"{name}: Invalid file name");
        }

        return parentVirtual.TrimEnd('/') + "/" + name;
    }

    /// <summary>"C0644 123 nome" ou "D0755 0 nome" → (tamanho, nome). O nome não pode ter barras: é só um nome.</summary>
    private static (long Size, string Name) ParseHeader(string header)
    {
        var parts = header.Split(' ', 3);
        if (parts.Length != 3 || parts[0].Length < 5 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            parts[2].Length is 0 or > 255 || parts[2].Contains('/') || parts[2].Contains('\\'))
        {
            throw new ScpProtocolException("Protocol error");
        }

        return (size, parts[2]);
    }

    private static DateTime? ParseTime(string line)
    {
        var parts = line[1..].Split(' ');
        return parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
               seconds is >= 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    // ---- Enviar (scp -f) ------------------------------------------------------------------------------

    private async Task SourceAsync(ScpCommand command, CancellationToken cancellationToken)
    {
        // O cliente começa com um "ok"; só então o servidor manda os arquivos.
        if (!await ReadAckAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var entries = ResolveSources(command);
        if (entries.Count == 0)
        {
            throw new ScpProtocolException($"{command.Path}: No such file or directory");
        }

        foreach (var (real, virtualPath) in entries)
        {
            await SendEntryAsync(real, virtualPath, command, depth: 0, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<(string Real, string Virtual)> ResolveSources(ScpCommand command)
    {
        var result = new List<(string, string)>();
        var normalized = ServerVirtualPath.Normalize(command.Path);
        var lastSeparator = normalized.LastIndexOf('/');
        var leaf = normalized[(lastSeparator + 1)..];

        // Curingas só no último nome ("pasta/*.txt"), como o shell do servidor expandiria.
        if (leaf.IndexOfAny(['*', '?']) >= 0)
        {
            var parentVirtual = lastSeparator <= 0 ? "/" : normalized[..lastSeparator];
            if (!ServerVirtualPath.TryResolve(rootPath, parentVirtual, out var parentReal, out parentVirtual))
            {
                throw new ScpProtocolException("Permission denied");
            }

            if (!Directory.Exists(parentReal))
            {
                return result;
            }

            foreach (var info in new DirectoryInfo(parentReal)
                         .EnumerateFileSystemInfos(leaf, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ServerVirtualPath.IsSafeSegment(info.Name))
                {
                    result.Add((info.FullName, parentVirtual.TrimEnd('/') + "/" + info.Name));
                }
            }

            return result;
        }

        if (!ServerVirtualPath.TryResolve(rootPath, command.Path, out var real, out var virtualPath))
        {
            throw new ScpProtocolException("Permission denied");
        }

        if (File.Exists(real) || Directory.Exists(real))
        {
            result.Add((real, virtualPath));
        }

        return result;
    }

    private async Task SendEntryAsync(string real, string virtualPath, ScpCommand command, int depth, CancellationToken cancellationToken)
    {
        if (Directory.Exists(real))
        {
            if (!command.Recursive)
            {
                throw new ScpProtocolException($"{virtualPath}: not a regular file");
            }

            if (depth >= MaxDepth)
            {
                throw new ScpProtocolException("Directories nested too deeply");
            }

            var directory = new DirectoryInfo(real);
            if (command.PreserveTimes)
            {
                await SendTimeAsync(directory.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);
            }

            await SendLineAsync($"D0755 0 {NameOf(real, virtualPath)}", cancellationToken).ConfigureAwait(false);
            foreach (var child in directory
                         .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!ServerVirtualPath.IsSafeSegment(child.Name))
                {
                    continue;
                }

                var childVirtual = virtualPath.TrimEnd('/') + "/" + child.Name;
                if (!ServerVirtualPath.TryResolve(rootPath, childVirtual, out var childReal, out childVirtual))
                {
                    continue;
                }

                await SendEntryAsync(childReal, childVirtual, command, depth + 1, cancellationToken).ConfigureAwait(false);
            }

            await SendLineAsync("E", cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(real);
        if (command.PreserveTimes)
        {
            await SendTimeAsync(file.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);
        }

        var length = file.Length;
        await SendLineAsync($"C0644 {length.ToString(CultureInfo.InvariantCulture)} {NameOf(real, virtualPath)}", cancellationToken).ConfigureAwait(false);

        await using (var input = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan))
        {
            var buffer = new byte[ChunkSize];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // O arquivo encolheu no meio do envio: o protocolo já prometeu o tamanho, não dá para continuar.
                    throw new IOException($"{virtualPath}: file changed while being sent");
                }

                await channel.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        await channel.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await channel.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!await ReadAckAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ScpProtocolException("Client reported a transfer error");
        }

        log($"{peer} leu {virtualPath} ({length} bytes)");
    }

    private static string NameOf(string real, string virtualPath) =>
        virtualPath == "/" ? Path.GetFileName(real.TrimEnd(Path.DirectorySeparatorChar)) : virtualPath[(virtualPath.LastIndexOf('/') + 1)..];

    private async Task SendTimeAsync(DateTime modifiedUtc, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(0, new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
        await SendLineAsync(string.Create(CultureInfo.InvariantCulture, $"T{seconds} 0 {seconds} 0"), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        await channel.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
        await channel.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!await ReadAckAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ScpProtocolException("Client reported a transfer error");
        }
    }

    // ---- Bytes do protocolo ------------------------------------------------------------------------------

    private async Task AckAsync(CancellationToken cancellationToken)
    {
        await channel.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await channel.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Falso se o cliente respondeu com erro (1 = aviso, 2 = erro fatal; a mensagem vem na linha seguinte).</summary>
    private async Task<bool> ReadAckAsync(CancellationToken cancellationToken)
    {
        var status = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (status == 0)
        {
            return true;
        }

        var message = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        log($"{peer}: o cliente SCP recusou: {message}");
        return false;
    }

    private async Task TrySendErrorAsync(string message)
    {
        try
        {
            var text = message.Replace('\n', ' ');
            await channel.WriteAsync(Encoding.UTF8.GetBytes("\u0001" + text + "\n")).ConfigureAwait(false);
            await channel.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // O canal já caiu: não há a quem avisar.
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    /// <summary>Lê até o fim da linha. Nulo quando o cliente fechou o canal entre dois comandos.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                throw new ScpProtocolException("Protocol error");
            }

            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            bytes.Add(single[0]);
            if (bytes.Count > MaxLineLength)
            {
                throw new ScpProtocolException("Line too long");
            }
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (buffer.Length > 0)
        {
            var read = await ReadWithIdleTimeoutAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("O cliente fechou o canal no meio da transferência.");
            }

            buffer = buffer[read..];
        }
    }

    private async Task<int> ReadWithIdleTimeoutAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        try
        {
            return await channel.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Sessão SCP encerrada por inatividade.");
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
}
