using System.Text.Json;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// "Iniciar sempre como administrador": o aviso do Windows (UAC) roda numa área protegida e não pode ser acionado em teste, então
/// a elevação é trocada por um dublê que só conta os pedidos. As configurações vão para um arquivo temporário.
/// </summary>
public sealed class AdministratorStartupServiceTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-admin-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => FileOperationTests.DeleteQuietly(_work);

    private string SettingsFile => Path.Combine(_work, "settings.json");

    private AdministratorStartupService Create(FakeElevationService elevation, bool load = true)
    {
        var service = new AdministratorStartupService(new AppSettingsService(SettingsFile), elevation);
        if (load)
        {
            service.Load();
        }

        return service;
    }

    private bool SavedFlag() =>
        File.Exists(SettingsFile) &&
        JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.TryGetProperty("StartAsAdministrator", out var value) &&
        value.GetBoolean();

    [Fact]
    public void It_is_off_by_default_and_a_settings_file_from_before_the_option_keeps_it_off()
    {
        File.WriteAllText(SettingsFile, """{"RememberPath":true}""");

        var service = Create(new FakeElevationService());

        service.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Turning_it_on_asks_for_administrator_access_right_away_and_remembers_the_choice()
    {
        var elevation = new FakeElevationService { RestartSucceeds = true };
        var service = Create(elevation);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var ok = service.TryEnable(out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        elevation.RestartRequests.Should().Be(1, "o check pede o acesso de administrador na hora");
        service.IsEnabled.Should().BeTrue();
        SavedFlag().Should().BeTrue("o app que reabre como administrador lê a opção do arquivo");
        changes.Should().Be(1);
    }

    [Fact]
    public void Turning_it_on_when_already_administrator_only_saves_and_does_not_restart()
    {
        var elevation = new FakeElevationService { IsElevated = true };
        var service = Create(elevation);

        var ok = service.TryEnable(out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        elevation.RestartRequests.Should().Be(0);
        service.IsEnabled.Should().BeTrue();
        SavedFlag().Should().BeTrue();
    }

    [Fact]
    public void Refusing_the_windows_confirmation_leaves_the_option_off_so_the_check_matches_reality()
    {
        var elevation = new FakeElevationService { RestartSucceeds = false, RestartError = null };
        var service = Create(elevation);

        var ok = service.TryEnable(out var error);

        ok.Should().BeFalse();
        error.Should().BeNull("recusar o UAC é uma escolha do usuário, não um erro");
        elevation.RestartRequests.Should().Be(1);
        service.IsEnabled.Should().BeFalse();
        SavedFlag().Should().BeFalse();
    }

    [Fact]
    public void A_failure_to_restart_leaves_the_option_off_and_reports_why()
    {
        var elevation = new FakeElevationService { RestartSucceeds = false, RestartError = "Não foi possível reiniciar." };
        var service = Create(elevation);

        var ok = service.TryEnable(out var error);

        ok.Should().BeFalse();
        error.Should().Be("Não foi possível reiniciar.");
        service.IsEnabled.Should().BeFalse();
        SavedFlag().Should().BeFalse();
    }

    [Fact]
    public void The_option_is_saved_before_the_restart_because_the_old_process_closes_right_after()
    {
        var elevation = new FakeElevationService { RestartSucceeds = true };
        var service = Create(elevation);
        elevation.OnRestart = () => SavedFlag().Should().BeTrue("o processo antigo fecha logo depois de pedir o reinício");

        service.TryEnable(out _);

        elevation.RestartRequests.Should().Be(1);
    }

    [Fact]
    public void Turning_it_off_is_remembered_and_never_asks_for_anything()
    {
        var elevation = new FakeElevationService { IsElevated = true };
        var service = Create(elevation);
        service.TryEnable(out _);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Disable();

        service.IsEnabled.Should().BeFalse();
        SavedFlag().Should().BeFalse();
        elevation.RestartRequests.Should().Be(0);
        changes.Should().Be(1);
        Create(new FakeElevationService()).IsEnabled.Should().BeFalse("a escolha vale na próxima abertura");
    }

    [Fact]
    public void On_the_next_run_the_saved_choice_is_loaded()
    {
        Create(new FakeElevationService { IsElevated = true }).TryEnable(out _);

        Create(new FakeElevationService()).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void At_startup_with_the_option_on_and_no_administrator_access_it_asks_for_it_and_stops_this_start()
    {
        Create(new FakeElevationService { IsElevated = true }).TryEnable(out _);
        var elevation = new FakeElevationService { RestartSucceeds = true };
        var service = Create(elevation);

        var restarting = service.TryElevateOnStartup([]);

        restarting.Should().BeTrue("quem chama não deve abrir a janela: o app está sendo reaberto como administrador");
        elevation.RestartRequests.Should().Be(1);
    }

    [Fact]
    public void At_startup_with_the_option_on_and_already_administrator_nothing_is_asked()
    {
        Create(new FakeElevationService { IsElevated = true }).TryEnable(out _);
        var elevation = new FakeElevationService { IsElevated = true };

        var restarting = Create(elevation).TryElevateOnStartup([]);

        restarting.Should().BeFalse();
        elevation.RestartRequests.Should().Be(0);
    }

    [Fact]
    public void At_startup_with_the_option_off_nothing_is_asked()
    {
        var elevation = new FakeElevationService { RestartSucceeds = true };

        var restarting = Create(elevation).TryElevateOnStartup([]);

        restarting.Should().BeFalse();
        elevation.RestartRequests.Should().Be(0);
    }

    [Fact]
    public void At_startup_refusing_the_windows_confirmation_opens_the_app_normally_and_keeps_the_option()
    {
        Create(new FakeElevationService { IsElevated = true }).TryEnable(out _);
        var elevation = new FakeElevationService { RestartSucceeds = false };
        var service = Create(elevation);

        var restarting = service.TryElevateOnStartup([]);

        restarting.Should().BeFalse("o usuário recusou: o app abre sem administrador");
        service.IsEnabled.Should().BeTrue("na próxima abertura o Windows pergunta de novo");
        SavedFlag().Should().BeTrue();
    }

    [Fact]
    public void An_app_that_was_just_reopened_by_the_elevation_never_asks_again_so_there_is_no_loop()
    {
        Create(new FakeElevationService { IsElevated = true }).TryEnable(out _);
        var elevation = new FakeElevationService { RestartSucceeds = true };

        var restarting = Create(elevation).TryElevateOnStartup(["--elevado-de", "1234"]);

        restarting.Should().BeFalse();
        elevation.RestartRequests.Should().Be(0);
    }
}
