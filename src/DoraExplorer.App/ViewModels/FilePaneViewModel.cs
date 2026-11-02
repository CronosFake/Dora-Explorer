using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.Views.Dialogs;
using Application = System.Windows.Application;

namespace DoraExplorer.App.ViewModels;

public partial class FilePaneViewModel : ObservableObject
{
    private readonly ILocalFileSystemService _fileSystemService;
    private readonly INavigationTreeService _navigationTreeService;
    private readonly ExplorerViewSettingsService _viewSettings;
    private readonly INetworkSessionStore _networkSessions;
    private readonly INetworkFileService _networkFileService;
    private readonly IFileOperationService _fileOperationService;
    private readonly IArchiveService _archiveService;
    private readonly IDirectoryIndexService _index;
    private readonly NetworkClientHealthService _networkHealth;
    private readonly PaneDisplayOptions _displayOptions;
    private readonly Action _saveDisplayOptions;

    public FilePaneViewModel(
        ILocalFileSystemService fileSystemService,
        INavigationTreeService navigationTreeService,
        ExplorerViewSettingsService viewSettings,
        INetworkSessionStore networkSessions,
        INetworkFileService networkFileService,
        IRemoteFileService remoteFiles,
        IFileOperationService fileOperationService,
        IArchiveService archiveService,
        IDirectoryIndexService index,
        NetworkClientHealthService networkHealth,
        PaneDisplayOptions displayOptions,
        Action saveDisplayOptions,
        string initialPath)
    {
        _fileSystemService = fileSystemService;
        _navigationTreeService = navigationTreeService;
        _viewSettings = viewSettings;
        _networkSessions = networkSessions;
        _networkFileService = networkFileService;
        _remoteFiles = remoteFiles;
        _fileOperationService = fileOperationService;
        _archiveService = archiveService;
        _index = index;
        _networkHealth = networkHealth;
        _displayOptions = displayOptions;
        _saveDisplayOptions = saveDisplayOptions;
        _viewSettings.DefaultApplicationVisibilityChanged += OnDefaultApplicationVisibilityChanged;
        _viewSettings.RowDensityChanged += OnRowDensityChanged;
        FileAssociationService.OverridesChanged += OnDefaultApplicationOverridesChanged;
        _networkHealth.HealthChanged += OnNetworkHealthChanged;
        RebuildNavigationTree();
        ApplyNetworkHealthToTree();
        NavigateToPath(initialPath);
        if (BreadcrumbSegments.Count == 0)
        {
            RebuildBreadcrumb(SelectedPath);
        }
    }

    internal bool IsRestoringInitialPath { get; private set; }

