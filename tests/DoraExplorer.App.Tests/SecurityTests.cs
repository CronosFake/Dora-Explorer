using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class ArchiveExtractionPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dora-tests", "dest", "docs");

    [Theory]
    [InlineData("a.txt")]
    [InlineData("sub/b.txt")]
    [InlineData("sub/./c.txt")]
    [InlineData("sub/../d.txt")]
    public void Accepts_paths_that_stay_inside_destination(string relative)
    {
        ArchivePathHelper.TryResolveSafeDestination(Root, relative, out var fullPath).Should().BeTrue();
        fullPath.Should().StartWith(Path.GetFullPath(Root));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("sub/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("/abs/escaped.txt")]
    [InlineData("\\abs\\escaped.txt")]
    [InlineData("C:/Windows/escaped.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_paths_that_escape_destination(string relative)
    {
        ArchivePathHelper.TryResolveSafeDestination(Root, relative, out var fullPath).Should().BeFalse();
        fullPath.Should().BeEmpty();
    }
}

public sealed class ArchiveServiceExtractionTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-zip-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public void Malicious_zip_folder_is_refused_and_writes_nothing_outside_the_destination()
    {
        var archive = Path.Combine(_work, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteEntry(zip, "docs/ok.txt", "ok");
            WriteEntry(zip, "docs/../../escaped.txt", "fora do destino");
        }

        var destination = Directory.CreateDirectory(Path.Combine(_work, "dest", "inner")).FullName;

        var success = new ArchiveService().TryExtractEntry(archive, "docs", destination, out var error);

        success.Should().BeFalse();
        error.Should().Contain("inseguro");
        Directory.GetFileSystemEntries(destination).Should().BeEmpty("nada deve ser extraído quando há uma entrada insegura");
        Directory.GetFiles(_work, "escaped.txt", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Regular_zip_folder_is_still_extracted()
    {
        var archive = Path.Combine(_work, "good.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteEntry(zip, "docs/ok.txt", "ok");
            WriteEntry(zip, "docs/sub/deep.txt", "deep");
        }

        var destination = Directory.CreateDirectory(Path.Combine(_work, "dest")).FullName;

        new ArchiveService().TryExtractEntry(archive, "docs", destination, out var error).Should().BeTrue(error);

        File.ReadAllText(Path.Combine(destination, "docs", "ok.txt")).Should().Be("ok");
        File.ReadAllText(Path.Combine(destination, "docs", "sub", "deep.txt")).Should().Be("deep");
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

public sealed class ProtectedPasswordTests
{
    [Fact]
    public void Password_is_not_written_in_clear_text_and_round_trips()
    {
        var settings = new NetworkProtocolSettings { Host = "10.0.0.1", Password = "s3cr3t-Ünï!" };

        var json = JsonSerializer.Serialize(settings);

        json.Should().NotContain("s3cr3t");
        json.Should().Contain("dpapi:");
        JsonSerializer.Deserialize<NetworkProtocolSettings>(json)!.Password.Should().Be("s3cr3t-Ünï!");
    }

    [Fact]
    public void Client_profile_password_round_trips()
    {
        var profile = new NetworkClientProfile { DisplayName = "sw", Password = "outra-senha" };

        var json = JsonSerializer.Serialize(profile);

        json.Should().NotContain("outra-senha");
        JsonSerializer.Deserialize<NetworkClientProfile>(json)!.Password.Should().Be("outra-senha");
    }

    [Fact]
    public void Legacy_clear_text_password_is_still_read()
    {
        var settings = JsonSerializer.Deserialize<NetworkProtocolSettings>("{\"Password\":\"legacy\"}");

        settings!.Password.Should().Be("legacy");
    }

    [Fact]
    public void Empty_password_stays_empty()
    {
        var json = JsonSerializer.Serialize(new NetworkProtocolSettings());

        json.Should().NotContain("dpapi:");
        JsonSerializer.Deserialize<NetworkProtocolSettings>(json)!.Password.Should().BeEmpty();
    }

    [Fact]
    public void Undecryptable_password_becomes_empty_instead_of_failing()
    {
        var settings = JsonSerializer.Deserialize<NetworkProtocolSettings>("{\"Password\":\"dpapi:AAAA\"}");

        settings!.Password.Should().BeEmpty();
    }
}

public sealed class LegacyPasswordMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dora-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Clear_text_passwords_are_protected_on_startup_and_still_readable()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            path,
            """
            {
              "ThemeMode": 1,
              "Network": {
                "Ftp": { "Port": 21, "Username": "bob", "Password": "senha-do-servidor" },
                "SftpClient": { "Port": 22, "Username": "ana", "Password": "senha-do-cliente" },
                "ClientProfiles": [
                  { "Id": "p1", "Protocol": 2, "Host": "10.0.0.5", "Port": 22, "Username": "root", "Password": "senha-do-perfil" }
                ]
              }
            }
            """);

        var service = new AppSettingsService(path);
        service.ProtectLegacyPasswords();

        var onDisk = File.ReadAllText(path);
        onDisk.Should().NotContain("senha-do-servidor");
        onDisk.Should().NotContain("senha-do-cliente");
        onDisk.Should().NotContain("senha-do-perfil");
        onDisk.Should().Contain("dpapi:");

        var reloaded = new AppSettingsService(path);
        reloaded.LoadNetworkProtocolSettings(NetworkProtocol.Ftp).Password.Should().Be("senha-do-servidor");
        reloaded.LoadNetworkClientSettings(NetworkProtocol.Sftp).Password.Should().Be("senha-do-cliente");
        reloaded.TryGetNetworkClientProfile("p1")!.Password.Should().Be("senha-do-perfil");
    }

    [Fact]
    public void Unreadable_settings_are_kept_aside_instead_of_being_overwritten()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ não é json");
        var service = new AppSettingsService(path);

        service.LoadAccentColorHex().Should().NotBeNullOrEmpty("os padrões são usados");
        service.SaveNetworkClientSettings(NetworkProtocol.Ftp, new NetworkProtocolSettings { Host = "h", Port = 21 });

        var backups = Directory.GetFiles(_directory, "settings.json.corrupt-*");
        backups.Should().ContainSingle();
        File.ReadAllText(backups[0]).Should().Be("{ não é json");
        new AppSettingsService(path).LoadNetworkClientSettings(NetworkProtocol.Ftp).Host.Should().Be("h");
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new AppSettingsService(path);

        service.SaveThemeMode(AppThemeMode.Dark);
        service.SaveThemeMode(AppThemeMode.Light);

        Directory.GetFiles(_directory).Select(Path.GetFileName).Should().Equal("settings.json");
        service.LoadThemeMode().Should().Be(AppThemeMode.Light);
    }

    [Fact]
    public void Already_protected_file_is_left_untouched()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new AppSettingsService(path);
        service.SaveNetworkClientSettings(
            NetworkProtocol.Ftp,
            new NetworkProtocolSettings { Host = "10.0.0.9", Port = 21, Password = "abc" });
        var before = File.ReadAllText(path);

        service.ProtectLegacyPasswords();

        File.ReadAllText(path).Should().Be(before);
    }
}

public sealed class HostKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dora-hostkeys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Unknown_host_requires_confirmation()
    {
        HostKeyVerifier.Decide(null, new KnownHostEntry("ssh-ed25519", "abc"))
            .Should().Be(HostKeyDecision.Unknown);
    }

    [Fact]
    public void Same_key_is_trusted()
    {
        var entry = new KnownHostEntry("ssh-ed25519", "abc");

        HostKeyVerifier.Decide(entry, new KnownHostEntry("SSH-ED25519", "abc"))
            .Should().Be(HostKeyDecision.Trusted);
    }

    [Fact]
    public void Different_fingerprint_or_algorithm_is_a_change()
    {
        var stored = new KnownHostEntry("ssh-ed25519", "abc");

        HostKeyVerifier.Decide(stored, new KnownHostEntry("ssh-ed25519", "xyz"))
            .Should().Be(HostKeyDecision.Changed);
        HostKeyVerifier.Decide(stored, new KnownHostEntry("ssh-rsa", "abc"))
            .Should().Be(HostKeyDecision.Changed);
    }

    [Fact]
    public void Store_round_trips_and_ignores_host_name_case()
    {
        var store = new KnownHostsStore(Path.Combine(_directory, "known_hosts.json"));
        store.TryGet("Switch01", 22).Should().BeNull();

        store.Save("Switch01", 22, new KnownHostEntry("ssh-ed25519", "abc"));

        store.TryGet("switch01", 22).Should().Be(new KnownHostEntry("ssh-ed25519", "abc"));
        store.TryGet("switch01", 2222).Should().BeNull();
    }

    [Fact]
    public void Unreadable_store_knows_no_host()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "known_hosts.json");
        File.WriteAllText(path, "{ isto não é json");
        var store = new KnownHostsStore(path);

        store.TryGet("switch01", 22).Should().BeNull();
    }
}

