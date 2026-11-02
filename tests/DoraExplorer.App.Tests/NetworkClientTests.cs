using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Renci.SshNet;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class FtpClientSecurityTests : IDisposable
{
    private const string User = "dora";
    private const string Password = "s3nha-forte";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ftpc-root-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ftpc-work-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerLogStore _logs = new();
    private readonly NetworkServerService _service;
    private readonly KnownHostsStore _store;
    private readonly string _certificatePath;
    private int _port;

    public FtpClientSecurityTests()
    {
        _certificatePath = Path.Combine(_work, "ftps.bin");
        _service = new NetworkServerService(_logs, _certificatePath);
        _store = new KnownHostsStore(Path.Combine(_work, "known_hosts.json"));
        File.WriteAllText(Path.Combine(_root, "arquivo.txt"), "conteudo");
    }

    public void Dispose()
    {
        _service.Dispose();
        FileOperationTests.DeleteQuietly(_root);
        FileOperationTests.DeleteQuietly(_work);
    }

    [Fact]
    public void Explicit_ftps_asks_once_to_trust_a_self_signed_certificate_and_remembers_it()
    {
        StartServer(FtpServerSecurity.Required);
        var asked = 0;
        using var trust = FtpCertificateVerifier.UseForTesting(_store, (_, _) =>
        {
            asked++;
            return true;
        });

        List(FtpClientSecurity.Explicit, out var entries, out var error).Should().BeTrue(error);
        entries.Select(e => e.Name).Should().Contain("arquivo.txt");

        List(FtpClientSecurity.Explicit, out _, out error).Should().BeTrue(error);
        asked.Should().Be(1, "na segunda ligação a impressão digital já é conhecida");

        using var certificate = FtpTlsCertificate.GetOrCreate(_certificatePath);
        _store.TryGet("ftps:127.0.0.1", _port)!.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));
    }

    [Fact]
    public void Declining_the_certificate_refuses_the_connection_and_stores_nothing()
    {
        StartServer(FtpServerSecurity.Required);
        using var trust = FtpCertificateVerifier.UseForTesting(_store, (_, _) => false);

        List(FtpClientSecurity.Explicit, out var entries, out var error).Should().BeFalse();

        entries.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
        _store.TryGet("ftps:127.0.0.1", _port).Should().BeNull();
    }

    [Fact]
    public void A_certificate_that_changed_since_it_was_trusted_triggers_a_warning()
    {
        StartServer(FtpServerSecurity.Required);
        _store.Save("ftps:127.0.0.1", _port, new KnownHostEntry(FtpCertificateVerifier.Algorithm, "AAAA"));
        string? warning = null;
        using var trust = FtpCertificateVerifier.UseForTesting(_store, (title, _) =>
        {
            warning = title;
            return false;
        });

        List(FtpClientSecurity.Explicit, out _, out _).Should().BeFalse();

        warning.Should().Contain("alterado");
        _store.TryGet("ftps:127.0.0.1", _port)!.Sha256.Should().Be("AAAA", "recusar não substitui a impressão digital guardada");
    }

    [Fact]
    public void Auto_mode_uses_tls_when_the_server_offers_it_and_plain_ftp_when_it_does_not()
    {
        using var trust = FtpCertificateVerifier.UseForTesting(_store, (_, _) => true);

        StartServer(FtpServerSecurity.Optional);
        List(FtpClientSecurity.Auto, out _, out var error).Should().BeTrue(error);
        _logs.GetText(NetworkProtocol.Ftp).Should().Contain("canal de controle protegido");

        _service.StopServer(NetworkProtocol.Ftp);
        StartServer(FtpServerSecurity.Disabled);
        List(FtpClientSecurity.Auto, out var entries, out error).Should().BeTrue(error);
        entries.Select(e => e.Name).Should().Contain("arquivo.txt");
        _logs.GetText(NetworkProtocol.Ftp).Should().NotContain("canal de controle protegido");
    }

    [Fact]
    public void Plain_client_cannot_log_into_a_server_that_requires_tls()
    {
        StartServer(FtpServerSecurity.Required);

        List(FtpClientSecurity.None, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Listed_times_come_back_in_local_time()
    {
        StartServer(FtpServerSecurity.Disabled);
        var expected = DateTime.Now.AddHours(-5);
        File.SetLastWriteTime(Path.Combine(_root, "arquivo.txt"), expected);

        List(FtpClientSecurity.None, out var entries, out var error).Should().BeTrue(error);

        var listed = entries.Single(e => e.Name == "arquivo.txt").LastModified!.Value;
        (listed - expected).Duration().Should().BeLessThan(TimeSpan.FromMinutes(2), "MLSD vem em UTC e o cliente converte");
    }

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    public void Certificates_with_policy_errors_need_the_users_confirmation(SslPolicyErrors errors, bool acceptedWithoutAsking)
    {
        using var certificate = FtpTlsCertificate.GetOrCreate(_certificatePath);
        var asked = false;

        var accepted = FtpCertificateVerifier.Validate("srv", 990, certificate, errors, _store, (_, _) =>
        {
            asked = true;
            return false;
        });

        accepted.Should().Be(acceptedWithoutAsking);
        asked.Should().Be(!acceptedWithoutAsking);
    }

    [Fact]
    public void A_missing_certificate_is_never_accepted()
    {
        FtpCertificateVerifier.Validate("srv", 21, null, SslPolicyErrors.None, _store, (_, _) => true).Should().BeFalse();
    }

    private void StartServer(FtpServerSecurity security)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _service.TryStart(
            new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Ftp,
                Host = "127.0.0.1",
                Port = _port,
                Username = User,
                Password = Password,
                RemotePath = _root,
                ServerSecurity = security
            },
            out var error).Should().BeTrue(error);
    }

    private bool List(FtpClientSecurity security, out IReadOnlyList<FileSystemEntry> entries, out string? error) =>
        new NetworkFileService().TryConnectAndList(
            new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Ftp,
                Host = "127.0.0.1",
                Port = _port,
                Username = User,
                Password = Password,
                RemotePath = "/",
                ClientSecurity = security
            },
            out entries,
            out error);
}

