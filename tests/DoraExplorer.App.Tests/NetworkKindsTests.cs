using System.Text.Json;
using System.Xml.Linq;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Quais nós da árvore são clientes de rede e quais navegam por pastas, e as portas padrão de cada protocolo.</summary>
public sealed class NetworkKindsTests
{
    public static TheoryData<NetworkProtocol> AllProtocols() => new(Enum.GetValues<NetworkProtocol>());

    [Theory]
    [MemberData(nameof(AllProtocols))]
    public void Every_protocol_has_its_own_tree_node_kind_and_maps_back(NetworkProtocol protocol)
    {
        var kind = NetworkKinds.ToKind(protocol);

        NetworkKinds.IsClientRoot(kind).Should().BeTrue();
        NetworkKinds.ToProtocol(kind).Should().Be(protocol);
        Enum.GetValues<NetworkProtocol>().Select(NetworkKinds.ToKind).Distinct().Should().HaveCount(
            Enum.GetValues<NetworkProtocol>().Length, "dois protocolos nunca dividem o mesmo nó da árvore");
    }

    [Theory]
    [InlineData(NetworkProtocol.Ftp, true, false)]
    [InlineData(NetworkProtocol.Sftp, true, false)]
    [InlineData(NetworkProtocol.Scp, true, true)]
    [InlineData(NetworkProtocol.Tftp, false, true)]
    [InlineData(NetworkProtocol.Smb, true, false)]
    [InlineData(NetworkProtocol.Nfs, true, false)]
    public void Browsable_and_transfer_only_protocols(NetworkProtocol protocol, bool browsable, bool transferOnly)
    {
        NetworkKinds.IsBrowsable(protocol).Should().Be(browsable);
        NetworkKinds.IsBrowsable(NetworkKinds.ToKind(protocol)).Should().Be(browsable);
        NetworkKinds.IsTransferOnly(protocol).Should().Be(transferOnly);
    }

    [Fact]
    public void Only_smb_and_nfs_are_file_shares_and_other_nodes_are_not_network_clients()
    {
        Enum.GetValues<NetworkProtocol>().Where(NetworkKinds.IsFileShare).Should().Equal(NetworkProtocol.Smb, NetworkProtocol.Nfs);
        NetworkKinds.IsClientRoot(NavigationNodeKind.Directory).Should().BeFalse();
        NetworkKinds.IsClientRoot(NavigationNodeKind.NetworkGroup).Should().BeFalse();
        NetworkKinds.IsClientRoot(NavigationNodeKind.Drive).Should().BeFalse();
    }

    [Theory]
    [InlineData(NetworkProtocol.Ftp, 21, 21)]
    [InlineData(NetworkProtocol.Sftp, 22, 22)]
    [InlineData(NetworkProtocol.Scp, 22, 22)]
    [InlineData(NetworkProtocol.Tftp, 69, 69)]
    [InlineData(NetworkProtocol.Smb, 445, 4445)]
    [InlineData(NetworkProtocol.Nfs, 2049, 2049)]
    public void Default_ports_differ_only_for_the_smb_server_which_avoids_the_windows_port(NetworkProtocol protocol, int client, int server)
    {
        NetworkKinds.DefaultPort(protocol).Should().Be(client);
        NetworkKinds.DefaultServerPort(protocol).Should().Be(server);
    }
}

/// <summary>Configurações dos novos protocolos: valores padrão, arquivos antigos sem os campos e perfis salvos.</summary>
public sealed class NetworkSharesSettingsTests : IDisposable
{
    private readonly string _directory = ShareTestSupport.NewFolder("dora-sharesettings-");

    public void Dispose() => ShareTestSupport.DeleteWhenReleased(_directory);

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Server_and_client_settings_of_smb_and_nfs_are_separate_and_start_with_safe_defaults()
    {
        var store = new NetworkSettingsStore();

        store.GetServer(NetworkProtocol.Smb).Port.Should().Be(4445);
        store.GetClient(NetworkProtocol.Smb).Port.Should().Be(445);
        store.GetServer(NetworkProtocol.Nfs).Port.Should().Be(2049);
        store.GetServer(NetworkProtocol.Nfs).ReadOnly.Should().BeTrue("o NFS não tem senha: o servidor começa somente leitura");
        store.GetClient(NetworkProtocol.Nfs).ReadOnly.Should().BeFalse();

        store.SetServer(NetworkProtocol.Smb, new NetworkProtocolSettings { Port = 1, DisplayName = "servidor" });
        store.SetClient(NetworkProtocol.Smb, new NetworkProtocolSettings { Port = 2, DisplayName = "cliente" });
        store.SetServer(NetworkProtocol.Nfs, new NetworkProtocolSettings { Port = 3, DisplayName = "servidor-nfs" });
        store.SetClient(NetworkProtocol.Nfs, new NetworkProtocolSettings { Port = 4, DisplayName = "cliente-nfs" });

        store.GetServer(NetworkProtocol.Smb).DisplayName.Should().Be("servidor");
        store.GetClient(NetworkProtocol.Smb).DisplayName.Should().Be("cliente");
        store.GetServer(NetworkProtocol.Nfs).DisplayName.Should().Be("servidor-nfs");
        store.GetClient(NetworkProtocol.Nfs).DisplayName.Should().Be("cliente-nfs");
        store.Ftp.DisplayName.Should().BeEmpty("mexer no SMB e no NFS não toca nos outros");
    }

