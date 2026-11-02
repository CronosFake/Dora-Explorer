using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;

namespace DoraExplorer.App.Services;

/// <summary>
/// Decide se o certificado de um servidor FTPS é aceito. Certificado válido (autoridade conhecida e nome
/// correto) entra direto; autoassinado, como os de servidores caseiros e equipamentos, é confirmado uma vez
/// pelo usuário e guardado — se mudar depois, o usuário é avisado (mesmo modelo do SSH, em <see cref="HostKeyVerifier"/>).
/// </summary>
internal static class FtpCertificateVerifier
{
    internal const string Algorithm = "FTPS-X509";

    private static readonly Lazy<KnownHostsStore> DefaultStore = new(() => new KnownHostsStore(KnownHostsStore.DefaultPath));

    private static readonly AsyncLocal<(KnownHostsStore Store, Func<string, string, bool> Confirm)?> Override = new();

    /// <summary>Usado nos testes: troca onde as impressões digitais ficam e quem responde à pergunta ao usuário.</summary>
    internal static IDisposable UseForTesting(KnownHostsStore store, Func<string, string, bool> confirm)
    {
        var previous = Override.Value;
        Override.Value = (store, confirm);
        return new Scope(previous);
    }

    public static void Attach(FtpClient client, string host, int port)
    {
        var (store, confirm) = Override.Value ?? (DefaultStore.Value, HostKeyVerifier.Confirm);
        Attach(client, host, port, store, confirm);
    }

    public static void Attach(FtpClient client, string host, int port, KnownHostsStore store, Func<string, string, bool> confirm) =>
        client.ValidateCertificate += (_, e) =>
            e.Accept = Validate(host, port, e.Certificate, e.PolicyErrors, store, confirm);

    internal static bool Validate(
        string host,
        int port,
        X509Certificate? certificate,
        SslPolicyErrors policyErrors,
        KnownHostsStore store,
        Func<string, string, bool> confirm)
    {
        if (certificate is null)
        {
            return false;
        }

        if (policyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        // O prefixo separa estas impressões digitais das chaves SSH do mesmo servidor e porta.
        var storeHost = "ftps:" + host;
        var offered = new KnownHostEntry(Algorithm, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));
        var stored = store.TryGet(storeHost, port);

        switch (HostKeyVerifier.Decide(stored, offered))
        {
            case HostKeyDecision.Trusted:
                return true;

            case HostKeyDecision.Unknown:
                if (!confirm(
                        "Novo certificado FTPS",
                        $"Primeira ligação segura a {host}:{port}.\n\n" +
                        $"Titular: {certificate.Subject}\n" +
                        $"Emissor: {certificate.Issuer}\n" +
                        $"Impressão digital SHA-256: {offered.Sha256}\n\n" +
                        "Este certificado não foi emitido por uma autoridade conhecida (comum em servidores próprios).\n" +
                        "Confirme a impressão digital com o administrador do servidor.\n" +
                        "Confiar neste servidor e guardá-lo?"))
                {
                    return false;
                }

                store.Save(storeHost, port, offered);
                AppLogService.Info($"Certificado FTPS de {host}:{port} guardado (SHA-256 {offered.Sha256}).");
                return true;

            default:
                AppLogService.Warning(
                    $"O certificado FTPS de {host}:{port} MUDOU. Guardado: {stored!.Sha256}; recebido: {offered.Sha256}.");
                if (!confirm(
                        "ATENÇÃO: certificado FTPS alterado",
                        $"O certificado do servidor {host}:{port} MUDOU.\n\n" +
                        $"Guardado: {stored.Sha256}\n" +
                        $"Recebido: {offered.Sha256}\n\n" +
                        "Isto pode indicar um ataque (interceptação) ou apenas que o servidor gerou um certificado novo.\n" +
                        "Só continue se tiver certeza. Substituir o certificado guardado?"))
                {
                    return false;
                }

                store.Save(storeHost, port, offered);
                return true;
        }
    }

    private sealed class Scope((KnownHostsStore Store, Func<string, string, bool> Confirm)? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}