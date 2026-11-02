# Dora Explorer - Book de Testes

> Auto-gerado por scripts/generate-booktest.ps1. Nao edite a mao. Atualizacao: 2026-11-01 23:18
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
| Areas | 23 |
| Classes de teste | 63 |
| Metodos de teste | 463 |
| Casos (cada InlineData conta um) | 709 |

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
- Testes (39 casos): FilePaneViewModelTests [FilePaneViewModelTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FilePaneViewModelTests"`

### Janela principal (copiar e colar entre os paineis)

Painel ativo, area de transferencia de copiar e cortar, regras que recusam transferencias sem sentido, arrastar e soltar, ordenacao nos dois lados e lembrar o caminho ao abrir.

- Codigo: MainWindowViewModel, RememberPathSettingsService, PaneDisplaySettingsService, TransferOutcome
- Testes (16 casos): MainWindowViewModelTests [MainWindowViewModelTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~MainWindowViewModelTests"`

### Acesso negado e reiniciar como administrador

Mensagens de falha em portugues (sem o prefixo \\?\ do .NET), falta de permissao criada de verdade com regra NTFS de negar (excluir, renomear, criar pasta, copiar, mover, compactar), quando perguntar se quer reiniciar como administrador, como o app reinicia e espera o processo antigo sair, e pasta que o Windows nao deixa listar (nao vira "vazia" no cache e a linha mostra um cadeado no lugar das contagens e a barra de status diz "sem permissao para abrir").

- Codigo: FileErrorText, ElevationPrompt, ElevationService, IElevationService, StartupArguments, TransferAccessDeniedException
- Testes (49 casos): FileErrorTextTests [FileErrorTextTests.cs], AccessDeniedOperationTests [AccessDeniedOperationTests.cs], ElevationTests [ElevationTests.cs], DeniedFolderRowTests [InterfaceTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~FileErrorTextTests|FullyQualifiedName~AccessDeniedOperationTests|FullyQualifiedName~ElevationTests|FullyQualifiedName~DeniedFolderRowTests"`

### Start.bat: fechar o app antes de compilar (stop-dora)

Pede para a janela fechar e espera (o app pode perguntar sobre uma operacao em andamento), so depois usa o taskkill; processo sem janela vai direto ao taskkill; processo que o terminal nao consegue encerrar (app aberto como administrador) e reconhecido antes, para avisar em vez de esperar. Os testes usam processos de mentira e so carregam as funcoes do script, nunca o app de verdade.

- Codigo: 
- Testes (4 casos): StopDoraScriptTests [StopDoraScriptTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~StopDoraScriptTests"`

### Registro de servicos (injecao de dependencia)

Todo servico registrado tem as dependencias registradas; um servico novo esquecido estouraria so ao abrir o app.

- Codigo: ServiceRegistration
- Testes (9 casos): ServiceRegistrationTests [ServiceRegistrationTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ServiceRegistrationTests"`

### Falhas esperadas do sistema

Volume bloqueado (BitLocker) e erros de E/S que o app trata sem registrar como erro.

- Codigo: ExpectedFailures
- Testes (2 casos): ExpectedFailuresTests [InfrastructureTests.cs]
- Rodar: `dotnet test tests\DoraExplorer.App.Tests --filter "FullyQualifiedName~ExpectedFailuresTests"`

## Indice por tipo (mudou este tipo? rode esta area)

| Tipo | Area |
|------|------|
| AppSettingsService | Seguranca de senhas e configuracoes |
| ArchiveFormat | Arquivos compactados (abrir como pasta) |
| ArchivePathHelper | Arquivos compactados (abrir como pasta) |
| ArchiveService | Arquivos compactados (abrir como pasta) |
| BreadcrumbSegmentViewModel | Painel de arquivos (um lado da janela) |
| DefaultApplicationOverrides | Abrir com e programas padrao |
| DirectoryIndexService | Cache de pastas e carga sem piscar |
| ElevationPrompt | Acesso negado e reiniciar como administrador |
| ElevationService | Acesso negado e reiniciar como administrador |
| ExpectedFailures | Falhas esperadas do sistema |
| ExplorerThemeResources | Tema, cores e interface |
| ExplorerViewSettingsService | Tema, cores e interface |
| FavoritesService | Lixeira e Favoritos |
| FileAssociationService | Abrir com e programas padrao |
| FileErrorText | Acesso negado e reiniciar como administrador |
| FileOpenService | Abrir com e programas padrao |
| FileOperationService | Copiar, mover e compactar (progresso, cancelamento, tempo limite); Painel de arquivos (um lado da janela) |
| FilePaneViewModel | Painel de arquivos (um lado da janela) |
| FileThumbnailService | Previa de imagens e PDF |
| FtpCertificateVerifier | Confianca em servidores (chaves, certificados, login do cliente) |
| FtpServer | Servidor FTP e FTPS |
| FtpTlsCertificate | Confianca em servidores (chaves, certificados, login do cliente) |
| HostKeyVerifier | Confianca em servidores (chaves, certificados, login do cliente) |
| IElevationService | Acesso negado e reiniciar como administrador |
| KnownHostsStore | Confianca em servidores (chaves, certificados, login do cliente) |
| LoginThrottle | Servidor SFTP e SCP |
| MainWindowViewModel | Janela principal (copiar e colar entre os paineis) |
| MemoryTrimmer | Cache de pastas e carga sem piscar |
| NativeFileCopy | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| NavigationHistory | Navegacao e caminhos |
| NavigationNodeKind | Pasta Rede na arvore |
| NavigationTreeNodeViewModel | Painel de arquivos (um lado da janela) |
| NavigationTreeService | Pasta Rede na arvore; Arquivos compactados (abrir como pasta) |
| NetworkClientHealthService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| NetworkFileService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| NetworkPortGuard | Travas dos servidores locais (pasta raiz e portas) |
| NetworkServerService | Travas dos servidores locais (pasta raiz e portas) |
| OperationTimeouts | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| OperationTracker | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| PaneDisplayOptions | Pasta Rede na arvore |
| PaneDisplaySettingsService | Janela principal (copiar e colar entre os paineis) |
| PathNavigation | Navegacao e caminhos |
| PathSafetyService | Navegacao e caminhos |
| ProtectedStringJsonConverter | Seguranca de senhas e configuracoes |
| RangeObservableCollection | Cache de pastas e carga sem piscar |
| RecycleBinService | Lixeira e Favoritos |
| RememberPathSettingsService | Janela principal (copiar e colar entre os paineis) |
| RemoteFileService | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| RemoteTransferEngine | Clientes de rede (sessoes, copia entre servidores, saude dos perfis) |
| ScpServerSession | Servidor SFTP e SCP |
| SelectionStatusFormatter | Tema, cores e interface |
| ServerVirtualPath | Servidor SFTP e SCP |
| ServiceRegistration | Registro de servicos (injecao de dependencia) |
| SftpServerSession | Servidor SFTP e SCP |
| ShellIconService | Icones de arquivos |
| SortHeaderLogic | Tema, cores e interface |
| SshFileServer | Servidor SFTP e SCP |
| SshHostKeyStore | Servidor SFTP e SCP |
| StartupArguments | Acesso negado e reiniciar como administrador |
| StorageUsage | Tema, cores e interface |
| TftpClient | TFTP (servidor e cliente) |
| TftpServer | TFTP (servidor e cliente) |
| ThemeManager | Tema, cores e interface |
| ThumbnailHoverPreview | Previa de imagens e PDF |
| TransferAccessDeniedException | Acesso negado e reiniciar como administrador |
| TransferOutcome | Janela principal (copiar e colar entre os paineis) |
| TransferProgressFormatter | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |
| TransferScanner | Copiar, mover e compactar (progresso, cancelamento, tempo limite) |

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
| Janelas de dialogo (progresso, conexao de rede, ordenar, excluir) | Janelas WPF; sem teste automatico. | Abrir cada uma no app. |
| Restaurar e excluir de verdade da Lixeira | Mexe na lixeira real do usuario; so as travas sao testadas. | Com arquivos de teste, na tela. |
| Start.bat e scripts de apoio | O fechamento do app esta em teste automatico (StopDoraScriptTests, com processos de mentira); o Start.bat inteiro (compilar e abrir) e o caso de um app aberto como administrador, que pede o UAC, nao. | Rodar Start.bat e conferir que o app abre. Com o app aberto, rodar de novo: ele fecha sozinho (o log termina com "encerrado") e reabre. Com o app aberto como administrador (Reiniciar como administrador), o Start.bat deve parar com o aviso de que nao consegue fecha-lo, sem compilar. |

## Apendice: casos por classe

### AccessDeniedOperationTests (AccessDeniedOperationTests.cs)

- Compressing_into_a_folder_that_refuses_new_files_is_denied_in_portuguese
- Copying_into_a_folder_that_refuses_new_files_is_denied_in_portuguese
- Creating_a_folder_where_it_is_not_allowed_is_denied_in_portuguese
- Deleting_a_folder_with_a_protected_file_inside_names_the_folder_and_a_path_inside_it
- Deleting_a_protected_file_permanently_is_denied_in_portuguese
- Moving_a_protected_file_is_denied_in_portuguese_and_keeps_it_in_place
- Renaming_a_protected_file_is_denied_in_portuguese

### ApplicationNameTests (InterfaceTests.cs)

- A_friendly_shell_name_is_kept_when_the_description_is_missing_the_same_or_already_listed
- A_raw_executable_name_from_the_shell_is_replaced_by_the_description_of_the_program
- Application_names_never_show_the_executable_extension (8 casos)
- Packaged_app_ids_are_told_apart_from_programs_and_paths (7 casos)

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
- A_subfolder_the_user_may_not_list_is_marked_from_the_parent_listing_without_opening_it
- An_entry_of_a_read_only_archive_cannot_be_deleted_and_the_archive_stays_intact
- An_entry_of_a_zip_can_be_deleted_and_leaves_the_rest
- Copying_a_file_leaves_the_original_and_never_overwrites_the_destination
- Copying_or_moving_into_an_archive_is_refused
- Creating_a_folder_makes_it_on_disk_and_shows_it_in_the_tree
- Creating_a_folder_that_already_exists_is_refused_and_does_not_touch_it
- Deleting_a_protected_item_reports_access_denied_in_portuguese_and_marked
- Deleting_nothing_asks_for_a_selection
- Deleting_permanently_removes_files_and_folders_and_updates_the_tree
- Folders_cannot_be_created_inside_an_archive
- Marking_favorites_from_the_pane_adds_and_removes_them
- Moving_a_file_takes_it_out_of_the_source
- Moving_out_of_a_read_only_archive_only_copies_and_the_archive_keeps_the_entry
- Moving_out_of_a_zip_extracts_the_entry_and_removes_it_from_the_zip
- Navigating_to_a_file_leaves_the_pane_in_the_folder_that_holds_it
- Navigating_to_another_folder_changes_where_the_pane_is
- Only_files_that_can_really_be_deleted_are_offered_for_deletion
- Only_paths_that_exist_on_this_computer_count_as_local_paths
- Opening_a_folder_the_user_may_not_list_marks_the_node_instead_of_showing_it_as_plainly_empty
- Renaming_a_file_renames_it_on_disk_and_follows_it_in_the_selection
- Renaming_onto_a_name_that_is_taken_is_refused_and_changes_nothing
- Renaming_with_nothing_selected_asks_for_a_selection
- Selecting_a_subfolder_the_user_may_not_list_shows_no_permission_in_the_status_bar
- Sorting_by_name_descending_reorders_what_is_listed
- The_mark_goes_away_when_the_subfolder_can_be_listed_again
- The_new_folder_shortcut_creates_Nova_pasta_when_the_name_is_free
- The_new_folder_shortcut_finds_the_first_free_number_among_folders_and_files
- The_new_folder_shortcut_skips_a_file_that_is_called_Nova_pasta
- The_new_folder_shortcut_skips_a_Nova_pasta_that_already_exists
- The_pane_opens_on_the_initial_folder
- The_status_bar_of_a_selected_folder_follows_the_lock_mark
- The_tree_lists_folders_before_files_and_everything_that_is_on_disk

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

### LegacyPasswordMigrationTests (SecurityTests.cs)

- Already_protected_file_is_left_untouched
- Clear_text_passwords_are_protected_on_startup_and_still_readable
- Saving_leaves_no_temporary_file_behind
- Unreadable_settings_are_kept_aside_instead_of_being_overwritten

### LoginThrottleTests (SshServerTests.cs)

- A_good_login_clears_the_failures
- Blocks_after_the_limit_and_unblocks_after_the_penalty
- Old_failures_do_not_count

### MainWindowViewModelTests (MainWindowViewModelTests.cs)

- A_remembered_folder_that_no_longer_exists_falls_back_to_a_drive_instead_of_failing
- A_transfer_takes_the_selection_of_the_active_pane_to_the_folder_of_the_other
- A_transfer_without_a_selection_says_what_is_missing
- Activating_the_right_pane_swaps_source_and_destination_and_tells_the_screen
- Copy_and_paste_puts_a_copy_in_the_other_pane_and_keeps_the_clipboard
- Cut_and_paste_moves_the_file_and_empties_the_clipboard
- Dropping_files_on_the_other_pane_copies_or_moves_them_there
- Dropping_onto_the_same_folder_is_refused_without_touching_the_file
- Panes_open_where_they_were_left_when_remembering_the_path_is_on_and_the_folder_still_exists
- Pasting_with_nothing_staged_reports_it
- Staging_without_a_selection_names_the_action_that_was_asked_for
- The_chosen_sort_order_applies_to_both_panes
- The_left_pane_starts_active_and_the_right_one_is_the_destination
- The_pane_with_a_selection_is_the_one_used_and_the_active_one_wins_a_tie
- Transfers_are_validated_before_anything_is_touched
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

