param(
    [string]$ProcessName = 'DoraExplorer.App',
    [int]$GraceSeconds = 10
)

# Fecha o Dora Explorer antes de o Start.bat compilar. Primeiro pede para a janela fechar (assim o app pergunta sobre uma
# copia em andamento e salva o que precisa); so quem nao sair no prazo vai para o taskkill /F.
# Saida 2: o app esta aberto de um jeito que este terminal nao consegue fechar (por exemplo como administrador).
$ErrorActionPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$exes = @(
    (Join-Path $root 'build\staging\DoraExplorer.App.exe'),
    (Join-Path $root 'build\compile\DoraExplorer.App.exe')
)
$dirs = @('build\app', 'build\staging', 'build\compile', 'src\DoraExplorer.App\bin') |
    ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root $_)) }

function Test-FileLocked {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    try {
        $stream = [IO.File]::Open($Path, 'Open', 'Read', 'None')
        $stream.Close()
        return $false
    }
    catch {
        return $true
    }
}

function Test-UsesDoraDotnet {
    param($Process)

    $cmd = [string](Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)").CommandLine
    if ($cmd -match 'DoraExplorer') {
        return $true
    }

    foreach ($dir in $dirs) {
        if ($cmd -like "*$dir*") {
            return $true
        }
    }

    return $false
}

# Um terminal comum nao encerra (nem manda fechar) um processo aberto como administrador: o Windows recusa com "acesso negado".
function Test-CanTerminate {
    param([int]$Id)

    if (-not ('DoraProcess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DoraProcess
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // PROCESS_TERMINATE e o que o taskkill precisa; 5 e ERROR_ACCESS_DENIED.
    public static bool CanTerminate(int pid)
    {
        IntPtr handle = OpenProcess(0x0001, false, pid);
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() != 5;
        }

        CloseHandle(handle);
        return true;
    }
}
'@
    }

    if (-not ('DoraProcess' -as [type])) {
        return $true
    }

    return [DoraProcess]::CanTerminate($Id)
}

# Pede para a janela principal fechar e espera ate $Seconds. Devolve quem continuou vivo.
# Sem janela principal ninguem poderia responder, entao esses voltam na hora, sem espera.
function Request-GracefulClose {
    param([object[]]$Processes, [int]$Seconds)

    $asked = @()
    foreach ($proc in $Processes) {
        $proc.Refresh()
        if ($proc.HasExited) {
            continue
        }

        Write-Host ('  Pedindo ao PID {0} para fechar...' -f $proc.Id)
        if ($proc.CloseMainWindow()) {
            $asked += $proc
        }
    }

    $start = Get-Date
    $deadline = $start.AddSeconds($Seconds)
    $warned = $false
    while ((Get-Date) -lt $deadline) {
        if (@($asked | Where-Object { -not $_.HasExited }).Count -eq 0) {
            break
        }

        if (-not $warned -and (Get-Date) -gt $start.AddSeconds(2)) {
            Write-Host ('  O app ainda esta aberto. Se ele perguntou algo (por exemplo sobre uma operacao em andamento), responda na janela dele: espero ate {0} s.' -f $Seconds)
            $warned = $true
        }

        Start-Sleep -Milliseconds 200
    }

    return @($Processes | Where-Object { -not $_.HasExited })
}

function Stop-Forcefully {
    param([object[]]$Processes)

    foreach ($proc in $Processes) {
        Write-Host ('  Encerrando PID {0} a forca...' -f $proc.Id)
        & taskkill.exe /F /T /PID $proc.Id | Out-Null
    }
}

function Stop-DotnetHosts {
    foreach ($proc in @(Get-Process -Name 'dotnet')) {
        if (Test-UsesDoraDotnet $proc) {
            Write-Host ('  Encerrando PID {0} (dotnet host)...' -f $proc.Id)
            & taskkill.exe /F /T /PID $proc.Id | Out-Null
        }
    }
}

function Test-AnyLocked {
    foreach ($exe in $exes) {
        if (Test-FileLocked $exe) {
            return $true
        }
    }

    return $false
}

# Carregado com ". stop-dora.ps1" (os testes), o script so define as funcoes: o corpo abaixo encerraria processos de verdade.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

$running = @(Get-Process -Name $ProcessName)
$blocked = @($running | Where-Object { -not (Test-CanTerminate $_.Id) })
$blockedIds = @($blocked | ForEach-Object { $_.Id })
$reachable = @($running | Where-Object { $blockedIds -notcontains $_.Id })

if ($reachable.Count -gt 0) {
    Stop-Forcefully @(Request-GracefulClose -Processes $reachable -Seconds $GraceSeconds)
}

Stop-DotnetHosts

if ($blocked.Count -gt 0) {
    Write-Host ''
    Write-Host ('[ERRO] O Dora Explorer (PID: {0}) continua aberto e o Windows nao deixa este terminal fecha-lo.' -f ($blockedIds -join ', ')) -ForegroundColor Red
    Write-Host 'Quase sempre e porque ele esta aberto como administrador.'
    Write-Host 'Feche o app pela janela dele, ou rode o Start.bat como administrador, e tente de novo.'
    Write-Host ''
    exit 2
}

for ($i = 1; $i -le 8; $i++) {
    Stop-Forcefully @(Get-Process -Name $ProcessName)
    if (-not (Test-AnyLocked)) {
        break
    }

    Start-Sleep -Milliseconds 800
}

if (Test-AnyLocked) {
    $left = @(Get-Process -Name $ProcessName)
    $ids = ($left | ForEach-Object { $_.Id }) -join ', '
    if (-not $ids) {
        $ids = 'desconhecido'
    }

    Write-Host ''
    Write-Host "[AVISO] build ainda bloqueado (PID: $ids)." -ForegroundColor Yellow
    Write-Host 'A compilacao pode falhar se o executavel continuar em uso.'
    Write-Host ''
}

exit 0
