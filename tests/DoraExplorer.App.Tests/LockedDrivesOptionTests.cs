using System.Text.Json;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Menu Exibir &gt; "Exibir unidades bloqueadas": ligado (o padrão) as unidades que o Windows não deixa abrir aparecem como
/// "Bloqueada"; desligado, somem da árvore. As configurações vão para um arquivo temporário, nunca para o do usuário.
/// </summary>
public sealed class LockedDrivesOptionTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-locked-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => FileOperationTests.DeleteQuietly(_work);

    private string SettingsFile => Path.Combine(_work, "settings.json");

    [Fact]
    public void Locked_drives_are_shown_by_default_so_nothing_changes_for_existing_users()
    {
        new ExplorerViewOptions().ShowLockedDrives.Should().BeTrue();
    }

    [Fact]
    public void A_settings_file_from_before_the_option_existed_still_shows_locked_drives()
    {
        File.WriteAllText(SettingsFile, """{"ExplorerView":{"ShowHiddenFiles":true}}""");

        var options = new AppSettingsService(SettingsFile).LoadExplorerViewOptions();

        options.ShowHiddenFiles.Should().BeTrue();
        options.ShowLockedDrives.Should().BeTrue("o arquivo antigo não tem a opção, e o padrão é mostrar");
    }

    [Fact]
    public void Hiding_locked_drives_is_remembered_between_runs()
    {
        var view = new ExplorerViewSettingsService(new AppSettingsService(SettingsFile));
        view.Load();

        view.SetShowLockedDrives(false);

        new AppSettingsService(SettingsFile).LoadExplorerViewOptions().ShowLockedDrives.Should().BeFalse();
        JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.GetProperty("ExplorerView").GetProperty("ShowLockedDrives").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Changing_the_option_tells_the_panes_to_redraw_and_setting_the_same_value_does_not()
    {
        var view = new ExplorerViewSettingsService(new AppSettingsService(SettingsFile));
        view.Load();
        var changes = 0;
        view.Changed += (_, _) => changes++;

        view.SetShowLockedDrives(false);
        view.SetShowLockedDrives(false);
        view.SetShowLockedDrives(true);

        changes.Should().Be(2);
    }

    [Theory]
    [InlineData(true, NavigationTreeService.BitLockerLockedText, true)]
    [InlineData(true, NavigationTreeService.AccessLockedText, true)]
    [InlineData(false, NavigationTreeService.BitLockerLockedText, false)]
    [InlineData(false, NavigationTreeService.AccessLockedText, false)]
    [InlineData(false, "Indisponível", true)]
    [InlineData(false, "Sem mídia", true)]
    [InlineData(false, "Sem resposta", true)]
    [InlineData(false, null, true)]
    public void Only_locked_drives_are_hidden_when_the_option_is_off(bool showLocked, string? reason, bool expected) =>
        NavigationTreeService.ShouldShowDrive(showLocked, reason).Should().Be(expected);

    [Theory]
    [InlineData(NavigationTreeService.BitLockerLockedText, true)]
    [InlineData(NavigationTreeService.AccessLockedText, true)]
    [InlineData("Indisponível", false)]
    [InlineData("Sem mídia", false)]
    [InlineData(null, false)]
    public void What_counts_as_a_locked_drive(string? reason, bool expected) =>
        NavigationTreeService.IsLockedDrive(reason).Should().Be(expected);
}
