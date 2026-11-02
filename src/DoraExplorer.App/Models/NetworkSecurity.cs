namespace DoraExplorer.App.Models;

/// <summary>Criptografia do servidor FTP embutido (FTPS explícito, RFC 4217).</summary>
public enum FtpServerSecurity
{
    /// <summary>Somente FTP simples (credenciais trafegam em texto puro).</summary>
    Disabled,

    /// <summary>Aceita FTPS (AUTH TLS) e também FTP simples.</summary>
    Optional,

    /// <summary>Exige FTPS: recusa o login antes de a sessão ser criptografada.</summary>
    Required
}

/// <summary>Criptografia usada pelo cliente FTP, como nos clientes de mercado.</summary>
public enum FtpClientSecurity
{
    /// <summary>FTPS explícito se o servidor oferecer; senão FTP simples.</summary>
    Auto,

    /// <summary>Somente FTP simples.</summary>
    None,

    /// <summary>FTPS explícito obrigatório (AUTH TLS na porta de controle).</summary>
    Explicit,

    /// <summary>FTPS implícito (TLS desde a conexão, normalmente porta 990).</summary>
    Implicit
}
