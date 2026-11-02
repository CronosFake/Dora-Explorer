using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using IKeyPair = Microsoft.DevTunnels.Ssh.Algorithms.IKeyPair;

namespace DoraExplorer.App.Services;

/// <summary>Pedido "subsystem" (RFC 4254 §6.5): é assim que o cliente pede o SFTP.</summary>
internal sealed class SubsystemRequestMessage : ChannelRequestMessage
{
    public string? Subsystem { get; private set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Subsystem = reader.ReadString(Encoding.UTF8);
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Subsystem ?? string.Empty, Encoding.UTF8);
    }
}

/// <summary>
/// Servidor SFTP e SCP para compartilhar uma pasta, sobre o protocolo SSH 2 (biblioteca Microsoft.DevTunnels.Ssh: troca de
/// chaves, criptografia e autenticação). Um único usuário com senha, confinado à pasta raiz, com opção somente leitura.
/// Cada instância atende só o protocolo escolhido: SFTP (subsistema "sftp") ou SCP (comando "scp -t/-f"); nada mais roda.
/// </summary>
internal sealed class SshFileServer : ProtocolServerBase
{
    private const int MaxClients = 16;
    private const int MaxClientsPerAddress = 8;
    private const int MaxChannelsPerSession = 4;
    private const int MaxAuthAttempts = 3;
    private static readonly TimeSpan AuthenticationDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoChannelDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoginFailureDelay = TimeSpan.FromSeconds(1);