    [Fact]
    public void A_settings_file_from_before_smb_and_nfs_loads_with_them_visible_and_default()
    {
        File.WriteAllText(SettingsFile, """
            {
              "LeftPaneDisplay": { "ShowFavorites": true, "ShowFtp": false, "ShowTftp": false },
              "Network": { "Ftp": { "Port": 2121 } }
            }
            """);
        var settings = new AppSettingsService(SettingsFile);

        var pane = settings.LoadLeftPaneDisplayOptions();
        pane.ShowFtp.Should().BeFalse("o que o usuário já tinha escolhido continua");
        pane.ShowSmb.Should().BeTrue();
        pane.ShowNfs.Should().BeTrue();
        settings.LoadNetworkProtocolSettings(NetworkProtocol.Ftp).Port.Should().Be(2121);
        settings.LoadNetworkProtocolSettings(NetworkProtocol.Smb).Port.Should().Be(4445);
        settings.LoadNetworkClientSettings(NetworkProtocol.Nfs).Port.Should().Be(2049);
    }

    [Fact]
    public void The_pane_options_for_smb_and_nfs_are_saved_per_pane()
    {
        var settings = new AppSettingsService(SettingsFile);

        settings.SaveLeftPaneDisplayOptions(new PaneDisplayOptions { ShowSmb = false, ShowNfs = true });
        settings.SaveRightPaneDisplayOptions(new PaneDisplayOptions { ShowSmb = true, ShowNfs = false });

        var reloaded = new AppSettingsService(SettingsFile);
        reloaded.LoadLeftPaneDisplayOptions().Should().BeEquivalentTo(new PaneDisplayOptions { ShowSmb = false, ShowNfs = true });
        reloaded.LoadRightPaneDisplayOptions().Should().BeEquivalentTo(new PaneDisplayOptions { ShowSmb = true, ShowNfs = false });
    }

    [Theory]
    [InlineData(NetworkProtocol.Smb, "DOMINIO\\ana")]
    [InlineData(NetworkProtocol.Nfs, "1000:1000")]
    public void A_client_profile_keeps_its_protocol_across_a_restart(NetworkProtocol protocol, string user)
    {
        var settings = new AppSettingsService(SettingsFile);
        settings.SaveNetworkClientProfile(new NetworkClientProfile
        {
            Protocol = protocol,
            DisplayName = "servidor",
            Host = "nas.local",
            Port = 1234,
            Username = user,
            RemotePath = "/dados"
        });

        var profile = new AppSettingsService(SettingsFile).LoadNetworkClientProfiles(protocol).Single();

        profile.Protocol.Should().Be(protocol);
        profile.Username.Should().Be(user);
        profile.Port.Should().Be(1234);
        profile.ToConnection().Protocol.Should().Be(protocol);
        new AppSettingsService(SettingsFile).LoadNetworkClientProfiles(NetworkProtocol.Ftp).Should().BeEmpty();

        // O valor gravado é o nome do número do enum: os arquivos antigos (Ftp=0..Tftp=3) continuam valendo.
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        var stored = json.RootElement.GetProperty("Network").GetProperty("ClientProfiles")[0].GetProperty("Protocol").GetInt32();
        stored.Should().Be(protocol == NetworkProtocol.Smb ? 4 : 5);
    }

    [Fact]
    public void The_pane_menu_loads_compares_and_applies_the_smb_and_nfs_options()
    {
        var menu = new PaneDisplayMenuViewModel();
        var options = new PaneDisplayOptions { ShowSmb = false, ShowNfs = true };

        menu.LoadFrom(options);
        menu.ShowSmb.Should().BeFalse();
        menu.ShowNfs.Should().BeTrue();
        menu.DiffersFrom(options).Should().BeFalse();

        menu.ShowSmb = true;
        menu.DiffersFrom(options).Should().BeTrue("ligar o SMB no menu é uma mudança a aplicar");
        menu.ShowSmb = false;
        menu.ShowNfs = false;
        menu.DiffersFrom(options).Should().BeTrue("desligar o NFS também");

        menu.ApplyTo(options);
        options.ShowSmb.Should().BeFalse();
        options.ShowNfs.Should().BeFalse();
    }
}

