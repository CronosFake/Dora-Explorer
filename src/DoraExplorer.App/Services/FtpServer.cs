using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Servidor FTP para compartilhar uma pasta: RFC 959 (incluindo apagar, criar pasta e renomear), RFC 2389 (FEAT/OPTS),
/// RFC 2428 (EPSV/EPRT), RFC 3659 (MLSD, MLST, SIZE, MDTM, REST), RFC 2640 (UTF-8) e FTPS explícito (RFC 4217).
/// Um único usuário, confinado à pasta raiz, com opção somente leitura.
/// </summary>
internal sealed class FtpServer : ProtocolServerBase
{
    private const int MaxClients = 16;
    private const int MaxClientsPerAddress = 8;
    private const int MaxFailedLoginsPerSession = 3;
    private const int MaxFailedLoginsPerAddress = 6;
    private const int MaxCommandLength = 4096;
    private const int MinActivePort = 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DataConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginFailureDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(10);
    private const SslProtocols TlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _certificate;
    private PassivePortRange _passiveRange;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<IPAddress, LoginFailures> _failures = new();
    private readonly string? _certificatePath;
    private int _clientSeq;

    public FtpServer(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs, string? certificatePath = null)
        : base(config, rootPath, logs)
    {
        _certificatePath = certificatePath;
    }

    private bool TlsOffered => _certificate is not null && Config.ServerSecurity != FtpServerSecurity.Disabled;

    private bool TlsRequired => TlsOffered && Config.ServerSecurity == FtpServerSecurity.Required;

    public override void Start()
    {
        if (!PassivePortRange.TryParse(Config.PassivePorts, out _passiveRange, out var rangeError))
        {
            throw new InvalidOperationException(rangeError);
        }

        if (Config.ServerSecurity != FtpServerSecurity.Disabled)
        {
            try
            {
                _certificate = FtpTlsCertificate.GetOrCreate(_certificatePath);
                Log($"FTPS ativo (impressão digital SHA-256 {_certificate.GetCertHashString(HashAlgorithmName.SHA256)})");
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                if (Config.ServerSecurity == FtpServerSecurity.Required)
                {
                    throw new InvalidOperationException($"Não foi possível preparar o certificado FTPS: {ex.Message}", ex);
                }

                Log($"AVISO: FTPS indisponível ({ex.Message}); apenas FTP simples.");
            }
        }

        var address = IPAddress.Parse(Config.Host);
        _listener = new TcpListener(address, Config.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;

        var mode = TlsRequired ? "FTPS obrigatório" : TlsOffered ? "FTPS opcional" : "FTP simples";
        Log($"FTP a escutar em {Config.Host}:{Config.Port} ({mode}{(ReadOnly ? ", somente leitura" : string.Empty)})");
        if (!TlsOffered)
        {
            Log("AVISO: sem criptografia, usuário e senha trafegam em texto puro na rede.");
        }

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

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Close();
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
        _certificate?.Dispose();
        _certificate = null;
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
                var address = NormalizeAddress(remote?.Address);

                if (address is not null && IsBlocked(address))
                {
                    Log($"Ligação recusada de {remote}: endereço bloqueado por excesso de logins falhados.");
                    await RejectAsync(client, "421 Too many failed logins; try again later.").ConfigureAwait(false);
                    continue;
                }

                if (_clients.Count >= MaxClients ||
                    (address is not null && CountClients(address) >= MaxClientsPerAddress))
                {
                    Log($"Ligação recusada de {remote}: limite de clientes simultâneos.");
                    await RejectAsync(client, "421 Too many connections.").ConfigureAwait(false);
                    continue;
                }

                var id = Interlocked.Increment(ref _clientSeq);
                _clients[id] = client;
                Log($"Cliente #{id} ligado de {remote}");
                _ = Task.Run(() => RunSessionAsync(id, client, address, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"ERRO accept: {ex.Message}");
                AppLogService.Error("FTP accept falhou", ex);
            }
        }
    }

    private int CountClients(IPAddress address) =>
        _clients.Values.Count(c => NormalizeAddress((c.Client.RemoteEndPoint as IPEndPoint)?.Address)?.Equals(address) == true);

    private static async Task RejectAsync(TcpClient client, string reply)
    {
        try
        {
            using (client)
            {
                var bytes = Encoding.ASCII.GetBytes(reply + "\r\n");
                await client.GetStream().WriteAsync(bytes).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // O cliente já foi embora.
        }
    }

    private async Task RunSessionAsync(int id, TcpClient client, IPAddress? peer, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                var session = new Session(this, id, client, peer, cancellationToken);
                await session.RunAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log($"Cliente #{id}: ligação encerrada (servidor parado)");
        }
        catch (Exception ex)
        {
            Log($"Cliente #{id}: sessão terminou com erro — {ex.Message}");
            AppLogService.Error("Sessão FTP terminou com erro", ex);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            Log($"Cliente #{id}: desligado");
        }
    }

    // ---- Bloqueio por tentativas falhas ------------------------------------------------------

    private sealed class LoginFailures
    {
        public int Count;
        public DateTime WindowStart;
        public DateTime BlockedUntil;
    }

    private bool IsBlocked(IPAddress address) =>
        _failures.TryGetValue(address, out var record) && record.BlockedUntil > DateTime.UtcNow;

