using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Estrutura do menu superior, lida do XAML da janela principal (abrir a janela exigiria o aplicativo aberto): o menu Arquivo
/// não existe mais, Ferramentas virou Preferência, e Exibir e Preferência ganharam as opções novas ligadas aos comandos.
/// </summary>
public sealed class MainMenuTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] ViewMenuHeaders =
    [
        "Exibir arquivos ocultos",
        "Exibir arquivos de sistema",
        "Exibir unidades externas",
        "Exibir unidades bloqueadas",
        "Exibir partições não mapeadas",
        "Linhas compactas em colunas",
        "Exibir aplicativo padrão"
    ];

    private static readonly string[] PreferenceMenuHeaders =
    [
        "Lembrar caminho",
        "Iniciar sempre como administrador"
    ];

    private static XElement TopMenu() =>
        XDocument.Load(ThemeUsageColorsTests.LocateSource(@"src\DoraExplorer.App\MainWindow.xaml"))
            .Descendants(Wpf + "Menu").First();

    private static XElement Top(string header) =>
        TopMenu().Elements(Wpf + "MenuItem").Single(item => (string?)item.Attribute("Header") == header);

    [Fact]
    public void The_top_menus_are_View_Themes_Preferences_and_Help_in_that_order()
    {
        var headers = TopMenu().Elements(Wpf + "MenuItem").Select(item => (string?)item.Attribute("Header"));

        headers.Should().Equal("_Exibir", "_Temas", "_Preferência", "_Ajuda");
    }

    [Fact]
    public void The_File_menu_and_the_old_Tools_name_are_gone()
    {
        var headers = TopMenu().Elements(Wpf + "MenuItem").Select(item => (string?)item.Attribute("Header")).ToList();

        headers.Should().NotContain("_Arquivo");
        headers.Should().NotContain("_Ferramentas");
        File.ReadAllText(ThemeUsageColorsTests.LocateSource(@"src\DoraExplorer.App\MainWindow.xaml"))
            .Should().NotContain("Exit_Click", "o item Sair saiu junto com o menu Arquivo");
        File.ReadAllText(ThemeUsageColorsTests.LocateSource(@"src\DoraExplorer.App\MainWindow.xaml.cs"))
            .Should().NotContain("Exit_Click");
    }

    [Fact]
    public void The_View_menu_lists_locked_drives_right_after_external_drives()
    {
        var headers = Top("_Exibir").Elements(Wpf + "MenuItem").Select(item => (string?)item.Attribute("Header"));

        headers.Should().Equal(ViewMenuHeaders);
    }

    [Fact]
    public void The_Preferences_menu_lists_remember_path_and_always_administrator()
    {
        var headers = Top("_Preferência").Elements(Wpf + "MenuItem").Select(item => (string?)item.Attribute("Header"));

        headers.Should().Equal(PreferenceMenuHeaders);
    }

    [Theory]
    [InlineData("_Exibir", "Exibir unidades bloqueadas", "ShowLockedDrives", "ToggleShowLockedDrivesCommand")]
    [InlineData("_Preferência", "Iniciar sempre como administrador", "StartAsAdministrator", "ToggleStartAsAdministratorCommand")]
    public void The_new_options_are_checkable_and_bound_to_their_property_and_command(
        string menu, string header, string property, string command)
    {
        var item = Top(menu).Elements(Wpf + "MenuItem").Single(i => (string?)i.Attribute("Header") == header);

        ((string?)item.Attribute("IsCheckable")).Should().Be("True");
        ((string?)item.Attribute("IsChecked")).Should().Be("{Binding " + property + ", Mode=OneWay}");
        ((string?)item.Attribute("Command")).Should().Be("{Binding " + command + "}");
    }
}
