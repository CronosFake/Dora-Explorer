# Dora Explorer - Mapa do Projeto

> Auto-gerado por scripts/generate-project-map.ps1 (o Start.bat roda a cada execucao). Nao edite a mao.
> Regras, comandos e armadilhas: AGENTS.md. Visao do produto e limitacoes conhecidas: README.md.
> Ultima atualizacao: 2026-11-06 23:40

## Como usar este mapa

1. Ache a funcionalidade em "Mapa funcional" e va direto aos tipos citados.
2. Arquivos em "Arquivos grandes" nao devem ser lidos inteiros: abra project-metodos.md (metodos com numero de linha), ache o metodo e leia so o trecho (Read com offset/limit).
3. O indice abaixo mostra tipos e metodos publicos; para qualquer outro nome, Grep.

## Visao geral

| Item | Valor |
|------|-------|
| Tipo | Gerenciador de arquivos Windows de dois paineis (releitura do X-plore) |
| Projeto | src/DoraExplorer.App/DoraExplorer.App.csproj |
| Framework | net8.0-windows (WPF), MVVM com CommunityToolkit.Mvvm |
| Executar | Start.bat -> build/compile/DoraExplorer.App.exe |
| Testes | dotnet test DoraExplorer.sln -c Release (tests/DoraExplorer.App.Tests, xUnit + FluentAssertions) |

## Estrutura de pastas

```
src/DoraExplorer.App/
  App.xaml.cs, MainWindow.xaml(.cs)   # inicializacao, DI, janela principal com dois paineis
  Assets/           0 arquivos      0 linhas  # Icones
  Contracts/       12 arquivos    339 linhas  # Interfaces dos servicos (DI e testes)
  Controls/        15 arquivos   8565 linhas  # Controles WPF: paineis, toolbar, arvore, animacoes
  Converters/       7 arquivos    187 linhas  # IValueConverter do WPF
  Infrastructure/  25 arquivos   1905 linhas  # DI, caminhos seguros, timeouts, instancia unica, colecao em bloco, enxugador de memoria
  Models/          29 arquivos    851 linhas  # DTOs, enums e opcoes
  Resources/        6 arquivos   1540 linhas  # Temas e estilos XAML
  Services/        71 arquivos  23953 linhas  # Arquivos, rede, indice, tema, icones, logs, arquivos compactados
  ViewModels/      12 arquivos   7514 linhas  # MVVM (CommunityToolkit.Mvvm)
  Views/           23 arquivos   2570 linhas  # Dialogos do menu central
tests/DoraExplorer.App.Tests/   # testes xUnit
scripts/                        # stop-dora.ps1, generate-project-map.ps1
scripts/dev/                    # ui-capture.ps1 (roteiros na tela), bench.ps1 (medicao), make-test-folder.ps1
```

## Mapa funcional -> onde alterar

Itens com (?) apontam para algo que nao existe mais no codigo: atualize a lista em scripts/generate-project-map.ps1.