public sealed class NetworkServerHardeningTests : IDisposable
{
    private const string User = "bob";
    private const string Password = "pw-de-teste";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-srv-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerService _service = new(new NetworkServerLogStore());

    public void Dispose()
    {
        _service.Dispose();
        DeleteWhenReleased(_root);
    }

    // Ao parar o servidor, uma transferência TFTP em curso ainda demora alguns ms a soltar o temporário.
    private static void DeleteWhenReleased(string directory)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void Ftp_server_requires_username_and_password()
    {
        _service.TryStart(Config(NetworkProtocol.Ftp, FreeTcpPort(), null, null), out var error).Should().BeFalse();
        error.Should().Contain("usuário e senha");

        _service.TryStart(Config(NetworkProtocol.Ftp, FreeTcpPort(), User, string.Empty), out error).Should().BeFalse();
        error.Should().Contain("usuário e senha");
    }

    [Theory]
    [InlineData(NetworkProtocol.Sftp)]
    [InlineData(NetworkProtocol.Scp)]
    [InlineData(NetworkProtocol.Smb)]
    public void Ssh_and_SMB_servers_require_username_and_password(NetworkProtocol protocol)
    {
        _service.TryStart(Config(protocol, FreeTcpPort(), null, null), out var error).Should().BeFalse();
        error.Should().Contain("usuário e senha");

        _service.TryStart(Config(protocol, FreeTcpPort(), User, string.Empty), out error).Should().BeFalse();
        error.Should().Contain("usuário e senha");
    }

