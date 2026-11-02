# Roteiros de verificacao na tela do Dora Explorer, dirigidos por UI Automation (nao mexem no codigo do app).
#
# Uso, a partir da raiz do repositorio (o app NAO pode estar aberto: a trava de instancia unica impede duas copias):
#   powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario basic [-Prefix nome] [-Build] [-OutDir pasta]
#
# Roteiros: basic sort density min nav windows rede dialogs picker openwith comfort light reload flicker blink delfocus multisel
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
#   delfocus: com Shift+Delete e clique de verdade, apaga arquivos de uma pasta de teste (DoraUi + hora, na raiz de C:, exclusao
#             permanente: nao toca na Lixeira) e confere, por UI Automation, que o foco vai para o vizinho e que as setas seguem
#             funcionando sem clicar de novo (AGENTS.md, armadilha 18). Imprime [OK] ou [FALHOU] por verificacao e sai com codigo 1
#             se alguma falhar.
#   multisel: Ctrl+clique (liga e desliga itens) e Shift+clique (intervalo) com o modificador segurado de verdade, lendo "N itens
#             selecionados" na barra de status (AGENTS.md, armadilha 19). Mesmo formato de resultado e de pasta de teste.
#   delfocus e multisel guardam %AppData%\DoraExplorer (configuracoes, favoritos, indice) antes de abrir o app e devolvem igual no
#             fim; o cursor do mouse tambem volta ao lugar. O app precisa ficar em primeiro plano durante o teste.
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