| Funcionalidade | Onde alterar |
|----------------|--------------|
| Arvore unificada (pastas e arquivos num TreeView) | NavigationTreeService, NavigationTreeNodeViewModel, FilePaneControl, TreeViewHierarchy, TreeHierarchyGuides |
| Abrir e fechar pasta (expandir/recolher, acordeao) | FilePaneViewModel.OnNavigationNodeExpanding, FilePaneViewModel.OnNavigationNodeExpanded, FilePaneViewModel.OnNavigationNodeCollapsed, FilePaneControl.ExpandNode, FilePaneControl.BeginAnimatedCollapse, NavigationTreeNodeViewModel.ReloadOnNextExpand |
| Carregar listagem de uma pasta | NavigationTreeService.LoadDirectoryChildren, NavigationTreeService.StartChunkedFill, NavigationTreeService.ScheduleBackgroundRefresh |
| Reabrir pasta sem piscar (revalidar e aplicar so a diferenca) | NavigationTreeService.TryRevalidateLoadedDirectory, NavigationTreeService.ApplyBackgroundResults, NavigationTreeService.RebuildChildrenFromEntries, RangeObservableCollection.SyncTo |
| Pastas enormes e memoria | RangeObservableCollection.ReplaceAll, MemoryTrimmer, TreeContainers, DirectoryIndexService |
| Animacao de abrir/fechar e rolagem | TreeExpansionRevealAnimator, SmoothScrollAnimator, TreeScrollHelper, WheelScroll |
| Caminho atual, breadcrumb, voltar/avancar/subir | FilePaneViewModel.NavigateToPath, FilePaneViewModel.RestorePath, BreadcrumbSegmentViewModel, NavigationHistory, PathNavigation |
| Selecao e atalhos de teclado | FilePaneControl.NavigationTree_PreviewKeyDown, FilePaneViewModel.SetSingleItemSelection, SelectionStatusFormatter |
| Menu Exibir (arvore) e opcoes por painel | NavigationTreeService.CreateVisibleRoots, PaneDisplayOptions, PaneDisplayMenuViewModel, PaneDisplaySettingsService |
| Menu Exibir (barra superior) | MainWindowViewModel, ExplorerViewSettingsService, ExplorerViewOptions |
| Copiar, Mover e Compactar (com progresso e cancelamento) | MainWindowViewModel.ExecuteTransferAsync, MainWindowViewModel.ExecuteCompressAsync, FileOperationService.TryCopy, FileOperationService.TryCompress, NativeFileCopy, TransferProgressDialog, TransferProgressFormatter, OperationTimeouts |
| Nova pasta, Renomear, Excluir, Lixeira | CenterToolbarViewModel, FilePaneViewModel, FileOperationService, DeleteChoiceDialog, RecycleBinService |
| Aplicativo padrao ao lado do arquivo e "Abrir com" | FileAssociationService, DefaultApplicationOverrides, FilePaneControl.DefaultAppButton_Click, FileOpenService.TryShowNativeOpenWithDialog |
| Localizar arquivos (menu, colunas ajustaveis e Abrir em Janela) | CenterToolbarViewModel.RunSearchWithPattern, FileSearchService, SearchDialog, SearchPopupSizing, CenterToolbarControl.SearchResizeGrip_DragDelta, CenterToolbarControl.SearchNewWindow_Click, Resources/Theme.xaml |
| Duplicados (Pasta > Duplicados: padrao e profundo) | DuplicateFinderService, DuplicateWorkers, StorageKind, DuplicateFinderService.IsSystemFolder, DuplicatesViewModel, DuplicateSorter, DuplicateText, DuplicatesDialog, CenterToolbarViewModel.TryCreateDuplicates, CenterToolbarControl.DuplicatesMode_Click, Resources/CenterToolbarIcons.xaml |
| Arrastar e soltar (onde cai, destaque da pasta de destino, soltar na raiz) | FilePaneControl.FindSelectableNodeAtHostY, RowHitTest, FilePaneControl.TreeHost_DragOver, FilePaneViewModel.SetDropTarget, FilePaneViewModel.TryResolveDropDirectory, NavigationTreeNodeViewModel, MainWindowViewModel.ExecuteDropTransferAsync |
| Renomear (F2, clique devagar, nome sem a extensao, mouse na caixa) | FilePaneViewModel.BeginRenameNode, FilePaneViewModel.CanBeginSlowRename, FilePaneViewModel.SelectRenamedItem, FilePaneControl.FocusAfterRename, RenameSelection, SystemMouse, FilePaneControl.FocusRenameBox, FilePaneControl.StartSlowRename, FilePaneControl.IsInsideRenameEditor |
| Menu da Lixeira (botao direito na linha: esvaziar, restaurar tudo) | FilePaneControl.BuildRecycleBinRootContextMenu, FilePaneViewModel.TryEmptyRecycleBinAsync, FilePaneViewModel.TryRestoreAllRecycleBinItems, MainWindowViewModel.EmptyRecycleBinAsync, RecycleBinService.TryEmpty, RecycleBinText |
| Atualizar so o que o painel mostra depois de copiar, mover e compactar | FilePaneViewModel.RefreshDirectoryIfShown, FilePaneViewModel.RefreshShownDirectories, MainWindowViewModel.RefreshShownAfterTransfer |
| Ordenacao (menu e cabecalho das colunas) | CenterToolbarViewModel.ApplySortOptions, TreeEntrySorter, SortHeaderLogic, TreeSortOptions |
| Favoritos | FavoritesService |
| Arquivos compactados (ZIP, TAR, RAR) como pastas | ArchiveService, ArchivePathHelper |
| Unidades e particoes | VolumeEnumerationService, LocalFileSystemService |
| Icones e miniaturas | ShellIconService, FileThumbnailService |
| Cliente de rede (FTP, SFTP, SCP, TFTP, SMB, NFS) | NetworkFileService, NetworkSessionStore, NetworkClientDialog, NetworkClientHealthService, TftpClient, HostKeyVerifier, KnownHostsStore, FtpCertificateVerifier, NetworkKinds, RemoteSessionFactory |
| Servidores embutidos (FTP, TFTP) | NetworkServerService, ProtocolServerBase, FtpServer, TftpServer, FtpTlsCertificate, NetworkPortGuard, NetworkConnectionDialog |
| SMB (cliente e servidor, SMBLibrary) | SmbRemoteSession, SmbFileServer, SmbDirectoryFileStore, SmbLibraryAccess, SmbWildcard, ShareNames |
| NFS v3 (cliente e servidor proprios: ONC RPC, XDR, MOUNT, portmap) | NfsClient, NfsRemoteSession, NfsServer, NfsHandleTable, NfsProtocol, RpcClient, Rpc, XdrWriter, XdrReader, RpcIdentity, NetworkAddressPolicy |
| Icones dos protocolos de rede na arvore (SMB janela, NFS pinguim, FTP, SFTP, SCP, TFTP) e do SMB e NFS no menu central | NetworkProtocolIcons, CenterToolbarButton, NavigationTreeService.CreateNetworkGroup, Resources/CenterToolbarIcons.xaml |
| Tema claro/escuro e cor de destaque | ThemeManager, ExplorerThemeResources, WindowsAccentColorService, Resources/Theme.xaml, Resources/DarkTheme.xaml, Resources/LightTheme.xaml |
| Configuracoes e senhas | AppSettingsService, ProtectedStringJsonConverter, RememberPathSettingsService (settings.json em %AppData%/DoraExplorer; senhas cifradas com DPAPI) |
| Registro de servicos (DI) | ServiceRegistration.AddDoraExplorerServices |
| Instancia unica (impede abrir duas vezes) | SingleInstanceGuard |
| Logs | AppLogService (pasta logs/ na raiz do repositorio) |

## Fluxo de arranque

```
App.OnStartup
  -> SingleInstanceGuard.TryAcquire (outra instancia aberta: avisa e sai)
  -> AppLogService.Initialize + handlers globais de excecao
  -> Host/DI (ServiceRegistration.AddDoraExplorerServices)
  -> AppSettingsService (migra senhas antigas), FileAssociationService.UsePersistence, ThemeManager,
     ExplorerViewSettings/PaneDisplaySettings/RememberPathSettings, DirectoryIndexService.ScheduleMaintenance
  -> servicos de rede comecam parados (ClearAll/StopAll) e a checagem de saude inicia
  -> MainWindow (DataContext = MainWindowViewModel; cada painel = FilePaneViewModel)
```

## Pacotes NuGet

CommunityToolkit.Mvvm 8.4.2, FluentFTP 51.1.0, Microsoft.Data.Sqlite 10.0.14, Microsoft.DevTunnels.Ssh 3.12.42, Microsoft.Extensions.DependencyInjection 8.0.1, Microsoft.Extensions.Hosting 8.0.1, Serilog 4.2.0, Serilog.Sinks.File 6.0.0, SharpCompress 0.49.1, SMBLibrary [1.5.8.1], SSH.NET 2026.0.0, System.Management 9.0.0, System.Security.Cryptography.ProtectedData 8.0.0

## Arquivos grandes (>= 600 linhas): ler por trechos

Metodos com numero de linha: project-metodos.md.

