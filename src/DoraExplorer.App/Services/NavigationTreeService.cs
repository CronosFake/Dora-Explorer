using System.Windows.Media;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Services;

public sealed class NavigationTreeService : INavigationTreeService
{
    private static readonly TimeSpan DriveProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly VolumeEnumerationService _volumeEnumerationService = new();
    private readonly IDirectoryIndexService _index;
    private readonly ExplorerViewSettingsService _viewSettings;
    private readonly IArchiveService _archiveService;

    public NavigationTreeService(
        IDirectoryIndexService index,
        ExplorerViewSettingsService viewSettings,
        IArchiveService archiveService)
    {
        _index = index;
        _viewSettings = viewSettings;
        _archiveService = archiveService;
    }

    public const string RecycleBinPath = ShellIconService.RecycleBinParsingName;

    public const string FavoritesPath = "favorites:";

    string INavigationTreeService.RecycleBinPath => RecycleBinPath;

    string INavigationTreeService.FavoritesPath => FavoritesPath;

    public IEnumerable<NavigationTreeNodeViewModel> CreateVisibleRoots(
        PaneDisplayOptions options,
        ExplorerViewOptions viewOptions)
    {
        var drives = DriveInfo.GetDrives()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(d => ShouldIncludeDrive(d, viewOptions))
            .ToList();
        var snapshots = ReadDriveInfos(drives);

        for (var i = 0; i < drives.Count; i++)
        {
            var drive = drives[i];
            var (isReady, volumeLabel, freeBytes, totalBytes, unavailableReason) = snapshots[i];

            if (!ShouldShowDrive(viewOptions.ShowLockedDrives, unavailableReason))
            {
                continue;
            }

            yield return CreateDriveNode(
                drive.Name,
                string.IsNullOrWhiteSpace(volumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.Name.TrimEnd('\\')} ({volumeLabel})",
                isReady ? FormatDriveStorage(freeBytes, totalBytes) : unavailableReason,
                isReady && totalBytes > 0
                    ? 1.0 - (double)freeBytes / totalBytes
                    : 0,
                isReady);
        }

        if (viewOptions.ShowUnmappedPartitions)
        {
            foreach (var volume in _volumeEnumerationService.GetUnmappedVolumes())
            {
                var label = string.IsNullOrWhiteSpace(volume.Label)
                    ? "Partição sem letra"
                    : $"{volume.Label} (sem letra)";

                yield return CreateDriveNode(
                    volume.DevicePath,
                    label,
                    FormatDriveStorage(volume.FreeBytes, volume.CapacityBytes),
                    volume.CapacityBytes > 0
                        ? 1.0 - (double)volume.FreeBytes / volume.CapacityBytes
                        : 0,
                    isReady: true);
            }
        }

        if (options.ShowFavorites)
        {
            var favoritesNode = new NavigationTreeNodeViewModel(
                "Favoritos",
                NavigationNodeKind.Favorites,
                FavoritesPath,
                iconGlyph: "\uE735");

            if (FavoritesService.HasItems())
            {
                var (folders, files) = FavoritesService.GetCounts();
                favoritesNode.FolderCount = folders;
                favoritesNode.FileCount = files;
                favoritesNode.Children.Add(CreateLazyPlaceholder());
            }

            yield return favoritesNode;
        }

        if (options.ShowRecycleBin)
        {
            var recycleBinNode = new NavigationTreeNodeViewModel(
                "Lixeira",
                NavigationNodeKind.RecycleBin,
                RecycleBinPath)
            {
                IconSource = ShellIconService.GetRecycleBinIcon()
            };

            if (RecycleBinService.HasItems())
            {
                var (folders, files) = RecycleBinService.GetCounts();
                recycleBinNode.FolderCount = folders;
                recycleBinNode.FileCount = files;
                recycleBinNode.Children.Add(CreateLazyPlaceholder());
            }

            yield return recycleBinNode;
        }

        var networkGroup = CreateNetworkGroup(options);
        if (networkGroup is not null)
        {
            yield return networkGroup;
        }
    }

    public const string NetworkGroupName = "Rede";

    /// <summary>
    /// Pasta "Rede" com os protocolos ativos em <paramref name="options"/>; nula quando todos estão ocultos.
    /// </summary>
    internal static NavigationTreeNodeViewModel? CreateNetworkGroup(PaneDisplayOptions options)
    {
        var group = new NavigationTreeNodeViewModel(NetworkGroupName, NavigationNodeKind.NetworkGroup)
        {
            IconSource = ShellIconService.GetNetworkLocationIcon()
        };

        if (options.ShowFtp)
        {
            group.Children.Add(CreateNetworkNode("FTP", NavigationNodeKind.Ftp, NetworkProtocol.Ftp));
        }

        if (options.ShowSftp)
        {
            group.Children.Add(CreateNetworkNode("SFTP", NavigationNodeKind.Sftp, NetworkProtocol.Sftp));
        }

        if (options.ShowScp)
        {
            group.Children.Add(CreateNetworkNode("SCP", NavigationNodeKind.Scp, NetworkProtocol.Scp));
        }

        if (options.ShowTftp)
        {
            group.Children.Add(CreateNetworkNode("TFTP", NavigationNodeKind.Tftp, NetworkProtocol.Tftp));
        }

        if (options.ShowSmb)
        {
            group.Children.Add(CreateNetworkNode("SMB", NavigationNodeKind.Smb, NetworkProtocol.Smb));
        }

        if (options.ShowNfs)
        {
            group.Children.Add(CreateNetworkNode("NFS", NavigationNodeKind.Nfs, NetworkProtocol.Nfs));
        }

        if (group.Children.Count == 0)
        {
            return null;
        }

        group.ChildrenLoaded = true;
        return group;
    }