# Apoio dos roteiros delfocus e multisel: teclado e mouse de verdade, pasta de teste e estado do usuario.
$uiFailures = 0
$uiRoot = $null
$userState = $null
if ($Scenario -in 'delfocus', 'multisel') {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Kb {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void TapWithShift(byte vk) { keybd_event(0xA0, 0x2A, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(120); keybd_event(vk, 0x53, 1, UIntPtr.Zero); keybd_event(vk, 0x53, 3, UIntPtr.Zero); System.Threading.Thread.Sleep(120); keybd_event(0xA0, 0x2A, 2, UIntPtr.Zero); }
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
'@
    $cursor0 = [System.Windows.Forms.Cursor]::Position
    $uiRoot = Join-Path (Split-Path -Parent $TestFolder) ('DoraUi' + (Get-Date -Format 'HHmmss'))
    $cleanup.Add($uiRoot)
    New-Item -ItemType Directory -Path $uiRoot -Force | Out-Null
    $fileCount = if ($Scenario -eq 'delfocus') { 5 } else { 6 }
    1..$fileCount | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "arq$_.txt") -Value ('x' * ($_ * 100)) -Encoding Ascii }
    if ($Scenario -eq 'multisel') {
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'pasta') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $uiRoot 'pasta\dentro.txt') -Value 'x' -Encoding Ascii
    }

    # O app grava configuracoes, favoritos e o indice de pastas em %AppData%\DoraExplorer: guarda tudo antes e devolve igual no fim.
    $appData = Join-Path $env:APPDATA 'DoraExplorer'
    $userState = Join-Path $outDir ('estado-do-usuario-' + (Get-Date -Format 'HHmmss'))
    New-Item -ItemType Directory -Path $userState -Force | Out-Null
    $userFiles = @(Get-ChildItem -LiteralPath $appData -File -ErrorAction SilentlyContinue)
    $userFiles | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $userState -Force }
    $userNames = @($userFiles | ForEach-Object { $_.Name })
    function Restore-UserState {
        Get-ChildItem -LiteralPath $userState -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $appData $_.Name) -Force }
        Get-ChildItem -LiteralPath $appData -File -ErrorAction SilentlyContinue | Where-Object { $userNames -notcontains $_.Name } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        $same = $true
        Get-ChildItem -LiteralPath $userState -File | ForEach-Object { if ((Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $appData $_.Name)).Hash) { $same = $false } }
        Remove-Item -LiteralPath $userState -Recurse -Force -ErrorAction SilentlyContinue
        "dados do usuario em %AppData%\DoraExplorer restaurados e identicos: $same"
    }

    function Ensure-Foreground($hwnd) {
        [void][Kb]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 400
        if ([Kb]::GetForegroundWindow() -eq $hwnd) { return $true }
        # o Windows nao deixa um processo em segundo plano tomar o primeiro plano: um clique real na barra de titulo resolve
        $r = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($hwnd, [ref]$r)
        [Kb]::Click([int]($r.Left + 400), [int]($r.Top + 12)); Start-Sleep -Milliseconds 500
        [void][Kb]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 400
        return ([Kb]::GetForegroundWindow() -eq $hwnd)
    }
    function Focused-Name {
        try { $f = $AE::FocusedElement; return (($f.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') + ': ' + $f.Current.Name) } catch { return 'indisponivel' }
    }
    function Row($parent, $name) { $parent.FindAll($TS::Children, (Cond $CT::TreeItem)) | Where-Object { $_.Current.Name -like "*$name*" } | Select-Object -First 1 }
    function Wait-Row($parent, $name, $ms = 12000) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) { $r = Row $parent $name; if ($r) { return $r }; Start-Sleep -Milliseconds 200 }
        return $null
    }
    # Clique de verdade na linha, com Shift ou Ctrl segurado de verdade ($mod = 'shift', 'ctrl' ou 'none').
    function Click-With($hwnd, $row, $mod) {
        if (-not (Ensure-Foreground $hwnd)) { return $false }
        $r = $row.Current.BoundingRectangle
        $x = [int]($r.Left + 120); $y = [int]($r.Top + $r.Height / 2)
        if ($mod -eq 'shift') { [Kb]::keybd_event(0xA0, 0x2A, 0, [UIntPtr]::Zero) }
        if ($mod -eq 'ctrl') { [Kb]::keybd_event(0xA2, 0x1D, 0, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 150
        [Kb]::Click($x, $y)
        Start-Sleep -Milliseconds 150
        if ($mod -eq 'shift') { [Kb]::keybd_event(0xA0, 0x2A, 2, [UIntPtr]::Zero) }
        if ($mod -eq 'ctrl') { [Kb]::keybd_event(0xA2, 0x1D, 2, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 900
        return $true
    }
    function Tap-Key($vk) { [void](Ensure-Foreground $h); [Kb]::Tap([byte]$vk); Start-Sleep -Milliseconds 800; Focused-Name }
    # Abre C:, expande a pasta de teste e espera ela listar $lastFile; devolve o item da pasta (ou $null).
    function Open-UiFolder($rootEl, $lastFile) {
        $c = (TopItems $rootEl)[0]; Expand $c
        $dir = Child $c (Split-Path $uiRoot -Leaf)
        if (-not $dir) { "pasta $(Split-Path $uiRoot -Leaf) nao apareceu em C: (o roteiro espera a pasta de teste na raiz de C:)"; return $null }
        $deadline = [DateTime]::Now.AddSeconds(20)
        while (("" + $dir.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState) -eq 'LeafNode' -and [DateTime]::Now -lt $deadline) { Start-Sleep -Milliseconds 500 }
        $dir.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 1500
        if (-not (Wait-Row $dir $lastFile)) { "a pasta de teste nao listou $lastFile"; return $null }
        return $dir
    }
    # Shift+Delete na linha em foco e "Sim" na confirmacao (exclusao permanente: nao mexe na Lixeira do usuario).
    function Delete-Permanently($procId) {
        [void](Ensure-Foreground $h)
        [Kb]::TapWithShift(0x2E)
        $dlg = [IntPtr]::Zero; $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($dlg -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 6000) { $dlg = [UiCap]::FindByTitle([uint32]$procId, 'Excluir permanentemente'); Start-Sleep -Milliseconds 200 }
        if ($dlg -eq [IntPtr]::Zero) { return $false }
        $yes = $AE::FromHandle($dlg).FindAll($TS::Descendants, (Cond $CT::Button)) | Where-Object { $_.Current.Name -eq 'Sim' } | Select-Object -First 1
        if (-not $yes) { return $false }
        $yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 2500
        return $true
    }
    function Status-Lines($rootEl) { @($rootEl.FindAll($TS::Descendants, (Cond $CT::Text)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'itens selecionados' }) }
    function Check($label, $ok, $detail = '') {
        if ($ok) { "[OK] $label" } else { $script:uiFailures++; "[FALHOU] $label" + $(if ($detail) { " (achado: $detail)" } else { '' }) }
    }
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
        'delfocus' {
            # Apagar pelo teclado (Shift+Delete) devolve o foco ao vizinho: as setas seguem funcionando sem clicar de novo.
            $dir = Open-UiFolder $root 'arq5.txt'
            if (-not $dir) { break }
            [void](Click-With $h (Row $dir 'arq3.txt') 'none')
            Check 'o clique deixou o foco em arq3' ((Focused-Name) -eq 'TreeItem: arq3.txt') (Focused-Name)
            Check 'Shift+Delete e "Sim" excluem arq3' (Delete-Permanently $proc.Id)
            Check 'arq3 saiu do disco' (-not (Test-Path -LiteralPath (Join-Path $uiRoot 'arq3.txt')))
            $f = Focused-Name
            Check 'depois de apagar, o foco esta no proximo item (arq4)' ($f -eq 'TreeItem: arq4.txt') $f
            Snap $h 'delfocus-depois-de-apagar'
            $f = Tap-Key 0x28; Check 'seta Baixo vai para arq5, sem clicar' ($f -eq 'TreeItem: arq5.txt') $f
            $f = Tap-Key 0x26; Check 'seta Cima volta para arq4' ($f -eq 'TreeItem: arq4.txt') $f
            $f = Tap-Key 0x26; Check 'seta Cima vai para arq2 (arq3 nao existe mais)' ($f -eq 'TreeItem: arq2.txt') $f
            $f = Tap-Key 0x28; Check 'seta Baixo vai para arq4' ($f -eq 'TreeItem: arq4.txt') $f
            $f = Tap-Key 0x28; Check 'seta Baixo vai para arq5, o ultimo da lista' ($f -eq 'TreeItem: arq5.txt') $f
            Check 'Shift+Delete e "Sim" excluem arq5, o ultimo' (Delete-Permanently $proc.Id)
            Check 'arq5 saiu do disco' (-not (Test-Path -LiteralPath (Join-Path $uiRoot 'arq5.txt')))
            $f = Focused-Name
            Check 'depois de apagar o ultimo, o foco vai para o anterior (arq4)' ($f -eq 'TreeItem: arq4.txt') $f
            $f = Tap-Key 0x26; Check 'seta Cima vai para arq2' ($f -eq 'TreeItem: arq2.txt') $f
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'multisel' {
            # Ctrl+clique liga e desliga itens; Shift+clique marca o intervalo. A barra de status mostra "N itens selecionados".
            $dir = Open-UiFolder $root 'arq6.txt'
            if (-not $dir) { break }
            [void](Click-With $h (Row $dir 'arq1.txt') 'none')
            Check 'clique simples em arq1 seleciona um item so' (@(Status-Lines $root).Count -eq 0) ((Status-Lines $root) -join ' || ')
            [void](Click-With $h (Row $dir 'arq4.txt') 'shift')
            $s = (Status-Lines $root) -join ' || '
            Check 'Shift+clique em arq4 marca o intervalo arq1 a arq4 (4 itens)' ($s -match '^4 itens selecionados') $s
            Snap $h 'multisel-shift-intervalo'
            [void](Click-With $h (Row $dir 'arq2.txt') 'none')
            Check 'clique simples em arq2 troca a selecao por um item so' (@(Status-Lines $root).Count -eq 0) ((Status-Lines $root) -join ' || ')
            [void](Click-With $h (Row $dir 'arq4.txt') 'ctrl')
            $s = (Status-Lines $root) -join ' || '
            Check 'Ctrl+clique em arq4 soma ao arq2 (2 itens)' ($s -match '^2 itens selecionados') $s
            [void](Click-With $h (Row $dir 'arq6.txt') 'ctrl')
            $s = (Status-Lines $root) -join ' || '
            Check 'Ctrl+clique em arq6 soma mais um (3 itens)' ($s -match '^3 itens selecionados') $s
            Snap $h 'multisel-ctrl-soltos'
            [void](Click-With $h (Row $dir 'arq4.txt') 'ctrl')
            $s = (Status-Lines $root) -join ' || '
            Check 'Ctrl+clique em arq4 de novo tira so ele (2 itens)' ($s -match '^2 itens selecionados') $s
            # Pastas no meio da selecao: o clique numa pasta normalmente troca a selecao (navegacao); com Shift ou Ctrl nao.
            [void](Click-With $h (Row $dir 'arq3.txt') 'none')
            [void](Click-With $h (Row $dir 'pasta') 'shift')
            $s = (Status-Lines $root) -join ' || '
            Check 'Shift+clique de arq3 ate a pasta (que vem antes) marca pasta, arq1, arq2 e arq3 (4 itens)' ($s -match '^4 itens selecionados') $s
            Snap $h 'multisel-shift-ate-pasta'
            [void](Click-With $h (Row $dir 'pasta') 'none')
            [void](Click-With $h (Row $dir 'arq2.txt') 'shift')
            $s = (Status-Lines $root) -join ' || '
            Check 'Shift+clique da pasta ate arq2 marca pasta, arq1 e arq2 (3 itens)' ($s -match '^3 itens selecionados') $s
            [void](Click-With $h (Row $dir 'arq1.txt') 'none')
            [void](Click-With $h (Row $dir 'pasta') 'ctrl')
            [void](Click-With $h (Row $dir 'arq5.txt') 'ctrl')
            $s = (Status-Lines $root) -join ' || '
            Check 'Ctrl+clique soma uma pasta e um arquivo a arq1 (3 itens)' ($s -match '^3 itens selecionados') $s
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'windows' {
            $c = (TopItems $root)[0]; Expand $c
            $w = Child $c 'Windows'; if ($w) { try { Expand $w } catch { "expandir Windows: $($_.Exception.Message)" } }
            Snap $h '7-windows'
        }
    }
} catch { "erro: $($_.Exception.Message) (linha $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()))" }
[void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(10000); 'app fechado: ' + $proc.HasExited
if (($Scenario -in 'delfocus', 'multisel') -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; 'o app nao fechou sozinho (ficou uma janela aberta?): encerrado a forca'; Start-Sleep -Seconds 1 }
foreach ($path in $cleanup) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
if ($Scenario -eq 'blink' -and $blinkRoot) { Clear-BlinkIndexRows $blinkRoot }
if ($userState) { Restore-UserState }
if ($Scenario -in 'delfocus', 'multisel') {
    [void][Kb]::SetCursorPos($cursor0.X, $cursor0.Y)
    'pasta de teste removida: ' + (-not (Test-Path -LiteralPath $uiRoot))
    "verificacoes com falha: $uiFailures"
    if ($uiFailures -gt 0) { exit 1 }
}