- `src/DoraExplorer.App/Controls/FilePaneControl.xaml.cs` - 4183 linhas
- `src/DoraExplorer.App/ViewModels/FilePaneViewModel.cs` - 3849 linhas
- `src/DoraExplorer.App/Services/NavigationTreeService.cs` - 1721 linhas
- `src/DoraExplorer.App/Services/FtpServer.cs` - 1588 linhas
- `src/DoraExplorer.App/Services/NfsServer.cs` - 1428 linhas
- `src/DoraExplorer.App/Controls/FilePaneControl.xaml` - 1161 linhas
- `src/DoraExplorer.App/Resources/Theme.xaml` - 1132 linhas
- `src/DoraExplorer.App/Services/ArchiveService.cs` - 1111 linhas
- `src/DoraExplorer.App/Services/FileAssociationService.cs` - 1011 linhas
- `src/DoraExplorer.App/Services/SmbDirectoryFileStore.cs` - 998 linhas
- `src/DoraExplorer.App/Services/SftpServerSession.cs` - 945 linhas
- `src/DoraExplorer.App/Services/FileOperationService.cs` - 929 linhas
- `src/DoraExplorer.App/ViewModels/MainWindowViewModel.cs` - 861 linhas
- `src/DoraExplorer.App/Services/DirectoryIndexService.cs` - 754 linhas
- `src/DoraExplorer.App/Services/ScpServerSession.cs` - 712 linhas
- `src/DoraExplorer.App/ViewModels/NavigationTreeNodeViewModel.cs` - 683 linhas
- `src/DoraExplorer.App/Services/TftpServer.cs` - 680 linhas
- `src/DoraExplorer.App/Services/NetworkFileService.cs` - 647 linhas
- `src/DoraExplorer.App/Services/RecycleBinService.cs` - 615 linhas

## Indice de arquivos fonte

Formato: arquivo (linhas) - tipos. Metodos: so os publicos, ate 12 (30 nos arquivos grandes).

- `App.xaml` (11)
- `App.xaml.cs` (176) - **App**
- `AssemblyInfo.cs` (13)
- `Contracts/IArchiveService.cs` (28) - **IArchiveService**
- `Contracts/IDirectoryIndexService.cs` (25) - **IDirectoryIndexService**
- `Contracts/IDuplicateFinderService.cs` (15) - **IDuplicateFinderService**
- `Contracts/IElevationService.cs` (17) - **IElevationService**
- `Contracts/IFileOperationService.cs` (64) - **IFileOperationService**
- `Contracts/IFileSearchService.cs` (12) - **IFileSearchService**
- `Contracts/ILocalFileSystemService.cs` (10) - **ILocalFileSystemService**
- `Contracts/INavigationTreeService.cs` (49) - **INavigationTreeService**
- `Contracts/INetworkFileService.cs` (23) - **INetworkFileService**
- `Contracts/INetworkServerService.cs` (18) - **INetworkServerService**
- `Contracts/INetworkSessionStore.cs` (16) - **INetworkSessionStore**
- `Contracts/IRemoteFileService.cs` (62) - **IRemoteFileService**
- `Controls/CenterToolbarButton.xaml` (134)
- `Controls/CenterToolbarButton.xaml.cs` (147) - **CenterToolbarButton**
- `Controls/CenterToolbarControl.xaml` (458)
- `Controls/CenterToolbarControl.xaml.cs` (269) - **CenterToolbarControl**
- `Controls/FilePaneControl.xaml` (1161)
- `Controls/FilePaneControl.xaml.cs` (4183) - **FilePaneControl**
- `Controls/SearchPopupSizing.cs` (9) - **SearchPopupSizing**
  - Metodos: Clamp
- `Controls/SmoothScrollAnimator.cs` (354) - **SmoothScrollAnimator**
  - Metodos: HandleMouseWheel, ScrollTo, SyncTarget
- `Controls/StickyToViewport.cs` (188) - **StickyLayout, StickyToViewport**
  - Metodos: GetIsSticky, SetIsSticky, Resolve
- `Controls/ThumbnailHoverPreview.cs` (192) - **ThumbnailHoverPreview**
  - Metodos: Attach
- `Controls/TreeContainers.cs` (56) - **TreeContainers**
  - Metodos: Realized
- `Controls/TreeExpansionRevealAnimator.cs` (396) - **TreeExpansionRevealAnimator**
  - Metodos: IsCollapsing, CancelCollapse, CollapseChildren, HideForReveal, ShowAfterReveal, RevealChildren, Reset
- `Controls/TreeHierarchyGuides.cs` (246) - **TreeHierarchyGuides**
- `Controls/TreeScrollHelper.cs` (542) - **TreeRowMetrics, TreeScrollHelper**
  - Metodos: EnsureItemVisible, CenterItemInViewport, TryCenterItemInViewport, HorizontalWheelDelta, HorizontalWheelOffset, TryCenterSearchItem, TryBringChildIntoView, TryJumpUsingRealizedSibling, TryEstimateTargetOffset, WheelRowHeight, JumpScrollNearTarget, GetEstimatedRowHeight (+1)
- `Controls/TreeViewHierarchy.cs` (230) - **TreeViewHierarchy**
  - Metodos: SpineX, ExpanderLeftX, IconLeftX, GetItemContentMargin, SetItemContentMargin, GetDepth, SetDepth, GetIsLastSibling, SetIsLastSibling, GetIsFirstSibling, SetIsFirstSibling, GetAncestorContinuations (+4)
- `Converters/AdaptiveModifiedDateConverter.cs` (22) - **AdaptiveModifiedDateConverter**
- `Converters/BoolAndToVisibilityConverter.cs` (24) - **BoolAndToVisibilityConverter**
- `Converters/BoolToVisibilityConverter.cs` (24) - **BoolToVisibilityConverter**
- `Converters/DateTimeConverter.cs` (19) - **DateTimeConverter**
- `Converters/FileSizeConverter.cs` (34) - **FileSizeConverter**
- `Converters/NameCellMinWidthConverter.cs` (27) - **NameCellMinWidthConverter**
- `Converters/NameRowContentConverter.cs` (37) - **NameRowContentConverter**
- `GlobalUsings.cs` (1)
- `Infrastructure/ArchivePathHelper.cs` (248) - **ArchivePathHelper**
  - Metodos: TryGetFormat, IsArchiveFile, SupportsRewrite, TrySplit, IsVirtualArchivePath, IsInsideOrIsArchive, Combine, NormalizeEntryPath, TryResolveSafeDestination, GetEntryFileName, GetEntryParent
- `Infrastructure/DragPolicy.cs` (58) - **DragPolicy**
  - Metodos: AllowedEffects, IsCursorOverOwnWindow, IsLeftButtonDown
- `Infrastructure/ElevationPrompt.cs` (41) - **ElevationPromptKind, ElevationPrompt**
  - Metodos: For, OfferText, AlreadyElevatedText
