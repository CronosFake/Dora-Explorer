using System.Reflection;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Testes que trocam o <c>App.SettingsService</c> estático não rodam em paralelo com outros.</summary>
[CollectionDefinition("App statics", DisableParallelization = true)]
public sealed class AppStaticsGroup;

/// <summary>
/// A pasta "Rede" da árvore agrupa os clientes FTP, SFTP, SCP e TFTP. Os perfis salvos aparecem dentro do protocolo
/// de cada um, e protocolos ocultos nas opções do painel somem. O teste usa um arquivo de configurações temporário
/// no lugar do <c>App.SettingsService</c> (que só existe com o app rodando).
/// </summary>
[Collection("App statics")]
public sealed class NetworkGroupTests : IDisposable
{
    private static readonly PropertyInfo SettingsProperty = typeof(App).GetProperty(nameof(App.SettingsService))!;

    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-netgroup-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly AppSettingsService _settings;
    private readonly object? _previous = SettingsProperty.GetValue(null);

    public NetworkGroupTests()
    {
        _settings = new AppSettingsService(Path.Combine(_directory, "settings.json"));
        SettingsProperty.GetSetMethod(nonPublic: true)!.Invoke(null, [_settings]);
    }

    public void Dispose()
    {
        SettingsProperty.GetSetMethod(nonPublic: true)!.Invoke(null, [_previous]);
        SshTestHelpers.DeleteDirectory(_directory);
    }

    private static PaneDisplayOptions Only(bool ftp = false, bool sftp = false, bool scp = false, bool tftp = false) =>
        new() { ShowFtp = ftp, ShowSftp = sftp, ShowScp = scp, ShowTftp = tftp };

    private void SaveProfile(NetworkProtocol protocol, string name) =>
        _settings.SaveNetworkClientProfile(new NetworkClientProfile { Protocol = protocol, DisplayName = name, Host = "servidor.local" });

    [Fact]
    public void The_network_group_holds_the_four_protocols_in_order()
    {
        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions());

        group.Should().NotBeNull();
        group!.Name.Should().Be("Rede");
        group.Kind.Should().Be(NavigationNodeKind.NetworkGroup);
        group.Children.Select(c => (c.Name, c.Kind)).Should().Equal(
            ("FTP", NavigationNodeKind.Ftp),
            ("SFTP", NavigationNodeKind.Sftp),
            ("SCP", NavigationNodeKind.Scp),
            ("TFTP", NavigationNodeKind.Tftp));
    }

    [Fact]
    public void Protocols_hidden_in_the_pane_options_do_not_appear()
    {
        var group = NavigationTreeService.CreateNetworkGroup(Only(sftp: true, scp: true));

        group!.Children.Select(c => c.Name).Should().Equal("SFTP", "SCP");
    }

    [Fact]
    public void With_every_protocol_hidden_there_is_no_network_group()
    {
        NavigationTreeService.CreateNetworkGroup(Only()).Should().BeNull();
    }

    [Fact]
    public void Saved_profiles_appear_under_their_own_protocol_sorted_by_name()
    {
        SaveProfile(NetworkProtocol.Ftp, "zeta");
        SaveProfile(NetworkProtocol.Ftp, "Alfa");
        SaveProfile(NetworkProtocol.Sftp, "backup");

        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        group.Children.Single(c => c.Name == "FTP").Children.Select(c => c.Name).Should().Equal("Alfa", "zeta");
        group.Children.Single(c => c.Name == "SFTP").Children.Select(c => c.Name).Should().Equal("backup");
        group.Children.Single(c => c.Name == "SCP").Children.Should().BeEmpty();
        group.Children.Single(c => c.Name == "TFTP").Children.Should().BeEmpty();
    }

    [Fact]
    public void The_group_and_its_protocols_are_already_loaded_so_they_never_wait_for_a_listing()
    {
        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        group.ChildrenLoaded.Should().BeTrue();
        group.Children.Should().OnlyContain(c => c.ChildrenLoaded);
    }
}
