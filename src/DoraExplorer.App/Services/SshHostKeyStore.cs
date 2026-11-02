using System.IO;
using System.Security.Cryptography;
using DoraExplorer.App.Infrastructure;
using Microsoft.DevTunnels.Ssh;
using IKeyPair = Microsoft.DevTunnels.Ssh.Algorithms.IKeyPair;

namespace DoraExplorer.App.Services;

/// <summary>
/// Chave de identidade do servidor SFTP/SCP (ECDSA P-384). É criada uma vez e guardada protegida pelo Windows
/// (só o usuário atual abre), para que a impressão digital não mude a cada início: se mudasse, todo cliente
/// avisaria "a identidade do servidor mudou" e o usuário aprenderia a ignorar o aviso que o protege de um impostor.
/// </summary>
internal static class SshHostKeyStore
{
    private static readonly byte[] Entropy = "DoraExplorer.SshHostKey.v1"u8.ToArray();

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DoraExplorer",
        "ssh-host-key.bin");

    public static IKeyPair GetOrCreate(string? path = null)
    {
        path ??= DefaultPath;

        if (TryLoad(path) is { } existing)
        {
            return existing;
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        Save(path, ecdsa);
        return CreateKeyPair(parameters);
    }

    /// <summary>Impressão digital no formato dos clientes OpenSSH ("SHA256:...").</summary>
    public static string Fingerprint(IKeyPair key)
    {
        var hash = SHA256.HashData(key.GetPublicKeyBytes(key.KeyAlgorithmName).ToArray());
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private static IKeyPair CreateKeyPair(ECParameters parameters)
    {
        var key = SshAlgorithms.PublicKey.ECDsaSha2Nistp384.CreateKeyPair();
        ((Microsoft.DevTunnels.Ssh.Algorithms.ECDsa.KeyPair)key).ImportParameters(parameters);
        return key;
    }

    private static IKeyPair? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
            return parameters.Curve.Oid.Value == ECCurve.NamedCurves.nistP384.Oid.Value ? CreateKeyPair(parameters) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Chave do servidor SFTP/SCP ilegível, será recriada: {ex.Message}");
            return null;
        }
    }

    private static void Save(string path, ECDsa key)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var protectedKey = ProtectedData.Protect(key.ExportPkcs8PrivateKey(), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedKey);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Sem salvar, a identidade só vale até fechar o programa — o servidor continua funcionando.
            AppLogService.Warning($"Não foi possível guardar a chave do servidor SFTP/SCP: {ex.Message}");
        }
    }
}
