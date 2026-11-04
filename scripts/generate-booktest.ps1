param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

# Gera tests/BOOKTEST.md: o book de testes. Diz, por area do app, quais testes protegem o que, como rodar so aqueles,
# o que foi verificado apenas na tela e onde ainda nao ha teste automatico.
# Os fatos (classes e casos de teste) sao lidos do codigo dos testes a cada execucao. O mapa de areas e a lista "so na tela"
# sao escritos a mao abaixo; cada tipo citado e conferido contra o codigo e cada classe de teste precisa estar numa area:
# o que sumiu ou ficou de fora aparece marcado no BOOKTEST.md e como aviso no console, em vez de envelhecer em silencio.
# Este arquivo e ASCII de proposito: o Windows PowerShell 5.1 le .ps1 sem BOM como ANSI.

$ErrorActionPreference = 'Stop'

$srcRoot = Join-Path $RepoRoot 'src\DoraExplorer.App'
$testRoot = Join-Path $RepoRoot 'tests\DoraExplorer.App.Tests'
$outFile = Join-Path $RepoRoot 'tests\BOOKTEST.md'
$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

function New-Area {
    param([string]$Name, [string]$Covers, [string[]]$Types, [string[]]$Tests)
    return [pscustomobject]@{ Name = $Name; Covers = $Covers; Types = $Types; Tests = $Tests }
}