- `Infrastructure/ExpectedFailures.cs` (52) - **ExpectedFailures**
  - Metodos: IsFileSystem, IsLockedVolume, IsShellInterop, IsIndexStore
- `Infrastructure/FileErrorText.cs` (189) - **FileErrorText**
  - Metodos: DisplayPath, Describe, AccessDenied, AccessDeniedForSome, ListingDenied, IsAccessDenied, WithoutMarker, IsAccessDeniedCode
- `Infrastructure/MemoryTrimmer.cs` (44) - **MemoryTrimmer**
  - Metodos: ScheduleAfterLargeLoad, Dispose
- `Infrastructure/NameTextMeasurer.cs` (52) - **NameTextMeasurer**
  - Metodos: Measure
- `Infrastructure/NameWidthScanner.cs` (132) - **NameWidthScanner**
  - Metodos: HasColumnRows, WidestRowWidth
- `Infrastructure/NetworkProtocolIcons.cs` (214) - **NetworkProtocolIcons**
  - Metodos: TreeIconFor, Filled, Stroked
- `Infrastructure/OperationTimeouts.cs` (93) - **OperationTimeouts**
  - Metodos: CreateLinked, RunAsync, RunWithInactivityAsync
- `Infrastructure/OperationTracker.cs` (96) - **OperationTracker**
  - Metodos: Begin, CancelAllAsync, Dispose
- `Infrastructure/PaneColumnWidths.cs` (94) - **PaneColumnWidths**
  - Metodos: ClampName, AfterNameDrag, FixedColumnsWidth, NameOverhead, NameDragStart, NameRowContentWidth, NameCellMinWidth
- `Infrastructure/PathNavigation.cs` (52) - **PathNavigation**
  - Metodos: GetParentPath
- `Infrastructure/PathSafetyService.cs` (40) - **PathSafetyService**
  - Metodos: IsSubPath
- `Infrastructure/ProtectedStringJsonConverter.cs` (52) - **ProtectedStringJsonConverter**
  - Metodos: Read, Write
- `Infrastructure/RangeObservableCollection.cs` (83) - **RangeObservableCollection**
  - Metodos: SyncTo, ReplaceAll
- `Infrastructure/RenameSelection.cs` (26) - **RenameSelection**
  - Metodos: GetRange
- `Infrastructure/RowHitTest.cs` (39) - **RowHitTest**
  - Metodos: FindExact
- `Infrastructure/ServiceRegistration.cs` (35) - **ServiceRegistration**
  - Metodos: AddDoraExplorerServices
- `Infrastructure/SingleInstanceGuard.cs` (48) - **SingleInstanceGuard**
  - Metodos: TryAcquire, Dispose
- `Infrastructure/StartupArguments.cs` (47) - **StartupArguments**
  - Metodos: BuildRestartArguments, TryGetParentProcessId, WaitForParentExit
- `Infrastructure/StorageKind.cs` (84) - **StorageKind**
  - Metodos: HasSeekPenalty
- `Infrastructure/SystemMouse.cs` (26) - **SystemMouse**
- `Infrastructure/TransferAccessDeniedException.cs` (17) - **TransferAccessDeniedException**
- `Infrastructure/WheelScroll.cs` (43) - **WheelScroll**
  - Metodos: StepPixels, RowHeightAtOffset
- `MainWindow.xaml` (139)
- `MainWindow.xaml.cs` (79) - **MainWindow**
- `Models/AccentColorOption.cs` (65) - **AccentColorOption, AccentColorPalette**
- `Models/AppThemeMode.cs` (8) - **AppThemeMode**
- `Models/ArchiveFormat.cs` (11) - **ArchiveFormat**
- `Models/AssociatedApplication.cs` (10) - **AssociatedApplication**
- `Models/DefaultApplicationEntry.cs` (9) - **DefaultApplicationEntry**
- `Models/DuplicateModels.cs` (91) - **DuplicateMode, DuplicateScanRequest, DuplicateFile, DuplicateGroup, DuplicateScanResult, DuplicateScanProgress, DuplicateScanPhase**
- `Models/ExplorerViewOptions.cs` (20) - **ExplorerViewOptions**
- `Models/FileDeleteMode.cs` (7) - **FileDeleteMode**
- `Models/FileOperationProgress.cs` (18) - **FileOperationProgress**
- `Models/FileSearchResults.cs` (8) - **FileSearchResults**
- `Models/FileSystemEntry.cs` (33) - **FileSystemEntry**
- `Models/FileTransferOperation.cs` (7) - **FileTransferOperation**
- `Models/ItemProperties.cs` (39) - **ItemProperties, ExecutableDetails, DriveDetails, FolderTotals, FileHashes**
- `Models/LocalNetworkAddressOption.cs` (12) - **LocalNetworkAddressOption**
- `Models/NavigationNodeKind.cs` (20) - **NavigationNodeKind**
- `Models/NetworkClientProfile.cs` (76) - **NetworkClientProfile**
- `Models/NetworkConnectionInfo.cs` (43) - **NetworkConnectionInfo**
- `Models/NetworkKinds.cs` (77) - **NetworkKinds**
- `Models/NetworkProtocol.cs` (11) - **NetworkProtocol**
- `Models/NetworkProtocolSettings.cs` (160) - **NetworkProtocolSettings, NetworkSettingsStore, NetworkDialogAction**
- `Models/NetworkSecurity.cs` (30) - **FtpServerSecurity, FtpClientSecurity**
- `Models/PaneDisplayOptions.cs` (25) - **PaneDisplayOptions**
- `Models/SearchListingEntry.cs` (3) - **SearchListingEntry**
- `Models/SearchResultItem.cs` (12) - **SearchResultItem**
- `Models/StorageUsage.cs` (22) - **StorageUsageLevel, StorageUsage**
- `Models/TransferOutcome.cs` (4) - **TransferOutcome**
- `Models/TreeSortField.cs` (9) - **TreeSortField**
- `Models/TreeSortMode.cs` (12) - **TreeSortMode**
- `Models/TreeSortOptions.cs` (9) - **TreeSortOptions**
- `Resources/CenterToolbarIcons.xaml` (28)
- `Resources/DarkTheme.xaml` (24)
- `Resources/LightTheme.xaml` (24)
- `Resources/MenuStyles.xaml` (204)
- `Resources/ScrollBarStyles.xaml` (128)
- `Resources/Theme.xaml` (1132)
- `Services/AdministratorStartupService.cs` (96) - **AdministratorStartupService**
  - Metodos: Load, TryEnable, Disable, TryElevateOnStartup