/// <summary>Os itens do menu de exibição e os botões do menu central dos novos protocolos, lidos do XAML.</summary>
public sealed class NetworkSharesXamlTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Load(string relativePath) => XDocument.Load(ShareTestSupport.LocateSource(relativePath));

    [Theory]
    [InlineData("FTP", "ShowFtp")]
    [InlineData("SFTP", "ShowSftp")]
    [InlineData("SCP", "ShowScp")]
    [InlineData("TFTP", "ShowTftp")]
    [InlineData("SMB", "ShowSmb")]
    [InlineData("NFS", "ShowNfs")]
    public void The_pane_display_menu_has_a_checkable_item_for_each_protocol(string header, string property)
    {
        var item = Load(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml")
            .Descendants(Wpf + "MenuItem")
            .Single(menuItem => (string?)menuItem.Attribute("Header") == header);

        ((string?)item.Attribute("IsCheckable")).Should().Be("True");
        ((string?)item.Attribute("IsChecked")).Should().Be("{Binding DisplayMenu." + property + ", Mode=TwoWay}");
    }

    [Theory]
    [InlineData("FTP", "NetworkProtocolIcons.FtpMark", "IsFtpServiceActive", "FtpCommand")]
    [InlineData("SFTP", "NetworkProtocolIcons.SftpMark", "IsSftpServiceActive", "SftpCommand")]
    [InlineData("SCP", "NetworkProtocolIcons.ScpMark", "IsScpServiceActive", "ScpCommand")]
    [InlineData("TFTP", "NetworkProtocolIcons.TftpMark", "IsTftpServiceActive", "TftpCommand")]
    [InlineData("SMB", "NetworkProtocolIcons.SmbMark", "IsSmbServiceActive", "SmbCommand")]
    [InlineData("NFS", "NetworkProtocolIcons.NfsMark", "IsNfsServiceActive", "NfsCommand")]
    public void The_server_buttons_use_the_drawn_outline_icons_and_show_the_service_state(string label, string icon, string active, string command)
    {
        var button = Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml")
            .Descendants()
            .Single(element => element.Name.LocalName == "CenterToolbarButton" && (string?)element.Attribute("Label") == label);

        ((string?)button.Attribute("IconData")).Should().Be("{x:Static infra:" + icon + "}");
        ((string?)button.Attribute("ShowServiceStatus")).Should().Be("True");
        ((string?)button.Attribute("IsServiceActive")).Should().Be("{Binding " + active + "}");
        ((string?)button.Attribute("Command")).Should().Be("{Binding " + command + "}");
        button.Attribute("IconGlyph").Should().BeNull("o ícone é um desenho, não um glifo da fonte");
    }

    [Fact]
    public void The_button_draws_the_icon_as_a_thin_outline_in_the_theme_colour_with_no_fill()
    {
        var path = Load(@"src\DoraExplorer.App\Controls\CenterToolbarButton.xaml")
            .Descendants(Wpf + "Path")
            .Single(element => ((string?)element.Attribute("Data"))?.Contains("IconData", StringComparison.Ordinal) == true);

        ((string?)path.Attribute("Stroke")).Should().Be("{Binding IconForeground, ElementName=Root}", "o contorno segue a cor do tema, como os glifos");
        ((string?)path.Attribute("Fill")).Should().Be("{x:Null}", "só o contorno: sem preenchimento");
        ((string?)path.Attribute("StrokeThickness")).Should().Be("1");
        ((string?)path.Attribute("StrokeLineJoin")).Should().Be("Round");
    }

    [Fact]
    public void The_four_old_glyph_resources_of_the_network_buttons_are_gone()
    {
        var resources = File.ReadAllText(ShareTestSupport.LocateSource(@"src\DoraExplorer.App\Resources\CenterToolbarIcons.xaml"));

        foreach (var name in new[] { "GlyphFtp", "GlyphSftp", "GlyphScp", "GlyphTftp" })
        {
            resources.Should().NotContain($"x:Key=\"{name}\"", "nenhum botão usa mais esse glifo");
        }
    }

    [Fact]
    public void The_six_server_buttons_are_in_the_network_section_in_this_order()
    {
        var labels = Load(@"src\DoraExplorer.App\Controls\CenterToolbarControl.xaml")
            .Descendants()
            .Where(element => element.Name.LocalName == "CenterToolbarButton" && element.Attribute("ShowServiceStatus") is not null)
            .Select(element => (string?)element.Attribute("Label"));

        labels.Should().Equal("FTP", "SFTP", "SCP", "TFTP", "SMB", "NFS");
    }
}