    [Fact]
    public void Overly_broad_or_missing_roots_are_refused()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var driveRoot = Path.GetPathRoot(_root)!;
        var roots = new[] { string.Empty, "/", driveRoot, profile, Path.GetDirectoryName(profile)! };

        foreach (var root in roots)
        {
            var config = Config(NetworkProtocol.Tftp, FreeUdpPort(), null, null, root);
            _service.TryStart(config, out var error).Should().BeFalse($"a raiz '{root}' não deve ser aceita");
            error.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Ftp_commands_other_than_login_require_authentication()
    {
        StartFtp(out var port);
        using var ftp = new FtpTestClient(port);
        ftp.ReadLine().Should().StartWith("220");

        ftp.Send("SIZE a.txt").Should().StartWith("530");
        ftp.Send("PWD").Should().StartWith("530");
        ftp.Send("PASV").Should().StartWith("530");
        ftp.Send("USER " + User).Should().StartWith("331");
    }

    [Fact]
    public void Ftp_closes_the_connection_after_three_failed_logins()
    {
        StartFtp(out var port);
        using var ftp = new FtpTestClient(port);
        ftp.ReadLine();
        ftp.Send("USER " + User).Should().StartWith("331");

        ftp.Send("PASS errada1").Should().StartWith("530");
        ftp.Send("PASS errada2").Should().StartWith("530");
        ftp.Send("PASS errada3").Should().StartWith("530");

        ftp.ReadLine().Should().StartWith("421");
        ftp.ReadLine().Should().Be("<closed>");
    }

    [Fact]
    public void Ftp_blocks_paths_outside_the_root_and_alternate_streams()
    {
        StartFtp(out var port);
        using var ftp = new FtpTestClient(port);
        ftp.ReadLine();
        Login(ftp);

        // ".." na raiz não sai da pasta compartilhada: a sessão continua na raiz.
        ftp.Send("CWD ..").Should().StartWith("250");
        ftp.Send("PWD").Should().StartWith("257 \"/\"");
        ftp.Send("CWD ../../..").Should().StartWith("250");
        ftp.Send("PWD").Should().StartWith("257 \"/\"");
        ftp.Send("CWD C:\\Windows").Should().StartWith("550");
        ftp.Send("SIZE segredo.txt:stream").Should().StartWith("550");
        ftp.Send("RETR ../fora.txt").Should().StartWith("550");
    }

    [Fact]
    public void Ftp_lists_over_a_passive_connection_from_the_same_client()
    {
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "oi");
        StartFtp(out var port);
        using var ftp = new FtpTestClient(port);
        ftp.ReadLine();
        Login(ftp);

        var pasv = ftp.Send("PASV");
        pasv.Should().StartWith("227");
        var match = Regex.Match(pasv, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
        match.Success.Should().BeTrue();
        var dataPort = (int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) * 256) +
                       int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);

        ftp.Send("LIST").Should().StartWith("150");
        using var data = new TcpClient();
        data.Connect(IPAddress.Loopback, dataPort);
        data.GetStream().ReadTimeout = 10000;
        using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
        reader.ReadToEnd().Should().Contain("hello.txt");