# Mapa de areas (escrito a mao). Cada classe de teste fica em uma area so.
$areas = @(
    (New-Area 'Seguranca de senhas e configuracoes' `
        'Senhas e frases-senha guardadas protegidas (DPAPI), migracao de senhas antigas em texto puro, versao do SQLite sem a falha conhecida.' `
        @('ProtectedStringJsonConverter', 'AppSettingsService') `
        @('ProtectedPasswordTests', 'LegacyPasswordMigrationTests', 'SqliteSecurityTests')),
    (New-Area 'Confianca em servidores (chaves, certificados, login do cliente)' `
        'O cliente so confia em chave de host ou certificado conhecido, avisa quando mudam, e escolhe os metodos de login (chave, senha, interativo).' `
        @('KnownHostsStore', 'HostKeyVerifier', 'FtpCertificateVerifier', 'FtpTlsCertificate') `
        @('HostKeyTests', 'FtpClientSecurityTests', 'SshAuthenticationTests')),
    (New-Area 'Servidor FTP e FTPS' `
        'Comandos FTP padrao (MLSD/MLST, REST/APPE, passivo e ativo), FTPS explicito, modo somente leitura, bloqueio por tentativas e nomes perigosos.' `
        @('FtpServer') `
        @('FtpServerTests')),
    (New-Area 'Servidor SFTP e SCP' `
        'Servidor SSH proprio: SFTP v3 e SCP com cliente real, chave de host estavel, confinamento na pasta compartilhada, nada de shell nem encaminhamento de porta.' `
        @('SshFileServer', 'SftpServerSession', 'ScpServerSession', 'SshHostKeyStore', 'ServerVirtualPath', 'LoginThrottle') `
        @('SshServerEndToEndTests', 'SftpServerSessionTests', 'ScpServerSessionTests', 'ScpCommandTests', 'ServerVirtualPathTests', 'LoginThrottleTests')),
    (New-Area 'TFTP (servidor e cliente)' `
        'Opcoes blksize e tsize, retransmissao, volta do contador de blocos, somente leitura, e o cliente com tempo limite e cancelamento.' `
        @('TftpServer', 'TftpClient') `
        @('TftpServerTests', 'TftpClientTests')),
    (New-Area 'Travas dos servidores locais (pasta raiz e portas)' `
        'Pasta raiz ampla demais e caminhos fora dela sao recusados; o app nunca encerra processo de terceiros que ocupa uma porta.' `
        @('NetworkServerService', 'NetworkPortGuard') `
        @('NetworkServerHardeningTests', 'NetworkPortGuardTests')),
    (New-Area 'Clientes de rede (sessoes, copia entre servidores, saude dos perfis)' `
        'Caminhos remotos, download e envio com progresso e limpeza, copia entre servidores, perfis iguais, indicador de saude sem travar a conta.' `
        @('RemoteFileService', 'RemoteTransferEngine', 'NetworkFileService', 'NetworkClientHealthService') `
        @('RemoteFileServiceTests', 'RemoteFileServiceFtpTests', 'RemoteTransferEngineTests', 'NetworkFileServiceTests', 'HealthServiceTests')),
    (New-Area 'Pasta Rede na arvore' `
        'A pasta "Rede" agrupa FTP, SFTP, SCP e TFTP; protocolos ocultos somem e os perfis salvos entram no protocolo certo.' `
        @('NavigationTreeService', 'NavigationNodeKind', 'PaneDisplayOptions') `
        @('NetworkGroupTests')),
    (New-Area 'Arquivos compactados (abrir como pasta)' `
        'ZIP, TAR, TAR.GZ, 7Z, GZ e RAR: listar com contagens, extrair, nomes repetidos, arquivos solidos, somente leitura e extracao sem sair da pasta de destino.' `
        @('ArchiveService', 'ArchivePathHelper', 'ArchiveFormat', 'NavigationTreeService') `
        @('ArchiveListingTests', 'SolidArchiveTests', 'ArchiveExtractionPathTests', 'ArchiveServiceExtractionTests')),
    (New-Area 'Icones de arquivos' `
        'Icone do Windows por extensao com cache, e o icone desenhado para arquivos compactados quando o Windows so tem o generico.' `
        @('ShellIconService') `
        @('ShellIconTests')),
    (New-Area 'Copiar, mover e compactar (progresso, cancelamento, tempo limite)' `
        'Copia nativa com progresso, cancelamento sem deixar resto, compactar ZIP e TAR.GZ, juncoes, janela de progresso e operacoes que travam.' `
        @('FileOperationService', 'NativeFileCopy', 'TransferScanner', 'TransferProgressFormatter', 'OperationTimeouts', 'OperationTracker') `
        @('FileOperationProgressTests', 'NativeFileCopyTests', 'CompressionProgressTests', 'FileOperationTests', 'TransferProgressFormatterTests', 'InactivityTimeoutTests', 'OperationTrackerTests')),
    (New-Area 'Previa de imagens e PDF' `
        'Miniaturas limitadas e assincronas, arquivos corrompidos ou gigantes sem travar, e quando a previa ao passar o mouse vale.' `
        @('FileThumbnailService', 'ThumbnailHoverPreview') `
        @('FileThumbnailServiceTests', 'ThumbnailHoverRulesTests')),
    (New-Area 'Cache de pastas e carga sem piscar' `
        'Indice SQLite das pastas (criar, ler, revalidar, limpar), troca de listas sem reiniciar a tela e liberacao de memoria depois de cargas grandes.' `
        @('DirectoryIndexService', 'RangeObservableCollection', 'MemoryTrimmer') `
        @('DirectoryIndexTests', 'RangeObservableCollectionTests', 'MemoryTrimmerTests')),
    (New-Area 'Navegacao e caminhos' `
        'Voltar, avancar e subir (inclusive dentro de arquivos e servidores) e deteccao de subcaminho.' `
        @('NavigationHistory', 'PathNavigation', 'PathSafetyService') `
        @('NavigationHistoryTests', 'PathNavigationTests', 'PathSafetyServiceTests')),
    (New-Area 'Tema, cores e interface' `
        'Cores de uso e de alerta, a cor de destaque nas linhas de hierarquia e na barra de rolagem, recursos XAML, densidade das linhas, ordenacao e texto da barra de status.' `
        @('ThemeManager', 'ExplorerThemeResources', 'SortHeaderLogic', 'SelectionStatusFormatter', 'StorageUsage', 'ExplorerViewSettingsService') `
        @('ThemeUsageColorsTests', 'XamlResourceTests', 'RowDensitySettingTests', 'SortHeaderLogicTests', 'StorageUsageTests', 'SelectionStatusFormatterTests')),
    (New-Area 'Abrir com e programas padrao' `
        'Nome amigavel dos programas, programa escolhido por extensao e a janela "Abrir com" do Windows.' `
        @('FileAssociationService', 'FileOpenService', 'DefaultApplicationOverrides') `
        @('ApplicationNameTests', 'DefaultApplicationOverridesTests', 'FileOpenServiceTests')),
    (New-Area 'Lixeira e Favoritos' `
        'Excluir ou restaurar so vale para o que esta mesmo na lixeira; a lixeira e o proprio "favorites:" nao entram nos favoritos; a lista de favoritos persiste entre execucoes e sobrevive a arquivo estragado.' `
        @('RecycleBinService', 'FavoritesService') `
        @('SpecialFolderGuardTests', 'FavoritesServiceTests')),
    (New-Area 'Painel de arquivos (um lado da janela)' `
        'Abrir numa pasta, navegar, criar pasta, renomear, excluir, copiar e mover (inclusive de dentro de ZIP, que pode ser alterado, e de .gz, .7z e .rar, que sao somente leitura), ordenar e marcar favoritos.' `
        @('FilePaneViewModel', 'NavigationTreeNodeViewModel', 'BreadcrumbSegmentViewModel', 'FileOperationService') `
        @('FilePaneViewModelTests')),
    (New-Area 'Janela principal (copiar e colar entre os paineis)' `
        'Painel ativo, area de transferencia de copiar e cortar, regras que recusam transferencias sem sentido, arrastar e soltar, ordenacao nos dois lados e lembrar o caminho ao abrir.' `
        @('MainWindowViewModel', 'RememberPathSettingsService', 'PaneDisplaySettingsService', 'TransferOutcome', 'DragPolicy') `
        @('MainWindowViewModelTests', 'DragPolicyTests')),
    (New-Area 'Janela Propriedades (botao direito)' `
        'O que a janela mostra para um arquivo, uma pasta, varios itens, uma unidade e um item fora do disco: tamanho e espaco em disco, datas, atributos, dono, versao de executavel, dimensoes de imagem, soma do conteudo das pastas (sem seguir links, contando o que nao foi lido, cancelavel), SHA-256 e MD5 numa leitura so, conferidos contra os valores publicados e contra o framework (cancelavel) e que fechar a janela cancela o que estava rodando.' `
        @('ItemPropertiesService', 'ItemPropertiesViewModel', 'PropertyRowViewModel', 'PropertySectionViewModel', 'PropertiesDialog') `
        @('ItemPropertiesServiceTests', 'ItemPropertiesViewModelTests')),
    (New-Area 'Menu superior: Exibir (unidades bloqueadas) e Preferencia' `
        'Estrutura do menu lida do XAML (sem o menu Arquivo, Ferramentas renomeado para Preferencia, itens novos ligados ao comando certo) e "Exibir unidades bloqueadas": ligado por padrao, lembrado entre execucoes, esconde so as unidades travadas (BitLocker ou sem acesso) e nunca as sem midia ou indisponiveis.' `
        @('ExplorerViewOptions', 'NavigationTreeService') `
        @('MainMenuTests', 'LockedDrivesOptionTests')),
    (New-Area 'Iniciar sempre como administrador' `
        'Marcar pede o administrador na hora e grava antes de reiniciar; recusar o aviso do Windows ou falhar desliga o check de volta; ao abrir com a opcao ligada o app pede o acesso (ou segue normalmente se recusar) e nunca pede de novo quando foi reaberto pelo proprio pedido. O aviso do Windows e trocado por uma elevacao de mentira.' `
        @('AdministratorStartupService') `
        @('AdministratorStartupServiceTests')),
    (New-Area 'Versao do programa' `
        'A versao fica num lugar so, o <Version> do csproj do app: o executavel informa essa mesma versao (a tela Sobre le a versao informacional) e o README a cita; mudar uma e esquecer a outra quebra o teste.' `
        @('AboutDialog') `
        @('AppVersionTests')),
    (New-Area 'Acesso negado e reiniciar como administrador' `
        'Mensagens de falha em portugues (sem o prefixo \\?\ do .NET), falta de permissao criada de verdade com regra NTFS de negar (excluir, renomear, criar pasta, copiar, mover, compactar), quando perguntar se quer reiniciar como administrador, como o app reinicia e espera o processo antigo sair, e pasta que o Windows nao deixa listar (nao vira "vazia" no cache e a linha mostra um cadeado no lugar das contagens e a barra de status diz "sem permissao para abrir").' `
        @('FileErrorText', 'ElevationPrompt', 'ElevationService', 'IElevationService', 'StartupArguments', 'TransferAccessDeniedException') `
        @('FileErrorTextTests', 'AccessDeniedOperationTests', 'ElevationTests', 'DeniedFolderRowTests')),
    (New-Area 'Start.bat: fechar o app antes de compilar (stop-dora)' `
        'Pede para a janela fechar e espera (o app pode perguntar sobre uma operacao em andamento), so depois usa o taskkill; processo sem janela vai direto ao taskkill; processo que o terminal nao consegue encerrar (app aberto como administrador) e reconhecido antes, para avisar em vez de esperar. Os testes usam processos de mentira e so carregam as funcoes do script, nunca o app de verdade.' `
        @() `
        @('StopDoraScriptTests')),
    (New-Area 'Arrastar e soltar: onde cai e o destaque da pasta de destino' `
        'A linha sob o mouse e a que vale (uma unidade aberta recebe na raiz, nao na primeira subpasta; espaco em branco cai na pasta aberta do painel), a pasta que recebe e destacada durante o arrasto e so uma por vez, e soltar na pasta de origem ou de um item dentro dele mesmo e recusado.' `
        @('RowHitTest', 'FilePaneViewModel', 'NavigationTreeNodeViewModel') `
        @('DropTargetTests')),
    (New-Area 'Renomear (F2, clique devagar, nome sem a extensao)' `
        'Que trecho do nome ja vem selecionado (sem a extensao; pasta, .gitignore e nome sem extensao selecionam tudo), quando um clique devagar no nome comeca a renomear (so o item que ja era o unico selecionado, fora de unidade e Lixeira, sem outro em renomeacao) e confirmar ou cancelar; depois de confirmar o item com o novo nome fica como unico selecionado (e a tela e avisada para dar o foco a ele).' `
        @('RenameSelection', 'SystemMouse', 'FilePaneViewModel') `
        @('RenameTests')),
    (New-Area 'Menu da Lixeira (esvaziar e restaurar tudo)' `
        'Esvaziar chama o Windows uma vez, Lixeira ja vazia nao e erro nem chama o Windows, recusa e excecao do Windows viram mensagem em portugues, a linha da Lixeira e atualizada nos dois paineis e as perguntas dizem quantos itens. O Windows e a consulta da Lixeira sao trocados por funcoes de mentira: nenhum teste esvazia a Lixeira de verdade.' `
        @('RecycleBinService', 'RecycleBinText') `
        @('RecycleBinMenuTests')),
    (New-Area 'Localizar (colunas ajustaveis e nova janela)' `
        'O cabecalho das colunas tem a alca que o GridView procura (sem ela a largura nao muda), o menu pode ser aumentado e nao tem teto de altura, existe o botao Abrir em Janela e a janela mostra Nome, Caminho e Tamanho ajustaveis (lidos do XAML); a pesquisa acha item em subpastas fundas e a barra alimenta a lista que a janela mostra.' `
        @('SearchDialog', 'SearchPopupSizing', 'FileSearchService', 'CenterToolbarViewModel') `
        @('SearchWindowTests')),
    (New-Area 'Duplicados (padrao: nome e tamanho; profundo: tamanho e hash)' `
        'Padrao agrupa por nome (sem diferenciar maiusculas) e tamanho sem ler arquivo; profundo confirma por SHA-256 (inclusive diferenca so no ultimo byte de um arquivo grande), so le arquivo que tem o tamanho de outro, conta o travado como ilegivel e segue; vazio, juncao e pasta inexistente; cancelar e andamento; a leitura enxuta correta em todos os tamanhos ao redor do bloco de leitura (64 KB e 1 MiB, inclusive diferenca so no ultimo byte), arquivo so da nuvem nao lido, varias leituras ao mesmo tempo com o mesmo resultado de uma so, a escolha do numero de leituras pelo tipo de disco (SSD x HD x desconhecido), as pastas do sistema de fora so no topo de uma unidade (e a opcao de inclui-las), a lista plana da janela (cabecalho de grupo + copias), modo, subpastas, textos, o botao no grupo Pasta e o icone novo e unico da mesma fonte.' `
        @('DuplicateFinderService', 'DuplicateWorkers', 'StorageKind', 'DuplicatesViewModel', 'DuplicateText', 'DuplicatesDialog', 'DuplicateRowViewModel') `
        @('DuplicateFinderTests', 'DuplicatesViewModelTests')),
    (New-Area 'Registro de servicos (injecao de dependencia)' `
        'Todo servico registrado tem as dependencias registradas; um servico novo esquecido estouraria so ao abrir o app.' `
        @('ServiceRegistration') `
        @('ServiceRegistrationTests')),
    (New-Area 'Falhas esperadas do sistema' `
        'Volume bloqueado (BitLocker) e erros de E/S que o app trata sem registrar como erro.' `
        @('ExpectedFailures') `
        @('ExpectedFailuresTests'))
)

