using System.Net;
using System.Net.Sockets;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class TftpClientTests : IDisposable
{
    private static readonly TftpClientOptions Fast = new(TimeSpan.FromMilliseconds(400), 1);

    private readonly string _serverRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-tftp-srv-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _clientDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-tftp-cli-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerService _server = new(new NetworkServerLogStore());
    private int _port;

    public void Dispose()
    {
        _server.Dispose();
        FileOperationTests.DeleteQuietly(_serverRoot);
        FileOperationTests.DeleteQuietly(_clientDir);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1024)]
    [InlineData(5000)]
    public void Download_returns_the_exact_bytes_for_sizes_around_the_block_boundary(int size)
    {
        StartServer();
        var expected = Pattern(size);
        File.WriteAllBytes(Path.Combine(_serverRoot, "data.bin"), expected);
        var local = Path.Combine(_clientDir, "downloaded.bin");

        NetworkFileService.ExecuteTftpTransfer(Transfer("data.bin", local, upload: false));

        File.ReadAllBytes(local).Should().Equal(expected);
        Directory.GetFiles(_clientDir, "*.dora-part-*").Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2000)]
    public void Upload_stores_the_exact_bytes_on_the_server(int size)
    {
        StartServer();
        var content = Pattern(size);
        var local = Path.Combine(_clientDir, "to-send.bin");
        File.WriteAllBytes(local, content);

        NetworkFileService.ExecuteTftpTransfer(Transfer("sent.bin", local, upload: true));

        var stored = Path.Combine(_serverRoot, "sent.bin");
        WaitFor(() => File.Exists(stored) && new FileInfo(stored).Length == size).Should().BeTrue();
        File.ReadAllBytes(stored).Should().Equal(content);
    }

    [Fact]
    public void Download_of_a_missing_file_reports_the_server_error_quickly()
    {
        StartServer();
        var local = Path.Combine(_clientDir, "never.bin");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => NetworkFileService.ExecuteTftpTransfer(Transfer("nao-existe.bin", local, upload: false));

        act.Should().Throw<IOException>().WithMessage("*Erro TFTP 1*");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Failed_download_keeps_the_existing_local_file()
    {
        StartServer();
        var local = Path.Combine(_clientDir, "precious.bin");
        File.WriteAllText(local, "importante");

        var act = () => NetworkFileService.ExecuteTftpTransfer(Transfer("nao-existe.bin", local, upload: false));

        act.Should().Throw<IOException>();
        File.ReadAllText(local).Should().Be("importante");
        Directory.GetFiles(_clientDir, "*.dora-part-*").Should().BeEmpty();
    }

    [Fact]
    public void Probe_succeeds_against_a_running_server_and_fails_against_a_closed_port()
    {
        StartServer();
        var alive = () => TftpClient.Probe("127.0.0.1", _port, Fast);
        alive.Should().NotThrow();

        var closed = () => TftpClient.Probe("127.0.0.1", FreeUdpPort(), Fast);
        closed.Should().Throw<Exception>().Where(e => e is IOException || e is TimeoutException);
    }

    [Fact]
    public void Transfer_to_a_silent_server_times_out_instead_of_hanging()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var local = Path.Combine(_clientDir, "silent.bin");
        var connection = Transfer("x.bin", local, upload: false, port);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => NetworkFileService.ExecuteTftpTransfer(connection, Fast);

        act.Should().Throw<TimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cancellation_stops_a_transfer_that_is_waiting_for_the_server()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var connection = Transfer("x.bin", Path.Combine(_clientDir, "cancel.bin"), upload: false, port);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => NetworkFileService.ExecuteTftpTransfer(connection, TftpClientOptions.Default, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    private void StartServer()
    {
        _port = FreeUdpPort();
        var config = new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Tftp,
            Host = "127.0.0.1",
            Port = _port,
            RemotePath = _serverRoot
        };
        _server.TryStart(config, out var error).Should().BeTrue(error);
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

    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        return data;
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
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
}
