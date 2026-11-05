# Dora Explorer - Book de Testes

> Auto-gerado por scripts/generate-booktest.ps1. Nao edite a mao. Atualizacao: 2026-11-05 09:21
> Regras de trabalho (testar sempre que mudar uma area): AGENTS.md. Mapa do codigo: project.md.

## Como usar

1. Achou a area que voce vai mexer (ou o tipo, no "Indice por tipo")? Rode os testes dela ANTES de mexer, para saber que estao passando.
2. Mudou ou criou algo: crie ou edite os testes da area (de preferencia vendo o teste falhar antes de implementar), rode a area e depois a suite inteira.
3. Deixe os testes salvos em tests/DoraExplorer.App.Tests: eles sao a rede de seguranca da proxima mudanca. Regenere este book ao criar ou renomear testes.

```bat
dotnet test DoraExplorer.sln -c Release                                   (tudo)
dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~NomeDaClasse"   (uma classe)
dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~A|FullyQualifiedName~B"   (varias)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\generate-booktest.ps1   (regenera este book)
```

Teste nunca toca em %AppData%\DoraExplorer do usuario nem na rede real; use pasta temporaria, banco temporario e portas livres.

## Resumo

| Item | Valor |
|------|-------|
| Areas | 34 |
| Classes de teste | 78 |
| Metodos de teste | 813 |
| Casos (cada InlineData conta um) | 1189 |

O numero de casos e estimado pelo codigo; o que vale e o total que o `dotnet test` informa.

## Areas e os testes que as protegem

### Seguranca de senhas e configuracoes

Senhas e frases-senha guardadas protegidas (DPAPI), migracao de senhas antigas em texto puro, versao do SQLite sem a falha conhecida.

- Codigo: ProtectedStringJsonConverter, AppSettingsService
- Testes (10 casos): ProtectedPasswordTests [SecurityTests.cs], LegacyPasswordMigrationTests [SecurityTests.cs], SqliteSecurityTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ProtectedPasswordTests|FullyQualifiedName~LegacyPasswordMigrationTests|FullyQualifiedName~SqliteSecurityTests"`

### Confianca em servidores (chaves, certificados, login do cliente)

O cliente so confia em chave de host ou certificado conhecido, avisa quando mudam, e escolhe os metodos de login (chave, senha, interativo).

