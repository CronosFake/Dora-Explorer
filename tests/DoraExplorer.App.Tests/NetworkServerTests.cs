using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using FluentFTP;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class FtpServerTests : IDisposable
{
    private const string User = "dora";
    private const string Password = "s3nha-forte";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ftp-root-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ftp-work-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerService _service;
    private int _port;

    public FtpServerTests()
    {
        _service = new NetworkServerService(new NetworkServerLogStore(), Path.Combine(_work, "ftps.bin"));
    }

    public void Dispose()
    {
        _service.Dispose();
        FileOperationTests.DeleteQuietly(_root);
        FileOperationTests.DeleteQuietly(_work);
    }

    // ---- Comandos de arquivo, com um cliente FTP de verdade (FluentFTP) -----------------------

    [Fact]
    public void A_real_client_can_list_upload_download_rename_and_delete()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "existente.txt"), "ola");
        using var client = Client(FtpEncryptionMode.None);
        client.Connect();

        client.GetListing("/").Select(i => i.Name).Should().Contain("existente.txt");

        var local = Path.Combine(_work, "envio.bin");
        File.WriteAllBytes(local, Enumerable.Range(0, 70_000).Select(i => (byte)(i % 251)).ToArray());
        client.UploadFile(local, "/envio.bin");
        File.ReadAllBytes(Path.Combine(_root, "envio.bin")).Should().Equal(File.ReadAllBytes(local));

        client.GetFileSize("/envio.bin").Should().Be(70_000);
        var modified = client.GetModifiedTime("/envio.bin");
        Math.Min(Math.Abs((modified - DateTime.Now).TotalMinutes), Math.Abs((modified - DateTime.UtcNow).TotalMinutes))
            .Should().BeLessThan(2, "MDTM devolve a hora UTC do arquivo");

        var back = Path.Combine(_work, "volta.bin");
        client.DownloadFile(back, "/envio.bin");
        File.ReadAllBytes(back).Should().Equal(File.ReadAllBytes(local));

        client.Rename("/envio.bin", "/renomeado.bin");
        File.Exists(Path.Combine(_root, "envio.bin")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "renomeado.bin")).Should().BeTrue();

        client.DeleteFile("/renomeado.bin");
        File.Exists(Path.Combine(_root, "renomeado.bin")).Should().BeFalse();
    }

    [Fact]
    public void Folders_can_be_created_listed_and_removed_only_when_empty()
    {
        Start();
        using var client = Client(FtpEncryptionMode.None);
        client.Connect();

        client.CreateDirectory("/docs");
        Directory.Exists(Path.Combine(_root, "docs")).Should().BeTrue();

        File.WriteAllText(Path.Combine(_root, "docs", "a.txt"), "x");
        client.GetListing("/docs").Select(i => i.Name).Should().Equal("a.txt");

        // O servidor só aceita RMD em pasta vazia (o FluentFTP esvazia antes; aqui o protocolo é puro).
        using var raw = new RawFtp(_port);
        raw.Login();
        raw.Send("RMD docs").Should().StartWith("550");
        raw.Send("DELE docs/a.txt").Should().StartWith("250");
        raw.Send("RMD docs").Should().StartWith("250");
        Directory.Exists(Path.Combine(_root, "docs")).Should().BeFalse();
    }

    [Fact]
    public void Uploads_can_be_resumed_with_REST_and_APPE_appends()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "log.txt"), "AAAA");
        using var raw = new RawFtp(_port);
        raw.Login();

        raw.Send("TYPE I").Should().StartWith("200");
        raw.Transfer("STOR log.txt", upload: "BB", restart: 2).Should().Be("226");
        File.ReadAllText(Path.Combine(_root, "log.txt")).Should().Be("AABB");

        raw.Transfer("APPE log.txt", upload: "CC").Should().Be("226");
        File.ReadAllText(Path.Combine(_root, "log.txt")).Should().Be("AABBCC");

        raw.TransferDownload("RETR log.txt", out var downloaded, restart: 3).Should().Be("226");
        downloaded.Should().Be("BCC");

        raw.Send("PASV").Should().StartWith("227");
        raw.Send("REST 99").Should().StartWith("350");
        raw.Send("RETR log.txt").Should().StartWith("554");
    }

    [Fact]
    public void MLSD_and_MLST_describe_entries_with_standard_facts()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "dados.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        using var raw = new RawFtp(_port);
        raw.Login();

        raw.Send("FEAT").Should().StartWith("211");
        raw.TransferDownload("MLSD", out var listing).Should().Be("226");
        listing.Should().MatchRegex(@"type=file;size=5;modify=\d{14};perm=rwadf; dados\.txt");
        listing.Should().MatchRegex(@"type=dir;size=0;modify=\d{14};perm=elcmfpd; pasta");

        raw.Send("MLST dados.txt").Should().Contain("type=file;size=5");
    }

    [Fact]
    public void Read_only_server_serves_files_but_refuses_every_change()
    {
        File.WriteAllText(Path.Combine(_root, "leitura.txt"), "so leitura");
        Start(readOnly: true);
        using var raw = new RawFtp(_port);
        raw.Login();

        raw.TransferDownload("RETR leitura.txt", out var content).Should().Be("226");
        content.Should().Be("so leitura");

        raw.Send("DELE leitura.txt").Should().StartWith("550");
        raw.Send("MKD nova").Should().StartWith("550");
        raw.Send("RMD qualquer").Should().StartWith("550");
        raw.Send("RNFR leitura.txt").Should().StartWith("550");
        raw.Send("PASV").Should().StartWith("227");
        raw.Send("STOR novo.txt").Should().StartWith("550");

        File.Exists(Path.Combine(_root, "leitura.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "novo.txt")).Should().BeFalse();
        raw.TransferDownload("MLSD", out var listing).Should().Be("226");
        listing.Should().Contain("perm=r;");
    }

    // ---- Conexões de dados ------------------------------------------------------------------

    [Fact]
    public void Passive_ports_stay_inside_the_configured_range()
    {
        var first = FreeTcpPort();
        var range = $"{first}-{first + 20}";
        Start(passivePorts: range);
        using var raw = new RawFtp(_port);
        raw.Login();

        for (var i = 0; i < 5; i++)
        {
            var reply = raw.Send("EPSV");
            reply.Should().StartWith("229");
            var port = int.Parse(Regex.Match(reply, @"\|\|\|(\d+)\|").Groups[1].Value, CultureInfo.InvariantCulture);
            port.Should().BeInRange(first, first + 20);
        }
    }

    [Fact]
    public void An_invalid_passive_range_is_refused_when_starting()
    {
        var config = Config(passivePorts: "abc");
        _service.TryStart(config, out var error).Should().BeFalse();
        error.Should().Contain("Portas passivas");

        _service.TryStart(Config(passivePorts: "80-90"), out error).Should().BeFalse();
        error.Should().Contain("1024");
    }

    [Fact]
    public void Active_mode_connects_back_only_to_the_client_on_a_high_port()
    {
        File.WriteAllText(Path.Combine(_root, "ativo.txt"), "x");
        Start();
        using var raw = new RawFtp(_port);
        raw.Login();

        // "FTP bounce": pedir que o servidor se conecte a outra máquina ou a uma porta baixa é recusado.
        raw.Send("PORT 10,0,0,5,200,10").Should().StartWith("504");
        raw.Send("PORT 127,0,0,1,0,80").Should().StartWith("504");
        raw.Send("EPRT |2|::1|50000|").Should().StartWith("522");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            raw.Send($"PORT 127,0,0,1,{port / 256},{port % 256}").Should().StartWith("200");
            raw.Send("LIST").Should().StartWith("150");

            using var data = listener.AcceptTcpClient();
            using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
            reader.ReadToEnd().Should().Contain("ativo.txt");
            raw.ReadLine().Should().StartWith("226");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- Segurança --------------------------------------------------------------------------

    [Fact]
    public void Explicit_ftps_protects_login_and_data_and_required_mode_refuses_plain_login()
    {
        File.WriteAllText(Path.Combine(_root, "secreto.txt"), "conteudo");
        Start(security: FtpServerSecurity.Required);

        using (var secure = Client(FtpEncryptionMode.Explicit))
        {
            secure.ValidateCertificate += (_, e) => e.Accept = true;
            secure.Connect();
            secure.IsEncrypted.Should().BeTrue();
            secure.GetListing("/").Select(i => i.Name).Should().Contain("secreto.txt");

            var local = Path.Combine(_work, "secreto.txt");
            secure.DownloadFile(local, "/secreto.txt");
            File.ReadAllText(local).Should().Be("conteudo");
        }

        using var plain = new RawFtp(_port);
        plain.Send("USER " + User).Should().StartWith("530");
        plain.Send("AUTH TLS").Should().StartWith("234");
    }

    [Fact]
    public void Optional_ftps_accepts_both_encrypted_and_plain_sessions()
    {
        Start(security: FtpServerSecurity.Optional);

        using (var plain = Client(FtpEncryptionMode.None))
        {
            plain.Connect();
            plain.IsEncrypted.Should().BeFalse();
        }

        using var secure = Client(FtpEncryptionMode.Explicit);
        secure.ValidateCertificate += (_, e) => e.Accept = true;
        secure.Connect();
        secure.IsEncrypted.Should().BeTrue();
    }

    [Fact]
    public void Disabled_ftps_does_not_advertise_tls()
    {
        Start(security: FtpServerSecurity.Disabled);
        using var raw = new RawFtp(_port);

        raw.Send("FEAT").Should().NotContain("AUTH TLS");
        raw.Send("AUTH TLS").Should().StartWith("504");
    }

    [Fact]
    public void The_ftps_certificate_is_created_once_and_reused()
    {
        var path = Path.Combine(_work, "reuso.bin");
        using var first = FtpTlsCertificate.GetOrCreate(path);
        using var second = FtpTlsCertificate.GetOrCreate(path);

        File.Exists(path).Should().BeTrue();
        second.Thumbprint.Should().Be(first.Thumbprint);
        // Um blob DPAPI começa com a versão 1; um PFX ou PEM em texto claro começaria com 0x30 ou "-----".
        File.ReadAllBytes(path).Take(4).Should().Equal(new byte[] { 1, 0, 0, 0 }, "o arquivo guarda o PFX protegido pelo Windows");
    }

    [Fact]
    public void Repeated_login_failures_block_the_address()
    {
        Start();

        for (var session = 0; session < 2; session++)
        {
            using var raw = new RawFtp(_port);
            raw.Send("USER " + User).Should().StartWith("331");
            raw.Send("PASS errada1").Should().StartWith("530");
            raw.Send("PASS errada2").Should().StartWith("530");
            raw.Send("PASS errada3").Should().StartWith("530");
        }

        using var blocked = new RawFtp(_port, expectGreeting: false);
        blocked.ReadLine().Should().StartWith("421");
    }

    [Fact]
    public void Links_that_point_outside_the_shared_folder_are_not_followed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_work, "fora")).FullName;
        File.WriteAllText(Path.Combine(outside, "segredo.txt"), "nao deve vazar");
        var link = Path.Combine(_root, "atalho");
        if (!TryCreateJunction(link, outside))
        {
            return; // sem permissão para criar a junção neste ambiente
        }

        Start();
        using var raw = new RawFtp(_port);
        raw.Login();

        raw.Send("SIZE atalho/segredo.txt").Should().StartWith("550");
        raw.Send("CWD atalho").Should().StartWith("550");
        raw.TransferDownload("MLSD", out var listing).Should().Be("226");
        listing.Should().NotContain("atalho");
    }

    [Fact]
    public void Names_that_could_escape_or_hide_streams_are_refused()
    {
        Start();
        using var raw = new RawFtp(_port);
        raw.Login();

        // ".." não sai da raiz: o nome é interpretado dentro da pasta compartilhada.
        raw.Send("MKD ..\\fora").Should().StartWith("257");
        Directory.Exists(Path.Combine(_root, "fora")).Should().BeTrue();
        Directory.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "fora")).Should().BeFalse();

        raw.Send("MKD nome.").Should().StartWith("553");
        raw.Send("SIZE a.txt::$DATA").Should().StartWith("550");
    }

    // ---- Apoio ------------------------------------------------------------------------------

    private NetworkConnectionInfo Config(
        bool readOnly = false,
        FtpServerSecurity security = FtpServerSecurity.Optional,
        string? passivePorts = null)
    {
        _port = FreeTcpPort();
        return new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Ftp,
            Host = "127.0.0.1",
            Port = _port,
            Username = User,
            Password = Password,
            RemotePath = _root,
            ReadOnly = readOnly,
            ServerSecurity = security,
            PassivePorts = passivePorts
        };
    }

    private void Start(
        bool readOnly = false,
        FtpServerSecurity security = FtpServerSecurity.Disabled,
        string? passivePorts = null)
    {
        _service.TryStart(Config(readOnly, security, passivePorts), out var error).Should().BeTrue(error);
    }

    private FtpClient Client(FtpEncryptionMode mode)
    {
        var client = new FtpClient("127.0.0.1", User, Password, _port);
        client.Config.EncryptionMode = mode;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.ConnectTimeout = 10_000;
        client.Config.ReadTimeout = 10_000;
        return client;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Cliente FTP mínimo, em protocolo puro, para provar respostas exatas do servidor.</summary>
    private sealed class RawFtp : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public RawFtp(int port, bool expectGreeting = true)
        {
            _client.Connect(IPAddress.Loopback, port);
            var stream = _client.GetStream();
            stream.ReadTimeout = 15_000;
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            if (expectGreeting)
            {
                ReadLine().Should().StartWith("220");
            }
        }

        public void Dispose() => _client.Dispose();

        public string ReadLine() => _reader.ReadLine() ?? "<closed>";

        /// <summary>Envia um comando e devolve a resposta (várias linhas, se for "xxx-").</summary>
        public string Send(string command)
        {
            _writer.WriteLine(command);
            var first = ReadLine();
            if (first.Length < 4 || first[3] != '-')
            {
                return first;
            }

            var all = new StringBuilder(first);
            string line;
            do
            {
                line = ReadLine();
                all.Append('\n').Append(line);
            }
            while (!(line.Length >= 4 && line[..3] == first[..3] && line[3] == ' '));
            return all.ToString();
        }

        public void Login()
        {
            Send("USER " + User).Should().StartWith("331");
            Send("PASS " + Password).Should().StartWith("230");
        }

        private (TcpClient Data, int Port) OpenPassive()
        {
            var reply = Send("PASV");
            reply.Should().StartWith("227");
            var match = Regex.Match(reply, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
            var port = (int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) * 256) +
                       int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
            var data = new TcpClient();
            data.Connect(IPAddress.Loopback, port);
            data.GetStream().ReadTimeout = 15_000;
            return (data, port);
        }

        /// <summary>Envia o arquivo por uma conexão passiva; devolve o código final (226).</summary>
        public string Transfer(string command, string upload, long? restart = null)
        {
            var reply = Send("PASV");
            var match = Regex.Match(reply, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
            var port = (int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) * 256) +
                       int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);

            // REST vem logo antes do comando de transferência (depois do PASV), como nos clientes reais.
            if (restart is { } offset)
            {
                Send($"REST {offset}").Should().StartWith("350");
            }

            _writer.WriteLine(command);
            ReadLine().Should().StartWith("150");
            using (var data = new TcpClient())
            {
                data.Connect(IPAddress.Loopback, port);
                var bytes = Encoding.UTF8.GetBytes(upload);
                data.GetStream().Write(bytes);
            }

            return ReadLine()[..3];
        }

        /// <summary>Baixa (RETR/LIST/MLSD) por uma conexão passiva; devolve o código final e o conteúdo.</summary>
        public string TransferDownload(string command, out string content, long? restart = null)
        {
            var (data, _) = OpenPassive();
            using (data)
            {
                if (restart is { } offset)
                {
                    Send($"REST {offset}").Should().StartWith("350");
                }

                _writer.WriteLine(command);
                var first = ReadLine();
                first.Should().StartWith("150");
                using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
                content = reader.ReadToEnd();
            }

            return ReadLine()[..3];
        }
    }
}