- `Services/AnimationFrameRateService.cs` (14) - **AnimationFrameRateService**
- `Services/AppLogService.cs` (116) - **AppLogService**
  - Metodos: Initialize, Shutdown, Info, Warning, Error
- `Services/AppSettingsService.cs` (380) - **AppSettingsService**
  - Metodos: ProtectLegacyPasswords, LoadThemeMode, LoadAccentColorHex, LoadExplorerViewOptions, LoadLeftPaneDisplayOptions, LoadRightPaneDisplayOptions, LoadRememberPathSettings, LoadStartAsAdministrator, SaveStartAsAdministrator, SaveThemeMode, SaveAccentColorHex, SaveExplorerViewOptions (+15)
- `Services/ArchiveService.cs` (1111) - **ArchiveService**
  - Metodos: TryListEntries, TryExtractEntry, TryRemoveEntries, TryExtractEntryToTempAndOpen
- `Services/DefaultApplicationOverrides.cs` (111) - **DefaultApplicationOverrides**
  - Metodos: Normalize, TryGet, Set, Clear
- `Services/DirectoryIndexService.cs` (754) - **DirectoryIndexService**
  - Metodos: TryGetEntries, ReadFromDiskAndQueueStore, TryGetCounts, ComputeCountsFromDisk, WasAccessDenied, IsIndexedAndCurrent, TryGetSearchListing, Invalidate, ScheduleMaintenance, Dispose
- `Services/DuplicateFinderService.cs` (497) - **DuplicateFinderService**
  - Metodos: IsDriveRoot, IsSystemFolder, FindAsync, Find
- `Services/DuplicateWorkers.cs` (30) - **DuplicateWorkers**
  - Metodos: Choose, ForPath
- `Services/ElevationService.cs` (97) - **ElevationService**
  - Metodos: TryRestartElevated, IsProcessElevated
- `Services/ExplorerThemeResources.cs` (253) - **ExplorerThemeResources**
  - Metodos: Create
- `Services/ExplorerViewSettingsService.cs` (73) - **ExplorerViewSettingsService**
  - Metodos: Load, SetShowHiddenFiles, SetShowSystemFiles, SetShowExternalDrives, SetShowLockedDrives, SetShowUnmappedPartitions, SetShowDefaultApplication, SetCompactRows
- `Services/FavoritesService.cs` (310) - **FavoritesService**
  - Metodos: HasItems, IsFavorite, GetCounts, GetEntries, TryAdd, TryRemove, TryToggle
- `Services/FileAssociationService.cs` (1011) - **FileAssociationService**
  - Metodos: GetDefaultApplicationName, GetAssociatedApplications, UsePersistence, SetUserOverride, ClearUserOverride, HasUserOverride, TryGetUserOverrideExecutable, IsLaunchable, GetDisplayNameForExecutable
- `Services/FileOpenService.cs` (171) - **FileOpenService**
  - Metodos: TryOpen, TryShowNativeOpenWithDialog, OpenFiles
- `Services/FileOperationService.cs` (929) - **FileOperationService**
  - Metodos: TryCreateDirectory, TryRename, TryCopy, TryMove, TryCopyAsync, TryMoveAsync, TryCompressAsync, TryDelete, TrySendToRecycleBin, TryDeletePermanently, TryCompress
- `Services/FileSearchService.cs` (259) - **FileSearchService**
  - Metodos: Search, TryAddResult
- `Services/FileThumbnailService.cs` (214) - **FileThumbnailService**
  - Metodos: CanLoadThumbnail, TryLoadThumbnail, TryLoadThumbnailAsync
- `Services/FtpCertificateVerifier.cs` (108) - **FtpCertificateVerifier**
  - Metodos: Attach, Dispose
- `Services/FtpServer.cs` (1588) - **FtpServer**
  - Metodos: Start, RunAsync
- `Services/FtpTlsCertificate.cs` (107) - **FtpTlsCertificate**
  - Metodos: GetOrCreate
- `Services/HostKeyVerifier.cs` (162) - **HostKeyDecision, HostKeyVerifier**
  - Metodos: BeginNonInteractive, Decide, Connect, Dispose
- `Services/ItemPropertiesService.cs` (417) - **ItemPropertiesService**
  - Metodos: Read, ReadDrive, ComputeTotalsAsync, ComputeSha256Async, ComputeHashesAsync, TypeDescriptionOf, SizeOnDisk, ClusterSizeOf
- `Services/KnownHostsStore.cs` (79) - **KnownHostEntry, KnownHostsStore**
  - Metodos: Key, TryGet, Save
- `Services/LocalFileSystemService.cs` (87) - **LocalFileSystemService**
  - Metodos: GetEntries, GetDirectoryChildren
- `Services/LocalNetworkAddressService.cs` (86) - **LocalNetworkAddressService**
  - Metodos: GetSortedIpv4Addresses
- `Services/LoginThrottle.cs` (50) - **LoginThrottle**
  - Metodos: IsBlocked, RecordFailure, RecordSuccess
- `Services/NativeFileCopy.cs` (366) - **NativeFileCopy, TransferProgressTracker, TransferScanner, TransferProgressAggregator, ProgressReadStream**
  - Metodos: CopyFile, MoveFile, SetTotals, BeginFile, FileProgress, EndFile, Complete, Heartbeat, Scan, ScanItem, CompleteItem, Read (+4)
- `Services/NativeFolderPicker.cs` (157) - **NativeFolderPicker**
  - Metodos: TryPickFolder
- `Services/NavigationHistory.cs` (68) - **NavigationHistory**
  - Metodos: Visit, Back, Forward
- `Services/NavigationTreeService.cs` (1721) - **NavigationTreeService**
  - Metodos: CreateVisibleRoots, TryMaterializeChild, TryMaterializeDirectoryByName, LoadDirectoryChildren, RefreshCollapsedDirectoryPlaceholder, CompleteChunkedFill, TryRevalidateLoadedDirectory, ResortDirectoryChildren
- `Services/NetworkClientHealthService.cs` (264) - **NetworkClientHealthService**
  - Metodos: Start, GetHealth, TestAndUpdateAsync, Remove, SetHealth, Dispose
