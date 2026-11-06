using System.Reflection;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Testes que trocam o <c>App.SettingsService</c> estático não rodam em paralelo com outros.</summary>
[CollectionDefinition("App statics", DisableParallelization = true)]
public sealed class AppStaticsGroup;

/// <summary>
/// A pasta "Rede" da árvore agrupa os clientes FTP, SFTP, SCP, TFTP, SMB e NFS. Os perfis salvos aparecem dentro do protocolo
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

    private static PaneDisplayOptions Only(
        bool ftp = false, bool sftp = false, bool scp = false, bool tftp = false, bool smb = false, bool nfs = false) =>
        new() { ShowFtp = ftp, ShowSftp = sftp, ShowScp = scp, ShowTftp = tftp, ShowSmb = smb, ShowNfs = nfs };

    private void SaveProfile(NetworkProtocol protocol, string name) =>
        _settings.SaveNetworkClientProfile(new NetworkClientProfile { Protocol = protocol, DisplayName = name, Host = "servidor.local" });

    [Fact]
    public void The_network_group_holds_the_six_protocols_in_order()
    {
        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions());

        group.Should().NotBeNull();
        group!.Name.Should().Be("Rede");
        group.Kind.Should().Be(NavigationNodeKind.NetworkGroup);
        group.Children.Select(c => (c.Name, c.Kind)).Should().Equal(
            ("FTP", NavigationNodeKind.Ftp),
            ("SFTP", NavigationNodeKind.Sftp),
            ("SCP", NavigationNodeKind.Scp),
            ("TFTP", NavigationNodeKind.Tftp),
            ("SMB", NavigationNodeKind.Smb),
            ("NFS", NavigationNodeKind.Nfs));
    }

    [Fact]
    public void The_new_protocols_follow_the_pane_option_and_keep_their_own_icons()
    {
        var onlySmb = NavigationTreeService.CreateNetworkGroup(Only(smb: true))!;
        onlySmb.Children.Select(c => c.Name).Should().Equal("SMB");

        var onlyNfs = NavigationTreeService.CreateNetworkGroup(Only(nfs: true))!;
        onlyNfs.Children.Select(c => c.Name).Should().Equal("NFS");

        onlySmb.Children.Single().IconSource.Should().BeSameAs(NetworkProtocolIcons.SmbIcon);
        onlyNfs.Children.Single().IconSource.Should().BeSameAs(NetworkProtocolIcons.NfsIcon);
    }

    [Fact]
    public void Every_protocol_node_has_its_own_drawn_icon_instead_of_the_generic_network_icon()
    {
        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        foreach (var node in group.Children)
        {
            node.IconSource.Should().BeSameAs(NetworkProtocolIcons.TreeIconFor(NetworkKinds.ToProtocol(node.Kind)));
        }

        group.Children.Select(node => node.IconSource).Distinct().Should().HaveCount(6);
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
        SaveProfile(NetworkProtocol.Smb, "nas");
        SaveProfile(NetworkProtocol.Smb, "Escritório");
        SaveProfile(NetworkProtocol.Nfs, "servidor-linux");

        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        group.Children.Single(c => c.Name == "FTP").Children.Select(c => c.Name).Should().Equal("Alfa", "zeta");
        group.Children.Single(c => c.Name == "SFTP").Children.Select(c => c.Name).Should().Equal("backup");
        group.Children.Single(c => c.Name == "SCP").Children.Should().BeEmpty();
        group.Children.Single(c => c.Name == "TFTP").Children.Should().BeEmpty();
        group.Children.Single(c => c.Name == "SMB").Children.Select(c => c.Name).Should().Equal("Escritório", "nas");
        group.Children.Single(c => c.Name == "NFS").Children.Select(c => c.Name).Should().Equal("servidor-linux");
    }

    [Fact]
    public void SMB_and_NFS_profiles_can_be_browsed_so_they_start_with_an_expand_arrow()
    {
        SaveProfile(NetworkProtocol.Smb, "nas");
        SaveProfile(NetworkProtocol.Tftp, "placa");

        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        group.Children.Single(c => c.Name == "SMB").Children.Single().Children.Should().ContainSingle(c => c.IsPlaceholder);
        group.Children.Single(c => c.Name == "TFTP").Children.Single().Children.Should().BeEmpty("o TFTP só transfere um arquivo");
    }

    [Fact]
    public void The_group_and_its_protocols_are_already_loaded_so_they_never_wait_for_a_listing()
    {
        var group = NavigationTreeService.CreateNetworkGroup(new PaneDisplayOptions())!;

        group.ChildrenLoaded.Should().BeTrue();
        group.Children.Should().OnlyContain(c => c.ChildrenLoaded);
    }
}
