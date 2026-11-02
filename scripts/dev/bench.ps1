# Mede abrir uma pasta grande no Dora Explorer: tempo ate terminar de carregar, travamentos da tela e memoria.
#
# Uso, a partir da raiz do repositorio (o app NAO pode estar aberto):
#   powershell -NoProfile -File scripts\dev\make-test-folder.ps1 -Path C:\DoraDevTest\plana-100k -Count 100000
#   powershell -NoProfile -File scripts\dev\bench.ps1 -Folder C:\DoraDevTest\plana-100k -Pattern plana-100k -Label "100k"
#
# O painel esquerdo reabre em -Folder e o direito na pasta pai (liga "Lembrar caminho" so durante a medicao).
# Suas configuracoes (%AppData%\DoraExplorer\settings.json) e o indice (index.db) sao guardados antes e SEMPRE
# restaurados no fim, mesmo se a medicao falhar; se uma execucao anterior morreu no meio, a restauracao acontece
# na proxima. Ao terminar, o carregamento e detectado pelo log (linha "Carregado ... <Pattern>") e CPU ociosa por 8 s.
# Mede: atraso de resposta da janela (amostras > 300 ms = travada), memoria (WorkingSet/Private) e itens materializados.
# Este arquivo e ASCII de proposito: o Windows PowerShell 5.1 le .ps1 sem BOM como ANSI.
param(
    [string]$Folder = '',
    [string]$Pattern = '',
    [string]$Label = 'medicao',
    [int]$TimeoutSeconds = 240,
    [string]$SnapPath = ''
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot 'build\compile\DoraExplorer.App.exe'
if (Get-Process DoraExplorer.App -ErrorAction SilentlyContinue) { 'JA HA UMA INSTANCIA ABERTA: feche o app antes'; return }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('BenchWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class BenchWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public static IntPtr Largest(uint pid) { IntPtr best = IntPtr.Zero; long area = 0;
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { RECT r; GetWindowRect(h, out r); long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top); if (a > area) { area = a; best = h; } } return true; }, IntPtr.Zero);
    return best; }
}
'@
}

$AE = [Windows.Automation.AutomationElement]; $TS = [Windows.Automation.TreeScope]; $CT = [Windows.Automation.ControlType]
$appData = Join-Path $env:APPDATA 'DoraExplorer'
$cfg = Join-Path $appData 'settings.json'
$index = Join-Path $appData 'index.db'
$backup = Join-Path $env:TEMP 'dora-dev\bench-backup'
$logPath = Join-Path $repoRoot ("logs\doraexplorer-{0}.log" -f (Get-Date).ToString('yyyyMMdd'))

function Restore-UserData {
    if (-not (Test-Path $backup)) { return }
    foreach ($ext in '-wal', '-shm') { Remove-Item -LiteralPath ($index + $ext) -Force -ErrorAction SilentlyContinue }
    foreach ($name in 'settings.json', 'index.db') {
        $saved = Join-Path $backup $name
        if (Test-Path $saved) { Copy-Item -LiteralPath $saved -Destination (Join-Path $appData $name) -Force }
    }
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
}

# uma medicao anterior que morreu no meio deixou o backup para tras: restaura antes de comecar
Restore-UserData
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($file in $cfg, $index) { if (Test-Path $file) { Copy-Item -LiteralPath $file -Destination $backup -Force } }