    /// <summary>
    /// Cria um filho a partir do índice se a pasta ainda está a ser preenchida em lotes
    /// (streaming) e o nó ainda não existe na coleção.
    /// </summary>
    public NavigationTreeNodeViewModel? TryMaterializeChild(
        NavigationTreeNodeViewModel parent,
        string childPath,
        ExplorerViewOptions view)
    {
        if (string.IsNullOrWhiteSpace(parent.TargetPath) || string.IsNullOrWhiteSpace(childPath))
        {
            return null;
        }

        var existing = parent.Children.FirstOrDefault(c =>
            !c.IsPlaceholder &&
            string.Equals(c.TargetPath, childPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var entries = _index.TryGetEntries(parent.TargetPath, view);
        var entry = entries?.FirstOrDefault(e =>
            string.Equals(e.FullPath, childPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var child = CreateChildNode(entry);
        parent.Children.Add(child);
        return child;
    }

    /// <summary>
    /// Materializa uma subpasta pelo nome quando o preenchimento em lotes ainda não a incluiu.
    /// </summary>
    public NavigationTreeNodeViewModel? TryMaterializeDirectoryByName(
        NavigationTreeNodeViewModel parent,
        string segmentName,
        ExplorerViewOptions view)
    {
        if (string.IsNullOrWhiteSpace(parent.TargetPath) || string.IsNullOrWhiteSpace(segmentName))
        {
            return null;
        }

        var existing = parent.Children.FirstOrDefault(c =>
            c.Kind == NavigationNodeKind.Directory &&
            !c.IsPlaceholder &&
            string.Equals(c.Name, segmentName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var entries = _index.TryGetEntries(parent.TargetPath, view);
        var entry = entries?.FirstOrDefault(e =>
            e.IsDirectory &&
            string.Equals(e.Name, segmentName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var child = CreateDirectoryNode(entry);
        parent.Children.Add(child);
        return child;
    }

    public void LoadDirectoryChildren(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService,
        TreeSortOptions sortOptions)
    {
        if (node.Kind == NavigationNodeKind.Favorites)
        {
            if (node.ChildrenLoaded)
            {
                return;
            }

            LoadFavoritesChildren(node, sortOptions);
            return;
        }

        if (node.ChildrenLoaded || string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        if (node.Kind == NavigationNodeKind.RecycleBin)
        {
            LoadRecycleBinChildren(node, sortOptions);
            return;
        }

        if (TryLoadArchiveChildren(node, sortOptions))
        {
            return;
        }

        try
        {
            var isNetwork = NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _);
            var generation = ++node.LoadGeneration;

            if (isNetwork)
            {
                if (!networkSessions.TryGetForPath(node.TargetPath, out var connection))
                {
                    AppLogService.Warning($"Sem sessão de rede para: {node.TargetPath}");
                    node.ChildrenLoaded = false;
                    if (node.IsExpandable && node.Children.Count == 0)
                    {
                        node.Children.Add(CreateLazyPlaceholder());
                    }

                    return;
                }

                // Não limpar os filhos agora: remover o placeholder com a pasta expandida
                // faz o TreeView colapsar (IsExpanded TwoWay) e a subpasta parece "não abrir".
                BeginLoadNetworkDirectoryChildren(
                    node,
                    connection,
                    fileSystemService,
                    networkSessions,
                    networkFileService,
                    sortOptions,
                    generation);
                return;
            }

            node.Children.Clear();
            IReadOnlyList<FileSystemEntry> entries;
            var servedFromCache = false;

            // Cache-first: serve a listagem do índice (instantâneo) e revalida em segundo
            // plano; se nunca foi indexada, lê o disco uma única vez (a gravação é diferida).
            var view = _viewSettings.Options;
            var cached = _index.TryGetEntries(node.TargetPath, view);
            if (cached is not null)
            {
                entries = cached;
                servedFromCache = true;
            }
            else
            {
                entries = _index.ReadFromDiskAndQueueStore(node.TargetPath, view);
            }

            node.ListingDenied = entries.Count == 0 && _index.WasAccessDenied(node.TargetPath);
            var sorted = TreeEntrySorter.Sort(entries, sortOptions);
            node.FolderCount = sorted.Count(e => e.IsDirectory);
            node.FileCount = sorted.Count - node.FolderCount;

            // Pastas enormes (ex.: C:\Windows\System32): construir e renderizar milhares de itens
            // de uma só vez bloqueia a thread de UI ("Não respondendo"). Nesse caso preenchemos
            // em lotes ao longo de vários frames do Dispatcher, mantendo a interface responsiva.
            // Pastas normais (abaixo do limiar) continuam a ser construídas de forma síncrona,
            // sem qualquer diferença de comportamento visual.
            if (sorted.Count > InlineBuildThreshold)
            {
                node.ChildrenLoaded = true;
                StartChunkedFill(node, sorted, servedFromCache, sortOptions, generation);
                AppLogService.Info(
                    $"Carregado (streaming): {node.TargetPath} ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
                return;
            }

            foreach (var entry in sorted)
            {
                node.Children.Add(CreateChildNode(entry));
            }

            node.ChildrenLoaded = true;
            ScheduleBackgroundRefresh(node, servedFromCache, sortOptions);
            AppLogService.Info($"Carregado: {node.TargetPath} ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao carregar árvore: {node.TargetPath}", ex);
            node.ChildrenLoaded = false;
        }
    }

    private void BeginLoadNetworkDirectoryChildren(
        NavigationTreeNodeViewModel node,
        NetworkConnectionInfo connection,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService,
        TreeSortOptions sortOptions,
        int generation)
    {
        _ = Task.Run(async () =>
        {
            var result = await networkFileService.ConnectAndListAsync(connection).ConfigureAwait(false);
            if (!result.Success)
            {
                AppLogService.Warning(result.ErrorMessage ?? "Falha ao listar caminho remoto.");
                var failDispatcher = System.Windows.Application.Current?.Dispatcher;
                if (failDispatcher is not null)
                {
                    await failDispatcher.InvokeAsync(() =>
                    {
                        if (node.LoadGeneration != generation)
                        {
                            return;
                        }

                        if (node.Children.Count == 0 && node.IsExpandable)
                        {
                            node.Children.Add(CreateLazyPlaceholder());
                        }

                        node.ChildrenLoaded = false;
                    });
                }

                return;
            }

            var sorted = TreeEntrySorter.Sort(result.Entries, sortOptions);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                if (node.LoadGeneration != generation)
                {
                    return;
                }

                node.Children.Clear();
                node.FolderCount = sorted.Count(e => e.IsDirectory);
                node.FileCount = sorted.Count - node.FolderCount;

                foreach (var entry in sorted)
                {
                    if (entry.IsDirectory)
                    {
                        var child = new NavigationTreeNodeViewModel(
                            entry.Name,
                            NavigationNodeKind.Directory,
                            entry.FullPath);
                        var folderName = entry.Name;
                        child.SetIconFactory(() => ShellIconService.GetRemoteItemIcon(folderName, isDirectory: true));
                        child.Children.Add(CreateLazyPlaceholder());
                        PopulateDirectoryCounts(child, fileSystemService, networkSessions, networkFileService);
                        node.Children.Add(child);
                    }
                    else
                    {
                        node.Children.Add(CreateChildNode(entry));
                    }
                }

                node.ChildrenLoaded = true;
                // Garantir que a pasta permanece aberta após o preenchimento assíncrono.
                node.IsExpanded = true;
                AppLogService.Info($"Carregado: {node.TargetPath} ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
            });
        });
    }

    /// <summary>
    /// Atualiza contagens e restabelece o placeholder de expansão numa pasta recolhida,
    /// sem carregar os filhos — mantém a seta de expandir visível após "Atualizar".
    /// </summary>
    public void RefreshCollapsedDirectoryPlaceholder(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService)
    {
        if (node.Kind == NavigationNodeKind.Favorites)
        {
            node.Children.Clear();
            node.FolderCount = 0;
            node.FileCount = 0;
            if (FavoritesService.HasItems())
            {
                var (folders, files) = FavoritesService.GetCounts();
                node.FolderCount = folders;
                node.FileCount = files;
                node.Children.Add(CreateLazyPlaceholder());
            }

            return;
        }

        if (node.Kind == NavigationNodeKind.RecycleBin)
        {
            node.Children.Clear();
            node.FolderCount = 0;
            node.FileCount = 0;
            if (RecycleBinService.HasItems())
            {
                var (folders, files) = RecycleBinService.GetCounts();
                node.FolderCount = folders;
                node.FileCount = files;
                node.Children.Add(CreateLazyPlaceholder());
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(node.TargetPath) ||
            node.Kind is not (NavigationNodeKind.Drive or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive
                or NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp
                or NavigationNodeKind.Smb or NavigationNodeKind.Nfs))
        {
            return;
        }

        try
        {
            if (node.Kind == NavigationNodeKind.Archive ||
                ArchivePathHelper.IsVirtualArchivePath(node.TargetPath))
            {
                if (!ArchivePathHelper.TrySplit(node.TargetPath, out var archivePath, out var entryDirectory))
                {
                    return;
                }

                if (node.Kind == NavigationNodeKind.Archive)
                {
                    entryDirectory = string.Empty;
                    archivePath = node.TargetPath;
                }

                if (!_archiveService.TryListEntries(archivePath, entryDirectory, out var archiveEntries, out _))
                {
                    return;
                }

                var archiveFolderCount = 0;
                var archiveFileCount = 0;
                foreach (var entry in archiveEntries)
                {
                    if (entry.IsParentLink)
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        archiveFolderCount++;
                    }
                    else
                    {
                        archiveFileCount++;
                    }
                }

                node.FolderCount = archiveFolderCount;
                node.FileCount = archiveFileCount;
                if (archiveFolderCount + archiveFileCount > 0 && node.Children.Count == 0)
                {
                    node.Children.Add(CreateLazyPlaceholder());
                }

                return;
            }

            if (NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
            {
                PopulateDirectoryCounts(node, fileSystemService, networkSessions, networkFileService);
                return;
            }

            var view = _viewSettings.Options;
            var entries = _index.ReadFromDiskAndQueueStore(node.TargetPath, view);
            var folderCount = 0;
            var fileCount = 0;

            foreach (var entry in entries)
            {
                if (entry.IsParentLink)
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }
            }

            node.FolderCount = folderCount;
            node.FileCount = fileCount;

            if (folderCount + fileCount > 0 && node.Children.Count == 0)
            {
                node.Children.Add(CreateLazyPlaceholder());
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao atualizar pasta recolhida: {node.TargetPath}", ex);
        }
    }

    private void LoadRecycleBinChildren(NavigationTreeNodeViewModel node, TreeSortOptions sortOptions)
    {
        try
        {
            node.Children.Clear();
            ++node.LoadGeneration;
            var entries = TreeEntrySorter.Sort(RecycleBinService.GetEntries(), sortOptions);
            node.FolderCount = entries.Count(e => e.IsDirectory);
            node.FileCount = entries.Count - node.FolderCount;

            foreach (var entry in entries)
            {
                node.Children.Add(entry.IsDirectory ? CreateRecycleBinDirectoryNode(entry) : CreateRecycleBinFileNode(entry));
            }

            node.ChildrenLoaded = true;
            AppLogService.Info($"Carregado: Lixeira ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao carregar a lixeira", ex);
            node.ChildrenLoaded = false;
        }
    }

    private void LoadFavoritesChildren(NavigationTreeNodeViewModel node, TreeSortOptions sortOptions)
    {
        try
        {
            node.Children.Clear();
            ++node.LoadGeneration;
            var entries = TreeEntrySorter.Sort(FavoritesService.GetEntries(), sortOptions);
            node.FolderCount = entries.Count(e => e.IsDirectory);
            node.FileCount = entries.Count - node.FolderCount;

            foreach (var entry in entries)
            {
                node.Children.Add(entry.IsDirectory
                    ? CreateFavoriteDirectoryNode(entry)
                    : CreateFavoriteFileNode(entry));
            }

            node.ChildrenLoaded = true;
            ScheduleFavoriteChildCounts(node);
            AppLogService.Info($"Carregado: Favoritos ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao carregar favoritos", ex);
            node.ChildrenLoaded = false;
        }
    }

    /// <summary>
    /// Preenche pastas/ficheiros das pastas favoritas (como o refresh das pastas normais),
    /// para a contagem aparecer antes de expandir.
    /// </summary>
    private void ScheduleFavoriteChildCounts(NavigationTreeNodeViewModel node)
    {
        var generation = node.LoadGeneration;
        var directoryPaths = node.Children
            .Where(c => c.Kind == NavigationNodeKind.Directory &&
                        !c.IsPlaceholder &&
                        !string.IsNullOrWhiteSpace(c.TargetPath))
            .Select(c => c.TargetPath!)
            .ToList();

        if (directoryPaths.Count == 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var view = _viewSettings.Options;
                var counts = new Dictionary<string, (int Folders, int Files)>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in directoryPaths)
                {
                    counts[path] = _index.TryGetCounts(path, view, out var folders, out var files)
                        ? (folders, files)
                        : _index.ComputeCountsFromDisk(path, view);
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(() => ApplyBackgroundResults(node, null, counts, generation));
            }
            catch (Exception ex)
            {
                AppLogService.Error("Falha ao contar pastas dos favoritos em segundo plano", ex);
            }
        });
    }

    private static NavigationTreeNodeViewModel CreateFavoriteFileNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.File, entry.FullPath)
        {
            SizeInBytes = entry.SizeInBytes,
            LastModified = entry.LastModified,
            IsFavoriteEntry = true,
            IsMarkedFavorite = true,
            OriginalPath = entry.FullPath
        };
        var iconPath = entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetCachedFileIcon(iconPath));
        return node;
    }

    private NavigationTreeNodeViewModel CreateFavoriteDirectoryNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.Directory, entry.FullPath)
        {
            IsFavoriteEntry = true,
            IsMarkedFavorite = true,
            OriginalPath = entry.FullPath,
            LastModified = entry.LastModified
        };
        var iconPath = entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetFolderIcon(iconPath));

        // Mesmo padrão das pastas normais: placeholder para a seta aparecer ao abrir Favoritos.
        ApplyKnownCounts(node);
        if (node.Children.Count == 0 &&
            !_index.TryGetCounts(entry.FullPath, _viewSettings.Options, out _, out _))
        {
            node.Children.Add(CreateLazyPlaceholder());
        }

        return node;
    }

    private static NavigationTreeNodeViewModel CreateRecycleBinFileNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.File, entry.FullPath)
        {
            SizeInBytes = entry.SizeInBytes,
            LastModified = entry.LastModified,
            IsRecycleBinEntry = true,
            OriginalPath = entry.OriginalPath ?? RecycleBinService.TryResolveOriginalPath(entry.FullPath)
        };
        var iconPath = entry.OriginalPath ?? entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetCachedFileIcon(iconPath));
        return node;
    }

    private static NavigationTreeNodeViewModel CreateRecycleBinDirectoryNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.Directory, entry.FullPath)
        {
            IsRecycleBinEntry = true,
            OriginalPath = entry.OriginalPath ?? RecycleBinService.TryResolveOriginalPath(entry.FullPath)
        };
        var iconPath = entry.OriginalPath ?? entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetFolderIcon(iconPath));
        return node;
    }

    /// <summary>
    /// Preenche as contagens (pastas/ficheiros) de uma pasta local a partir do índice, sem
    /// tocar no disco. Se a pasta tiver conteúdo conhecido, adiciona o placeholder tardio que
    /// faz aparecer a seta de expandir. Se ainda não estiver indexada, fica para o segundo plano.
    /// </summary>
    // Acima deste número de itens numa pasta, o preenchimento passa a ser em lotes (streaming)
    // para não bloquear a UI. Abaixo, construção síncrona (comportamento idêntico ao original).
    private const int InlineBuildThreshold = 600;
    private const int BuildBatchSize = 2000;

    private NavigationTreeNodeViewModel CreateChildNode(FileSystemEntry entry)
    {
        if (!entry.IsDirectory &&
            !string.IsNullOrWhiteSpace(entry.FullPath) &&
            ArchivePathHelper.IsArchiveFile(entry.FullPath) &&
            File.Exists(entry.FullPath))
        {
            return CreateArchiveNode(entry);
        }

        return entry.IsDirectory ? CreateDirectoryNode(entry) : CreateFileNode(entry);
    }

    /// <summary>
    /// Pastas dentro de um arquivo compactado não estão no índice do disco (<see cref="ApplyKnownCounts"/> não as conhece):
    /// as contagens vêm da própria listagem do arquivo. Com conteúdo, entra o marcador que faz a seta de expandir aparecer.
    /// </summary>
    private static void ApplyArchiveCounts(NavigationTreeNodeViewModel child, FileSystemEntry entry)
    {
        if (!entry.IsDirectory || entry.ChildFolderCount is not { } folders || entry.ChildFileCount is not { } files)
        {
            return;
        }

        child.FolderCount = folders;
        child.FileCount = files;

        if (folders + files > 0 && child.Children.Count == 0)
        {
            child.Children.Add(CreateLazyPlaceholder());
        }
    }

    private bool TryLoadArchiveChildren(NavigationTreeNodeViewModel node, TreeSortOptions sortOptions)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return false;
        }

        if (node.Kind != NavigationNodeKind.Archive &&
            !ArchivePathHelper.IsVirtualArchivePath(node.TargetPath))
        {
            return false;
        }

        if (!ArchivePathHelper.TrySplit(node.TargetPath, out var archivePath, out var entryDirectory))
        {
            return false;
        }

        // Archive root node: TargetPath is the archive file itself (empty entry).
        if (node.Kind == NavigationNodeKind.Archive)
        {
            entryDirectory = string.Empty;
            archivePath = node.TargetPath;
        }

        try
        {
            node.Children.Clear();
            _ = ++node.LoadGeneration;

            if (!_archiveService.TryListEntries(archivePath, entryDirectory, out var entries, out var error))
            {
                AppLogService.Warning($"Falha ao listar arquivo '{archivePath}': {error}");
                node.ChildrenLoaded = true;
                return true;
            }

            var sorted = TreeEntrySorter.Sort(entries, sortOptions).ToList();
            var folderCount = 0;
            var fileCount = 0;

            foreach (var entry in sorted)
            {
                if (entry.IsParentLink)
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                var child = CreateChildNode(entry);
                ApplyArchiveCounts(child, entry);
                node.Children.Add(child);
            }

            node.FolderCount = folderCount;
            node.FileCount = fileCount;
            node.ChildrenLoaded = true;
            AppLogService.Info($"Arquivo carregado: {node.TargetPath} ({folderCount} pastas, {fileCount} ficheiros)");
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao carregar arquivo: {node.TargetPath}", ex);
            node.ChildrenLoaded = false;
            return true;
        }
    }

    /// <summary>
    /// Preenche os filhos de uma pasta muito grande. Os nós são criados em lotes ao longo de vários frames do
    /// Dispatcher (a interface não trava) numa lista solta, e a lista inteira entra na árvore de uma só vez no fim.
    /// Adicionar item a item à coleção já ligada à tela custava de 0,8 a 4 ms por item e crescia com o tamanho da
    /// pasta (mais de 3 minutos para 100 mil arquivos); a troca única leva uma fração de segundo.
    /// Um token de geração cancela o preenchimento se o nó for recolhido/recarregado entretanto.
    /// </summary>
    private void StartChunkedFill(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry> sorted,
        bool servedFromCache,
        TreeSortOptions sortOptions,
        int generation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var scheduler = ChunkedFillScheduler ??
            (dispatcher is null
                ? null
                : new Action<Action>(batch => dispatcher.BeginInvoke(batch, System.Windows.Threading.DispatcherPriority.Background)));
        var built = new List<NavigationTreeNodeViewModel>(sorted.Count);
        var position = 0;
        var finished = false;

        void Finish()
        {
            finished = true;
            node.CompleteChunkedFillNow = null;

            // Um filho pode ter sido criado antes (a navegação por caminho materializa o nó que precisa selecionar):
            // reaproveita a instância para a seleção não ficar apontando para um nó que saiu da árvore.
            if (node.Children.Count > 0)
            {
                var existing = new Dictionary<string, NavigationTreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in node.Children)
                {
                    if (!child.IsPlaceholder && !string.IsNullOrWhiteSpace(child.TargetPath))
                    {
                        existing[child.TargetPath] = child;
                    }
                }

                if (existing.Count > 0)
                {
                    for (var i = 0; i < built.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(built[i].TargetPath) &&
                            existing.TryGetValue(built[i].TargetPath!, out var kept))
                        {
                            built[i] = kept;
                        }
                    }
                }
            }

            node.Children.ReplaceAll(built);
            ScheduleBackgroundRefresh(node, servedFromCache, sortOptions);
            MemoryTrimmer.Shared.ScheduleAfterLargeLoad(built.Count);
        }

        void BuildNextBatch()
        {
            // O nó foi recarregado/substituído (ou o preenchimento já foi terminado à força): abortar sem tocar na coleção atual.
            if (finished || node.LoadGeneration != generation)
            {
                return;
            }

            var end = scheduler is null ? sorted.Count : Math.Min(position + BuildBatchSize, sorted.Count);
            for (; position < end; position++)
            {
                built.Add(CreateChildNode(sorted[position]));
            }

            if (position < sorted.Count)
            {
                scheduler!(BuildNextBatch);
                return;
            }

            Finish();
        }

        // Navegação por caminho (Localizar, Duplicados): termina o que falta agora, no mesmo passo, para o nó procurado existir.
        void CompleteNow()
        {
            if (finished || node.LoadGeneration != generation)
            {
                node.CompleteChunkedFillNow = null;
                return;
            }

            for (; position < sorted.Count; position++)
            {
                built.Add(CreateChildNode(sorted[position]));
            }

            Finish();
        }

        BuildNextBatch();
        if (!finished)
        {
            node.CompleteChunkedFillNow = CompleteNow;
        }
    }

    /// <summary>
    /// Termina na hora o preenchimento em lotes de uma pasta grande (se houver um em andamento): os filhos entram na árvore de
    /// uma vez e o nó que a navegação procura passa a existir. Devolve false quando não havia preenchimento pendente.
    /// </summary>
    public bool CompleteChunkedFill(NavigationTreeNodeViewModel node)
    {
        if (node.CompleteChunkedFillNow is not { } complete)
        {
            return false;
        }

        complete();
        return true;
    }

    /// <summary>
    /// Troca o agendador dos lotes do preenchimento em lotes (só os testes usam): por padrão cada lote vai para a fila do
    /// Dispatcher em prioridade Background, ou tudo é feito de uma vez quando não há janela (testes sem Application).
    /// </summary>
    public Action<Action>? ChunkedFillScheduler { get; set; }
    private void ApplyKnownCounts(NavigationTreeNodeViewModel child)
    {
        if (string.IsNullOrWhiteSpace(child.TargetPath))
        {
            return;
        }

        if (!_index.TryGetCounts(child.TargetPath, _viewSettings.Options, out var folders, out var files))
        {
            return;
        }

        child.FolderCount = folders;
        child.FileCount = files;

        if (folders + files > 0 && child.Children.Count == 0)
        {
            child.Children.Add(CreateLazyPlaceholder());
        }
    }

    /// <summary>
    /// Revalida a pasta contra o disco (se foi servida da cache e mudou) e calcula as contagens
    /// das subpastas — tudo fora da thread de UI. As atualizações são aplicadas de volta na UI.
    /// </summary>
    private void ScheduleBackgroundRefresh(
        NavigationTreeNodeViewModel node,
        bool servedFromCache,
        TreeSortOptions sortOptions)
    {
        var path = node.TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var generation = node.LoadGeneration;

        Task.Run(() =>
        {
            try
            {
                var view = _viewSettings.Options;

                IReadOnlyList<FileSystemEntry>? freshSorted = null;
                if (servedFromCache && !_index.IsIndexedAndCurrent(path))
                {
                    var fresh = _index.ReadFromDiskAndQueueStore(path, view);
                    freshSorted = TreeEntrySorter.Sort(fresh, sortOptions);
                }

                // Só as pastas entram no dicionário e a ordem não importa: não ordena de novo uma lista enorme.
                var basis = freshSorted
                    ?? _index.TryGetEntries(path, view)
                    ?? Array.Empty<FileSystemEntry>();

                var counts = new Dictionary<string, (int Folders, int Files)>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in basis)
                {
                    if (!entry.IsDirectory)
                    {
                        continue;
                    }

                    counts[entry.FullPath] = _index.TryGetCounts(entry.FullPath, view, out var f, out var fi)
                        ? (f, fi)
                        : _index.ComputeCountsFromDisk(entry.FullPath, view);
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    return;
                }

                dispatcher.BeginInvoke(() => ApplyBackgroundResults(node, freshSorted, counts, generation));

                // A revalidação também deixa lixo grande; a espera do enxugador recomeça a partir daqui.
                MemoryTrimmer.Shared.ScheduleAfterLargeLoad(basis.Count);
            }
            catch (Exception ex)
            {
                AppLogService.Error($"Falha na revalidação em segundo plano: {path}", ex);
            }
        });
    }

    internal void ApplyBackgroundResults(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry>? freshSorted,
        Dictionary<string, (int Folders, int Files)> counts,
        int generation)
    {
        // Se o nó foi entretanto recolhido/recarregado, não mexemos (evita glitches visuais).
        if (!node.ChildrenLoaded || node.LoadGeneration != generation)
        {
            return;
        }

        // Só reconstrói se o disco realmente diferir da cache — no caso comum (sem alterações)
        // nada muda, portanto não há flicker nem reanimação.
        if (freshSorted is not null && HasListingChanged(node, freshSorted))
        {
            RebuildChildrenFromEntries(node, freshSorted);
        }

        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder ||
                child.Kind != NavigationNodeKind.Directory ||
                string.IsNullOrWhiteSpace(child.TargetPath))
            {
                continue;
            }

            if (!counts.TryGetValue(child.TargetPath, out var c))
            {
                continue;
            }

            child.FolderCount = c.Folders;
            child.FileCount = c.Files;

            // O índice anotou que o Windows recusou a contagem: a linha mostra um cadeado em vez de "0 pastas, 0 arquivos".
            child.ListingDenied = c.Folders + c.Files == 0 && _index.WasAccessDenied(child.TargetPath);

            var hasChildren = c.Folders + c.Files > 0;
            var hasPlaceholderOnly = child.Children.Count == 1 && child.Children[0].IsPlaceholder;

            if (hasChildren && child.Children.Count == 0)
            {
                child.Children.Add(CreateLazyPlaceholder());
            }
            else if (!hasChildren && !child.ChildrenLoaded && hasPlaceholderOnly)
            {
                child.Children.Clear();
            }
        }
    }

    private static bool HasListingChanged(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry> freshSorted)
    {
        var current = node.Children.Where(c => !c.IsPlaceholder).ToList();
        if (current.Count != freshSorted.Count)
        {
            return true;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var c = current[i];
            var e = freshSorted[i];

            if (!string.Equals(c.TargetPath, e.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var currentIsDirectory = c.Kind == NavigationNodeKind.Directory;
            if (currentIsDirectory != e.IsDirectory)
            {
                return true;
            }

            if (c.SizeInBytes != e.SizeInBytes || c.LastModified != e.LastModified)
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildChildrenFromEntries(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry> freshSorted)
    {
        var existingByPath = node.Children
            .Where(c => !c.IsPlaceholder && !string.IsNullOrWhiteSpace(c.TargetPath))
            .GroupBy(c => c.TargetPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var reordered = new List<NavigationTreeNodeViewModel>();
        var folderCount = 0;
        var fileCount = 0;

        foreach (var entry in freshSorted)
        {
            if (entry.IsDirectory)
            {
                folderCount++;
            }
            else
            {
                fileCount++;
            }

            if (existingByPath.TryGetValue(entry.FullPath, out var existing))
            {
                // Ficheiro com tamanho/data alterados: recria para refletir (props são init-only).
                if (!entry.IsDirectory &&
                    existing.Kind is NavigationNodeKind.File &&
                    (existing.SizeInBytes != entry.SizeInBytes || existing.LastModified != entry.LastModified))
                {
                    reordered.Add(CreateChildNode(entry));
                }
                else
                {
                    reordered.Add(existing);
                }

                continue;
            }

            reordered.Add(CreateChildNode(entry));
        }

        // Só o que mudou entra e sai: os itens que ficam não são recriados na tela (não piscam).
        node.Children.SyncTo(reordered);

        node.FolderCount = folderCount;
        node.FileCount = fileCount;
    }

    /// <summary>
    /// Reabrir uma pasta já carregada: a listagem atual continua na tela e o disco é relido em segundo plano; só o
    /// que mudou é trocado. Limpar e recarregar tudo fazia os itens sumirem e reaparecerem ("piscar").
    /// Retorna falso para pastas que não são do disco (arquivo compactado, lixeira, favoritos, rede): essas são
    /// recarregadas pelo caminho normal.
    /// </summary>
    public bool TryRevalidateLoadedDirectory(NavigationTreeNodeViewModel node, TreeSortOptions sortOptions)
    {
        if (!node.ChildrenLoaded ||
            node.Kind is not (NavigationNodeKind.Drive or NavigationNodeKind.Directory) ||
            string.IsNullOrWhiteSpace(node.TargetPath) ||
            ArchivePathHelper.IsVirtualArchivePath(node.TargetPath) ||
            NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
        {
            return false;
        }

        _index.Invalidate(node.TargetPath);
        ScheduleBackgroundRefresh(node, servedFromCache: true, sortOptions);
        return true;
    }

    public void ResortDirectoryChildren(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        TreeSortOptions sortOptions)
    {
        if (node.Kind == NavigationNodeKind.Favorites)
        {
            if (!node.ChildrenLoaded)
            {
                return;
            }

            node.ChildrenLoaded = false;
            LoadFavoritesChildren(node, sortOptions);
            return;
        }

        if (node.Kind == NavigationNodeKind.RecycleBin)
        {
            if (!node.ChildrenLoaded)
            {
                return;
            }

            node.ChildrenLoaded = false;
            LoadRecycleBinChildren(node, sortOptions);
            return;
        }

        if (!node.ChildrenLoaded || string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        if (node.Kind == NavigationNodeKind.Archive ||
            ArchivePathHelper.IsVirtualArchivePath(node.TargetPath))
        {
            node.ChildrenLoaded = false;
            TryLoadArchiveChildren(node, sortOptions);
            return;
        }

        if (NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
        {
            return;
        }

        try
        {
            var view = _viewSettings.Options;
            var entries = _index.TryGetEntries(node.TargetPath, view)
                ?? _index.ReadFromDiskAndQueueStore(node.TargetPath, view);
            var sorted = TreeEntrySorter.Sort(entries, sortOptions);

            var existingByPath = node.Children
                .Where(child => !string.IsNullOrWhiteSpace(child.TargetPath))
                .GroupBy(child => child.TargetPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var reordered = new List<NavigationTreeNodeViewModel>();
            var folderCount = 0;
            var fileCount = 0;

            foreach (var entry in sorted)
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }

                if (existingByPath.TryGetValue(entry.FullPath, out var existing))
                {
                    reordered.Add(existing);
                    continue;
                }

                reordered.Add(CreateChildNode(entry));
            }

            node.Children.Clear();
            foreach (var child in reordered)
            {
                node.Children.Add(child);
            }

            node.FolderCount = folderCount;
            node.FileCount = fileCount;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao reordenar árvore: {node.TargetPath}", ex);
        }
    }

    private static void PopulateDirectoryCounts(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore? networkSessions = null,
        INetworkFileService? networkFileService = null)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        try
        {
            if (NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
            {
                if (networkSessions is null || networkFileService is null)
                {
                    return;
                }

                if (!networkSessions.TryGetForPath(node.TargetPath, out var connection))
                {
                    return;
                }

                BeginPopulateNetworkCounts(node, connection, fileSystemService, networkSessions, networkFileService);
                return;
            }

            var entries = fileSystemService.GetEntries(node.TargetPath);
            ApplyCountsFromEntries(node, entries, fileSystemService, networkSessions, networkFileService);
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao contar itens: {node.TargetPath}", ex);
        }
    }

    private static void BeginPopulateNetworkCounts(
        NavigationTreeNodeViewModel node,
        NetworkConnectionInfo connection,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService)
    {
        _ = Task.Run(async () =>
        {
            var result = await networkFileService.ConnectAndListAsync(connection).ConfigureAwait(false);
            if (!result.Success)
            {
                return;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
                ApplyCountsFromEntries(node, result.Entries, fileSystemService, networkSessions, networkFileService));
        });
    }

    private static void ApplyCountsFromEntries(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry> entries,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore? networkSessions,
        INetworkFileService? networkFileService)
    {
        var folderCount = 0;
        var fileCount = 0;

        foreach (var entry in entries)
        {
            if (entry.IsParentLink)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                folderCount++;
            }
            else
            {
                fileCount++;
            }
        }

        node.FolderCount = folderCount;
        node.FileCount = fileCount;

        if (folderCount + fileCount > 0 && node.Children.Count == 0)
        {
            node.Children.Add(CreateLazyPlaceholder());
        }
    }

    private static NavigationTreeNodeViewModel CreateNetworkNode(
        string name,
        NavigationNodeKind kind,
        NetworkProtocol protocol)
    {
        var node = new NavigationTreeNodeViewModel(name, kind)
        {
            // SMB e NFS têm ícone próprio (janela e pinguim); os demais usam o ícone de rede do Windows.
            IconSource = NetworkProtocolIcons.TreeIconFor(protocol) ?? ShellIconService.GetNetworkIcon()
        };

        foreach (var profile in App.SettingsService.LoadNetworkClientProfiles(protocol)
                     .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(CreateNetworkProfileNode(profile));
        }

        node.ChildrenLoaded = true;
        return node;
    }

    private static NavigationTreeNodeViewModel CreateNetworkProfileNode(NetworkClientProfile profile)
    {
        var node = new NavigationTreeNodeViewModel(
            profile.DisplayName,
            NavigationNodeKind.Directory)
        {
            IconSource = ShellIconService.GetNetworkServerIcon(),
            NetworkProfileId = profile.Id
        };

        if (NetworkKinds.IsBrowsable(profile.Protocol))
        {
            node.Children.Add(CreateLazyPlaceholder());
        }
        else
        {
            node.ChildrenLoaded = true;
        }

        return node;
    }

    private static NavigationTreeNodeViewModel CreateFileNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.File, entry.FullPath)
        {
            SizeInBytes = entry.SizeInBytes,
            LastModified = entry.LastModified,
            IsMarkedFavorite = FavoritesService.IsFavorite(entry.FullPath)
        };
        var path = entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetCachedFileIcon(path));
        return node;
    }

    private NavigationTreeNodeViewModel CreateArchiveNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.Archive, entry.FullPath)
        {
            SizeInBytes = entry.SizeInBytes,
            LastModified = entry.LastModified,
            IsMarkedFavorite = FavoritesService.IsFavorite(entry.FullPath)
        };
        var path = entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetCachedFileIcon(path));

        // Assume non-empty until listed; placeholder keeps expander visible.
        node.FolderCount = 0;
        node.FileCount = 1;
        node.Children.Add(CreateLazyPlaceholder());
        return node;
    }

    private NavigationTreeNodeViewModel CreateDirectoryNode(FileSystemEntry entry)
    {
        var node = new NavigationTreeNodeViewModel(entry.Name, NavigationNodeKind.Directory, entry.FullPath)
        {
            LastModified = entry.LastModified,
            IsMarkedFavorite = FavoritesService.IsFavorite(entry.FullPath)
        };
        var path = entry.FullPath;
        node.SetIconFactory(() => ShellIconService.GetFolderIcon(path));
        ApplyKnownCounts(node);
        return node;
    }

    private static NavigationTreeNodeViewModel CreateDriveNode(
        string targetPath,
        string label,
        string? storageSummary,
        double storageUsedRatio,
        bool isReady)
    {
        var node = new NavigationTreeNodeViewModel(
            label,
            NavigationNodeKind.Drive,
            targetPath)
        {
            IconSource = ShellIconService.GetDriveIcon(targetPath, isReady),
            StorageSummary = storageSummary,
            StorageUsedRatio = storageUsedRatio,
            IsMarkedFavorite = FavoritesService.IsFavorite(targetPath)
        };

        if (isReady)
        {
            node.Children.Add(CreateLazyPlaceholder());
        }

        return node;
    }

    /// <summary>
    /// Lê todas as unidades em paralelo com um tempo máximo: uma unidade de rede offline pode levar
    /// dezenas de segundos a responder e, lida em série na thread da UI, travava a abertura do app.
    /// Quem não responde a tempo aparece como indisponível.
    /// </summary>
    private static (bool IsReady, string? VolumeLabel, long FreeBytes, long TotalBytes, string? UnavailableReason)[] ReadDriveInfos(
        IReadOnlyList<DriveInfo> drives)
    {
        var tasks = drives.Select(drive => Task.Run(() => ReadDriveInfo(drive))).ToArray();

        try
        {
            Task.WaitAll(tasks, DriveProbeTimeout);
        }
        catch (AggregateException ex)
        {
            AppLogService.Error("Falha ao ler as unidades", ex);
        }

        var results = new (bool IsReady, string? VolumeLabel, long FreeBytes, long TotalBytes, string? UnavailableReason)[tasks.Length];
        for (var i = 0; i < tasks.Length; i++)
        {
            if (tasks[i].IsCompletedSuccessfully)
            {
                results[i] = tasks[i].Result;
                continue;
            }

            AppLogService.Warning(
                $"Unidade {drives[i].Name} não respondeu em {DriveProbeTimeout.TotalSeconds:0} s; mostrada como indisponível.");
            results[i] = (false, null, 0, 0, "Sem resposta");
        }

        return results;
    }

    /// <summary>
    /// Lê rótulo e espaço da unidade sem lançar exceção. Unidades bloqueadas (BitLocker),
    /// leitores sem disco ou unidades de rede desligadas fazem o <see cref="DriveInfo"/> lançar
    /// <see cref="IOException"/>, o que não pode impedir o arranque da aplicação.
    /// </summary>
    private static (bool IsReady, string? VolumeLabel, long FreeBytes, long TotalBytes, string? UnavailableReason) ReadDriveInfo(
        DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady)
            {
                return (false, null, 0, 0, DescribeUnavailable(drive));
            }

            return (true, drive.VolumeLabel, drive.TotalFreeSpace, drive.TotalSize, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Unidade {drive.Name} indisponível: {ex.Message}");
            return (false, null, 0, 0, DescribeFailure(ex));
        }
    }

    private static string DescribeFailure(Exception exception) =>
        IsBitLockerLocked(exception) ? BitLockerLockedText
        : exception is UnauthorizedAccessException ? AccessLockedText
        : UnavailableText;

    internal const string BitLockerLockedText = "Bloqueada (BitLocker)";

    /// <summary>Outra trava: o Windows nega o acesso à raiz da unidade (criptografia de outro programa, política de segurança...).</summary>
    internal const string AccessLockedText = "Bloqueada (sem acesso)";

    private const string UnavailableText = "Indisponível";

    /// <summary>A unidade está travada (BitLocker bloqueado ou acesso negado), e não só sem mídia ou desligada.</summary>
    internal static bool IsLockedDrive(string? unavailableReason) =>
        unavailableReason is BitLockerLockedText or AccessLockedText;

    /// <summary>Desligar "Exibir unidades bloqueadas" esconde as travadas; as demais (inclusive "Indisponível" e "Sem mídia") continuam.</summary>
    internal static bool ShouldShowDrive(bool showLockedDrives, string? unavailableReason) =>
        showLockedDrives || !IsLockedDrive(unavailableReason);

    /// <summary>Explica por que a unidade não está pronta, para o usuário não ver uma entrada "muda".</summary>
    private static string DescribeUnavailable(DriveInfo drive)
    {
        if (drive.DriveType == DriveType.CDRom)
        {
            return "Sem mídia";
        }

        // Unidade bloqueada pelo BitLocker: IsReady é falso e só ler o rótulo revela o motivo.
        // Redes são deixadas de fora: o acesso poderia demorar dezenas de segundos.
        if (drive.DriveType is DriveType.Fixed or DriveType.Removable)
        {
            try
            {
                _ = drive.VolumeLabel;
            }
            catch (Exception ex) when (IsBitLockerLocked(ex))
            {
                return BitLockerLockedText;
            }
            catch (UnauthorizedAccessException)
            {
                return AccessLockedText;
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                // Outro motivo (sem cartão, desconectada...): cai no texto genérico.
            }
        }

        return UnavailableText;
    }

    // FVE_E_LOCKED_VOLUME (0x80310000): a mesma falha que já derrubava a abertura do app.
    private static bool IsBitLockerLocked(Exception exception) =>
        exception is IOException && exception.HResult == unchecked((int)0x80310000);

    private static bool ShouldIncludeDrive(DriveInfo drive, ExplorerViewOptions viewOptions)
    {
        if (!viewOptions.ShowExternalDrives &&
            drive.DriveType is DriveType.Removable or DriveType.CDRom)
        {
            return false;
        }

        return true;
    }

    private static string FormatDriveStorage(long freeBytes, long totalBytes)
    {
        var free = FormatBytes(freeBytes);
        var total = FormatBytes(totalBytes);
        return $"Livre {free}/{total}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0}{units[unit]}"
            : $"{size:0.#}{units[unit]}";
    }

    private static NavigationTreeNodeViewModel CreateLazyPlaceholder() =>
        new(string.Empty, NavigationNodeKind.Directory)
        {
            IsPlaceholder = true
        };
}