- `Services/NetworkFileService.cs` (647) - **NetworkFileService**
  - Metodos: TryConnectAndList, TryExecuteTransfer, ConnectAndListAsync, ExecuteTransferAsync, TestConnectionAsync, BuildRemotePath, TryParseRemotePath
- `Services/NetworkPortGuard.cs` (271) - **NetworkPortGuard**
  - Metodos: IsPortUsedByOtherProcess, IsPortListening, ForceStopOwnedListeners
- `Services/NetworkServerBase.cs` (181) - **IProtocolServer, ProtocolServerBase, ServerPathGuard, PassivePortRange**
  - Metodos: Start, DisposeWithoutClosingExternalPort, Dispose, IsInside, TryParse
- `Services/NetworkServerLogStore.cs` (57) - **NetworkServerLogStore**
  - Metodos: Clear, Append, GetText
- `Services/NetworkServerService.cs` (240) - **NetworkServerService**
  - Metodos: IsRunning, GetRunningConfig, TryStart, StopServer, StopAll, Dispose
- `Services/NetworkSessionStore.cs` (99) - **NetworkSessionStore**
  - Metodos: Register, TryGetForPath, HasProtocol, UnregisterProtocol, ClearAll
- `Services/NfsClient.cs` (432) - **NfsClient**
  - Metodos: Connect, ListExports, Mount, Unmount, GetAttributes, Lookup, ReadDirectory, Read, Write, CreateOrTruncate, SetAttributes, MakeDirectory (+4)
- `Services/NfsProtocol.cs` (294) - **NfsStatus, NfsFileType, NfsException, NfsAttributes, NfsDirectoryEntry, NfsSetAttributes, NfsProtocol**
  - Metodos: From, Describe, FromNfsTime, ToNfsTime, ReadAttributes, ReadPostOpAttributes, SkipWccData, WriteAttributes, WriteSetAttributes, ReadSetAttributes
- `Services/NfsRemoteSession.cs` (350) - **NfsRemoteSession**
  - Metodos: Open, Stat, List, CreateDirectory, DeleteFile, DeleteEmptyDirectory, Rename, Download, Upload, Dispose
- `Services/NfsRpc.cs` (562) - **XdrException, RpcException, XdrWriter, XdrReader, RpcIdentity, Rpc, CallMessage, RpcClient**
  - Metodos: AsSpan, ToArray, UInt32, Int32, UInt64, Bool, Opaque, Fixed, String, Raw, OpaqueSpan, FixedSpan (+12)
- `Services/NfsServer.cs` (1428) - **NfsServer, NfsHandleTable, ShareNames, NetworkAddressPolicy**
  - Metodos: Start, IdFor, HandleFor, TryGetPath, FromFolder, IsLocalNetwork
- `Services/PaneDisplaySettingsService.cs` (27) - **PaneDisplaySettingsService**
  - Metodos: Load, SaveLeftPane, SaveRightPane
- `Services/RecycleBinService.cs` (615) - **RecycleBinService**
  - Metodos: HasItems, TryEmpty, GetAllItemPaths, GetCounts, GetEntries, IsRecycleBinDataPath, TryResolveOriginalPath, TryRestoreItems, TryDeletePermanently
- `Services/RecycleBinText.cs` (27) - **RecycleBinText**
  - Metodos: ConfirmEmpty, ConfirmRestoreAll
- `Services/RememberPathSettingsService.cs` (54) - **RememberPathSettingsService**
  - Metodos: Load, SetEnabled, SavePanePaths
- `Services/RemoteFileService.cs` (338) - **RemoteFileService**
  - Metodos: CreateDirectoryAsync, DeleteAsync, RenameAsync, DownloadAsync, UploadAsync, TransferBetweenAsync, DownloadForOpenAsync
- `Services/RemoteSession.cs` (218) - **RemoteEntry, IRemoteSession, RemoteSessionFactory, FtpRemoteSession, SftpRemoteSession**
  - Metodos: Stat, List, CreateDirectory, DeleteFile, DeleteEmptyDirectory, Rename, Download, Upload, Open, Dispose
- `Services/RemoteTransferEngine.cs` (356) - **RemoteTransferEngine**
  - Metodos: UniqueName, Scan, Download, Upload, Delete, Rename, Combine, ParentOf, LastSegment, SafeName
- `Services/ScpServerSession.cs` (712) - **ScpCommand, ScpServerSession**
  - Metodos: TryParse, RunAsync
- `Services/SelectionStatusFormatter.cs` (52) - **SelectionStatusFormatter**
  - Metodos: Format
- `Services/ServerVirtualPath.cs` (85) - **ServerVirtualPath**
  - Metodos: Normalize, TryResolve, IsSafeSegment
- `Services/SftpServerSession.cs` (945) - **SftpServerSession, SftpAttributes**
  - Metodos: RunAsync, UInt32, UInt64, BytesView, String, Attributes, SkipAttributes, Byte, Bytes, ToArray
- `Services/ShellIconService.cs` (457) - **ShellIconService**
  - Metodos: GetCachedFileIcon, GetRemoteItemIcon, GetNetworkLocationIcon, GetNetworkServerIcon, GetGenericFolderIcon, GetGenericFileIcon, GetFileIcon, GetDriveIcon, GetFolderIcon, GetNetworkIcon, GetSpecialIcon, GetRecycleBinIcon (+3)
- `Services/SmbDirectoryFileStore.cs` (998) - **SmbDirectoryFileStore, SmbWildcard**
  - Metodos: CreateFile, CloseFile, ReadFile, WriteFile, FlushFileBuffers, LockFile, UnlockFile, QueryDirectory, GetFileInformation, SetFileInformation, GetFileSystemInformation, SetFileSystemInformation, GetSecurityInformation, SetSecurityInformation, NotifyChange, Cancel, DeviceIOControl, IsMatch
- `Services/SmbFileServer.cs` (117) - **SmbFileServer**
  - Metodos: Start
- `Services/SmbLibraryAccess.cs` (124) - **SmbLibraryAccess**
  - Metodos: Connect, Resolve, Start, Describe
- `Services/SmbRemoteSession.cs` (463) - **SmbRemoteSession**
  - Metodos: Open, Stat, List, CreateDirectory, DeleteFile, DeleteEmptyDirectory, Rename, Download, Upload, Dispose
- `Services/SortHeaderLogic.cs` (29) - **SortHeaderLogic**
  - Metodos: Toggle, Indicator
