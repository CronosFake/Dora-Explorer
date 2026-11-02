param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

# Gera project.md: mapa do codigo para agentes (e pessoas) acharem onde mexer sem ler arquivos inteiros.
# Tudo o que e fato do codigo (arquivos, tipos, metodos, linhas, pacotes) e lido do codigo-fonte a cada execucao.
# O "Mapa funcional" e escrito a mao abaixo, mas cada referencia e conferida contra o codigo: nome que sumiu
# aparece marcado com (?) no project.md e como aviso no console, em vez de envelhecer em silencio.
# Este arquivo e ASCII de proposito: o Windows PowerShell 5.1 le .ps1 sem BOM como ANSI.

$ErrorActionPreference = 'Stop'

$srcRoot = Join-Path $RepoRoot 'src\DoraExplorer.App'
$testRoot = Join-Path $RepoRoot 'tests\DoraExplorer.App.Tests'
$outFile = Join-Path $RepoRoot 'project.md'
$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

# Acima disto o arquivo e "grande": ler por trechos (offset/limit) e usar o indice de metodos com numero de linha.
$LargeFileLines = 600

$typePattern = '^\s{0,4}(?:public|internal)\s+(?:(?:static|sealed|abstract|partial|readonly|unsafe)\s+)*(?:class|enum|interface|record(?:\s+(?:class|struct))?|struct)\s+(\w+)'
$interfacePattern = '^\s{0,4}(?:public|internal)\s+(?:partial\s+)?interface\s'
$methodPattern = '^\s{4,8}(?<acc>public|private|protected|internal)(?:\s+(?:static|async|override|virtual|abstract|sealed|new|extern|unsafe|partial))*\s+[\w<>\[\],.?()\s]+?\s+(?<name>\w+)\s*(?:<[^>()]*>)?\s*\('
$partialPattern = '^\s{4,8}partial\s+void\s+(?<name>\w+)\s*\('
$interfaceMethodPattern = '^\s{4}(?!(?:public|private|protected|internal|return|using|if|else|foreach|for|while|var|new)\b)[\w<>\[\],.?]+\s+(?<name>\w+)\s*(?:<[^>()]*>)?\s*\('

function Get-FileFacts {
    param([string]$FilePath, [string]$RelativePath)

    $all = [System.IO.File]::ReadAllLines($FilePath)
    $types = New-Object System.Collections.Generic.List[string]
    $methods = New-Object System.Collections.Generic.List[object]
    $isInterfaceFile = $false

    for ($i = 0; $i -lt $all.Count; $i++) {
        $line = $all[$i]

        if ($line -match $typePattern) {
            if (-not $types.Contains($Matches[1])) { $types.Add($Matches[1]) }
            if ($line -match $interfacePattern) { $isInterfaceFile = $true }
            continue
        }

        $name = $null
        $isPublic = $false
        if ($line -match $methodPattern) {
            $name = $Matches['name']
            $isPublic = ($Matches['acc'] -eq 'public')
        }
        elseif ($line -match $partialPattern) {
            $name = $Matches['name']
        }
        elseif ($isInterfaceFile -and $line -match $interfaceMethodPattern) {
            $name = $Matches['name']
            $isPublic = $true
        }

        if ($name -and -not ($methods | Where-Object { $_.Name -eq $name })) {
            $methods.Add([pscustomobject]@{ Name = $name; Line = $i + 1; Public = $isPublic })
        }
    }

    return [pscustomobject]@{
        Path    = $RelativePath
        Full    = $FilePath
        Lines   = $all.Count
        Types   = $types
        Methods = $methods
        IsCode  = $FilePath.EndsWith('.cs')
    }
}

function Get-RelativePath {
    param([string]$FullPath)
    return ($FullPath.Substring($RepoRoot.Length + 1) -replace '\\', '/')
}

if (-not (Test-Path $srcRoot)) {
    throw "Pasta de codigo-fonte nao encontrada: $srcRoot"
}

$sourceFiles = Get-ChildItem $srcRoot -Recurse -Include *.cs, *.xaml -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object FullName

$facts = @($sourceFiles | ForEach-Object { Get-FileFacts $_.FullName (Get-RelativePath $_.FullName) })

$testFacts = @()
if (Test-Path $testRoot) {
    $testFacts = @(Get-ChildItem $testRoot -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object FullName |
        ForEach-Object { Get-FileFacts $_.FullName (Get-RelativePath $_.FullName) })
}