    private void RegisterFailure(IPAddress? address)
    {
        if (address is null)
        {
            return;
        }

        var record = _failures.GetOrAdd(address, _ => new LoginFailures { WindowStart = DateTime.UtcNow });
        lock (record)
        {
            var now = DateTime.UtcNow;
            if (now - record.WindowStart > FailureWindow)
            {
                record.Count = 0;
                record.WindowStart = now;
            }

            record.Count++;
            if (record.Count >= MaxFailedLoginsPerAddress)
            {
                record.BlockedUntil = now + BlockDuration;
                record.Count = 0;
                Log($"Endereço {address} bloqueado por {BlockDuration.TotalMinutes:0} min: {MaxFailedLoginsPerAddress} logins falhados.");
            }
        }
    }

    private void ClearFailures(IPAddress? address)
    {
        if (address is not null)
        {
            _failures.TryRemove(address, out _);
        }
    }

    private static IPAddress? NormalizeAddress(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private bool IsAuthenticated(string username, string password)
    {
        // Sem credenciais configuradas o acesso é negado (TryStart já impede esse estado).
        if (string.IsNullOrWhiteSpace(Config.Username) || string.IsNullOrEmpty(Config.Password))
        {
            return false;
        }

        var userMatches = FixedTimeEquals(username, Config.Username);
        var passwordMatches = FixedTimeEquals(password, Config.Password);
        return userMatches & passwordMatches;
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private TcpListener OpenPassiveListener()
    {
        var address = IPAddress.Parse(Config.Host);
        if (_passiveRange.IsEmpty)
        {
            var any = new TcpListener(address, 0);
            any.Start();
            return any;
        }

        var span = _passiveRange.End - _passiveRange.Start + 1;
        var offset = Random.Shared.Next(span);
        for (var i = 0; i < span; i++)
        {
            var port = _passiveRange.Start + ((offset + i) % span);
            var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                // Porta ocupada por outra sessão ou programa: tenta a próxima da faixa.
            }
        }

        throw new IOException("Nenhuma porta passiva livre na faixa configurada.");
    }

    // ---- Sessão ------------------------------------------------------------------------------

    private sealed class Session
    {
        private readonly FtpServer _server;
        private readonly int _id;
        private readonly TcpClient _client;
        private readonly IPAddress? _peer;
        private readonly CancellationToken _ct;
        private Stream _control;

        private string _cwd = "/";
        private string _user = string.Empty;
        private bool _hasUser;
        private bool _authenticated;
        private bool _tls;
        private bool _protectData;
        private int _failedLogins;
        private long _restart;
        private string? _renameFrom;
        private TcpListener? _passive;
        private IPEndPoint? _active;

        public Session(FtpServer server, int id, TcpClient client, IPAddress? peer, CancellationToken ct)
        {
            _server = server;
            _id = id;
            _client = client;
            _peer = peer;
            _ct = ct;
            _control = client.GetStream();
        }

        private NetworkConnectionInfo Config => _server.Config;

        private bool ReadOnly => _server.ReadOnly;

        public async Task RunAsync()
        {
            try
            {
                await ReplyAsync(220, "Dora Explorer FTP ready.").ConfigureAwait(false);

                while (!_ct.IsCancellationRequested && _client.Connected)
                {
                    string? line;
                    try
                    {
                        line = await ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        _server.Log($"Cliente #{_id}: linha de comando demasiado longa — ligação encerrada");
                        await ReplyAsync(500, "Command line too long.").ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
                    {
                        _server.Log($"Cliente #{_id}: inatividade — ligação encerrada");
                        await ReplyAsync(421, "Timeout.").ConfigureAwait(false);
                        return;
                    }

                    if (line is null)
                    {
                        return;
                    }

                    var (command, argument) = Split(line);
                    if (command.Length == 0)
                    {
                        continue;
                    }

                    if (!await DispatchAsync(command, argument).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            finally
            {
                CloseDataChannel();
            }
        }

        /// <returns>Falso quando a sessão deve terminar.</returns>
        private async Task<bool> DispatchAsync(string command, string argument)
        {
            // REST e RNFR só valem para o comando seguinte.
            if (command is not ("REST" or "RETR" or "STOR" or "APPE"))
            {
                _restart = 0;
            }

            if (command is not "RNTO" && command is not "RNFR")
            {
                _renameFrom = null;
            }

            if (!_authenticated && RequiresLogin(command))
            {
                await ReplyAsync(530, "Please login with USER and PASS.").ConfigureAwait(false);
                return true;
            }

            try
            {
                switch (command)
                {
                    case "USER":
                        return await UserAsync(argument).ConfigureAwait(false);
                    case "PASS":
                        return await PassAsync(argument).ConfigureAwait(false);
                    case "QUIT":
                        _server.Log($"Cliente #{_id}: QUIT");
                        await ReplyAsync(221, "Goodbye.").ConfigureAwait(false);
                        return false;
                    case "NOOP":
                        await ReplyAsync(200, "OK.").ConfigureAwait(false);
                        return true;
                    case "SYST":
                        await ReplyAsync(215, "UNIX Type: L8").ConfigureAwait(false);
                        return true;
                    case "HELP":
                        await ReplyAsync(214, "Dora Explorer FTP: comandos padrão RFC 959/3659; use FEAT para a lista.").ConfigureAwait(false);
                        return true;
                    case "FEAT":
                        await FeatAsync().ConfigureAwait(false);
                        return true;
                    case "OPTS":
                        await OptsAsync(argument).ConfigureAwait(false);
                        return true;
                    case "AUTH":
                        return await AuthAsync(argument).ConfigureAwait(false);
                    case "PBSZ":
                        await ReplyAsync(_tls ? 200 : 503, _tls ? "PBSZ=0" : "Send AUTH TLS first.").ConfigureAwait(false);
                        return true;
                    case "PROT":
                        await ProtAsync(argument).ConfigureAwait(false);
                        return true;
                    case "TYPE":
                        var typeSupported = argument.Length > 0 && "AIL".Contains(char.ToUpperInvariant(argument[0]), StringComparison.Ordinal);
                        await ReplyAsync(typeSupported ? 200 : 504, typeSupported ? "Type set." : "Unsupported type.").ConfigureAwait(false);
                        return true;
                    case "MODE":
                        await ReplyAsync(argument.Equals("S", StringComparison.OrdinalIgnoreCase) ? 200 : 504, "Stream mode only.").ConfigureAwait(false);
                        return true;
                    case "STRU":
                        await ReplyAsync(argument.Equals("F", StringComparison.OrdinalIgnoreCase) ? 200 : 504, "File structure only.").ConfigureAwait(false);
                        return true;
                    case "ALLO":
                        await ReplyAsync(202, "No storage allocation necessary.").ConfigureAwait(false);
                        return true;
                    case "PWD":
                    case "XPWD":
                        await ReplyAsync(257, $"\"{_cwd.Replace("\"", "\"\"", StringComparison.Ordinal)}\" is the current directory.").ConfigureAwait(false);
                        return true;
                    case "CWD":
                    case "XCWD":
                        await CwdAsync(argument).ConfigureAwait(false);
                        return true;
                    case "CDUP":
                    case "XCUP":
                        await CwdAsync("..").ConfigureAwait(false);
                        return true;
                    case "PASV":
                        await PasvAsync(extended: false, argument).ConfigureAwait(false);
                        return true;
                    case "EPSV":
                        await PasvAsync(extended: true, argument).ConfigureAwait(false);
                        return true;
                    case "PORT":
                        await PortAsync(argument).ConfigureAwait(false);
                        return true;
                    case "EPRT":
                        await EprtAsync(argument).ConfigureAwait(false);
                        return true;
                    case "LIST":
                    case "NLST":
                    case "MLSD":
                        await ListAsync(command, argument).ConfigureAwait(false);
                        return true;
                    case "MLST":
                        await MlstAsync(argument).ConfigureAwait(false);
                        return true;
                    case "STAT":
                        await StatAsync(argument).ConfigureAwait(false);
                        return true;
                    case "SIZE":
                        await SizeAsync(argument).ConfigureAwait(false);
                        return true;
                    case "MDTM":
                        await MdtmAsync(argument).ConfigureAwait(false);
                        return true;
                    case "REST":
                        if (long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                        {
                            _restart = offset;
                            await ReplyAsync(350, $"Restarting at {offset}.").ConfigureAwait(false);
                        }
                        else
                        {
                            await ReplyAsync(501, "Invalid restart position.").ConfigureAwait(false);
                        }

                        return true;
                    case "RETR":
                        await RetrieveAsync(argument).ConfigureAwait(false);
                        return true;
                    case "STOR":
                        await StoreAsync(argument, append: false).ConfigureAwait(false);
                        return true;
                    case "APPE":
                        await StoreAsync(argument, append: true).ConfigureAwait(false);
                        return true;
                    case "DELE":
                        await DeleteFileAsync(argument).ConfigureAwait(false);
                        return true;
                    case "MKD":
                    case "XMKD":
                        await MakeDirectoryAsync(argument).ConfigureAwait(false);
                        return true;
                    case "RMD":
                    case "XRMD":
                        await RemoveDirectoryAsync(argument).ConfigureAwait(false);
                        return true;
                    case "RNFR":
                        await RenameFromAsync(argument).ConfigureAwait(false);
                        return true;
                    case "RNTO":
                        await RenameToAsync(argument).ConfigureAwait(false);
                        return true;
                    case "ABOR":
                        await ReplyAsync(226, "No transfer in progress.").ConfigureAwait(false);
                        return true;
                    default:
                        await ReplyAsync(502, "Command not implemented.").ConfigureAwait(false);
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _server.Log($"Cliente #{_id}: {command} falhou — {ex.Message}");
                await ReplyAsync(550, "Requested action not taken.").ConfigureAwait(false);
                return true;
            }
        }

        /// <summary>Comandos aceitos antes do login; todos os outros exigem sessão autenticada.</summary>
        private static bool RequiresLogin(string command) =>
            command is not ("USER" or "PASS" or "QUIT" or "NOOP" or "SYST" or "FEAT" or "OPTS" or "HELP"
                or "AUTH" or "PBSZ" or "PROT" or "TYPE" or "MODE" or "STRU");

        // ---- Login e criptografia ------------------------------------------------------------

        private async Task<bool> UserAsync(string argument)
        {
            if (_server.TlsRequired && !_tls)
            {
                await ReplyAsync(530, "SSL/TLS required: send AUTH TLS before USER.").ConfigureAwait(false);
                return true;
            }

            _user = argument;
            _hasUser = true;
            _authenticated = false;
            await ReplyAsync(331, "Password required.").ConfigureAwait(false);
            return true;
        }

        private async Task<bool> PassAsync(string argument)
        {
            if (_server.TlsRequired && !_tls)
            {
                await ReplyAsync(530, "SSL/TLS required: send AUTH TLS before PASS.").ConfigureAwait(false);
                return true;
            }

            if (!_hasUser)
            {
                await ReplyAsync(503, "Login with USER first.").ConfigureAwait(false);
                return true;
            }

            _authenticated = _server.IsAuthenticated(_user, argument);
            _server.Log(_authenticated
                ? $"Cliente #{_id}: login OK ({_user})"
                : $"Cliente #{_id}: login falhou ({_user})");

            if (_authenticated)
            {
                _server.ClearFailures(_peer);
                await ReplyAsync(230, "Login successful.").ConfigureAwait(false);
                return true;
            }

            // Atraso, limite por sessão e bloqueio por endereço dificultam adivinhar a senha por força bruta.
            _failedLogins++;
            _server.RegisterFailure(_peer);
            await Task.Delay(LoginFailureDelay, _ct).ConfigureAwait(false);
            await ReplyAsync(530, "Login incorrect.").ConfigureAwait(false);
            if (_failedLogins >= MaxFailedLoginsPerSession || (_peer is not null && _server.IsBlocked(_peer)))
            {
                _server.Log($"Cliente #{_id}: logins falhados — ligação encerrada");
                await ReplyAsync(421, "Too many failed logins.").ConfigureAwait(false);
                return false;
            }

            return true;
        }

        private async Task<bool> AuthAsync(string argument)
        {
            var mechanism = argument.ToUpperInvariant();
            if (mechanism is not ("TLS" or "TLS-C" or "SSL" or "TLS-P") || !_server.TlsOffered)
            {
                await ReplyAsync(504, "Security mechanism not available.").ConfigureAwait(false);
                return true;
            }

            if (_tls)
            {
                await ReplyAsync(503, "TLS already active.").ConfigureAwait(false);
                return true;
            }

            await ReplyAsync(234, "Proceed with negotiation.").ConfigureAwait(false);

            var ssl = new SslStream(_control, leaveInnerStreamOpen: false);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                limit.CancelAfter(DataConnectionTimeout);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _server._certificate,
                        EnabledSslProtocols = TlsProtocols,
                        ClientCertificateRequired = false
                    },
                    limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                _server.Log($"Cliente #{_id}: negociação TLS falhou — {ex.Message}");
                ssl.Dispose();
                return false;
            }

            _control = ssl;
            _tls = true;
            _server.Log($"Cliente #{_id}: canal de controle protegido ({ssl.SslProtocol})");
            return true;
        }

        private async Task ProtAsync(string argument)
        {
            if (!_tls)
            {
                await ReplyAsync(503, "Send AUTH TLS first.").ConfigureAwait(false);
                return;
            }

            switch (argument.ToUpperInvariant())
            {
                case "P":
                    _protectData = true;
                    await ReplyAsync(200, "Data connection will be protected.").ConfigureAwait(false);
                    break;
                case "C":
                    _protectData = false;
                    await ReplyAsync(200, "Data connection will be clear.").ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync(504, "Protection level not supported.").ConfigureAwait(false);
                    break;
            }
        }

        private async Task FeatAsync()
        {
            var lines = new List<string>
            {
                " UTF8", " MDTM", " SIZE", " REST STREAM", " EPSV", " TVFS", " MLST type*;size*;modify*;perm*;"
            };
            if (_server.TlsOffered)
            {
                lines.Insert(0, " AUTH TLS");
                lines.Insert(1, " PBSZ");
                lines.Insert(2, " PROT");
            }

            await WriteRawAsync("211-Features:\r\n" + string.Join("\r\n", lines) + "\r\n211 End\r\n").ConfigureAwait(false);
        }

        private async Task OptsAsync(string argument)
        {
            var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "UTF8":
                    await ReplyAsync(200, "UTF8 is always on.").ConfigureAwait(false);
                    break;
                case "MLST":
                    await ReplyAsync(200, "MLST OPTS type;size;modify;perm;").ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync(501, "Option not understood.").ConfigureAwait(false);
                    break;
            }
        }

        // ---- Navegação -----------------------------------------------------------------------

        private async Task CwdAsync(string argument)
        {
            var virtualPath = Resolve(argument);
            if (virtualPath is not null && ToLocal(virtualPath) is { } local && Directory.Exists(local))
            {
                _cwd = virtualPath;
                await ReplyAsync(250, "Directory changed.").ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(550, "Directory not found.").ConfigureAwait(false);
            }
        }

        private async Task SizeAsync(string argument)
        {
            if (Resolve(argument) is { } virtualPath && ToLocal(virtualPath) is { } local && File.Exists(local))
            {
                await ReplyAsync(213, new FileInfo(local).Length.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            }
        }

        private async Task MdtmAsync(string argument)
        {
            if (Resolve(argument) is { } virtualPath && ToLocal(virtualPath) is { } local &&
                (File.Exists(local) || Directory.Exists(local)))
            {
                var stamp = File.GetLastWriteTimeUtc(local).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                await ReplyAsync(213, stamp).ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            }
        }

        private async Task StatAsync(string argument)
        {
            if (argument.Length == 0)
            {
                await WriteRawAsync(
                    "211-Dora Explorer FTP status:\r\n" +
                    $" Logged in as {_user}\r\n" +
                    $" Control connection is {(_tls ? "protected with TLS" : "not encrypted")}\r\n" +
                    $" Server is {(ReadOnly ? "read-only" : "read/write")}\r\n" +
                    "211 End\r\n").ConfigureAwait(false);
                return;
            }

            await MlstAsync(argument).ConfigureAwait(false);
        }

        // ---- Listagens -----------------------------------------------------------------------

        private async Task ListAsync(string command, string argument)
        {
            // Clientes enviam "LIST -a", "LIST -al /pasta"; as opções são ignoradas.
            var target = StripListFlags(argument);
            var virtualPath = Resolve(target);
            var local = virtualPath is null ? null : ToLocal(virtualPath);
            var isDirectory = local is not null && Directory.Exists(local);
            var isFile = local is not null && File.Exists(local);

            if (local is null || (!isDirectory && !(isFile && command != "MLSD")))
            {
                _server.Log($"Cliente #{_id}: {command} caminho inválido ({argument})");
                await ReplyAsync(550, "Directory not found.").ConfigureAwait(false);
                return;
            }

            IEnumerable<FileSystemInfo> entries = isDirectory ? EnumerateEntries(local) : new FileSystemInfo[] { new FileInfo(local) };

            if (!await PrepareTransferAsync().ConfigureAwait(false))
            {
                return;
            }

            await ReplyAsync(150, "Opening data connection for the listing.").ConfigureAwait(false);
            var channel = await OpenDataChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                await ReplyAsync(425, "Can't open data connection.").ConfigureAwait(false);
                return;
            }

            try
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    builder.Append(command switch
                    {
                        "NLST" => isDirectory ? entry.Name : ToVirtualFromLocal(entry.FullName),
                        "MLSD" => FormatMlsx(entry),
                        _ => FormatUnix(entry)
                    }).Append("\r\n");
                }

                var bytes = Encoding.UTF8.GetBytes(builder.ToString());
                await channel.Stream.WriteAsync(bytes, _ct).ConfigureAwait(false);
                await channel.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
            {
                _server.Log($"Cliente #{_id}: {command} interrompido — {ex.Message}");
                await ReplyAsync(426, "Connection closed; transfer aborted.").ConfigureAwait(false);
                return;
            }
            finally
            {
                channel.Dispose();
            }

            _server.Log($"Cliente #{_id}: {command} {virtualPath!}");
            await ReplyAsync(226, "Transfer complete.").ConfigureAwait(false);
        }

        private async Task MlstAsync(string argument)
        {
            var virtualPath = Resolve(argument);
            var local = virtualPath is null ? null : ToLocal(virtualPath);
            FileSystemInfo? info = local is null
                ? null
                : Directory.Exists(local) ? new DirectoryInfo(local) : File.Exists(local) ? new FileInfo(local) : null;

            if (info is null)
            {
                await ReplyAsync(550, "File not found.").ConfigureAwait(false);
                return;
            }

            await WriteRawAsync($"250-Listing {virtualPath}\r\n {FormatMlsx(info, virtualPath)}\r\n250 End\r\n").ConfigureAwait(false);
        }

        private IEnumerable<FileSystemInfo> EnumerateEntries(string directory)
        {
            var list = new List<FileSystemInfo>();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                // Atalhos e junções que apontam para fora da pasta compartilhada não são mostrados.
                if (entry.Name.Contains('\r') || entry.Name.Contains('\n') ||
                    !ServerPathGuard.IsInside(_server.RootPath, entry.FullName, allowRoot: true))
                {
                    continue;
                }

                list.Add(entry);
            }

            return list.OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }

        private string FormatUnix(FileSystemInfo entry)
        {
            var isDirectory = entry is DirectoryInfo;
            var permissions = (isDirectory ? "d" : "-") + (ReadOnly
                ? (isDirectory ? "r-xr-xr-x" : "r--r--r--")
                : (isDirectory ? "rwxr-xr-x" : "rw-r--r--"));
            var size = entry is FileInfo file ? file.Length : 0;
            var time = entry.LastWriteTime;
            var now = DateTime.Now;
            var recent = time > now.AddMonths(-6) && time <= now.AddDays(1);
            var stamp = string.Create(
                CultureInfo.InvariantCulture,
                $"{time:MMM} {time.Day,2} {(recent ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : time.Year.ToString(CultureInfo.InvariantCulture).PadLeft(5))}");
            return string.Create(CultureInfo.InvariantCulture, $"{permissions} 1 ftp ftp {size,12} {stamp} {entry.Name}");
        }

        private string FormatMlsx(FileSystemInfo entry, string? virtualName = null)
        {
            var isDirectory = entry is DirectoryInfo;
            var permissions = isDirectory
                ? (ReadOnly ? "el" : "elcmfpd")
                : (ReadOnly ? "r" : "rwadf");
            var size = entry is FileInfo file ? file.Length : 0;
            var modify = entry.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"type={(isDirectory ? "dir" : "file")};size={size};modify={modify};perm={permissions}; {virtualName ?? entry.Name}");
        }