        ftp.ReadLine().Should().StartWith("226");
    }

    [Fact]
    public void Tftp_write_that_never_completes_does_not_truncate_the_existing_file()
    {
        var existing = Path.Combine(_root, "keep.txt");
        File.WriteAllText(existing, "original");
        StartTftp(out var port);
        using var client = TftpClient();

        client.Send(WriteRequest("keep.txt"), new IPEndPoint(IPAddress.Loopback, port)).Should().BeGreaterThan(0);
        Receive(client).Buffer[1].Should().Be(4);

        Thread.Sleep(500);
        File.ReadAllText(existing).Should().Be("original");
    }

    [Fact]
    public void Tftp_completed_write_replaces_the_file_and_ignores_repeated_blocks()
    {
        StartTftp(out var port);
        using var client = TftpClient();
        client.Send(WriteRequest("upload.bin"), new IPEndPoint(IPAddress.Loopback, port)).Should().BeGreaterThan(0);

        var ack0 = Receive(client);
        ack0.Buffer[1].Should().Be(4);
        var transfer = ack0.RemoteEndPoint;

        var firstBlock = Enumerable.Repeat((byte)'A', 512).ToArray();
        var lastBlock = Enumerable.Repeat((byte)'B', 10).ToArray();

        client.Send(DataPacket(1, firstBlock), transfer);
        Receive(client).Buffer[3].Should().Be(1);

        // Repetição do bloco 1 (ACK perdido): é confirmada de novo, mas não gravada duas vezes.
        client.Send(DataPacket(1, firstBlock), transfer);
        Receive(client).Buffer[3].Should().Be(1);

        client.Send(DataPacket(2, lastBlock), transfer);
        Receive(client).Buffer[3].Should().Be(2);

        var target = Path.Combine(_root, "upload.bin");
        WaitFor(() => File.Exists(target) && new FileInfo(target).Length == 522).Should().BeTrue();
        var content = File.ReadAllBytes(target);
        content.Take(512).Should().OnlyContain(b => b == (byte)'A');
        content.Skip(512).Should().OnlyContain(b => b == (byte)'B');
    }

    [Fact]
    public void Tftp_rejects_paths_outside_the_root_but_accepts_a_leading_slash()
    {
        StartTftp(out var port);
        using var client = TftpClient();
        var server = new IPEndPoint(IPAddress.Loopback, port);

        client.Send(WriteRequest("../escape.txt"), server).Should().BeGreaterThan(0);
        Receive(client).Buffer[1].Should().Be(5);

        client.Send(WriteRequest("segredo.txt:stream"), server).Should().BeGreaterThan(0);
        Receive(client).Buffer[1].Should().Be(5);

        client.Send(WriteRequest("/ok.txt"), server).Should().BeGreaterThan(0);
        Receive(client).Buffer[1].Should().Be(4);

        File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "escape.txt")).Should().BeFalse();
    }

    private NetworkConnectionInfo Config(
        NetworkProtocol protocol,
        int port,
        string? user,
        string? password,
        string? root = null) =>
        new()
        {
            Protocol = protocol,
            Host = "127.0.0.1",
            Port = port,
            Username = user,
            Password = password,
            RemotePath = root ?? _root
        };

    private void StartFtp(out int port)
    {
        port = FreeTcpPort();
        _service.TryStart(Config(NetworkProtocol.Ftp, port, User, Password), out var error)
            .Should().BeTrue(error);
    }

    private void StartTftp(out int port)
    {
        port = FreeUdpPort();
        _service.TryStart(Config(NetworkProtocol.Tftp, port, null, null), out var error)
            .Should().BeTrue(error);
    }

    private static void Login(FtpTestClient ftp)
    {
        ftp.Send("USER " + User).Should().StartWith("331");
        ftp.Send("PASS " + Password).Should().StartWith("230");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static UdpClient TftpClient()
    {
        var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 5000;
        return client;
    }

    private static UdpReceiveResult Receive(UdpClient client)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var buffer = client.Receive(ref remote);
        return new UdpReceiveResult(buffer, remote);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    private static byte[] WriteRequest(string fileName)
    {
        var name = Encoding.ASCII.GetBytes(fileName);
        var mode = Encoding.ASCII.GetBytes("octet");
        var packet = new byte[2 + name.Length + 1 + mode.Length + 1];
        packet[1] = 2;
        name.CopyTo(packet, 2);
        mode.CopyTo(packet, 3 + name.Length);
        return packet;
    }

    private static byte[] DataPacket(int block, byte[] payload)
    {
        var packet = new byte[4 + payload.Length];
        packet[1] = 3;
        packet[2] = (byte)(block >> 8);
        packet[3] = (byte)block;
        payload.CopyTo(packet, 4);
        return packet;
    }

    private sealed class FtpTestClient : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public FtpTestClient(int port)
        {
            _tcp.Connect(IPAddress.Loopback, port);
            var stream = _tcp.GetStream();
            stream.ReadTimeout = 10000;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        }

        public string ReadLine()
        {
            try
            {
                return _reader.ReadLine() ?? "<closed>";
            }
            catch (IOException)
            {
                return "<closed>";
            }
        }

        public string Send(string command)
        {
            _writer.WriteLine(command);
            return ReadLine();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _tcp.Dispose();
        }
    }
}
