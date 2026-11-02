using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Testes com um processo de verdade escutando numa porta não rodam em paralelo com nenhum outro.</summary>
[CollectionDefinition("External processes", DisableParallelization = true)]
public sealed class ExternalProcessesGroup;

/// <summary>
/// Antes de iniciar um servidor o app confere se a porta está livre e quem a ocupa. O que importa: nunca tratar uma porta
/// do próprio app como "de outro processo" (senão parar o servidor pareceria impossível) e reconhecer de verdade quando é
/// outro processo (para nunca encerrá-lo).
/// Aqui há um processo de verdade escutando, e o app recusa iniciar servidor em porta ocupada por outro processo, em qualquer
/// protocolo. Por isso estes testes têm coleção própria (sem paralelismo) e usam portas numa faixa que os outros testes não
/// usam: quando rodavam juntos, a porta que outro teste acabara de achar livre era tomada por este processo e o servidor do
/// outro teste não subia ("A porta N já está em uso por outro processo").
/// </summary>
[Collection("External processes")]
public sealed class NetworkPortGuardTests
{
    /// <summary>Uma porta livre em TCP e UDP, numa faixa que os outros testes (portas dadas pelo sistema, de 49152 em diante) não usam.</summary>
    private static int FreeTcpPort()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(20000, 30000);
            try
            {
                using var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                // ocupada: tenta outra
            }
        }

        throw new InvalidOperationException("Nenhuma porta livre na faixa de teste.");
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Ports_outside_the_valid_range_are_never_reported_as_used_by_another_process(int port) =>
        NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeFalse();

    [Fact]
    public void A_port_nobody_listens_on_is_free()
    {
        var port = FreeTcpPort();

        NetworkPortGuard.IsPortListening(port).Should().BeFalse();
        NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeFalse();
    }

    [Fact]
    public void A_port_this_process_listens_on_is_listening_but_not_used_by_another_process()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            NetworkPortGuard.IsPortListening(port).Should().BeTrue();
            NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeFalse("o dono é o próprio app");
        }
        finally
        {
            listener.Stop();
        }

        WaitFor(() => !NetworkPortGuard.IsPortListening(port), TimeSpan.FromSeconds(5))
            .Should().BeTrue("a porta volta a ficar livre quando o servidor para");
    }

    [Fact]
    public void A_udp_port_this_process_uses_is_listening()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        NetworkPortGuard.IsPortListening(port).Should().BeTrue();
        NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeFalse();
    }

    [Fact]
    public void A_port_another_process_listens_on_is_reported_as_used_by_another_process()
    {
        var port = FreeTcpPort();
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {port}); $l.Start(); Start-Sleep -Seconds 60");

        using var child = Process.Start(start)!;
        try
        {
            WaitFor(() => NetworkPortGuard.IsPortListening(port), TimeSpan.FromSeconds(20))
                .Should().BeTrue("o processo de teste precisa estar escutando para o resto valer");

            NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeTrue();
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
        }
    }

    [Fact]
    public void Cleaning_up_at_startup_never_ends_other_processes()
    {
        var port = FreeTcpPort();
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {port}); $l.Start(); Start-Sleep -Seconds 60");

        using var child = Process.Start(start)!;
        try
        {
            WaitFor(() => NetworkPortGuard.IsPortListening(port), TimeSpan.FromSeconds(20)).Should().BeTrue();

            NetworkPortGuard.ForceStopOwnedListeners([port, port]);

            child.HasExited.Should().BeFalse("a limpeza da inicialização nunca encerra processo de terceiros");
            NetworkPortGuard.IsPortListening(port).Should().BeTrue();
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
        }
    }

    // ---- o servidor só se importa com o transporte dele ----------------------------------------------------------------

    private const string HoldTcpScript =
        "$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {0}); $l.Start(); Start-Sleep -Seconds 60";

    private const string HoldUdpScript =
        "$u = New-Object System.Net.Sockets.UdpClient([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, {0})); Start-Sleep -Seconds 60";

    private static Process StartHolder(string script, int port)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, script, port));
        var child = Process.Start(start)!;
        WaitFor(() => NetworkPortGuard.IsPortListening(port), TimeSpan.FromSeconds(20))
            .Should().BeTrue("o processo de teste precisa estar com a porta aberta para o resto valer");
        return child;
    }

    private static void Stop(Process child)
    {
        child.Kill(entireProcessTree: true);
        child.WaitForExit(5000);
        child.Dispose();
    }

    [Fact]
    public void A_udp_socket_of_another_process_does_not_block_a_tcp_server_on_the_same_number()
    {
        var port = FreeTcpPort();
        var holder = StartHolder(HoldUdpScript, port);
        try
        {
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Ftp).Should().BeFalse("FTP usa TCP");
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Sftp).Should().BeFalse("SFTP usa TCP");
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Scp).Should().BeFalse("SCP usa TCP");
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Tftp).Should().BeTrue("TFTP usa UDP, e a porta é do outro processo");
            NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeTrue("sem dizer o protocolo vale qualquer um");
        }
        finally
        {
            Stop(holder);
        }
    }

    [Fact]
    public void A_tcp_listener_of_another_process_does_not_block_a_tftp_server_on_the_same_number()
    {
        var port = FreeTcpPort();
        var holder = StartHolder(HoldTcpScript, port);
        try
        {
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Tftp).Should().BeFalse("TFTP usa UDP");
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Ftp).Should().BeTrue("FTP usa TCP, e a porta é do outro processo");
            NetworkPortGuard.IsPortUsedByOtherProcess(port, NetworkProtocol.Sftp).Should().BeTrue();
            NetworkPortGuard.IsPortUsedByOtherProcess(port).Should().BeTrue();
        }
        finally
        {
            Stop(holder);
        }
    }

    [Fact]
    public void An_ftp_server_starts_even_when_another_process_holds_the_same_number_on_udp()
    {
        var port = FreeTcpPort();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dora-portguard-" + Guid.NewGuid().ToString("N"))).FullName;
        var holder = StartHolder(HoldUdpScript, port);
        using var service = new NetworkServerService(new NetworkServerLogStore());
        try
        {
            var config = new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Ftp,
                Host = "127.0.0.1",
                Port = port,
                Username = "usuario",
                Password = "senha",
                RemotePath = root,
                ServerSecurity = FtpServerSecurity.Disabled
            };

            service.TryStart(config, out var error).Should().BeTrue(error);
        }
        finally
        {
            Stop(holder);
            service.StopAll();
            FileOperationTests.DeleteQuietly(root);
        }
    }

    [Fact]
    public void A_tftp_server_starts_even_when_another_process_holds_the_same_number_on_tcp()
    {
        var port = FreeTcpPort();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dora-portguard-" + Guid.NewGuid().ToString("N"))).FullName;
        var holder = StartHolder(HoldTcpScript, port);
        using var service = new NetworkServerService(new NetworkServerLogStore());
        try
        {
            var config = new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Tftp,
                Host = "127.0.0.1",
                Port = port,
                RemotePath = root
            };

            service.TryStart(config, out var error).Should().BeTrue(error);
        }
        finally
        {
            Stop(holder);
            service.StopAll();
            FileOperationTests.DeleteQuietly(root);
        }
    }

    [Fact]
    public void A_server_is_still_refused_when_another_process_holds_the_port_in_its_own_transport()
    {
        var port = FreeTcpPort();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dora-portguard-" + Guid.NewGuid().ToString("N"))).FullName;
        var holder = StartHolder(HoldTcpScript, port);
        using var service = new NetworkServerService(new NetworkServerLogStore());
        try
        {
            var config = new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Ftp,
                Host = "127.0.0.1",
                Port = port,
                Username = "usuario",
                Password = "senha",
                RemotePath = root,
                ServerSecurity = FtpServerSecurity.Disabled
            };

            service.TryStart(config, out var error).Should().BeFalse();
            error.Should().Contain(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Stop(holder);
            service.StopAll();
            FileOperationTests.DeleteQuietly(root);
        }
    }
}