- `Services/SshFileServer.cs` (449) - **SubsystemRequestMessage, SshFileServer**
  - Metodos: Start
- `Services/SshHostKeyStore.cs` (93) - **SshHostKeyStore**
  - Metodos: GetOrCreate, Fingerprint
- `Services/TftpClient.cs` (406) - **TftpClientOptions, TftpClient**
  - Metodos: Download, Upload, Probe, Dispose, Send, Expect, Exchange
- `Services/TftpServer.cs` (680) - **TftpServer**
  - Metodos: Start, SendAsync, ExchangeAsync
- `Services/ThemeManager.cs` (127) - **ThemeManager**
  - Metodos: Initialize, SetThemeMode, SetAccentColor, Dispose, ApplyTitleBar
- `Services/TransferProgressFormatter.cs` (67) - **TransferProgressFormatter**
  - Metodos: Title, CompressTitle, Describe, FormatDuration
- `Services/TreeEntrySorter.cs` (52) - **TreeEntrySorter**
  - Metodos: Sort
- `Services/VolumeEnumerationService.cs` (72) - **VolumeEnumerationService, UnmappedVolume**
  - Metodos: GetUnmappedVolumes
- `Services/WindowsAccentColorService.cs` (27) - **WindowsAccentColorService**
  - Metodos: GetAccentColor
- `Services/WindowsThemeService.cs` (21) - **WindowsThemeService**
  - Metodos: IsWindowsAppsLightTheme
- `Services/WindowsTitleBarService.cs` (95) - **WindowsTitleBarService**
  - Metodos: Apply, ApplyToAllWindows
- `ViewModels/AccentColorMenuItemViewModel.cs` (38) - **AccentColorMenuItemViewModel**
- `ViewModels/BreadcrumbSegmentViewModel.cs` (23) - **BreadcrumbSegmentViewModel**
- `ViewModels/CenterToolbarViewModel.cs` (586) - **CenterToolbarViewModel**
  - Metodos: RefreshNetworkServiceStatus, RefreshSearchContext, RunSearchWithPattern, OpenSearchResult, TryCreateDuplicates, LocateDuplicate, ApplySortOptions
- `ViewModels/DuplicatesViewModel.cs` (389) - **DuplicateRowViewModel, DuplicatesViewModel, DuplicateSortField, DuplicateSorter, DuplicateText**
  - Metodos: SortBy, StartAsync, Cancel, Sort, Size, Header, Row, Progress, Fraction, Summary, Finished
- `ViewModels/FileItemViewModel.cs` (72) - **FileItemViewModel**
- `ViewModels/FilePaneViewModel.cs` (3849) - **FilePaneViewModel**
  - Metodos: RestorePath, CompleteInitialPathRestore, ClearCenterNavigatedNode, ResizeNameColumn, ResetNameColumnWidth, RefreshColumnHeader, RefreshWidestNameRow, CommitColumnWidths, UpdateResponsiveLayout, OnNavigationNodeExpanding, OnNavigationNodeExpanded, OnNavigationNodeCollapsed, OnNavigationNodeSelected, NavigateToPath, RefreshForViewOptionsChange, BeginDisplayMenu, CommitDisplayMenu, ClearItemSelection, SetSingleItemSelection, ToggleItemSelection, HandleShiftItemSelection, SelectSiblingItems, ShareSelectionParent, GetSelectableSiblings, GetParentNode, GetVisibleSelectableNodes, SelectSiblingRange, HasOpenableFilesInSelection, GetPrimarySelectedNode, HasActivatableSelection (+57)
- `ViewModels/FilePaneViewModel.Remote.cs` (392) - **FilePaneViewModel**
- `ViewModels/ItemPropertiesViewModel.cs` (549) - **PropertyRowViewModel, PropertySectionViewModel, ItemPropertiesViewModel**
  - Metodos: Create, LoadAsync, Dispose, ComputeHashAsync
- `ViewModels/MainWindowViewModel.cs` (861) - **MainWindowViewModel**
  - Metodos: OppositePane, EmptyRecycleBinAsync, RestoreAllFromRecycleBin, GetPaneWithSelection, ApplyGlobalSortOptions, TryResolveTransfer, TryValidateTransfer, ExecuteDropTransferAsync, TryStageTransfer, TryPasteTransferAsync, ExecuteTransferAsync, ExecuteCompressAsync, ClearTransferClipboard, OnWindowLoaded, PersistPanePaths
- `ViewModels/NavigationTreeNodeViewModel.cs` (683) - **NavigationTreeNodeViewModel**
  - Metodos: SetName, SetIconFactory, SetDefaultApplication, RefreshDefaultApplication, SetHierarchyMetadata
- `ViewModels/PaneDisplayMenuViewModel.cs` (65) - **PaneDisplayMenuViewModel**
  - Metodos: LoadFrom, DiffersFrom, ApplyTo
- `ViewModels/PathRenamedEventArgs.cs` (7) - **PathRenamedEventArgs**
- `Views/Dialogs/AboutDialog.xaml` (59)
- `Views/Dialogs/AboutDialog.xaml.cs` (100) - **AboutDialog**
  - Metodos: Show
- `Views/Dialogs/DeleteChoiceDialog.xaml` (35)
- `Views/Dialogs/DeleteChoiceDialog.xaml.cs` (85) - **DeleteChoiceDialog**
  - Metodos: TryChoose, ConfirmShortcut, DescribeTargets
- `Views/Dialogs/DialogChrome.cs` (21) - **DialogChrome**
  - Metodos: Apply
- `Views/Dialogs/DuplicatesDialog.xaml` (210)
- `Views/Dialogs/DuplicatesDialog.xaml.cs` (165) - **DuplicatesDialog**
  - Metodos: Show
- `Views/Dialogs/MessageDialog.xaml` (48)
- `Views/Dialogs/MessageDialog.xaml.cs` (95) - **MessageDialog**
  - Metodos: Show, Confirm, Ask
- `Views/Dialogs/NetworkClientDialog.xaml` (141)
- `Views/Dialogs/NetworkClientDialog.xaml.cs` (352) - **NetworkClientDialog**
  - Metodos: TryShow
- `Views/Dialogs/NetworkConnectionDialog.xaml` (133)
- `Views/Dialogs/NetworkConnectionDialog.xaml.cs` (386) - **NetworkConnectionDialog**
  - Metodos: ShowOrActivate, SetRunningState, NotifyRunningState
