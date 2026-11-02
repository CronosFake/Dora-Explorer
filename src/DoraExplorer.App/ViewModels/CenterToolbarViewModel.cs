using System.Collections.ObjectModel;

using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;

using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.Views.Dialogs;

namespace DoraExplorer.App.ViewModels;

public partial class CenterToolbarViewModel : ObservableObject
{
    private readonly MainWindowViewModel _mainWindow;
    private readonly IFileSearchService _searchService;
    private readonly INetworkServerService _networkServers;

    private FilePaneViewModel? _lastSearchPane;

    private CancellationTokenSource? _searchCancellation;

    private string? _lastSearchRoot;

    private SearchResultItem? _pendingLocateResult;

    private bool _locateNavigationScheduled;



    public CenterToolbarViewModel(
        MainWindowViewModel mainWindow,
        IFileSearchService searchService,
        INetworkServerService networkServers)
    {
        _mainWindow = mainWindow;
        _searchService = searchService;
        _networkServers = networkServers;
        _networkServers.StateChanged += (_, _) => RefreshNetworkServiceStatus();
        RefreshNetworkServiceStatus();
    }

    [ObservableProperty]
    private bool _isFtpServiceActive;

    [ObservableProperty]
    private bool _isSftpServiceActive;

    [ObservableProperty]
    private bool _isScpServiceActive;

    [ObservableProperty]
    private bool _isTftpServiceActive;