try {
    # configuracao da execucao: reabre o painel esquerdo na pasta medida
    if ($Folder -and (Test-Path $cfg)) {
        $text = [IO.File]::ReadAllText($cfg)
        $left = $Folder | ConvertTo-Json
        $right = (Split-Path $Folder -Parent) | ConvertTo-Json
        $text = [regex]::Replace($text, '"RememberPath":\s*(true|false)', '"RememberPath": true')
        $text = [regex]::Replace($text, '"LeftPanePath":\s*(null|"(?:[^"\\]|\\.)*")', { param($m) '"LeftPanePath": ' + $left })
        $text = [regex]::Replace($text, '"RightPanePath":\s*(null|"(?:[^"\\]|\\.)*")', { param($m) '"RightPanePath": ' + $right })
        [IO.File]::WriteAllText($cfg, $text, (New-Object Text.UTF8Encoding($false)))
    }

    $logStart = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
    $launch = Get-Date
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $h = [IntPtr]::Zero; $root = $null
    $memory = New-Object System.Collections.Generic.List[object]
    $latency = New-Object System.Collections.Generic.List[object]
    $loadedAt = $null; $loadedLine = $null; $startedAt = $null; $windowAt = 0
    $pos = $logStart
    $lastCpu = 0.0; $lastBusyAt = 0.0
    $clock = [Diagnostics.Stopwatch]::StartNew()

    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Milliseconds 150
        if ($proc.HasExited) { 'o app fechou sozinho'; break }
        $proc.Refresh()
        $cpuNow = $proc.TotalProcessorTime.TotalSeconds
        if (($cpuNow - $lastCpu) -gt 0.04) { $lastBusyAt = $clock.Elapsed.TotalSeconds }
        $lastCpu = $cpuNow
        $memory.Add([pscustomobject]@{ T = $clock.Elapsed.TotalSeconds; WorkingSet = $proc.WorkingSet64; Private = $proc.PrivateMemorySize64 })

        if ($null -eq $root) {
            $h = [BenchWin]::Largest([uint32]$proc.Id)
            if ($h -ne [IntPtr]::Zero) { $root = $AE::FromHandle($h); $windowAt = $clock.Elapsed.TotalSeconds }
        } else {
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            try { $null = $root.Current.IsEnabled } catch { }
            $latency.Add([pscustomobject]@{ T = $clock.Elapsed.TotalSeconds; Ms = $stopwatch.Elapsed.TotalMilliseconds })
        }

        if (Test-Path $logPath) {
            $fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
            $fs.Seek($pos, 'Begin') | Out-Null
            $sr = New-Object IO.StreamReader($fs)
            $chunk = $sr.ReadToEnd(); $pos = $fs.Position
            $sr.Dispose(); $fs.Dispose()
            foreach ($line in ($chunk -split "`r?`n")) {
                if ($line -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]') {
                    $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
                    if (-not $startedAt -and $line -match 'iniciado|started') { $startedAt = $stamp }
                    if ($Pattern -and -not $loadedAt -and $line -match 'Carregado' -and $line -match [regex]::Escape($Pattern)) { $loadedAt = $stamp; $loadedLine = $line.Substring(25).Trim() }
                }
            }
        }

        if ($Pattern) { if ($loadedAt -and ($clock.Elapsed.TotalSeconds - $lastBusyAt) -gt 8) { break } }
        elseif ($clock.Elapsed.TotalSeconds -gt 12) { break }
    }

    $proc.Refresh()
    $final = if ($proc.HasExited) { $null } else { [pscustomobject]@{ WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 1); PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1); PeakWorkingSetMB = [math]::Round($proc.PeakWorkingSet64 / 1MB, 1); CpuSeconds = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1) } }
    $items = $null
    if ($root) {
        try {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $items = @($root.FindAll($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::TreeItem)))).Count
            $itemsMs = $watch.Elapsed.TotalMilliseconds
        } catch { }
    }
    if ($SnapPath -and $h -ne [IntPtr]::Zero -and -not $proc.HasExited) {
        Add-Type -AssemblyName System.Drawing
        $r = New-Object BenchWin+RECT; [void][BenchWin]::GetWindowRect($h, [ref]$r)
        $bm = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top); $g = [System.Drawing.Graphics]::FromImage($bm)
        $dc = $g.GetHdc(); [void][BenchWin]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
        $bm.Save($SnapPath, [System.Drawing.Imaging.ImageFormat]::Png); $bm.Dispose(); "captura: $SnapPath"
    }
    if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(15000) }

    $maxLat = ($latency | Measure-Object -Property Ms -Maximum).Maximum
    $freezes = @($latency | Where-Object { $_.Ms -gt 300 })
    $beforeLoad = $memory | Where-Object { $_.T -lt 3 } | Select-Object -Last 1
    "=== $Label ==="
    if ($startedAt) { "inicio do app (log): $($startedAt.ToString('HH:mm:ss.fff')); janela aberta apos {0:N1}s do lancamento" -f $windowAt }
    if ($loadedAt) {
        "carga terminou (CPU ociosa por 8s): {0:N1}s apos o lancamento; CPU gasta ate la {1:N1}s" -f $lastBusyAt, $lastCpu
        "carregou em: {0:N2}s apos o lancamento ({1:N2}s apos o 'iniciado' do app)" -f (($loadedAt - $launch).TotalSeconds), (($loadedAt - $startedAt).TotalSeconds)
        "linha do log: $loadedLine"
    } elseif ($Pattern) { "NAO carregou dentro de ${TimeoutSeconds}s" }
    "tela: maior atraso de resposta {0:N0} ms; {1} amostras acima de 300 ms (de {2}); tempo total congelado ~{3:N1}s" -f $maxLat, $freezes.Count, $latency.Count, (($freezes | Measure-Object -Property Ms -Sum).Sum / 1000)
    if ($beforeLoad) { "memoria aos 3s: WorkingSet {0:N0} MB, Private {1:N0} MB" -f ($beforeLoad.WorkingSet / 1MB), ($beforeLoad.Private / 1MB) }
    if ($final) { "memoria no fim: WorkingSet $($final.WorkingSetMB) MB, Private $($final.PrivateMB) MB, pico WorkingSet $($final.PeakWorkingSetMB) MB, CPU $($final.CpuSeconds)s" }
    if ($null -ne $items) { "itens de arvore materializados (virtualizacao): $items (consulta de {0:N0} ms)" -f $itemsMs }
}
finally {
    if ($proc -and -not $proc.HasExited) { [void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(15000) }
    Restore-UserData
    'configuracoes e indice restaurados'
}