# tipo -> arquivos que o declaram (para conferir o mapa funcional)
$typeFiles = @{}
foreach ($f in $facts) {
    foreach ($t in $f.Types) {
        if (-not $typeFiles.ContainsKey($t)) { $typeFiles[$t] = New-Object System.Collections.Generic.List[object] }
        $typeFiles[$t].Add($f)
    }
}

$fileText = @{}
function Get-FileText {
    param($Fact)
    if (-not $fileText.ContainsKey($Fact.Full)) { $fileText[$Fact.Full] = [System.IO.File]::ReadAllText($Fact.Full) }
    return $fileText[$Fact.Full]
}

$problems = New-Object System.Collections.Generic.List[string]
$methodOutFile = Join-Path $RepoRoot 'project-metodos.md'

# Tipos de dados simples: a lista de metodos nao ajuda a achar nada e so custa tokens.
$noMethodDirs = @('src/DoraExplorer.App/Models/', 'src/DoraExplorer.App/Converters/', 'src/DoraExplorer.App/Contracts/')

function Resolve-Reference {
    # "Tipo" ou "Tipo.Membro" (metodo, propriedade ou campo) ou caminho relativo com "/". Devolve o texto com (?) se nao existir.
    param([string]$Ref)

    if ($Ref.Contains('/')) {
        $relative = $Ref -replace '/', '\'
        if ((Test-Path (Join-Path $srcRoot $relative)) -or (Test-Path (Join-Path $RepoRoot $relative))) { return $Ref }
        $problems.Add($Ref)
        return "$Ref (?)"
    }

    $parts = $Ref.Split('.')
    $typeName = $parts[0]
    if (-not $typeFiles.ContainsKey($typeName)) {
        $problems.Add($Ref)
        return "$Ref (?)"
    }

    if ($parts.Count -gt 1) {
        $member = $parts[1]
        $found = $false
        foreach ($f in $typeFiles[$typeName]) {
            if ((Get-FileText $f) -match ('\b' + [regex]::Escape($member) + '\b')) { $found = $true; break }
        }
        if (-not $found) {
            $problems.Add($Ref)
            return "$Ref (?)"
        }
    }

    return $Ref
}