public sealed class SshAuthenticationTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ssh-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => FileOperationTests.DeleteQuietly(_work);

    [Fact]
    public void A_private_key_is_tried_first_then_password_and_keyboard_interactive()
    {
        var methods = NetworkFileService.BuildSshAuthentication(Connection(KeyFile(), "segredo"));

        methods.Select(m => m.Name).Should().Equal("publickey", "password", "keyboard-interactive");
    }

    [Fact]
    public void A_key_without_password_does_not_add_password_methods()
    {
        var methods = NetworkFileService.BuildSshAuthentication(Connection(KeyFile(), password: null));

        methods.Select(m => m.Name).Should().Equal("publickey");
    }

    [Fact]
    public void Without_a_key_the_password_methods_are_used()
    {
        var methods = NetworkFileService.BuildSshAuthentication(Connection(keyPath: null, "senha"));

        methods.Select(m => m.Name).Should().Equal("password", "keyboard-interactive");
    }

    [Fact]
    public void An_unreadable_key_explains_which_file_failed()
    {
        var missing = Path.Combine(_work, "nao-existe.pem");
        var notAKey = Path.Combine(_work, "texto.pem");
        File.WriteAllText(notAKey, "isto não é uma chave privada");

        var missingKey = () => NetworkFileService.BuildSshAuthentication(Connection(missing, null));
        var invalidKey = () => NetworkFileService.BuildSshAuthentication(Connection(notAKey, null));

        missingKey.Should().Throw<IOException>().WithMessage("*nao-existe.pem*");
        invalidKey.Should().Throw<IOException>().WithMessage("*texto.pem*");
    }

    [Fact]
    public void The_key_passphrase_is_stored_protected_and_round_trips()
    {
        var path = Path.Combine(_work, "settings.json");
        var settings = new AppSettingsService(path);
        var profile = new NetworkClientProfile
        {
            Protocol = NetworkProtocol.Sftp,
            DisplayName = "servidor",
            Host = "10.0.0.7",
            Port = 22,
            Username = "root",
            PrivateKeyPath = @"C:\chaves\id_rsa",
            PrivateKeyPassphrase = "frase-secreta-da-chave",
            ClientSecurity = FtpClientSecurity.Explicit
        };

        settings.SaveNetworkClientProfile(profile);

        var onDisk = File.ReadAllText(path);
        onDisk.Should().NotContain("frase-secreta-da-chave");
        onDisk.Should().Contain("dpapi:");

        var loaded = new AppSettingsService(path).TryGetNetworkClientProfile(profile.Id)!;
        loaded.PrivateKeyPassphrase.Should().Be("frase-secreta-da-chave");
        loaded.PrivateKeyPath.Should().Be(@"C:\chaves\id_rsa");
        loaded.ClientSecurity.Should().Be(FtpClientSecurity.Explicit);
        loaded.ToConnection().PrivateKeyPassphrase.Should().Be("frase-secreta-da-chave");
    }

    [Fact]
    public void Old_profiles_without_the_new_fields_keep_working_with_safe_defaults()
    {
        var path = Path.Combine(_work, "velho.json");
        File.WriteAllText(path, """
            { "Network": { "ClientProfiles": [
              { "Id": "p1", "Protocol": 1, "DisplayName": "antigo", "Host": "h", "Port": 21, "Username": "u" } ] } }
            """);

        var profile = new AppSettingsService(path).TryGetNetworkClientProfile("p1")!;

        profile.ClientSecurity.Should().Be(FtpClientSecurity.Auto);
        profile.PrivateKeyPath.Should().BeEmpty();
    }

    private string KeyFile()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(_work, "id_rsa");
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        return path;
    }

    private static NetworkConnectionInfo Connection(string? keyPath, string? password) =>
        new()
        {
            Protocol = NetworkProtocol.Sftp,
            Host = "h",
            Port = 22,
            Username = "u",
            Password = password,
            PrivateKeyPath = keyPath
        };
}