# O que foi conferido apenas na tela ou ainda nao tem teste automatico. Escrito a mao: ao testar algo daqui, tire da lista.
$manualOnly = @(
    [pscustomobject]@{ Item = 'Atalhos de teclado (tabela do README)'; Why = 'Dependem do estado real do teclado no WPF.'; How = 'Teclas reais com keybd_event (AGENTS.md, armadilha 12).' },
    [pscustomobject]@{ Item = 'Visual do redesenho (cores, linhas de hierarquia, barras, icones, truncamento de nomes)'; Why = 'Aparencia; os testes so conferem os recursos XAML e as cores.'; How = 'scripts\dev\ui-capture.ps1 e captura de tela.' },
    [pscustomobject]@{ Item = 'Previa ao passar o mouse (a dica na tela)'; Why = 'So a regra de quando vale e testada; a dica depende da janela.'; How = 'Passar o mouse sobre uma imagem com o app aberto.' },
    [pscustomobject]@{ Item = 'Seta e contagem de pastas dentro de arquivos compactados, na tela'; Why = 'Testado na arvore (ArchiveListingTests); o desenho da seta depende do WPF.'; How = 'Roteiro de UI Automation: ExpandCollapseState de cada filho do arquivo.' },
    [pscustomobject]@{ Item = 'Copiar de dentro de um .7z, .rar ou .gz clicando no app'; Why = 'A extracao e testada no ArchiveService, mas nao pela interface.'; How = 'Selecionar o arquivo dentro do arquivo compactado e copiar para a outra tela.' },
    [pscustomobject]@{ Item = 'Barra central (CenterToolbarViewModel) e controles dos paineis (FilePaneControl)'; Why = 'Dependem do WPF e de janelas; os paineis e a janela principal sao testados sem janela, a barra central e os controles nao.'; How = 'Rodar o app e conferir o log sem [ERR] nem [WRN].' },
    [pscustomobject]@{ Item = 'Janela de progresso aparecendo quando a operacao passa de 600 ms'; Why = 'Os testes copiam arquivos pequenos, que terminam antes; a janela nunca e criada neles.'; How = 'Copiar uma pasta grande no app e ver a janela, o cancelar e o fechamento.' },
    [pscustomobject]@{ Item = 'Excluir para a Lixeira do Windows (modo RecycleBin)'; Why = 'Mexe na lixeira real do usuario; os testes usam exclusao permanente em pastas temporarias.'; How = 'Excluir um arquivo de teste no app e achar na Lixeira.' },
    [pscustomobject]@{ Item = 'Controles e animacoes da arvore (rolagem suave, linhas de hierarquia, revelar ao expandir)'; Why = 'Aparencia e tempo; sem teste automatico.'; How = 'Rodar o app e navegar.' },
    [pscustomobject]@{ Item = 'Abrir pasta sem piscar (a lista cresce enquanto os filhos entram em cascata; TreeExpansionRevealAnimator)'; Why = 'Depende do WPF, do tempo e da tela: sem janela nao ha como testar.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario blink: cada pasta precisa sair ok (maior queda em UM quadro abaixo de 20% do conteudo). Antes da correcao a pasta grande dava 76% (PISCA).' },
    [pscustomobject]@{ Item = 'Reiniciar como administrador de verdade (aviso do Windows/UAC) e as janelas "Reiniciar como administrador / Agora nao"'; Why = 'O aviso do Windows roda em outra area de trabalho e pede uma pessoa; o app so e testado ate chamar o Windows (ElevationTests) e a regra de quando perguntar (ElevationPrompt).'; How = 'Tentar excluir um item de C:\Windows ou C:\Program Files: a janela deve falar de acesso negado, em portugues, e perguntar. Escolher Reiniciar, confirmar o aviso do Windows e ver o app abrir de novo (Gerenciador de Tarefas: Elevado = Sim); repetir a operacao.' },
    [pscustomobject]@{ Item = 'Aviso ao abrir uma pasta que o Windows nao deixa listar (duplo clique, Enter ou seta para a direita)'; Why = 'O teste de unidade cobre o no marcado (ListingDenied) e a mensagem, nao o gesto no TreeView real: um clique so seleciona a linha, e o cadeado so e conferido no app real (os testes cobrem a marca no no e a troca das contagens, nao o desenho do XAML).'; How = 'Criar uma pasta com um arquivo e negar a leitura dela: icacls <pasta> /deny "%USERNAME%:(RD)". Abrir o app: a linha da pasta mostra um cadeado no lugar de 0 pastas / 0 arquivos (passe o mouse: "Sem permissao para abrir esta pasta"), e ao selecionar a pasta a barra de status diz "sem permissao para abrir", nas linhas compactas e nas confortaveis (Exibir > Linhas compactas em colunas). Dar duplo clique na pasta: aparece "Sem permissao para abrir ..." com Reiniciar como administrador / Agora nao. Abrir a mesma pasta de novo (seta para a direita) nao repete o aviso. Remover a regra: icacls <pasta> /remove:d %USERNAME%.' },
    [pscustomobject]@{ Item = 'Foco e setas depois de apagar pelo teclado (Shift+Delete)'; Why = 'O vizinho a selecionar e testado (FilePaneViewModelTests), mas o foco do WPF so existe com a janela aberta.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario delfocus (app fechado; AGENTS.md, armadilha 18): cada verificacao sai [OK] ou [FALHOU]; o foco deve ir para o vizinho e Baixo/Cima mover o foco sem clicar.' },
    [pscustomobject]@{ Item = 'Ctrl+clique e Shift+clique para selecionar varios itens'; Why = 'A regra da selecao (ligar, desligar, intervalo, outra pasta) e testada no modelo, mas o clique com o modificador segurado so existe com a janela aberta.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario multisel (app fechado; AGENTS.md, armadilha 19): Shift+clique (intervalo) e Ctrl+clique (itens soltos), lendo "N itens selecionados" na barra de status; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Arrastar e soltar entre os paineis, para o Windows Explorer e para navegadores (e do Explorer para o app)'; Why = 'O destino do soltar e o que sai da janela sao testados no modelo, mas o arrasto de verdade depende do mouse, do OLE e das janelas de outros programas.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario dragdrop (app fechado; AGENTS.md, armadilha 20): mouse de verdade, janela real do Explorer, Chrome, Edge e Firefox com perfil temporario; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Menu de contexto (botao direito): Adicionar e Remover dos favoritos em arquivo, pasta e favorito da pasta Favoritos'; Why = 'O texto da entrada e testado no modelo, mas o menu so existe com a janela aberta e o clique direito.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario favmenu (app fechado; AGENTS.md, armadilha 21): botao direito de verdade, le as entradas do menu pelo UI Automation e confere o favorites.json; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Janela Propriedades: abrir pelo botao direito, o visual nos temas escuro e claro e os valores contra o disco'; Why = 'Os dados e o texto sao testados sem janela, mas a janela, o menu e o visual so existem com o app aberto.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario props (app fechado; AGENTS.md, armadilha 22): botao direito de verdade num arquivo, numa pasta, em dois itens e numa unidade, le as linhas pelo UI Automation, confere o SHA-256 contra o Get-FileHash e fotografa nos dois temas; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Menu superior na tela (Exibir, Temas, Preferencia, Ajuda) e o aviso do Windows de "Iniciar sempre como administrador"'; Why = 'A estrutura do menu e as ligacoes sao testadas pelo XAML, mas o menu aberto so existe com a janela; o aviso do Windows (UAC) roda numa area protegida e nenhum roteiro consegue aceita-lo ou recusa-lo.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario menu (app fechado; AGENTS.md, armadilha 23) confere os menus, o estado dos checks e liga e desliga "Exibir unidades bloqueadas" de verdade (uma unidade travada some e volta nos dois paineis), sem nunca acionar o administrador. A mao: o menu superior mostra Exibir, Temas, Preferencia e Ajuda (sem Arquivo), com "Exibir unidades bloqueadas" em Exibir e "Iniciar sempre como administrador" em Preferencia. Marcar o administrador: o Windows pede a confirmacao; recusar deixa o check desmarcado, aceitar reabre o app (Gerenciador de Tarefas: Elevado = Sim) e, fechando e abrindo de novo, o aviso aparece ao abrir. Desmarcar nao pede nada.' },
    [pscustomobject]@{ Item = 'Selecionar com o retangulo (arrastar na borda esquerda) e arrastar a selecao, o destaque da pasta de destino durante o arrasto e soltar na raiz da unidade'; Why = 'A regra da linha sob o mouse (RowHitTest) e o destaque no modelo sao testados, mas o mouse apertado, o OLE e o desenho do destaque so existem com a janela aberta.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario marqueedrag (app fechado; AGENTS.md, armadilhas 25 e 26): retangulo de 4 itens, arrasto da selecao para o outro painel, o painel de origem continua na mesma pasta, fotos do destaque (dest e a linha C:) e a pasta solta na raiz de C:; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Renomear na tela: F2 e clique devagar abrem a caixa com o foco e o nome sem a extensao selecionado, selecionar o texto com o mouse'; Why = 'O trecho selecionado e a regra do clique devagar sao testados, mas o foco, o mouse e o temporizador so existem com a janela aberta.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario rename (app fechado; AGENTS.md, armadilha 27): F2 e clique devagar (le a selecao pelo UI Automation), arrasto do mouse dentro da caixa (o arquivo nao pode sair do lugar), Enter renomeia no disco e deixa o item com o novo nome selecionado e com o foco (a pasta que o contem nao), e Esc cancela.' },
    [pscustomobject]@{ Item = 'Menu da Lixeira na tela, Localizar (Abrir em Janela e coluna ajustada com o mouse) e a janela Itens duplicados aberta pelo botao do grupo Pasta'; Why = 'As regras sao testadas sem janela, mas o menu, o botao, as janelas e o arrasto da borda do cabecalho so existem com o app aberto.'; How = 'powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario newfeatures (app fechado; AGENTS.md, armadilhas 28 a 30): botao direito na Lixeira (so le o menu e recusa a pergunta de esvaziar), Localizar e Abrir em Janela, largura da coluna Nome antes e depois de arrastar a borda, Duplicados nos tipos Padrao e Profundo; cada verificacao sai [OK] ou [FALHOU].' },
    [pscustomobject]@{ Item = 'Esvaziar a Lixeira de verdade (SHEmptyRecycleBin)'; Why = 'Apaga para sempre a Lixeira do usuario; os testes trocam a chamada do Windows por uma de mentira e os roteiros de tela recusam a pergunta.'; How = 'Numa maquina de teste, jogar arquivos de teste na Lixeira, botao direito na Lixeira > Esvaziar Lixeira > Sim e conferir que a Lixeira ficou vazia nos dois paineis.' },
    [pscustomobject]@{ Item = 'Janelas de dialogo (progresso, conexao de rede, ordenar, excluir)'; Why = 'Janelas WPF; sem teste automatico.'; How = 'Abrir cada uma no app.' },
    [pscustomobject]@{ Item = 'Restaurar e excluir de verdade da Lixeira'; Why = 'Mexe na lixeira real do usuario; so as travas sao testadas.'; How = 'Com arquivos de teste, na tela.' },
    [pscustomobject]@{ Item = 'Start.bat e scripts de apoio'; Why = 'O fechamento do app esta em teste automatico (StopDoraScriptTests, com processos de mentira); o Start.bat inteiro (compilar e abrir) e o caso de um app aberto como administrador, que pede o UAC, nao.'; How = 'Rodar Start.bat e conferir que o app abre. Com o app aberto, rodar de novo: ele fecha sozinho (o log termina com "encerrado") e reabre. Com o app aberto como administrador (Reiniciar como administrador), o Start.bat deve parar com o aviso de que nao consegue fecha-lo, sem compilar.' }
)