# ---- Mapa funcional (escrito a mao; as referencias sao conferidas acima) ----
$features = @(
    @{ F = 'Arvore unificada (pastas e arquivos num TreeView)'; R = @('NavigationTreeService', 'NavigationTreeNodeViewModel', 'FilePaneControl', 'TreeViewHierarchy', 'TreeHierarchyGuides') },
    @{ F = 'Abrir e fechar pasta (expandir/recolher, acordeao)'; R = @('FilePaneViewModel.OnNavigationNodeExpanding', 'FilePaneViewModel.OnNavigationNodeExpanded', 'FilePaneViewModel.OnNavigationNodeCollapsed', 'FilePaneControl.ExpandNode', 'FilePaneControl.BeginAnimatedCollapse', 'NavigationTreeNodeViewModel.ReloadOnNextExpand') },
    @{ F = 'Carregar listagem de uma pasta'; R = @('NavigationTreeService.LoadDirectoryChildren', 'NavigationTreeService.StartChunkedFill', 'NavigationTreeService.ScheduleBackgroundRefresh') },
    @{ F = 'Reabrir pasta sem piscar (revalidar e aplicar so a diferenca)'; R = @('NavigationTreeService.TryRevalidateLoadedDirectory', 'NavigationTreeService.ApplyBackgroundResults', 'NavigationTreeService.RebuildChildrenFromEntries', 'RangeObservableCollection.SyncTo') },
    @{ F = 'Pastas enormes e memoria'; R = @('RangeObservableCollection.ReplaceAll', 'MemoryTrimmer', 'TreeContainers', 'DirectoryIndexService') },
    @{ F = 'Animacao de abrir/fechar e rolagem'; R = @('TreeExpansionRevealAnimator', 'SmoothScrollAnimator', 'TreeScrollHelper') },
    @{ F = 'Caminho atual, breadcrumb, voltar/avancar/subir'; R = @('FilePaneViewModel.NavigateToPath', 'FilePaneViewModel.RestorePath', 'BreadcrumbSegmentViewModel', 'NavigationHistory', 'PathNavigation') },
    @{ F = 'Selecao e atalhos de teclado'; R = @('FilePaneControl.NavigationTree_PreviewKeyDown', 'FilePaneViewModel.SetSingleItemSelection', 'SelectionStatusFormatter') },
    @{ F = 'Menu Exibir (arvore) e opcoes por painel'; R = @('NavigationTreeService.CreateVisibleRoots', 'PaneDisplayOptions', 'PaneDisplayMenuViewModel', 'PaneDisplaySettingsService') },
    @{ F = 'Menu Exibir (barra superior)'; R = @('MainWindowViewModel', 'ExplorerViewSettingsService', 'ExplorerViewOptions') },
    @{ F = 'Copiar, Mover e Compactar (com progresso e cancelamento)'; R = @('MainWindowViewModel.ExecuteTransferAsync', 'MainWindowViewModel.ExecuteCompressAsync', 'FileOperationService.TryCopy', 'FileOperationService.TryCompress', 'NativeFileCopy', 'TransferProgressDialog', 'TransferProgressFormatter', 'OperationTimeouts') },
    @{ F = 'Nova pasta, Renomear, Excluir, Lixeira'; R = @('CenterToolbarViewModel', 'FilePaneViewModel', 'FileOperationService', 'DeleteChoiceDialog', 'RecycleBinService') },
    @{ F = 'Aplicativo padrao ao lado do arquivo e "Abrir com"'; R = @('FileAssociationService', 'DefaultApplicationOverrides', 'FilePaneControl.DefaultAppButton_Click', 'FileOpenService.TryShowNativeOpenWithDialog') },
    @{ F = 'Localizar arquivos'; R = @('CenterToolbarViewModel.RunSearchWithPattern', 'FileSearchService', 'SearchDialog') },
    @{ F = 'Ordenacao (menu e cabecalho das colunas)'; R = @('CenterToolbarViewModel.ApplySortOptions', 'TreeEntrySorter', 'SortHeaderLogic', 'TreeSortOptions') },
    @{ F = 'Favoritos'; R = @('FavoritesService') },
    @{ F = 'Arquivos compactados (ZIP, TAR, RAR) como pastas'; R = @('ArchiveService', 'ArchivePathHelper') },
    @{ F = 'Unidades e particoes'; R = @('VolumeEnumerationService', 'LocalFileSystemService') },
    @{ F = 'Icones e miniaturas'; R = @('ShellIconService', 'FileThumbnailService') },
    @{ F = 'Cliente de rede (FTP, SFTP, SCP, TFTP)'; R = @('NetworkFileService', 'NetworkSessionStore', 'NetworkClientDialog', 'NetworkClientHealthService', 'TftpClient', 'HostKeyVerifier', 'KnownHostsStore', 'FtpCertificateVerifier') },
    @{ F = 'Servidores embutidos (FTP, TFTP)'; R = @('NetworkServerService', 'ProtocolServerBase', 'FtpServer', 'TftpServer', 'FtpTlsCertificate', 'NetworkPortGuard', 'NetworkConnectionDialog') },
    @{ F = 'Tema claro/escuro e cor de destaque'; R = @('ThemeManager', 'ExplorerThemeResources', 'WindowsAccentColorService', 'Resources/Theme.xaml', 'Resources/DarkTheme.xaml', 'Resources/LightTheme.xaml') },
    @{ F = 'Configuracoes e senhas'; R = @('AppSettingsService', 'ProtectedStringJsonConverter', 'RememberPathSettingsService'); N = 'settings.json em %AppData%/DoraExplorer; senhas cifradas com DPAPI' },
    @{ F = 'Registro de servicos (DI)'; R = @('ServiceRegistration.AddDoraExplorerServices') },
    @{ F = 'Instancia unica (impede abrir duas vezes)'; R = @('SingleInstanceGuard') },
    @{ F = 'Logs'; R = @('AppLogService'); N = 'pasta logs/ na raiz do repositorio' }
)

$dirNotes = @{
    'Assets'         = 'Icones'
    'Contracts'      = 'Interfaces dos servicos (DI e testes)'
    'Controls'       = 'Controles WPF: paineis, toolbar, arvore, animacoes'
    'Converters'     = 'IValueConverter do WPF'
    'Infrastructure' = 'DI, caminhos seguros, timeouts, instancia unica, colecao em bloco, enxugador de memoria'
    'Models'         = 'DTOs, enums e opcoes'
    'Resources'      = 'Temas e estilos XAML'
    'Services'       = 'Arquivos, rede, indice, tema, icones, logs, arquivos compactados'
    'ViewModels'     = 'MVVM (CommunityToolkit.Mvvm)'
    'Views'          = 'Dialogos do menu central'
}

