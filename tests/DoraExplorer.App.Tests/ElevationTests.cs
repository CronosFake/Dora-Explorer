using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Quando o Windows nega acesso, o app oferece reiniciar como administrador. Aqui ficam as regras (quando oferecer, o texto
/// da pergunta, como o app reinicia e espera o processo antigo sair) sem abrir o aviso do Windows de verdade.
/// </summary>
public sealed class ElevationTests
{
    private static string Denied(string text) => text + FileErrorText.AccessDeniedMarker;

    // ---- quando e o que perguntar ----------------------------------------------------------------------------------

    [Fact]
    public void A_plain_message_never_asks_for_elevation()
    {
        ElevationPrompt.For("Selecione um item.", isElevated: false).Should().Be(ElevationPromptKind.None);
        ElevationPrompt.For("Selecione um item.", isElevated: true).Should().Be(ElevationPromptKind.None);
    }

    [Fact]
    public void An_access_denied_message_offers_elevation_only_when_not_elevated()
    {
        var message = Denied("Intel: acesso negado a 'C:\\Intel'.");

        ElevationPrompt.For(message, isElevated: false).Should().Be(ElevationPromptKind.Offer);
        ElevationPrompt.For(message, isElevated: true).Should().Be(ElevationPromptKind.AlreadyElevated);
    }

    [Fact]
    public void The_question_keeps_the_message_hides_the_marker_and_says_what_will_happen()
    {
        var text = ElevationPrompt.OfferText(Denied("Intel: acesso negado a 'C:\\Intel'."));

        text.Should().StartWith("Intel: acesso negado a 'C:\\Intel'.");
        text.Should().NotContain(FileErrorText.AccessDeniedMarker.ToString());
        text.Should().Contain("administrador").And.Contain("fecha e abre de novo", "a pergunta explica o que vai acontecer");
        text.Should().Contain("repete a operação");
    }

    [Fact]
    public void When_already_elevated_the_text_explains_instead_of_asking()
    {
        var text = ElevationPrompt.AlreadyElevatedText(Denied("inetpub: acesso negado a 'C:\\inetpub'."));

        text.Should().StartWith("inetpub: acesso negado a 'C:\\inetpub'.");
        text.Should().NotContain(FileErrorText.AccessDeniedMarker.ToString());
        text.Should().Contain("já está sendo executado como administrador").And.Contain("TrustedInstaller");
    }

    // ---- reiniciar como administrador ------------------------------------------------------------------------------

    private sealed class Launch
    {
        public ProcessStartInfo? Started { get; private set; }

        public int ShutdownCalls { get; private set; }

        public Exception? ThrowOnStart { get; init; }

        public bool Elevated { get; init; }

        public ElevationService Service => new(
            () => Elevated,
            info =>
            {
                Started = info;
                if (ThrowOnStart is not null)
                {
                    throw ThrowOnStart;
                }

                return null; // o processo novo existe, mas o teste não precisa dele
            },
            () => @"C:\Programas\DoraExplorer.App.exe",
            () => ShutdownCalls++);
    }

    [Fact]
    public void Restarting_launches_the_same_program_as_administrator_and_then_closes_this_one()
    {
        var launch = new Launch();

        launch.Service.TryRestartElevated(out var error).Should().BeTrue(error);

        launch.Started.Should().NotBeNull();
        launch.Started!.FileName.Should().Be(@"C:\Programas\DoraExplorer.App.exe");
        launch.Started.Verb.Should().Be("runas", "é o que faz o Windows pedir a confirmação de administrador");
        launch.Started.UseShellExecute.Should().BeTrue("o verbo runas só vale com o shell");
        launch.Started.Arguments.Should().Be($"--elevado-de {Environment.ProcessId}");
        launch.Started.WorkingDirectory.Should().Be(@"C:\Programas");
        launch.ShutdownCalls.Should().Be(1);
    }

    [Fact]
    public void Declining_the_windows_confirmation_keeps_the_app_open_and_is_not_an_error()
    {
        var launch = new Launch { ThrowOnStart = new Win32Exception(1223, "A operação foi cancelada pelo usuário") };

        launch.Service.TryRestartElevated(out var error).Should().BeFalse();

        error.Should().BeNull("recusar a confirmação é uma escolha, não uma falha");
        launch.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void A_failure_to_launch_keeps_the_app_open_and_explains_in_portuguese()
    {
        var launch = new Launch { ThrowOnStart = new Win32Exception(2, "The system cannot find the file specified") };

        launch.Service.TryRestartElevated(out var error).Should().BeFalse();

        error.Should().StartWith("Não foi possível reiniciar como administrador");
        launch.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void An_app_that_is_already_elevated_does_not_restart_again()
    {
        var launch = new Launch { Elevated = true };

        launch.Service.TryRestartElevated(out var error).Should().BeFalse();

        error.Should().Contain("já está sendo executado como administrador");
        launch.Started.Should().BeNull();
        launch.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void The_real_service_reports_the_real_windows_token()
    {
        var expected = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        new ElevationService().IsElevated.Should().Be(expected);
        IElevationService contract = new ElevationService();
        contract.IsElevated.Should().Be(expected);
    }

    // ---- o processo novo espera o antigo sair (senão a trava de instância única o recusaria) -------------------------

    [Theory]
    [InlineData("--elevado-de 123", true, 123)]
    [InlineData("--outra coisa --elevado-de 77", true, 77)]
    [InlineData("", false, 0)]
    [InlineData("--elevado-de", false, 0)]
    [InlineData("--elevado-de abc", false, 0)]
    [InlineData("--elevado-de -5", false, 0)]
    public void The_parent_process_id_is_read_from_the_arguments(string line, bool expected, int pid)
    {
        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        StartupArguments.TryGetParentProcessId(args, out var parsed).Should().Be(expected);
        if (expected)
        {
            parsed.Should().Be(pid);
        }
    }

    [Fact]
    public void Waiting_for_a_process_that_no_longer_exists_returns_at_once()
    {
        var watch = Stopwatch.StartNew();

        StartupArguments.WaitForParentExit(int.MaxValue - 1, TimeSpan.FromSeconds(30)).Should().BeTrue();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Waiting_gives_up_when_the_process_keeps_running()
    {
        StartupArguments.WaitForParentExit(Environment.ProcessId, TimeSpan.FromMilliseconds(100)).Should().BeFalse();
    }

    [Fact]
    public void Waiting_returns_when_the_process_exits()
    {
        using var child = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Milliseconds 700" },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        StartupArguments.WaitForParentExit(child.Id, TimeSpan.FromSeconds(30)).Should().BeTrue();

        child.HasExited.Should().BeTrue();
    }
}
