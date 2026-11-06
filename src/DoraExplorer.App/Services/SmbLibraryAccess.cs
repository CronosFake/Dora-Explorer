using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.Server;

namespace DoraExplorer.App.Services;

/// <summary>
/// A API pública da SMBLibrary só conecta e escuta nas portas fixas (445 e 139), mas a classe tem sobrecargas internas
/// com a porta. Elas são usadas por reflexão só quando a porta não é a 445, e <c>SmbLibraryContractTests</c> falha se
/// uma atualização do pacote as tirar do lugar (o pacote está fixado numa versão exata por esse motivo).
/// </summary>
internal static class SmbLibraryAccess
{
    public const int StandardPort = 445;

    private static readonly MethodInfo? ClientConnectWithPort = typeof(SMB2Client).GetMethod(
        "Connect",
        BindingFlags.NonPublic | BindingFlags.Instance,
        binder: null,
        [typeof(IPAddress), typeof(SMBTransportType), typeof(int)],
        modifiers: null);

    private static readonly MethodInfo? ServerStartWithPort = typeof(SMBServer).GetMethod(
        "Start",
        BindingFlags.NonPublic | BindingFlags.Instance,
        binder: null,
        [typeof(IPAddress), typeof(SMBTransportType), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(TimeSpan?)],
        modifiers: null);

    /// <summary>As duas sobrecargas internas existem nesta versão da biblioteca.</summary>
    public static bool SupportsCustomPorts => ClientConnectWithPort is not null && ServerStartWithPort is not null;

    public static bool Connect(SMB2Client client, IPAddress address, int port)
    {
        if (port == StandardPort)
        {
            return client.Connect(address, SMBTransportType.DirectTCPTransport);
        }

        if (ClientConnectWithPort is null)
        {
            throw new NotSupportedException("Esta versão da biblioteca SMB só liga na porta 445.");
        }

        try
        {
            return (bool)ClientConnectWithPort.Invoke(client, [address, SMBTransportType.DirectTCPTransport, port])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is SocketException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Resolve o nome do servidor para um endereço (IPv4 primeiro).</summary>
    public static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Inicia só com SMB 2 e 3 (o SMB 1 é inseguro e fica desligado).</summary>
    public static void Start(SMBServer server, IPAddress address, int port)
    {
        if (port == StandardPort)
        {
            server.Start(address, SMBTransportType.DirectTCPTransport, enableSMB1: false, enableSMB2: true, enableSMB3: true);
            return;
        }

        if (ServerStartWithPort is null)
        {
            throw new NotSupportedException("Esta versão da biblioteca SMB só escuta na porta 445.");
        }

        try
        {
            ServerStartWithPort.Invoke(server, [address, SMBTransportType.DirectTCPTransport, port, false, true, true, null]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Quem chama trata a falha de abrir a porta (SocketException) como qualquer outra.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Texto do código de resultado do SMB em português.</summary>
    public static string Describe(NTStatus status) =>
        status switch
        {
            NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD => "usuário ou senha incorretos",
            NTStatus.STATUS_ACCOUNT_DISABLED => "a conta está desativada no servidor",
            NTStatus.STATUS_PASSWORD_EXPIRED => "a senha expirou",
            NTStatus.STATUS_ACCOUNT_RESTRICTION or NTStatus.STATUS_INVALID_LOGON_HOURS or NTStatus.STATUS_INVALID_WORKSTATION
                => "a conta não pode entrar nesta máquina ou neste horário",
            NTStatus.STATUS_ACCESS_DENIED => "acesso negado pelo servidor",
            NTStatus.STATUS_BAD_NETWORK_NAME => "o compartilhamento não existe no servidor",
            NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE
                => "o item não existe",
            NTStatus.STATUS_OBJECT_NAME_COLLISION => "já existe um item com esse nome",
            NTStatus.STATUS_SHARING_VIOLATION => "o item está em uso por outro programa",
            NTStatus.STATUS_DISK_FULL => "sem espaço no servidor",
            NTStatus.STATUS_MEDIA_WRITE_PROTECTED => "o compartilhamento é somente leitura",
            NTStatus.STATUS_FILE_IS_A_DIRECTORY => "o item é uma pasta",
            NTStatus.STATUS_DELETE_PENDING => "o item está sendo apagado",
            NTStatus.STATUS_NOT_SUPPORTED or NTStatus.STATUS_NOT_IMPLEMENTED => "o servidor não oferece esta operação",
            NTStatus.STATUS_IO_TIMEOUT => "o servidor não respondeu a tempo",
            NTStatus.STATUS_TOO_MANY_SESSIONS => "o servidor atingiu o limite de sessões",
            NTStatus.STATUS_OBJECT_NAME_INVALID or NTStatus.STATUS_OBJECT_PATH_INVALID or NTStatus.STATUS_OBJECT_PATH_SYNTAX_BAD
                => "nome inválido",
            _ => $"o servidor respondeu {status}"
        };
}
