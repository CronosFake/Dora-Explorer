using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.Views.Dialogs;

namespace DoraExplorer.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeManager _themeManager;
    private readonly ExplorerViewSettingsService _viewSettings;
    private readonly RememberPathSettingsService _rememberPathSettings;
    private bool _pathPersistenceEnabled;

    public MainWindowViewModel(
        ThemeManager themeManager,
        ExplorerViewSettingsService viewSettings,
        PaneDisplaySettingsService paneDisplaySettings,
        RememberPathSettingsService rememberPathSettings,
        ILocalFileSystemService fileSystemService,
        INavigationTreeService navigationTreeService,
        INetworkSessionStore networkSessions,
        INetworkServerService networkServers,
        INetworkFileService networkFileService,
        IRemoteFileService remoteFiles,
        IFileOperationService fileOperationService,
        IArchiveService archiveService,
        IDirectoryIndexService directoryIndex,
        IFileSearchService fileSearchService,
        NetworkClientHealthService networkHealth)
    {
        _themeManager = themeManager;
        _viewSettings = viewSettings;
        _rememberPathSettings = rememberPathSettings;
        _themeManager.ThemeChanged += OnThemeChanged;
        _viewSettings.Changed += OnViewSettingsChanged;
        _viewSettings.DefaultApplicationVisibilityChanged += OnDefaultApplicationVisibilityChanged;
        _viewSettings.RowDensityChanged += (_, _) => OnPropertyChanged(nameof(CompactRows));
        _rememberPathSettings.Changed += OnRememberPathSettingsChanged;

        var defaultPath = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)?.Name ?? @"C:\";
        var leftInitialPath = _rememberPathSettings.IsEnabled
            ? ResolveInitialPath(_rememberPathSettings.LeftPanePath, defaultPath)
            : string.Empty;
        var rightInitialPath = _rememberPathSettings.IsEnabled
            ? ResolveInitialPath(_rememberPathSettings.RightPanePath, defaultPath)
            : string.Empty;

        LeftPane = new FilePaneViewModel(
            fileSystemService,
            navigationTreeService,
            viewSettings,
            networkSessions,
            networkFileService,
            remoteFiles,
            fileOperationService,
            archiveService,
            directoryIndex,
            networkHealth,
            paneDisplaySettings.LeftPane,
            paneDisplaySettings.SaveLeftPane,
            leftInitialPath);
        RightPane = new FilePaneViewModel(
            fileSystemService,
            navigationTreeService,
            viewSettings,
            networkSessions,
            networkFileService,
            remoteFiles,
            fileOperationService,
            archiveService,
            directoryIndex,
            networkHealth,
            paneDisplaySettings.RightPane,
            paneDisplaySettings.SaveRightPane,
            rightInitialPath);

        CenterToolbar = new CenterToolbarViewModel(this, fileSearchService, networkServers);

        AccentColors = new ObservableCollection<AccentColorMenuItemViewModel>(
            AccentColorPalette.Options.Select(
                option => new AccentColorMenuItemViewModel(option, SelectAccentColor)));
        UpdateAccentColorSelection();
    }

    public ObservableCollection<AccentColorMenuItemViewModel> AccentColors { get; }

    public FilePaneViewModel LeftPane { get; }

    public FilePaneViewModel RightPane { get; }

    public ExplorerViewSettingsService ExplorerViewSettings => _viewSettings;

    public CenterToolbarViewModel CenterToolbar { get; }

    public FilePaneViewModel ActivePane => IsLeftPaneActive ? LeftPane : RightPane;

    public FilePaneViewModel SourcePane => IsLeftPaneActive ? LeftPane : RightPane;

    public FilePaneViewModel DestinationPane => IsLeftPaneActive ? RightPane : LeftPane;

    [ObservableProperty]
    private bool _isLeftPaneActive = true;

    partial void OnIsLeftPaneActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(SourcePane));
        OnPropertyChanged(nameof(DestinationPane));
    }

    public FilePaneViewModel OppositePane(FilePaneViewModel pane) =>
        ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;

    public FilePaneViewModel? GetPaneWithSelection()
    {
        var leftHasSelection = !string.IsNullOrWhiteSpace(LeftPane.GetSelectedItemPath());
        var rightHasSelection = !string.IsNullOrWhiteSpace(RightPane.GetSelectedItemPath());

        if (leftHasSelection && !rightHasSelection)
        {
            return LeftPane;
        }

        if (!leftHasSelection && rightHasSelection)
        {
            return RightPane;
        }

        if (leftHasSelection && rightHasSelection)
        {
            return ActivePane;
        }

        return null;
    }

    public TreeSortOptions GlobalSortOptions { get; private set; } = TreeSortOptions.Default;

    public void ApplyGlobalSortOptions(TreeSortOptions options)
    {
        GlobalSortOptions = options;
        LeftPane.ApplySortOptions(options);
        RightPane.ApplySortOptions(options);
    }

    private FilePaneViewModel? _transferSourcePane;
    private IReadOnlyList<string>? _transferPaths;
    private FileTransferOperation? _transferOperation;

    public bool HasTransferClipboard =>
        _transferPaths is { Count: > 0 } && _transferSourcePane is not null && _transferOperation is not null;

    public bool TryResolveTransfer(
        out FilePaneViewModel sourcePane,
        out FilePaneViewModel destinationPane,
        out string sourcePath,
        out string destinationDirectory,
        out string? error)
    {
        sourcePane = ActivePane;
        return TryResolveTransfer(sourcePane, ActivePane.GetTransferableItemPaths(), out destinationPane, out sourcePath, out destinationDirectory, out error);
    }

    public bool TryResolveTransfer(
        FilePaneViewModel sourcePane,
        IReadOnlyList<string> sourcePaths,
        out FilePaneViewModel destinationPane,
        out string sourcePath,
        out string destinationDirectory,
        out string? error)
    {
        destinationPane = null!;
        sourcePath = string.Empty;
        destinationDirectory = string.Empty;
        error = null;

        if (sourcePaths.Count == 0)
        {
            error = "Selecione um ficheiro ou pasta no painel de origem.";
            return false;
        }

        destinationPane = OppositePane(sourcePane);
        destinationDirectory = destinationPane.GetWorkingDirectory();

        if (!TryValidateTransfer(sourcePaths, destinationDirectory, out error))
        {
            return false;
        }

        sourcePath = sourcePaths[0];
        return true;
    }

    public bool TryValidateTransfer(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        out string? error)
    {
        error = null;

        if (sourcePaths.Count == 0)
        {
            error = "Selecione um ficheiro ou pasta no painel de origem.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            error = "Abra uma pasta no painel de destino.";
            return false;
        }

        var destinationRemote = NetworkFileService.TryParseRemotePath(destinationDirectory, out var destinationProtocol, out var destinationHost, out var destinationRemotePath);

        foreach (var path in sourcePaths)
        {
            if (string.Equals(path, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                error = "Não é possível transferir um item para si próprio.";
                return false;
            }

            if (NetworkFileService.TryParseRemotePath(path, out var sourceProtocol, out var sourceHost, out var sourceRemotePath))
            {
                // Dentro do mesmo servidor valem as mesmas regras de uma cópia local.
                if (destinationRemote && sourceProtocol == destinationProtocol &&
                    string.Equals(sourceHost, destinationHost, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(sourceRemotePath, destinationRemotePath, StringComparison.Ordinal))
                    {
                        error = "Não é possível transferir um item para si próprio.";
                        return false;
                    }

                    if (destinationRemotePath.StartsWith(sourceRemotePath.TrimEnd('/') + "/", StringComparison.Ordinal))
                    {
                        error = "Não é possível transferir uma pasta para dentro de si mesma.";
                        return false;
                    }

                    if (string.Equals(RemoteTransferEngine.ParentOf(sourceRemotePath), destinationRemotePath, StringComparison.Ordinal))
                    {
                        error = "O item já está nesta pasta.";
                        return false;
                    }
                }

                continue;
            }

            if (destinationRemote)
            {
                continue;
            }

            if (Directory.Exists(path) && PathSafetyService.IsSubPath(destinationDirectory, path))
            {
                error = "Não é possível transferir uma pasta para dentro de si mesma.";
                return false;
            }

            var sourceParent = Path.GetDirectoryName(path) ?? path;
            if (string.Equals(sourceParent, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                error = "O item já está nesta pasta.";
                return false;
            }
        }

        return true;
    }

    public async Task<TransferOutcome> ExecuteDropTransferAsync(
        FilePaneViewModel? sourcePane,
        IReadOnlyList<string> sourcePaths,
        FilePaneViewModel destinationPane,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateTransfer(sourcePaths, destinationDirectory, out var error))
        {
            return new TransferOutcome(false, error, false);
        }

        var operationPane = sourcePane ?? destinationPane;
        var outcome = await RunTransferAsync(operationPane, sourcePaths, destinationDirectory, move, cancellationToken)
            .ConfigureAwait(true);

        // Mesmo cancelada ou interrompida, parte dos itens pode já ter sido transferida.
        RefreshAfterDrop(sourcePane, destinationPane, move);
        return outcome;
    }

    private void RefreshAfterDrop(
        FilePaneViewModel? sourcePane,
        FilePaneViewModel destinationPane,
        bool move)
    {
        if (sourcePane is null)
        {
            LeftPane.RefreshPane();
            RightPane.RefreshPane();
            return;
        }

        if (move)
        {
            sourcePane.RefreshPane();
        }

        if (!ReferenceEquals(sourcePane, destinationPane) || !move)
        {
            destinationPane.RefreshPane();
        }
    }

    /// <summary>
    /// Indica que um drop foi tratado dentro da app (evita refrescar como se o Explorer tivesse movido).
    /// </summary>
    public bool InternalDropHandled { get; set; }

    public bool TryStageTransfer(FilePaneViewModel sourcePane, FileTransferOperation operation, out string? error)
    {
        error = null;
        var paths = sourcePane.GetTransferableItemPaths();
        if (paths.Count == 0)
        {
            error = operation == FileTransferOperation.Move
                ? "Selecione um ficheiro ou pasta para cortar."
                : "Selecione um ficheiro ou pasta para copiar.";
            return false;
        }

        _transferSourcePane = sourcePane;
        _transferPaths = paths;
        _transferOperation = operation;
        return true;
    }

    public async Task<TransferOutcome> TryPasteTransferAsync(CancellationToken cancellationToken = default)
    {
        if (_transferSourcePane is null || _transferPaths is null || _transferOperation is null)
        {
            return new TransferOutcome(false, "Nenhum item na área de transferência.", false);
        }

        var move = _transferOperation == FileTransferOperation.Move;
        var outcome = await ExecuteTransferAsync(_transferSourcePane, _transferPaths, move, cancellationToken)
            .ConfigureAwait(true);

        if (outcome.Success && move)
        {
            ClearTransferClipboard();
        }

        return outcome;
    }
    public Task<TransferOutcome> ExecuteTransferAsync(
        FilePaneViewModel sourcePane,
        bool move,
        CancellationToken cancellationToken = default) =>
        ExecuteTransferAsync(sourcePane, sourcePane.GetTransferableItemPaths(), move, cancellationToken);

    public async Task<TransferOutcome> ExecuteTransferAsync(
        FilePaneViewModel sourcePane,
        IReadOnlyList<string> sourcePaths,
        bool move,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTransfer(sourcePane, sourcePaths, out var destinationPane, out _, out var destinationDirectory, out var error))
        {
            return new TransferOutcome(false, error, false);
        }

        var outcome = await RunTransferAsync(sourcePane, sourcePaths, destinationDirectory, move, cancellationToken)
            .ConfigureAwait(true);

        // Mesmo cancelada ou interrompida, parte dos itens pode já ter sido transferida.
        if (move)
        {
            sourcePane.RefreshPane();
        }

        destinationPane.RefreshPane();
        return outcome;
    }

    private static readonly TimeSpan ProgressDialogDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Copia ou move os itens com janela de progresso (só aparece se demorar) e botão Cancelar. O cancelamento
    /// vale no meio de um arquivo grande, e o que ficou pela metade é apagado.
    /// </summary>
    private Task<TransferOutcome> RunTransferAsync(
        FilePaneViewModel operationPane,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken external) =>
        RunWithProgressDialogAsync(
            TransferProgressFormatter.Title(move, sourcePaths.Count, destinationDirectory),
            move ? "Movendo" : "Copiando",
            (progress, token) => TransferItemsAsync(operationPane, sourcePaths, destinationDirectory, move, progress, token),
            external);

    /// <summary>
    /// Executa o trabalho com a janela de progresso e o botão Cancelar. A janela só é criada (e mostrada) se o trabalho passar
    /// do atraso: o que termina antes disso não monta janela nenhuma.
    /// </summary>
    private static async Task<TransferOutcome> RunWithProgressDialogAsync(
        string title,
        string windowTitle,
        Func<IProgress<FileOperationProgress>, CancellationToken, Task<TransferOutcome>> work,
        CancellationToken external)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
        TransferProgressDialog? dialog = null;
        FileOperationProgress? latest = null;

        // Criado na thread da interface: os avisos de andamento chegam a ela, mesmo vindos do trabalho em segundo plano.
        var progress = new Progress<FileOperationProgress>(report =>
        {
            latest = report;
            dialog?.Update(report);
        });
        var task = work(progress, cancellation.Token);
        using var registration = OperationTracker.Shared.Begin(cancellation, task);

        try
        {
            if (await Task.WhenAny(task, Task.Delay(ProgressDialogDelay, CancellationToken.None)).ConfigureAwait(true) != task &&
                Application.Current?.MainWindow is { } owner)
            {
                dialog = new TransferProgressDialog(title, windowTitle, () => cancellation.Cancel());
                if (latest is { } current)
                {
                    dialog.Update(current);
                }

                dialog.ShowFor(owner);
            }

            return await task.ConfigureAwait(true);
        }
        finally
        {
            dialog?.CloseWhenDone();
        }
    }
    private static async Task<TransferOutcome> TransferItemsAsync(
        FilePaneViewModel operationPane,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move,
        IProgress<FileOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            // Totais do conjunto primeiro: a barra mostra a porcentagem de tudo, não a de cada item.
            var scans = await Task.Run(
                    () => sourcePaths.Select(path => TransferScanner.ScanItem(path, heartbeat: null, cancellationToken)).ToList(),
                    cancellationToken)
                .ConfigureAwait(true);

            var aggregate = new TransferProgressAggregator(progress, scans.Sum(s => s.Bytes), scans.Sum(s => s.Files));
            aggregate.Report(new FileOperationProgress(0, 0, 0, 0, string.Empty));

            for (var i = 0; i < sourcePaths.Count; i++)
            {
                var result = move
                    ? await operationPane.TryMoveItemAsync(sourcePaths[i], destinationDirectory, aggregate, cancellationToken).ConfigureAwait(true)
                    : await operationPane.TryCopyItemAsync(sourcePaths[i], destinationDirectory, aggregate, cancellationToken).ConfigureAwait(true);

                if (!result.Success)
                {
                    var cancelled = cancellationToken.IsCancellationRequested || result.ErrorMessage == FileOperationService.CancelledMessage;
                    return new TransferOutcome(false, result.ErrorMessage, cancelled);
                }

                aggregate.CompleteItem(scans[i].Bytes, scans[i].Files);
            }

            return new TransferOutcome(true, null, false);
        }
        catch (OperationCanceledException)
        {
            return new TransferOutcome(false, FileOperationService.CancelledMessage, true);
        }
    }
    public async Task<TransferOutcome> ExecuteCompressAsync(
        FilePaneViewModel sourcePane,
        ArchiveFormat format,
        CancellationToken cancellationToken = default)
    {
        var sourcePaths = sourcePane.GetTransferableItemPaths();
        if (sourcePaths.Count == 0)
        {
            return new TransferOutcome(false, "Selecione um ficheiro ou pasta no painel de origem.", false);
        }

        var destinationPane = OppositePane(sourcePane);
        var destinationDirectory = destinationPane.GetWorkingDirectory();

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return new TransferOutcome(false, "Abra uma pasta no painel de destino.", false);
        }

        foreach (var sourcePath in sourcePaths)
        {
            if (string.Equals(sourcePath, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return new TransferOutcome(false, "Não é possível compactar um item para si próprio.", false);
            }

            if (Directory.Exists(sourcePath) && PathSafetyService.IsSubPath(destinationDirectory, sourcePath))
            {
                return new TransferOutcome(false, "Não é possível compactar uma pasta para dentro de si mesma.", false);
            }
        }

        var outcome = await RunWithProgressDialogAsync(
                TransferProgressFormatter.CompressTitle(sourcePaths.Count, format, destinationDirectory),
                "Compactando",
                (progress, token) => CompressItemsAsync(sourcePane, sourcePaths, destinationDirectory, format, progress, token),
                cancellationToken)
            .ConfigureAwait(true);

        destinationPane.RefreshPane();
        return outcome;
    }

    private static async Task<TransferOutcome> CompressItemsAsync(
        FilePaneViewModel sourcePane,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        IProgress<FileOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await sourcePane
                .TryCompressItemsAsync(sourcePaths, destinationDirectory, format, progress, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {
                return new TransferOutcome(true, null, false);
            }

            var cancelled = cancellationToken.IsCancellationRequested || result.ErrorMessage == FileOperationService.CancelledMessage;
            return new TransferOutcome(false, result.ErrorMessage, cancelled);
        }
        catch (OperationCanceledException)
        {
            return new TransferOutcome(false, FileOperationService.CancelledMessage, true);
        }
    }
    public void ClearTransferClipboard()
    {
        _transferSourcePane = null;
        _transferPaths = null;
        _transferOperation = null;
    }

    public bool IsLightThemeSelected => _themeManager.ThemeMode == AppThemeMode.Light;

    public bool IsDarkThemeSelected => _themeManager.ThemeMode == AppThemeMode.Dark;

    public bool IsSystemThemeSelected => _themeManager.ThemeMode == AppThemeMode.System;

    public bool ShowHiddenFiles => _viewSettings.Options.ShowHiddenFiles;

    public bool ShowSystemFiles => _viewSettings.Options.ShowSystemFiles;

    public bool ShowExternalDrives => _viewSettings.Options.ShowExternalDrives;

    public bool ShowUnmappedPartitions => _viewSettings.Options.ShowUnmappedPartitions;

    public bool ShowDefaultApplication => _viewSettings.Options.ShowDefaultApplication;

    public bool CompactRows => _viewSettings.Options.CompactRows;

    public bool RememberPath => _rememberPathSettings.IsEnabled;

    public void OnWindowLoaded()
    {
        if (_rememberPathSettings.IsEnabled)
        {
            var defaultPath = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)?.Name ?? @"C:\";
            LeftPane.RestorePath(ResolveInitialPath(_rememberPathSettings.LeftPanePath, defaultPath));
            RightPane.RestorePath(ResolveInitialPath(_rememberPathSettings.RightPanePath, defaultPath));

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                LeftPane.CompleteInitialPathRestore();
                RightPane.CompleteInitialPathRestore();
                EnablePathPersistence();
                PersistPanePaths();
            }, DispatcherPriority.ApplicationIdle);
        }
        else
        {
            EnablePathPersistence();
        }
    }

    private void EnablePathPersistence()
    {
        if (_pathPersistenceEnabled)
        {
            return;
        }

        _pathPersistenceEnabled = true;
        LeftPane.PropertyChanged += OnPanePathChanged;
        RightPane.PropertyChanged += OnPanePathChanged;
    }

    [RelayCommand]
    private void ToggleRememberPath()
    {
        var enabling = !RememberPath;
        _rememberPathSettings.SetEnabled(enabling);

        if (enabling)
        {
            PersistPanePaths();
        }
    }

    public void PersistPanePaths()
    {
        if (!_rememberPathSettings.IsEnabled)
        {
            return;
        }

        _rememberPathSettings.SavePanePaths(
            GetPersistedPanePath(LeftPane),
            GetPersistedPanePath(RightPane));
    }

    private static string GetPersistedPanePath(FilePaneViewModel pane) =>
        string.IsNullOrWhiteSpace(pane.SelectedPath) ? pane.CurrentPath : pane.SelectedPath;

    private static string ResolveInitialPath(string? savedPath, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            return defaultPath;
        }

        if (NetworkFileService.TryParseRemotePath(savedPath, out _, out _, out _))
        {
            return savedPath;
        }

        if (Directory.Exists(savedPath) || File.Exists(savedPath))
        {
            return savedPath;
        }

        return defaultPath;
    }

    private void OnPanePathChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_pathPersistenceEnabled)
        {
            return;
        }

        if (e.PropertyName is nameof(FilePaneViewModel.CurrentPath) or nameof(FilePaneViewModel.SelectedPath))
        {
            PersistPanePaths();
        }
    }

    private void OnRememberPathSettingsChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(RememberPath));

    [RelayCommand]
    private void SetLightTheme()
    {
        _themeManager.SetThemeMode(AppThemeMode.Light);
    }

    [RelayCommand]
    private void SetDarkTheme()
    {
        _themeManager.SetThemeMode(AppThemeMode.Dark);
    }

    [RelayCommand]
    private void SetSystemTheme()
    {
        _themeManager.SetThemeMode(AppThemeMode.System);
    }

    private void SelectAccentColor(AccentColorMenuItemViewModel item)
    {
        _themeManager.SetAccentColor(item.Option.Color);
    }

    private void UpdateAccentColorSelection()
    {
        foreach (var item in AccentColors)
        {
            item.IsSelected = item.Option.Color == _themeManager.AccentColor;
        }
    }

    [RelayCommand]
    private void ToggleShowHiddenFiles()
    {
        _viewSettings.SetShowHiddenFiles(!ShowHiddenFiles);
    }

    [RelayCommand]
    private void ToggleShowSystemFiles()
    {
        _viewSettings.SetShowSystemFiles(!ShowSystemFiles);
    }

    [RelayCommand]
    private void ToggleShowExternalDrives()
    {
        _viewSettings.SetShowExternalDrives(!ShowExternalDrives);
    }

    [RelayCommand]
    private void ToggleShowUnmappedPartitions()
    {
        _viewSettings.SetShowUnmappedPartitions(!ShowUnmappedPartitions);
    }

    [RelayCommand]
    private void ToggleShowDefaultApplication()
    {
        _viewSettings.SetShowDefaultApplication(!ShowDefaultApplication);
    }

    [RelayCommand]
    private void ToggleCompactRows()
    {
        _viewSettings.SetCompactRows(!CompactRows);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        NotifyThemeSelectionChanged();
        UpdateAccentColorSelection();
    }

    private void OnViewSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShowHiddenFiles));
        OnPropertyChanged(nameof(ShowSystemFiles));
        OnPropertyChanged(nameof(ShowExternalDrives));
        OnPropertyChanged(nameof(ShowUnmappedPartitions));
        LeftPane.RefreshForViewOptionsChange();
        RightPane.RefreshForViewOptionsChange();
    }

    private void OnDefaultApplicationVisibilityChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(ShowDefaultApplication));

    private void NotifyThemeSelectionChanged()
    {
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
    }
}
