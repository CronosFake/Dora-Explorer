using System.IO;
using System.Net;
using DoraExplorer.App.Models;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using Utilities;

namespace DoraExplorer.App.Services;

/// <summary>
/// Servidor SMB 2 e 3 (SMBLibrary): um compartilhamento com a pasta raiz, protegido por usuário e senha (NTLM, com
/// limite de tentativas de login). O SMB 1, inseguro, fica desligado. O acesso aos arquivos é do
/// <see cref="SmbDirectoryFileStore"/>, que só enxerga a pasta escolhida.
/// </summary>
internal sealed class SmbFileServer : ProtocolServerBase
{
    private const int MaxLoginAttempts = 5;
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(5);

    private SMBServer? _server;

    public SmbFileServer(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs)
        : base(config, rootPath, logs)
    {
        ShareName = ShareNames.FromFolder(rootPath);
    }

    /// <summary>Nome do compartilhamento (a parte depois do servidor em \\servidor\nome).</summary>
    public string ShareName { get; }

    public override void Start()
    {
        var store = new SmbDirectoryFileStore(RootPath, ShareName, ReadOnly, Log);
        var share = new FileSystemShare(ShareName, store);
        share.AccessRequested += OnAccessRequested;

        var shares = new SMBShareCollection { share };
        var authentication = new IndependentNTLMAuthenticationProvider(PasswordOf, MaxLoginAttempts, LoginWindow);
        var server = new SMBServer(shares, new GSSProvider(authentication));
        server.LogEntryAdded += OnLogEntry;
        server.ConnectionRequested += OnConnectionRequested;

        try
        {
            SmbLibraryAccess.Start(server, IPAddress.Parse(Config.Host), Config.Port);
        }
        catch
        {
            server.LogEntryAdded -= OnLogEntry;
            server.ConnectionRequested -= OnConnectionRequested;
            throw;
        }

        _server = server;
        IsRunning = true;
        Log($"SMB a escutar em {Config.Host}:{Config.Port}" + (ReadOnly ? " (somente leitura)" : string.Empty));
        Log($"Compartilhamento: \\\\{Config.Host}\\{ShareName}" + (Config.Port == SmbLibraryAccess.StandardPort
            ? string.Empty
            : $" (porta {Config.Port}: no Linux use -o port={Config.Port}; o Explorer do Windows costuma exigir a porta 445)"));
        Log("Somente SMB 2 e 3 (o SMB 1 está desligado).");
    }

    protected override void StopCore(bool closeListener)
    {
        var server = _server;
        _server = null;
        if (server is null)
        {
            return;
        }

        server.LogEntryAdded -= OnLogEntry;
        server.ConnectionRequested -= OnConnectionRequested;

        if (closeListener)
        {
            try
            {
                server.Stop();
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                // ignored
            }
        }
    }

    /// <summary>A senha do usuário do servidor; qualquer outro nome não existe (o NTLM então recusa o login).</summary>
    private string? PasswordOf(string userName) =>
        string.Equals(userName, Config.Username, StringComparison.OrdinalIgnoreCase) ? Config.Password : null;

    private void OnAccessRequested(object? sender, AccessRequestArgs e)
    {
        // Um usuário que não é o configurado nunca chega aqui (o login falha), mas a checagem é barata. O modo somente
        // leitura fica por conta do SmbDirectoryFileStore: ele recusa com "mídia protegida contra gravação", que o cliente
        // mostra como somente leitura (negar aqui viraria só "acesso negado").
        e.Allow = string.Equals(e.UserName, Config.Username, StringComparison.OrdinalIgnoreCase);
        if (!e.Allow)
        {
            Log($"Acesso recusado a {e.UserName} em {e.Path} ({e.RequestedAccess})");
        }
    }

    private void OnConnectionRequested(object? sender, ConnectionRequestEventArgs e) =>
        Log($"Conexão de {e.IPEndPoint}");

    private void OnLogEntry(object? sender, LogEntry entry)
    {
        // A biblioteca registra muito detalhe; o que ajuda o usuário são avisos e erros.
        if (entry.Severity is Severity.Critical or Severity.Error or Severity.Warning)
        {
            Log($"{entry.Message}");
        }
    }
}