    private readonly string? _hostKeyPath;
    private readonly LoginThrottle _throttle = new(6, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    private readonly ConcurrentDictionary<int, ClientEntry> _clients = new();
    private readonly byte[] _expectedUser;
    private readonly byte[] _expectedPassword;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private IKeyPair? _hostKey;
    private int _clientSeq;

    private sealed record ClientEntry(TcpClient Client, IPAddress? Address);

    public SshFileServer(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs, string? hostKeyPath = null)
        : base(config, rootPath, logs)
    {
        _hostKeyPath = hostKeyPath;
        // Comparar resumos de tamanho fixo evita revelar, pelo tempo de resposta, quantos caracteres estão certos.
        _expectedUser = SHA256.HashData(Encoding.UTF8.GetBytes(config.Username ?? string.Empty));
        _expectedPassword = SHA256.HashData(Encoding.UTF8.GetBytes(config.Password ?? string.Empty));
    }

    private string Label => Config.Protocol == NetworkProtocol.Scp ? "SCP" : "SFTP";

    public override void Start()
    {
        _hostKey = SshHostKeyStore.GetOrCreate(_hostKeyPath);
        Log($"Impressão digital da chave do servidor: {SshHostKeyStore.Fingerprint(_hostKey)} (ecdsa-sha2-nistp384)");

        var address = IPAddress.Parse(Config.Host);
        _listener = new TcpListener(address, Config.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;

        Log($"{Label} a escutar em {Config.Host}:{Config.Port} (SSH 2{(ReadOnly ? ", somente leitura" : string.Empty)})");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
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

        foreach (var entry in _clients.Values)
        {
            try
            {
                entry.Client.Close();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // ignored
            }
        }

        _clients.Clear();

        if (closeListener)
        {
            try
            {
                _listener?.Stop();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // ignored
            }
        }

        _listener = null;
        _hostKey = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                var address = Normalize(remote?.Address);
                var key = address?.ToString() ?? string.Empty;

                if (address is not null && _throttle.IsBlocked(key))
                {
                    Log($"Ligação recusada de {remote}: endereço bloqueado por excesso de logins falhados.");
                    client.Close();
                    continue;
                }

                if (_clients.Count >= MaxClients || (address is not null && CountClients(address) >= MaxClientsPerAddress))
                {
                    Log($"Ligação recusada de {remote}: limite de clientes simultâneos.");
                    client.Close();
                    continue;
                }

                var id = Interlocked.Increment(ref _clientSeq);
                _clients[id] = new ClientEntry(client, address);
                Log($"Cliente #{id} ligado de {remote}");
                _ = Task.Run(() => RunSessionAsync(id, client, key, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Log($"Erro ao aceitar ligação: {ex.Message}");
            }
        }
    }

    private async Task RunSessionAsync(int id, TcpClient client, string address, CancellationToken serverToken)
    {
        var hostKey = _hostKey;
        if (hostKey is null)
        {
            client.Close();
            return;
        }

        var peer = $"#{id}";
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        SshServerSession? session = null;
        try
        {
            client.NoDelay = true;
            var configuration = CreateConfiguration();
            session = new SshServerSession(configuration, new TraceSource("DoraSsh"))
            {
                Credentials = new SshServerCredentials(hostKey),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            };

            var authenticated = false;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Authenticating += (_, e) => OnAuthenticating(e, address, peer);
            session.ClientAuthenticated += (_, _) =>
            {
                authenticated = true;
                Log($"{peer}: autenticado como {Config.Username}");
            };
            session.ChannelOpening += (_, e) => OnChannelOpening(session, e, peer, sessionCts.Token);
            session.Closed += (_, e) =>
            {
                // Cliente que simplesmente vai embora (ou se despede) é rotina; o resto vale registrar.
                if (e.Reason is not (SshDisconnectReason.None or SshDisconnectReason.ConnectionLost or SshDisconnectReason.ByApplication))
                {
                    Log($"{peer}: sessão encerrada ({e.Reason}){(e.Exception is null ? string.Empty : ": " + Sanitize(e.Exception.Message))}");
                }

                closed.TrySetResult();
            };

            await session.ConnectAsync(client.GetStream(), sessionCts.Token).ConfigureAwait(false);

            // Quem conecta e não se autentica, ou autentica e não abre nada, não pode ocupar uma vaga para sempre.
            _ = WatchdogAsync(session, () => authenticated, sessionCts.Token);
            using var stop = sessionCts.Token.Register(() => closed.TrySetResult());
            await closed.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Servidor parando.
        }
        catch (Exception ex) when (ex is SshConnectionException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Log($"{peer}: ligação encerrada: {Sanitize(ex.Message)}");
        }
        finally
        {
            session?.Dispose();
            client.Close();
            _ = _clients.TryRemove(id, out _);
            Log($"Cliente {peer} desligado");
        }
    }

    private static SshSessionConfiguration CreateConfiguration()
    {
        var configuration = new SshSessionConfiguration(useSecurity: true, enableCompression: false, enableReconnect: false);

        // Só senha: não há lista de chaves autorizadas para configurar, e "none" deixaria entrar sem login.
        configuration.AuthenticationMethods.Clear();
        configuration.AuthenticationMethods.Add(AuthenticationMethods.Password);
        configuration.MaxClientAuthenticationAttempts = MaxAuthAttempts;

        // A chave do servidor é sempre ECDSA P-384, e o modo CBC (frágil) fica de fora.
        configuration.PublicKeyAlgorithms.Clear();
        configuration.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp384);
        _ = configuration.EncryptionAlgorithms.Remove(SshAlgorithms.Encryption.Aes256Cbc);
        return configuration;
    }

    private void OnAuthenticating(SshAuthenticatingEventArgs e, string address, string peer)
    {
        if (e.AuthenticationType != SshAuthenticationType.ClientPassword)
        {
            return; // "none", chave pública etc.: recusado (o cliente é informado de que só há senha)
        }

        var userOk = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(e.Username ?? string.Empty)), _expectedUser);
        var passwordOk = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(e.Password ?? string.Empty)), _expectedPassword);
        if (userOk & passwordOk)
        {
            _throttle.RecordSuccess(address);
            e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity("password")));
            return;
        }

        Log($"{peer}: login recusado (usuário '{Sanitize(e.Username)}')");
        if (_throttle.RecordFailure(address))
        {
            Log($"Endereço {address} bloqueado por 10 minutos: logins falhados demais.");
        }

        // A pausa torna a tentativa de adivinhar senhas lenta mesmo antes do bloqueio.
        e.AuthenticationTask = DenyAfterDelayAsync();
    }

    private static async Task<ClaimsPrincipal?> DenyAfterDelayAsync()
    {
        await Task.Delay(LoginFailureDelay).ConfigureAwait(false);
        return null; // sem identidade = login recusado
    }

    private void OnChannelOpening(SshServerSession session, SshChannelOpeningEventArgs e, string peer, CancellationToken cancellationToken)
    {
        if (e.Request.ChannelType != SshChannel.SessionChannelType)
        {
            e.FailureReason = SshChannelOpenFailureReason.UnknownChannelType;
            e.FailureDescription = "Only session channels are supported";
            Log($"{peer}: canal '{e.Request.ChannelType}' recusado (encaminhamento de portas não é permitido).");
            return;
        }

        if (session.Channels.Count > MaxChannelsPerSession)
        {
            e.FailureReason = SshChannelOpenFailureReason.ResourceShortage;
            e.FailureDescription = "Too many channels";
            return;
        }

        e.Channel.Request += (_, request) => OnChannelRequest(e.Channel, request, peer, cancellationToken);
    }

    private void OnChannelRequest(SshChannel channel, SshRequestEventArgs<ChannelRequestMessage> e, string peer, CancellationToken cancellationToken)
    {
        switch (e.RequestType)
        {
            case "subsystem":
                var subsystem = e.Request.ConvertTo<SubsystemRequestMessage>().Subsystem;
                if (Config.Protocol != NetworkProtocol.Sftp || !string.Equals(subsystem, "sftp", StringComparison.Ordinal))
                {
                    Log($"{peer}: subsistema '{Sanitize(subsystem)}' recusado (este servidor atende só {Label}).");
                    Refuse(channel, e, closeChannel: true);
                    return;
                }

                e.IsAuthorized = true;
                _ = Task.Run(() => ServeSftpAsync(channel, peer, cancellationToken), cancellationToken);
                return;

            case ChannelRequestTypes.Command:
                var commandLine = e.Request.ConvertTo<CommandRequestMessage>().Command;
                if (Config.Protocol != NetworkProtocol.Scp || !ScpCommand.TryParse(commandLine, out var scp))
                {
                    Log($"{peer}: comando recusado '{Sanitize(commandLine)}' (este servidor atende só {Label}).");
                    Refuse(channel, e, closeChannel: true);
                    return;
                }

                e.IsAuthorized = true;
                _ = Task.Run(() => ServeScpAsync(channel, scp, peer, cancellationToken), cancellationToken);
                return;

            case "env":
                e.IsAuthorized = true; // variáveis de ambiente são aceitas e ignoradas (nada é executado)
                return;
        }

        // Terminal, shell, agente, X11...: este servidor só transfere arquivos.
        Log($"{peer}: pedido '{Sanitize(e.RequestType)}' recusado.");
        Refuse(channel, e, closeChannel: e.RequestType == ChannelRequestTypes.Shell);
    }

    /// <summary>
    /// Responde "não" ao pedido (CHANNEL_FAILURE). Quando o pedido era o que daria algo a fazer ao canal (comando, subsistema,
    /// shell), o canal é fechado em seguida: alguns clientes (como o SSH.NET) esperam o fim do canal em vez de olhar a recusa.
    /// </summary>
    private static void Refuse(SshChannel channel, SshRequestEventArgs<ChannelRequestMessage> e, bool closeChannel)
    {
        e.IsAuthorized = false;
        e.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
        if (closeChannel)
        {
            e.ResponseContinuation = _ => CloseChannelAsync(channel, 1);
        }
    }

    private async Task ServeSftpAsync(SshChannel channel, string peer, CancellationToken cancellationToken)
    {
        await using var stream = new SshStream(channel);
        try
        {
            var session = new SftpServerSession(stream, RootPath, ReadOnly, peer, Log);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseChannelAsync(channel, 0).ConfigureAwait(false);
        }
    }

    private async Task ServeScpAsync(SshChannel channel, ScpCommand command, string peer, CancellationToken cancellationToken)
    {
        uint exitStatus = 1;
        await using var stream = new SshStream(channel);
        try
        {
            var session = new ScpServerSession(stream, RootPath, ReadOnly, peer, Log);
            exitStatus = (uint)await session.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseChannelAsync(channel, exitStatus).ConfigureAwait(false);
        }
    }

    private static async Task CloseChannelAsync(SshChannel channel, uint exitStatus)
    {
        try
        {
            if (!channel.IsClosed)
            {
                await channel.CloseAsync(exitStatus, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SshConnectionException or SshChannelException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // O cliente já tinha ido embora.
        }
    }

    private static async Task WatchdogAsync(SshServerSession session, Func<bool> authenticated, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AuthenticationDeadline, cancellationToken).ConfigureAwait(false);
            if (!authenticated())
            {
                await session.CloseAsync(SshDisconnectReason.AuthCancelledByUser, "Authentication timed out").ConfigureAwait(false);
                return;
            }

            await Task.Delay(NoChannelDeadline, cancellationToken).ConfigureAwait(false);
            if (session.Channels.Count == 0)
            {
                await session.CloseAsync(SshDisconnectReason.ByApplication, "No channel opened").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SshConnectionException or ObjectDisposedException or InvalidOperationException)
        {
            // Sessão já encerrada ou servidor parando.
        }
    }

    private int CountClients(IPAddress address) => _clients.Values.Count(entry => Equals(entry.Address, address));

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>Texto vindo da rede vai para o log sem quebras de linha nem caracteres de controle (evita forjar linhas do log).</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = new string(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return clean.Length > 80 ? clean[..80] + "…" : clean;
    }
}