    public void RefreshNetworkServiceStatus()
    {
        IsFtpServiceActive = _networkServers.IsRunning(NetworkProtocol.Ftp);
        IsSftpServiceActive = _networkServers.IsRunning(NetworkProtocol.Sftp);
        IsScpServiceActive = _networkServers.IsRunning(NetworkProtocol.Scp);
        IsTftpServiceActive = _networkServers.IsRunning(NetworkProtocol.Tftp);
    }


    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];



    [ObservableProperty]

    private string _searchPattern = string.Empty;



    [ObservableProperty]

    private string _searchRootPath = string.Empty;



    [ObservableProperty]

    private string _searchStatusText = string.Empty;



    [ObservableProperty]

    [NotifyPropertyChangedFor(nameof(IsSearchEnabled))]

    [NotifyCanExecuteChangedFor(nameof(RunSearchCommand))]

    private bool _isSearching;



    public bool IsSearchEnabled => !IsSearching;



    [RelayCommand]

    private void Refresh()

    {

        _mainWindow.ActivePane.RefreshPane();

    }



    [RelayCommand]

    private void NewFolder()

    {

        var pane = _mainWindow.ActivePane;

        if (!pane.TryCreateFolderWithInlineRename(out var error))

        {

            ShowError(error ?? "Não foi possível criar a pasta.");

        }

    }



    public void RefreshSearchContext()

    {

        _lastSearchPane = _mainWindow.ActivePane;

        var root = _lastSearchPane.GetWorkingDirectory();

        SearchRootPath = string.IsNullOrWhiteSpace(root)

            ? "Selecione uma pasta local num painel"

            : root;

    }



    public void RunSearchWithPattern(string pattern)

    {

        SearchPattern = pattern;

        RunSearchCommand.Execute(null);

    }



    [RelayCommand(CanExecute = nameof(CanRunSearch))]

    private async Task RunSearchAsync()

    {

        var pane = _lastSearchPane ?? _mainWindow.ActivePane;

        _lastSearchPane = pane;



        var root = pane.GetWorkingDirectory();

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))

        {

            ShowError("Abra uma pasta local para pesquisar.");

            return;

        }



        if (string.IsNullOrWhiteSpace(SearchPattern))

        {

            ShowError("Digite o texto a localizar.");

            return;

        }



        _searchCancellation?.Cancel();

        _searchCancellation?.Dispose();

        _searchCancellation = new CancellationTokenSource();

        var cancellationToken = _searchCancellation.Token;

        var pattern = SearchPattern;



        SearchRootPath = root;

        _lastSearchRoot = root;

        IsSearching = true;

        SearchStatusText = "Pesquisando...";

        SearchResults.Clear();



        try

        {

            var searchResults = await Task.Run(
                () => _searchService.Search(root, pattern, recursive: true, cancellationToken),
                cancellationToken);

            SearchResults.Clear();
            foreach (var result in searchResults.Items)
            {
                SearchResults.Add(result);
            }

            SearchStatusText = searchResults.Items.Count == 0
                ? "Nenhum resultado"
                : searchResults.LimitReached
                    ? $"{searchResults.Items.Count} resultado(s) (limite atingido)"
                    : $"{searchResults.Items.Count} resultado(s)";

        }

        catch (OperationCanceledException)

        {

            SearchStatusText = "Pesquisa cancelada";

        }

        catch (Exception ex)

        {

            AppLogService.Error($"Falha na pesquisa em {root}", ex);

            SearchStatusText = "Falha na pesquisa";

        }

        finally

        {

            IsSearching = false;

        }

    }



    private bool CanRunSearch() => !IsSearching;



    public void OpenSearchResult(SearchResultItem item)
    {
        _pendingLocateResult = item;
        if (_locateNavigationScheduled)
        {
            return;
        }

        _locateNavigationScheduled = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _locateNavigationScheduled = false;
            var pending = _pendingLocateResult;
            _pendingLocateResult = null;
            if (pending is null)
            {
                return;
            }

            OpenSearchResultCore(pending);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OpenSearchResultCore(SearchResultItem item)
    {
        var pane = _lastSearchPane ?? _mainWindow.ActivePane;

        if (ReferenceEquals(pane, _mainWindow.LeftPane))
        {
            _mainWindow.IsLeftPaneActive = true;
        }
        else
        {
            _mainWindow.IsLeftPaneActive = false;
        }

        pane.NavigateToSearchResult(item, _lastSearchRoot);

        if (_pendingLocateResult is not null)
        {
            OpenSearchResult(_pendingLocateResult);
        }
    }



    public void ApplySortOptions(TreeSortOptions options) => _mainWindow.ApplyGlobalSortOptions(options);



    public TreeSortOptions CurrentSortOptions => _mainWindow.GlobalSortOptions;



    [RelayCommand]
    private Task Copy() => TransferAsync(move: false);

    [RelayCommand]
    private Task Move() => TransferAsync(move: true);

    private async Task TransferAsync(bool move)
    {
        var outcome = await _mainWindow.ExecuteTransferAsync(_mainWindow.ActivePane, move).ConfigureAwait(true);
        if (!outcome.Success && !outcome.Cancelled)
        {
            ShowError(outcome.ErrorMessage ?? (move ? "Não foi possível mover." : "Não foi possível copiar."));
        }
    }

    [RelayCommand]
    private async Task Compress(ArchiveFormat format)
    {
        var pane = _mainWindow.ActivePane;
        var paths = pane.GetTransferableItemPaths();
        if (paths.Count == 0)
        {
            ShowError("Selecione um ficheiro ou pasta para compactar.");
            return;
        }

        var outcome = await _mainWindow.ExecuteCompressAsync(pane, format).ConfigureAwait(true);
        if (!outcome.Success && !outcome.Cancelled)
        {
            ShowError(outcome.ErrorMessage ?? "Não foi possível compactar.");
        }
    }



    [RelayCommand]

    private void Rename()

    {

        var pane = _mainWindow.ActivePane;

        if (string.IsNullOrWhiteSpace(pane.GetSelectedItemPath()))

        {

            ShowError("Selecione um ficheiro ou pasta para renomear.");

            return;

        }



        pane.BeginRenameSelected();

    }



    [RelayCommand]

    private void Delete()

    {

        var pane = _mainWindow.ActivePane;

        var paths = pane.GetDeletableItemPaths();

        if (paths.Count == 0)

        {

            ShowError("Selecione um ficheiro ou pasta para excluir.");

            return;

        }



        var mode = DeleteChoiceDialog.TryChoose(Application.Current.MainWindow, paths);

        if (mode is null)

        {

            return;

        }



        if (!pane.TryDeleteItems(paths, mode.Value, out var error))

        {

            ShowError(error ?? "Não foi possível excluir.");

        }

    }



    [RelayCommand]
    private void Favorite()
    {
        var pane = _mainWindow.ActivePane;
        var paths = pane.GetFavoriteableItemPaths();
        if (paths.Count == 0)
        {
            ShowError("Selecione um ficheiro ou pasta para marcar como favorito.");
            return;
        }

        if (!pane.TryToggleFavorites(paths, out var added, out var error))
        {
            ShowError(error ?? "Não foi possível atualizar os favoritos.");
            return;
        }

        // Manter o painel oposto sincronizado (favoritos são globais).
        var opposite = _mainWindow.OppositePane(pane);
        if (added)
        {
            opposite.EnsureFavoritesVisible();
        }

        opposite.RefreshFavoritesNode();
        opposite.RefreshFavoriteMarks();

        AppLogService.Info(added
            ? $"Favorito(s) adicionado(s): {paths.Count}"
            : $"Favorito(s) removido(s): {paths.Count}");
    }

    [RelayCommand]
    private void Ftp() => ConnectNetwork(NetworkProtocol.Ftp);

    [RelayCommand]
    private void Scp() => ConnectNetwork(NetworkProtocol.Scp);

    [RelayCommand]
    private void Sftp() => ConnectNetwork(NetworkProtocol.Sftp);

    [RelayCommand]
    private void Tftp() => ConnectNetwork(NetworkProtocol.Tftp);

    private void ConnectNetwork(NetworkProtocol protocol)
    {
        var owner = Application.Current.MainWindow;
        if (owner is null)
        {
            return;
        }

        var isRunning = _networkServers.IsRunning(protocol);

        NetworkConnectionDialog.ShowOrActivate(owner, protocol, isRunning, (action, connection) =>
            HandleNetworkServerDialogResult(protocol, action, connection));
    }

    private void HandleNetworkServerDialogResult(
        NetworkProtocol protocol,
        NetworkDialogAction action,
        NetworkConnectionInfo? connection)
    {
        if (action == NetworkDialogAction.Stop)
        {
            _networkServers.StopServer(protocol);
            RefreshNetworkServiceStatus();
            NetworkConnectionDialog.NotifyRunningState(protocol, isRunning: false);
            return;
        }

        if (action != NetworkDialogAction.Start || connection is null)
        {
            return;
        }

        if (!_networkServers.TryStart(connection, out var error))
        {
            ShowError(error ?? "Não foi possível iniciar o servidor.");
            NetworkConnectionDialog.NotifyRunningState(protocol, isRunning: false);
            return;
        }

        RefreshNetworkServiceStatus();
        NetworkConnectionDialog.NotifyRunningState(protocol, isRunning: true);
    }

    private static void ShowError(string message) =>
        MessageDialog.Show(Application.Current.MainWindow, message);

}