# ---- Montagem do documento ----
$projFile = Join-Path $srcRoot 'DoraExplorer.App.csproj'
$packages = @()
$framework = 'net8.0-windows'
if (Test-Path $projFile) {
    [xml]$proj = Get-Content $projFile -Raw
    $packages = @($proj.Project.ItemGroup.PackageReference | Where-Object { $_ } | ForEach-Object { "$($_.Include) $($_.Version)" })
    $tf = @($proj.Project.PropertyGroup.TargetFramework | Where-Object { $_ })
    if ($tf.Count -gt 0) { $framework = $tf[0] }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Dora Explorer - Mapa do Projeto')
$lines.Add('')
$lines.Add('> Auto-gerado por scripts/generate-project-map.ps1 (o Start.bat roda a cada execucao). Nao edite a mao.')
$lines.Add('> Regras, comandos e armadilhas: AGENTS.md. Visao do produto e limitacoes conhecidas: README.md.')
$lines.Add("> Ultima atualizacao: $timestamp")
$lines.Add('')
$lines.Add('## Como usar este mapa')
$lines.Add('')
$lines.Add('1. Ache a funcionalidade em "Mapa funcional" e va direto aos tipos citados.')
$lines.Add('2. Arquivos em "Arquivos grandes" nao devem ser lidos inteiros: abra project-metodos.md (metodos com numero de linha), ache o metodo e leia so o trecho (Read com offset/limit).')
$lines.Add('3. O indice abaixo mostra tipos e metodos publicos; para qualquer outro nome, Grep.')
$lines.Add('')
$lines.Add('## Visao geral')
$lines.Add('')
$lines.Add('| Item | Valor |')
$lines.Add('|------|-------|')
$lines.Add('| Tipo | Gerenciador de arquivos Windows de dois paineis (releitura do X-plore) |')
$lines.Add('| Projeto | src/DoraExplorer.App/DoraExplorer.App.csproj |')
$lines.Add("| Framework | $framework (WPF), MVVM com CommunityToolkit.Mvvm |")
$lines.Add('| Executar | Start.bat -> build/compile/DoraExplorer.App.exe |')
$lines.Add('| Testes | dotnet test DoraExplorer.sln -c Release (tests/DoraExplorer.App.Tests, xUnit + FluentAssertions) |')
$lines.Add('')
$lines.Add('## Estrutura de pastas')
$lines.Add('')
$lines.Add('```')
$lines.Add('src/DoraExplorer.App/')
$lines.Add('  App.xaml.cs, MainWindow.xaml(.cs)   # inicializacao, DI, janela principal com dois paineis')
foreach ($dir in (Get-ChildItem $srcRoot -Directory | Where-Object { $_.Name -notin 'bin', 'obj' } | Sort-Object Name)) {
    $inDir = @($facts | Where-Object { $_.Path.StartsWith('src/DoraExplorer.App/' + $dir.Name + '/') })
    $count = $inDir.Count
    $total = [int]($inDir | Measure-Object -Property Lines -Sum).Sum
    $note = if ($dirNotes.ContainsKey($dir.Name)) { '# ' + $dirNotes[$dir.Name] } else { '' }
    $lines.Add(('  {0,-15} {1,3} arquivos {2,6} linhas  {3}' -f ($dir.Name + '/'), $count, $total, $note).TrimEnd())
}
$lines.Add('tests/DoraExplorer.App.Tests/   # testes xUnit')
$lines.Add('scripts/                        # stop-dora.ps1, generate-project-map.ps1')
$lines.Add('scripts/dev/                    # ui-capture.ps1 (roteiros na tela), bench.ps1 (medicao), make-test-folder.ps1')
$lines.Add('```')
$lines.Add('')
$lines.Add('## Mapa funcional -> onde alterar')
$lines.Add('')
$lines.Add('Itens com (?) apontam para algo que nao existe mais no codigo: atualize a lista em scripts/generate-project-map.ps1.')
$lines.Add('')
$lines.Add('| Funcionalidade | Onde alterar |')
$lines.Add('|----------------|--------------|')
foreach ($feature in $features) {
    $resolved = @($feature.R | ForEach-Object { Resolve-Reference $_ })
    $text = $resolved -join ', '
    if ($feature.N) { $text += ' (' + $feature.N + ')' }
    $lines.Add('| ' + $feature.F + ' | ' + $text + ' |')
}
$lines.Add('')
$lines.Add('## Fluxo de arranque')
$lines.Add('')
$lines.Add('```')
$lines.Add('App.OnStartup')
$lines.Add('  -> SingleInstanceGuard.TryAcquire (outra instancia aberta: avisa e sai)')
$lines.Add('  -> AppLogService.Initialize + handlers globais de excecao')
$lines.Add('  -> Host/DI (ServiceRegistration.AddDoraExplorerServices)')
$lines.Add('  -> AppSettingsService (migra senhas antigas), FileAssociationService.UsePersistence, ThemeManager,')
$lines.Add('     ExplorerViewSettings/PaneDisplaySettings/RememberPathSettings, DirectoryIndexService.ScheduleMaintenance')
$lines.Add('  -> servicos de rede comecam parados (ClearAll/StopAll) e a checagem de saude inicia')
$lines.Add('  -> MainWindow (DataContext = MainWindowViewModel; cada painel = FilePaneViewModel)')
$lines.Add('```')
$lines.Add('')
$lines.Add('## Pacotes NuGet')
$lines.Add('')
$lines.Add(($packages -join ', '))
$lines.Add('')

$large = @($facts | Where-Object { $_.Lines -ge $LargeFileLines } | Sort-Object Lines -Descending)
$lines.Add("## Arquivos grandes (>= $LargeFileLines linhas): ler por trechos")
$lines.Add('')
$lines.Add('Metodos com numero de linha: project-metodos.md.')
$lines.Add('')
foreach ($f in $large) {
    $lines.Add('- `' + $f.Path + '` - ' + $f.Lines + ' linhas')
}
$lines.Add('')

$lines.Add('## Indice de arquivos fonte')
$lines.Add('')
$lines.Add('Formato: arquivo (linhas) - tipos. Metodos: so os publicos, ate 12 (30 nos arquivos grandes).')
$lines.Add('')

foreach ($f in $facts) {
    $typeText = if ($f.Types.Count -gt 0) { ' - **' + ($f.Types -join ', ') + '**' } else { '' }
    $lines.Add('- `' + $f.Path.Substring('src/DoraExplorer.App/'.Length) + '` (' + $f.Lines + ')' + $typeText)

    if (-not $f.IsCode -or $f.Types.Count -eq 0 -or $f.Methods.Count -eq 0) { continue }
    if (@($noMethodDirs | Where-Object { $f.Path.StartsWith($_) }).Count -gt 0) { continue }

    $cap = if ($f.Lines -ge $LargeFileLines) { 30 } else { 12 }
    $public = @($f.Methods | Where-Object { $_.Public } | ForEach-Object { $_.Name })
    if ($public.Count -gt 0) {
        $shown = @($public | Select-Object -First $cap)
        $more = if ($public.Count -gt $shown.Count) { ' (+' + ($public.Count - $shown.Count) + ')' } else { '' }
        $lines.Add('  - Metodos: ' + ($shown -join ', ') + $more)
    }
}

if ($testFacts.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Testes (tests/DoraExplorer.App.Tests)')
    $lines.Add('')
    foreach ($f in $testFacts) {
        $lines.Add('- `' + $f.Path.Substring('tests/DoraExplorer.App.Tests/'.Length) + '` (' + $f.Lines + ') - ' + ($f.Types -join ', '))
    }
}

if ($problems.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Referencias do mapa funcional que nao existem mais')
    $lines.Add('')
    foreach ($p in ($problems | Select-Object -Unique)) { $lines.Add('- ' + $p) }
    foreach ($p in ($problems | Select-Object -Unique)) { Write-Warning "Mapa funcional: referencia nao encontrada: $p" }
}

$methodLines = New-Object System.Collections.Generic.List[string]
$methodLines.Add('# Dora Explorer - Metodos dos arquivos grandes')
$methodLines.Add('')
$methodLines.Add('> Auto-gerado por scripts/generate-project-map.ps1. Nao edite a mao. Atualizacao: ' + $timestamp)
$methodLines.Add('> Use junto com project.md: ache o metodo aqui e leia so o trecho (Read com offset/limit). Linhas mudam a cada edicao: confirme com Grep.')
$methodLines.Add('')
foreach ($f in $large) {
    $methodLines.Add('## ' + $f.Path + ' (' + $f.Lines + ' linhas)')
    $methodLines.Add('')
    if ($f.IsCode -and $f.Methods.Count -gt 0) {
        $methodLines.Add((@($f.Methods | ForEach-Object { $_.Name + ':' + $_.Line }) -join ', '))
    }
    else {
        $methodLines.Add('(sem metodos; arquivo XAML)')
    }
    $methodLines.Add('')
}

[System.IO.File]::WriteAllLines($outFile, $lines, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllLines($methodOutFile, $methodLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "project.md e project-metodos.md atualizados em: $RepoRoot"
