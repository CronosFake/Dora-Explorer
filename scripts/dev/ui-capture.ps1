# Roteiros de verificacao na tela do Dora Explorer, dirigidos por UI Automation (nao mexem no codigo do app).
#
# Uso, a partir da raiz do repositorio (o app NAO pode estar aberto: a trava de instancia unica impede duas copias):
#   powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario basic [-Prefix nome] [-Build] [-OutDir pasta]
#
# Roteiros: basic sort density min nav windows rede dialogs picker openwith comfort light reload flicker blink delfocus multisel dragdrop about favmenu props menu marqueedrag rename newfeatures
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
#   dragdrop: arrasta de verdade com o mouse (botao apertado, cursor andando, botao solto) e confere o resultado: 1) entre os dois
#             paineis do app (um item, e dois marcados com Ctrl para copiar); 2) do app para uma janela real do Windows Explorer e dela
#             de volta para o app; 3) do app para uma pagina em cada navegador instalado (Chrome, Edge, Firefox), cada um com um perfil
#             temporario so seu: os navegadores abertos do usuario nao sao tocados. O app vai para o canto da tela e as outras janelas
#             ficam ao lado dele. Mesmo formato de resultado, de pasta de teste e de restauracao do estado do usuario de delfocus e
#             multisel (AGENTS.md, armadilha 20).
#   marqueedrag: seleciona 4 itens ARRASTANDO o mouse na borda esquerda (retangulo de selecao) e arrasta a selecao para o outro painel (tem
#             de levar os 4); o painel de origem nao pode ir para outra pasta ao terminar; fotografa o destino destacado durante o arrasto e
#             solta uma pasta na RAIZ da unidade (linha C: aberta). Mesmo formato de resultado e de restauracao dos outros roteiros.
#   rename  : F2 e clique devagar no nome (como no Explorer): a caixa abre ja com o foco, o nome vem selecionado sem a extensao (pasta: tudo),
#             selecionar o texto com o mouse nao arrasta o arquivo, Enter renomeia no disco e Esc cancela.
#   newfeatures: menu de contexto da Lixeira (so le; recusa a confirmacao de esvaziar), Localizar (Abrir em Janela e coluna ajustavel) e a janela
#             de Duplicados aberta pelo botao do grupo Pasta, nos tipos Padrao e Profundo.
#   about   : abre Ajuda > Sobre e confere que a tela mostra a versao declarada em <Version> no csproj do app (AGENTS.md: versao).
#             Mesmo formato de resultado e de restauracao do estado do usuario dos outros roteiros com [OK] e [FALHOU].
#   favmenu : clica com o botao direito de verdade num arquivo, numa pasta e num favorito da pasta Favoritos, le as entradas do menu de
#             contexto e usa Adicionar aos favoritos e Remover dos favoritos, conferindo o favorites.json (AGENTS.md, armadilha 21).
#   props   : botao direito > Propriedades num arquivo, numa pasta, em dois itens e numa unidade; le as linhas da janela pelo UI Automation,
#             confere contra o disco (o SHA-256 contra o Get-FileHash) e fotografa nos temas escuro e claro (AGENTS.md, armadilha 22).
#   menu    : confere o menu superior (Exibir, Temas, Preferencia, Ajuda; sem Arquivo nem Ferramentas), as entradas novas de Exibir e de
#             Preferencia com o estado de cada check, e liga e desliga "Exibir unidades bloqueadas" de verdade (confere o settings.json).
#             NUNCA aciona "Iniciar sempre como administrador": marcar pede o aviso do Windows (UAC), que roda numa area protegida e nao
#             pode ser aceito nem recusado por roteiro; so le o estado do check e confere que o settings.json nao mudou.
#   delfocus, multisel, dragdrop, about, favmenu, props e menu guardam %AppData%\DoraExplorer (configuracoes, favoritos, indice) antes de abrir o app e devolvem igual no
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
if ($Scenario -in 'delfocus', 'multisel', 'dragdrop', 'about', 'favmenu', 'props', 'menu', 'marqueedrag', 'rename', 'newfeatures') {
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
    $fileCount = switch ($Scenario) { 'delfocus' { 5 } 'multisel' { 6 } 'favmenu' { 2 } 'props' { 2 } default { 0 } }
    if ($fileCount -gt 0) { 1..$fileCount | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "arq$_.txt") -Value ('x' * ($_ * 100)) -Encoding Ascii } }
    if ($Scenario -eq 'dragdrop') {
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'src') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'dest') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'dest2') -Force | Out-Null
        'a', 'b', 'c' | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "src\$_.txt") -Value "arquivo $_" -Encoding Ascii }
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'out') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'exp') -Force | Out-Null
        'o1', 'o2', 'o3' | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "out\$_.txt") -Value "saida $_" -Encoding Ascii }
        Set-Content -LiteralPath (Join-Path $uiRoot 'exp\in1.txt') -Value 'entrada in1' -Encoding Ascii
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'web') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $uiRoot 'web\w1.txt') -Value 'para o navegador' -Encoding Ascii
    }
    if ($Scenario -eq 'marqueedrag') {
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'src') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'dest') -Force | Out-Null
        1..6 | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "src\m$_.txt") -Value "marquee $_" -Encoding Ascii }
        # Pasta que vai ser solta na RAIZ de C: (vira C:\<nome>); entra na limpeza para nao ficar la.
        $script:rootDropName = 'DoraRootDrop' + (Get-Date -Format 'HHmmss')
        New-Item -ItemType Directory -Path (Join-Path $uiRoot ('src\' + $script:rootDropName)) -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $uiRoot ('src\' + $script:rootDropName + '\dentro.txt')) -Value 'x' -Encoding Ascii
        $cleanup.Add('C:\' + $script:rootDropName)
    }
    if ($Scenario -eq 'rename') {
        1..2 | ForEach-Object { Set-Content -LiteralPath (Join-Path $uiRoot "relatorio.final$_.docx") -Value 'x' -Encoding Ascii }
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'pasta.v2') -Force | Out-Null
    }
    if ($Scenario -eq 'newfeatures') {
        # Subpastas fundas com um arquivo raro (Localizar) e pares de arquivos iguais (Duplicados).
        $deep = Join-Path $uiRoot 'a\b\c\d\e'
        New-Item -ItemType Directory -Path $deep -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $deep 'achado-raro.txt') -Value 'conteudo raro' -Encoding Ascii
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'copias1') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $uiRoot 'copias2') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias1\igual.txt') -Value 'mesmo conteudo' -Encoding Ascii
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias2\igual.txt') -Value 'mesmo conteudo' -Encoding Ascii
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias2\outro-nome.txt') -Value 'mesmo conteudo' -Encoding Ascii
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias1\diferente.txt') -Value 'AAAAAAAAAAAAAA' -Encoding Ascii
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias2\diferente.txt') -Value 'BBBBBBBBBBBBBB' -Encoding Ascii
        # Um par maior (5 KB): a lista tem de comecar por ele (maiores primeiro).
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias1\grande.bin') -Value ('z' * 5000) -Encoding Ascii
        Set-Content -LiteralPath (Join-Path $uiRoot 'copias2\grande.bin') -Value ('z' * 5000) -Encoding Ascii
    }
    if ($Scenario -in 'multisel', 'favmenu', 'props') {
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
    # "Iniciar sempre como administrador" ligado no settings.json do usuario faz o app se reabrir elevado logo ao abrir: o roteiro perde o
    # controle (o terminal comum nao consegue fecha-lo nem a UI Automation e o mouse alcancam uma janela elevada) e a janela fica aberta na
    # tela do usuario. So durante o roteiro a opcao e desligada; Restore-UserState devolve o settings.json byte a byte igual.
    $settingsFile = Join-Path $appData 'settings.json'
    if (Test-Path -LiteralPath $settingsFile) {
        $settingsText = [IO.File]::ReadAllText($settingsFile)
        if ($settingsText -match '"StartAsAdministrator"\s*:\s*true') {
            [IO.File]::WriteAllText($settingsFile, ($settingsText -replace '"StartAsAdministrator"\s*:\s*true', '"StartAsAdministrator": false'), (New-Object Text.UTF8Encoding($false)))
            'Iniciar sempre como administrador estava ligado: desligado so durante o roteiro (o settings.json volta igual no fim)'
        }
    }
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
    # Ponto (X, Y) na linha de um item da arvore, para o mouse apertar ou soltar ali.
    function Row-Point($row, $dx = 120) {
        $r = $row.Current.BoundingRectangle
        [pscustomobject]@{ X = [int]($r.Left + $dx); Y = [int]($r.Top + $r.Height / 2) }
    }
    # Arrasta de verdade com o mouse: aperta em $from, anda ate $to em passos (o arrasto precisa ser visto andando) e solta.
    # $ctrl segura Ctrl so DEPOIS de apertar (Ctrl no clique mudaria a selecao) e ate depois de soltar: o destino ve a tecla e copia.
    function Drag-Mouse($hwnd, $from, $to, $ctrl = $false) {
        if (-not (Ensure-Foreground $hwnd)) { return $false }
        [void][Kb]::SetCursorPos([int]$from.X, [int]$from.Y); Start-Sleep -Milliseconds 200
        [Kb]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
        if ($ctrl) { [Kb]::keybd_event(0xA2, 0x1D, 0, [UIntPtr]::Zero) }
        $steps = 25
        for ($i = 1; $i -le $steps; $i++) {
            [void][Kb]::SetCursorPos([int]($from.X + ($to.X - $from.X) * $i / $steps), [int]($from.Y + ($to.Y - $from.Y) * $i / $steps))
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 400
        [Kb]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        if ($ctrl) { Start-Sleep -Milliseconds 150; [Kb]::keybd_event(0xA2, 0x1D, 2, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 2000
        return $true
    }
    # Item da pasta de teste (ou de uma subpasta dela) no painel $treeIndex (0 = esquerdo, 1 = direito). Acha de novo a cada chamada:
    # depois de uma transferencia o painel e recarregado e os elementos antigos deixam de valer.
    function Ui-Item($rootEl, $treeIndex, $leaf = $null) {
        $c = (TopItems $rootEl $treeIndex)[0]
        $ui = Child $c (Split-Path $uiRoot -Leaf)
        if (-not $ui -or -not $leaf) { return $ui }
        return Child $ui $leaf
    }
    function Open-UiTree($rootEl, $treeIndex) {
        $c = (TopItems $rootEl $treeIndex)[0]; Expand $c
        $ui = Child $c (Split-Path $uiRoot -Leaf)
        if (-not $ui) { return $null }
        $deadline = [DateTime]::Now.AddSeconds(20)
        while (("" + $ui.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState) -eq 'LeafNode' -and [DateTime]::Now -lt $deadline) { Start-Sleep -Milliseconds 500 }
        $ui.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 1500
        return $ui
    }
    # Janela do Explorer do Windows aberta numa pasta de teste, achada pelo nome da pasta (a janela do usuario nunca e tocada).
    function Open-Explorer($folder) {
        Start-Process explorer.exe -ArgumentList "/n,`"$folder`""
        $leaf = Split-Path $folder -Leaf
        $cond = New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'CabinetWClass')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 20000) {
            $w = $AE::RootElement.FindAll($TS::Children, $cond) | Where-Object { $_.Current.Name -like "$leaf*" } | Select-Object -First 1
            if ($w) { Start-Sleep -Milliseconds 1500; return $w }
            Start-Sleep -Milliseconds 300
        }
        return $null
    }
    function Close-Window($w) { try { $w.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch { } }
    # Poe uma janela de outro programa ao lado da janela do app (a tela tem espaco a direita), sem cobrir o app.
    function Place-Beside($w, $appHandle) {
        $r = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($appHandle, [ref]$r)
        $x = $r.Right + 10; $width = [Math]::Max(500, [Math]::Min(700, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - $x - 10))
        $tp = $w.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern)
        $tp.Move($x, $r.Top); $tp.Resize($width, 560); Start-Sleep -Milliseconds 1200
    }
    function Window-Point($w, $fx = 0.5, $fy = 0.7) {
        $r = $w.Current.BoundingRectangle
        [pscustomobject]@{ X = [int]($r.Left + $r.Width * $fx); Y = [int]($r.Top + $r.Height * $fy) }
    }
    function Explorer-Item($w, $nameLike) {
        $w.FindAll($TS::Descendants, (Cond $CT::ListItem)) | Where-Object { $_.Current.Name -like "*$nameLike*" } | Select-Object -First 1
    }
    # Navegadores para o teste de soltar arquivos numa pagina. Cada um abre com um perfil temporario so seu (os navegadores abertos
    # do usuario nao sao tocados) e, no fim, so os processos desse perfil sao encerrados.
    function Get-TestBrowsers {
        @(
            [pscustomobject]@{ Name = 'Chrome'; Exe = 'C:\Program Files\Google\Chrome\Application\chrome.exe'; Kind = 'chromium' }
            [pscustomobject]@{ Name = 'Edge'; Exe = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'; Kind = 'chromium' }
            [pscustomobject]@{ Name = 'Firefox'; Exe = 'C:\Program Files\Mozilla Firefox\firefox.exe'; Kind = 'firefox' }
        ) | Where-Object { Test-Path -LiteralPath $_.Exe }
    }
    function Open-DropBrowser($browser, $pageFile) {
        $profileDir = Join-Path $env:TEMP ('dora-browser-' + $browser.Name + '-' + (Get-Date -Format 'HHmmss'))
        New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
        $url = 'file:///' + ($pageFile -replace '\\', '/')
        # Os avisos de primeiro uso (sincronizar a conta do Windows no Edge, aceitar termos no Firefox) sao telas modais que engolem
        # o soltar e que ninguem deve aceitar por quem testa: o Edge abre em InPrivate (sem sincronizacao) e o Firefox recebe, neste
        # perfil temporario, as preferencias que desligam a tela de boas-vindas.
        if ($browser.Kind -eq 'firefox') {
            @('browser.preonboarding.enabled', 'browser.aboutwelcome.enabled', 'browser.shell.checkDefaultBrowser', 'datareporting.policy.dataSubmissionEnabled') |
                ForEach-Object { 'user_pref("' + $_ + '", false);' } | Set-Content -LiteralPath (Join-Path $profileDir 'user.js') -Encoding Ascii
            Add-Content -LiteralPath (Join-Path $profileDir 'user.js') -Value 'user_pref("browser.startup.homepage_override.mstone", "ignore");' -Encoding Ascii
        }
        $argList = if ($browser.Kind -eq 'firefox') { "-no-remote -profile `"$profileDir`" -new-window `"$url`"" }
                   else { "--user-data-dir=`"$profileDir`" --no-first-run --no-default-browser-check" + $(if ($browser.Name -eq 'Edge') { ' --inprivate' } else { '' }) + " --new-window `"$url`"" }
        Start-Process -FilePath $browser.Exe -ArgumentList $argList
        $sw = [Diagnostics.Stopwatch]::StartNew(); $w = $null
        while (-not $w -and $sw.ElapsedMilliseconds -lt 30000) {
            $w = $AE::RootElement.FindAll($TS::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like '*DoraDrop*' } | Select-Object -First 1
            if (-not $w) { Start-Sleep -Milliseconds 400 }
        }
        [pscustomobject]@{ Window = $w; Profile = $profileDir }
    }
    function Close-DropBrowser($opened) {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$($opened.Profile)*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 800
        Remove-Item -LiteralPath $opened.Profile -Recurse -Force -ErrorAction SilentlyContinue
    }
    # A pasta ganhou (ou perdeu) a seta de expandir? A seta e o filho provisorio da linha; na automacao, LeafNode = sem seta.
    function Has-Arrow($rootEl, $treeIndex, $leaf) {
        $item = Ui-Item $rootEl $treeIndex $leaf
        if (-not $item) { return $false }
        try { return (("" + $item.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState) -ne 'LeafNode') } catch { return $false }
    }
    function Wait-Arrow($rootEl, $treeIndex, $leaf, $want = $true, $ms = 4000) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) { if ((Has-Arrow $rootEl $treeIndex $leaf) -eq $want) { return $true }; Start-Sleep -Milliseconds 250 }
        return ((Has-Arrow $rootEl $treeIndex $leaf) -eq $want)
    }
    # Entradas do menu de contexto aberto: o UI Automation as expoe como filhas de uma janela SEM NOME dentro da janela principal
    # (a barra de menus do app, Arquivo/Exibir/..., nao fica dentro de uma janela dessas).
    function Popup-MenuItems($hwnd) {
        $main = $AE::FromHandle($hwnd)
        foreach ($w in $main.FindAll($TS::Descendants, (Cond $CT::Window))) {
            foreach ($m in $w.FindAll($TS::Descendants, (Cond $CT::MenuItem))) { $m }
        }
    }
    # Clique com o botao DIREITO de verdade na linha e as entradas do menu de contexto que abriu. Esc fecha o menu.
    function Right-Click-Menu($hwnd, $row, $procId, $firstLine = $false) {
        if (-not (Ensure-Foreground $hwnd)) { return @() }
        $r = $row.Current.BoundingRectangle
        # uma pasta ou unidade expandida tem o retangulo da subarvore inteira: o meio dele cai num filho, e o botao direito agiria sobre ele
        $y = if ($firstLine) { [int]($r.Top + 14) } else { [int]($r.Top + $r.Height / 2) }
        [void][Kb]::SetCursorPos([int]($r.Left + 120), $y); Start-Sleep -Milliseconds 250
        [Kb]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
        [Kb]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 900
        return @(Popup-MenuItems $hwnd | ForEach-Object { $_.Current.Name })
    }
    function Pick-Menu-Entry($procId, $hwnd, $name) {
        $m = Popup-MenuItems $hwnd | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
        if (-not $m) { return $false }
        $m.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 1200
        return $true
    }
    function Close-Menu { [Kb]::Tap(0x1B); Start-Sleep -Milliseconds 500 }
    # Menu superior: o item de topo pelo nome, aberto, e as entradas dele (so leitura; nada e acionado).
    function Open-TopMenu($rootEl, $top) {
        $m = $rootEl.FindAll($TS::Descendants, (Cond $CT::MenuItem)) | Where-Object { $_.Current.Name -eq $top } | Select-Object -First 1
        if (-not $m) { return @() }
        $m.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700
        return @($m.FindAll($TS::Descendants, (Cond $CT::MenuItem)))
    }
    function Drive-Names($rootEl, $treeIndex) { @(TopItems $rootEl $treeIndex | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^[A-Z]:' }) }
    function Has-Drive($rootEl, $treeIndex, $letter) { @(Drive-Names $rootEl $treeIndex | Where-Object { $_ -match ('^' + [regex]::Escape($letter) + '( |$)') }).Count -gt 0 }
    function Toggle-State($el) { try { return ('' + $el.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState) } catch { return 'sem-check' } }
    function Entry($entries, $name) { $entries | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1 }
    function Snap-Screen($hwnd, $name) {
        $r = New-Object UiCap+RECT; [void][UiCap]::GetWindowRect($hwnd, [ref]$r)
        $bm = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top); $g = [System.Drawing.Graphics]::FromImage($bm)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bm.Size); $g.Dispose()
        $file = Join-Path $outDir "$Prefix-$name.png"; $bm.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bm.Dispose()
        "captura $Prefix-$name.png"
    }
    function Setting-Value($section, $name) {
        $f = Join-Path $appData 'settings.json'
        if (-not (Test-Path -LiteralPath $f)) { return $null }
        $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
        $target = if ($section) { $j.$section } else { $j }
        if ($null -eq $target) { return $null }
        return $target.$name
    }
    function Favorites-Paths { $f = Join-Path $appData 'favorites.json'; if (Test-Path -LiteralPath $f) { @((Get-Content -LiteralPath $f -Raw | ConvertFrom-Json) | Where-Object { $_ }) } else { @() } }
    # As linhas da janela Propriedades: cada valor (uma caixa de texto somente leitura) com o rotulo que vem logo antes dele.
    function Read-Props($dialog) {
        $or = New-Object Windows.Automation.OrCondition((Cond $CT::Text), (Cond $CT::Edit))
        $map = [ordered]@{}; $last = $null
        foreach ($e in $dialog.FindAll($TS::Descendants, $or)) {
            if ($e.Current.ControlType.Id -eq $CT::Edit.Id) {
                $v = try { $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $e.Current.Name }
                if ($last) { $map[$last] = $v }
            } else { $last = $e.Current.Name }
        }
        return $map
    }
    function Prop($map, $labelRegex) { foreach ($k in $map.Keys) { if ($k -match $labelRegex) { return $map[$k] } }; return $null }
    function Wait-Dialog($procId, $title, $ms = 8000) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) {
            $d = [UiCap]::FindByTitle([uint32]$procId, $title)
            if ($d -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 700; return $d }
            Start-Sleep -Milliseconds 200
        }
        return [IntPtr]::Zero
    }
    # Botao direito na linha, confere que Propriedades e a ULTIMA entrada do menu, usa-a e devolve o identificador da janela aberta.
    function Open-Properties($hwnd, $row, $procId, $title, $firstLine = $false) {
        $entries = @(Right-Click-Menu $hwnd $row $procId $firstLine)
        # o texto do Check vai para a tela (Write-Host): se fosse para o fluxo, a funcao devolveria a linha junto com o identificador da janela
        Write-Host (Check "o menu de '$title' termina com Propriedades" (($entries.Count -gt 0) -and ($entries[-1] -eq 'Propriedades')) ($entries -join ' | '))
        if (-not (Pick-Menu-Entry $procId $hwnd 'Propriedades')) { return [IntPtr]::Zero }
        return (Wait-Dialog $procId $title)
    }
    function Press-DialogButton($dlg, $name) {
        $b = $AE::FromHandle($dlg).FindAll($TS::Descendants, (Cond $CT::Button)) | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
        if ($b) { $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900; return $true }
        return $false
    }
    function Names-In($folder) { (@(Get-ChildItem -LiteralPath $folder -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ',') }
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
        'dragdrop' {
            # Arrastar de verdade com o mouse. Etapa 1: entre os dois paineis do proprio app (mesmo disco: o padrao e mover).
            # O app vai para o canto esquerdo da tela: a direita sobra espaco para as janelas de outros programas.
            $root.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Move(0, 0); Start-Sleep -Milliseconds 800
            $srcPath = Join-Path $uiRoot 'src'; $destPath = Join-Path $uiRoot 'dest'
            $left = Open-UiTree $root 0
            $right = Open-UiTree $root 1
            if (-not $left -or -not $right) { 'a pasta de teste nao apareceu nos dois paineis'; break }
            Expand (Child $left 'src')
            Expand (Child $right 'src')
            $a = Wait-Row (Ui-Item $root 0 'src') 'a.txt'
            Check 'a.txt aparece no painel da esquerda' ($null -ne $a)
            if (-not $a -or -not (Ui-Item $root 1 'dest')) { break }
            [void](Drag-Mouse $h (Row-Point $a) (Row-Point (Ui-Item $root 1 'dest')))
            Check 'arrastar a.txt para a pasta dest do painel da direita move o arquivo' ((Test-Path -LiteralPath (Join-Path $destPath 'a.txt')) -and -not (Test-Path -LiteralPath (Join-Path $srcPath 'a.txt'))) ("dest=" + (Names-In $destPath) + " src=" + (Names-In $srcPath))
            Snap $h 'dragdrop-entre-paineis'
            Check 'depois de soltar, a pasta dest ganha a seta de expandir no painel da esquerda (o de origem)' (Wait-Arrow $root 0 'dest')
            Check 'depois de soltar, a pasta dest ganha a seta de expandir no painel da direita (onde foi solto)' (Wait-Arrow $root 1 'dest')
            Check 'a pasta src, de onde a.txt saiu, continua com a seta nos dois paineis (ainda tem b.txt e c.txt)' ((Wait-Arrow $root 0 'src') -and (Wait-Arrow $root 1 'src'))

            # Varios itens: b e c marcados, arrastados com Ctrl (copiar). Apertar num item ja marcado nao pode desfazer a selecao.
            $b = Wait-Row (Ui-Item $root 0 'src') 'b.txt'
            [void](Click-With $h $b 'none')
            [void](Click-With $h (Row (Ui-Item $root 0 'src') 'c.txt') 'ctrl')
            $s = (Status-Lines $root) -join ' || '
            Check 'b.txt e c.txt estao marcados antes de arrastar (2 itens)' ($s -match '^2 itens selecionados') $s
            [void](Drag-Mouse $h (Row-Point (Row (Ui-Item $root 0 'src') 'c.txt')) (Row-Point (Ui-Item $root 1 'dest')) $true)
            Check 'Ctrl+arrastar copia os dois itens marcados para dest' ((Test-Path -LiteralPath (Join-Path $destPath 'b.txt')) -and (Test-Path -LiteralPath (Join-Path $destPath 'c.txt'))) ("dest=" + (Names-In $destPath))
            Check 'a copia deixa b.txt e c.txt na pasta de origem' ((Test-Path -LiteralPath (Join-Path $srcPath 'b.txt')) -and (Test-Path -LiteralPath (Join-Path $srcPath 'c.txt'))) ("src=" + (Names-In $srcPath))

            # Etapa 2: do app para o Windows Explorer (uma janela de verdade, ao lado do app) e de volta.
            $outPath = Join-Path $uiRoot 'out'; $expPath = Join-Path $uiRoot 'exp'
            $explorer = Open-Explorer $expPath
            Check 'uma janela do Explorer abriu na pasta exp' ($null -ne $explorer)
            if ($explorer) {
                Place-Beside $explorer $h
                Check 'o Explorer mostra in1.txt, que ja estava em exp' ($null -ne (Explorer-Item $explorer 'in1'))
                Expand (Ui-Item $root 0 'out')
                $o1 = Wait-Row (Ui-Item $root 0 'out') 'o1.txt'
                if ($o1) {
                    [void](Drag-Mouse $h (Row-Point $o1) (Window-Point $explorer))
                    Check 'arrastar o1.txt do app para a janela do Explorer entrega o arquivo em exp' (Test-Path -LiteralPath (Join-Path $expPath 'o1.txt')) ("exp=" + (Names-In $expPath) + " out=" + (Names-In $outPath))
                    Snap $h 'dragdrop-para-o-explorer'
                    Check 'para outro programa so se copia: o1.txt continua na pasta de origem do app' (Test-Path -LiteralPath (Join-Path $outPath 'o1.txt')) ("out=" + (Names-In $outPath))
                }
                # varios itens do app para o Explorer
                [void](Click-With $h (Row (Ui-Item $root 0 'out') 'o2.txt') 'none')
                [void](Click-With $h (Row (Ui-Item $root 0 'out') 'o3.txt') 'ctrl')
                [void](Drag-Mouse $h (Row-Point (Row (Ui-Item $root 0 'out') 'o3.txt')) (Window-Point $explorer))
                Check 'arrastar dois itens marcados do app para o Explorer entrega os dois' ((Test-Path -LiteralPath (Join-Path $expPath 'o2.txt')) -and (Test-Path -LiteralPath (Join-Path $expPath 'o3.txt'))) ("exp=" + (Names-In $expPath))
                Check 'os dois itens arrastados para o Explorer continuam na pasta de origem do app' ((Test-Path -LiteralPath (Join-Path $outPath 'o2.txt')) -and (Test-Path -LiteralPath (Join-Path $outPath 'o3.txt'))) ("out=" + (Names-In $outPath))
                # do Explorer para o app: in1.txt vai da janela do Explorer para a pasta dest do painel da direita
                $in1 = Explorer-Item $explorer 'in1'
                $dest2Path = Join-Path $uiRoot 'dest2'
                $destRow = Ui-Item $root 1 'dest2'
                if ($in1 -and $destRow) {
                    [void](Drag-Mouse $h (Row-Point $in1 40) (Row-Point $destRow))
                    Check 'arrastar in1.txt da janela do Explorer para a pasta vazia dest2 do app entrega o arquivo' (Test-Path -LiteralPath (Join-Path $dest2Path 'in1.txt')) ("dest2=" + (Names-In $dest2Path) + " exp=" + (Names-In $expPath))
                    Check 'depois de soltar do Explorer, dest2 ganha a seta de expandir no painel da direita (onde foi solto)' (Wait-Arrow $root 1 'dest2')
                    Check 'depois de soltar do Explorer, dest2 ganha a seta de expandir no painel da esquerda tambem' (Wait-Arrow $root 0 'dest2')
                    Snap $h 'dragdrop-do-explorer'
                } else { Check 'in1.txt no Explorer e dest no app foram achados para o arrasto' $false }
                Close-Window $explorer
            }

            # Etapa 3: do app para um navegador (pagina local que aceita soltar arquivos e troca o proprio titulo).
            $webPath = Join-Path $uiRoot 'web'
            $page = Join-Path $uiRoot 'drop.html'
            $html = '<!doctype html><title>DoraDrop</title><body style="margin:0;font:24px sans-serif"><div style="height:100vh;display:flex;align-items:center;justify-content:center">Solte aqui</div><script>addEventListener("dragover",function(e){e.preventDefault()});addEventListener("drop",function(e){e.preventDefault();document.title="DROP:"+Array.from(e.dataTransfer.files).map(function(f){return f.name}).join(",")});</script></body>'
            Set-Content -LiteralPath $page -Value $html -Encoding Ascii
            $browsers = @(Get-TestBrowsers)
            if ($browsers.Count -eq 0) { 'nenhum navegador (Chrome, Edge ou Firefox) instalado: etapa dos navegadores pulada' }
            Expand (Ui-Item $root 0 'web')
            foreach ($browser in $browsers) {
                $opened = Open-DropBrowser $browser $page
                Check "o $($browser.Name) abriu a pagina de teste" ($null -ne $opened.Window)
                if ($opened.Window) {
                    try { $opened.Window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal) } catch { }
                    Place-Beside $opened.Window $h
                    $w1 = Wait-Row (Ui-Item $root 0 'web') 'w1.txt'
                    if ($w1) {
                        [void](Drag-Mouse $h (Row-Point $w1) (Window-Point $opened.Window 0.5 0.5))
                        Start-Sleep -Milliseconds 1000
                        $title = $opened.Window.Current.Name
                        Check "arrastar w1.txt do app para uma pagina no $($browser.Name) entrega o arquivo" ($title -like '*DROP:w1.txt*') $title
                        Check "o arrasto para o $($browser.Name) nao move nem apaga o original" (Test-Path -LiteralPath (Join-Path $webPath 'w1.txt'))
                    } else { Check 'w1.txt apareceu no painel do app' $false }
                }
                Close-DropBrowser $opened
            }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'about' {
            # A tela Sobre mostra a versao declarada no csproj do app: uma versao so, em um lugar so.
            $csproj = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\DoraExplorer.App\DoraExplorer.App.csproj') -Raw)
            $declared = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
            Check 'o csproj do app declara a versao' (-not [string]::IsNullOrWhiteSpace($declared)) "$declared"
            [void](Menu-Invoke $root 'Ajuda' 'Sobre*')
            $dlg = [IntPtr]::Zero; $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($dlg -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 6000) { $dlg = [UiCap]::FindByTitle([uint32]$proc.Id, 'Sobre o Dora Explorer'); Start-Sleep -Milliseconds 200 }
            Check 'a janela Sobre abriu' ($dlg -ne [IntPtr]::Zero)
            if ($dlg -ne [IntPtr]::Zero) {
                $dialog = $AE::FromHandle($dlg)
                $texts = @($dialog.FindAll($TS::Descendants, (Cond $CT::Text)) | ForEach-Object { $_.Current.Name })
                Snap $dlg 'about-sobre'
                Check "a tela Sobre mostra a versao $declared" (@($texts | Where-Object { $_ -match ('^Vers.o ' + [regex]::Escape($declared) + '$') }).Count -eq 1) ($texts -join ' | ')
                $ok = $dialog.FindAll($TS::Descendants, (Cond $CT::Button)) | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1
                if ($ok) { $ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
            }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'favmenu' {
            # Botao direito: o menu de contexto tem de oferecer Adicionar aos favoritos (arquivo e pasta) e, num favorito, Remover.
            $file1 = Join-Path $uiRoot 'arq1.txt'; $folder1 = Join-Path $uiRoot 'pasta'
            $left = Open-UiTree $root 0
            $right = Open-UiTree $root 1
            if (-not $left -or -not $right) { 'a pasta de teste nao apareceu nos dois paineis'; break }
            Check 'o app comeca sem favoritos de teste' (-not (@(Favorites-Paths) -contains $file1))

            $row = Wait-Row (Ui-Item $root 0) 'arq1.txt'
            $entries = @(Right-Click-Menu $h $row $proc.Id)
            Snap $h 'favmenu-arquivo'
            Check 'o menu de um arquivo oferece Adicionar aos favoritos' ($entries -contains 'Adicionar aos favoritos') ($entries -join ' | ')
            Check 'o menu de um arquivo nao oferece Remover dos favoritos (ainda nao e favorito)' (-not ($entries -contains 'Remover dos favoritos')) ($entries -join ' | ')
            Check 'usar Adicionar aos favoritos grava o arquivo em favorites.json' ((Pick-Menu-Entry $proc.Id $h 'Adicionar aos favoritos') -and (@(Favorites-Paths) -contains $file1)) ((Favorites-Paths) -join ' ; ')

            $entries = @(Right-Click-Menu $h (Wait-Row (Ui-Item $root 0) 'arq1.txt') $proc.Id)
            Check 'depois de favoritado, o mesmo arquivo passa a oferecer Remover dos favoritos' (($entries -contains 'Remover dos favoritos') -and -not ($entries -contains 'Adicionar aos favoritos')) ($entries -join ' | ')
            Close-Menu

            $entries = @(Right-Click-Menu $h (Row (Ui-Item $root 0) 'pasta') $proc.Id)
            Check 'o menu de uma pasta oferece Adicionar aos favoritos' ($entries -contains 'Adicionar aos favoritos') ($entries -join ' | ')
            Check 'usar Adicionar aos favoritos numa pasta grava a pasta em favorites.json' ((Pick-Menu-Entry $proc.Id $h 'Adicionar aos favoritos') -and (@(Favorites-Paths) -contains $folder1)) ((Favorites-Paths) -join ' ; ')

            # na pasta Favoritos (painel da direita, que o painel da esquerda manteve em dia): o favorito oferece Remover
            $favRoot = @(TopItems $root 1) | Where-Object { $_.Current.Name -eq 'Favoritos' } | Select-Object -First 1
            Check 'a pasta Favoritos existe no painel da direita' ($null -ne $favRoot)
            if ($favRoot) {
                try { Expand $favRoot } catch { 'a pasta Favoritos esta vazia (nada para expandir)' }
                $entry = Wait-Row $favRoot 'arq1.txt'
                Check 'o favorito arq1.txt aparece na pasta Favoritos do outro painel' ($null -ne $entry)
                if ($entry) {
                    $entries = @(Right-Click-Menu $h $entry $proc.Id)
                    Snap $h 'favmenu-favorito'
                    Check 'o menu de um favorito na pasta Favoritos oferece Remover dos favoritos' (($entries -contains 'Remover dos favoritos') -and -not ($entries -contains 'Adicionar aos favoritos')) ($entries -join ' | ')
                    Check 'usar Remover dos favoritos tira arq1.txt de favorites.json e mantem a pasta' ((Pick-Menu-Entry $proc.Id $h 'Remover dos favoritos') -and -not (@(Favorites-Paths) -contains $file1) -and (@(Favorites-Paths) -contains $folder1)) ((Favorites-Paths) -join ' ; ')
                }
            }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'menu' {
            $Preferencia = 'Prefer' + [char]0xEA + 'ncia'
            $bar = $root.FindAll($TS::Descendants, (Cond $CT::Menu)) | Select-Object -First 1
            $tops = if ($bar) { @($bar.FindAll($TS::Children, (Cond $CT::MenuItem)) | ForEach-Object { $_.Current.Name }) } else { @() }
            Check 'o menu superior tem Exibir, Temas, Preferencia e Ajuda, nessa ordem' (($tops -join '|') -eq ('Exibir|Temas|' + $Preferencia + '|Ajuda')) ($tops -join ' | ')
            Check 'o menu Arquivo nao existe mais' (($tops.Count -gt 0) -and -not ($tops -contains 'Arquivo')) ($tops -join ' | ')
            Check 'o menu Ferramentas nao existe mais' (($tops.Count -gt 0) -and -not ($tops -contains 'Ferramentas')) ($tops -join ' | ')

            $locked = @([IO.DriveInfo]::GetDrives() | Where-Object { -not $_.IsReady -and ($_.DriveType -eq 'Fixed' -or $_.DriveType -eq 'Removable') } | ForEach-Object { $_.Name.TrimEnd('\') })
            "unidades travadas nesta maquina: " + $(if ($locked.Count) { $locked -join ', ' } else { 'nenhuma (as verificacoes da arvore ficam de fora)' })
            foreach ($letter in $locked) { foreach ($ti in 0, 1) { Check "com a opcao ligada (padrao), $letter aparece no painel $ti" (Has-Drive $root $ti $letter) ((Drive-Names $root $ti) -join ' | ') } }
            $adminBefore = Setting-Value $null 'StartAsAdministrator'
            $lockedBefore = Setting-Value 'ExplorerView' 'ShowLockedDrives'

            $pref = Open-TopMenu $root $Preferencia
            $prefNames = @($pref | ForEach-Object { $_.Current.Name })
            Snap-Screen $h 'menu-preferencia'
            Check 'Preferencia lista Lembrar caminho e Iniciar sempre como administrador, nessa ordem' (($prefNames -join '|') -eq 'Lembrar caminho|Iniciar sempre como administrador') ($prefNames -join ' | ')
            $adminItem = Entry $pref 'Iniciar sempre como administrador'
            Check 'o item de administrador e um check (tem estado ligado/desligado)' ($adminItem -and (Toggle-State $adminItem) -in 'On', 'Off') (Toggle-State $adminItem)
            $expectedAdmin = if ($adminBefore -eq $true) { 'On' } else { 'Off' }
            Check "o check de administrador reflete a configuracao guardada ($expectedAdmin)" ($adminItem -and (Toggle-State $adminItem) -eq $expectedAdmin) (Toggle-State $adminItem)
            Close-Menu

            $view = Open-TopMenu $root 'Exibir'
            $viewNames = @($view | ForEach-Object { $_.Current.Name })
            Snap-Screen $h 'menu-exibir'
            $iExt = $viewNames.IndexOf('Exibir unidades externas'); $iLock = $viewNames.IndexOf('Exibir unidades bloqueadas')
            Check 'Exibir lista "Exibir unidades bloqueadas" logo depois de "Exibir unidades externas"' (($iExt -ge 0) -and ($iLock -eq $iExt + 1)) ($viewNames -join ' | ')
            $lockItem = Entry $view 'Exibir unidades bloqueadas'
            $expectedLock = if ($lockedBefore -eq $false) { 'Off' } else { 'On' }
            Check "o check de unidades bloqueadas vem $expectedLock (padrao ligado)" ($lockItem -and (Toggle-State $lockItem) -eq $expectedLock) (Toggle-State $lockItem)
            Close-Menu

            # desliga de verdade: o clique passa pelo comando, grava o settings.json e o check acompanha
            [void](Menu-Invoke $root 'Exibir' 'Exibir unidades bloqueadas')
            Check 'desmarcar grava ShowLockedDrives = false no settings.json' ((Setting-Value 'ExplorerView' 'ShowLockedDrives') -eq $false) ('' + (Setting-Value 'ExplorerView' 'ShowLockedDrives'))
            $lockItem = Entry (Open-TopMenu $root 'Exibir') 'Exibir unidades bloqueadas'
            Check 'o check fica desmarcado depois do clique' ($lockItem -and (Toggle-State $lockItem) -eq 'Off') (Toggle-State $lockItem)
            Close-Menu
            Start-Sleep -Milliseconds 800
            foreach ($letter in $locked) { foreach ($ti in 0, 1) { Check "com a opcao desligada, $letter some do painel $ti" (-not (Has-Drive $root $ti $letter)) ((Drive-Names $root $ti) -join ' | ') } }
            Check 'com as bloqueadas ocultas as unidades prontas continuam nos dois paineis' ((@(Drive-Names $root 0).Count -ge 1) -and (@(Drive-Names $root 1).Count -ge 1)) ((Drive-Names $root 0) -join ' | ')
            Snap-Screen $h 'menu-bloqueadas-ocultas'

            [void](Menu-Invoke $root 'Exibir' 'Exibir unidades bloqueadas')
            Check 'marcar de novo grava ShowLockedDrives = true no settings.json' ((Setting-Value 'ExplorerView' 'ShowLockedDrives') -eq $true) ('' + (Setting-Value 'ExplorerView' 'ShowLockedDrives'))
            $lockItem = Entry (Open-TopMenu $root 'Exibir') 'Exibir unidades bloqueadas'
            Check 'o check volta a marcado' ($lockItem -and (Toggle-State $lockItem) -eq 'On') (Toggle-State $lockItem)
            Close-Menu
            Start-Sleep -Milliseconds 800
            foreach ($letter in $locked) { foreach ($ti in 0, 1) { Check "ligando de novo, $letter volta ao painel $ti" (Has-Drive $root $ti $letter) ((Drive-Names $root $ti) -join ' | ') } }
            Snap-Screen $h 'menu-bloqueadas-visiveis'

            # ausente no arquivo = desligado: o app grava false ao salvar qualquer outra opcao
            Check 'o roteiro nao mexeu em StartAsAdministrator' (([bool](Setting-Value $null 'StartAsAdministrator')) -eq ([bool]$adminBefore)) ('' + (Setting-Value $null 'StartAsAdministrator'))
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'props' {
            # Botao direito > Propriedades: o que a janela mostra tem de bater com o disco, no tema escuro e no claro.
            $file1 = Join-Path $uiRoot 'arq1.txt'; $folder1 = Join-Path $uiRoot 'pasta'
            $left = Open-UiTree $root 0
            if (-not $left) { 'a pasta de teste nao apareceu no painel da esquerda'; break }
            $info1 = Get-Item -LiteralPath $file1

            # ---- um arquivo ----
            $dlg = Open-Properties $h (Wait-Row (Ui-Item $root 0) 'arq1.txt') $proc.Id 'Propriedades de arq1.txt'
            Check 'a janela Propriedades de arq1.txt abriu' ($dlg -ne [IntPtr]::Zero)
            if ($dlg -ne [IntPtr]::Zero) {
                $m = Read-Props ($AE::FromHandle($dlg))
                Check 'o caminho mostrado e o do arquivo' ((Prop $m '^Caminho') -eq $file1) ($m.Keys -join ' | ')
                Check "o tamanho mostrado bate com o disco ($($info1.Length) bytes)" ((Prop $m '^Tamanho$') -match ('\(' + [regex]::Escape($info1.Length.ToString('N0', [Globalization.CultureInfo]::GetCultureInfo('pt-BR'))) + ' bytes\)')) (Prop $m '^Tamanho$')
                Check 'o tamanho em disco aparece' ((Prop $m '^Tamanho em disco') -match 'bytes\)') (Prop $m '^Tamanho em disco')
                Check 'a data de modificacao tem dia da semana e hora' ((Prop $m '^Modificado') -match '^\w+-?\w*, \d+ de \w+ de \d{4}, \d{2}:\d{2}:\d{2}$') (Prop $m '^Modificado')
                Check 'o proprietario aparece' (-not [string]::IsNullOrWhiteSpace((Prop $m '^Propriet'))) (Prop $m '^Propriet')
                Check 'o aplicativo padrao aparece (Abre com)' (-not [string]::IsNullOrWhiteSpace((Prop $m '^Abre com'))) (Prop $m '^Abre com')
                Check 'o tipo mostra a extensao' ((Prop $m '^Tipo$') -match '\(\.txt\)$') (Prop $m '^Tipo$')
                Snap $dlg 'props-arquivo-escuro'
                # SHA-256 sob demanda, conferido contra o Windows
                Check 'o botao Calcular SHA-256 e MD5 existe' (Press-DialogButton $dlg 'Calcular SHA-256 e MD5')
                $expected = (Get-FileHash -LiteralPath $file1 -Algorithm SHA256).Hash.ToLowerInvariant()
                $expectedMd5 = (Get-FileHash -LiteralPath $file1 -Algorithm MD5).Hash.ToLowerInvariant()
                $sw = [Diagnostics.Stopwatch]::StartNew(); $hash = $null
                while ($sw.ElapsedMilliseconds -lt 8000) { $hash = Prop (Read-Props ($AE::FromHandle($dlg))) '^SHA-256'; if ($hash -match '^[0-9a-f]{64}$') { break }; Start-Sleep -Milliseconds 300 }
                Check 'o SHA-256 mostrado e o mesmo do Get-FileHash' ($hash -eq $expected) "$hash"
                $md5 = Prop (Read-Props ($AE::FromHandle($dlg))) '^MD5'
                Check 'o MD5 mostrado e o mesmo do Get-FileHash -Algorithm MD5' ($md5 -eq $expectedMd5) "$md5"
                Snap $dlg 'props-arquivo-hashes'
                # tema claro: a janela aberta muda junto?
                [void](Menu-Invoke $root 'Temas' 'Claro' 'Tema')
                Start-Sleep -Milliseconds 800
                Snap $dlg 'props-arquivo-claro'
                [void](Menu-Invoke $root 'Temas' 'Escuro' 'Tema')
                Check 'o botao OK fecha a janela' ((Press-DialogButton $dlg 'OK') -and ([UiCap]::FindByTitle([uint32]$proc.Id, 'Propriedades de arq1.txt') -eq [IntPtr]::Zero))
            }

            # ---- uma pasta (o que ha dentro chega depois) ----
            $dlg = Open-Properties $h (Row (Ui-Item $root 0) 'pasta') $proc.Id 'Propriedades de pasta'
            Check 'a janela Propriedades de pasta abriu' ($dlg -ne [IntPtr]::Zero)
            if ($dlg -ne [IntPtr]::Zero) {
                $sw = [Diagnostics.Stopwatch]::StartNew(); $m = $null
                while ($sw.ElapsedMilliseconds -lt 8000) { $m = Read-Props ($AE::FromHandle($dlg)); if ((Prop $m '^Conte.do') -match '^\d+ arquivo') { break }; Start-Sleep -Milliseconds 300 }
                Check 'o conteudo da pasta e contado (1 arquivo, 0 pastas)' ((Prop $m '^Conte.do') -eq '1 arquivo, 0 pastas') (Prop $m '^Conte.do')
                Check 'o tamanho da pasta e a soma do que ha dentro' ((Prop $m '^Tamanho$') -match '\(3 bytes\)') (Prop $m '^Tamanho$')
                Check 'a pasta nao tem o botao SHA-256 e MD5' (-not (Press-DialogButton $dlg 'Calcular SHA-256 e MD5'))
                Snap $dlg 'props-pasta'
                [void](Press-DialogButton $dlg 'OK')
            }

            # ---- dois itens ----
            [void](Click-With $h (Row (Ui-Item $root 0) 'arq1.txt') 'none')
            [void](Click-With $h (Row (Ui-Item $root 0) 'arq2.txt') 'ctrl')
            $dlg = Open-Properties $h (Row (Ui-Item $root 0) 'arq2.txt') $proc.Id 'Propriedades de 2 itens'
            Check 'a janela Propriedades de 2 itens abriu' ($dlg -ne [IntPtr]::Zero)
            if ($dlg -ne [IntPtr]::Zero) {
                $total = (Get-Item -LiteralPath $file1).Length + (Get-Item -LiteralPath (Join-Path $uiRoot 'arq2.txt')).Length
                $sw = [Diagnostics.Stopwatch]::StartNew(); $m = $null
                while ($sw.ElapsedMilliseconds -lt 8000) { $m = Read-Props ($AE::FromHandle($dlg)); if ((Prop $m '^Tamanho$') -match 'bytes\)') { break }; Start-Sleep -Milliseconds 300 }
                Check "o tamanho de dois itens e a soma ($total bytes)" ((Prop $m '^Tamanho$') -match ('\(' + [regex]::Escape($total.ToString('N0', [Globalization.CultureInfo]::GetCultureInfo('pt-BR'))) + ' bytes\)')) (Prop $m '^Tamanho$')
                Check 'o local comum aparece' ((Prop $m '^Local$') -eq $uiRoot) (Prop $m '^Local$')
                [void](Press-DialogButton $dlg 'OK')
            }

            # ---- uma unidade ----
            $driveRow = (TopItems $root 0)[0]
            $dlg = Open-Properties $h $driveRow $proc.Id ('Propriedades de ' + $driveRow.Current.Name) $true
            Check 'a janela Propriedades da unidade abriu' ($dlg -ne [IntPtr]::Zero) ('janelas do app: ' + (($AE::RootElement.FindAll($TS::Children, (New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id))) | ForEach-Object { $_.Current.Name }) -join ' | ') + '; linha: ' + $driveRow.Current.Name)
            if ($dlg -eq [IntPtr]::Zero) { Snap-Screen $h 'props-unidade-falha' }
            if ($dlg -ne [IntPtr]::Zero) {
                $m = Read-Props ($AE::FromHandle($dlg))
                Check 'a unidade mostra o sistema de arquivos' ((Prop $m '^Sistema de arquivos') -match '^\w+$') (Prop $m '^Sistema de arquivos')
                Check 'a unidade mostra a capacidade' ((Prop $m '^Capacidade') -match 'bytes\)') (Prop $m '^Capacidade')
                Snap $dlg 'props-unidade'
                [void](Press-DialogButton $dlg 'OK')
            }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'marqueedrag' {
            # Selecionar varios itens ARRASTANDO o mouse na borda esquerda (retangulo de selecao) e depois arrastar a selecao para o outro
            # painel; soltar na RAIZ de uma unidade aberta (linha C:); e a pasta de destino destacada enquanto o arrasto passa por cima.
            $root.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Move(0, 0); Start-Sleep -Milliseconds 800
            $srcPath = Join-Path $uiRoot 'src'; $destPath = Join-Path $uiRoot 'dest'
            $left = Open-UiTree $root 0
            $right = Open-UiTree $root 1
            if (-not $left -or -not $right) { 'a pasta de teste nao apareceu nos dois paineis'; break }
            Expand (Child $left 'src')
            $m1 = Wait-Row (Ui-Item $root 0 'src') 'm1.txt'
            Check 'os arquivos aparecem no painel da esquerda' ($null -ne $m1)
            if (-not $m1) { break }

            # retangulo de selecao: aperta na borda esquerda da linha m1 e arrasta ate a linha m4
            $r1 = (Row (Ui-Item $root 0 'src') 'm1.txt').Current.BoundingRectangle
            $r4 = (Row (Ui-Item $root 0 'src') 'm4.txt').Current.BoundingRectangle
            $from = [pscustomobject]@{ X = [int]($r1.Left + 8); Y = [int]($r1.Top + $r1.Height / 2) }
            $to = [pscustomobject]@{ X = [int]($r1.Left + 8); Y = [int]($r4.Top + $r4.Height / 2) }
            [void](Drag-Mouse $h $from $to)
            $s = (Status-Lines $root) -join ' || '
            Check 'o retangulo de selecao marca 4 itens (m1 a m4)' ($s -match '^4 itens selecionados') $s
            Snap-Screen $h 'marqueedrag-1-selecao'

            # arrastar a selecao pelo nome de m2 ate a pasta dest do painel da direita: tem de levar os QUATRO
            [void](Drag-Mouse $h (Row-Point (Row (Ui-Item $root 0 'src') 'm2.txt')) (Row-Point (Ui-Item $root 1 'dest')))
            $moved = @('m1', 'm2', 'm3', 'm4') | Where-Object { Test-Path -LiteralPath (Join-Path $destPath "$_.txt") }
            Check 'arrastar a selecao do retangulo leva os 4 arquivos marcados para dest' ($moved.Count -eq 4) ("dest=" + (Names-In $destPath) + " src=" + (Names-In $srcPath))
            Check 'm5 e m6, que nao estavam marcados, continuam na origem' ((Test-Path -LiteralPath (Join-Path $srcPath 'm5.txt')) -and (Test-Path -LiteralPath (Join-Path $srcPath 'm6.txt'))) (Names-In $srcPath)

            # o painel de origem nao pode ter ido para outra pasta (a pasta src continua aberta e com m5 e m6)
            Check 'o painel de origem continua mostrando a pasta src' ($null -ne (Wait-Row (Ui-Item $root 0 'src') 'm5.txt' 6000))
            Check 'o painel de destino continua mostrando a pasta de teste' ($null -ne (Ui-Item $root 1 'dest'))

            # destaque da pasta de destino durante o arrasto (foto com o botao ainda apertado)
            $p5 = Row-Point (Row (Ui-Item $root 0 'src') 'm5.txt'); $pd = Row-Point (Ui-Item $root 1 'dest')
            if (Ensure-Foreground $h) {
                [void][Kb]::SetCursorPos([int]$p5.X, [int]$p5.Y); Start-Sleep -Milliseconds 200
                [Kb]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
                for ($i = 1; $i -le 25; $i++) { [void][Kb]::SetCursorPos([int]($p5.X + ($pd.X - $p5.X) * $i / 25), [int]($p5.Y + ($pd.Y - $p5.Y) * $i / 25)); Start-Sleep -Milliseconds 40 }
                Start-Sleep -Milliseconds 900
                Snap-Screen $h 'marqueedrag-2-destaque'
                [Kb]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 2000
            }
            Check 'soltar depois da foto moveu m5 para dest' (Test-Path -LiteralPath (Join-Path $destPath 'm5.txt')) (Names-In $destPath)

            # soltar na RAIZ da unidade: arrasta a pasta de teste da raiz para a linha C: (aberta) do painel da direita
            $dropName = $script:rootDropName
            $folderRow = Wait-Row (Ui-Item $root 0 'src') $dropName 6000
            Check 'a pasta para soltar na raiz aparece no painel da esquerda' ($null -ne $folderRow)
            if ($folderRow) {
                $cRow = (TopItems $root 1)[0]
                $cr = $cRow.Current.BoundingRectangle
                $toRoot = [pscustomobject]@{ X = [int]($cr.Left + 120); Y = [int]($cr.Top + 14) }
                if (Ensure-Foreground $h) {
                    $pf = Row-Point $folderRow
                    [void][Kb]::SetCursorPos([int]$pf.X, [int]$pf.Y); Start-Sleep -Milliseconds 200
                    [Kb]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
                    for ($i = 1; $i -le 25; $i++) { [void][Kb]::SetCursorPos([int]($pf.X + ($toRoot.X - $pf.X) * $i / 25), [int]($pf.Y + ($toRoot.Y - $pf.Y) * $i / 25)); Start-Sleep -Milliseconds 40 }
                    Start-Sleep -Milliseconds 900
                    Snap-Screen $h 'marqueedrag-3-destaque-raiz'
                    [Kb]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 2500
                }
                Check 'soltar na linha C: (unidade aberta) move a pasta para a RAIZ da unidade' ((Test-Path -LiteralPath ('C:\' + $dropName + '\dentro.txt')) -and -not (Test-Path -LiteralPath (Join-Path $srcPath $dropName))) ("raiz=" + (Test-Path -LiteralPath ('C:\' + $dropName)) + " src=" + (Names-In $srcPath))
            }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'rename' {
            # Renomear como no Explorer: F2 e clique devagar no nome, com o foco ja na caixa e o nome sem a extensao selecionado,
            # e selecionar o texto com o mouse sem arrastar o arquivo.
            $dir = Open-UiFolder $root 'relatorio.final2.docx'
            if (-not $dir) { break }
            function Rename-Box { $e = $root.FindAll($TS::Descendants, (Cond $CT::Edit)) | Where-Object { $_.Current.HasKeyboardFocus } | Select-Object -First 1; $e }
            function Box-Selection($box) { try { $tp = $box.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern); $sel = $tp.GetSelection(); if ($sel.Count -gt 0) { return $sel[0].GetText(-1) } } catch { }; return $null }

            # 1) F2 num arquivo: a caixa abre ja com o foco, e o nome vem selecionado sem a extensao
            [void](Click-With $h (Row $dir 'relatorio.final1.docx') 'none')
            [void](Ensure-Foreground $h); [Kb]::Tap(0x71); Start-Sleep -Milliseconds 1500
            $box = Rename-Box
            Check 'F2 abre a caixa de renomear JA com o foco (sem clicar de novo)' ($null -ne $box)
            if ($box) {
                $sel = Box-Selection $box
                Check 'vem selecionado o nome sem a extensao (relatorio.final1)' ($sel -eq 'relatorio.final1') $sel
                Snap-Screen $h 'rename-1-f2'

                # 3) selecionar o texto com o mouse: arrasta dentro da caixa; o arquivo nao pode sair do lugar nem a caixa fechar
                $br = $box.Current.BoundingRectangle
                $y = [int]($br.Top + $br.Height / 2)
                # Apertar DENTRO do trecho ja selecionado e andar arrasta o TEXTO (como em qualquer caixa do Windows), nao muda a selecao:
                # um clique antes desfaz a selecao do F2, e so entao apertar e arrastar seleciona um trecho novo.
                [Kb]::Click([int]($br.Left + 6), $y); Start-Sleep -Milliseconds 500
                [void][Kb]::SetCursorPos([int]($br.Left + 6), $y); Start-Sleep -Milliseconds 250
                [Kb]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
                for ($i = 1; $i -le 12; $i++) { [void][Kb]::SetCursorPos([int]($br.Left + 6 + $i * 5), $y); Start-Sleep -Milliseconds 40 }
                [Kb]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 700
                $box2 = Rename-Box
                Check 'arrastar o mouse dentro da caixa nao fecha a caixa de renomear' ($null -ne $box2)
                $sel2 = if ($box2) { Box-Selection $box2 } else { $null }
                Check 'arrastar o mouse seleciona so um trecho do texto (nem tudo, nem nada)' (($sel2) -and ($sel2.Length -gt 0) -and ($sel2.Length -lt 'relatorio.final1.docx'.Length) -and ($sel2.Length -ne 'relatorio.final1'.Length)) $sel2
                Check 'o arquivo continuou no lugar (nao foi arrastado)' (Test-Path -LiteralPath (Join-Path $uiRoot 'relatorio.final1.docx')) (Names-In $uiRoot)
                Snap-Screen $h 'rename-2-mouse'

                # digitar substitui o trecho selecionado; Enter confirma
                [System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 200
                [System.Windows.Forms.SendKeys]::SendWait('novo-nome.docx'); Start-Sleep -Milliseconds 300
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 1500
                Check 'Enter confirma e o arquivo foi renomeado no disco' ((Test-Path -LiteralPath (Join-Path $uiRoot 'novo-nome.docx')) -and -not (Test-Path -LiteralPath (Join-Path $uiRoot 'relatorio.final1.docx'))) (Names-In $uiRoot)
                # depois do Enter o item com o NOVO nome fica selecionado e com o foco, e a pasta que o contem nao
                $renamedRow = Wait-Row (Ui-Item $root 0) 'novo-nome.docx' 8000
                Check 'o item com o novo nome aparece na lista' ($null -ne $renamedRow)
                if ($renamedRow) {
                    $isSelected = try { $renamedRow.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false }
                    Check 'depois do Enter o item com o NOVO nome fica selecionado' $isSelected
                    Start-Sleep -Milliseconds 600
                    Check 'e fica com o foco do teclado' ((Focused-Name) -like '*novo-nome.docx*') (Focused-Name)
                }
                $parentSelected = try { (Ui-Item $root 0).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false }
                Check 'a pasta que contem o item NAO fica selecionada' (-not $parentSelected)
                Snap-Screen $h 'rename-2b-depois-do-enter'
            }

            # 2) clique devagar: selecionar, esperar mais que o duplo clique e clicar no NOME de novo
            $row2 = Wait-Row $dir 'relatorio.final2.docx' 6000
            [void](Click-With $h $row2 'none')
            Start-Sleep -Milliseconds 1200
            Check 'um unico clique so seleciona: nenhuma caixa de renomear abriu' ($null -eq (Rename-Box))
            $row2 = Row $dir 'relatorio.final2.docx'
            $nameText = $row2.FindAll($TS::Descendants, (Cond $CT::Text)) | Where-Object { $_.Current.Name -eq 'relatorio.final2.docx' } | Select-Object -First 1
            if ($nameText) {
                $nr = $nameText.Current.BoundingRectangle
                [void](Ensure-Foreground $h)
                [Kb]::Click([int]($nr.Left + 20), [int]($nr.Top + $nr.Height / 2))
                Start-Sleep -Milliseconds 400
                Check 'logo depois do segundo clique ainda nao abriu a caixa (espera o tempo de duplo clique)' ($null -eq (Rename-Box))
                Start-Sleep -Milliseconds 1600
                $box3 = Rename-Box
                Check 'o clique devagar no nome abre a caixa de renomear com o foco' ($null -ne $box3)
                if ($box3) { Check 'e o nome sem a extensao vem selecionado' ((Box-Selection $box3) -eq 'relatorio.final2') (Box-Selection $box3); Snap-Screen $h 'rename-3-clique-devagar'; [Kb]::Tap(0x1B); Start-Sleep -Milliseconds 800 }
                Check 'Esc cancela e o arquivo continua com o nome antigo' (Test-Path -LiteralPath (Join-Path $uiRoot 'relatorio.final2.docx'))
            } else { Check 'o texto do nome do arquivo foi achado na linha' $false }

            # 4) um duplo clique rapido no nome NAO renomeia (abre o arquivo/pasta): testa na pasta, que apenas se abre
            $pasta = Row $dir 'pasta.v2'
            [void](Click-With $h $pasta 'none')
            [void](Ensure-Foreground $h); [Kb]::Tap(0x71); Start-Sleep -Milliseconds 1500
            $box4 = Rename-Box
            Check 'F2 numa pasta tambem abre a caixa com o foco' ($null -ne $box4)
            if ($box4) { Check 'numa pasta o nome inteiro vem selecionado (pasta.v2)' ((Box-Selection $box4) -eq 'pasta.v2') (Box-Selection $box4); [Kb]::Tap(0x1B); Start-Sleep -Milliseconds 600 }
            Check 'app continua respondendo' (-not $proc.HasExited -and $proc.Responding)
        }
        'newfeatures' {
            # Menu da Lixeira (botao direito na linha dela), Localizar (colunas e Abrir em Janela) e Duplicados (Pasta no menu central).
            $root.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Move(0, 0); Start-Sleep -Milliseconds 800
            # ---- Lixeira: so le o menu; nunca esvazia a Lixeira do usuario (a confirmacao e recusada)
            $bin = @(TopItems $root 0) | Where-Object { $_.Current.Name -like 'Lixeira*' } | Select-Object -First 1
            Check 'a linha da Lixeira existe no painel' ($null -ne $bin)
            if ($bin) {
                $entries = @(Right-Click-Menu $h $bin $proc.Id $true)
                Check 'o menu da Lixeira abre com o botao direito' ($entries.Count -gt 0) ($entries -join ' | ')
                Check 'o menu da Lixeira tem Esvaziar Lixeira' ($entries -contains 'Esvaziar Lixeira') ($entries -join ' | ')
                Check 'o menu da Lixeira tem Restaurar todos os itens' ($entries -contains 'Restaurar todos os itens') ($entries -join ' | ')
                Check 'o menu da Lixeira tem Atualizar' ($entries -contains 'Atualizar') ($entries -join ' | ')
                Snap-Screen $h 'newfeatures-1-menu-lixeira'
                $empty = Popup-MenuItems $h | Where-Object { $_.Current.Name -eq 'Esvaziar Lixeira' } | Select-Object -First 1
                if ($empty -and $empty.Current.IsEnabled) {
                    Check 'Esvaziar Lixeira pede confirmacao antes de apagar' (Pick-Menu-Entry $proc.Id $h 'Esvaziar Lixeira')
                    $dlg = Wait-Dialog $proc.Id 'Esvaziar Lixeira' 6000
                    Check 'a pergunta Esvaziar Lixeira aparece' ($dlg -ne [IntPtr]::Zero)
                    if ($dlg -ne [IntPtr]::Zero) {
                        Snap $dlg 'newfeatures-1b-confirma-esvaziar'
                        Check 'recusa a pergunta (Nao): a Lixeira do usuario nao e esvaziada' ((Press-DialogButton $dlg ('N' + [char]0xE3 + 'o')) -or (Press-DialogButton $dlg 'Cancelar'))
                    }
                } else { Close-Menu; 'a Lixeira esta vazia nesta maquina: Esvaziar Lixeira aparece desligado (nada a confirmar)' }
            }

            # ---- Localizar: abre o menu, pesquisa, abre os resultados em janela e ajusta uma coluna
            Expand ((TopItems $root 0)[0]); [void](Choose-Item (Child ((TopItems $root 0)[0]) (Split-Path $uiRoot -Leaf)))
            [void](Ensure-Foreground $h)
            Check 'o botao Localizar abre o menu' (Invoke-Button $root 'Localizar*')
            Start-Sleep -Milliseconds 600
            [System.Windows.Forms.SendKeys]::SendWait('achado-raro{ENTER}'); Start-Sleep -Milliseconds 2500
            $newWin = $root.FindAll($TS::Descendants, (Cond $CT::Button)) | Where-Object { $_.Current.Name -eq 'Abrir em Janela' } | Select-Object -First 1
            Check 'o menu Localizar tem o botao Abrir em Janela' ($null -ne $newWin)
            Snap-Screen $h 'newfeatures-2-localizar-menu'
            if ($newWin) {
                $newWin.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 1500
                $sw = Wait-Dialog $proc.Id 'Localizar' 6000
                Check 'Abrir em Janela abre a janela Localizar com os resultados' ($sw -ne [IntPtr]::Zero)
                if ($sw -ne [IntPtr]::Zero) {
                    $win = $AE::FromHandle($sw)
                    $items = @($win.FindAll($TS::Descendants, (Cond $CT::DataItem)) + $win.FindAll($TS::Descendants, (Cond $CT::ListItem)))
                    Check 'a janela mostra o resultado achado-raro.txt' ($items.Count -ge 1) "itens: $($items.Count)"
                    $headers = @($win.FindAll($TS::Descendants, (Cond $CT::HeaderItem)))
                    Check 'a janela tem os cabecalhos Nome, Caminho e Tamanho' (@($headers | Where-Object { $_.Current.Name -in 'Nome', 'Caminho', 'Tamanho' }).Count -eq 3) (($headers | ForEach-Object { $_.Current.Name }) -join ', ')
                    $nome = $headers | Where-Object { $_.Current.Name -eq 'Nome' } | Select-Object -First 1
                    if ($nome) {
                        $wBefore = $nome.Current.BoundingRectangle.Width
                        $rr = $nome.Current.BoundingRectangle
                        [void][Kb]::SetCursorPos([int]($rr.Right - 1), [int]($rr.Top + $rr.Height / 2)); Start-Sleep -Milliseconds 300
                        [Kb]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
                        for ($i = 1; $i -le 10; $i++) { [void][Kb]::SetCursorPos([int]($rr.Right - 1 + $i * 12), [int]($rr.Top + $rr.Height / 2)); Start-Sleep -Milliseconds 40 }
                        [Kb]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 700
                        $nome2 = $win.FindAll($TS::Descendants, (Cond $CT::HeaderItem)) | Where-Object { $_.Current.Name -eq 'Nome' } | Select-Object -First 1
                        $wAfter = $nome2.Current.BoundingRectangle.Width
                        Check 'arrastar a borda do cabecalho Nome aumenta a coluna' ($wAfter -gt $wBefore + 50) "antes=$wBefore depois=$wAfter"
                    }
                    Snap $sw 'newfeatures-3-localizar-janela'
                    [void](Press-DialogButton $sw 'Fechar')
                }
            }

            # ---- Duplicados: o botao do grupo Pasta abre um menu com os dois tipos
            [void](Ensure-Foreground $h)
            Check 'o botao Duplicados abre o menu' (Invoke-Button $root 'Duplicados*')
            Start-Sleep -Milliseconds 600
            $entries = @(Popup-MenuItems $h | ForEach-Object { $_.Current.Name })
            Check 'o menu Duplicados tem os dois tipos de verificacao' (($entries -contains ('Padr' + [char]0xE3 + 'o: nome e tamanho')) -and ($entries -contains 'Profundo: tamanho e hash')) ($entries -join ' | ')
            Snap-Screen $h 'newfeatures-4-menu-duplicados'
            [void](Pick-Menu-Entry $proc.Id $h ('Padr' + [char]0xE3 + 'o: nome e tamanho'))
            $dd = Wait-Dialog $proc.Id 'Itens duplicados' 8000
            Check 'a janela Itens duplicados abre' ($dd -ne [IntPtr]::Zero)
            if ($dd -ne [IntPtr]::Zero) {
                $dw = $AE::FromHandle($dd)
                $sysBox = $dw.FindAll($TS::Descendants, (Cond $CT::CheckBox)) | Where-Object { $_.Current.Name -like 'Incluir pastas do sistema*' } | Select-Object -First 1
                Check 'a janela tem a caixa Incluir pastas do sistema, desligada por padrao' (($null -ne $sysBox) -and ((Toggle-State $sysBox) -eq 'Off')) (Toggle-State $sysBox)
                Start-Sleep -Milliseconds 2500
                $rows = @($dw.FindAll($TS::Descendants, (Cond $CT::DataItem)) + $dw.FindAll($TS::Descendants, (Cond $CT::ListItem)))
                Check 'no tipo Padrao a lista tem 3 grupos (3 cabecalhos + 6 copias = 9 linhas)' ($rows.Count -eq 9) "linhas: $($rows.Count)"
                Snap $dd 'newfeatures-5-duplicados-padrao'

                # Ordem: comeca pelos maiores; clicar em Nome e Tamanho reordena e mostra a seta da direcao.
                $down = [string][char]0x25BC; $up = [string][char]0x25B2
                function Header-Names { @($dw.FindAll($TS::Descendants, (Cond $CT::HeaderItem)) | ForEach-Object { $_.Current.Name }) }
                function Click-Header($like) { $hd = $dw.FindAll($TS::Descendants, (Cond $CT::HeaderItem)) | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1; if ($hd) { $hd.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900 }; [bool]$hd }
                # nomes dos arquivos, de cima para baixo (so as linhas de arquivo: as de grupo tem "copias" no texto)
                function First-File-Names { @($dw.FindAll($TS::Descendants, (Cond $CT::Text)) | Where-Object { $_.Current.Name -match '\.(txt|bin)$' } | Sort-Object { $_.Current.BoundingRectangle.Top } | ForEach-Object { $_.Current.Name }) }
                $headers = Header-Names
                Check 'a coluna Tamanho comeca ordenada, do maior para o menor (seta para baixo)' ($headers -contains ('Tamanho ' + $down)) ($headers -join ' | ')
                $names = First-File-Names
                Check 'a lista comeca pelo maior arquivo (grande.bin)' ($names.Count -gt 0 -and $names[0] -eq 'grande.bin') ($names -join ', ')
                Check 'clicar em Nome ordena de A a Z' (Click-Header 'Nome*')
                $headers = Header-Names; $names = First-File-Names
                Check 'o cabecalho Nome mostra a seta para cima' ($headers -contains ('Nome ' + $up)) ($headers -join ' | ')
                Check 'a lista comeca por diferente.txt (A a Z)' ($names.Count -gt 0 -and $names[0] -eq 'diferente.txt') ($names -join ', ')
                Check 'clicar em Nome de novo inverte (Z a A)' (Click-Header 'Nome*')
                $names = First-File-Names
                Check 'a lista comeca por igual.txt (Z a A)' ($names.Count -gt 0 -and $names[0] -eq 'igual.txt') ($names -join ', ')
                Snap $dd 'newfeatures-5b-duplicados-por-nome'
                [void](Click-Header 'Tamanho*')
                $names = First-File-Names
                Check 'clicar em Tamanho volta a comecar pelos maiores' ($names.Count -gt 0 -and $names[0] -eq 'grande.bin') ($names -join ', ')
                $deep = $dw.FindAll($TS::Descendants, (Cond $CT::RadioButton)) | Where-Object { $_.Current.Name -like 'Profundo*' } | Select-Object -First 1
                if ($deep) { $deep.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400 }
                Check 'o tipo Profundo e escolhido na janela' ($null -ne $deep)
                Check 'o botao Procurar roda de novo' (Press-DialogButton $dd 'Procurar')
                Start-Sleep -Milliseconds 2500
                $rows = @($dw.FindAll($TS::Descendants, (Cond $CT::DataItem)) + $dw.FindAll($TS::Descendants, (Cond $CT::ListItem)))
                Check 'no tipo Profundo a lista tem 2 grupos (igual.txt + outro-nome.txt = 3 copias; grande.bin = 2 copias; 7 linhas)' ($rows.Count -eq 7) "linhas: $($rows.Count)"
                Snap $dd 'newfeatures-6-duplicados-profundo'
                [void](Press-DialogButton $dd 'Fechar')
            }
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
if (($Scenario -in 'delfocus', 'multisel', 'dragdrop', 'about', 'favmenu', 'props', 'menu', 'marqueedrag', 'rename', 'newfeatures') -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; 'o app nao fechou sozinho (ficou uma janela aberta?): encerrado a forca'; Start-Sleep -Seconds 1 }
foreach ($path in $cleanup) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
if ($Scenario -eq 'blink' -and $blinkRoot) { Clear-BlinkIndexRows $blinkRoot }
if ($userState) { Restore-UserState }
if ($Scenario -in 'delfocus', 'multisel', 'dragdrop', 'about', 'favmenu', 'props', 'menu', 'marqueedrag', 'rename', 'newfeatures') {
    [void][Kb]::SetCursorPos($cursor0.X, $cursor0.Y)
    'pasta de teste removida: ' + (-not (Test-Path -LiteralPath $uiRoot))
    "verificacoes com falha: $uiFailures"
    if ($uiFailures -gt 0) { exit 1 }
}