# ---- le os testes -----------------------------------------------------------------------------------------------------
$classPattern = '^\s*(?:public|internal)\s+(?:(?:sealed|static|abstract|partial)\s+)*class\s+(\w+)'
$attributePattern = '^\s*\[(Fact|Theory)\b'
$methodPattern = '^\s*public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\('

$testFiles = Get-ChildItem $testRoot -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object Name
$testClasses = @{}

foreach ($file in $testFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $current = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $classPattern) { $current = $Matches[1]; continue }
        if ($current -and $lines[$i] -match $attributePattern) {
            $isTheory = ($Matches[1] -eq 'Theory')
            $inlineCount = 0
            $name = $null
            for ($j = $i + 1; $j -lt [Math]::Min($i + 40, $lines.Count); $j++) {
                if ($lines[$j] -match '^\s*\[(InlineData|MemberData|ClassData)\b') { $inlineCount++; continue }
                if ($lines[$j] -match $methodPattern) { $name = $Matches[1]; break }
            }
            if ($name) {
                if (-not $testClasses.ContainsKey($current)) {
                    $testClasses[$current] = [pscustomobject]@{ Name = $current; File = $file.Name; Methods = New-Object System.Collections.Generic.List[object] }
                }
                $cases = 1
                if ($isTheory -and $inlineCount -gt 0) { $cases = $inlineCount }
                $testClasses[$current].Methods.Add([pscustomobject]@{ Name = $name; Cases = $cases })
            }
        }
    }
}