- Codigo: KnownHostsStore, HostKeyVerifier, FtpCertificateVerifier, FtpTlsCertificate
- Testes (21 casos): HostKeyTests [SecurityTests.cs], FtpClientSecurityTests [NetworkClientTests.cs], SshAuthenticationTests [NetworkClientTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~HostKeyTests|FullyQualifiedName~FtpClientSecurityTests|FullyQualifiedName~SshAuthenticationTests"`

### Servidor FTP e FTPS

Comandos FTP padrao (MLSD/MLST, REST/APPE, passivo e ativo), FTPS explicito, modo somente leitura, bloqueio por tentativas e nomes perigosos.

- Codigo: FtpServer
- Testes (15 casos): FtpServerTests [NetworkServerTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FtpServerTests"`

### Servidor SFTP e SCP

Servidor SSH proprio: SFTP v3 e SCP com cliente real, chave de host estavel, confinamento na pasta compartilhada, nada de shell nem encaminhamento de porta.

- Codigo: SshFileServer, SftpServerSession, ScpServerSession, SshHostKeyStore, ServerVirtualPath, LoginThrottle
- Testes (122 casos): SshServerEndToEndTests [SshServerTests.cs], SftpServerSessionTests [SshServerTests.cs], ScpServerSessionTests [SshServerTests.cs], ScpCommandTests [SshServerTests.cs], ServerVirtualPathTests [SshServerTests.cs], LoginThrottleTests [SshServerTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~SshServerEndToEndTests|FullyQualifiedName~SftpServerSessionTests|FullyQualifiedName~ScpServerSessionTests|FullyQualifiedName~ScpCommandTests|FullyQualifiedName~ServerVirtualPathTests|FullyQualifiedName~LoginThrottleTests"`

### TFTP (servidor e cliente)

Opcoes blksize e tsize, retransmissao, volta do contador de blocos, somente leitura, e o cliente com tempo limite e cancelamento.

- Codigo: TftpServer, TftpClient
- Testes (30 casos): TftpServerTests [NetworkServerTests.cs], TftpClientTests [TftpClientTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~TftpServerTests|FullyQualifiedName~TftpClientTests"`

### Travas dos servidores locais (pasta raiz e portas)

Pasta raiz ampla demais e caminhos fora dela sao recusados; o app nunca encerra processo de terceiros que ocupa uma porta.

- Codigo: NetworkServerService, NetworkPortGuard
- Testes (24 casos): NetworkServerHardeningTests [SecurityTests.cs], NetworkPortGuardTests [NetworkPortGuardTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~NetworkServerHardeningTests|FullyQualifiedName~NetworkPortGuardTests"`

### Clientes de rede (sessoes, copia entre servidores, saude dos perfis)

Caminhos remotos, download e envio com progresso e limpeza, copia entre servidores, perfis iguais, indicador de saude sem travar a conta.

- Codigo: RemoteFileService, RemoteTransferEngine, NetworkFileService, NetworkClientHealthService
- Testes (44 casos): RemoteFileServiceTests [RemoteFileTests.cs], RemoteFileServiceFtpTests [RemoteFileTests.cs], RemoteTransferEngineTests [RemoteFileTests.cs], NetworkFileServiceTests [InfrastructureTests.cs], HealthServiceTests [HealthServiceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~RemoteFileServiceTests|FullyQualifiedName~RemoteFileServiceFtpTests|FullyQualifiedName~RemoteTransferEngineTests|FullyQualifiedName~NetworkFileServiceTests|FullyQualifiedName~HealthServiceTests"`

### Pasta Rede na arvore

A pasta "Rede" agrupa FTP, SFTP, SCP e TFTP; protocolos ocultos somem e os perfis salvos entram no protocolo certo.

- Codigo: NavigationTreeService, NavigationNodeKind, PaneDisplayOptions
- Testes (5 casos): NetworkGroupTests [NetworkGroupTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~NetworkGroupTests"`

### Arquivos compactados (abrir como pasta)

ZIP, TAR, TAR.GZ, 7Z, GZ e RAR: listar com contagens, extrair, nomes repetidos, arquivos solidos, somente leitura e extracao sem sair da pasta de destino.

- Codigo: ArchiveService, ArchivePathHelper, ArchiveFormat, NavigationTreeService
- Testes (55 casos): ArchiveListingTests [ArchiveListingTests.cs], SolidArchiveTests [SolidArchiveTests.cs], ArchiveExtractionPathTests [SecurityTests.cs], ArchiveServiceExtractionTests [SecurityTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ArchiveListingTests|FullyQualifiedName~SolidArchiveTests|FullyQualifiedName~ArchiveExtractionPathTests|FullyQualifiedName~ArchiveServiceExtractionTests"`

### Icones de arquivos

Icone do Windows por extensao com cache, e o icone desenhado para arquivos compactados quando o Windows so tem o generico.

- Codigo: ShellIconService
- Testes (27 casos): ShellIconTests [ShellIconTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ShellIconTests"`

### Copiar, mover e compactar (progresso, cancelamento, tempo limite)

Copia nativa com progresso, cancelamento sem deixar resto, compactar ZIP e TAR.GZ, juncoes, janela de progresso e operacoes que travam.

- Codigo: FileOperationService, NativeFileCopy, TransferScanner, TransferProgressFormatter, OperationTimeouts, OperationTracker
- Testes (46 casos): FileOperationProgressTests [TransferTests.cs], NativeFileCopyTests [TransferTests.cs], CompressionProgressTests [TransferTests.cs], FileOperationTests [FileOperationTests.cs], TransferProgressFormatterTests [TransferTests.cs], InactivityTimeoutTests [TransferTests.cs], OperationTrackerTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FileOperationProgressTests|FullyQualifiedName~NativeFileCopyTests|FullyQualifiedName~CompressionProgressTests|FullyQualifiedName~FileOperationTests|FullyQualifiedName~TransferProgressFormatterTests|FullyQualifiedName~InactivityTimeoutTests|FullyQualifiedName~OperationTrackerTests"`

### Previa de imagens e PDF

Miniaturas limitadas e assincronas, arquivos corrompidos ou gigantes sem travar, e quando a previa ao passar o mouse vale.

- Codigo: FileThumbnailService, ThumbnailHoverPreview
- Testes (27 casos): FileThumbnailServiceTests [ThumbnailTests.cs], ThumbnailHoverRulesTests [ThumbnailTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FileThumbnailServiceTests|FullyQualifiedName~ThumbnailHoverRulesTests"`

### Cache de pastas e carga sem piscar

Indice SQLite das pastas (criar, ler, revalidar, limpar), troca de listas sem reiniciar a tela e liberacao de memoria depois de cargas grandes.

- Codigo: DirectoryIndexService, RangeObservableCollection, MemoryTrimmer
- Testes (31 casos): DirectoryIndexTests [DirectoryIndexTests.cs], RangeObservableCollectionTests [InfrastructureTests.cs], MemoryTrimmerTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~DirectoryIndexTests|FullyQualifiedName~RangeObservableCollectionTests|FullyQualifiedName~MemoryTrimmerTests"`

### Navegacao e caminhos

Voltar, avancar e subir (inclusive dentro de arquivos e servidores) e deteccao de subcaminho.

- Codigo: NavigationHistory, PathNavigation, PathSafetyService
- Testes (30 casos): NavigationHistoryTests [NavigationTests.cs], PathNavigationTests [NavigationTests.cs], PathSafetyServiceTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~NavigationHistoryTests|FullyQualifiedName~PathNavigationTests|FullyQualifiedName~PathSafetyServiceTests"`

### Tema, cores e interface

Cores de uso e de alerta, a cor de destaque nas linhas de hierarquia e na barra de rolagem, recursos XAML, densidade das linhas, ordenacao e texto da barra de status.

- Codigo: ThemeManager, ExplorerThemeResources, SortHeaderLogic, SelectionStatusFormatter, StorageUsage, ExplorerViewSettingsService
- Testes (34 casos): ThemeUsageColorsTests [InterfaceTests.cs], XamlResourceTests [NavigationTests.cs], RowDensitySettingTests [NavigationTests.cs], SortHeaderLogicTests [NavigationTests.cs], StorageUsageTests [InterfaceTests.cs], SelectionStatusFormatterTests [InterfaceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ThemeUsageColorsTests|FullyQualifiedName~XamlResourceTests|FullyQualifiedName~RowDensitySettingTests|FullyQualifiedName~SortHeaderLogicTests|FullyQualifiedName~StorageUsageTests|FullyQualifiedName~SelectionStatusFormatterTests"`

### Abrir com e programas padrao

Nome amigavel dos programas, programa escolhido por extensao e a janela "Abrir com" do Windows.

- Codigo: FileAssociationService, FileOpenService, DefaultApplicationOverrides
- Testes (30 casos): ApplicationNameTests [InterfaceTests.cs], DefaultApplicationOverridesTests [InterfaceTests.cs], FileOpenServiceTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ApplicationNameTests|FullyQualifiedName~DefaultApplicationOverridesTests|FullyQualifiedName~FileOpenServiceTests"`

### Lixeira e Favoritos

Excluir ou restaurar so vale para o que esta mesmo na lixeira; a lixeira e o proprio "favorites:" nao entram nos favoritos; a lista de favoritos persiste entre execucoes e sobrevive a arquivo estragado.

- Codigo: RecycleBinService, FavoritesService
- Testes (39 casos): SpecialFolderGuardTests [SpecialFolderGuardTests.cs], FavoritesServiceTests [FavoritesServiceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~SpecialFolderGuardTests|FullyQualifiedName~FavoritesServiceTests"`

### Painel de arquivos (um lado da janela)

Abrir numa pasta, navegar, criar pasta, renomear, excluir, copiar e mover (inclusive de dentro de ZIP, que pode ser alterado, e de .gz, .7z e .rar, que sao somente leitura), ordenar e marcar favoritos.

- Codigo: FilePaneViewModel, NavigationTreeNodeViewModel, BreadcrumbSegmentViewModel, FileOperationService
- Testes (69 casos): FilePaneViewModelTests [FilePaneViewModelTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FilePaneViewModelTests"`

### Janela principal (copiar e colar entre os paineis)

Painel ativo, area de transferencia de copiar e cortar, regras que recusam transferencias sem sentido, arrastar e soltar, ordenacao nos dois lados e lembrar o caminho ao abrir.

- Codigo: MainWindowViewModel, RememberPathSettingsService, PaneDisplaySettingsService, TransferOutcome, DragPolicy
- Testes (31 casos): MainWindowViewModelTests [MainWindowViewModelTests.cs], DragPolicyTests [DragPolicyTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~DragPolicyTests"`

### Janela Propriedades (botao direito)

O que a janela mostra para um arquivo, uma pasta, varios itens, uma unidade e um item fora do disco: tamanho e espaco em disco, datas, atributos, dono, versao de executavel, dimensoes de imagem, soma do conteudo das pastas (sem seguir links, contando o que nao foi lido, cancelavel), SHA-256 e MD5 numa leitura so, conferidos contra os valores publicados e contra o framework (cancelavel) e que fechar a janela cancela o que estava rodando.

- Codigo: ItemPropertiesService, ItemPropertiesViewModel, PropertyRowViewModel, PropertySectionViewModel, PropertiesDialog
- Testes (59 casos): ItemPropertiesServiceTests [ItemPropertiesServiceTests.cs], ItemPropertiesViewModelTests [ItemPropertiesViewModelTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ItemPropertiesServiceTests|FullyQualifiedName~ItemPropertiesViewModelTests"`

### Menu superior: Exibir (unidades bloqueadas) e Preferencia

Estrutura do menu lida do XAML (sem o menu Arquivo, Ferramentas renomeado para Preferencia, itens novos ligados ao comando certo) e "Exibir unidades bloqueadas": ligado por padrao, lembrado entre execucoes, esconde so as unidades travadas (BitLocker ou sem acesso) e nunca as sem midia ou indisponiveis.

- Codigo: ExplorerViewOptions, NavigationTreeService
- Testes (23 casos): MainMenuTests [MainMenuTests.cs], LockedDrivesOptionTests [LockedDrivesOptionTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~MainMenuTests|FullyQualifiedName~LockedDrivesOptionTests"`

### Iniciar sempre como administrador

Marcar pede o administrador na hora e grava antes de reiniciar; recusar o aviso do Windows ou falhar desliga o check de volta; ao abrir com a opcao ligada o app pede o acesso (ou segue normalmente se recusar) e nunca pede de novo quando foi reaberto pelo proprio pedido. O aviso do Windows e trocado por uma elevacao de mentira.

- Codigo: AdministratorStartupService
- Testes (13 casos): AdministratorStartupServiceTests [AdministratorStartupServiceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~AdministratorStartupServiceTests"`

### Versao do programa

A versao fica num lugar so, o <Version> do csproj do app: o executavel informa essa mesma versao (a tela Sobre le a versao informacional) e o README a cita; mudar uma e esquecer a outra quebra o teste.

- Codigo: AboutDialog
- Testes (3 casos): AppVersionTests [AppVersionTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~AppVersionTests"`

### Acesso negado e reiniciar como administrador

Mensagens de falha em portugues (sem o prefixo \\?\ do .NET), falta de permissao criada de verdade com regra NTFS de negar (excluir, renomear, criar pasta, copiar, mover, compactar), quando perguntar se quer reiniciar como administrador, como o app reinicia e espera o processo antigo sair, e pasta que o Windows nao deixa listar (nao vira "vazia" no cache e a linha mostra um cadeado no lugar das contagens e a barra de status diz "sem permissao para abrir").

- Codigo: FileErrorText, ElevationPrompt, ElevationService, IElevationService, StartupArguments, TransferAccessDeniedException
- Testes (53 casos): FileErrorTextTests [FileErrorTextTests.cs], AccessDeniedOperationTests [AccessDeniedOperationTests.cs], ElevationTests [ElevationTests.cs], DeniedFolderRowTests [InterfaceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FileErrorTextTests|FullyQualifiedName~AccessDeniedOperationTests|FullyQualifiedName~ElevationTests|FullyQualifiedName~DeniedFolderRowTests"`

### Start.bat: fechar o app antes de compilar (stop-dora)

Pede para a janela fechar e espera (o app pode perguntar sobre uma operacao em andamento), so depois usa o taskkill; processo sem janela vai direto ao taskkill; processo que o terminal nao consegue encerrar (app aberto como administrador) e reconhecido antes, para avisar em vez de esperar. Os testes usam processos de mentira e so carregam as funcoes do script, nunca o app de verdade.

- Codigo: 
- Testes (4 casos): StopDoraScriptTests [StopDoraScriptTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~StopDoraScriptTests"`

### Arrastar e soltar: onde cai e o destaque da pasta de destino

A linha sob o mouse e a que vale (uma unidade aberta recebe na raiz, nao na primeira subpasta; espaco em branco cai na pasta aberta do painel), a pasta que recebe e destacada durante o arrasto e so uma por vez, e soltar na pasta de origem ou de um item dentro dele mesmo e recusado.

- Codigo: RowHitTest, FilePaneViewModel, NavigationTreeNodeViewModel
- Testes (21 casos): DropTargetTests [DropTargetTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~DropTargetTests"`

### Renomear (F2, clique devagar, nome sem a extensao)

Que trecho do nome ja vem selecionado (sem a extensao; pasta, .gitignore e nome sem extensao selecionam tudo), quando um clique devagar no nome comeca a renomear (so o item que ja era o unico selecionado, fora de unidade e Lixeira, sem outro em renomeacao) e confirmar ou cancelar; depois de confirmar o item com o novo nome fica como unico selecionado (e a tela e avisada para dar o foco a ele).

- Codigo: RenameSelection, SystemMouse, FilePaneViewModel
- Testes (27 casos): RenameTests [RenameTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~RenameTests"`

### Menu da Lixeira (esvaziar e restaurar tudo)

Esvaziar chama o Windows uma vez, Lixeira ja vazia nao e erro nem chama o Windows, recusa e excecao do Windows viram mensagem em portugues, a linha da Lixeira e atualizada nos dois paineis e as perguntas dizem quantos itens. O Windows e a consulta da Lixeira sao trocados por funcoes de mentira: nenhum teste esvazia a Lixeira de verdade.

- Codigo: RecycleBinService, RecycleBinText
- Testes (15 casos): RecycleBinMenuTests [RecycleBinMenuTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~RecycleBinMenuTests"`

### Localizar (colunas ajustaveis e nova janela)

O cabecalho das colunas tem a alca que o GridView procura (sem ela a largura nao muda), o menu pode ser aumentado e nao tem teto de altura, existe o botao Abrir em Janela e a janela mostra Nome, Caminho e Tamanho ajustaveis (lidos do XAML); a pesquisa acha item em subpastas fundas e a barra alimenta a lista que a janela mostra.

- Codigo: SearchDialog, SearchPopupSizing, FileSearchService, CenterToolbarViewModel
- Testes (16 casos): SearchWindowTests [SearchWindowTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~SearchWindowTests"`

### Duplicados (padrao: nome e tamanho; profundo: tamanho e hash)

Padrao agrupa por nome (sem diferenciar maiusculas) e tamanho sem ler arquivo; profundo confirma por SHA-256 (inclusive diferenca so no ultimo byte de um arquivo grande), so le arquivo que tem o tamanho de outro, conta o travado como ilegivel e segue; vazio, juncao e pasta inexistente; cancelar e andamento; a leitura enxuta correta em todos os tamanhos ao redor do bloco de leitura (64 KB e 1 MiB, inclusive diferenca so no ultimo byte), arquivo so da nuvem nao lido, varias leituras ao mesmo tempo com o mesmo resultado de uma so, a escolha do numero de leituras pelo tipo de disco (SSD x HD x desconhecido), as pastas do sistema de fora so no topo de uma unidade (e a opcao de inclui-las), o andamento por bytes da leitura inteira (um aviso por bloco lido, mais o inicial e o final, so quando ha arquivo grande), a ordem da lista (maiores primeiro, Nome A a Z e Z a A, tamanho crescente e decrescente, desempate fixo, reordenar sem procurar de novo, ordem guardada entre procuras), a barra e o texto de andamento de cada etapa, a lista plana da janela (cabecalho de grupo + copias), modo, subpastas, textos, o botao no grupo Pasta e o icone novo e unico da mesma fonte.

- Codigo: DuplicateFinderService, DuplicateWorkers, StorageKind, DuplicatesViewModel, DuplicateSorter, DuplicateText, DuplicatesDialog, DuplicateRowViewModel
- Testes (134 casos): DuplicateFinderTests [DuplicateFinderTests.cs], DuplicatesViewModelTests [DuplicatesViewModelTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~DuplicateFinderTests|FullyQualifiedName~DuplicatesViewModelTests"`

### Localizar e Duplicados: duplo clique leva ao arquivo

Em pasta grande ainda preenchida em lotes o arquivo existe como no e fica como unico selecionado (e a pasta do caminho tambem); terminar o preenchimento na hora e seguro contra lote atrasado e pasta recarregada; a selecao de arquivo vizinho continua limpa ao abrir outra pasta; a altura estimada da linha (compacta 32, confortavel 48, medida real quando existe, pasta fechada, arquivo sem rodape).

- Codigo: NavigationTreeService, TreeScrollHelper, FilePaneViewModel
- Testes (19 casos): SearchNavigationTests [SearchNavigationTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~SearchNavigationTests"`

### Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada

Tipo, Modificado e Tamanho com largura fixa e folgada (nao mudam nem quando o Nome e alargado); a largura da linha mais larga da lista INTEIRA (so as pastas abertas, o nivel vem da descida, so os 4 nomes mais compridos de cada pasta sao medidos, raiz fechada e pasta fechada nao contam, marcador de lugar e nome vazio sao ignorados) e a largura minima que cada linha da ao nome para todas terem a mesma largura natural (a barra horizontal nao muda de tamanho nem anda quando a lista rola); so o Nome se ajusta, maior ou menor que o automatico (a conta do arrasto, minimo de 100 px, parte da largura guardada ou da que a coluna tem na tela, volta ao automatico so com o duplo clique; a grade de cada linha comum fica com largura fixa e a esquerda para as colunas acompanharem a borda, as linhas de topo ficam de fora), guardada por painel, gravada so no fim do arrasto e lida ao abrir, painel antigo ou com as larguras da versao anterior le o padrao; em colunas nenhuma coluna some quando o painel estreita (nas linhas confortaveis a data continua sumindo); a linha da unidade fica parada na rolagem horizontal SEMPRE, com ou sem rolagem (a conta pura; ligar e desligar fazia a tela piscar); o touchpad de dois dedos e a roda inclinada (o valor com sinal do giro, a soma de passos pequenos, os limites); as barras de rolagem de 12 px e o canto sem o quadrado branco; quem entra nas colunas (so pasta, arquivo e arquivo compactado: as raizes e os protocolos da Rede nao mostram as colunas, nao contam para o maior nome e nao recebem largura minima nem grade fixa), o cabecalho vazio enquanto so ha raizes abertas (e de volta ao abrir uma pasta), o cabecalho do modo padrao com Nome, Modificado e Tamanho travados e alinhados com o rodape da linha, a ordenacao do maior para o menor nos dois modos, as bordas de arrasto so no Nome, o cabecalho que anda num Canvas recortado, a barra horizontal da lista e a caixa do aplicativo padrao dentro da linha confortavel (lidos do XAML).

- Codigo: PaneColumnWidths, PaneDisplayOptions, StickyToViewport, NameWidthScanner, NameTextMeasurer, NameCellMinWidthConverter, NameRowContentConverter, NavigationTreeNodeViewModel, TreeScrollHelper, FilePaneViewModel
- Testes (100 casos): PaneColumnsTests [PaneColumnsTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~PaneColumnsTests"`

### Registro de servicos (injecao de dependencia)

Todo servico registrado tem as dependencias registradas; um servico novo esquecido estouraria so ao abrir o app.

- Codigo: ServiceRegistration
- Testes (10 casos): ServiceRegistrationTests [ServiceRegistrationTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ServiceRegistrationTests"`

### Falhas esperadas do sistema

Volume bloqueado (BitLocker) e erros de E/S que o app trata sem registrar como erro.

- Codigo: ExpectedFailures
- Testes (2 casos): ExpectedFailuresTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ExpectedFailuresTests"`

## Indice por tipo (mudou este tipo? rode esta area)

| Tipo | Area |
|------|------|
| AboutDialog | Versao do programa |
| AdministratorStartupService | Iniciar sempre como administrador |
| AppSettingsService | Seguranca de senhas e configuracoes |
| ArchiveFormat | Arquivos compactados (abrir como pasta) |
| ArchivePathHelper | Arquivos compactados (abrir como pasta) |
| ArchiveService | Arquivos compactados (abrir como pasta) |
| BreadcrumbSegmentViewModel | Painel de arquivos (um lado da janela) |
| CenterToolbarViewModel | Localizar (colunas ajustaveis e nova janela) |
| DefaultApplicationOverrides | Abrir com e programas padrao |
| DirectoryIndexService | Cache de pastas e carga sem piscar |
| DragPolicy | Janela principal (copiar e colar entre os paineis) |
| DuplicateFinderService | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicateRowViewModel | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicatesDialog | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicateSorter | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicatesViewModel | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicateText | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| DuplicateWorkers | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| ElevationPrompt | Acesso negado e reiniciar como administrador |
| ElevationService | Acesso negado e reiniciar como administrador |
| ExpectedFailures | Falhas esperadas do sistema |
| ExplorerThemeResources | Tema, cores e interface |
| ExplorerViewOptions | Menu superior: Exibir (unidades bloqueadas) e Preferencia |
| ExplorerViewSettingsService | Tema, cores e interface |
| FavoritesService | Lixeira e Favoritos |
| FileAssociationService | Abrir com e programas padrao |
| FileErrorText | Acesso negado e reiniciar como administrador |
| FileOpenService | Abrir com e programas padrao |
| FileOperationService | Copiar, mover e compactar (progresso, cancelamento, tempo limite); Painel de arquivos (um lado da janela) |
| FilePaneViewModel | Painel de arquivos (um lado da janela); Arrastar e soltar: onde cai e o destaque da pasta de destino; Renomear (F2, clique devagar, nome sem a extensao); Localizar e Duplicados: duplo clique leva ao arquivo; Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| FileSearchService | Localizar (colunas ajustaveis e nova janela) |
| FileThumbnailService | Previa de imagens e PDF |
| FtpCertificateVerifier | Confianca em servidores (chaves, certificados, login do cliente) |
| FtpServer | Servidor FTP e FTPS |
| FtpTlsCertificate | Confianca em servidores (chaves, certificados, login do cliente) |
| HostKeyVerifier | Confianca em servidores (chaves, certificados, login do cliente) |
| IElevationService | Acesso negado e reiniciar como administrador |
| ItemPropertiesService | Janela Propriedades (botao direito) |
| ItemPropertiesViewModel | Janela Propriedades (botao direito) |
| KnownHostsStore | Confianca em servidores (chaves, certificados, login do cliente) |
| LoginThrottle | Servidor SFTP e SCP |
| MainWindowViewModel | Janela principal (copiar e colar entre os paineis) |
| MemoryTrimmer | Cache de pastas e carga sem piscar |
| NameCellMinWidthConverter | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| NameRowContentConverter | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| NameTextMeasurer | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| NameWidthScanner | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| NativeFileCopy | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| NavigationHistory | Navegacao e caminhos |
| NavigationNodeKind | Pasta Rede na arvore |
| NavigationTreeNodeViewModel | Painel de arquivos (um lado da janela); Arrastar e soltar: onde cai e o destaque da pasta de destino; Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| NavigationTreeService | Pasta Rede na arvore; Arquivos compactados (abrir como pasta); Menu superior: Exibir (unidades bloqueadas) e Preferencia; Localizar e Duplicados: duplo clique leva ao arquivo |
| NetworkClientHealthService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| NetworkFileService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| NetworkPortGuard | Travas dos servidores locais (pasta raiz e portas) |
| NetworkServerService | Travas dos servidores locais (pasta raiz e portas) |
| OperationTimeouts | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| OperationTracker | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| PaneColumnWidths | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| PaneDisplayOptions | Pasta Rede na arvore; Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| PaneDisplaySettingsService | Janela principal (copiar e colar entre os paineis) |
| PathNavigation | Navegacao e caminhos |
| PathSafetyService | Navegacao e caminhos |
| PropertiesDialog | Janela Propriedades (botao direito) |
| PropertyRowViewModel | Janela Propriedades (botao direito) |
| PropertySectionViewModel | Janela Propriedades (botao direito) |
| ProtectedStringJsonConverter | Seguranca de senhas e configuracoes |
| RangeObservableCollection | Cache de pastas e carga sem piscar |
| RecycleBinService | Lixeira e Favoritos; Menu da Lixeira (esvaziar e restaurar tudo) |
| RecycleBinText | Menu da Lixeira (esvaziar e restaurar tudo) |
| RememberPathSettingsService | Janela principal (copiar e colar entre os paineis) |
| RemoteFileService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| RemoteTransferEngine | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| RenameSelection | Renomear (F2, clique devagar, nome sem a extensao) |
| RowHitTest | Arrastar e soltar: onde cai e o destaque da pasta de destino |
| ScpServerSession | Servidor SFTP e SCP |
| SearchDialog | Localizar (colunas ajustaveis e nova janela) |
| SearchPopupSizing | Localizar (colunas ajustaveis e nova janela) |
| SelectionStatusFormatter | Tema, cores e interface |
| ServerVirtualPath | Servidor SFTP e SCP |
| ServiceRegistration | Registro de servicos (injecao de dependencia) |
| SftpServerSession | Servidor SFTP e SCP |
| ShellIconService | Icones de arquivos |
| SortHeaderLogic | Tema, cores e interface |
| SshFileServer | Servidor SFTP e SCP |
| SshHostKeyStore | Servidor SFTP e SCP |
| StartupArguments | Acesso negado e reiniciar como administrador |
| StickyToViewport | Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |
| StorageKind | Duplicados (padrao: nome e tamanho; profundo: tamanho e hash) |
| StorageUsage | Tema, cores e interface |
| SystemMouse | Renomear (F2, clique devagar, nome sem a extensao) |
| TftpClient | TFTP (servidor e cliente) |
| TftpServer | TFTP (servidor e cliente) |
| ThemeManager | Tema, cores e interface |
| ThumbnailHoverPreview | Previa de imagens e PDF |
| TransferAccessDeniedException | Acesso negado e reiniciar como administrador |
| TransferOutcome | Janela principal (copiar e colar entre os paineis) |
| TransferProgressFormatter | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| TransferScanner | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| TreeScrollHelper | Localizar e Duplicados: duplo clique leva ao arquivo; Visao em colunas: Nome alargavel, barra do maior nome, touchpad, barras mais grossas e linha da unidade parada |

## Conferido so na tela ou sem teste automatico

Itens que nenhum teste automatico protege. Ao mexer neles, confira na tela e diga no relato o que foi e o que nao foi verificado.

| Item | Por que | Como conferir |
|------|---------|---------------|
| Atalhos de teclado (tabela do README) | Dependem do estado real do teclado no WPF. | Teclas reais com keybd_event (AGENTS.md, armadilha 12). |
| Visual do redesenho (cores, linhas de hierarquia, barras, icones, truncamento de nomes) | Aparencia; os testes so conferem os recursos XAML e as cores. | scripts\dev\ui-capture.ps1 e captura de tela. |
| Previa ao passar o mouse (a dica na tela) | So a regra de quando vale e testada; a dica depende da janela. | Passar o mouse sobre uma imagem com o app aberto. |
| Seta e contagem de pastas dentro de arquivos compactados, na tela | Testado na arvore (ArchiveListingTests); o desenho da seta depende do WPF. | Roteiro de UI Automation: ExpandCollapseState de cada filho do arquivo. |
| Copiar de dentro de um .7z, .rar ou .gz clicando no app | A extracao e testada no ArchiveService, mas nao pela interface. | Selecionar o arquivo dentro do arquivo compactado e copiar para a outra tela. |
| Barra central (CenterToolbarViewModel) e controles dos paineis (FilePaneControl) | Dependem do WPF e de janelas; os paineis e a janela principal sao testados sem janela, a barra central e os controles nao. | Rodar o app e conferir o log sem [ERR] nem [WRN]. |
| Janela de progresso aparecendo quando a operacao passa de 600 ms | Os testes copiam arquivos pequenos, que terminam antes; a janela nunca e criada neles. | Copiar uma pasta grande no app e ver a janela, o cancelar e o fechamento. |
| Excluir para a Lixeira do Windows (modo RecycleBin) | Mexe na lixeira real do usuario; os testes usam exclusao permanente em pastas temporarias. | Excluir um arquivo de teste no app e achar na Lixeira. |
| Controles e animacoes da arvore (rolagem suave, linhas de hierarquia, revelar ao expandir) | Aparencia e tempo; sem teste automatico. | Rodar o app e navegar. |
| Abrir pasta sem piscar (a lista cresce enquanto os filhos entram em cascata; TreeExpansionRevealAnimator) | Depende do WPF, do tempo e da tela: sem janela nao ha como testar. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario blink: cada pasta precisa sair ok (maior queda em UM quadro abaixo de 20% do conteudo). Antes da correcao a pasta grande dava 76% (PISCA). |
| Reiniciar como administrador de verdade (aviso do Windows/UAC) e as janelas "Reiniciar como administrador / Agora nao" | O aviso do Windows roda em outra area de trabalho e pede uma pessoa; o app so e testado ate chamar o Windows (ElevationTests) e a regra de quando perguntar (ElevationPrompt). | Tentar excluir um item de C:\Windows ou C:\Program Files: a janela deve falar de acesso negado, em portugues, e perguntar. Escolher Reiniciar, confirmar o aviso do Windows e ver o app abrir de novo (Gerenciador de Tarefas: Elevado = Sim); repetir a operacao. |
| Aviso ao abrir uma pasta que o Windows nao deixa listar (duplo clique, Enter ou seta para a direita) | O teste de unidade cobre o no marcado (ListingDenied) e a mensagem, nao o gesto no TreeView real: um clique so seleciona a linha, e o cadeado so e conferido no app real (os testes cobrem a marca no no e a troca das contagens, nao o desenho do XAML). | Criar uma pasta com um arquivo e negar a leitura dela: icacls <pasta> /deny "%USERNAME%:(RD)". Abrir o app: a linha da pasta mostra um cadeado no lugar de 0 pastas / 0 arquivos (passe o mouse: "Sem permissao para abrir esta pasta"), e ao selecionar a pasta a barra de status diz "sem permissao para abrir", nas linhas compactas e nas confortaveis (Exibir > Linhas compactas em colunas). Dar duplo clique na pasta: aparece "Sem permissao para abrir ..." com Reiniciar como administrador / Agora nao. Abrir a mesma pasta de novo (seta para a direita) nao repete o aviso. Remover a regra: icacls <pasta> /remove:d %USERNAME%. |
| Foco e setas depois de apagar pelo teclado (Shift+Delete) | O vizinho a selecionar e testado (FilePaneViewModelTests), mas o foco do WPF so existe com a janela aberta. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario delfocus (app fechado; AGENTS.md, armadilha 18): cada verificacao sai [OK] ou [FALHOU]; o foco deve ir para o vizinho e Baixo/Cima mover o foco sem clicar. |
| Ctrl+clique e Shift+clique para selecionar varios itens | A regra da selecao (ligar, desligar, intervalo, outra pasta) e testada no modelo, mas o clique com o modificador segurado so existe com a janela aberta. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario multisel (app fechado; AGENTS.md, armadilha 19): Shift+clique (intervalo) e Ctrl+clique (itens soltos), lendo "N itens selecionados" na barra de status; cada verificacao sai [OK] ou [FALHOU]. |
| Arrastar e soltar entre os paineis, para o Windows Explorer e para navegadores (e do Explorer para o app) | O destino do soltar e o que sai da janela sao testados no modelo, mas o arrasto de verdade depende do mouse, do OLE e das janelas de outros programas. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario dragdrop (app fechado; AGENTS.md, armadilha 20): mouse de verdade, janela real do Explorer, Chrome, Edge e Firefox com perfil temporario; cada verificacao sai [OK] ou [FALHOU]. |
| Menu de contexto (botao direito): Adicionar e Remover dos favoritos em arquivo, pasta e favorito da pasta Favoritos | O texto da entrada e testado no modelo, mas o menu so existe com a janela aberta e o clique direito. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario favmenu (app fechado; AGENTS.md, armadilha 21): botao direito de verdade, le as entradas do menu pelo UI Automation e confere o favorites.json; cada verificacao sai [OK] ou [FALHOU]. |
| Janela Propriedades: abrir pelo botao direito, o visual nos temas escuro e claro e os valores contra o disco | Os dados e o texto sao testados sem janela, mas a janela, o menu e o visual so existem com o app aberto. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario props (app fechado; AGENTS.md, armadilha 22): botao direito de verdade num arquivo, numa pasta, em dois itens e numa unidade, le as linhas pelo UI Automation, confere o SHA-256 contra o Get-FileHash e fotografa nos dois temas; cada verificacao sai [OK] ou [FALHOU]. |
| Menu superior na tela (Exibir, Temas, Preferencia, Ajuda) e o aviso do Windows de "Iniciar sempre como administrador" | A estrutura do menu e as ligacoes sao testadas pelo XAML, mas o menu aberto so existe com a janela; o aviso do Windows (UAC) roda numa area protegida e nenhum roteiro consegue aceita-lo ou recusa-lo. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario menu (app fechado; AGENTS.md, armadilha 23) confere os menus, o estado dos checks e liga e desliga "Exibir unidades bloqueadas" de verdade (uma unidade travada some e volta nos dois paineis), sem nunca acionar o administrador. A mao: o menu superior mostra Exibir, Temas, Preferencia e Ajuda (sem Arquivo), com "Exibir unidades bloqueadas" em Exibir e "Iniciar sempre como administrador" em Preferencia. Marcar o administrador: o Windows pede a confirmacao; recusar deixa o check desmarcado, aceitar reabre o app (Gerenciador de Tarefas: Elevado = Sim) e, fechando e abrindo de novo, o aviso aparece ao abrir. Desmarcar nao pede nada. |
| Selecionar com o retangulo (arrastar na borda esquerda) e arrastar a selecao, o destaque da pasta de destino durante o arrasto e soltar na raiz da unidade | A regra da linha sob o mouse (RowHitTest) e o destaque no modelo sao testados, mas o mouse apertado, o OLE e o desenho do destaque so existem com a janela aberta. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario marqueedrag (app fechado; AGENTS.md, armadilhas 25 e 26): retangulo de 4 itens, arrasto da selecao para o outro painel, o painel de origem continua na mesma pasta, fotos do destaque (dest e a linha C:) e a pasta solta na raiz de C:; cada verificacao sai [OK] ou [FALHOU]. |
| Renomear na tela: F2 e clique devagar abrem a caixa com o foco e o nome sem a extensao selecionado, selecionar o texto com o mouse | O trecho selecionado e a regra do clique devagar sao testados, mas o foco, o mouse e o temporizador so existem com a janela aberta. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario rename (app fechado; AGENTS.md, armadilha 27): F2 e clique devagar (le a selecao pelo UI Automation), arrasto do mouse dentro da caixa (o arquivo nao pode sair do lugar), Enter renomeia no disco e deixa o item com o novo nome selecionado e com o foco (a pasta que o contem nao), e Esc cancela. |
| Menu da Lixeira na tela, Localizar (Abrir em Janela e coluna ajustada com o mouse) e a janela Itens duplicados aberta pelo botao do grupo Pasta | As regras sao testadas sem janela, mas o menu, o botao, as janelas e o arrasto da borda do cabecalho so existem com o app aberto. | powershell -NoProfile -File scripts\dev\ui-capture.ps1 -Scenario newfeatures (app fechado; AGENTS.md, armadilhas 28 a 30): botao direito na Lixeira (so le o menu e recusa a pergunta de esvaziar), Localizar e Abrir em Janela, largura da coluna Nome antes e depois de arrastar a borda, Duplicados nos tipos Padrao e Profundo; cada verificacao sai [OK] ou [FALHOU]. |
| Esvaziar a Lixeira de verdade (SHEmptyRecycleBin) | Apaga para sempre a Lixeira do usuario; os testes trocam a chamada do Windows por uma de mentira e os roteiros de tela recusam a pergunta. | Numa maquina de teste, jogar arquivos de teste na Lixeira, botao direito na Lixeira > Esvaziar Lixeira > Sim e conferir que a Lixeira ficou vazia nos dois paineis. |
| Janelas de dialogo (progresso, conexao de rede, ordenar, excluir) | Janelas WPF; sem teste automatico. | Abrir cada uma no app. |
| Restaurar e excluir de verdade da Lixeira | Mexe na lixeira real do usuario; so as travas sao testadas. | Com arquivos de teste, na tela. |
| Start.bat e scripts de apoio | O fechamento do app esta em teste automatico (StopDoraScriptTests, com processos de mentira); o Start.bat inteiro (compilar e abrir) e o caso de um app aberto como administrador, que pede o UAC, nao. | Rodar Start.bat e conferir que o app abre. Com o app aberto, rodar de novo: ele fecha sozinho (o log termina com "encerrado") e reabre. Com o app aberto como administrador (Reiniciar como administrador), o Start.bat deve parar com o aviso de que nao consegue fecha-lo, sem compilar. |

## Apendice: casos por classe

### AccessDeniedOperationTests (AccessDeniedOperationTests.cs)

- Compressing_into_a_folder_that_refuses_new_files_is_denied_in_portuguese
- Copying_into_a_folder_that_refuses_new_files_is_denied_in_portuguese
- Creating_a_folder_where_it_is_not_allowed_is_denied_in_portuguese
- Deleting_a_folder_that_refuses_to_be_removed_is_denied_in_portuguese_and_offers_administrator
- Deleting_a_folder_with_a_protected_file_inside_names_the_folder_and_a_path_inside_it
- Deleting_a_protected_file_permanently_is_denied_in_portuguese
- Moving_a_protected_file_is_denied_in_portuguese_and_keeps_it_in_place
- Renaming_a_protected_file_is_denied_in_portuguese

### AdministratorStartupServiceTests (AdministratorStartupServiceTests.cs)

- A_failure_to_restart_leaves_the_option_off_and_reports_why
- An_app_that_was_just_reopened_by_the_elevation_never_asks_again_so_there_is_no_loop
- At_startup_refusing_the_windows_confirmation_opens_the_app_normally_and_keeps_the_option
- At_startup_with_the_option_off_nothing_is_asked
- At_startup_with_the_option_on_and_already_administrator_nothing_is_asked
- At_startup_with_the_option_on_and_no_administrator_access_it_asks_for_it_and_stops_this_start
- It_is_off_by_default_and_a_settings_file_from_before_the_option_keeps_it_off
- On_the_next_run_the_saved_choice_is_loaded
- Refusing_the_windows_confirmation_leaves_the_option_off_so_the_check_matches_reality
- The_option_is_saved_before_the_restart_because_the_old_process_closes_right_after
- Turning_it_off_is_remembered_and_never_asks_for_anything
- Turning_it_on_asks_for_administrator_access_right_away_and_remembers_the_choice
- Turning_it_on_when_already_administrator_only_saves_and_does_not_restart

### ApplicationNameTests (InterfaceTests.cs)

- A_friendly_shell_name_is_kept_when_the_description_is_missing_the_same_or_already_listed
- A_raw_executable_name_from_the_shell_is_replaced_by_the_description_of_the_program
- Application_names_never_show_the_executable_extension (8 casos)
- Packaged_app_ids_are_told_apart_from_programs_and_paths (7 casos)

### AppVersionTests (AppVersionTests.cs)

- The_built_app_reports_the_version_declared_in_the_project_file
- The_project_file_declares_the_version_in_the_major_dot_minor_format
- The_readme_states_the_current_version

### ArchiveExtractionPathTests (SecurityTests.cs)

- Accepts_paths_that_stay_inside_destination (4 casos)
- Rejects_paths_that_escape_destination (10 casos)

### ArchiveListingTests (ArchiveListingTests.cs)

- A_gz_shorter_than_its_own_header_does_not_crash_the_listing
- A_gz_that_is_not_gzip_is_reported_instead_of_listed
- Archive_node_gives_its_folders_the_expander_arrow_and_the_right_counts
- Backslash_names_are_counted_like_slash_names
- Entries_cannot_be_removed_from_read_only_archives (2 casos)
- Files_have_no_folder_counts_and_the_counts_follow_the_level_being_listed
- Folders_of_a_7z_node_get_the_arrow_and_gz_and_7z_files_become_archive_nodes
- Gz_and_7z_are_read_only_like_rar
- Gzip_entry_name_drops_only_the_last_extension (3 casos)
- Gzip_extraction_does_not_overwrite_an_existing_file
- Gzip_file_can_be_extracted_with_its_original_content
- Gzip_opens_as_a_folder_with_the_one_file_inside
- SevenZip_files_and_folders_can_be_extracted
- SevenZip_folders_report_what_they_hold_directly
- SevenZip_inner_folder_can_be_listed
- Tar_folders_report_what_they_hold_directly
- Tar_gz_made_by_the_app_can_be_listed
- Tar_gz_with_files_can_be_listed_with_sizes_and_counts
- The_format_comes_from_the_name_and_tar_gz_is_not_a_plain_gz (8 casos)
- Zip_folders_report_what_they_hold_directly
- Zip_with_repeated_names_still_lists

### ArchiveServiceExtractionTests (SecurityTests.cs)

- Malicious_zip_folder_is_refused_and_writes_nothing_outside_the_destination
- Regular_zip_folder_is_still_extracted

### CompressionProgressTests (TransferTests.cs)

- A_junction_pointing_back_at_its_own_folder_does_not_make_compression_loop_forever
- Cancelling_in_the_middle_of_a_big_file_leaves_no_partial_archive (2 casos)
- Compress_title_names_count_format_and_destination
- Files_older_than_1980_do_not_break_zip_creation
- TarGz_keeps_content_and_folders
- Zip_keeps_content_folders_and_dates_and_reports_progress_to_the_end

### DefaultApplicationOverridesTests (InterfaceTests.cs)

- A_chosen_application_that_no_longer_exists_is_ignored_in_favor_of_the_windows_default
- Choosing_and_clearing_an_application_is_saved_and_announced
- Extensions_are_normalized_so_upper_and_lower_case_are_the_same (6 casos)
- Old_settings_without_chosen_applications_load_as_empty
- Only_programs_the_system_can_run_are_accepted_as_applications
- Saved_choices_are_loaded_back_with_the_settings_service

### DeniedFolderRowTests (InterfaceTests.cs)

- A_folder_the_user_may_not_list_swaps_its_counts_for_a_lock_on_the_row

### DirectoryIndexTests (DirectoryIndexTests.cs)

- A_folder_that_was_never_read_is_not_in_the_index
- A_folder_the_user_cannot_list_is_reported_and_is_never_stored_as_empty
- A_large_folder_is_stored_and_read_back_completely
- A_listing_stored_empty_by_an_older_session_is_forgotten_on_startup
- A_read_made_after_invalidating_is_stored_as_usual
- A_write_still_waiting_in_the_queue_cannot_bring_back_a_folder_that_was_invalidated
- An_empty_or_blank_path_never_reaches_the_database
- Computing_counts_from_the_disk_returns_them_and_indexes_the_folder
- Counting_a_folder_the_user_cannot_list_does_not_store_it_either
- Counts_and_listing_are_served_from_the_index_even_after_the_disk_changes
- Folder_paths_match_regardless_of_case_and_trailing_separator
- Hidden_and_system_files_follow_the_view_options
- Invalidate_forgets_the_folder
- Maintenance_purges_folders_not_indexed_for_more_than_ninety_days_and_keeps_recent_ones
- Reading_a_folder_stores_it_and_the_cached_listing_matches_the_disk
- The_database_is_created_in_wal_mode_with_the_expected_schema_version
- The_index_is_current_until_the_folder_changes_and_becomes_current_again_after_a_new_read
- The_index_survives_closing_and_reopening_the_database
- The_search_listing_has_names_kinds_and_sizes
- When_the_database_cannot_be_created_the_index_is_disabled_but_reading_still_works

### DragPolicyTests (DragPolicyTests.cs)

- Dragging_between_the_two_panes_of_the_app_still_allows_copy_and_move
- Dragging_to_another_program_only_allows_copying
- The_cursor_check_runs_without_a_window_and_never_finds_the_test_process_under_it

### DropTargetTests (DropTargetTests.cs)

- A_drive_row_receives_the_items_at_the_root_of_the_drive
- A_file_is_never_highlighted_as_the_target
- A_file_row_sends_the_items_to_the_folder_that_holds_the_file
- A_folder_row_receives_the_items_inside_that_folder
- A_folder_that_does_not_exist_is_refused_with_a_reason
- An_empty_tree_has_no_row
- Blank_space_above_or_below_the_rows_is_no_row_at_all (3 casos)
- Blank_space_sends_the_items_to_the_folder_open_in_the_pane
- Clearing_the_target_removes_the_highlight_and_a_folder_the_pane_does_not_show_highlights_nothing
- Dropping_a_folder_on_the_root_of_its_own_drive_moves_it_and_never_into_itself
- Dropping_an_item_that_already_sits_at_the_root_on_that_root_is_refused
- Highlighting_the_same_folder_twice_changes_nothing
- The_folder_that_would_receive_the_items_is_highlighted_and_only_that_one
- The_line_of_an_open_drive_is_the_drive_and_not_its_nearest_folder
- The_row_under_the_mouse_is_the_one_whose_own_band_holds_the_height (5 casos)

### DuplicateFinderTests (DuplicateFinderTests.cs)

- A_big_file_reports_while_it_is_being_read_and_not_only_when_it_ends
- A_blank_folder_gives_an_empty_result
- A_cancelled_search_stops_with_an_operation_cancelled_exception
- A_file_that_only_exists_in_the_cloud_is_not_read_by_the_deep_search_but_still_seen_by_the_standard_one
- A_folder_called_windows_is_searched_normally_when_the_search_does_not_start_at_a_drive_root
- A_folder_that_does_not_exist_gives_an_empty_result_instead_of_failing
- A_junction_to_another_folder_is_not_followed_so_nothing_is_counted_twice
- An_ssd_gets_one_read_per_processor_up_to_sixteen_and_a_hard_disk_or_an_unknown_disk_keeps_four (8 casos)
- Asking_the_disk_kind_never_throws_and_gives_up_on_paths_it_cannot_ask_about
- At_a_drive_root_the_system_folders_are_left_out_and_named_in_the_result
- Deep_catches_a_difference_in_the_first_byte_without_reading_the_whole_file
- Deep_catches_a_difference_in_the_very_last_byte_of_a_large_file
- Deep_confirms_a_large_file_that_is_really_identical
- Deep_confirms_identical_files_of_every_size_around_the_read_block_boundaries (8 casos)
- Deep_counts_a_locked_candidate_as_unreadable_and_still_finds_the_other_duplicates
- Deep_does_not_group_files_of_different_sizes_even_if_one_is_the_start_of_the_other
- Deep_finds_the_same_content_under_different_names_and_reports_the_sha256
- Deep_handles_many_candidates_in_one_group_and_several_groups_at_once
- Deep_never_reads_a_file_whose_size_nobody_else_has
- Deep_separates_files_that_have_the_same_name_and_size_but_different_content
- Deep_tells_apart_files_that_differ_only_in_the_last_byte_at_every_size (5 casos)
- Empty_files_are_never_duplicates_of_each_other
- Files_that_differ_after_the_start_are_still_told_apart_by_the_full_read
- Including_the_system_folders_brings_everything_back_and_reports_nothing_skipped
- Many_reads_at_once_give_the_same_groups_as_one_at_a_time
- Only_the_root_of_a_drive_or_of_a_network_share_counts_as_a_drive_root (8 casos)
- Small_files_never_reach_the_full_read_stage
- Standard_does_not_tell_capitals_apart_in_the_name
- Standard_groups_files_with_the_same_name_and_size_in_different_folders
- Standard_ignores_the_same_name_with_a_different_size
- Standard_ignores_the_same_size_under_a_different_name
- Standard_reports_three_copies_as_one_group_and_the_wasted_space_of_two
- The_async_search_runs_off_the_calling_thread_and_honours_cancellation
- The_default_interval_keeps_the_screen_from_being_flooded
- The_full_read_reports_bytes_that_grow_to_the_total_and_end_at_one_hundred_percent
- The_groups_that_waste_the_most_come_first
- The_listing_carries_the_attributes_so_no_second_lookup_is_needed
- The_modified_date_and_folder_of_each_copy_are_reported_for_the_list
- The_search_reports_listing_and_then_reading_progress_ending_with_everything_read
- The_system_folders_are_left_out_only_at_a_drive_root_and_only_when_not_asked_for (6 casos)
- The_system_folders_at_the_top_of_a_drive_are_recognised_by_name_or_by_the_system_attribute (10 casos)
- Without_subfolders_nothing_is_reported_as_skipped_because_nothing_would_have_been_entered
- Without_subfolders_only_the_chosen_folder_is_searched

### DuplicatesViewModelTests (DuplicatesViewModelTests.cs)

- A_failure_reading_the_folder_becomes_a_message_and_not_a_crash
- A_folder_without_duplicates_says_so
- A_new_search_replaces_the_old_rows
- A_search_cannot_start_without_a_folder
- A_search_lists_each_group_with_a_header_followed_by_its_copies
- By_name_it_is_alphabetical_without_telling_capitals_apart
- By_size_ascending_starts_from_the_smallest
- By_size_the_biggest_copies_come_first_and_a_tie_goes_to_the_most_space_to_recover
- Cancelling_stops_the_search_and_leaves_the_list_empty
- Cancelling_with_nothing_running_does_nothing
- Choosing_a_mode_flips_the_other_option_and_the_description
- Clicking_the_name_column_sorts_from_a_to_z_and_a_second_click_goes_from_z_to_a
- Clicking_the_size_column_again_flips_between_biggest_first_and_smallest_first
- Going_back_to_size_from_name_starts_with_the_biggest_again
- Groups_with_the_same_name_are_ordered_by_size_and_then_by_path_so_the_order_never_changes_between_searches
- Sizes_are_written_like_everywhere_else_in_the_app (4 casos)
- Sorting_before_any_search_only_remembers_the_choice
- Sorting_reorders_what_is_on_screen_without_searching_again
- Starting_without_a_folder_asks_for_one_and_searches_nothing
- The_bar_follows_the_files_in_stage_one_and_the_bytes_in_stage_two_and_wanders_when_there_is_no_way_to_know (7 casos)
- The_bar_is_full_and_stops_wandering_when_the_search_ends
- The_chosen_order_survives_a_new_search
- The_deep_mode_shows_the_start_of_the_hash_in_the_group_header
- The_Duplicates_icon_comes_from_the_same_icon_font_and_no_other_button_uses_the_same_glyph
- The_final_status_mentions_files_that_could_not_be_read
- The_final_status_stays_short_when_everything_was_read
- The_Folder_group_has_the_Duplicates_button_with_the_two_checks_in_a_menu
- The_full_read_stage_shows_the_bytes_and_the_percentage_so_it_never_looks_stuck
- The_full_read_stage_without_a_known_size_still_shows_the_file_counts
- The_menu_tags_are_the_names_of_the_two_modes
- The_model_notifies_when_the_mode_changes_so_both_radio_buttons_update
- The_progress_text_names_the_phase_and_the_counts (2 casos)
- The_result_comes_with_the_biggest_files_first_by_default
- The_status_names_the_system_folders_that_were_left_out_and_how_to_include_them
- The_status_says_nothing_about_system_folders_when_none_were_skipped
- The_subfolder_option_is_passed_to_the_search
- The_system_folders_option_starts_off_and_is_passed_to_the_search
- The_toolbar_prepares_the_window_for_the_folder_open_in_the_active_pane
- The_window_has_both_checks_the_subfolder_option_and_adjustable_columns
- Unchecking_a_radio_button_does_not_change_the_mode (2 casos)
- Without_a_local_folder_the_toolbar_refuses_and_says_why

### ElevationTests (ElevationTests.cs)

- A_failure_to_launch_keeps_the_app_open_and_explains_in_portuguese
- A_plain_message_never_asks_for_elevation
- An_access_denied_message_offers_elevation_only_when_not_elevated
- An_app_that_is_already_elevated_does_not_restart_again
- Declining_the_windows_confirmation_keeps_the_app_open_and_is_not_an_error
- Restarting_launches_the_same_program_as_administrator_and_then_closes_this_one
- The_parent_process_id_is_read_from_the_arguments (6 casos)
- The_question_keeps_the_message_hides_the_marker_and_says_what_will_happen
- The_real_service_reports_the_real_windows_token
- Waiting_for_a_process_that_no_longer_exists_returns_at_once
- Waiting_gives_up_when_the_process_keeps_running
- Waiting_returns_when_the_process_exits
- When_already_elevated_the_text_explains_instead_of_asking

### ExpectedFailuresTests (InfrastructureTests.cs)

- A_bitlocker_locked_volume_is_recognized_by_its_hresult
- Other_io_failures_are_not_mistaken_for_a_locked_volume

### FavoritesServiceTests (FavoritesServiceTests.cs)

- A_corrupt_file_loads_as_empty_and_is_replaced_by_the_next_save
- A_favorite_that_disappeared_stays_listed_instead_of_vanishing
- A_new_store_has_no_favorites
- Adding_the_same_path_again_keeps_a_single_entry
- An_added_favorite_is_found_ignoring_case_and_survives_a_restart
- Blank_and_repeated_entries_in_the_file_are_ignored_on_load
- Entries_tell_folders_from_files_and_carry_name_size_and_date
- Removing_a_favorite_forgets_it_for_good
- Removing_something_that_is_not_a_favorite_is_harmless
- Switching_the_store_never_leaks_favorites_between_files
- The_storage_folder_is_created_when_saving
- Toggling_adds_everything_unless_everything_is_already_a_favorite
- Toggling_repeated_paths_counts_each_one_once

### FileErrorTextTests (FileErrorTextTests.cs)

- A_missing_item_is_explained
- A_path_that_is_too_long_is_explained
- A_plain_IO_exception_about_something_else_is_not_taken_for_access_denied
- A_plain_IO_exception_that_says_the_path_is_denied_is_access_denied (2 casos)
- A_read_only_file_is_explained_and_is_not_offered_elevation
- Access_denied_names_the_inner_path_in_portuguese_and_is_marked
- Access_denied_texts_built_without_an_exception_are_marked_too
- Anything_else_keeps_the_original_text_after_a_portuguese_lead_in
- Common_io_failures_are_explained_in_portuguese_and_are_not_access_denied (5 casos)
- Shell_and_win32_codes_that_mean_access_denied_are_recognized (5 casos)
- The_long_path_prefix_is_not_shown (4 casos)
- The_marker_survives_when_several_failures_are_joined_and_is_removed_for_display
- The_same_text_comes_out_when_the_runtime_message_is_already_in_portuguese
- Without_a_path_in_the_message_the_item_itself_is_named

### FileOpenServiceTests (InfrastructureTests.cs)

- The_open_with_window_refuses_a_missing_file_instead_of_showing_anything
- The_open_with_window_refuses_an_empty_path

### FileOperationProgressTests (TransferTests.cs)

- A_cancelled_copy_reports_cancellation_and_leaves_nothing_behind
- Aggregator_adds_finished_items_to_the_current_one
- Copying_a_folder_reports_totals_files_and_ends_complete
- Moving_a_file_reports_progress_and_the_source_disappears
- Scanner_totals_the_tree_and_does_not_follow_junctions
- Tracker_throttles_reports_but_always_delivers_the_final_one

### FileOperationTests (FileOperationTests.cs)

- A_name_that_differs_only_by_letter_case_counts_as_existing
- Cancelled_copy_never_leaves_a_partial_folder_behind
- Compress_async_does_not_fail_with_a_disposed_cancellation_source
- Copying_a_folder_does_not_follow_a_junction_loop
- Creating_a_folder_makes_it_inside_the_parent
- Creating_a_folder_that_already_exists_is_refused_and_leaves_it_untouched
- Creating_a_folder_with_the_name_of_an_existing_file_is_refused
- Failed_compression_removes_the_partial_archive
- Move_folder_to_another_volume_copies_then_removes_the_source
- Moving_a_folder_with_a_junction_across_volumes_fails_and_keeps_the_source
- RunAsync_hands_the_work_a_token_that_is_cancelled_on_timeout
- RunAsync_returns_at_the_timeout_even_when_the_work_ignores_the_token
- The_names_dot_and_dot_dot_are_not_new_folders (2 casos)
- Zip_folder_with_explicit_directory_entries_is_extracted_as_directories

### FilePaneViewModelTests (FilePaneViewModelTests.cs)

- A_folder_name_that_is_empty_or_cannot_exist_is_refused (5 casos)
- A_folder_that_can_be_listed_is_not_marked
- A_path_that_is_not_in_the_list_gives_no_neighbour
- A_range_made_with_Shift_keeps_its_anchor_when_the_other_end_moves
- A_subfolder_the_user_may_not_list_is_marked_from_the_parent_listing_without_opening_it
- After_removing_an_item_the_next_one_in_the_list_is_the_one_to_select
- After_removing_an_open_folder_the_items_inside_it_are_skipped
- After_removing_every_item_the_folder_that_held_them_is_the_one_to_select
- After_removing_several_items_the_neighbour_is_outside_the_removed_group
- After_removing_the_last_item_the_previous_one_is_the_one_to_select
- An_entry_in_the_favorites_folder_offers_to_remove_it_from_the_favorites
- An_entry_inside_an_archive_has_no_file_on_the_disk_to_drag_out
- An_entry_of_a_read_only_archive_cannot_be_deleted_and_the_archive_stays_intact
- An_entry_of_a_zip_can_be_deleted_and_leaves_the_rest
- Copying_a_file_leaves_the_original_and_never_overwrites_the_destination
- Copying_or_moving_into_an_archive_is_refused
- Creating_a_folder_makes_it_on_disk_and_shows_it_in_the_tree
- Creating_a_folder_that_already_exists_is_refused_and_does_not_touch_it
- Ctrl_click_adds_an_item_to_the_selection_and_a_second_Ctrl_click_takes_it_out
- Ctrl_click_keeps_folders_and_files_selected_together
- Ctrl_click_on_the_only_selected_item_leaves_nothing_selected
- Ctrl_click_without_a_selection_starts_one
- Deleting_a_protected_item_reports_access_denied_in_portuguese_and_marked
- Deleting_nothing_asks_for_a_selection
- Deleting_permanently_removes_files_and_folders_and_updates_the_tree
- Dragging_out_of_the_app_carries_every_selected_file_and_folder_from_the_disk
- Dropping_inside_an_archive_or_on_a_folder_that_is_gone_is_refused_with_a_reason
- Dropping_on_a_file_row_lands_in_the_folder_that_holds_it
- Dropping_on_a_folder_row_lands_in_that_folder
- Dropping_on_empty_space_lands_in_the_folder_the_pane_has_open
- Folders_cannot_be_created_inside_an_archive
- Items_in_the_recycle_bin_are_left_out_of_the_properties_window
- Marking_favorites_from_the_pane_adds_and_removes_them
- Moving_a_file_takes_it_out_of_the_source
- Moving_out_of_a_read_only_archive_only_copies_and_the_archive_keeps_the_entry
- Moving_out_of_a_zip_extracts_the_entry_and_removes_it_from_the_zip
- Navigating_to_a_file_leaves_the_pane_in_the_folder_that_holds_it
- Navigating_to_another_folder_changes_where_the_pane_is
- Nothing_selected_means_no_properties_to_show
- Only_files_that_can_really_be_deleted_are_offered_for_deletion
- Only_paths_that_exist_on_this_computer_count_as_local_paths
- Opening_a_folder_the_user_may_not_list_marks_the_node_instead_of_showing_it_as_plainly_empty
- Renaming_a_file_renames_it_on_disk_and_follows_it_in_the_selection
- Renaming_onto_a_name_that_is_taken_is_refused_and_changes_nothing
- Renaming_with_nothing_selected_asks_for_a_selection
- Selecting_a_folder_node_for_navigation_clears_the_item_selection_unless_asked_to_keep_it
- Selecting_a_subfolder_the_user_may_not_list_shows_no_permission_in_the_status_bar
- Shift_click_from_a_folder_reaches_the_files_below_it
- Shift_click_selects_every_item_between_the_anchor_and_the_clicked_one
- Sorting_by_name_descending_reorders_what_is_listed
- The_context_menu_adds_when_only_some_of_the_selected_items_are_favorites
- The_context_menu_has_no_favorites_entry_for_items_with_no_file_on_the_disk
- The_context_menu_offers_to_add_a_file_or_a_folder_to_the_favorites
- The_context_menu_offers_to_remove_what_is_already_a_favorite
- The_mark_goes_away_when_the_subfolder_can_be_listed_again
- The_new_folder_shortcut_creates_Nova_pasta_when_the_name_is_free
- The_new_folder_shortcut_finds_the_first_free_number_among_folders_and_files
- The_new_folder_shortcut_skips_a_file_that_is_called_Nova_pasta
- The_new_folder_shortcut_skips_a_Nova_pasta_that_already_exists
- The_pane_opens_on_the_initial_folder
- The_properties_window_gets_every_selected_item
- The_status_bar_counts_the_items_picked_with_Ctrl_click
- The_status_bar_of_a_selected_folder_follows_the_lock_mark
- The_tree_lists_folders_before_files_and_everything_that_is_on_disk
- Without_a_selection_the_properties_window_gets_the_current_row

### FileThumbnailServiceTests (ThumbnailTests.cs)

- Async_loading_honours_cancellation
- Async_loading_works_off_the_calling_thread
- Garbage_with_a_pdf_name_does_not_throw
- Huge_image_files_are_skipped_without_reading_them
- Missing_unsupported_and_corrupt_files_give_nothing_instead_of_failing
- Only_images_and_pdfs_have_previews (9 casos)
- Pdf_preview_either_works_or_gives_nothing_but_never_hangs_or_throws
- Small_images_are_not_enlarged
- Tall_images_are_limited_by_their_height_not_just_their_width
- Wide_images_are_limited_by_their_width

### FtpClientSecurityTests (NetworkClientTests.cs)

- A_certificate_that_changed_since_it_was_trusted_triggers_a_warning
- A_missing_certificate_is_never_accepted
- Auto_mode_uses_tls_when_the_server_offers_it_and_plain_ftp_when_it_does_not
- Certificates_with_policy_errors_need_the_users_confirmation (3 casos)
- Declining_the_certificate_refuses_the_connection_and_stores_nothing
- Explicit_ftps_asks_once_to_trust_a_self_signed_certificate_and_remembers_it
- Listed_times_come_back_in_local_time
- Plain_client_cannot_log_into_a_server_that_requires_tls

### FtpServerTests (NetworkServerTests.cs)

- A_real_client_can_list_upload_download_rename_and_delete
- Active_mode_connects_back_only_to_the_client_on_a_high_port
- An_invalid_passive_range_is_refused_when_starting
- Disabled_ftps_does_not_advertise_tls
- Explicit_ftps_protects_login_and_data_and_required_mode_refuses_plain_login
- Folders_can_be_created_listed_and_removed_only_when_empty
- Links_that_point_outside_the_shared_folder_are_not_followed
- MLSD_and_MLST_describe_entries_with_standard_facts
- Names_that_could_escape_or_hide_streams_are_refused
- Optional_ftps_accepts_both_encrypted_and_plain_sessions
- Passive_ports_stay_inside_the_configured_range
- Read_only_server_serves_files_but_refuses_every_change
- Repeated_login_failures_block_the_address
- The_ftps_certificate_is_created_once_and_reused
- Uploads_can_be_resumed_with_REST_and_APPE_appends

### HealthServiceTests (HealthServiceTests.cs)

- A_successful_manual_test_resumes_the_automatic_check
- Background_login_stops_after_repeated_failures_to_avoid_locking_accounts
- Changing_the_credentials_resumes_the_automatic_check
- Default_probe_reports_a_closed_port_as_unreachable_without_logging_in
- Default_probe_sees_a_listening_port_and_then_checks_the_login
- Reachable_server_with_a_valid_login_is_healthy
- Tftp_profiles_have_no_passive_check
- Unreachable_server_is_unhealthy_without_any_login_attempt

### HostKeyTests (SecurityTests.cs)

- Different_fingerprint_or_algorithm_is_a_change
- Same_key_is_trusted
- Store_round_trips_and_ignores_host_name_case
- Unknown_host_requires_confirmation
- Unreadable_store_knows_no_host

### InactivityTimeoutTests (TransferTests.cs)

- The_user_can_still_cancel_at_any_time
- Work_that_keeps_reporting_is_never_cut_off_even_when_it_runs_longer_than_the_idle_time
- Work_that_stops_making_progress_is_cancelled_after_the_idle_time

### ItemPropertiesServiceTests (ItemPropertiesServiceTests.cs)

- A_broken_image_does_not_break_reading_the_other_properties
- A_cancelled_count_stops_instead_of_finishing
- A_cancelled_sha256_stops_instead_of_finishing
- A_drive_reports_its_format_and_space
- A_drive_that_is_not_a_valid_root_has_no_details
- A_file_reports_its_name_location_size_and_dates
- A_folder_counts_every_file_and_folder_inside_it_and_their_bytes
- A_folder_has_no_size_of_its_own_and_is_flagged_as_a_directory
- A_folder_the_user_may_not_list_is_counted_as_unreadable_and_the_rest_still_adds_up
- A_junction_is_counted_as_a_folder_but_is_not_followed_so_nothing_is_counted_twice_or_forever
- A_large_file_gets_the_same_md5_and_sha256_as_the_framework_computes_in_one_go
- A_missing_file_fails_with_a_file_system_error_for_both_sums
- A_path_that_no_longer_exists_has_no_properties
- A_plain_text_file_has_no_executable_details_and_no_image_size
- An_empty_folder_has_nothing_inside
- An_executable_or_library_reports_its_version_details
- An_image_reports_its_width_and_height_in_pixels
- Attributes_reflect_what_is_set_on_the_file
- Cancelled_sums_stop_instead_of_finishing
- Loose_files_in_the_selection_count_as_files_next_to_the_folder_contents
- Rounding_a_size_up_to_the_cluster (5 casos)
- The_owner_of_a_file_created_by_this_user_is_this_user
- The_sha256_and_md5_of_known_content_match_the_published_values (3 casos)
- The_sha256_of_known_content_matches_the_published_value (2 casos)
- The_sha256_reports_progress_up_to_the_end
- The_size_on_disk_is_the_size_rounded_up_to_whole_clusters
- The_sums_report_progress_up_to_the_end
- The_two_sums_come_from_one_read_and_the_sha256_alone_is_unchanged

### ItemPropertiesViewModelTests (ItemPropertiesViewModelTests.cs)

- A_drive_shows_its_format_and_how_full_it_is
- A_file_shows_the_general_dates_attributes_security_and_verification_sections
- A_file_that_cannot_be_read_explains_it_in_the_sha256_row
- A_file_that_is_a_favorite_says_so
- A_file_that_is_gone_says_so_instead_of_showing_empty_rows
- A_folder_has_neither_the_sha256_nor_the_md5_row
- A_folder_shows_what_is_inside_it_once_the_count_finishes
- A_library_shows_a_details_section_with_its_version
- An_empty_folder_says_it_is_empty
- An_item_inside_an_archive_shows_only_what_the_listing_knows_and_explains_why
- Asking_again_while_the_sha256_runs_cancels_it
- Attribute_names (4 casos)
- Attributes_are_listed_by_name_in_portuguese
- Closing_the_window_while_a_folder_is_being_counted_stops_quietly
- Computing_fills_the_md5_row_together_with_the_sha256_row
- Computing_the_sha256_fills_its_row_and_gives_the_button_back
- Items_from_different_folders_say_there_are_several_locations
- Several_items_are_summed_and_the_common_folder_is_shown
- Sizes_are_written_in_the_app_format_followed_by_the_exact_bytes
- The_button_says_it_calculates_both_sums
- The_md5_row_sits_right_after_the_sha256_row_and_starts_as_not_calculated

### LegacyPasswordMigrationTests (SecurityTests.cs)

- Already_protected_file_is_left_untouched
- Clear_text_passwords_are_protected_on_startup_and_still_readable
- Saving_leaves_no_temporary_file_behind
- Unreadable_settings_are_kept_aside_instead_of_being_overwritten

### LockedDrivesOptionTests (LockedDrivesOptionTests.cs)

- A_settings_file_from_before_the_option_existed_still_shows_locked_drives
- Changing_the_option_tells_the_panes_to_redraw_and_setting_the_same_value_does_not
- Hiding_locked_drives_is_remembered_between_runs
- Locked_drives_are_shown_by_default_so_nothing_changes_for_existing_users
- Only_locked_drives_are_hidden_when_the_option_is_off (8 casos)
- What_counts_as_a_locked_drive (5 casos)

### LoginThrottleTests (SshServerTests.cs)

- A_good_login_clears_the_failures
- Blocks_after_the_limit_and_unblocks_after_the_penalty
- Old_failures_do_not_count

### MainMenuTests (MainMenuTests.cs)

- The_File_menu_and_the_old_Tools_name_are_gone
- The_new_options_are_checkable_and_bound_to_their_property_and_command (2 casos)
- The_Preferences_menu_lists_remember_path_and_always_administrator
- The_top_menus_are_View_Themes_Preferences_and_Help_in_that_order
- The_View_menu_lists_locked_drives_right_after_external_drives

### MainWindowViewModelTests (MainWindowViewModelTests.cs)

- A_failure_to_restart_as_administrator_shows_the_reason
- A_folder_that_was_empty_gets_its_expand_arrow_in_both_panes_after_a_drop_between_them
- A_folder_that_was_empty_gets_its_expand_arrow_in_both_panes_after_a_drop_from_another_program
- A_remembered_folder_that_no_longer_exists_falls_back_to_a_drive_instead_of_failing
- A_transfer_takes_the_selection_of_the_active_pane_to_the_folder_of_the_other
- A_transfer_without_a_selection_says_what_is_missing
- Activating_the_right_pane_swaps_source_and_destination_and_tells_the_screen
- Checking_always_administrator_asks_for_access_right_away_and_keeps_the_check_on
- Copy_and_paste_puts_a_copy_in_the_other_pane_and_keeps_the_clipboard
- Copying_to_the_other_pane_leaves_both_panes_in_the_folders_they_were_in
- Cut_and_paste_moves_the_file_and_empties_the_clipboard
- Dropping_files_on_the_other_pane_copies_or_moves_them_there
- Dropping_on_the_other_pane_leaves_both_panes_in_the_folders_they_were_in
- Dropping_onto_the_same_folder_is_refused_without_touching_the_file
- Moving_to_the_other_pane_leaves_both_panes_in_the_folders_they_were_in
- Panes_open_where_they_were_left_when_remembering_the_path_is_on_and_the_folder_still_exists
- Pasting_into_the_other_pane_leaves_both_panes_in_the_folders_they_were_in
- Pasting_with_nothing_staged_reports_it
- Refusing_the_windows_confirmation_puts_the_check_back_to_off_without_a_message
- Staging_without_a_selection_names_the_action_that_was_asked_for
- The_chosen_sort_order_applies_to_both_panes
- The_folder_a_file_was_moved_out_of_loses_its_arrow_in_both_panes
- The_left_pane_starts_active_and_the_right_one_is_the_destination
- The_menu_option_for_locked_drives_flips_and_is_saved
- The_pane_with_a_selection_is_the_one_used_and_the_active_one_wins_a_tie
- Transfers_are_validated_before_anything_is_touched
- Unchecking_always_administrator_only_saves_the_choice
- Without_remembering_the_panes_do_not_restore_a_path

### MemoryTrimmerTests (InfrastructureTests.cs)

- A_large_load_triggers_one_collection_after_the_settle_delay
- Back_to_back_large_loads_wait_for_the_last_one
- Small_loads_never_trigger_a_collection

### NativeFileCopyTests (TransferTests.cs)

- An_existing_destination_is_never_overwritten
- Cancelling_in_the_middle_of_a_file_stops_it_and_leaves_no_partial_destination
- Copy_keeps_the_bytes_dates_attributes_and_alternate_streams
- Move_reports_progress_and_removes_the_source
- Progress_grows_and_ends_at_the_file_length

### NavigationHistoryTests (NavigationTests.cs)

- Back_and_forward_walk_through_the_visited_folders
- Capacity_drops_the_oldest_entries
- New_history_has_nowhere_to_go
- Repeating_the_current_folder_does_not_add_an_entry
- Visiting_a_new_folder_discards_what_was_ahead

### NetworkFileServiceTests (InfrastructureTests.cs)

- TryParseRemotePath_rejects_local_paths
- TryParseRemotePath_rejects_unknown_schemes (3 casos)
- TryParseRemotePath_roundtrips_BuildRemotePath (5 casos)

### NetworkGroupTests (NetworkGroupTests.cs)

- Protocols_hidden_in_the_pane_options_do_not_appear
- Saved_profiles_appear_under_their_own_protocol_sorted_by_name
- The_group_and_its_protocols_are_already_loaded_so_they_never_wait_for_a_listing
- The_network_group_holds_the_four_protocols_in_order
- With_every_protocol_hidden_there_is_no_network_group

### NetworkPortGuardTests (NetworkPortGuardTests.cs)

- A_port_another_process_listens_on_is_reported_as_used_by_another_process
- A_port_nobody_listens_on_is_free
- A_port_this_process_listens_on_is_listening_but_not_used_by_another_process
- A_server_is_still_refused_when_another_process_holds_the_port_in_its_own_transport
- A_tcp_listener_of_another_process_does_not_block_a_tftp_server_on_the_same_number
- A_tftp_server_starts_even_when_another_process_holds_the_same_number_on_tcp
- A_udp_port_this_process_uses_is_listening
- A_udp_socket_of_another_process_does_not_block_a_tcp_server_on_the_same_number
- An_ftp_server_starts_even_when_another_process_holds_the_same_number_on_udp
- Cleaning_up_at_startup_never_ends_other_processes
- Ports_outside_the_valid_range_are_never_reported_as_used_by_another_process (3 casos)

### NetworkServerHardeningTests (SecurityTests.cs)

- Ftp_blocks_paths_outside_the_root_and_alternate_streams
- Ftp_closes_the_connection_after_three_failed_logins
- Ftp_commands_other_than_login_require_authentication
- Ftp_lists_over_a_passive_connection_from_the_same_client
- Ftp_server_requires_username_and_password
- Overly_broad_or_missing_roots_are_refused
- Ssh_servers_require_username_and_password (2 casos)
- Tftp_completed_write_replaces_the_file_and_ignores_repeated_blocks
- Tftp_rejects_paths_outside_the_root_but_accepts_a_leading_slash
- Tftp_write_that_never_completes_does_not_truncate_the_existing_file

### OperationTrackerTests (InfrastructureTests.cs)

- A_failed_or_already_disposed_operation_does_not_break_cancelling_the_others
- An_operation_counts_as_active_only_while_it_is_registered
- Cancelling_all_gives_up_after_the_timeout_when_an_operation_never_stops
- Cancelling_all_waits_for_the_operation_to_finish_its_cleanup

### PaneColumnsTests (PaneColumnsTests.cs)

- A_closed_folder_does_not_count_what_is_inside_it
- A_closed_top_level_row_does_not_count_its_children
- A_deeper_row_counts_its_larger_indent
- A_name_set_by_the_user_wins_over_the_widest_name_wider_or_narrower_and_goes_back_with_a_double_click
- A_name_width_that_makes_no_sense_means_automatic (4 casos)
- A_narrow_name_does_not_change_when_the_folders_open_or_close
- A_new_pane_has_the_fixed_widths_and_an_automatic_name
- A_settings_file_with_the_old_per_column_widths_loads_with_an_automatic_name
- A_swipe_to_the_right_scrolls_right_and_to_the_left_scrolls_left_inside_the_scrollable_range
- An_element_that_starts_inside_the_viewport_gets_only_the_width_that_is_left
- An_element_with_no_visible_area_is_left_alone
- An_open_root_with_only_protocols_has_no_row_with_columns
- Comfortable_rows_still_drop_the_date_when_the_pane_gets_narrow_and_the_columns_come_back_in_columns_mode
- Double_clicking_the_edge_goes_back_to_automatic_and_saves
- Dragging_all_the_way_to_the_left_stops_at_the_minimum_and_does_not_jump_back_to_automatic
- Dragging_changes_the_name_width_on_screen_but_only_saves_at_the_end_of_the_drag
- Dragging_the_right_edge_of_the_name_widens_it_to_the_right_and_narrows_it_to_the_left
- Each_pane_keeps_its_own_name_width
- Each_row_gives_its_name_the_width_that_completes_the_widest_row (7 casos)
- Every_header_title_sorts_the_list_in_both_modes
- Every_row_ends_up_with_the_same_natural_width_whatever_its_level
- In_a_huge_folder_only_the_longest_names_are_measured
- In_columns_mode_no_column_is_hidden_when_the_pane_gets_narrow (3 casos)
- In_comfortable_rows_the_header_shows_name_modified_and_size_without_the_type_and_without_the_resize_grip
- Narrowing_the_name_tells_the_rows_and_the_header
- Only_folders_files_and_archives_take_part_in_the_columns (11 casos)
- Only_the_name_column_has_a_grip_in_the_header
- Placeholders_and_empty_names_are_ignored
- Refreshing_tells_the_list_only_when_the_widest_row_changed
- Small_touchpad_steps_add_up_like_one_notch
- Sorting_by_size_from_largest_to_smallest_works_in_comfortable_rows
- The_columns_of_a_row_are_not_drawn_for_roots_and_protocols
- The_columns_start_at_the_same_place_in_every_row_whatever_its_level
- The_comfortable_footer_has_the_same_fixed_columns_as_the_header_so_the_titles_line_up
- The_corner_where_the_two_scroll_bars_meet_is_not_the_white_system_color
- The_default_application_box_can_grow_as_wide_as_the_type_column_allows
- The_default_application_box_fits_inside_the_comfortable_row
- The_drag_starts_from_the_width_the_column_has_now (5 casos)
- The_drive_row_converters_leave_roots_and_protocols_alone
- The_fixed_column_headers_take_their_widths_from_the_pane
- The_fixed_columns_add_up_to_what_sits_to_the_right_of_the_name
- The_fixed_columns_are_wide_enough_for_their_content
- The_header_is_always_there_but_hidden_while_there_are_no_column_rows
- The_header_moves_with_the_horizontal_scroll_of_the_list
- The_header_tells_the_screen_when_it_empties_and_when_it_fills
- The_horizontal_wheel_delta_is_the_signed_high_word_of_wParam (3 casos)
- The_list_has_a_horizontal_scroll_bar_when_needed
- The_manual_name_width_only_counts_in_columns_mode
- The_name_cell_and_the_row_grid_only_use_the_columns_when_the_row_takes_part_in_them
- The_name_width_saved_in_the_pane_options_is_used_when_the_pane_opens
- The_name_width_stays_between_the_minimum_and_the_maximum_once_it_is_set
- The_name_width_survives_saving_and_loading_the_settings
- The_other_columns_never_change_when_the_name_is_widened
- The_pane_row_width_is_the_widest_row_of_the_open_folders
- The_row_content_has_a_fixed_width_when_the_name_is_set_by_the_user (3 casos)
- The_row_of_a_root_such_as_a_drive_is_pinned_while_the_list_scrolls_sideways
- The_row_width_is_zero_in_comfortable_rows_and_comes_back_in_columns_mode
- The_scroll_bars_are_half_again_as_thick_as_before
- The_sticky_element_always_takes_the_visible_width_even_without_scroll
- The_sticky_width_is_the_same_before_and_after_it_was_already_moved
- The_top_level_rows_are_not_counted_because_they_only_take_the_visible_area
- The_widest_name_ignores_the_names_of_the_roots_and_of_the_network_protocols
- The_widest_row_is_the_longest_name_in_the_open_folders_with_its_indent_and_icon
- What_sits_left_of_the_name_is_the_indent_of_the_deepest_row_plus_the_icon (5 casos)
- Widening_the_name_tells_the_list_that_the_row_width_changed
- With_every_root_closed_the_header_has_no_titles_and_it_comes_back_when_a_folder_opens
- With_horizontal_scroll_the_element_moves_right_by_the_offset_and_keeps_the_visible_width

### PathNavigationTests (NavigationTests.cs)

- Archives_open_as_folders_go_up_through_their_entries_and_out_of_the_file
- Local_folders_go_up_one_level (4 casos)
- Remote_folders_go_up_until_the_server_root
- Roots_have_no_parent (2 casos)
- Special_or_empty_paths_have_no_parent (5 casos)

### PathSafetyServiceTests (InfrastructureTests.cs)

- IsSubPath_detects_child_directories (12 casos)

### ProtectedPasswordTests (SecurityTests.cs)

- Client_profile_password_round_trips
- Empty_password_stays_empty
- Legacy_clear_text_password_is_still_read
- Password_is_not_written_in_clear_text_and_round_trips
- Undecryptable_password_becomes_empty_instead_of_failing

### RangeObservableCollectionTests (InfrastructureTests.cs)

- ReplaceAll_swaps_the_content_with_a_single_reset_notification
- ReplaceAll_with_nothing_empties_the_collection
- SyncTo_moves_reordered_items_instead_of_recreating_them
- SyncTo_reaches_the_target_for_any_mix_of_insertions_removals_and_moves
- SyncTo_swaps_everything_at_once_above_the_limit_and_when_there_are_repeats
- SyncTo_touches_only_what_changed_and_never_resets
- SyncTo_with_the_same_content_raises_no_notification
- Tree_nodes_expose_the_bulk_collection_and_still_work_as_a_normal_observable_collection

### RecycleBinMenuTests (RecycleBinMenuTests.cs)

- A_failure_still_refreshes_because_part_of_the_bin_may_have_been_emptied
- A_refusal_from_windows_becomes_a_message_in_portuguese
- An_exception_from_the_shell_is_reported_instead_of_crashing
- Emptying_a_bin_that_is_already_empty_is_not_an_error_and_does_not_bother_windows
- Emptying_asks_windows_once_and_succeeds
- Emptying_from_a_pane_clears_its_selection_and_reports_success
- Emptying_the_bin_refreshes_the_row_in_both_panes
- Restoring_everything_from_an_empty_bin_says_it_is_empty
- The_question_before_emptying_says_how_many_items_go_for_good (3 casos)
- The_question_before_restoring_says_how_many_items_come_back (3 casos)
- The_unexpected_code_windows_returns_when_the_bin_emptied_meanwhile_counts_as_done

### RemoteFileServiceFtpTests (RemoteFileTests.cs)

- A_folder_goes_up_comes_back_gets_renamed_and_deleted_on_a_real_ftp_server
- A_wrong_password_is_reported_instead_of_hanging

### RemoteFileServiceTests (RemoteFileTests.cs)

- A_failed_upload_removes_what_reached_the_server
- Cancelling_an_upload_stops_it_and_cleans_the_server
- Copying_between_servers_goes_through_a_temporary_folder_that_is_cleaned
- Create_directory_refuses_a_name_that_already_exists
- Delete_removes_all_the_given_items
- Moving_inside_the_same_account_only_renames_on_the_server
- Opening_a_remote_file_downloads_it_to_a_temporary_folder
- Rename_returns_the_new_tree_path
- Tree_paths_and_plain_paths_are_both_understood
- Two_connections_are_the_same_account_only_when_protocol_host_port_and_user_match

### RemoteTransferEngineTests (RemoteFileTests.cs)

- A_connection_that_drops_mid_download_leaves_nothing_behind
- A_download_never_overwrites_an_existing_local_item
- A_free_name_is_kept (2 casos)
- A_remote_name_that_tries_to_escape_the_destination_folder_is_refused (4 casos)
- A_taken_name_gets_a_number_before_the_extension_like_local_copies
- Deleting_removes_files_and_folders_recursively_but_never_the_server_root
- Downloading_a_file_copies_the_bytes_reports_progress_and_leaves_no_temporary_file
- Downloading_a_folder_brings_everything_inside_and_skips_symbolic_links
- Renaming_keeps_the_folder_and_refuses_bad_or_taken_names
- Scanning_adds_up_bytes_and_files_without_following_links
- Uploading_a_folder_creates_the_tree_and_picks_a_free_name_on_the_server

### RenameTests (RenameTests.cs)

- A_drive_or_a_recycle_bin_entry_is_never_renamed_by_a_slow_click
- A_file_starts_with_the_name_selected_but_not_the_extension_and_a_folder_with_everything (11 casos)
- A_folder_can_be_renamed_by_a_slow_click_too
- A_slow_click_renames_the_item_that_was_already_the_only_one_selected
- After_renaming_a_file_the_item_with_the_new_name_is_the_only_one_selected
- After_renaming_a_folder_the_folder_with_the_new_name_is_selected
- An_item_inside_a_selection_of_several_does_not_start_renaming
- An_item_that_was_not_selected_yet_does_not_start_renaming
- Cancelling_leaves_the_name_untouched
- Cancelling_or_keeping_the_same_name_announces_nothing
- Committing_a_new_name_renames_the_file_on_disk
- Confirming_the_same_name_changes_nothing
- Nothing_starts_renaming_while_another_item_is_being_renamed
- Renaming_announces_the_new_path_so_the_screen_can_give_it_the_focus
- Starting_to_rename_fills_the_box_with_the_current_name_and_says_so
- The_old_name_is_not_left_selected_after_renaming
- The_selected_range_never_leaves_the_text

### RowDensitySettingTests (NavigationTests.cs)

- Changing_the_density_is_saved_and_announced_once
- Compact_rows_are_the_default_for_new_and_old_settings_files

### ScpCommandTests (SshServerTests.cs)

- Parses_what_real_clients_send (9 casos)
- Refuses_everything_that_is_not_a_plain_scp_transfer (12 casos)

### ScpServerSessionTests (SshServerTests.cs)

- An_endless_line_is_cut_off
- Client_reported_error_after_the_data_discards_the_file
- Interrupted_upload_leaves_nothing_behind_and_keeps_the_old_file
- Sink_on_a_read_only_server_refuses_before_accepting_anything
- Sink_receives_a_file_into_the_target_folder
- Sink_receives_a_folder_tree_and_keeps_times
- Sink_refuses_a_target_outside_or_unsafe
- Sink_refuses_unsafe_names_and_garbage (10 casos)
- Sink_treats_an_end_of_directory_with_nothing_open_as_the_end_of_the_transfer
- Sink_uses_the_target_name_when_the_target_is_a_file_path
- Source_dotdot_cannot_reach_files_outside_the_share
- Source_expands_a_wildcard_in_the_last_name_only
- Source_reports_missing_files_and_refuses_unsafe_paths
- Source_sends_a_file
- Source_sends_a_folder_only_when_recursive

### SearchNavigationTests (SearchNavigationTests.cs)

- A_big_folder_found_as_a_result_opens_with_everything_in_it
- A_collapsed_folder_does_not_count_the_rows_inside_it
- A_file_in_a_big_folder_still_being_filled_is_found_and_selected
- A_file_without_size_or_date_has_no_footer_even_with_comfortable_rows
- A_fill_forced_to_finish_is_not_applied_again_by_the_queued_batches
- A_folder_inside_a_big_folder_still_being_filled_is_found_on_the_way_to_the_file
- A_folder_row_keeps_the_plain_height_in_comfortable_rows
- A_stale_forced_finish_after_the_folder_was_reloaded_does_not_touch_the_new_children
- Comfortable_rows_use_the_measured_height_of_a_drawn_file_row
- Comfortable_rows_without_a_measured_row_add_the_footer_height
- Compact_rows_are_estimated_at_the_plain_row_height_with_no_footer
- Completing_a_chunked_fill_puts_every_child_in_the_node_at_once
- Completing_a_fill_that_is_not_pending_does_nothing
- Expanding_a_sibling_folder_clears_the_selection_of_a_file_next_to_it
- Navigating_finishes_the_pending_fill_so_the_selected_node_is_the_one_that_stays_in_the_tree
- Navigating_to_a_file_in_another_folder_clears_the_selection_of_the_first_folder
- Navigating_to_a_file_leaves_only_that_file_selected
- Opening_a_path_through_a_big_folder_still_being_filled_reaches_the_end_of_the_path
- The_fill_of_a_small_folder_is_not_left_pending

### SearchWindowTests (SearchWindowTests.cs)

- A_long_path_can_be_read_in_full_from_the_tooltip
- A_search_finds_an_item_buried_in_many_subfolders_and_reports_its_full_path
- A_size_not_measured_yet_falls_back_to_the_minimum
- Enlarging_the_menu_stays_inside_the_limits (5 casos)
- The_column_header_template_has_the_gripper_the_gridview_looks_for
- The_filler_column_after_the_last_one_gets_no_gripper
- The_menu_has_a_grip_to_make_it_bigger_and_a_button_to_open_a_new_window
- The_menu_is_not_capped_at_a_small_height_any_more
- The_menu_results_list_uses_the_adjustable_header_and_shows_name_and_path
- The_new_window_can_be_resized_and_shows_the_same_search_text_and_status
- The_new_window_lists_name_path_and_size_with_adjustable_columns
- The_toolbar_search_fills_the_list_the_new_window_shows

### SelectionStatusFormatterTests (InterfaceTests.cs)

- A_folder_the_user_may_not_list_says_so_instead_of_showing_zero_folders_and_files
- Drive_shows_name_and_free_space_or_the_reason_it_is_unavailable
- File_shows_name_size_and_date
- Folder_shows_name_and_counts_with_correct_plurals
- Multiple_selection_shows_count_and_total_size
- Nothing_selected_has_no_text
- Placeholders_never_show_text

### ServerVirtualPathTests (SshServerTests.cs)

- IsSafeSegment_matches_windows_rules (12 casos)
- Normalize_never_goes_above_the_root (9 casos)
- Resolve_keeps_dotdot_inside_the_root
- Resolve_maps_inside_the_root
- Resolve_refuses_names_windows_would_treat_as_something_else (12 casos)

### ServiceRegistrationTests (ServiceRegistrationTests.cs)

- Each_contract_is_served_by_its_implementation (7 casos)
- Every_registered_service_has_all_its_dependencies_registered
- The_administrator_startup_option_is_one_shared_object_so_the_menu_and_the_app_start_agree
- The_main_window_gets_a_fresh_view_model_each_time

### SftpServerSessionTests (SshServerTests.cs)

- A_junction_pointing_outside_is_hidden_and_cannot_be_followed
- A_request_before_init_ends_the_session
- Dangerous_paths_are_denied_without_touching_the_disk (4 casos)
- Dotdot_stays_inside_the_share
- Handles_only_work_for_the_kind_of_thing_they_were_opened_for
- Interrupted_overwrite_keeps_the_existing_file
- Interrupted_upload_removes_the_new_partial_file
- Malformed_request_closes_the_session
- Oversized_packet_closes_the_session
- Read_length_is_capped_so_a_client_cannot_ask_for_gigabytes
- Read_only_server_refuses_every_change_but_still_reads
- Rename_only_replaces_with_the_posix_extension
- Setstat_can_truncate_and_set_the_time_but_not_escape
- The_number_of_open_handles_is_limited
- The_root_cannot_be_removed_or_renamed_and_non_empty_folders_survive
- Unknown_requests_get_an_unsupported_status_and_the_session_survives
- Upload_and_download_round_trip
- Version_3_is_negotiated_and_the_root_is_listed

### ShellIconTests (ShellIconTests.cs)

- Archives_always_get_an_icon (7 casos)
- Files_that_are_not_archives_never_get_the_drawn_icon
- Only_archives_the_app_opens_can_get_the_drawn_icon (15 casos)
- The_drawn_icon_has_the_page_the_band_and_the_zipper_pull
- The_drawn_icon_is_frozen_and_16_by_16
- The_drawn_icon_replaces_a_generic_or_missing_windows_icon
- The_icon_from_windows_is_kept_when_it_is_a_real_one

### SolidArchiveTests (SolidArchiveTests.cs)

- A_folder_of_a_solid_archive_is_extracted_whole_and_correct (2 casos)
- A_missing_entry_of_a_solid_archive_is_reported (2 casos)
- A_solid_archive_lists_its_folders_with_counts (2 casos)
- Every_file_of_a_solid_archive_comes_out_with_its_own_content (2 casos)

### SortHeaderLogicTests (NavigationTests.cs)

- Clicking_a_header_keeps_folders_first_as_configured
- Clicking_another_column_starts_in_its_natural_direction (4 casos)
- Clicking_the_sorted_column_flips_the_direction
- Indicator_marks_only_the_sorted_column

### SpecialFolderGuardTests (SpecialFolderGuardTests.cs)

- A_blank_path_is_never_a_favorite (3 casos)
- Favorites_refuse_the_recycle_bin_themselves_and_blank_paths (6 casos)
- Only_paths_inside_the_recycle_bin_count_as_recycle_bin_data (8 casos)
- Permanent_delete_refuses_anything_that_is_not_in_the_recycle_bin
- Removing_a_blank_favorite_is_refused
- Restore_and_delete_ask_for_a_selection_when_there_is_none
- Restore_refuses_anything_that_is_not_in_the_recycle_bin
- The_original_path_is_unknown_outside_the_bin_or_without_its_info_file (4 casos)
- Toggling_favorites_needs_at_least_one_real_path

### SqliteSecurityTests (InfrastructureTests.cs)

- The_bundled_sqlite_is_new_enough_to_include_the_fix_for_CVE_2025_6965

### SshAuthenticationTests (NetworkClientTests.cs)

- A_key_without_password_does_not_add_password_methods
- A_private_key_is_tried_first_then_password_and_keyboard_interactive
- An_unreadable_key_explains_which_file_failed
- Old_profiles_without_the_new_fields_keep_working_with_safe_defaults
- The_key_passphrase_is_stored_protected_and_round_trips
- Without_a_key_the_password_methods_are_used

### SshServerEndToEndTests (SshServerTests.cs)

- Each_server_only_speaks_its_own_protocol
- Garbage_connections_do_not_stop_the_server
- Host_key_is_stable_across_restarts
- Log_records_activity_but_never_the_password
- Port_forwarding_is_never_opened
- Read_only_scp_server_refuses_uploads
- Read_only_sftp_server_allows_reading_and_refuses_changes
- Scp_transfers_folders_recursively
- Scp_uploads_and_downloads_files_with_a_real_client
- Sftp_client_is_confined_to_the_share
- Sftp_lists_downloads_uploads_and_manages_files
- Sftp_upload_over_an_existing_file_replaces_it
- Shell_commands_are_never_executed
- Ssh_servers_require_a_username_and_password (2 casos)
- Stopping_the_server_disconnects_clients_and_frees_the_port
- Too_many_failed_logins_block_the_address
- Wrong_password_and_wrong_user_are_refused

### StopDoraScriptTests (StopDoraScriptTests.cs)

- A_process_the_terminal_may_not_terminate_is_told_apart_from_one_it_may
- A_process_without_a_window_is_not_waited_for_because_nobody_could_answer
- A_window_that_accepts_the_close_request_leaves_without_being_killed
- A_window_that_refuses_is_waited_for_then_handed_back_to_be_forced

### StorageUsageTests (InterfaceTests.cs)

- Drive_node_exposes_its_level
- Usage_ratio_maps_to_a_level (7 casos)

### TftpClientTests (TftpClientTests.cs)

- Cancellation_stops_a_transfer_that_is_waiting_for_the_server
- Download_of_a_missing_file_reports_the_server_error_quickly
- Download_returns_the_exact_bytes_for_sizes_around_the_block_boundary (7 casos)
- Failed_download_keeps_the_existing_local_file
- Probe_succeeds_against_a_running_server_and_fails_against_a_closed_port
- Transfer_to_a_silent_server_times_out_instead_of_hanging
- Upload_stores_the_exact_bytes_on_the_server (5 casos)

### TftpServerTests (NetworkServerTests.cs)

- A_lost_ack_makes_the_server_retransmit_the_same_block
- An_upload_declaring_more_than_the_limit_is_refused_up_front
- Block_numbers_wrap_around_after_65535_for_big_files
- Block_size_is_limited_to_what_the_protocol_allows (2 casos)
- Options_blksize_and_tsize_are_negotiated_with_an_OACK
- Our_client_falls_back_when_the_server_ignores_options
- Our_client_negotiates_larger_blocks_and_round_trips_files
- Our_client_retries_without_options_after_error_8
- Packets_from_another_port_get_error_5_without_breaking_the_transfer
- Read_only_mode_refuses_writes_with_an_access_violation
- Unknown_options_are_ignored_and_the_transfer_uses_512_byte_blocks
- Unsupported_transfer_modes_and_malformed_requests_are_rejected

### ThemeUsageColorsTests (InterfaceTests.cs)

- Both_themes_define_distinct_usage_colors (2 casos)
- Normal_usage_follows_the_accent_while_the_alert_colors_stay_fixed (3 casos)
- Scrollbar_and_hierarchy_lines_follow_the_accent (2 casos)
- Storage_summary_text_also_signals_the_alert_level
- Tree_items_declare_an_accessible_name

### ThumbnailHoverRulesTests (ThumbnailTests.cs)

- Preview_only_applies_to_local_images_and_pdfs (8 casos)
- Recycle_bin_entries_are_never_previewed

### TransferProgressFormatterTests (TransferTests.cs)

- Description_says_calculating_until_the_total_is_known
- Description_shows_bytes_files_speed_and_remaining_time
- Durations_are_shown_as_clock_time (3 casos)
- Title_names_the_action_the_count_and_the_destination_folder

### XamlResourceTests (NavigationTests.cs)

- Every_static_resource_used_in_xaml_is_defined_somewhere