- `Views/Dialogs/PromptDialog.xaml` (37)
- `Views/Dialogs/PromptDialog.xaml.cs` (44) - **PromptDialog**
  - Metodos: TryPrompt
- `Views/Dialogs/PropertiesDialog.xaml` (153)
- `Views/Dialogs/PropertiesDialog.xaml.cs` (80) - **PropertiesDialog**
  - Metodos: Show
- `Views/Dialogs/SearchDialog.xaml` (106)
- `Views/Dialogs/SearchDialog.xaml.cs` (90) - **SearchDialog**
  - Metodos: ShowOrActivate
- `Views/Dialogs/SortDialog.xaml` (43)
- `Views/Dialogs/SortDialog.xaml.cs` (39) - **SortDialog**
  - Metodos: TrySelect
- `Views/Dialogs/TransferProgressDialog.xaml` (43)
- `Views/Dialogs/TransferProgressDialog.xaml.cs` (105) - **TransferProgressDialog**
  - Metodos: ShowFor, CloseWhenDone, Update

## Testes (tests/DoraExplorer.App.Tests)

- `AccessDeniedOperationTests.cs` (159) - AccessDeniedOperationTests
- `AccessDenyScope.cs` (63) - AccessDenyScope
- `AdministratorStartupServiceTests.cs` (209) - AdministratorStartupServiceTests
- `AppVersionTests.cs` (54) - AppVersionTests
- `ArchiveListingTests.cs` (407) - ArchiveListingTests
- `DirectoryIndexTests.cs` (430) - DirectoryIndexTests
- `DragPolicyTests.cs` (38) - DragPolicyTests
- `DropTargetTests.cs` (236) - DropTargetTests
- `DuplicateFinderTests.cs` (751) - DuplicateFinderTests
- `DuplicatesViewModelTests.cs` (657) - DuplicatesViewModelTests
- `ElevationTests.cs` (196) - ElevationTests
- `FakeElevationService.cs` (28) - FakeElevationService
- `FavoritesServiceTests.cs` (204) - FavoritesServiceTests
- `FileErrorTextTests.cs` (171) - FileErrorTextTests
- `FileOperationTests.cs` (342) - FileOperationTests
- `FilePaneViewModelTests.cs` (985) - FilePaneViewModelTests
- `HealthServiceTests.cs` (186) - HealthServiceTests
- `InfrastructureTests.cs` (394) - PathSafetyServiceTests, NetworkFileServiceTests, FileOpenServiceTests, OperationTrackerTests, ExpectedFailuresTests, SqliteSecurityTests, MemoryTrimmerTests, RangeObservableCollectionTests
- `InterfaceTests.cs` (374) - StorageUsageTests, DeniedFolderRowTests, SelectionStatusFormatterTests, ThemeUsageColorsTests, ApplicationNameTests, DefaultApplicationOverridesTests
- `ItemPropertiesServiceTests.cs` (408) - ItemPropertiesServiceTests
- `ItemPropertiesViewModelTests.cs` (330) - ItemPropertiesViewModelTests
- `LockedDrivesOptionTests.cs` (86) - LockedDrivesOptionTests
- `MainMenuTests.cs` (88) - MainMenuTests
- `MainWindowViewModelTests.cs` (519) - MainWindowViewModelTests
- `NavigationTests.cs` (277) - SortHeaderLogicTests, NavigationHistoryTests, PathNavigationTests, RowDensitySettingTests, XamlResourceTests
- `NetworkClientTests.cs` (306) - FtpClientSecurityTests, SshAuthenticationTests
- `NetworkGroupTests.cs` (146) - AppStaticsGroup, NetworkGroupTests
- `NetworkKindsTests.cs` (234) - NetworkKindsTests, NetworkSharesSettingsTests, NetworkSharesXamlTests
- `NetworkPortGuardTests.cs` (316) - ExternalProcessesGroup, NetworkPortGuardTests
- `NetworkProtocolIconsTests.cs` (165) - NetworkProtocolIconsTests
- `NetworkServerTests.cs` (879) - FtpServerTests, TftpServerTests
- `NfsTests.cs` (776) - NfsXdrTests, NfsExportResolutionTests, NfsServerTests
- `PaneColumnsTests.cs` (1020) - PaneColumnsTests
- `PaneTestHarness.cs` (239) - PaneTestHarness
- `RecycleBinMenuTests.cs` (200) - RecycleBinMenuTests
- `RemoteFileTests.cs` (618) - FakeRemoteServer, CapturingProgress, RemoteTransferEngineTests, RemoteFileServiceTests, RemoteFileServiceFtpTests
- `RenameTests.cs` (282) - RenameTests
- `SearchNavigationTests.cs` (387) - SearchNavigationTests
- `SearchWindowTests.cs` (192) - SearchWindowTests
- `SecurityTests.cs` (665) - ArchiveExtractionPathTests, ArchiveServiceExtractionTests, ProtectedPasswordTests, LegacyPasswordMigrationTests, HostKeyTests, NetworkServerHardeningTests
- `ServiceRegistrationTests.cs` (69) - ServiceRegistrationTests
- `ShareTestSupport.cs` (94) - ShareTestSupport
- `ShellIconTests.cs` (158) - ShellIconTests
- `SmbTests.cs` (435) - SmbLibraryContractTests, SmbHelperTests, SmbServerTests
- `SolidArchiveTests.cs` (175) - SolidArchiveTests
- `SpecialFolderGuardTests.cs` (111) - SpecialFolderGuardTests
- `SshServerTests.cs` (1586) - ServerVirtualPathTests, LoginThrottleTests, ScpCommandTests, SftpServerSessionTests, ScpServerSessionTests, SshServerEndToEndTests, SshTestHelpers, PipePair, RawSftp, Reply
- `StopDoraScriptTests.cs` (194) - StopDoraScriptTests
- `TftpClientTests.cs` (194) - TftpClientTests
- `ThumbnailTests.cs` (197) - FileThumbnailServiceTests, ThumbnailHoverRulesTests
- `TransferTests.cs` (531) - SyncProgress, NativeFileCopyTests, FileOperationProgressTests, InactivityTimeoutTests, TransferProgressFormatterTests, CompressionProgressTests
- `WheelScrollTests.cs` (198) - WheelScrollTests, WheelRowHeightTests