        private static string StripListFlags(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                return string.Empty;
            }

            // "-a", "-al", "-a /logs", "/logs -a": fica só o caminho.
            var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var path = parts.Where(part => !part.StartsWith('-')).ToArray();
            return path.Length == 0 ? string.Empty : string.Join(' ', path);
        }

        // ---- Transferências ------------------------------------------------------------------

        private async Task RetrieveAsync(string argument)
        {
            if (Resolve(argument) is not { } virtualPath || ToLocal(virtualPath) is not { } local || !File.Exists(local))
            {
                await ReplyAsync(550, "File not found.").ConfigureAwait(false);
                return;
            }

            var length = new FileInfo(local).Length;
            if (_restart > length)
            {
                await ReplyAsync(554, "Invalid REST parameter.").ConfigureAwait(false);
                return;
            }

            if (!await PrepareTransferAsync().ConfigureAwait(false))
            {
                return;
            }

            var start = _restart;
            _restart = 0;
            await ReplyAsync(150, $"Opening data connection for {Path.GetFileName(local)} ({length - start} bytes).").ConfigureAwait(false);

            var channel = await OpenDataChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                await ReplyAsync(425, "Can't open data connection.").ConfigureAwait(false);
                return;
            }

            try
            {
                await using var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
                file.Seek(start, SeekOrigin.Begin);
                await file.CopyToAsync(channel.Stream, 1 << 16, _ct).ConfigureAwait(false);
                await channel.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or UnauthorizedAccessException)
            {
                _server.Log($"Cliente #{_id}: RETR {virtualPath} interrompido — {ex.Message}");
                await ReplyAsync(426, "Connection closed; transfer aborted.").ConfigureAwait(false);
                return;
            }
            finally
            {
                channel.Dispose();
            }

            _server.Log($"Cliente #{_id}: RETR {virtualPath} ({length - start} bytes)");
            await ReplyAsync(226, "Transfer complete.").ConfigureAwait(false);
        }

        private async Task StoreAsync(string argument, bool append)
        {
            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (argument.Length == 0 || Resolve(argument) is not { } virtualPath || virtualPath == "/" ||
                ToLocal(virtualPath) is not { } local)
            {
                await ReplyAsync(553, "Invalid file name.").ConfigureAwait(false);
                return;
            }

            var directory = Path.GetDirectoryName(local);
            if (Directory.Exists(local) || directory is null || !Directory.Exists(directory))
            {
                await ReplyAsync(550, "Cannot write there: the directory does not exist or the name is a folder.").ConfigureAwait(false);
                return;
            }

            if (_restart > 0 && File.Exists(local) && _restart > new FileInfo(local).Length)
            {
                await ReplyAsync(554, "Invalid REST parameter.").ConfigureAwait(false);
                return;
            }

            if (!await PrepareTransferAsync().ConfigureAwait(false))
            {
                return;
            }

            var start = append ? 0 : _restart;
            _restart = 0;
            await ReplyAsync(150, "Ready to receive the file.").ConfigureAwait(false);

            var channel = await OpenDataChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                await ReplyAsync(425, "Can't open data connection.").ConfigureAwait(false);
                return;
            }

            long received;
            try
            {
                var mode = append ? FileMode.Append : start > 0 ? FileMode.OpenOrCreate : FileMode.Create;
                await using var file = new FileStream(local, mode, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                if (!append && start > 0)
                {
                    file.Seek(start, SeekOrigin.Begin);
                }

                var before = file.Position;
                await channel.Stream.CopyToAsync(file, 1 << 16, _ct).ConfigureAwait(false);
                received = file.Position - before;
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or UnauthorizedAccessException)
            {
                _server.Log($"Cliente #{_id}: {(append ? "APPE" : "STOR")} {virtualPath} interrompido — {ex.Message}");
                await ReplyAsync(426, "Connection closed; transfer aborted.").ConfigureAwait(false);
                return;
            }
            finally
            {
                channel.Dispose();
            }

            _server.Log($"Cliente #{_id}: {(append ? "APPE" : "STOR")} {virtualPath} ({received} bytes)");
            await ReplyAsync(226, "Transfer complete.").ConfigureAwait(false);
        }

        // ---- Arquivos e pastas ---------------------------------------------------------------

        private async Task DeleteFileAsync(string argument)
        {
            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (Resolve(argument) is { } virtualPath && ToLocal(virtualPath) is { } local && File.Exists(local))
            {
                File.Delete(local);
                _server.Log($"Cliente #{_id}: DELE {virtualPath}");
                await ReplyAsync(250, "File deleted.").ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            }
        }

        private async Task MakeDirectoryAsync(string argument)
        {
            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (argument.Length == 0 || Resolve(argument) is not { } virtualPath || virtualPath == "/" ||
                ToLocal(virtualPath) is not { } local)
            {
                await ReplyAsync(553, "Invalid directory name.").ConfigureAwait(false);
                return;
            }

            var parent = Path.GetDirectoryName(local);
            if (Directory.Exists(local) || File.Exists(local) || parent is null || !Directory.Exists(parent))
            {
                await ReplyAsync(550, "Cannot create the directory.").ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(local);
            _server.Log($"Cliente #{_id}: MKD {virtualPath}");
            await ReplyAsync(257, $"\"{virtualPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\" created.").ConfigureAwait(false);
        }

        private async Task RemoveDirectoryAsync(string argument)
        {
            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (Resolve(argument) is not { } virtualPath || virtualPath == "/" ||
                ToLocal(virtualPath) is not { } local || !Directory.Exists(local))
            {
                await ReplyAsync(550, "Directory not found.").ConfigureAwait(false);
                return;
            }

            if (Directory.EnumerateFileSystemEntries(local).Any())
            {
                await ReplyAsync(550, "Directory not empty.").ConfigureAwait(false);
                return;
            }

            Directory.Delete(local);
            if (_cwd == virtualPath || _cwd.StartsWith(virtualPath + "/", StringComparison.Ordinal))
            {
                _cwd = "/";
            }

            _server.Log($"Cliente #{_id}: RMD {virtualPath}");
            await ReplyAsync(250, "Directory removed.").ConfigureAwait(false);
        }

        private async Task RenameFromAsync(string argument)
        {
            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (Resolve(argument) is { } virtualPath && virtualPath != "/" && ToLocal(virtualPath) is { } local &&
                (File.Exists(local) || Directory.Exists(local)))
            {
                _renameFrom = local;
                await ReplyAsync(350, "Ready for RNTO.").ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(550, "File or directory not found.").ConfigureAwait(false);
            }
        }

        private async Task RenameToAsync(string argument)
        {
            var source = _renameFrom;
            _renameFrom = null;

            if (source is null)
            {
                await ReplyAsync(503, "Send RNFR first.").ConfigureAwait(false);
                return;
            }

            if (await DenyWriteAsync().ConfigureAwait(false))
            {
                return;
            }

            if (Resolve(argument) is not { } virtualPath || virtualPath == "/" || ToLocal(virtualPath) is not { } target)
            {
                await ReplyAsync(553, "Invalid target name.").ConfigureAwait(false);
                return;
            }

            var parent = Path.GetDirectoryName(target);
            if (File.Exists(target) || Directory.Exists(target) || parent is null || !Directory.Exists(parent))
            {
                await ReplyAsync(550, "The target already exists or its folder does not.").ConfigureAwait(false);
                return;
            }

            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            _server.Log($"Cliente #{_id}: RNTO {ToVirtualFromLocal(source)} → {virtualPath}");
            await ReplyAsync(250, "Renamed.").ConfigureAwait(false);
        }

        private async Task<bool> DenyWriteAsync()
        {
            if (!ReadOnly)
            {
                return false;
            }

            await ReplyAsync(550, "Permission denied: the server is read-only.").ConfigureAwait(false);
            return true;
        }

        // ---- Conexões de dados ---------------------------------------------------------------

        private async Task PasvAsync(bool extended, string argument)
        {
            if (extended && argument.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(200, "EPSV ALL accepted.").ConfigureAwait(false);
                return;
            }

            if (extended && argument.Length > 0 && argument != "1")
            {
                await ReplyAsync(522, "Network protocol not supported, use (1).").ConfigureAwait(false);
                return;
            }

            CloseDataChannel();
            try
            {
                _passive = _server.OpenPassiveListener();
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                _server.Log($"Cliente #{_id}: não foi possível abrir porta passiva — {ex.Message}");
                await ReplyAsync(425, "Can't open passive connection.").ConfigureAwait(false);
                return;
            }

            var port = ((IPEndPoint)_passive.LocalEndpoint).Port;
            if (extended)
            {
                await ReplyAsync(229, $"Entering Extended Passive Mode (|||{port}|)").ConfigureAwait(false);
                return;
            }

            var bytes = IPAddress.Parse(Config.Host).GetAddressBytes();
            await ReplyAsync(
                227,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Entering Passive Mode ({bytes[0]},{bytes[1]},{bytes[2]},{bytes[3]},{port / 256},{port % 256})")).ConfigureAwait(false);
        }

        private async Task PortAsync(string argument)
        {
            var parts = argument.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 6 || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                await ReplyAsync(501, "Syntax error in PORT.").ConfigureAwait(false);
                return;
            }

            var numbers = parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var address = new IPAddress(numbers[..4]);
            await SetActiveAsync(address, (numbers[4] << 8) | numbers[5]).ConfigureAwait(false);
        }

        private async Task EprtAsync(string argument)
        {
            // |1|192.168.0.5|50000|  (o delimitador pode ser qualquer caractere imprimível)
            if (argument.Length < 5)
            {
                await ReplyAsync(501, "Syntax error in EPRT.").ConfigureAwait(false);
                return;
            }

            var parts = argument.Split(argument[0]);
            if (parts.Length < 4 || parts[1] != "1" ||
                !IPAddress.TryParse(parts[2], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                await ReplyAsync(522, "Only IPv4 is supported, use (1).").ConfigureAwait(false);
                return;
            }

            await SetActiveAsync(address, port).ConfigureAwait(false);
        }

        private async Task SetActiveAsync(IPAddress address, int port)
        {
            // Protege contra "FTP bounce": o servidor só se liga de volta ao próprio cliente, em portas altas.
            if (_peer is null || !address.Equals(_peer) || port is < MinActivePort or > 65535)
            {
                _server.Log($"Cliente #{_id}: PORT recusado ({address}:{port})");
                await ReplyAsync(504, "Active mode is only allowed back to your own address on ports 1024 and above.").ConfigureAwait(false);
                return;
            }

            CloseDataChannel();
            _active = new IPEndPoint(address, port);
            await ReplyAsync(200, "PORT command successful.").ConfigureAwait(false);
        }

        /// <summary>Verifica antes do "150": há modo passivo/ativo pendente e a política de TLS é atendida.</summary>
        private async Task<bool> PrepareTransferAsync()
        {
            if (_server.TlsRequired && !_protectData)
            {
                await ReplyAsync(522, "Data connection must be protected: send PBSZ 0 and PROT P.").ConfigureAwait(false);
                return false;
            }

            if (_passive is null && _active is null)
            {
                await ReplyAsync(425, "Use PASV, EPSV or PORT first.").ConfigureAwait(false);
                return false;
            }

            return true;
        }

        private async Task<DataChannel?> OpenDataChannelAsync()
        {
            var passive = _passive;
            var active = _active;
            _passive = null;
            _active = null;

            TcpClient? tcp = null;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                limit.CancelAfter(DataConnectionTimeout);

                if (passive is not null)
                {
                    tcp = await AcceptDataClientAsync(passive, limit.Token).ConfigureAwait(false);
                }
                else if (active is not null)
                {
                    tcp = new TcpClient(AddressFamily.InterNetwork);
                    await tcp.ConnectAsync(active, limit.Token).ConfigureAwait(false);
                }
                else
                {
                    return null;
                }

                tcp.SendTimeout = 60_000;
                tcp.ReceiveTimeout = 60_000;
                Stream stream = tcp.GetStream();
                SslStream? ssl = null;
                if (_protectData && _server._certificate is not null)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _server._certificate,
                            EnabledSslProtocols = TlsProtocols,
                            ClientCertificateRequired = false
                        },
                        limit.Token).ConfigureAwait(false);
                    stream = ssl;
                }

                return new DataChannel(tcp, stream, ssl);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or AuthenticationException or ObjectDisposedException)
            {
                tcp?.Dispose();
                _server.Log($"Cliente #{_id}: conexão de dados falhou — {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    passive?.Stop();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Aceita a ligação de dados passiva, mas só do mesmo IP da ligação de controle (impede que outra máquina
        /// "roube" a transferência ou prenda o servidor).
        /// </summary>
        private async Task<TcpClient> AcceptDataClientAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (true)
            {
                var data = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var remote = NormalizeAddress((data.Client.RemoteEndPoint as IPEndPoint)?.Address);
                if (_peer is null || (remote is not null && remote.Equals(_peer)))
                {
                    return data;
                }

                data.Close();
            }
        }

        private void CloseDataChannel()
        {
            try
            {
                _passive?.Stop();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // ignored
            }

            _passive = null;
            _active = null;
        }

        private sealed class DataChannel(TcpClient client, Stream stream, SslStream? ssl) : IDisposable
        {
            public Stream Stream { get; } = stream;

            /// <summary>Encerra a camada TLS corretamente (close_notify); sem isso alguns clientes acusam erro.</summary>
            public async Task CompleteAsync()
            {
                if (ssl is not null)
                {
                    await ssl.ShutdownAsync().ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                Stream.Dispose();
                client.Dispose();
            }
        }

        // ---- Caminhos ------------------------------------------------------------------------

        /// <summary>Caminho FTP (sempre com "/") a partir da pasta atual; ".." nunca passa da raiz. Nulo se inválido.</summary>
        private string? Resolve(string argument)
        {
            if (argument.Length == 0)
            {
                return _cwd;
            }

            // ':' só aparece em streams alternativos NTFS ou letras de unidade; controle quebra o log e o protocolo.
            if (argument.Contains(':') || argument.Any(char.IsControl))
            {
                return null;
            }

            var path = argument.Replace('\\', '/');
            var segments = new List<string>();
            if (!path.StartsWith('/'))
            {
                segments.AddRange(_cwd.Split('/', StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                // Nomes com ponto/espaço no fim ou nomes reservados do Windows se comportam de forma inesperada.
                if (part.TrimEnd('.', ' ').Length != part.Length)
                {
                    return null;
                }

                segments.Add(part);
            }

            return "/" + string.Join('/', segments);
        }

        /// <summary>Caminho no disco para um caminho FTP já resolvido, ou nulo se sair da pasta compartilhada.</summary>
        private string? ToLocal(string virtualPath)
        {
            try
            {
                var relative = virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var local = Path.GetFullPath(Path.Combine(_server.RootPath, relative));
                return ServerPathGuard.IsInside(_server.RootPath, local, allowRoot: true) ? local : null;
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                return null;
            }
        }

        private string ToVirtualFromLocal(string local)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(_server.RootPath), Path.GetFullPath(local));
            return relative == "." ? "/" : "/" + relative.Replace('\\', '/');
        }

        // ---- Protocolo -----------------------------------------------------------------------

        private static (string Command, string Argument) Split(string line)
        {
            var text = line.Trim();
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return space < 0
                ? (text.ToUpperInvariant(), string.Empty)
                : (text[..space].ToUpperInvariant(), text[(space + 1)..].Trim());
        }

        private Task ReplyAsync(int code, string text) =>
            WriteRawAsync(string.Create(CultureInfo.InvariantCulture, $"{code} {text}\r\n"));

        private async Task WriteRawAsync(string text)
        {
            var bytes = Utf8NoBom.GetBytes(text);
            await _control.WriteAsync(bytes, _ct).ConfigureAwait(false);
            await _control.FlushAsync(_ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Lê uma linha de comando byte a byte (sem buffer de leitura: ao trocar para TLS não pode sobrar nada
        /// em texto puro), com limite de tamanho e de inatividade. Sequências Telnet (IAC) são descartadas.
        /// </summary>
        private async Task<string?> ReadLineAsync()
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            idle.CancelAfter(IdleTimeout);

            var bytes = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                var read = await _control.ReadAsync(single.AsMemory(), idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return bytes.Count == 0 ? null : Utf8NoBom.GetString([.. bytes]);
                }

                var value = single[0];
                if (value == 0xFF)
                {
                    // IAC: comando Telnet (por exemplo, o "ABOR" urgente) — descarta o comando e a opção.
                    if (await _control.ReadAsync(single.AsMemory(), idle.Token).ConfigureAwait(false) == 1 && single[0] is >= 251 and <= 254)
                    {
                        _ = await _control.ReadAsync(single.AsMemory(), idle.Token).ConfigureAwait(false);
                    }

                    continue;
                }

                if (value == (byte)'\n')
                {
                    return Utf8NoBom.GetString([.. bytes]).TrimEnd('\r');
                }

                if (bytes.Count >= MaxCommandLength)
                {
                    throw new InvalidDataException("Linha de comando FTP demasiado longa.");
                }

                bytes.Add(value);
            }
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
