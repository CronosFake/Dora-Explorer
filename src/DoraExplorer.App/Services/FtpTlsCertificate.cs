using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Certificado autoassinado do servidor FTPS. É criado uma vez e guardado (protegido pelo Windows, só o usuário
/// atual abre), para que a impressão digital não mude a cada início e os clientes consigam confiar nela.
/// </summary>
internal static class FtpTlsCertificate
{
    private static readonly byte[] Entropy = "DoraExplorer.FtpTls.v1"u8.ToArray();
    private static readonly TimeSpan Validity = TimeSpan.FromDays(5 * 365);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DoraExplorer",
        "ftps-certificate.bin");

    public static X509Certificate2 GetOrCreate(string? path = null)
    {
        path ??= DefaultPath;

        if (TryLoad(path) is { } existing)
        {
            return existing;
        }

        var created = Create();
        Save(path, created);

        // Reimportar do PFX: uma chave recém-gerada em memória não serve para o TLS do Windows (SChannel).
        return new X509Certificate2(created.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var certificate = new X509Certificate2(pfx);
            if (certificate.NotAfter - DateTime.Now < RenewBefore || !certificate.HasPrivateKey)
            {
                certificate.Dispose();
                return null;
            }

            return certificate;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Certificado FTPS ilegível, será recriado: {ex.Message}");
            return null;
        }
    }

    private static void Save(string path, X509Certificate2 certificate)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var protectedPfx = ProtectedData.Protect(
                certificate.Export(X509ContentType.Pfx), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedPfx);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Sem salvar, o certificado só vale até fechar o programa — o servidor continua funcionando.
            AppLogService.Warning($"Não foi possível guardar o certificado FTPS: {ex.Message}");
        }
    }

    private static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var machine = Environment.MachineName;
        var request = new CertificateRequest($"CN=Dora Explorer FTP ({machine})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // autenticação de servidor

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(machine);
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Validity));
    }
}
