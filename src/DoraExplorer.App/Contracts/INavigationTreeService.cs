using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Contracts;

public interface INavigationTreeService
{
    string RecycleBinPath { get; }

    string FavoritesPath { get; }

    IEnumerable<NavigationTreeNodeViewModel> CreateVisibleRoots(
        PaneDisplayOptions options,
        ExplorerViewOptions viewOptions);

    NavigationTreeNodeViewModel? TryMaterializeChild(
        NavigationTreeNodeViewModel parent,
        string fullPath,
        ExplorerViewOptions viewOptions);

    NavigationTreeNodeViewModel? TryMaterializeDirectoryByName(
        NavigationTreeNodeViewModel parent,
        string directoryName,
        ExplorerViewOptions viewOptions);

    void LoadDirectoryChildren(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService,
        TreeSortOptions sortOptions);

    bool TryRevalidateLoadedDirectory(
        NavigationTreeNodeViewModel node,
        TreeSortOptions sortOptions);

    void RefreshCollapsedDirectoryPlaceholder(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService);

    void ResortDirectoryChildren(
        NavigationTreeNodeViewModel node,
        ILocalFileSystemService fileSystemService,
        TreeSortOptions sortOptions);
}