    public void RestorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsRestoringInitialPath = true;
        NavigateToPath(path);
    }

    public void CompleteInitialPathRestore() => IsRestoringInitialPath = false;

    public event EventHandler? NavigationTreeRebuilt;

    public event EventHandler? PaneContentRefreshed;

    public event EventHandler? NavigationApplied;


    public event EventHandler? RenamingStarted;

    public event EventHandler? RenamingEnded;

    private bool _suppressSelectionUpdates;

    private bool _centerNavigatedNode;

    private bool _skipAccordionCollapse;

    private NavigationTreeNodeViewModel? _selectionAnchor;

    private int _searchNavigationGeneration;

    public int SearchNavigationGeneration => _searchNavigationGeneration;

    public bool ShouldCenterNavigatedNode => _centerNavigatedNode;

    public bool ShouldSuppressTreeAnimations => _centerNavigatedNode;

    public bool IsSuppressingSelectionUpdates =>
        _suppressSelectionUpdates || IsRestoringInitialPath || _centerNavigatedNode;

    public void ClearCenterNavigatedNode(int? forNavigationId = null)
    {
        if (forNavigationId.HasValue && forNavigationId.Value != _searchNavigationGeneration)
        {
            return;
        }

        _centerNavigatedNode = false;
    }

    private IReadOnlyList<NavigationTreeNodeViewModel> _selectedItems = [];

    public IReadOnlyList<NavigationTreeNodeViewModel> SelectedItems
    {
        get => _selectedItems;
        private set
        {
            _selectedItems = value;
            UpdateStatusText();
        }
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    partial void OnSelectedNodeChanging(NavigationTreeNodeViewModel? oldValue, NavigationTreeNodeViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedNodeCountsChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedNodeCountsChanged;
        }
    }

    partial void OnSelectedNodeChanged(NavigationTreeNodeViewModel? value) => UpdateStatusText();

    // A revalidação em segundo plano pode mudar a contagem (ou a marca de "sem permissão") da pasta selecionada depois de ela já estar na tela.
    private void OnSelectedNodeCountsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NavigationTreeNodeViewModel.FolderCount)
            or nameof(NavigationTreeNodeViewModel.FileCount)
            or nameof(NavigationTreeNodeViewModel.ListingDenied))
        {
            UpdateStatusText();
        }
    }

    private void UpdateStatusText() => StatusText = SelectionStatusFormatter.Format(SelectedItems, SelectedNode);

    public NavigationTreeNodeViewModel? SelectionAnchor => _selectionAnchor;

    public PaneDisplayMenuViewModel DisplayMenu { get; } = new();

    public bool IsFtpServiceActive => _networkSessions.HasProtocol(NetworkProtocol.Ftp);

    public bool IsSftpServiceActive => _networkSessions.HasProtocol(NetworkProtocol.Sftp);

    public bool IsScpServiceActive => _networkSessions.HasProtocol(NetworkProtocol.Scp);

    public bool IsTftpServiceActive => _networkSessions.HasProtocol(NetworkProtocol.Tftp);

    public event EventHandler? NetworkServicesChanged;

    public ObservableCollection<NavigationTreeNodeViewModel> NavigationRoots { get; } = [];

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbSegments { get; } = [];

    [ObservableProperty]
    private string _currentPath = string.Empty;

    // ---- Voltar, avançar e subir ---------------------------------------------------------------

    private readonly NavigationHistory _history = new();

    private bool _navigatingHistory;

    partial void OnCurrentPathChanged(string value)
    {
        // Só pastas "de verdade" entram no histórico (não Favoritos, Lixeira nem rótulos).
        if (!_navigatingHistory && IsHistoryPath(value))
        {
            _history.Visit(value);
        }

        NotifyNavigationCommands();
    }

    private static bool IsHistoryPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (Path.IsPathRooted(path) || NetworkFileService.TryParseRemotePath(path, out _, out _, out _));

    private bool CanGoBack() => _history.CanGoBack;

    private bool CanGoForward() => _history.CanGoForward;

    private bool CanGoUp() => PathNavigation.GetParentPath(CurrentPath) is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => NavigateInHistory(_history.Back());

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => NavigateInHistory(_history.Forward());

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (PathNavigation.GetParentPath(CurrentPath) is { } parent)
        {
            NavigateToPath(parent);
        }
    }

    private void NavigateInHistory(string? path)
    {
        if (path is null)
        {
            return;
        }

        _navigatingHistory = true;
        try
        {
            NavigateToPath(path);
        }
        finally
        {
            _navigatingHistory = false;
        }

        NotifyNavigationCommands();
    }

    private void NotifyNavigationCommands()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    partial void OnSelectedPathChanged(string value) => RebuildBreadcrumb(value);

    [RelayCommand]
    private void NavigateBreadcrumb(BreadcrumbSegmentViewModel? segment)
    {
        if (segment is null || !segment.IsNavigable || string.IsNullOrWhiteSpace(segment.FullPath))
        {
            return;
        }

        NavigateToPath(segment.FullPath);
    }

    [RelayCommand]
    private void CopySelectedPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(SelectedPath);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao copiar o caminho para a área de transferência.", ex);
        }
    }

    private void RebuildBreadcrumb(string path)
    {
        BreadcrumbSegments.Clear();

        if (string.IsNullOrWhiteSpace(path))
        {
            // Sem caminho (raiz da árvore): um rótulo em vez de um segmento vazio, que parecia um defeito.
            BreadcrumbSegments.Add(new BreadcrumbSegmentViewModel(
                name: "Este computador",
                fullPath: string.Empty,
                isNavigable: false,
                isFirst: true,
                isCurrent: true));
            return;
        }

        var raw = new List<(string Name, string FullPath, bool Navigable)>();

        if (NetworkFileService.TryParseRemotePath(path, out var protocol, out var host, out var remotePath))
        {
            var rootPath = NetworkFileService.BuildRemotePath(protocol, host, "/");
            raw.Add((host, rootPath, true));

            var accumulated = "/";
            foreach (var part in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = accumulated.EndsWith('/') ? accumulated + part : $"{accumulated}/{part}";
                raw.Add((part, NetworkFileService.BuildRemotePath(protocol, host, accumulated), true));
            }
        }
        else
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(path))
            {
                // Rótulos especiais (Favoritos, Lixeira, avisos) — segmento único, não navegável.
                raw.Add((path, string.Empty, false));
            }
            else
            {
                var rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrEmpty(rootName))
                {
                    rootName = root;
                }

                raw.Add((rootName, root, true));

                var relative = path[root.Length..];
                var accumulatedLocal = root;
                foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    accumulatedLocal = Path.Combine(accumulatedLocal, part);
                    raw.Add((part, accumulatedLocal, true));
                }
            }
        }

        if (SelectedNode?.IsRecycleBinEntry == true)
        {
            raw.Insert(0, ("Lixeira", string.Empty, false));
        }
        else if (SelectedNode?.IsFavoriteEntry == true)
        {
            raw.Insert(0, ("Favoritos", string.Empty, false));
        }

        for (var i = 0; i < raw.Count; i++)
        {
            BreadcrumbSegments.Add(new BreadcrumbSegmentViewModel(
                raw[i].Name,
                raw[i].FullPath,
                raw[i].Navigable,
                isFirst: i == 0,
                isCurrent: i == raw.Count - 1));
        }
    }

    [ObservableProperty]
    private NavigationTreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private TreeSortOptions _sortOptions = TreeSortOptions.Default;

    [ObservableProperty]
    private bool _showFileModifiedDate = true;

    [ObservableProperty]
    private bool _showFileModifiedTime = true;

    [ObservableProperty]
    private bool _useShortModifiedDate;

    public bool ShowDefaultApplication => _viewSettings.Options.ShowDefaultApplication;

    // ---- Visão em colunas (linhas compactas) ------------------------------------------------
    // Larguras iguais no cabeçalho e em todas as linhas: é isso que mantém as colunas alinhadas.

    public bool CompactRows => _viewSettings.Options.CompactRows;

    public bool ShowComfortableRows => !CompactRows;

    public double SizeColumnWidth => 68;

    /// <summary>Reserva à direita para a estrela de favorito e a marca de seleção.</summary>
    public double TrailingColumnWidth => CompactRows ? 36 : 0;

    public double DateColumnWidth => ShowFileModifiedDate ? (ShowFileModifiedTime ? 104 : 80) : 0;

    [ObservableProperty]
    private double _typeColumnWidth = 84;

    public string NameHeaderText => "Nome" + SortHeaderLogic.Indicator(SortOptions, TreeSortField.Name);

    public string TypeHeaderText => "Tipo" + SortHeaderLogic.Indicator(SortOptions, TreeSortField.Extension);

    public string DateHeaderText => "Modificado" + SortHeaderLogic.Indicator(SortOptions, TreeSortField.Date);

    public string SizeHeaderText => "Tamanho" + SortHeaderLogic.Indicator(SortOptions, TreeSortField.Size);

    partial void OnShowFileModifiedDateChanged(bool value) => OnPropertyChanged(nameof(DateColumnWidth));

    partial void OnShowFileModifiedTimeChanged(bool value) => OnPropertyChanged(nameof(DateColumnWidth));

    partial void OnSortOptionsChanged(TreeSortOptions value)
    {
        OnPropertyChanged(nameof(NameHeaderText));
        OnPropertyChanged(nameof(TypeHeaderText));
        OnPropertyChanged(nameof(DateHeaderText));
        OnPropertyChanged(nameof(SizeHeaderText));
    }

    // Mudou o aplicativo de uma extensão: todas as linhas dessa extensão (não só a clicada) refazem o nome.
    private void OnDefaultApplicationOverridesChanged(object? sender, string extension)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            foreach (var root in NavigationRoots)
            {
                RefreshDefaultApplicationNames(root, extension);
            }
        });
    }

    private static void RefreshDefaultApplicationNames(NavigationTreeNodeViewModel node, string extension)
    {
        if (node.IsFile &&
            string.Equals(Path.GetExtension(node.TargetPath), extension, StringComparison.OrdinalIgnoreCase))
        {
            node.RefreshDefaultApplication();
        }

        foreach (var child in node.Children)
        {
            RefreshDefaultApplicationNames(child, extension);
        }
    }

    private void OnRowDensityChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CompactRows));
        OnPropertyChanged(nameof(ShowComfortableRows));
        OnPropertyChanged(nameof(TrailingColumnWidth));
        ApplyResponsiveLayout();
    }

    private double _availableWidth;

    // Degradacao responsiva por etapas: a medida que o painel encolhe, escondemos
    // as colunas menos essenciais ANTES de elas serem cortadas, para que a informacao
    // nunca "desapareca" abruptamente. O tamanho (size) e sempre mantido; abaixo do
    // menor patamar o limitador de largura da janela (MainWindow.MinWidth) impede
    // encolher mais.
    public void UpdateResponsiveLayout(double availableWidth)
    {
        _availableWidth = availableWidth;
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (_availableWidth <= 0)
        {
            return;
        }

        const double showTimeMinWidth = 460;   // data completa + hora + tamanho
        const double shortDateMaxWidth = 360;  // abaixo disto usa data curta (dd/MM)

        // Em colunas, data e tipo disputam espaço com o nome: só aparecem quando ainda sobra
        // uma largura útil para ele. Nas linhas confortáveis a data fica numa linha própria.
        var showDateMinWidth = CompactRows ? 400 : 250;
        var showTypeMinWidth = CompactRows ? 520 : 480;

        ShowFileModifiedTime = _availableWidth >= showTimeMinWidth;
        UseShortModifiedDate = _availableWidth < shortDateMaxWidth;
        ShowFileModifiedDate = _availableWidth >= showDateMinWidth;

        // A coluna "Tipo" (contagens e aplicativo padrão) é a primeira a sair quando o painel estreita.
        TypeColumnWidth = _availableWidth >= showTypeMinWidth ? 84 : 0;
    }

    [ObservableProperty]
    private NavigationTreeNodeViewModel? _renamingNode;

    private bool _renameCancelled;

    /// <param name="userInitiated">
    /// Clique do usuário: os ramos que o acordeão fecha mostram listagem nova quando reabertos. Aberturas por código
    /// (restaurar o caminho, eventos sintéticos do WPF) fecham os mesmos ramos, mas sem marcá-los para recarga.
    /// </param>
    public void OnNavigationNodeExpanding(NavigationTreeNodeViewModel node, bool userInitiated = false)
    {
        if (!_skipAccordionCollapse)
        {
            CollapseOtherHierarchies(node, userInitiated);
        }
    }

    public void OnNavigationNodeExpanded(NavigationTreeNodeViewModel node)
    {
        if (node.Kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp
            or NavigationNodeKind.Tftp)
        {
            // Nó contentor do cliente — as ligações aparecem como pastas filhas.
            if (string.IsNullOrWhiteSpace(node.TargetPath))
            {
                node.ChildrenLoaded = true;
                return;
            }
        }

        if (node.IsNetworkClientProfile && string.IsNullOrWhiteSpace(node.TargetPath))
        {
            _ = ConnectNetworkProfileAsync(node);
            return;
        }

        if (node.Kind is not (NavigationNodeKind.Drive or NavigationNodeKind.Directory
            or NavigationNodeKind.Archive
            or NavigationNodeKind.RecycleBin
            or NavigationNodeKind.Favorites
            or NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp))
        {
            return;
        }

        if (node.ChildrenLoaded)
        {
            // Perfil já preenchido na ligação: não recarregar (apagava a listagem).
            if (node.IsNetworkClientProfile)
            {
                return;
            }

            // Subpasta remota: se só ficou o placeholder (falha anterior), tentar de novo.
            if (!string.IsNullOrWhiteSpace(node.TargetPath) &&
                NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
            {
                if (node.Children.Count == 0 || node.Children.All(c => c.IsPlaceholder))
                {
                    node.ChildrenLoaded = false;
                    LoadRemoteDirectoryChildren(node);
                }

                return;
            }

            // Já carregada e ainda aberta: recarregar de novo só repetiria o trabalho (medido: a mesma pasta
            // era preenchida 3 vezes ao restaurar o caminho). Só reabrir depois de fechar traz listagem nova.
            if (!node.ReloadOnNextExpand)
            {
                return;
            }

            node.ReloadOnNextExpand = false;

            // A listagem que já está na tela fica; o disco é relido por trás e só a diferença é aplicada.
            if (!_navigationTreeService.TryRevalidateLoadedDirectory(node, SortOptions))
            {
                ReloadNodeChildrenKeepingExpansion(node);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.TargetPath) &&
            NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
        {
            LoadRemoteDirectoryChildren(node);
            return;
        }

        LoadNodeChildren(node);
    }

    /// <summary>
    /// Carrega subpastas remotas de forma síncrona para a TreeView não colapsar
    /// enquanto a listagem assíncrona ainda não preencheu os filhos.
    /// </summary>
    private void LoadRemoteDirectoryChildren(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath) ||
            !_networkSessions.TryGetForPath(node.TargetPath, out var connection))
        {
            AppLogService.Warning($"Sem sessão de rede para: {node.TargetPath}");
            if (node.Children.Count == 0 && node.IsExpandable)
            {
                node.Children.Add(
                    new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
            }

            node.ChildrenLoaded = false;
            return;
        }

        try
        {
            var listResult = _networkFileService.ConnectAndListAsync(connection)
                .ConfigureAwait(true)
                .GetAwaiter()
                .GetResult();

            if (!listResult.Success)
            {
                AppLogService.Warning(listResult.ErrorMessage ?? $"Falha ao listar {node.TargetPath}");
                node.ChildrenLoaded = false;
                if (node.Children.Count == 0 && node.IsExpandable)
                {
                    node.Children.Add(
                        new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
                }

                return;
            }

            ApplyRemoteEntries(node, listResult.Entries);
            node.IsExpanded = true;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao abrir pasta remota: {node.TargetPath}", ex);
            node.ChildrenLoaded = false;
            if (node.Children.Count == 0 && node.IsExpandable)
            {
                node.Children.Add(
                    new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
            }
        }
    }

    /// <param name="userInitiated">
    /// Fechado pelo usuário (clique): reabrir traz listagem nova. O WPF também levanta "fechada" ao reaproveitar
    /// containers com a virtualização, e isso não deve forçar recarga na próxima abertura.
    /// </param>
    public void OnNavigationNodeCollapsed(NavigationTreeNodeViewModel node, bool userInitiated = false)
    {
        node.IsExpanded = false;
        if (userInitiated)
        {
            node.ReloadOnNextExpand = true;
        }
        ClearItemSelectionIfUnder(node);

        foreach (var child in node.Children)
        {
            CollapseRecursive(child, markStale: userInitiated);
        }
    }

    private void CollapseOtherHierarchies(NavigationTreeNodeViewModel expandedNode, bool markStale = false)
    {
        var path = BuildPathToRoot(expandedNode);

        foreach (var root in NavigationRoots)
        {
            if (!path.Contains(root))
            {
                ClearItemSelectionIfUnder(root);
                CollapseRecursive(root, markStale);
                continue;
            }

            CollapseBranchesNotOnPath(root, path, expandedNode, markStale);
        }
    }

    private void CollapseBranchesNotOnPath(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<NavigationTreeNodeViewModel> path,
        NavigationTreeNodeViewModel expandedNode,
        bool markStale)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child, expandedNode))
            {
                continue;
            }

            if (!path.Contains(child))
            {
                ClearItemSelectionIfUnder(child);
                CollapseRecursive(child, markStale);
            }
            else
            {
                CollapseBranchesNotOnPath(child, path, expandedNode, markStale);
            }
        }
    }

    private static void CollapseRecursive(NavigationTreeNodeViewModel node, bool markStale)
    {
        node.IsExpanded = false;
        if (markStale)
        {
            node.ReloadOnNextExpand = true;
        }
        foreach (var child in node.Children)
        {
            CollapseRecursive(child, markStale);
        }
    }

    private void ClearItemSelectionIfUnder(NavigationTreeNodeViewModel ancestor)
    {
        if (SelectedItems.Any(item => ReferenceEquals(item, ancestor) || IsDescendantOf(item, ancestor)))
        {
            ClearItemSelection();
        }
    }

    private bool IsDescendantOf(NavigationTreeNodeViewModel node, NavigationTreeNodeViewModel ancestor)
    {
        for (var parent = GetParentNode(node); parent is not null; parent = GetParentNode(parent))
        {
            if (ReferenceEquals(parent, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private List<NavigationTreeNodeViewModel> BuildPathToRoot(NavigationTreeNodeViewModel node)
    {
        var path = new List<NavigationTreeNodeViewModel>();
        var current = node;

        while (current is not null)
        {
            path.Insert(0, current);
            current = FindParent(current);
        }

        return path;
    }

    private NavigationTreeNodeViewModel? FindParent(NavigationTreeNodeViewModel target)
    {
        foreach (var root in NavigationRoots)
        {
            var parent = FindParentRecursive(root, target);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private static NavigationTreeNodeViewModel? FindParentRecursive(
        NavigationTreeNodeViewModel current,
        NavigationTreeNodeViewModel target)
    {
        foreach (var child in current.Children)
        {
            if (ReferenceEquals(child, target))
            {
                return current;
            }

            var found = FindParentRecursive(child, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public void OnNavigationNodeSelected(NavigationTreeNodeViewModel? node, bool preserveItemSelection = false)
    {
        if (node is null)
        {
            return;
        }

        // Durante uma navegação programática (breadcrumb/busca), o recolher do acordeão pode
        // fazer o WPF reselecionar um ancestral. Ignoramos isso para não sobrescrever o destino.
        if (IsSuppressingSelectionUpdates)
        {
            return;
        }

        SelectedNode = node;

        // Um clique comum numa pasta troca a seleção de itens; Ctrl ou Shift+clique a mantém (preserveItemSelection).
        if (!preserveItemSelection &&
            node.Kind is NavigationNodeKind.Directory or NavigationNodeKind.Drive or NavigationNodeKind.Archive)
        {
            ClearItemSelection();
        }

        if (node.Kind == NavigationNodeKind.RecycleBin)
        {
            ClearItemSelection();
            CurrentPath = NavigationTreeService.RecycleBinPath;
            SelectedPath = node.Name;
            return;
        }

        if (node.Kind == NavigationNodeKind.Favorites)
        {
            ClearItemSelection();
            CurrentPath = NavigationTreeService.FavoritesPath;
            SelectedPath = node.Name;
            return;
        }

        if (node.Kind is NavigationNodeKind.NetworkGroup)
        {
            ClearItemSelection();
            SelectedPath = node.Name;
            return;
        }

        if (node.Kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp
            or NavigationNodeKind.Scp or NavigationNodeKind.Tftp)
        {
            ClearItemSelection();
            if (!string.IsNullOrWhiteSpace(node.TargetPath))
            {
                CurrentPath = node.TargetPath;
                SelectedPath = node.TargetPath;
            }
            else
            {
                SelectedPath = node.Name;
            }

            return;
        }

        if (node.IsRecycleBinEntry)
        {
            CurrentPath = NavigationTreeService.RecycleBinPath;
            SelectedPath = !string.IsNullOrWhiteSpace(node.DisplayPath) ? node.DisplayPath : node.Name;
            return;
        }

        if (node.IsFavoriteEntry)
        {
            CurrentPath = NavigationTreeService.FavoritesPath;
            SelectedPath = !string.IsNullOrWhiteSpace(node.TargetPath) ? node.TargetPath : node.Name;
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.TargetPath))
        {
            CurrentPath = node.Kind is NavigationNodeKind.File
                ? Path.GetDirectoryName(node.TargetPath) ?? node.TargetPath
                : node.TargetPath;
            SelectedPath = node.TargetPath;

            // Pastas remotas: selecionar = entrar (carregar e expandir), como no WinSCP.
            if (node.Kind is (NavigationNodeKind.Directory or NavigationNodeKind.Archive) &&
                NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
            {
                EnterRemoteDirectory(node);
            }
        }
    }

    private void EnterRemoteDirectory(NavigationTreeNodeViewModel node)
    {
        if (node.ChildrenLoaded &&
            node.Children.Count > 0 &&
            !node.Children.All(c => c.IsPlaceholder))
        {
            node.IsExpanded = true;
            return;
        }

        OnNavigationNodeExpanding(node);
        LoadRemoteDirectoryChildren(node);
        node.IsExpanded = true;
    }

    public void NavigateToPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (NetworkFileService.TryParseRemotePath(path, out _, out _, out _))
        {
            if (!_centerNavigatedNode)
            {
                ClearItemSelection();
            }

            SelectedNode = FindNodeByPath(path) ?? SelectedNode;
            SelectedPath = path;
            CurrentPath = path;

            if (SelectedNode is not null &&
                SelectedNode.Kind is (NavigationNodeKind.Directory or NavigationNodeKind.Archive) &&
                string.Equals(SelectedNode.TargetPath, path, StringComparison.OrdinalIgnoreCase))
            {
                EnterRemoteDirectory(SelectedNode);
            }

            NavigationApplied?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_centerNavigatedNode)
        {
            ClearItemSelection();
        }

        _suppressSelectionUpdates = true;
        try
        {
            string navigationPath;
            if (ArchivePathHelper.TrySplit(path, out var archivePath, out var entryPath))
            {
                // Expand into the archive (or to the archive root when entry is empty).
                navigationPath = string.IsNullOrEmpty(entryPath)
                    ? archivePath
                    : path;
            }
            else
            {
                navigationPath = Directory.Exists(path) || !File.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path) ?? path;
            }

            ExpandToPath(navigationPath);

            if (ArchivePathHelper.IsVirtualArchivePath(path) ||
                (File.Exists(path) && ArchivePathHelper.IsArchiveFile(path)))
            {
                var targetNode = FindNodeByPath(path);
                if (targetNode is not null)
                {
                    SelectedNode = targetNode;
                }
            }
        }
        finally
        {
            _suppressSelectionUpdates = false;
        }

        SelectedPath = path;
        if (File.Exists(path) && ArchivePathHelper.IsArchiveFile(path))
        {
            CurrentPath = path;
        }
        else if (ArchivePathHelper.IsVirtualArchivePath(path))
        {
            CurrentPath = FindNodeByPath(path) is { Kind: NavigationNodeKind.File }
                ? Path.GetDirectoryName(path) ?? path
                : path;
        }
        else
        {
            CurrentPath = Directory.Exists(path) || !File.Exists(path)
                ? path
                : Path.GetDirectoryName(path) ?? path;
        }

        NavigationApplied?.Invoke(this, EventArgs.Empty);
    }

    private void ExpandToPath(string path)
    {
        if (NetworkFileService.TryParseRemotePath(path, out _, out _, out _))
        {
            SelectedNode = FindNodeByPath(path);
            return;
        }
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var driveNode = NavigationRoots.FirstOrDefault(n =>
            n.Kind == NavigationNodeKind.Drive &&
            string.Equals(n.TargetPath, root, StringComparison.OrdinalIgnoreCase));

        if (driveNode is null)
        {
            return;
        }

        driveNode.IsExpanded = true;
        OnNavigationNodeExpanding(driveNode);
        OnNavigationNodeExpanded(driveNode);

        var relative = path[root.Length..].TrimStart('\\', '/');
        if (string.IsNullOrEmpty(relative))
        {
            SelectedNode = driveNode;
            return;
        }

        var current = driveNode;
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = FindDirectoryChild(current, segment);
            if (next is null)
            {
                if (!current.ChildrenLoaded)
                {
                    OnNavigationNodeExpanded(current);
                    next = FindDirectoryChild(current, segment);
                }

                if (next is null && current.ChildrenLoaded)
                {
                    ReloadDirectoryChildrenForNavigation(current);
                    next = FindDirectoryChild(current, segment);
                }
            }

            if (next is null)
            {
                break;
            }

            next.IsExpanded = true;
            OnNavigationNodeExpanding(next);
            OnNavigationNodeExpanded(next);
            current = next;
        }

        SelectedNode = current;
    }

    private NavigationTreeNodeViewModel? EnsurePathExpandedInTree(string path, int? searchNavId = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (searchNavId.HasValue && searchNavId.Value != _searchNavigationGeneration)
        {
            return null;
        }

        if (NetworkFileService.TryParseRemotePath(path, out _, out _, out _))
        {
            ExpandToPath(path);
            return FindNodeByPath(path);
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var driveNode = NavigationRoots.FirstOrDefault(n =>
            n.Kind == NavigationNodeKind.Drive &&
            string.Equals(n.TargetPath, root, StringComparison.OrdinalIgnoreCase));

        if (driveNode is null)
        {
            return null;
        }

        driveNode.IsExpanded = true;
        OnNavigationNodeExpanding(driveNode);
        OnNavigationNodeExpanded(driveNode);

        var relative = path[root.Length..].TrimStart('\\', '/');
        if (string.IsNullOrEmpty(relative))
        {
            SelectedNode = driveNode;
            return driveNode;
        }

        var current = driveNode;
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (searchNavId.HasValue && searchNavId.Value != _searchNavigationGeneration)
            {
                return null;
            }

            if (!current.ChildrenLoaded)
            {
                OnNavigationNodeExpanded(current);
            }

            var next = FindDirectoryChild(current, segment);
            if (next is null)
            {
                if (IsDirectoryPopulationInProgress(current))
                {
                    next = _navigationTreeService.TryMaterializeDirectoryByName(
                        current,
                        segment,
                        _viewSettings.Options);
                }

                if (next is null && !IsDirectoryPopulationInProgress(current))
                {
                    ReloadDirectoryChildrenForNavigation(current);
                    next = FindDirectoryChild(current, segment);
                }
            }

            if (next is null)
            {
                SelectedNode = current;
                return FindNodeByPath(path);
            }

            next.IsExpanded = true;
            OnNavigationNodeExpanding(next);
            OnNavigationNodeExpanded(next);
            current = next;
        }

        SelectedNode = current;

        if (Directory.Exists(path) &&
            string.Equals(current.TargetPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        if (!current.ChildrenLoaded)
        {
            OnNavigationNodeExpanded(current);
        }

        return FindNodeByPath(path) ?? current;
    }

    private static NavigationTreeNodeViewModel? FindDirectoryChild(
        NavigationTreeNodeViewModel parent,
        string segment) =>
        parent.Children.FirstOrDefault(c =>
            c.Kind is NavigationNodeKind.Directory or NavigationNodeKind.Archive &&
            string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));

    private void ReloadDirectoryChildrenForNavigation(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        // Aborta preenchimento em lotes pendente antes de limpar a coleção.
        node.LoadGeneration++;
        node.Children.Clear();
        node.ChildrenLoaded = false;
        LoadNodeChildren(node);
    }

    private static bool IsDirectoryPopulationInProgress(NavigationTreeNodeViewModel node)
    {
        if (!node.ChildrenLoaded)
        {
            return true;
        }

        var loadedCount = node.Children.Count(c => !c.IsPlaceholder);
        return loadedCount < node.FolderCount + node.FileCount;
    }

    public void RefreshForViewOptionsChange() => RebuildNavigationTree();

    private void OnDefaultApplicationVisibilityChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(ShowDefaultApplication));

    public void BeginDisplayMenu() => DisplayMenu.LoadFrom(_displayOptions);

    public void CommitDisplayMenu()
    {
        if (!DisplayMenu.DiffersFrom(_displayOptions))
        {
            return;
        }

        DisplayMenu.ApplyTo(_displayOptions);
        _saveDisplayOptions();
        NotifyNetworkServicesChanged();
        RebuildNavigationTree();
    }

    private void NotifyNetworkServicesChanged()
    {
        OnPropertyChanged(nameof(IsFtpServiceActive));
        OnPropertyChanged(nameof(IsSftpServiceActive));
        OnPropertyChanged(nameof(IsScpServiceActive));
        OnPropertyChanged(nameof(IsTftpServiceActive));
        NetworkServicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearItemSelection()
    {
        foreach (var item in SelectedItems)
        {
            item.IsItemSelected = false;
        }

        SelectedItems = [];
        _selectionAnchor = null;
    }

    public void SetSingleItemSelection(NavigationTreeNodeViewModel node)
    {
        if (!node.IsSelectableItem)
        {
            ClearItemSelection();
            return;
        }

        ClearItemSelection();
        node.IsItemSelected = true;
        SelectedItems = [node];
        _selectionAnchor = node;
    }

    /// <summary>
    /// Ctrl+clique: liga o item à seleção ou, se já estava nela, tira só ele. Itens de outra pasta começam uma seleção nova
    /// (como o Shift+clique, que só vale entre irmãos). O item clicado vira a âncora do próximo Shift+clique.
    /// </summary>
    public void ToggleItemSelection(NavigationTreeNodeViewModel node)
    {
        if (!node.IsSelectableItem)
        {
            return;
        }

        if (SelectedItems.Count > 0 && !ShareSelectionParent(SelectedItems[0], node))
        {
            SetSingleItemSelection(node);
            return;
        }

        if (SelectedItems.Any(item => ReferenceEquals(item, node)))
        {
            node.IsItemSelected = false;
            var remaining = SelectedItems.Where(item => !ReferenceEquals(item, node)).ToList();
            SelectedItems = remaining;
            if (ReferenceEquals(_selectionAnchor, node))
            {
                _selectionAnchor = remaining.LastOrDefault();
            }

            SelectedNode = remaining.LastOrDefault() ?? node;
            return;
        }

        node.IsItemSelected = true;
        SelectedItems = [.. SelectedItems, node];
        _selectionAnchor = node;
        SelectedNode = node;
    }

    public void HandleShiftItemSelection(NavigationTreeNodeViewModel node)
    {
        if (!node.IsSelectableItem)
        {
            return;
        }

        if (_selectionAnchor is null)
        {
            SetSingleItemSelection(node);
            return;
        }

        if (!ShareSelectionParent(_selectionAnchor, node))
        {
            SetSingleItemSelection(node);
            return;
        }

        SelectSiblingRange(_selectionAnchor, node);
    }

    public void SelectSiblingItems(IEnumerable<NavigationTreeNodeViewModel> nodes)
    {
        var list = nodes.Where(n => n.IsSelectableItem).ToList();
        if (list.Count == 0)
        {
            ClearItemSelection();
            return;
        }

        var parent = FindParent(list[0]);
        if (list.Any(n => !ShareSelectionParent(list[0], n)))
        {
            return;
        }

        ClearItemSelection();
        foreach (var item in list)
        {
            item.IsItemSelected = true;
        }

        SelectedItems = list;
        _selectionAnchor = list[0];
        SelectedNode = list[^1];
    }

    public bool ShareSelectionParent(NavigationTreeNodeViewModel first, NavigationTreeNodeViewModel second) =>
        ReferenceEquals(FindParent(first), FindParent(second));

    public IReadOnlyList<NavigationTreeNodeViewModel> GetSelectableSiblings(NavigationTreeNodeViewModel node)
    {
        var parent = FindParent(node);
        if (parent is null)
        {
            return NavigationRoots.Where(n => n.IsSelectableItem).ToList();
        }

        return parent.Children.Where(n => n.IsSelectableItem).ToList();
    }

    public NavigationTreeNodeViewModel? GetParentNode(NavigationTreeNodeViewModel node) => FindParent(node);

    public IReadOnlyList<NavigationTreeNodeViewModel> GetVisibleSelectableNodes()
    {
        var result = new List<NavigationTreeNodeViewModel>();
        CollectVisibleSelectableNodes(NavigationRoots, result);
        return result;
    }

    private static void CollectVisibleSelectableNodes(
        IEnumerable<NavigationTreeNodeViewModel> nodes,
        List<NavigationTreeNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder)
            {
                continue;
            }

            if (node.IsSelectableItem)
            {
                result.Add(node);
            }

            if (node.IsExpanded && node.Children.Count > 0)
            {
                CollectVisibleSelectableNodes(node.Children, result);
            }
        }
    }

    public void SelectSiblingRange(NavigationTreeNodeViewModel anchor, NavigationTreeNodeViewModel end)
    {
        if (!anchor.IsSelectableItem || !end.IsSelectableItem || !ShareSelectionParent(anchor, end))
        {
            return;
        }

        var siblings = GetSelectableSiblings(anchor).ToList();
        var startIndex = siblings.FindIndex(n => ReferenceEquals(n, anchor));
        var endIndex = siblings.FindIndex(n => ReferenceEquals(n, end));
        if (startIndex < 0 || endIndex < 0)
        {
            return;
        }

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        ClearItemSelection();
        var selected = new List<NavigationTreeNodeViewModel>();
        for (var index = startIndex; index <= endIndex; index++)
        {
            var sibling = siblings[index];
            sibling.IsItemSelected = true;
            selected.Add(sibling);
        }

        SelectedItems = selected;
        _selectionAnchor = anchor;
        SelectedNode = end;
    }

    public bool HasOpenableFilesInSelection() =>
        GetOpenableFilePaths().Count > 0;

    public NavigationTreeNodeViewModel? GetPrimarySelectedNode() =>
        SelectedItems.LastOrDefault() ?? SelectedNode;

    public bool HasActivatableSelection()
    {
        if (HasOpenableFilesInSelection())
        {
            return true;
        }

        return GetPrimarySelectedNode() is { IsExpandable: true };
    }

    public IReadOnlyList<string> GetOpenableFilePaths()
    {
        var fromSelection = SelectedItems
            .Where(n => n.Kind == NavigationNodeKind.File && !string.IsNullOrWhiteSpace(n.TargetPath))
            .Select(n => n.TargetPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fromSelection.Count > 0)
        {
            return fromSelection;
        }

        if (SelectedNode?.Kind == NavigationNodeKind.File &&
            !string.IsNullOrWhiteSpace(SelectedNode.TargetPath))
        {
            return [SelectedNode.TargetPath];
        }

        return [];
    }

    public void OpenSelectedFiles()
    {
        var paths = GetOpenableFilePaths();
        if (paths.Count == 0)
        {
            return;
        }

        var errors = new List<string>();
        foreach (var path in paths)
        {
            if (ArchivePathHelper.TrySplit(path, out var archivePath, out var entryPath) &&
                !string.IsNullOrEmpty(entryPath))
            {
                if (!_archiveService.TryExtractEntryToTempAndOpen(archivePath, entryPath, out var archiveError) &&
                    !string.IsNullOrWhiteSpace(archiveError))
                {
                    errors.Add(archiveError);
                }

                continue;
            }

            if (IsRemotePath(path))
            {
                _ = OpenRemoteFileAsync(path);
                continue;
            }

            if (!FileOpenService.TryOpen(path, out var error) && !string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join(Environment.NewLine, errors),
                "Dora Explorer",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    public void OpenArchiveExternally(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        FileOpenService.OpenFiles([archivePath]);
    }

    public string? GetSelectedItemPath()
    {
        var primary = SelectedItems.LastOrDefault() ?? SelectedNode;
        if (primary is null)
        {
            return null;
        }

        if (primary.Kind is NavigationNodeKind.File or NavigationNodeKind.Directory
            or NavigationNodeKind.Drive or NavigationNodeKind.Archive)
        {
            return primary.TargetPath;
        }

        return null;
    }

    public IReadOnlyList<string> GetDeletableItemPaths()
    {
        var paths = GetTransferableItemPaths();
        // Entradas de .rar, .7z e .gz não podem ser excluídas (reescrever o arquivo não é suportado).
        return paths
            .Where(path =>
            {
                if (!ArchivePathHelper.TrySplit(path, out var archivePath, out var entryPath) ||
                    string.IsNullOrEmpty(entryPath))
                {
                    return true;
                }

                return ArchivePathHelper.TryGetFormat(archivePath, out var format) &&
                       ArchivePathHelper.SupportsRewrite(format);
            })
            .ToList();
    }

    /// <summary>
    /// Caminho do item que deve ficar selecionado depois de remover <paramref name="removedPaths"/>: o próximo irmão que não sai
    /// junto, senão o anterior, senão a pasta que continha os removidos (o conteúdo de uma pasta removida sai com ela). Tem de ser
    /// pedido antes de apagar, porque depois a pasta é recarregada e os nós são outros; sem um vizinho selecionado o foco se perde
    /// e as setas deixam de funcionar. <see langword="null"/> se nenhum dos caminhos está na lista visível.
    /// </summary>
    public string? GetPathToSelectAfterRemoving(IReadOnlyCollection<string> removedPaths)
    {
        var removed = removedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.TrimEnd('\\', '/'))
            .ToList();

        bool GoesAway(NavigationTreeNodeViewModel node) =>
            node.TargetPath is { Length: > 0 } path && removed.Any(gone =>
                string.Equals(path, gone, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(gone + '\\', StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(gone + '/', StringComparison.OrdinalIgnoreCase));

        var first = GetVisibleSelectableNodes().FirstOrDefault(GoesAway);
        if (first is null)
        {
            return null;
        }

        var siblings = GetSelectableSiblings(first);
        var firstIndex = siblings.ToList().FindIndex(GoesAway);
        var lastIndex = siblings.ToList().FindLastIndex(GoesAway);

        var neighbour =
            siblings.Skip(lastIndex + 1).FirstOrDefault(node => !GoesAway(node) && !string.IsNullOrWhiteSpace(node.TargetPath)) ??
            siblings.Take(firstIndex).LastOrDefault(node => !GoesAway(node) && !string.IsNullOrWhiteSpace(node.TargetPath)) ??
            GetParentNode(first);

        return string.IsNullOrWhiteSpace(neighbour?.TargetPath) ? null : neighbour.TargetPath;
    }

    public IReadOnlyList<string> GetTransferableItemPaths()
    {
        var fromSelection = SelectedItems
            .Where(n => n.Kind is NavigationNodeKind.File or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive)
            .Where(n => !string.IsNullOrWhiteSpace(n.TargetPath))
            .Select(n => n.TargetPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fromSelection.Count > 0)
        {
            return fromSelection;
        }

        if (SelectedNode is not null &&
            SelectedNode.Kind is (NavigationNodeKind.File or NavigationNodeKind.Directory or NavigationNodeKind.Archive) &&
            !string.IsNullOrWhiteSpace(SelectedNode.TargetPath))
        {
            return [SelectedNode.TargetPath];
        }

        return [];
    }

    public IReadOnlyList<string> GetFavoriteableItemPaths()
    {
        var fromSelection = SelectedItems
            .Where(n => n.Kind is NavigationNodeKind.File or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive or NavigationNodeKind.Drive)
            .Where(n => !n.IsRecycleBinEntry)
            .Where(n => !string.IsNullOrWhiteSpace(n.TargetPath))
            .Select(n => n.TargetPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fromSelection.Count > 0)
        {
            return fromSelection;
        }

        if (SelectedNode is not null &&
            SelectedNode.Kind is (NavigationNodeKind.File or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive or NavigationNodeKind.Drive) &&
            !SelectedNode.IsRecycleBinEntry &&
            !string.IsNullOrWhiteSpace(SelectedNode.TargetPath))
        {
            return [SelectedNode.TargetPath];
        }

        return [];
    }

    /// <summary>
    /// Os itens que a janela Propriedades mostra: a seleção, ou, sem seleção, a linha atual. Itens da Lixeira ficam de fora (têm o menu
    /// próprio e o caminho real deles é interno do Windows).
    /// </summary>
    public IReadOnlyList<NavigationTreeNodeViewModel> GetPropertiesNodes()
    {
        var selected = SelectedItems.Where(node => node.IsSelectableItem && !node.IsRecycleBinEntry).ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        return SelectedNode is { IsSelectableItem: true, IsRecycleBinEntry: false } current ? [current] : [];
    }

    /// <summary>
    /// Texto da entrada de favoritos do menu de contexto: "Remover dos favoritos" se tudo o que está selecionado já é favorito, senão
    /// "Adicionar aos favoritos" (a mesma regra do botão Favorito e do Ctrl+D). <see langword="null"/> quando a seleção não tem arquivo
    /// no disco: itens de servidor ou de dentro de arquivos compactados não entram nos favoritos.
    /// </summary>
    public string? GetFavoriteMenuLabel()
    {
        var paths = GetFavoriteableItemPaths()
            .Where(path => !IsRemotePath(path) && !ArchivePathHelper.IsVirtualArchivePath(path))
            .ToList();

        if (paths.Count == 0)
        {
            return null;
        }

        return paths.All(FavoritesService.IsFavorite) ? "Remover dos favoritos" : "Adicionar aos favoritos";
    }

    public bool TryToggleFavorites(IReadOnlyList<string> paths, out bool added, out string? errorMessage)
    {
        added = false;
        if (!FavoritesService.TryToggle(paths, out added, out errorMessage))
        {
            return false;
        }

        if (added)
        {
            EnsureFavoritesVisible();
        }

        RefreshFavoritesNode();
        RefreshFavoriteMarks();
        return true;
    }

    public void EnsureFavoritesVisible()
    {
        if (_displayOptions.ShowFavorites)
        {
            return;
        }

        _displayOptions.ShowFavorites = true;
        _saveDisplayOptions();
        RebuildNavigationTree();
    }

    public IReadOnlyList<string> GetRecycleBinItemPaths()
    {
        var fromSelection = SelectedItems
            .Where(n => n.IsRecycleBinEntry)
            .Where(n => !string.IsNullOrWhiteSpace(n.TargetPath))
            .Select(n => n.TargetPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fromSelection.Count > 0)
        {
            return fromSelection;
        }

        if (SelectedNode?.IsRecycleBinEntry == true &&
            !string.IsNullOrWhiteSpace(SelectedNode.TargetPath))
        {
            return [SelectedNode.TargetPath];
        }

        return [];
    }

    public bool TryRestoreRecycleBinItems(out string? errorMessage)
    {
        errorMessage = null;
        var paths = GetRecycleBinItemPaths();
        if (paths.Count == 0)
        {
            errorMessage = "Selecione um item da lixeira para restaurar.";
            return false;
        }

        if (!RecycleBinService.TryRestoreItems(paths, out errorMessage))
        {
            return false;
        }

        RefreshRecycleBinNode();
        ClearItemSelection();
        return true;
    }

    public bool TryPermanentlyDeleteRecycleBinItems(out string? errorMessage)
    {
        errorMessage = null;
        var paths = GetRecycleBinItemPaths();
        if (paths.Count == 0)
        {
            errorMessage = "Selecione um item da lixeira para excluir.";
            return false;
        }

        if (!RecycleBinService.TryDeletePermanently(paths, out errorMessage))
        {
            return false;
        }

        RefreshRecycleBinNode();
        ClearItemSelection();
        return true;
    }

    public bool TryDeleteItems(IReadOnlyList<string> paths, FileDeleteMode mode, out string? errorMessage)
    {
        errorMessage = null;

        if (paths.Count == 0)
        {
            errorMessage = "Selecione um ficheiro ou pasta para excluir.";
            return false;
        }

        var remotePaths = paths.Where(IsRemotePath).ToList();
        if (remotePaths.Count > 0)
        {
            if (!TryStartRemoteDelete(remotePaths, out errorMessage))
            {
                return false;
            }

            paths = [.. paths.Where(path => !IsRemotePath(path))];
            if (paths.Count == 0)
            {
                return true;
            }
        }

        var virtualEntries = new List<(string ArchivePath, string EntryPath)>();
        var realPaths = new List<string>();

        foreach (var path in paths)
        {
            if (ArchivePathHelper.TrySplit(path, out var archivePath, out var entryPath) &&
                !string.IsNullOrEmpty(entryPath))
            {
                if (!ArchivePathHelper.TryGetFormat(archivePath, out var format) ||
                    !ArchivePathHelper.SupportsRewrite(format))
                {
                    errorMessage = "Não é possível excluir entradas de arquivos .rar, .7z ou .gz.";
                    return false;
                }

                virtualEntries.Add((archivePath, entryPath));
            }
            else
            {
                realPaths.Add(path);
            }
        }

        var archivesToRefresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (virtualEntries.Count > 0)
        {
            foreach (var group in virtualEntries.GroupBy(e => e.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                var entryPaths = group.Select(e => e.EntryPath).ToList();
                if (!_archiveService.TryRemoveEntries(group.Key, entryPaths, out errorMessage))
                {
                    return false;
                }

                archivesToRefresh.Add(group.Key);
            }
        }

        if (realPaths.Count > 0)
        {
            if (!_fileOperationService.TryDelete(realPaths, mode, out errorMessage))
            {
                return false;
            }
        }

        foreach (var archivePath in archivesToRefresh)
        {
            RefreshDirectory(archivePath);
            var parent = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                // Keep parent listing in sync (size/date of archive file).
                RefreshDirectory(parent);
            }
        }

        var parents = realPaths
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var parent in parents)
        {
            RefreshDirectory(parent!);
        }

        ClearItemSelection();
        return true;
    }

    public void RefreshPane()
    {
        var directory = GetWorkingDirectory();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            RefreshDirectory(directory);
            return;
        }

        RebuildNavigationTree();
    }

    /// <summary>
    /// Recarrega as pastas dadas que este painel mostra como linha da árvore; as que ele não mostra são ignoradas. Depois de soltar ou
    /// colar itens, a pasta que mudou nem sempre é a que está aberta no painel (soltar cai numa linha qualquer): sem isto a linha
    /// dela ficava com as contagens e a seta de expandir antigas, no painel que não executou a operação.
    /// </summary>
    public void RefreshShownDirectories(IEnumerable<string?> directories)
    {
        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindNodeByPath(directory!) is not null)
            {
                RefreshDirectory(directory!);
            }
        }
    }

    public void RefreshNode(NavigationTreeNodeViewModel node)
    {
        if (node.IsRecycleBinEntry)
        {
            RefreshDirectory(NavigationTreeService.RecycleBinPath);
            return;
        }

        if (node.IsFavoriteEntry)
        {
            RefreshDirectory(NavigationTreeService.FavoritesPath);
            return;
        }

        if (node.Kind is NavigationNodeKind.File)
        {
            var parentPath = Path.GetDirectoryName(node.TargetPath);
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                RefreshDirectory(parentPath);
            }

            return;
        }

        if (node.Kind is NavigationNodeKind.Drive or NavigationNodeKind.Directory
            or NavigationNodeKind.Archive
            or NavigationNodeKind.RecycleBin
            or NavigationNodeKind.Favorites
            or NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp)
        {
            if (!string.IsNullOrWhiteSpace(node.TargetPath))
            {
                RefreshDirectory(node.TargetPath);
            }
            else
            {
                ReloadNodeChildrenKeepingExpansion(node);
            }

            return;
        }

        RefreshPane();
    }

    public string GetWorkingDirectory()
    {
        if (SelectedNode is not null && !string.IsNullOrWhiteSpace(SelectedNode.TargetPath))
        {
            switch (SelectedNode.Kind)
            {
                case NavigationNodeKind.File:
                    return Path.GetDirectoryName(SelectedNode.TargetPath) ?? string.Empty;
                case NavigationNodeKind.Directory:
                case NavigationNodeKind.Drive:
                case NavigationNodeKind.Archive:
                    return SelectedNode.TargetPath;
                case NavigationNodeKind.RecycleBin:
                    return NavigationTreeService.RecycleBinPath;
                case NavigationNodeKind.Favorites:
                    return NavigationTreeService.FavoritesPath;
            }
        }

        if (NetworkFileService.TryParseRemotePath(CurrentPath, out _, out _, out _))
        {
            return CurrentPath;
        }

        return ResolveLocalDirectoryPath(CurrentPath);
    }

    public bool TryResolveDropDirectory(
        NavigationTreeNodeViewModel? hoverNode,
        out string directory,
        out string? errorMessage)
    {
        directory = string.Empty;
        errorMessage = null;

        if (hoverNode is not null && !string.IsNullOrWhiteSpace(hoverNode.TargetPath))
        {
            switch (hoverNode.Kind)
            {
                case NavigationNodeKind.Directory:
                case NavigationNodeKind.Drive:
                    directory = hoverNode.TargetPath;
                    break;
                case NavigationNodeKind.File:
                case NavigationNodeKind.Archive:
                    directory = Path.GetDirectoryName(hoverNode.TargetPath) ?? string.Empty;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = GetWorkingDirectory();
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            errorMessage = "Abra uma pasta no painel de destino.";
            return false;
        }

        if (NetworkFileService.TryParseRemotePath(directory, out _, out _, out _))
        {
            errorMessage = "Não é possível largar ficheiros em caminhos de rede.";
            return false;
        }

        if (IsInvalidArchiveDestination(directory, out errorMessage))
        {
            return false;
        }

        if (!Directory.Exists(directory))
        {
            errorMessage = "A pasta de destino não existe.";
            return false;
        }

        return true;
    }

    public static bool IsLocalFileSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (NetworkFileService.TryParseRemotePath(path, out _, out _, out _))
        {
            return false;
        }

        if (ArchivePathHelper.IsVirtualArchivePath(path))
        {
            return false;
        }

        return File.Exists(path) || Directory.Exists(path);
    }

    public IReadOnlyList<string> GetLocalTransferableItemPaths() =>
        GetTransferableItemPaths()
            .Where(IsLocalFileSystemPath)
            .ToList();

    private bool IsInvalidArchiveDestination(string destinationDirectory, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            errorMessage = "Pasta de destino inválida.";
            return true;
        }

        if (ArchivePathHelper.IsVirtualArchivePath(destinationDirectory) ||
            (File.Exists(destinationDirectory) && ArchivePathHelper.IsArchiveFile(destinationDirectory)))
        {
            errorMessage = "Não é possível copiar ou mover para dentro de um arquivo compactado.";
            return true;
        }

        return false;
    }

    private bool TryCopyOrExtract(
        string sourcePath,
        string destinationDirectory,
        out string? errorMessage)
    {
        if (ArchivePathHelper.TrySplit(sourcePath, out var archivePath, out var entryPath) &&
            !string.IsNullOrEmpty(entryPath))
        {
            return _archiveService.TryExtractEntry(archivePath, entryPath, destinationDirectory, out errorMessage);
        }

        return _fileOperationService.TryCopy(sourcePath, destinationDirectory, out errorMessage);
    }

    private bool TryMoveOrExtract(
        string sourcePath,
        string destinationDirectory,
        out string? errorMessage)
    {
        if (ArchivePathHelper.TrySplit(sourcePath, out var archivePath, out var entryPath) &&
            !string.IsNullOrEmpty(entryPath))
        {
            if (!ArchivePathHelper.TryGetFormat(archivePath, out var format))
            {
                errorMessage = "Formato de arquivo não suportado.";
                return false;
            }

            // .rar, .7z e .gz: só copiar (sem reescrever o arquivo).
            if (!ArchivePathHelper.SupportsRewrite(format))
            {
                return _archiveService.TryExtractEntry(archivePath, entryPath, destinationDirectory, out errorMessage);
            }

            if (!_archiveService.TryExtractEntry(archivePath, entryPath, destinationDirectory, out errorMessage))
            {
                return false;
            }

            if (!_archiveService.TryRemoveEntries(archivePath, [entryPath], out errorMessage))
            {
                return false;
            }

            RefreshDirectory(archivePath);
            var archiveParent = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(archiveParent))
            {
                RefreshDirectory(archiveParent);
            }

            return true;
        }

        return _fileOperationService.TryMove(sourcePath, destinationDirectory, out errorMessage);
    }

    private static string ResolveLocalDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }

        return path;
    }

    public bool TryCreateFolder(string folderName, out string? errorMessage)
    {
        var parent = GetWorkingDirectory();
        if (IsRemotePath(parent))
        {
            return TryCreateRemoteFolder(parent, folderName, out errorMessage);
        }

        if (ArchivePathHelper.IsVirtualArchivePath(parent) ||
            (File.Exists(parent) && ArchivePathHelper.IsArchiveFile(parent)))
        {
            errorMessage = "Não é possível criar pastas dentro de um arquivo compactado.";
            return false;
        }

        if (!_fileOperationService.TryCreateDirectory(parent, folderName, out errorMessage))
        {
            return false;
        }

        RefreshDirectory(parent);
        return true;
    }

    public bool TryCreateFolderWithInlineRename(out string? errorMessage)
    {
        errorMessage = null;
        var parent = GetWorkingDirectory();
        if (IsRemotePath(parent))
        {
            return TryCreateRemoteFolderWithFreeName(parent, out errorMessage);
        }

        if (ArchivePathHelper.IsVirtualArchivePath(parent) ||
            (File.Exists(parent) && ArchivePathHelper.IsArchiveFile(parent)))
        {
            errorMessage = "Não é possível criar pastas dentro de um arquivo compactado.";
            return false;
        }

        var folderName = GetUniqueFolderName(parent, "Nova pasta");
        if (!TryCreateFolder(folderName, out errorMessage))
        {
            return false;
        }

        var newPath = Path.Combine(parent, folderName);
        RefreshDirectory(parent);
        ExpandToPath(newPath);
        var node = FindNodeByPath(newPath);
        if (node is not null)
        {
            BeginRenameNode(node);
        }

        return true;
    }

    public void BeginRenameSelected()
    {
        var source = GetSelectedItemPath();
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (SelectedNode is not null)
        {
            BeginRenameNode(SelectedNode);
        }
    }

    public void BeginRenameNode(NavigationTreeNodeViewModel node)
    {
        if (RenamingNode is not null && !ReferenceEquals(RenamingNode, node))
        {
            CancelRename();
        }

        RenamingNode = node;
        _renameCancelled = false;
        node.IsRenaming = true;
        node.RenameText = node.Name;
        RenamingStarted?.Invoke(this, EventArgs.Empty);
    }

    public void CommitRename()
    {
        if (RenamingNode is null || _renameCancelled)
        {
            CancelRename();
            return;
        }

        var node = RenamingNode;
        var originalName = node.Name;
        var newName = node.RenameText.Trim();
        var source = node.TargetPath;

        node.IsRenaming = false;
        RenamingNode = null;
        RenamingEnded?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (string.Equals(newName, originalName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryRenamePath(source, newName, out _, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageDialog.Show(
                    System.Windows.Application.Current.MainWindow,
                    error,
                    "Renomear");
            }
        }
    }

    public void CancelRename()
    {
        if (RenamingNode is null)
        {
            return;
        }

        var node = RenamingNode;
        _renameCancelled = true;
        node.IsRenaming = false;
        node.RenameText = node.Name;
        RenamingNode = null;
        _renameCancelled = false;
        RenamingEnded?.Invoke(this, EventArgs.Empty);
    }

    private static string GetUniqueFolderName(string parentPath, string baseName)
    {
        if (!NameIsTaken(parentPath, baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var numbered = $"{baseName} ({index})";
            if (!NameIsTaken(parentPath, numbered))
            {
                return numbered;
            }
        }
    }

    // Um arquivo com o nome também ocupa o lugar: criar a pasta falharia do mesmo jeito que com uma pasta.
    private static bool NameIsTaken(string parentPath, string name)
    {
        var candidate = Path.Combine(parentPath, name);
        return Directory.Exists(candidate) || File.Exists(candidate);
    }

    public bool TryRenameSelected(string newName, out string? newPath, out string? errorMessage)
    {
        var source = GetSelectedItemPath();
        if (string.IsNullOrWhiteSpace(source))
        {
            newPath = null;
            errorMessage = "Selecione um ficheiro ou pasta para renomear.";
            return false;
        }

        return TryRenamePath(source, newName, out newPath, out errorMessage);
    }

    public bool TryRenamePath(string sourcePath, string newName, out string? newPath, out string? errorMessage)
    {
        newPath = null;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            errorMessage = "Selecione um ficheiro ou pasta para renomear.";
            return false;
        }

        if (IsRemotePath(sourcePath))
        {
            return TryRenameRemote(sourcePath, newName, out newPath, out errorMessage);
        }

        var parent = Path.GetDirectoryName(sourcePath);
        if (!_fileOperationService.TryRename(sourcePath, newName, out newPath, out errorMessage))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parent))
        {
            RefreshDirectory(parent);
        }

        if (!string.IsNullOrWhiteSpace(newPath))
        {
            SelectedPath = newPath;
            CurrentPath = Directory.Exists(newPath) ? newPath : parent ?? CurrentPath;
            SelectedNode = FindNodeByPath(newPath) ?? SelectedNode;
        }

        return true;
    }

    public bool TryCopyTo(string destinationDirectory, out string? errorMessage)
    {
        var source = GetSelectedItemPath();
        if (string.IsNullOrWhiteSpace(source))
        {
            errorMessage = "Selecione um ficheiro ou pasta para copiar.";
            return false;
        }

        return TryCopyItem(source, destinationDirectory, out errorMessage);
    }

    public async Task<(bool Success, string? ErrorMessage)> TryCopyItemAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return (false, "Selecione um ficheiro ou pasta para copiar.");
        }

        if (IsRemotePath(sourcePath) || IsRemotePath(destinationDirectory))
        {
            return await TransferRemoteAsync(sourcePath, destinationDirectory, move: false, progress, cancellationToken)
                .ConfigureAwait(true);
        }

        if (IsInvalidArchiveDestination(destinationDirectory, out var archiveError))
        {
            return (false, archiveError);
        }

        if (ArchivePathHelper.IsVirtualArchivePath(sourcePath))
        {
            string? extractError = null;
            var ok = await Task.Run(
                    () => TryCopyOrExtract(sourcePath, destinationDirectory, out extractError),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!ok)
            {
                return (false, extractError);
            }

            RefreshDirectory(destinationDirectory);
            return (true, null);
        }

        var result = await _fileOperationService.TryCopyAsync(sourcePath, destinationDirectory, progress, cancellationToken)
            .ConfigureAwait(true);
        if (!result.Success)
        {
            return result;
        }

        RefreshDirectory(destinationDirectory);
        return (true, null);
    }

    public bool TryCopyItem(string sourcePath, string destinationDirectory, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            errorMessage = "Selecione um ficheiro ou pasta para copiar.";
            return false;
        }

        if (IsRemotePath(sourcePath) || IsRemotePath(destinationDirectory))
        {
            var remote = RunBlocking(() => TransferRemoteAsync(sourcePath, destinationDirectory, move: false, null, CancellationToken.None));
            errorMessage = remote.ErrorMessage;
            return remote.Success;
        }

        if (IsInvalidArchiveDestination(destinationDirectory, out errorMessage))
        {
            return false;
        }

        if (!TryCopyOrExtract(sourcePath, destinationDirectory, out errorMessage))
        {
            return false;
        }

        RefreshDirectory(destinationDirectory);
        return true;
    }

    public bool TryMoveTo(string destinationDirectory, out string? errorMessage)
    {
        var source = GetSelectedItemPath();
        if (string.IsNullOrWhiteSpace(source))
        {
            errorMessage = "Selecione um ficheiro ou pasta para mover.";
            return false;
        }

        return TryMoveItem(source, destinationDirectory, out errorMessage);
    }

    public async Task<(bool Success, string? ErrorMessage)> TryMoveItemAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return (false, "Selecione um ficheiro ou pasta para mover.");
        }

        if (IsRemotePath(sourcePath) || IsRemotePath(destinationDirectory))
        {
            return await TransferRemoteAsync(sourcePath, destinationDirectory, move: true, progress, cancellationToken)
                .ConfigureAwait(true);
        }

        if (IsInvalidArchiveDestination(destinationDirectory, out var archiveError))
        {
            return (false, archiveError);
        }

        if (ArchivePathHelper.IsVirtualArchivePath(sourcePath))
        {
            string? extractError = null;
            var ok = await Task.Run(
                    () => TryMoveOrExtract(sourcePath, destinationDirectory, out extractError),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!ok)
            {
                return (false, extractError);
            }

            RefreshDirectory(destinationDirectory);
            return (true, null);
        }

        var result = await _fileOperationService.TryMoveAsync(sourcePath, destinationDirectory, progress, cancellationToken)
            .ConfigureAwait(true);
        if (!result.Success)
        {
            return result;
        }

        var sourceParent = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceParent))
        {
            RefreshDirectory(sourceParent);
        }

        RefreshDirectory(destinationDirectory);
        return (true, null);
    }

    public bool TryMoveItem(string sourcePath, string destinationDirectory, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            errorMessage = "Selecione um ficheiro ou pasta para mover.";
            return false;
        }

        if (IsRemotePath(sourcePath) || IsRemotePath(destinationDirectory))
        {
            var remote = RunBlocking(() => TransferRemoteAsync(sourcePath, destinationDirectory, move: true, null, CancellationToken.None));
            errorMessage = remote.ErrorMessage;
            return remote.Success;
        }

        if (IsInvalidArchiveDestination(destinationDirectory, out errorMessage))
        {
            return false;
        }

        if (!TryMoveOrExtract(sourcePath, destinationDirectory, out errorMessage))
        {
            return false;
        }

        if (!ArchivePathHelper.IsVirtualArchivePath(sourcePath))
        {
            var sourceParent = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(sourceParent))
            {
                RefreshDirectory(sourceParent);
            }
        }

        RefreshDirectory(destinationDirectory);
        return true;
    }

    public async Task<(bool Success, string? ErrorMessage)> TryCompressItemsAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count == 0)
        {
            return (false, "Selecione um ficheiro ou pasta para compactar.");
        }

        if (sourcePaths.Any(IsRemotePath) || IsRemotePath(destinationDirectory))
        {
            return await CompressWithRemoteAsync(sourcePaths, destinationDirectory, format, progress, cancellationToken)
                .ConfigureAwait(true);
        }

        foreach (var sourcePath in sourcePaths)
        {
            if (ArchivePathHelper.IsVirtualArchivePath(sourcePath))
            {
                return (false, "Não é possível compactar itens de dentro de um arquivo compactado.");
            }
        }

        if (IsInvalidArchiveDestination(destinationDirectory, out var archiveError))
        {
            return (false, archiveError);
        }

        var result = await _fileOperationService.TryCompressAsync(
                sourcePaths,
                destinationDirectory,
                format,
                progress,
                cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            return (false, result.ErrorMessage);
        }

        RefreshDirectory(destinationDirectory);
        return (true, null);
    }
    public async Task<(bool Success, string? ErrorMessage)> TryConnectNetworkAsync(
        NetworkConnectionInfo connection,
        string? profileId = null,
        IReadOnlyList<FileSystemEntry>? prefetchedEntries = null,
        CancellationToken cancellationToken = default)
    {
        if (connection.Protocol is NetworkProtocol.Scp or NetworkProtocol.Tftp)
        {
            var transfer = await _networkFileService.ExecuteTransferAsync(connection, cancellationToken)
                .ConfigureAwait(true);
            if (!transfer.Success)
            {
                if (!string.IsNullOrWhiteSpace(profileId))
                {
                    _networkHealth.SetHealth(profileId, healthy: false);
                    ApplyNetworkHealthToTree();
                }

                return (false, transfer.ErrorMessage);
            }

            _networkSessions.Register(connection);
            EnsureProfileAttached(connection, profileId, entries: null, navigate: false);
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                _networkHealth.SetHealth(profileId, healthy: true);
                ApplyNetworkHealthToTree();
            }

            NotifyNetworkServicesChanged();
            return (true, null);
        }

        IReadOnlyList<FileSystemEntry> entries;
        if (prefetchedEntries is not null)
        {
            entries = prefetchedEntries;
        }
        else
        {
            var listResult = await _networkFileService.ConnectAndListAsync(connection, cancellationToken)
                .ConfigureAwait(true);
            if (!listResult.Success)
            {
                if (!string.IsNullOrWhiteSpace(profileId))
                {
                    _networkHealth.SetHealth(profileId, healthy: false);
                    ApplyNetworkHealthToTree();
                }

                return (false, listResult.ErrorMessage);
            }

            entries = listResult.Entries;
        }

        _networkSessions.Register(connection);
        var connectionNode = EnsureProfileAttached(connection, profileId, entries, navigate: true);
        if (connectionNode is null)
        {
            _networkSessions.UnregisterProtocol(connection.Protocol);
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                _networkHealth.SetHealth(profileId, healthy: false);
                ApplyNetworkHealthToTree();
            }

            return (false, "Ative este destino em Exibir na árvore.");
        }

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            _networkHealth.SetHealth(profileId, healthy: true);
            ApplyNetworkHealthToTree();
        }

        NotifyNetworkServicesChanged();
        return (true, null);
    }

    public void OpenNetworkClient(NavigationNodeKind kind)
    {
        if (kind is not (NavigationNodeKind.Ftp or NavigationNodeKind.Sftp
            or NavigationNodeKind.Scp or NavigationNodeKind.Tftp))
        {
            return;
        }

        _ = OpenNetworkClientAsync(MapKindToProtocol(kind));
    }

    public void ConnectNetworkProfile(NavigationTreeNodeViewModel node) =>
        _ = ConnectNetworkProfileAsync(node);

    public void EditNetworkProfile(NavigationTreeNodeViewModel node) =>
        _ = EditNetworkProfileAsync(node);

    public void DeleteNetworkProfile(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            return;
        }

        var owner = Application.Current.MainWindow;
        if (owner is null)
        {
            return;
        }

        if (!MessageDialog.Confirm(owner, $"Excluir o cliente \"{node.Name}\"?"))
        {
            return;
        }

        var profileId = node.NetworkProfileId!;
        var protocol = GuessProtocolFromProfileNode(node);
        App.SettingsService.DeleteNetworkClientProfile(profileId);
        _networkHealth.Remove(profileId);

        if (protocol is not null &&
            NetworkFileService.TryParseRemotePath(node.TargetPath ?? string.Empty, out var pathProtocol, out _, out _) &&
            pathProtocol == protocol.Value)
        {
            _networkSessions.UnregisterProtocol(protocol.Value);
        }

        RefreshNetworkClientProfiles();
    }

    public void TestNetworkProfile(NavigationTreeNodeViewModel node) =>
        _ = TestNetworkProfileAsync(node);

    private async Task OpenNetworkClientAsync(NetworkProtocol protocol, NetworkClientProfile? editProfile = null)
    {
        var owner = Application.Current.MainWindow;
        if (owner is null)
        {
            return;
        }

        var rootNode = FindNetworkRoot(protocol);
        var isConnected = _networkSessions.HasProtocol(protocol)
                          && protocol is not (NetworkProtocol.Scp or NetworkProtocol.Tftp)
                          && rootNode?.Children.Any(c => !string.IsNullOrWhiteSpace(c.TargetPath)) == true;

        if (!NetworkClientDialog.TryShow(
                owner,
                protocol,
                isConnected,
                out var action,
                out var connection,
                out var editingProfileId,
                editProfile))
        {
            return;
        }

        if (action == NetworkDialogAction.Stop)
        {
            StopNetworkProtocol(protocol);
            return;
        }

        if (connection is null)
        {
            return;
        }

        if (action == NetworkDialogAction.Save)
        {
            SaveOrUpdateProfile(connection, editingProfileId ?? editProfile?.Id);
            RefreshNetworkClientProfiles();
            MessageDialog.Show(owner, "Cliente guardado.");
            return;
        }

        if (action != NetworkDialogAction.Start)
        {
            return;
        }

        // Ligar também grava/atualiza o perfil para aparecer na árvore.
        var profileId = SaveOrUpdateProfile(connection, editingProfileId ?? editProfile?.Id);
        RefreshNetworkClientProfiles();

        var (success, error) = await TryConnectNetworkAsync(connection, profileId: profileId).ConfigureAwait(true);
        if (!success)
        {
            MessageDialog.Show(owner, error ?? "Não foi possível ligar.");
            return;
        }

        if (protocol is NetworkProtocol.Scp or NetworkProtocol.Tftp)
        {
            MessageDialog.Show(owner, "Transferência concluída com sucesso.");
        }
    }

    private async Task ConnectNetworkProfileAsync(NavigationTreeNodeViewModel node)
    {
        var owner = Application.Current.MainWindow;
        if (owner is null || string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            return;
        }

        var profile = App.SettingsService.TryGetNetworkClientProfile(node.NetworkProfileId);
        if (profile is null)
        {
            MessageDialog.Show(owner, "Perfil de cliente não encontrado.");
            return;
        }

        var (success, error) = await TryConnectNetworkAsync(profile.ToConnection(), profileId: profile.Id)
            .ConfigureAwait(true);
        if (!success)
        {
            MessageDialog.Show(owner, error ?? "Não foi possível ligar.");
            return;
        }

        if (profile.Protocol is NetworkProtocol.Scp or NetworkProtocol.Tftp)
        {
            MessageDialog.Show(owner, "Transferência concluída com sucesso.");
        }
    }

    private async Task EditNetworkProfileAsync(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            return;
        }

        var profile = App.SettingsService.TryGetNetworkClientProfile(node.NetworkProfileId);
        if (profile is null)
        {
            return;
        }

        await OpenNetworkClientAsync(profile.Protocol, profile).ConfigureAwait(true);
    }

    private async Task TestNetworkProfileAsync(NavigationTreeNodeViewModel node)
    {
        var owner = Application.Current.MainWindow;
        if (owner is null || string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            return;
        }

        var profile = App.SettingsService.TryGetNetworkClientProfile(node.NetworkProfileId);
        if (profile is null)
        {
            MessageDialog.Show(owner, "Perfil de cliente não encontrado.");
            return;
        }

        var ok = await _networkHealth.TestAndUpdateAsync(profile).ConfigureAwait(true);
        ApplyNetworkHealthToTree();
        MessageDialog.Show(
            owner,
            ok
                ? $"Ligação a \"{profile.DisplayName}\" validada com sucesso."
                : $"Falha ao validar \"{profile.DisplayName}\".");
    }

    private static string SaveOrUpdateProfile(NetworkConnectionInfo connection, string? existingId)
    {
        var profile = NetworkClientProfile.FromConnection(connection);
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            profile.Id = existingId;
        }
        else
        {
            // Reutilizar perfil com o mesmo nome+protocolo se existir.
            var existing = App.SettingsService.LoadNetworkClientProfiles(connection.Protocol)
                .FirstOrDefault(p => string.Equals(p.DisplayName, profile.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                profile.Id = existing.Id;
            }
        }

        App.SettingsService.SaveNetworkClientProfile(profile);
        return profile.Id;
    }

    private void RefreshNetworkClientProfiles()
    {
        var expandedKeys = CollectExpandedKeys();
        RebuildNavigationTree();
        RestoreExpansion(NavigationRoots, expandedKeys);
        ApplyNetworkHealthToTree();
        NavigationTreeRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private NavigationTreeNodeViewModel? EnsureProfileAttached(
        NetworkConnectionInfo connection,
        string? profileId,
        IReadOnlyList<FileSystemEntry>? entries,
        bool navigate)
    {
        var rootNode = FindNetworkRoot(connection.Protocol);
        if (rootNode is null)
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(connection.DisplayName)
            ? connection.Host
            : connection.DisplayName.Trim();
        var remotePath = NetworkFileService.BuildRemotePath(
            connection.Protocol,
            connection.Host,
            connection.RemotePath);

        NavigationTreeNodeViewModel? connectionNode = null;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            connectionNode = rootNode.Children.FirstOrDefault(c =>
                string.Equals(c.NetworkProfileId, profileId, StringComparison.Ordinal));
        }

        connectionNode ??= rootNode.Children.FirstOrDefault(c =>
            string.Equals(c.Name, displayName, StringComparison.OrdinalIgnoreCase));

        if (connectionNode is null)
        {
            connectionNode = new NavigationTreeNodeViewModel(displayName, NavigationNodeKind.Directory)
            {
                IconSource = ShellIconService.GetNetworkServerIcon(),
                NetworkProfileId = profileId
            };
            rootNode.Children.Add(connectionNode);
        }
        else
        {
            connectionNode.SetName(displayName);
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                connectionNode.NetworkProfileId = profileId;
            }
        }

        connectionNode.TargetPath = remotePath;
        connectionNode.Children.Clear();
        connectionNode.ChildrenLoaded = false;

        var canBrowse = connection.Protocol is NetworkProtocol.Ftp or NetworkProtocol.Sftp or NetworkProtocol.Scp;
        if (canBrowse)
        {
            if (entries is not null)
            {
                ApplyRemoteEntries(connectionNode, entries);
            }
            else
            {
                connectionNode.Children.Add(
                    new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
            }
        }
        else
        {
            connectionNode.ChildrenLoaded = true;
            connectionNode.FolderCount = 0;
            connectionNode.FileCount = 0;
        }

        rootNode.ChildrenLoaded = true;
        rootNode.IsExpanded = true;

        if (canBrowse)
        {
            // Expandir depois de preencher: OnNavigationNodeExpanded já não recarrega caminhos remotos.
            connectionNode.IsExpanded = true;
            if (entries is null)
            {
                OnNavigationNodeExpanding(connectionNode);
                LoadNodeChildren(connectionNode);
            }
        }

        if (navigate)
        {
            SelectedNode = connectionNode;
            NavigateToPath(remotePath);
        }

        return connectionNode;
    }

    private void ApplyRemoteEntries(
        NavigationTreeNodeViewModel node,
        IReadOnlyList<FileSystemEntry> entries)
    {
        var sorted = TreeEntrySorter.Sort(entries, SortOptions);
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
                child.Children.Add(
                    new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
                node.Children.Add(child);
            }
            else
            {
                var file = new NavigationTreeNodeViewModel(
                    entry.Name,
                    NavigationNodeKind.File,
                    entry.FullPath)
                {
                    SizeInBytes = entry.SizeInBytes,
                    LastModified = entry.LastModified
                };
                var fileName = entry.Name;
                file.SetIconFactory(() => ShellIconService.GetRemoteItemIcon(fileName, isDirectory: false));
                node.Children.Add(file);
            }
        }

        node.ChildrenLoaded = true;
        AppLogService.Info($"Carregado: {node.TargetPath} ({node.FolderCount} pastas, {node.FileCount} ficheiros)");
    }

    public void StopNetworkProtocol(NetworkProtocol protocol)
    {
        _networkSessions.UnregisterProtocol(protocol);

        var rootNode = FindNetworkRoot(protocol);
        if (rootNode is not null)
        {
            foreach (var child in rootNode.Children.Where(c => c.IsNetworkClientProfile).ToList())
            {
                if (!string.IsNullOrWhiteSpace(child.NetworkProfileId))
                {
                    // Desligar não implica servidor down — volta a cinza até novo teste/ligação.
                    _networkHealth.Remove(child.NetworkProfileId);
                }

                child.IsExpanded = false;
                child.TargetPath = null;
                child.Children.Clear();
                child.ChildrenLoaded = false;
                child.FolderCount = 0;
                child.FileCount = 0;
                if (protocol is NetworkProtocol.Ftp or NetworkProtocol.Sftp or NetworkProtocol.Scp)
                {
                    child.Children.Add(
                        new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true });
                }
                else
                {
                    child.ChildrenLoaded = true;
                }
            }
        }

        if (NetworkFileService.TryParseRemotePath(CurrentPath, out var currentProtocol, out _, out _) &&
            currentProtocol == protocol)
        {
            CurrentPath = string.Empty;
            SelectedPath = string.Empty;
            ClearItemSelection();
        }

        ApplyNetworkHealthToTree();
        NotifyNetworkServicesChanged();
    }

    private void OnNetworkHealthChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyNetworkHealthToTree);
    }

    private void ApplyNetworkHealthToTree()
    {
        foreach (var root in NavigationRoots)
        {
            ApplyNetworkHealthRecursive(root);
        }
    }

    private void ApplyNetworkHealthRecursive(NavigationTreeNodeViewModel node)
    {
        if (node.IsNetworkClientProfile && !string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            var health = _networkHealth.GetHealth(node.NetworkProfileId);
            // Força notificação mesmo quando o valor lógico não muda (ex.: re-teste OK).
            if (node.IsNetworkHealthy == health)
            {
                node.IsNetworkHealthy = null;
            }

            node.IsNetworkHealthy = health;
        }

        foreach (var child in node.Children)
        {
            ApplyNetworkHealthRecursive(child);
        }
    }

    private NetworkProtocol? GuessProtocolFromProfileNode(NavigationTreeNodeViewModel node)
    {
        for (var parent = GetParentNode(node); parent is not null; parent = GetParentNode(parent))
        {
            if (parent.Kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp
                or NavigationNodeKind.Scp or NavigationNodeKind.Tftp)
            {
                return MapKindToProtocol(parent.Kind);
            }
        }

        if (!string.IsNullOrWhiteSpace(node.NetworkProfileId))
        {
            return App.SettingsService.TryGetNetworkClientProfile(node.NetworkProfileId)?.Protocol;
        }

        return null;
    }

    public void NavigateToSearchResult(SearchResultItem item, string? searchRoot = null)
    {
        var navId = ++_searchNavigationGeneration;

        _skipAccordionCollapse = true;
        _centerNavigatedNode = true;
        _suppressSelectionUpdates = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(searchRoot))
            {
                EnsurePathExpandedInTree(searchRoot, navId);
                if (navId != _searchNavigationGeneration)
                {
                    return;
                }
            }

            NavigationTreeNodeViewModel? targetNode;

            if (item.IsDirectory)
            {
                targetNode = EnsurePathExpandedInTree(item.FullPath, navId);
                if (navId != _searchNavigationGeneration)
                {
                    return;
                }

                CurrentPath = item.FullPath;
                SelectedPath = item.FullPath;
            }
            else
            {
                var parent = Path.GetDirectoryName(item.FullPath);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    ClearCenterNavigatedNode();
                    return;
                }

                EnsurePathExpandedInTree(parent, navId);
                if (navId != _searchNavigationGeneration)
                {
                    return;
                }

                CurrentPath = parent;
                SelectedPath = item.FullPath;

                targetNode = FindNodeByPath(item.FullPath);
                if (targetNode is null)
                {
                    var parentNode = FindNodeByPath(parent);
                    if (parentNode is not null)
                    {
                        if (IsDirectoryPopulationInProgress(parentNode))
                        {
                            targetNode = _navigationTreeService.TryMaterializeChild(
                                parentNode,
                                item.FullPath,
                                _viewSettings.Options);
                        }
                        else
                        {
                            ReloadDirectoryChildrenForNavigation(parentNode);
                            targetNode = FindNodeByPath(item.FullPath);
                        }
                    }
                }
            }

            if (navId != _searchNavigationGeneration)
            {
                return;
            }

            if (targetNode is not null)
            {
                CollapseOtherHierarchies(targetNode);
                SelectedNode = targetNode;
                if (targetNode.IsSelectableItem)
                {
                    SetSingleItemSelection(targetNode);
                }
            }

            if (navId != _searchNavigationGeneration)
            {
                return;
            }

            var capturedNavId = navId;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (capturedNavId != _searchNavigationGeneration)
                {
                    return;
                }

                NavigationApplied?.Invoke(this, EventArgs.Empty);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally
        {
            _skipAccordionCollapse = false;
            _suppressSelectionUpdates = false;

            if (navId != _searchNavigationGeneration)
            {
                ClearCenterNavigatedNode(navId);
            }
        }
    }

    public void ApplySortOptions(TreeSortOptions options)
    {
        SortOptions = options;
        foreach (var root in NavigationRoots)
        {
            ResortLoadedDirectoriesRecursive(root);
        }
    }

    private void ResortLoadedDirectoriesRecursive(NavigationTreeNodeViewModel node)
    {
        if (node.ChildrenLoaded && node.IsExpandable && !string.IsNullOrWhiteSpace(node.TargetPath))
        {
            if (NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
            {
                ReloadNodeChildrenKeepingExpansion(node);
            }
            else
            {
                _navigationTreeService.ResortDirectoryChildren(node, _fileSystemService, SortOptions);
            }
        }

        foreach (var child in node.Children)
        {
            ResortLoadedDirectoriesRecursive(child);
        }
    }

    private void ReloadNodeChildrenKeepingExpansion(NavigationTreeNodeViewModel node)
    {
        var expandedKeys = CollectExpandedKeys();
        InvalidateNodeIndex(node);
        node.LoadGeneration++;
        node.Children.Clear();
        node.ChildrenLoaded = false;
        if (node.IsExpanded)
        {
            LoadNodeChildren(node);
        }
        else if (node.IsExpandable)
        {
            _navigationTreeService.RefreshCollapsedDirectoryPlaceholder(
                node,
                _fileSystemService,
                _networkSessions,
                _networkFileService);
        }

        RestoreExpansion(NavigationRoots, expandedKeys);
        PaneContentRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateNodeIndex(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        if (!NetworkFileService.TryParseRemotePath(node.TargetPath, out _, out _, out _))
        {
            _index.Invalidate(node.TargetPath);
        }
    }

    private void LoadNodeChildren(NavigationTreeNodeViewModel node) =>
        _navigationTreeService.LoadDirectoryChildren(
            node,
            _fileSystemService,
            _networkSessions,
            _networkFileService,
            SortOptions);

    private void RefreshDirectory(string directoryPath)
    {
        if (string.Equals(directoryPath, NavigationTreeService.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
        {
            RefreshRecycleBinNode();
            return;
        }

        if (string.Equals(directoryPath, NavigationTreeService.FavoritesPath, StringComparison.OrdinalIgnoreCase))
        {
            RefreshFavoritesNode();
            return;
        }

        var node = FindNodeByPath(directoryPath);
        if (node is null)
        {
            RebuildNavigationTree();
            ExpandToPath(directoryPath);
            return;
        }

        ReloadNodeChildrenKeepingExpansion(node);
    }

    public void RefreshFavoritesNode()
    {
        var node = NavigationRoots.FirstOrDefault(n => n.Kind == NavigationNodeKind.Favorites);
        if (node is null)
        {
            return;
        }

        node.LoadGeneration++;
        node.Children.Clear();
        node.ChildrenLoaded = false;
        node.FolderCount = 0;
        node.FileCount = 0;

        if (FavoritesService.HasItems())
        {
            var (folders, files) = FavoritesService.GetCounts();
            node.FolderCount = folders;
            node.FileCount = files;
            node.Children.Add(new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory)
            {
                IsPlaceholder = true
            });
        }

        if (node.IsExpanded)
        {
            LoadNodeChildren(node);
        }

        PaneContentRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshFavoriteMarks()
    {
        foreach (var root in NavigationRoots)
        {
            RefreshFavoriteMarksRecursive(root);
        }
    }

    private static void RefreshFavoriteMarksRecursive(NavigationTreeNodeViewModel node)
    {
        if (!string.IsNullOrWhiteSpace(node.TargetPath) &&
            !node.IsFavoriteEntry &&
            node.Kind is NavigationNodeKind.File or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive or NavigationNodeKind.Drive)
        {
            node.IsMarkedFavorite = FavoritesService.IsFavorite(node.TargetPath);
        }

        foreach (var child in node.Children)
        {
            RefreshFavoriteMarksRecursive(child);
        }
    }

    private void RefreshRecycleBinNode()
    {
        var node = NavigationRoots.FirstOrDefault(n => n.Kind == NavigationNodeKind.RecycleBin);
        if (node is null)
        {
            return;
        }

        node.LoadGeneration++;
        node.Children.Clear();
        node.ChildrenLoaded = false;
        node.FolderCount = 0;
        node.FileCount = 0;

        if (RecycleBinService.HasItems())
        {
            var (folders, files) = RecycleBinService.GetCounts();
            node.FolderCount = folders;
            node.FileCount = files;
            node.Children.Add(new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory)
            {
                IsPlaceholder = true
            });
        }

        if (node.IsExpanded)
        {
            LoadNodeChildren(node);
        }

        PaneContentRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public NavigationTreeNodeViewModel? FindNodeByPath(string path)
    {
        foreach (var root in NavigationRoots)
        {
            var found = FindNodeByPathRecursive(root, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static NavigationTreeNodeViewModel? FindNodeByPathRecursive(NavigationTreeNodeViewModel node, string path)
    {
        if (!string.IsNullOrWhiteSpace(node.TargetPath) &&
            string.Equals(node.TargetPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNodeByPathRecursive(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Nó FTP/SFTP/SCP/TFTP, que vive dentro da pasta "Rede" da árvore.</summary>
    private NavigationTreeNodeViewModel? FindNetworkRoot(NetworkProtocol protocol)
    {
        var kind = MapProtocolToKind(protocol);
        return NavigationRoots
            .Where(root => root.Kind == NavigationNodeKind.NetworkGroup)
            .SelectMany(group => group.Children)
            .FirstOrDefault(node => node.Kind == kind);
    }

    private static NavigationNodeKind MapProtocolToKind(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => NavigationNodeKind.Ftp,
            NetworkProtocol.Sftp => NavigationNodeKind.Sftp,
            NetworkProtocol.Scp => NavigationNodeKind.Scp,
            NetworkProtocol.Tftp => NavigationNodeKind.Tftp,
            _ => NavigationNodeKind.Ftp
        };

    private static NetworkProtocol MapKindToProtocol(NavigationNodeKind kind) =>
        kind switch
        {
            NavigationNodeKind.Ftp => NetworkProtocol.Ftp,
            NavigationNodeKind.Sftp => NetworkProtocol.Sftp,
            NavigationNodeKind.Scp => NetworkProtocol.Scp,
            NavigationNodeKind.Tftp => NetworkProtocol.Tftp,
            _ => NetworkProtocol.Ftp
        };

    private void RebuildNavigationTree()
    {
        var expandedKeys = CollectExpandedKeys();

        NavigationRoots.Clear();

        ClearItemSelection();

        foreach (var root in _navigationTreeService.CreateVisibleRoots(_displayOptions, _viewSettings.Options))
        {
            NavigationRoots.Add(root);
        }

        RestoreExpansion(NavigationRoots, expandedKeys);
        NavigationTreeRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private HashSet<string> CollectExpandedKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in NavigationRoots)
        {
            CollectExpandedKeysRecursive(root, keys);
        }

        return keys;
    }

    private static void CollectExpandedKeysRecursive(NavigationTreeNodeViewModel node, HashSet<string> keys)
    {
        if (!node.IsExpanded)
        {
            return;
        }

        keys.Add(GetExpansionKey(node));

        foreach (var child in node.Children)
        {
            CollectExpandedKeysRecursive(child, keys);
        }
    }

    private void RestoreExpansion(
        IEnumerable<NavigationTreeNodeViewModel> roots,
        HashSet<string> expandedKeys)
    {
        foreach (var root in roots)
        {
            RestoreExpansionRecursive(root, expandedKeys);
        }
    }

    private void RestoreExpansionRecursive(NavigationTreeNodeViewModel node, HashSet<string> expandedKeys)
    {
        if (!expandedKeys.Contains(GetExpansionKey(node)))
        {
            return;
        }

        node.IsExpanded = true;

        if (node.Kind is (NavigationNodeKind.Drive or NavigationNodeKind.Directory
                or NavigationNodeKind.Archive or NavigationNodeKind.RecycleBin
                or NavigationNodeKind.Favorites)
            && !node.ChildrenLoaded)
        {
            LoadNodeChildren(node);
        }

        foreach (var child in node.Children)
        {
            RestoreExpansionRecursive(child, expandedKeys);
        }
    }

    private static string GetExpansionKey(NavigationTreeNodeViewModel node) =>
        node.Kind switch
        {
            NavigationNodeKind.Drive
                or NavigationNodeKind.Directory
                or NavigationNodeKind.File
                or NavigationNodeKind.RecycleBin
                or NavigationNodeKind.Favorites
                or NavigationNodeKind.Ftp
                or NavigationNodeKind.Sftp
                or NavigationNodeKind.Scp
                or NavigationNodeKind.Tftp => node.TargetPath ?? node.Name,
            _ => node.Name
        };
}
