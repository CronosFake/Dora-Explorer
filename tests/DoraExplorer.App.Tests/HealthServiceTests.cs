using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class HealthServiceTests
{
    private readonly FakeNetworkFileService _files = new();
    private bool _reachable = true;

    [Fact]
    public async Task Unreachable_server_is_unhealthy_without_any_login_attempt()
    {
        _reachable = false;

        var (healthy, reason) = await Service().CheckProfileAsync(Profile(), CancellationToken.None);

        healthy.Should().BeFalse();
        reason.Should().Contain("inacessível");
        _files.LoginAttempts.Should().Be(0, "nenhuma credencial deve ser enviada a um servidor que nem atende");
    }

    [Fact]
    public async Task Reachable_server_with_a_valid_login_is_healthy()
    {
        var (healthy, _) = await Service().CheckProfileAsync(Profile(), CancellationToken.None);

        healthy.Should().BeTrue();
        _files.LoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Background_login_stops_after_repeated_failures_to_avoid_locking_accounts()
    {
        _files.LoginSucceeds = false;
        var service = Service();
        var profile = Profile();

        for (var cycle = 0; cycle < 5; cycle++)
        {
            (await service.CheckProfileAsync(profile, CancellationToken.None)).Healthy.Should().BeFalse();
        }

        _files.LoginAttempts.Should().Be(2, "depois de 2 falhas seguidas o login automático é suspenso");
    }

    [Fact]
    public async Task Changing_the_credentials_resumes_the_automatic_check()
    {
        _files.LoginSucceeds = false;
        var service = Service();
        var profile = Profile();
        await service.CheckProfileAsync(profile, CancellationToken.None);
        await service.CheckProfileAsync(profile, CancellationToken.None);
        await service.CheckProfileAsync(profile, CancellationToken.None);
        _files.LoginAttempts.Should().Be(2);

        profile.Password = "nova-senha";
        _files.LoginSucceeds = true;
        var (healthy, _) = await service.CheckProfileAsync(profile, CancellationToken.None);

        healthy.Should().BeTrue();
        _files.LoginAttempts.Should().Be(3);
    }

    [Fact]
    public async Task A_successful_manual_test_resumes_the_automatic_check()
    {
        _files.LoginSucceeds = false;
        var service = Service();
        var profile = Profile();
        await service.CheckProfileAsync(profile, CancellationToken.None);
        await service.CheckProfileAsync(profile, CancellationToken.None);

        _files.LoginSucceeds = true;
        (await service.TestAndUpdateAsync(profile)).Should().BeTrue();
        service.GetHealth(profile.Id).Should().BeTrue();

        _files.LoginSucceeds = false;
        await service.CheckProfileAsync(profile, CancellationToken.None);
        _files.LoginAttempts.Should().Be(4, "a contagem recomeçou depois do teste manual");
    }

    [Fact]
    public async Task Tftp_profiles_have_no_passive_check()
    {
        var profile = Profile();
        profile.Protocol = NetworkProtocol.Tftp;

        var (healthy, _) = await Service().CheckProfileAsync(profile, CancellationToken.None);

        healthy.Should().BeNull();
        _files.LoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Default_probe_reports_a_closed_port_as_unreachable_without_logging_in()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var profile = Profile();
        profile.Host = "127.0.0.1";
        profile.Port = closedPort;

        var (healthy, reason) = await new NetworkClientHealthService(_files).CheckProfileAsync(profile, CancellationToken.None);

        healthy.Should().BeFalse();
        reason.Should().Contain("inacessível");
        _files.LoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Default_probe_sees_a_listening_port_and_then_checks_the_login()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var profile = Profile();
            profile.Host = "127.0.0.1";
            profile.Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            var (healthy, _) = await new NetworkClientHealthService(_files).CheckProfileAsync(profile, CancellationToken.None);

            healthy.Should().BeTrue();
            _files.LoginAttempts.Should().Be(1);
        }
        finally
        {
            listener.Stop();
        }
    }

    private NetworkClientHealthService Service() =>
        new(_files, (_, _) => Task.FromResult(_reachable));

    private static NetworkClientProfile Profile() =>
        new()
        {
            Id = "perfil-1",
            Protocol = NetworkProtocol.Sftp,
            DisplayName = "switch",
            Host = "10.0.0.5",
            Port = 22,
            Username = "admin",
            Password = "senha"
        };

    private sealed class FakeNetworkFileService : INetworkFileService
    {
        private int _loginAttempts;

        public bool LoginSucceeds { get; set; } = true;

        public int LoginAttempts => _loginAttempts;

        public Task<(bool Success, string? ErrorMessage)> TestConnectionAsync(
            NetworkConnectionInfo connection,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loginAttempts);
            return Task.FromResult<(bool, string?)>((LoginSucceeds, LoginSucceeds ? null : "login recusado"));
        }

        public bool TryConnectAndList(NetworkConnectionInfo connection, out IReadOnlyList<FileSystemEntry> entries, out string? errorMessage) =>
            throw new NotSupportedException();

        public Task<(bool Success, IReadOnlyList<FileSystemEntry> Entries, string? ErrorMessage)> ConnectAndListAsync(
            NetworkConnectionInfo connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryExecuteTransfer(NetworkConnectionInfo connection, out string? errorMessage) =>
            throw new NotSupportedException();

        public Task<(bool Success, string? ErrorMessage)> ExecuteTransferAsync(
            NetworkConnectionInfo connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