public sealed class TftpServerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-tftp-opt-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-tftp-work-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerLogStore _logs = new();
    private readonly NetworkServerService _service;
    private int _port;

    public TftpServerTests()
    {
        _service = new NetworkServerService(_logs);
    }

    public void Dispose()
    {
        _service.Dispose();
        FileOperationTests.DeleteQuietly(_root);
        FileOperationTests.DeleteQuietly(_work);
    }

    [Fact]
    public void Options_blksize_and_tsize_are_negotiated_with_an_OACK()
    {
        File.WriteAllBytes(Path.Combine(_root, "grande.bin"), new byte[3000]);
        Start();
        using var udp = Udp();

        udp.Send(Request(1, "grande.bin", ("blksize", "1024"), ("tsize", "0")), new IPEndPoint(IPAddress.Loopback, _port));
        var oack = Receive(udp);

        oack.Buffer[1].Should().Be(6);
        var options = ParseOptions(oack.Buffer);
        options["blksize"].Should().Be("1024");
        options["tsize"].Should().Be("3000");

        udp.Send([0, 4, 0, 0], oack.RemoteEndPoint);
        var data1 = Receive(udp);
        data1.Buffer[1].Should().Be(3);
        data1.Buffer.Length.Should().Be(4 + 1024);
    }

    [Theory]
    [InlineData("100000", "65464")] // acima do máximo: limitado
    [InlineData("1428", "1428")]
    public void Block_size_is_limited_to_what_the_protocol_allows(string requested, string expected)
    {
        File.WriteAllBytes(Path.Combine(_root, "x.bin"), new byte[10]);
        Start();
        using var udp = Udp();

        udp.Send(Request(1, "x.bin", ("blksize", requested)), new IPEndPoint(IPAddress.Loopback, _port));

        ParseOptions(Receive(udp).Buffer)["blksize"].Should().Be(expected);
    }

    [Fact]
    public void Unknown_options_are_ignored_and_the_transfer_uses_512_byte_blocks()
    {
        File.WriteAllBytes(Path.Combine(_root, "f.bin"), new byte[700]);
        Start();
        using var udp = Udp();

        udp.Send(Request(1, "f.bin", ("windowsize", "8"), ("opcao-inventada", "1")), new IPEndPoint(IPAddress.Loopback, _port));
        var data1 = Receive(udp);

        data1.Buffer[1].Should().Be(3, "sem opções aceitas não há OACK");
        data1.Buffer.Length.Should().Be(4 + 512);
    }

    [Fact]
    public void A_lost_ack_makes_the_server_retransmit_the_same_block()
    {
        File.WriteAllBytes(Path.Combine(_root, "r.bin"), new byte[600]);
        Start();
        using var udp = Udp();

        udp.Send(Request(1, "r.bin", ("timeout", "1")), new IPEndPoint(IPAddress.Loopback, _port));
        var oack = Receive(udp);
        oack.Buffer[1].Should().Be(6);

        // Sem responder, o OACK é retransmitido depois do tempo limite negociado (1 s).
        var again = Receive(udp, TimeSpan.FromSeconds(3));
        again.Buffer.Should().Equal(oack.Buffer);

        udp.Send([0, 4, 0, 0], oack.RemoteEndPoint);
        var first = Receive(udp);
        var repeated = Receive(udp, TimeSpan.FromSeconds(3));
        repeated.Buffer.Should().Equal(first.Buffer, "o ACK do bloco 1 não chegou");
    }

    [Fact]
    public void Packets_from_another_port_get_error_5_without_breaking_the_transfer()
    {
        File.WriteAllBytes(Path.Combine(_root, "t.bin"), new byte[600]);
        Start();
        using var udp = Udp();
        using var intruder = Udp();

        udp.Send(Request(1, "t.bin"), new IPEndPoint(IPAddress.Loopback, _port));
        var data1 = Receive(udp);

        intruder.Send([0, 4, 0, 1], data1.RemoteEndPoint);
        Receive(intruder).Buffer[1].Should().Be(5);

        udp.Send([0, 4, 0, 1], data1.RemoteEndPoint);
        var data2 = Receive(udp);
        data2.Buffer[1].Should().Be(3);
        data2.Buffer[3].Should().Be(2);
    }

    [Fact]
    public void Read_only_mode_refuses_writes_with_an_access_violation()
    {
        Start(readOnly: true);
        using var udp = Udp();

        udp.Send(Request(2, "novo.txt"), new IPEndPoint(IPAddress.Loopback, _port));
        var reply = Receive(udp);

        reply.Buffer[1].Should().Be(5);
        reply.Buffer[3].Should().Be(2);
        File.Exists(Path.Combine(_root, "novo.txt")).Should().BeFalse();
        _logs.GetText(NetworkProtocol.Tftp).Should().Contain("somente leitura");
    }

    [Fact]
    public void An_upload_declaring_more_than_the_limit_is_refused_up_front()
    {
        Start();
        using var udp = Udp();

        udp.Send(Request(2, "enorme.bin", ("tsize", "999999999999")), new IPEndPoint(IPAddress.Loopback, _port));
        var reply = Receive(udp);

        reply.Buffer[1].Should().Be(5);
        reply.Buffer[3].Should().Be(3);
    }

    [Fact]
    public void Unsupported_transfer_modes_and_malformed_requests_are_rejected()
    {
        Start();
        using var udp = Udp();
        var server = new IPEndPoint(IPAddress.Loopback, _port);

        udp.Send(RequestWithMode(1, "a.txt", "mail"), server);
        Receive(udp).Buffer[3].Should().Be(4);

        udp.Send([0, 1, (byte)'a', 0], server); // sem o campo de modo
        Receive(udp).Buffer[3].Should().Be(4);
    }

    [Fact]
    public void Our_client_negotiates_larger_blocks_and_round_trips_files()
    {
        Start();
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        var local = Path.Combine(_work, "ida.bin");
        File.WriteAllBytes(local, content);
        var connection = Transfer("ida.bin", local, upload: true);

        NetworkFileService.ExecuteTftpTransfer(connection);
        File.ReadAllBytes(Path.Combine(_root, "ida.bin")).Should().Equal(content);

        var back = Path.Combine(_work, "volta.bin");
        NetworkFileService.ExecuteTftpTransfer(Transfer("ida.bin", back, upload: false));
        File.ReadAllBytes(back).Should().Equal(content);

        _logs.GetText(NetworkProtocol.Tftp).Should().Contain("blocos de 1428");
    }

    [Fact]
    public void Block_numbers_wrap_around_after_65535_for_big_files()
    {
        Start();
        // 8 bytes por bloco (o mínimo): 70 000 blocos passam do limite de 16 bits.
        var content = Enumerable.Range(0, 8 * 70_000).Select(i => (byte)(i % 253)).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "rollover.bin"), content);
        var back = Path.Combine(_work, "rollover.bin");

        NetworkFileService.ExecuteTftpTransfer(
            Transfer("rollover.bin", back, upload: false),
            new TftpClientOptions(TimeSpan.FromSeconds(5), 3, BlockSize: 8));

        File.ReadAllBytes(back).Should().Equal(content);
    }

    [Fact]
    public void Our_client_falls_back_when_the_server_ignores_options()
    {
        using var fake = new FakeSimpleTftpServer(option8: false);
        var local = Path.Combine(_work, "simples.bin");

        NetworkFileService.ExecuteTftpTransfer(Transfer("qualquer", local, upload: false, fake.Port));

        File.ReadAllBytes(local).Should().Equal(fake.Content);
    }

    [Fact]
    public void Our_client_retries_without_options_after_error_8()
    {
        using var fake = new FakeSimpleTftpServer(option8: true);
        var local = Path.Combine(_work, "erro8.bin");

        NetworkFileService.ExecuteTftpTransfer(Transfer("qualquer", local, upload: false, fake.Port));

        File.ReadAllBytes(local).Should().Equal(fake.Content);
        fake.RequestsReceived.Should().Be(2);
    }

    // ---- Apoio ------------------------------------------------------------------------------

    private void Start(bool readOnly = false)
    {
        _port = FreeUdpPort();
        _service.TryStart(
            new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Tftp,
                Host = "127.0.0.1",
                Port = _port,
                RemotePath = _root,
                ReadOnly = readOnly
            },
            out var error).Should().BeTrue(error);
    }

    private NetworkConnectionInfo Transfer(string remote, string local, bool upload, int? port = null) =>
        new()
        {
            Protocol = NetworkProtocol.Tftp,
            Host = "127.0.0.1",
            Port = port ?? _port,
            RemoteFilePath = remote,
            LocalFilePath = local,
            IsUpload = upload
        };

    private static UdpClient Udp() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static UdpReceiveResult Receive(UdpClient client, TimeSpan? timeout = null)
    {
        var task = client.ReceiveAsync();
        if (!task.Wait(timeout ?? TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("O servidor TFTP não respondeu a tempo.");
        }

        return task.Result;
    }

    private static byte[] Request(byte opcode, string file, params (string Name, string Value)[] options) =>
        RequestWithMode(opcode, file, "octet", options);

    private static byte[] RequestWithMode(byte opcode, string file, string mode, params (string Name, string Value)[] options)
    {
        var bytes = new List<byte> { 0, opcode };
        foreach (var field in new[] { file, mode }.Concat(options.SelectMany(o => new[] { o.Name, o.Value })))
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(field));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static Dictionary<string, string> ParseOptions(byte[] oack)
    {
        var fields = Encoding.ASCII.GetString(oack, 2, oack.Length - 2).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            result[fields[i]] = fields[i + 1];
        }

        return result;
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Servidor TFTP antigo: ignora opções (devolve DATA direto) ou, em modo "erro 8", recusa o primeiro
    /// pedido que traga opções e só atende o pedido simples.
    /// </summary>
    private sealed class FakeSimpleTftpServer : IDisposable
    {
        private readonly UdpClient _listener = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly bool _option8;
        private readonly CancellationTokenSource _cts = new();

        public FakeSimpleTftpServer(bool option8)
        {
            _option8 = option8;
            Content = Enumerable.Range(0, 1300).Select(i => (byte)(i % 200)).ToArray();
            Port = ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;
            _ = Task.Run(RunAsync);
        }

        public int Port { get; }

        public byte[] Content { get; }

        public int RequestsReceived { get; private set; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var request = await _listener.ReceiveAsync(_cts.Token);
                    RequestsReceived++;
                    var hasOptions = Encoding.ASCII.GetString(request.Buffer, 2, request.Buffer.Length - 2)
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries).Length > 2;

                    using var transfer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    if (_option8 && hasOptions)
                    {
                        var error = new byte[] { 0, 5, 0, 8, (byte)'n', (byte)'o', 0 };
                        await transfer.SendAsync(error, error.Length, request.RemoteEndPoint);
                        continue;
                    }

                    for (var block = 1; (block - 1) * 512 <= Content.Length; block++)
                    {
                        var chunk = Content.Skip((block - 1) * 512).Take(512).ToArray();
                        var packet = new byte[] { 0, 3, (byte)(block >> 8), (byte)block }.Concat(chunk).ToArray();
                        await transfer.SendAsync(packet, packet.Length, request.RemoteEndPoint);
                        _ = await transfer.ReceiveAsync(_cts.Token); // ACK
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // teste terminou
            }
        }
    }
}