# ---- confere o mapa contra o codigo -------------------------------------------------------------------------------------
$sourceText = (Get-ChildItem $srcRoot -Recurse -Include *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
$problems = New-Object System.Collections.Generic.List[string]

function Resolve-Type {
    param([string]$TypeName)
    if ($sourceText -match ('(?m)\b(?:class|record|enum|struct|interface)\s+' + [regex]::Escape($TypeName) + '\b')) { return $TypeName }
    $problems.Add("tipo $TypeName")
    return "$TypeName (?)"
}

$mappedClasses = @{}
foreach ($area in $areas) {
    foreach ($t in $area.Tests) {
        if (-not $testClasses.ContainsKey($t)) { $problems.Add("classe de teste $t (area '$($area.Name)')") }
        $mappedClasses[$t] = $true
    }
}
$unmapped = @($testClasses.Keys | Where-Object { -not $mappedClasses.ContainsKey($_) } | Sort-Object)
foreach ($u in $unmapped) { $problems.Add("classe de teste sem area: $u") }

# ---- escreve ----------------------------------------------------------------------------------------------------------
$totalMethods = 0
$totalCases = 0
foreach ($c in $testClasses.Values) {
    $totalMethods += $c.Methods.Count
    $totalCases += ($c.Methods | Measure-Object -Property Cases -Sum).Sum
}

$out = New-Object System.Collections.Generic.List[string]
$out.Add('# Dora Explorer - Book de Testes')
$out.Add('')
$out.Add('> Auto-gerado por scripts/generate-booktest.ps1. Nao edite a mao. Atualizacao: ' + $timestamp)
$out.Add('> Regras de trabalho (testar sempre que mudar uma area): AGENTS.md. Mapa do codigo: project.md.')
$out.Add('')
$out.Add('## Como usar')
$out.Add('')
$out.Add('1. Achou a area que voce vai mexer (ou o tipo, no "Indice por tipo")? Rode os testes dela ANTES de mexer, para saber que estao passando.')
$out.Add('2. Mudou ou criou algo: crie ou edite os testes da area (de preferencia vendo o teste falhar antes de implementar), rode a area e depois a suite inteira.')
$out.Add('3. Deixe os testes salvos em tests/DoraExplorer.App.Tests: eles sao a rede de seguranca da proxima mudanca. Regenere este book ao criar ou renomear testes.')
$out.Add('')
$out.Add('```bat')
$out.Add('dotnet test DoraExplorer.sln -c Release                                   (tudo)')
$out.Add('dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~NomeDaClasse"   (uma classe)')
$out.Add('dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~A|FullyQualifiedName~B"   (varias)')
$out.Add('powershell -NoProfile -ExecutionPolicy Bypass -File scripts\generate-booktest.ps1   (regenera este book)')
$out.Add('```')
$out.Add('')
$out.Add('Teste nunca toca em %AppData%\DoraExplorer do usuario nem na rede real; use pasta temporaria, banco temporario e portas livres.')
$out.Add('')
$out.Add('## Resumo')
$out.Add('')
$out.Add('| Item | Valor |')
$out.Add('|------|-------|')
$out.Add('| Areas | ' + $areas.Count + ' |')
$out.Add('| Classes de teste | ' + $testClasses.Count + ' |')
$out.Add('| Metodos de teste | ' + $totalMethods + ' |')
$out.Add('| Casos (cada InlineData conta um) | ' + $totalCases + ' |')
$out.Add('')
$out.Add('O numero de casos e estimado pelo codigo; o que vale e o total que o `dotnet test` informa.')
$out.Add('')
$out.Add('## Areas e os testes que as protegem')
$out.Add('')

foreach ($area in $areas) {
    $areaCases = 0
    foreach ($t in $area.Tests) {
        if ($testClasses.ContainsKey($t)) { $areaCases += ($testClasses[$t].Methods | Measure-Object -Property Cases -Sum).Sum }
    }
    $out.Add('### ' + $area.Name)
    $out.Add('')
    $out.Add($area.Covers)
    $out.Add('')
    $out.Add('- Codigo: ' + (($area.Types | ForEach-Object { Resolve-Type $_ }) -join ', '))
    $out.Add('- Testes (' + $areaCases + ' casos): ' + (($area.Tests | ForEach-Object {
        if ($testClasses.ContainsKey($_)) { $_ + ' [' + $testClasses[$_].File + ']' } else { $_ + ' (?)' }
    }) -join ', '))
    $filter = ($area.Tests | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
    $out.Add('- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "' + $filter + '"`')
    $out.Add('')
}

$out.Add('## Indice por tipo (mudou este tipo? rode esta area)')
$out.Add('')
$out.Add('| Tipo | Area |')
$out.Add('|------|------|')
$byType = @{}
foreach ($area in $areas) {
    foreach ($t in $area.Types) {
        if (-not $byType.ContainsKey($t)) { $byType[$t] = New-Object System.Collections.Generic.List[string] }
        if (-not $byType[$t].Contains($area.Name)) { $byType[$t].Add($area.Name) }
    }
}
foreach ($t in ($byType.Keys | Sort-Object)) {
    $out.Add('| ' + $t + ' | ' + ($byType[$t] -join '; ') + ' |')
}
$out.Add('')

$out.Add('## Conferido so na tela ou sem teste automatico')
$out.Add('')
$out.Add('Itens que nenhum teste automatico protege. Ao mexer neles, confira na tela e diga no relato o que foi e o que nao foi verificado.')
$out.Add('')
$out.Add('| Item | Por que | Como conferir |')
$out.Add('|------|---------|---------------|')
foreach ($m in $manualOnly) {
    $out.Add('| ' + $m.Item + ' | ' + $m.Why + ' | ' + $m.How + ' |')
}
$out.Add('')

if ($problems.Count -gt 0) {
    $out.Add('## Problemas do mapa (corrija o script)')
    $out.Add('')
    foreach ($p in ($problems | Select-Object -Unique)) { $out.Add('- ' + $p) }
    $out.Add('')
}

$out.Add('## Apendice: casos por classe')
$out.Add('')
foreach ($c in ($testClasses.Values | Sort-Object Name)) {
    $out.Add('### ' + $c.Name + ' (' + $c.File + ')')
    $out.Add('')
    foreach ($m in ($c.Methods | Sort-Object Name)) {
        $suffix = ''
        if ($m.Cases -gt 1) { $suffix = ' (' + $m.Cases + ' casos)' }
        $out.Add('- ' + $m.Name + $suffix)
    }
    $out.Add('')
}

[System.IO.File]::WriteAllLines($outFile, $out, [System.Text.UTF8Encoding]::new($false))
foreach ($p in ($problems | Select-Object -Unique)) { Write-Warning "Book de testes: $p" }
Write-Host "tests\BOOKTEST.md atualizado: $($areas.Count) areas, $($testClasses.Count) classes, $totalMethods metodos, $totalCases casos (estimados)."
