# Roteiros de verificacao na tela do Dora Explorer, dirigidos por UI Automation (nao mexem no codigo do app).
#
# Uso, a partir da raiz do repositorio (o app NAO pode estar aberto: a trava de instancia unica impede duas copias):
#   powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario basic [-Prefix nome] [-Build] [-OutDir pasta]
#
# Roteiros: basic sort density min nav windows rede dialogs picker openwith comfort light reload flicker blink
#   reload  : fecha uma pasta com clique, cria uma pasta por fora e reabre; confere que a listagem se atualiza
#   flicker : fotografa a janela em alta frequencia ao reabrir "Program Files" e conta quedas de conteudo (o "pisca")
#   blink   : abre pastas pela setinha com clique REAL do mouse e mede se a arvore despenca de uma vez (o "pisca" ao abrir
#             pasta: as linhas abaixo dela sumiam em um quadro e voltavam durante a animacao). Cria uma pasta nova ao lado de
#             -TestFolder (DoraBlink + hora, com blink-pequena e blink-grande) ANTES de abrir o app, para o cache de contagens
#             nao a mostrar como vazia, e remove no fim. O app indexa a pasta em %AppData%\DoraExplorer\index.db: ao terminar, o
#             roteiro apaga de la as linhas dessa pasta (caminho exato, com a winsqlite3.dll do Windows; se o app ainda estiver
#             aberto, avisa e nao mexe). O app precisa ficar em primeiro plano e o cursor se mexe por alguns segundos.
#             Cada pasta imprime a maior queda em UM quadro; acima de 20% do conteudo e PISCA (antes da correcao: 41% a 70%).
#   openwith: abre o menu do aplicativo padrao e a janela "Abrir com" do Windows (usa o rotulo -AppLabel)
# Capturas: PNG em -OutDir (padrao %TEMP%\dora-dev). Os roteiros so leem a tela; 'reload' cria e remove a pasta -TestFolder.
# Este arquivo e ASCII de proposito: o Windows PowerShell 5.1 le .ps1 sem BOM como ANSI.
param(
    [string]$Prefix = 'ui',
    [string]$Scenario = 'basic',
    [switch]$Build,
    [string]$ExeOverride = '',
    [string]$OutDir = '',
    [string]$TestFolder = 'C:\DoraDevTest',
    [string]$AppLabel = 'Bloco de notas'
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = if ($OutDir) { $OutDir } else { Join-Path $env:TEMP 'dora-dev' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exe = if ($ExeOverride) { $ExeOverride } else { Join-Path $repoRoot 'build\compile\DoraExplorer.App.exe' }
if (Get-Process DoraExplorer.App -ErrorAction SilentlyContinue) { 'JA HA UMA INSTANCIA ABERTA: feche o app antes'; return }
if ($Build) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    & $dotnet build (Join-Path $repoRoot 'src\DoraExplorer.App\DoraExplorer.App.csproj') -c Release --nologo -o (Join-Path $repoRoot 'build\compile') 2>&1 | Out-Null
    "build exit: $LASTEXITCODE"
}
$cleanup = New-Object System.Collections.Generic.List[string]
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class UiCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static void ClickScreen(IntPtr h, int sx, int sy, uint downMsg, uint upMsg, int xbutton) {
    POINT p; p.X = sx; p.Y = sy; ScreenToClient(h, ref p);
    IntPtr l = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
    IntPtr wDown = (IntPtr)(xbutton << 16);
    PostMessage(h, 0x0200, IntPtr.Zero, l);              // WM_MOUSEMOVE
    PostMessage(h, downMsg, wDown, l);
    PostMessage(h, upMsg, wDown, l);
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int max);
  public static IntPtr FindByTitle(uint pid, string title) { IntPtr found = IntPtr.Zero;
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var sb = new System.Text.StringBuilder(256); GetWindowTextW(h, sb, 256); if (sb.ToString() == title) { found = h; return false; } } return true; }, IntPtr.Zero);
    return found; }
  public static IntPtr Largest(uint pid) { IntPtr best = IntPtr.Zero; long area = 0;
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { RECT r; GetWindowRect(h, out r); long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top); if (a > area) { area = a; best = h; } } return true; }, IntPtr.Zero);
    return best; }
}
'@
[void][UiCap]::SetProcessDPIAware()
if ($Scenario -eq 'blink') {
    Add-Type -AssemblyName System.Windows.Forms
    $blinkCompiler = New-Object System.CodeDom.Compiler.CompilerParameters
    $blinkCompiler.CompilerOptions = '/unsafe'; [void]$blinkCompiler.ReferencedAssemblies.Add('System.Drawing.dll')
    Add-Type -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class BlinkMouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
  // Pixels claros (texto e icones) na area da arvore: o conteudo visivel da lista, por quadro.
  public static unsafe int Bright(Bitmap bmp, int min) {
    var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int n = 0; byte* p = (byte*)d.Scan0;
    for (int y = 0; y < bmp.Height; y++) { byte* row = p + y * d.Stride; for (int x = 0; x < bmp.Width; x++) { byte* px = row + x * 4; if (px[0] > min || px[1] > min || px[2] > min) n++; } }
    bmp.UnlockBits(d); return n; }
}
'@ -CompilerParameters $blinkCompiler -ErrorAction Stop
    $blinkRoot = Join-Path (Split-Path -Parent $TestFolder) ('DoraBlink' + (Get-Date -Format 'HHmmss'))
    $cleanup.Add($blinkRoot)
    1..3 | ForEach-Object { New-Item -ItemType Directory -Path (Join-Path $blinkRoot "blink-pequena\pasta$_") -Force | Out-Null }
    1..60 | ForEach-Object { New-Item -ItemType Directory -Path (Join-Path $blinkRoot ('blink-grande\pasta{0:D2}' -f $_)) -Force | Out-Null }
    1..20 | ForEach-Object { Set-Content -LiteralPath (Join-Path $blinkRoot "blink-grande\arquivo$_.txt") -Value 'x' -Encoding Ascii }
    function Get-TreePixels($wr) {
        $bm = New-Object System.Drawing.Bitmap 530, 530; $g = [System.Drawing.Graphics]::FromImage($bm)
        $g.CopyFromScreen($wr.Left + 20, $wr.Top + 130, 0, 0, (New-Object System.Drawing.Size 530, 530)); $g.Dispose()
        $n = [BlinkMouse]::Bright($bm, 110); $bm.Dispose(); $n
    }
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class WinSqlite {
  [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open16(string file, out IntPtr db);
  [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
  [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
  [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_changes(IntPtr db);
  [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
  [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
  // Roda cada comando e devolve quantas linhas cada um apagou; na primeira falha para e devolve a mensagem em error.
  public static int[] Run(string file, string[] statements, out string error) {
    error = null;
    int[] counts = new int[statements.Length];
    IntPtr db;
    if (sqlite3_open16(file, out db) != 0) { error = "nao abriu o banco"; sqlite3_close(db); return counts; }
    try {
      sqlite3_busy_timeout(db, 5000);
      for (int i = 0; i < statements.Length; i++) {
        IntPtr unused;
        if (sqlite3_exec(db, statements[i], IntPtr.Zero, IntPtr.Zero, out unused) != 0) { error = Marshal.PtrToStringAnsi(sqlite3_errmsg(db)); return counts; }
        counts[i] = sqlite3_changes(db);
      }
    } finally { sqlite3_close(db); }
    return counts;
  }
}
'@
    # O app indexa cada pasta que abre (chave = caminho completo, sem barra final, em minusculas; C:\ vira c:). Apaga so as
    # linhas da pasta que ESTE roteiro criou: caminho exato, sem curinga, e a linha dela na listagem da pasta pai.
    function Clear-BlinkIndexRows($folder) {
        $db = Join-Path $env:APPDATA 'DoraExplorer\index.db'
        if (-not (Test-Path -LiteralPath $db)) { return }
        if (Get-Process DoraExplorer.App -ErrorAction SilentlyContinue) { 'indice: o app ainda esta aberto, as linhas da pasta de teste ficaram no indice'; return }
        $key = [IO.Path]::GetFullPath($folder).TrimEnd('\', '/').ToLowerInvariant().Replace("'", "''")
        $parentKey = [IO.Path]::GetFullPath((Split-Path -Parent $folder)).TrimEnd('\', '/').ToLowerInvariant().Replace("'", "''")
        $leaf = (Split-Path -Leaf $folder).Replace("'", "''")
        $len = $key.Length + 1
        $statements = [string[]]@(
            "DELETE FROM entries WHERE parent_path = '$key' OR substr(parent_path, 1, $len) = '$key\'",
            "DELETE FROM directories WHERE path = '$key' OR substr(path, 1, $len) = '$key\'",
            "DELETE FROM entries WHERE parent_path = '$parentKey' AND name = '$leaf' COLLATE NOCASE"
        )
        $err = $null
        $counts = [WinSqlite]::Run($db, $statements, [ref]$err)
        if ($err) { "indice: nao consegui limpar ($err)"; return }
        "indice: apagadas $($counts[0]) entradas e $($counts[1]) pastas de $folder, e $($counts[2]) linha na listagem da pasta pai"
    }
}
$Avancar = 'Avan' + [char]0xE7 + 'ar'
$AE = [Windows.Automation.AutomationElement]; $TS = [Windows.Automation.TreeScope]; $CT = [Windows.Automation.ControlType]
function Cond($ct) { New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct) }
function Snap($h, $name) {
    $r = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bm = New-Object System.Drawing.Bitmap $w, $ht; $g = [System.Drawing.Graphics]::FromImage($bm)
    $dc = $g.GetHdc(); [void][UiCap]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $file = Join-Path $outDir "$Prefix-$name.png"; $bm.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bm.Dispose()
    "captura $Prefix-$name.png ${w}x${ht}"
}
function TopItems($root, $index = 0) { $trees = $root.FindAll($TS::Descendants, (Cond $CT::Tree)); $trees[$index].FindAll($TS::Children, (Cond $CT::TreeItem)) }
function Child($item, $name) { foreach ($k in $item.FindAll($TS::Children, (Cond $CT::TreeItem))) { if ($k.Current.Name -eq $name) { return $k } } }
function Expand($item) { $item.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 1800 }
function Choose-Item($item) { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
function Click-Item($h, $item, $dx = 120, $dy = 14) { $r = $item.Current.BoundingRectangle; [UiCap]::ClickScreen($h, [int]($r.Left + $dx), [int]($r.Top + $dy), 0x0201, 0x0202, 0); Start-Sleep -Milliseconds 1200 }
function Click-X($h, $x1or2) { $r = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($h, [ref]$r); [UiCap]::ClickScreen($h, $r.Left + 300, $r.Top + 400, 0x020B, 0x020C, $x1or2); Start-Sleep -Milliseconds 1200 }
function Button-State($root, $name) { foreach ($b in $root.FindAll($TS::Descendants, (Cond $CT::Button))) { if ($b.Current.Name -eq $name) { return "$name enabled=$($b.Current.IsEnabled)" } } "$name nao encontrado" }
function Invoke-Button($root, $like) { foreach ($b in $root.FindAll($TS::Descendants, (Cond $CT::Button))) { if ($b.Current.Name -like $like) { $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 1500; return $true } }; $false }

function Menu-Invoke($root, $top, $like, $mid = $null) {
    $MI = Cond $CT::MenuItem
    foreach ($m in $root.FindAll($TS::Descendants, $MI)) {
        if ($m.Current.Name -eq $top) {
            $m.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
            if ($mid) { foreach ($x in $m.FindAll($TS::Descendants, $MI)) { if ($x.Current.Name -eq $mid) { $x.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600; break } } }
            foreach ($s in $m.FindAll($TS::Descendants, $MI)) {
                if ($s.Current.Name -like $like) {
                    try { $s.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { $s.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle() }
                    Start-Sleep -Milliseconds 1500; return "menu $top > $($s.Current.Name)"
                }
            }
            return "item '$like' nao encontrado em $top"
        }
    }
    "menu '$top' nao encontrado"
}

$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 5
$h = [UiCap]::Largest([uint32]$proc.Id)
$root = $AE::FromHandle($h)
try {
    switch ($Scenario) {
        'basic' {
            Snap $h '1-raiz'
            $items = @(TopItems $root); "itens de topo: $($items.Count) -> " + (($items | % { $_.Current.Name }) -join ', ')
            $c = $items[0]
            try { Expand $c; 'expandiu C:' } catch { "falha ao expandir '$($c.Current.Name)': $($_.Exception.Message)" }
            $windows = Child $c 'Windows'
            if ($windows) { try { Choose-Item $windows; 'selecionou Windows' } catch { "falha ao selecionar: $($_.Exception.Message)" } } else { 'Windows nao encontrado' }
            Snap $h '2-c-expandido'
        }
        'sort' {
            $c = (TopItems $root)[0]; Expand $c
            "clique em Modificado: " + (Invoke-Button $root 'Modificado*')
            Snap $h '3-ordenado-data'
            "clique em Tamanho: " + (Invoke-Button $root 'Tamanho*')
            Snap $h '4-ordenado-tamanho'
        }
        'density' {
            $c = (TopItems $root)[0]; Expand $c
            Snap $h '5-compacto'
        }
        'min' {
            $c = (TopItems $root)[0]; Expand $c
            $tp = $root.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern); $tp.Resize(900, 580); Start-Sleep -Seconds 2
            Snap $h '6-minimo'
        }
        'nav' {
            $c = (TopItems $root)[0]; Expand $c
            'inicial: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar), (Button-State $root 'Subir uma pasta') -join ' | ')
            Choose-Item (Child $c 'Windows')
            'apos abrir Windows: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar), (Button-State $root 'Subir uma pasta') -join ' | ')
            Snap $h '8-windows-selecionado'
            "subir: " + (Invoke-Button $root 'Subir uma pasta')
            'apos subir: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar), (Button-State $root 'Subir uma pasta') -join ' | ')
            Snap $h '9-apos-subir'
            "voltar: " + (Invoke-Button $root 'Voltar')
            'apos voltar: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar), (Button-State $root 'Subir uma pasta') -join ' | ')
            Snap $h '10-apos-voltar'
            "avancar: " + (Invoke-Button $root $Avancar)
            'apos avancar: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar), (Button-State $root 'Subir uma pasta') -join ' | ')
            Click-X $h 1
            'apos botao lateral voltar do mouse: ' + ((Button-State $root 'Voltar'), (Button-State $root $Avancar) -join ' | ')
        }
        'reload' {
            $testName = Split-Path $TestFolder -Leaf
            if (-not (Test-Path -LiteralPath $TestFolder)) { $cleanup.Add($TestFolder) } else { $cleanup.Add((Join-Path $TestFolder 'novo-teste')) }
            New-Item -ItemType Directory -Path (Join-Path $TestFolder 'base') -Force | Out-Null
            $c = (TopItems $root)[0]; Expand $c
            $dct = Child $c $testName
            if (-not $dct) { "pasta $testName nao apareceu em C: (o roteiro espera a pasta de teste na raiz de C:)"; break }
            Expand $dct
            "filhos antes: " + ((@($dct.FindAll($TS::Children, (Cond $CT::TreeItem))) | % { $_.Current.Name }) -join ', ')
            Click-Item $h $dct 26 16
            Snap $h 'r2-fechado-por-clique'
            "estado apos clique: " + $dct.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
            New-Item -ItemType Directory -Path (Join-Path $TestFolder 'novo-teste') -Force | Out-Null
            Click-Item $h $dct 26 16
            Start-Sleep -Milliseconds 1500
            Snap $h 'r3-reaberto-por-clique'
            "estado apos reabrir: " + $dct.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
            $depois = ((@($dct.FindAll($TS::Children, (Cond $CT::TreeItem))) | % { $_.Current.Name }) -join ', ')
            "filhos depois: $depois"
            'listagem atualizada (novo-teste apareceu): ' + ($depois -match 'novo-teste')
        }        'flicker' {
            $frameCode = @"
using System; using System.Drawing; using System.Drawing.Imaging;
public static class FrameCount {
  public static int Bright(Bitmap bmp, int x0, int y0, int x1, int y1, int min) {
    var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int n = 0;
    unsafe { byte* p = (byte*)d.Scan0; for (int y = y0; y < y1; y++) { byte* row = p + y * d.Stride; for (int x = x0; x < x1; x++) { byte* px = row + x * 4; if (px[0] > min || px[1] > min || px[2] > min) n++; } } }
    bmp.UnlockBits(d); return n; }
}
"@
            $cp = New-Object System.CodeDom.Compiler.CompilerParameters; $cp.CompilerOptions = '/unsafe'; [void]$cp.ReferencedAssemblies.Add('System.Drawing.dll')
            if (-not ('FrameCount' -as [type])) { Add-Type -TypeDefinition $frameCode -CompilerParameters $cp }
            $c = (TopItems $root)[0]; Expand $c
            $pf = Child $c 'Program Files'
            Expand $pf
            Start-Sleep -Milliseconds 1500
            $r = $pf.Current.BoundingRectangle
            Click-Item $h $pf 26 16        # fecha por clique
            Start-Sleep -Milliseconds 1500
            $wr = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($h, [ref]$wr)
            $series = New-Object System.Collections.Generic.List[object]
            $clock = [Diagnostics.Stopwatch]::StartNew()
            [UiCap]::ClickScreen($h, [int]($r.Left + 26), [int]($r.Top + 16), 0x0201, 0x0202, 0)   # reabre por clique
            $w = $wr.Right - $wr.Left; $ht = $wr.Bottom - $wr.Top
            while ($clock.Elapsed.TotalMilliseconds -lt 2500) {
                $bm = New-Object System.Drawing.Bitmap $w, $ht; $g = [System.Drawing.Graphics]::FromImage($bm)
                $g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht)); $g.Dispose()
                $series.Add([pscustomobject]@{ Ms = [int]$clock.Elapsed.TotalMilliseconds; Px = [FrameCount]::Bright($bm, 20, 130, 550, 660, 110) })
                if ($series.Count -in 3, 9, 20) { $bm.Save("$outDir\$Prefix-quadro$($series.Count).png", [System.Drawing.Imaging.ImageFormat]::Png) }
                $bm.Dispose()
            }
            "quadros: $($series.Count)"
            ($series | % { "$($_.Ms)ms:$($_.Px)" }) -join '  '
            $px = $series | % { $_.Px }
            $max = ($px | Measure-Object -Maximum).Maximum; $fin = $px[-1]
            # quedas: depois de atingir o conteudo final, algum quadro com muito menos pixels?
            $first = ($px | % { $_ } | Select-Object -First 1)
            $dips = 0; $peak = 0
            foreach ($v in $px) { if ($v -gt $peak) { $peak = $v } elseif ($peak -gt 0 -and $v -lt ($peak * 0.7)) { $dips++ } }
            "pico=$max final=$fin quadros_com_queda_maior_que_30pct_apos_pico_parcial=$dips"
        }
        'blink' {
            $testName = Split-Path $blinkRoot -Leaf
            $c = (TopItems $root)[0]; Expand $c
            $dct = Child $c $testName
            if (-not $dct) { "pasta $testName nao apareceu em C: (o roteiro espera a pasta de teste na raiz de C:)"; break }
            # o app calcula as contagens em segundo plano: sem setinha a pasta ainda e uma folha
            $deadline = [DateTime]::Now.AddSeconds(20)
            while (("" + $dct.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState) -eq 'LeafNode' -and [DateTime]::Now -lt $deadline) { Start-Sleep -Milliseconds 500 }
            Expand $dct
            $cursor0 = [System.Windows.Forms.Cursor]::Position
            [void][BlinkMouse]::SetForegroundWindow($h); Start-Sleep -Milliseconds 500
            if ([BlinkMouse]::GetForegroundWindow() -ne $h) { 'ABORTADO: a janela do app nao esta em primeiro plano (nenhum clique enviado)'; break }
            $wr = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($h, [ref]$wr)
            foreach ($name in 'blink-pequena', 'blink-grande') {
                $item = Child $dct $name
                if (-not $item) { "[$name] nao encontrada na arvore"; continue }
                $btn = $item.FindFirst($TS::Descendants, (Cond $CT::Button))
                if (-not $btn) { "[$name] sem setinha de expandir"; continue }
                $br = $btn.Current.BoundingRectangle
                $base = Get-TreePixels $wr
                $series = New-Object System.Collections.Generic.List[int]
                $clock = [Diagnostics.Stopwatch]::StartNew()
                [BlinkMouse]::Click([int]($br.Left + $br.Width / 2), [int]($br.Top + $br.Height / 2))
                while ($clock.Elapsed.TotalMilliseconds -lt 1800) { $series.Add((Get-TreePixels $wr)) }
                $drop = 0; for ($i = 1; $i -lt $series.Count; $i++) { $d = $series[$i - 1] - $series[$i]; if ($d -gt $drop) { $drop = $d } }
                $min = ($series | Measure-Object -Minimum).Minimum
                $pct = [int](100 * $drop / [Math]::Max(1, $base))
                $kids = @($item.FindAll($TS::Children, (Cond $CT::TreeItem))).Count
                "[$name] base=$base minimo=$min ($([int](100 * $min / [Math]::Max(1, $base)))% da base) maior queda em um quadro=$drop ($pct%) final=$($series[$series.Count - 1]) quadros=$($series.Count) filhos criados=$kids -> " + $(if ($pct -gt 20) { 'PISCA' } else { 'ok' })
                Start-Sleep -Milliseconds 300
            }
            [void][BlinkMouse]::SetCursorPos($cursor0.X, $cursor0.Y)
        }
        'rede' {
            $items = @(TopItems $root); "itens de topo: $($items.Count) -> " + (($items | % { $_.Current.Name }) -join ', ')
            Snap $h '20-raiz'
            $g = $items | ? { $_.Current.Name -eq 'Rede' } | Select-Object -First 1
            if ($g) {
                try { Expand $g; 'expandiu Rede' } catch { "falha ao expandir Rede: $($_.Exception.Message)" }
                'filhos de Rede: ' + ((@($g.FindAll($TS::Children, (Cond $CT::TreeItem))) | % { $_.Current.Name }) -join ', ')
                Snap $h '21-rede-expandida'
                $f = Child $g 'FTP'
                if ($f) { try { Choose-Item $f; 'selecionou FTP' } catch { "falha: $($_.Exception.Message)" }; Snap $h '22-ftp-selecionado' }
            } else { 'grupo Rede nao encontrado' }
        }
        'dialogs' {
            $items = @(TopItems $root)
            $g = $items | ? { $_.Current.Name -eq 'Rede' } | Select-Object -First 1
            Expand $g
            function Find-Window($name) {
                for ($i = 0; $i -lt 10; $i++) {
                    $hw = [UiCap]::FindByTitle([uint32]$proc.Id, $name); $w = if ($hw -ne [IntPtr]::Zero) { $AE::FromHandle($hw) } else { $null }
                    if ($w) { return $w }
                    Start-Sleep -Milliseconds 400
                }
            }
            function Close-Window($w) { try { $w.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch { 'falha ao fechar' }; Start-Sleep -Milliseconds 600 }
            foreach ($proto in 'FTP', 'SFTP', 'TFTP') {
                $node = Child $g $proto
                if (-not $node) { "no $proto nao encontrado"; continue }
                Choose-Item $node
                $r = $node.Current.BoundingRectangle
                [UiCap]::ClickScreen($h, [int]($r.Left + 120), [int]($r.Top + 14), 0x0203, 0x0202, 0)
                $w = Find-Window "Cliente $proto"
                if ($w) { Snap ([IntPtr]$w.Current.NativeWindowHandle) "30-cliente-$proto"; Close-Window $w } else { "janela Cliente $proto nao apareceu" }
            }
            'botoes do menu central: ' + ((@($root.FindAll($TS::Descendants, (Cond $CT::Button))) | ? { $_.Current.Name -match 'FTP|SCP' } | % { $_.Current.Name }) -join ' | ')
            foreach ($proto in 'FTP', 'TFTP') {
                $ok = Invoke-Button $root "*$proto"
                $w = Find-Window "Servidor $proto"
                if ($w) { Snap ([IntPtr]$w.Current.NativeWindowHandle) "31-servidor-$proto"; Close-Window $w } else { "janela Servidor $proto nao apareceu (invoke=$ok)" }
            }
        }
        'picker' {
            $c = (TopItems $root)[0]; Expand $c
            Start-Sleep -Seconds 1
            $btn = $null
            foreach ($b in $root.FindAll($TS::Descendants, (Cond $CT::Button))) { if ($b.Current.Name -eq $AppLabel) { $btn = $b; break } }
            if (-not $btn) { 'botao "' + $AppLabel + '" nao encontrado; botoes: ' + ((@($root.FindAll($TS::Descendants, (Cond $CT::Button))) | Select-Object -First 12 | % { $_.Current.Name }) -join ' | '); break }
            "rotulo encontrado: [$($btn.Current.Name)]"
            $btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1200
            $procCond = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$proc.Id)
            $names = @()
            foreach ($w in $AE::RootElement.FindAll($TS::Children, $procCond)) {
                foreach ($m in $w.FindAll($TS::Descendants, (Cond $CT::MenuItem))) { $names += $m.Current.Name }
            }
            'itens do menu: ' + ($names -join ' | ')
            Snap $h '60-menu-aberto'
        }
        'openwith' {
            Add-Type -AssemblyName System.Windows.Forms
            $c = (TopItems $root)[0]; Expand $c
            Start-Sleep -Seconds 1
            $btn = $null
            foreach ($b in $root.FindAll($TS::Descendants, (Cond $CT::Button))) { if ($b.Current.Name -eq $AppLabel) { $btn = $b; break } }
            if (-not $btn) { 'botao nao encontrado'; break }
            $btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1200
            $procCond = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$proc.Id)
            $target = $null
            foreach ($w in $AE::RootElement.FindAll($TS::Children, $procCond)) {
                foreach ($m in $w.FindAll($TS::Descendants, (Cond $CT::MenuItem))) { if ($m.Current.Name -like 'Escolher no Windows*') { $target = $m } }
            }
            if (-not $target) { 'item "Escolher no Windows" nao encontrado'; break }
            $before = @(Get-Process notepad -ErrorAction SilentlyContinue | % { $_.Id })
            $target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $dlg = $null
            for ($i = 0; $i -lt 15 -and -not $dlg; $i++) {
                Start-Sleep -Milliseconds 400
                $op = Get-Process OpenWith -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($op -and $op.MainWindowHandle -ne 0) { $dlg = $AE::FromHandle($op.MainWindowHandle) }
            }
            if (-not $dlg) { 'dialogo OpenWith nao apareceu'; break }
            'dialogo aberto: ' + $dlg.Current.Name
            $once = $null
            foreach ($b in $dlg.FindAll($TS::Descendants, (Cond $CT::Button))) { if ($b.Current.Name -like 'S* uma vez') { $once = $b } }
            if (-not $once) { 'botao "So uma vez" nao encontrado: ' + ((@($dlg.FindAll($TS::Descendants, (Cond $CT::Button))) | % { $_.Current.Name }) -join ' | '); [void](Get-Process OpenWith | % { $_.CloseMainWindow() }); break }
            $once.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 3
            $after = @(Get-Process notepad -ErrorAction SilentlyContinue | % { $_.Id })
            $novos = @($after | ? { $before -notcontains $_ })
            'Notepad aberto pelo dialogo: ' + ($novos.Count -gt 0)
            foreach ($id in $novos) { $np = Get-Process -Id $id; "  titulo: $($np.MainWindowTitle)"; [void]$np.CloseMainWindow() }
            'app respondendo: ' + (-not $proc.HasExited -and $proc.Responding)
        }
        'comfort' {
            $c = (TopItems $root)[0]; Expand $c
            Choose-Item (Child $c 'Windows')
            Menu-Invoke $root 'Exibir' 'Linhas compactas*'
            Snap $h '11-confortavel'
            Menu-Invoke $root 'Exibir' 'Linhas compactas*'
            Snap $h '12-compacto-de-volta'
        }
        'light' {
            $c = (TopItems $root)[0]; Expand $c
            Choose-Item (Child $c 'Windows')
            Menu-Invoke $root 'Temas' 'Claro' 'Tema'
            Snap $h '13-tema-claro'
            $tp = $root.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern); $tp.Resize(900, 580); Start-Sleep -Seconds 2
            Snap $h '14-claro-minimo'
            Menu-Invoke $root 'Temas' 'Escuro' 'Tema'
            Snap $h '15-escuro-minimo'
        }
        'windows' {
            $c = (TopItems $root)[0]; Expand $c
            $w = Child $c 'Windows'; if ($w) { try { Expand $w } catch { "expandir Windows: $($_.Exception.Message)" } }
            Snap $h '7-windows'
        }
    }
} catch { "erro: $($_.Exception.Message) (linha $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()))" }
[void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(10000); 'app fechado: ' + $proc.HasExited
foreach ($path in $cleanup) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
if ($Scenario -eq 'blink' -and $blinkRoot) { Clear-BlinkIndexRows $blinkRoot }
