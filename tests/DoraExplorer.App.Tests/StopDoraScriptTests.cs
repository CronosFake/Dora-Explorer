using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O Start.bat fecha o app pelo scripts\stop-dora.ps1. Estes testes usam processos de mentira (nunca o Dora Explorer de verdade)
/// e chamam só as funções do script: o corpo dele encerra qualquer DoraExplorer.App ou dotnet da pasta do projeto, inclusive o próprio teste.
/// </summary>
public sealed class StopDoraScriptTests : IDisposable
{
    private readonly List<string> _temp = [];

    private static string StopScript
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "scripts", "stop-dora.ps1");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("scripts\\stop-dora.ps1 não encontrado acima de " + AppContext.BaseDirectory);
        }
    }

    // Uma janela invisível (opacidade 0, fora da tela) num PowerShell à parte: tem janela principal (sem ShowInTaskbar = false, que a faria
    // pertencer a outra janela e deixaria de contar), então aceita o pedido de fechar.
    // Com refuse ela diz não, como o app faz ao perguntar "Cancelar a operação e sair?". O arquivo marca que o pedido chegou.
    private string NewFakeWindow(bool refuse, out string marker)
    {
        marker = Path.Combine(Path.GetTempPath(), $"dora-fake-{Guid.NewGuid():N}.txt");
        var form = Path.Combine(Path.GetTempPath(), $"dora-fake-{Guid.NewGuid():N}.ps1");
        _temp.Add(marker);
        _temp.Add(marker + ".ready");
        _temp.Add(form);
        File.WriteAllText(form, $$"""
            Add-Type -AssemblyName System.Windows.Forms
            $f = New-Object Windows.Forms.Form
            $f.FormBorderStyle = 'None'; $f.Opacity = 0; $f.StartPosition = 'Manual'
            $f.Location = New-Object Drawing.Point(-32000, -32000); $f.Size = New-Object Drawing.Size(1, 1)
            $f.add_Shown({ Set-Content -LiteralPath '{{marker}}.ready' -Value 'ready' })
            $f.add_FormClosing({ param($s, $e) Set-Content -LiteralPath '{{marker}}' -Value 'asked'; if ({{(refuse ? "$true" : "$false")}}) { $e.Cancel = $true } })
            [Windows.Forms.Application]::Run($f)
            """, Encoding.ASCII);
        return form;
    }

    private const string Helpers = """
        # O caminho da pasta temporária tem espaço: sem aspas o Start-Process o corta.
        function Start-Fake($form) { Start-Process powershell.exe -ArgumentList '-NoProfile', '-Sta', '-File', ('"' + $form + '"') -WindowStyle Hidden -PassThru }
        # Espera a janela de mentira dizer que já apareceu: logo depois de iniciar, a "janela principal" do processo ainda é o console dele.
        function Wait-Window($p, $ready) { $end = (Get-Date).AddSeconds(30); while (-not (Test-Path -LiteralPath $ready) -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 200 }; $p.Refresh() }
        function Say($key, $value) { Write-Output ('RESULT:' + $key + '=' + $value) }
        """;

    // Roda o cenário num PowerShell que já carregou as funções do script; devolve as linhas "RESULT:chave=valor" que ele imprimiu.
    private Dictionary<string, string> Run(string scenario)
    {
        var script = Path.Combine(Path.GetTempPath(), $"dora-stop-test-{Guid.NewGuid():N}.ps1");
        _temp.Add(script);
        File.WriteAllText(script, $". '{StopScript.Replace("'", "''")}'\n{Helpers}\n{scenario}", Encoding.ASCII);

        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("O cenário do stop-dora não terminou em 90 s.");
        }

        var results = output.Result
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("RESULT:", StringComparison.Ordinal))
            .Select(line => line["RESULT:".Length..].Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
        if (!string.IsNullOrWhiteSpace(errors.Result))
        {
            throw new InvalidOperationException($"O cenário escreveu erros:\n{errors.Result}\nSaída:\n{output.Result}");
        }

        return results;
    }

    [Fact]
    public void A_process_the_terminal_may_not_terminate_is_told_apart_from_one_it_may()
    {
        // csrss é protegido: nem como administrador o taskkill chega nele. Faz o papel de um app aberto como administrador.
        var result = Run("""
            $child = Start-Process ping.exe -ArgumentList '-t', '127.0.0.1' -WindowStyle Hidden -PassThru
            Say 'child' (Test-CanTerminate $child.Id)
            Say 'protected' (Test-CanTerminate (Get-Process -Name csrss | Select-Object -First 1).Id)
            Stop-Process -Id $child.Id -Force
            """);

        result["child"].Should().Be("True", "um processo do próprio usuário pode ser encerrado");
        result["protected"].Should().Be("False", "assim o script avisa em vez de esperar um fechamento que nunca chega");
    }

    [Fact]
    public void A_window_that_accepts_the_close_request_leaves_without_being_killed()
    {
        var form = NewFakeWindow(refuse: false, out var marker);

        var result = Run($$"""
            $p = Start-Fake '{{form}}'
            Wait-Window $p '{{marker}}.ready'
            $left = @(Request-GracefulClose @($p) 15)
            Say 'left' $left.Count
            Say 'exited' $p.HasExited
            Say 'asked' (Test-Path -LiteralPath '{{marker}}')
            if (-not $p.HasExited) { $p.Kill() }
            """);

        result["left"].Should().Be("0");
        result["exited"].Should().Be("True");
        result["asked"].Should().Be("True", "a janela recebeu o pedido de fechar e saiu sozinha");
    }

    [Fact]
    public void A_window_that_refuses_is_waited_for_then_handed_back_to_be_forced()
    {
        var form = NewFakeWindow(refuse: true, out var marker);

        var result = Run($$"""
            $p = Start-Fake '{{form}}'
            Wait-Window $p '{{marker}}.ready'
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $left = @(Request-GracefulClose @($p) 3)
            Say 'waited' ([int]$watch.Elapsed.TotalSeconds)
            Say 'left' $left.Count
            Say 'asked' (Test-Path -LiteralPath '{{marker}}')
            Stop-Forcefully $left
            [void]$p.WaitForExit(10000)
            Say 'exited' $p.HasExited
            if (-not $p.HasExited) { $p.Kill() }
            """);

        result["asked"].Should().Be("True");
        int.Parse(result["waited"], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(3, "dá tempo de o usuário responder à pergunta do app");
        result["left"].Should().Be("1", "quem não saiu volta para o taskkill");
        result["exited"].Should().Be("True");
    }

    [Fact]
    public void A_process_without_a_window_is_not_waited_for_because_nobody_could_answer()
    {
        var result = Run("""
            $p = Start-Process ping.exe -ArgumentList '-t', '127.0.0.1' -WindowStyle Hidden -PassThru
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $left = @(Request-GracefulClose @($p) 30)
            Say 'waited' ([int]$watch.Elapsed.TotalSeconds)
            Say 'left' $left.Count
            Stop-Forcefully $left
            [void]$p.WaitForExit(10000)
            Say 'exited' $p.HasExited
            if (-not $p.HasExited) { $p.Kill() }
            """);

        int.Parse(result["waited"], System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThan(5);
        result["left"].Should().Be("1");
        result["exited"].Should().Be("True");
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
