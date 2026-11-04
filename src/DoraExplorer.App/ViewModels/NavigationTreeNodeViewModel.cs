using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.Infrastructure;



namespace DoraExplorer.App.ViewModels;



public partial class NavigationTreeNodeViewModel : ObservableObject

{

    public NavigationTreeNodeViewModel(

        string name,

        NavigationNodeKind kind,

        string? targetPath = null,

        string iconGlyph = "\uE8B7")

    {

        Name = name;

        Kind = kind;

        TargetPath = targetPath;

        IconGlyph = iconGlyph;

    }



    public string Name { get; private set; }



    public void SetName(string name)
    {
        if (string.Equals(Name, name, StringComparison.Ordinal))
        {
            return;
        }

        Name = name;
        OnPropertyChanged(nameof(Name));
    }
    public NavigationNodeKind Kind { get; }



    public string? TargetPath { get; set; }



    public string IconGlyph { get; }



    public bool IsRecycleBinEntry { get; init; }

    public bool IsFavoriteEntry { get; init; }

    public string? NetworkProfileId { get; set; }

    public bool IsNetworkClientProfile => !string.IsNullOrWhiteSpace(NetworkProfileId);

    [ObservableProperty]
    private bool? _isNetworkHealthy;

    public bool ShowNetworkHealthStatus => IsNetworkClientProfile;

    private static readonly System.Windows.Media.SolidColorBrush NetworkHealthUnknownBrush = CreateFrozenBrush(0x9E, 0x9E, 0x9E);
    private static readonly System.Windows.Media.SolidColorBrush NetworkHealthOkBrush = CreateFrozenBrush(0x4C, 0xAF, 0x50);
    private static readonly System.Windows.Media.SolidColorBrush NetworkHealthDownBrush = CreateFrozenBrush(0xE5, 0x39, 0x35);

    public System.Windows.Media.Brush NetworkHealthDotBrush => IsNetworkHealthy switch
    {
        true => NetworkHealthOkBrush,
        false => NetworkHealthDownBrush,
        _ => NetworkHealthUnknownBrush
    };

    partial void OnIsNetworkHealthyChanged(bool? value) =>
        OnPropertyChanged(nameof(NetworkHealthDotBrush));

    private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string? OriginalPath { get; init; }

    public string? DisplayPath => IsRecycleBinEntry
        ? OriginalPath ?? (TargetPath is not null ? RecycleBinService.TryResolveOriginalPath(TargetPath) : null)
        : TargetPath;

    [ObservableProperty]
    private bool _isMarkedFavorite;

    public bool ShowShellIcon => IconSource is not null;



    public bool ShowGlyphIcon => IconSource is null && !string.IsNullOrEmpty(IconGlyph);



    // Ícone resolvido de forma preguiçosa: em pastas enormes não extraímos milhares de ícones
    // (cada um é uma chamada shell). Com a virtualização, o binding só lê IconSource quando o
    // container é materializado (item visível) — e aí a fábrica corre sincronamente, pelo que
    // não há "flash" de glifo nem alteração visual. Nós fixos (unidades, rede, etc.) continuam
    // a definir o ícone diretamente via inicializador (init).
    private Func<System.Windows.Media.ImageSource?>? _iconFactory;

    private System.Windows.Media.ImageSource? _iconSource;

    private bool _iconResolved;

    public System.Windows.Media.ImageSource? IconSource
    {
        get
        {
            if (!_iconResolved)
            {
                _iconResolved = true;
                _iconSource = _iconFactory?.Invoke();
            }

            return _iconSource;
        }
        init
        {
            _iconSource = value;
            _iconResolved = true;
        }
    }

    public void SetIconFactory(Func<System.Windows.Media.ImageSource?> factory)
    {
        _iconFactory = factory;
        _iconSource = null;
        _iconResolved = false;
    }



    public long? SizeInBytes { get; init; }



    public DateTime? LastModified { get; init; }



    public string? StorageSummary { get; init; }



    public double StorageUsedRatio { get; init; }

    public StorageUsageLevel StorageLevel => StorageUsage.Classify(StorageUsedRatio);



    public bool HasStorageInfo => IsDrive && !string.IsNullOrEmpty(StorageSummary);



    public System.Windows.Media.ImageSource? Thumbnail { get; init; }



    public bool HasThumbnail => false;



    public bool ShowSelectionMark => IsSelectableItem;

    public bool IsSelectableItem =>
        !IsPlaceholder &&
        Kind is NavigationNodeKind.File or NavigationNodeKind.Directory or NavigationNodeKind.Drive
            or NavigationNodeKind.Archive;

    [ObservableProperty]
    private bool _isItemSelected;

    /// <summary>A pasta que receberia os itens se o arrasto em andamento fosse solto agora (destacada na linha).</summary>
    [ObservableProperty]
    private bool _isDropTarget;



    public bool IsFile => Kind == NavigationNodeKind.File;

    /// <summary>
    /// Ficheiros normais e arquivos compactados (.zip/.tar/.tar.gz/.rar) partilham
    /// o rodapé de metadados (tamanho, data e hora de modificação).
    /// </summary>
    public bool ShowsFileMetadata =>
        Kind is NavigationNodeKind.File or NavigationNodeKind.Archive;



    public bool IsLocalFile =>

        IsFile &&

        !string.IsNullOrWhiteSpace(TargetPath) &&

        !TargetPath.StartsWith("dora-", StringComparison.OrdinalIgnoreCase) &&

        !Infrastructure.ArchivePathHelper.IsVirtualArchivePath(TargetPath);



    public bool ShowFileMetadataFooter =>
        ShowsFileMetadata && !IsRenaming && (LastModified.HasValue || SizeInBytes.HasValue);

    public bool ShowDefaultAppButton =>
        IsLocalFile &&
        !string.Equals(Path.GetExtension(TargetPath), ".exe", StringComparison.OrdinalIgnoreCase);

    private string? _defaultApplicationName;
    private bool _defaultApplicationResolved;

    public string DefaultApplicationName
    {
        get
        {
            try
            {
                if (!_defaultApplicationResolved)
                {
                    _defaultApplicationResolved = true;
                    _defaultApplicationName = IsLocalFile && !string.IsNullOrWhiteSpace(TargetPath)
                        ? FileAssociationService.GetDefaultApplicationName(TargetPath)
                        : string.Empty;
                }

                if (string.IsNullOrWhiteSpace(_defaultApplicationName))
                {
                    return FileAssociationService.FallbackApplicationName;
                }

                return _defaultApplicationName;
            }
            catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
            {
                return FileAssociationService.FallbackApplicationName;
            }
        }
    }

    public void SetDefaultApplication(string displayName, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(TargetPath))
        {
            return;
        }

        _defaultApplicationName = displayName;
        _defaultApplicationResolved = true;
        OnPropertyChanged(nameof(DefaultApplicationName));
        FileAssociationService.SetUserOverride(TargetPath, executablePath, displayName);
    }

    public void RefreshDefaultApplication()
    {
        _defaultApplicationResolved = false;
        _defaultApplicationName = null;
        OnPropertyChanged(nameof(DefaultApplicationName));
    }



    public bool IsDrive => Kind == NavigationNodeKind.Drive;



    public bool IsFavorites => Kind == NavigationNodeKind.Favorites;



    public bool IsNavigationRoot =>

        Kind is NavigationNodeKind.Drive

            or NavigationNodeKind.Favorites

            or NavigationNodeKind.RecycleBin

            or NavigationNodeKind.NetworkGroup;



    // Todas as raízes usam o mesmo formato das unidades (sem realce simplificado).

    public bool IsSimpleNavigationRoot => false;



    public bool IsFolder =>
        Kind is NavigationNodeKind.Drive or NavigationNodeKind.Directory or NavigationNodeKind.Archive;



    public bool IsExpandable =>

        Kind switch

        {

            NavigationNodeKind.Drive or NavigationNodeKind.Directory or NavigationNodeKind.Archive
                => !IsRecycleBinEntry,

            NavigationNodeKind.RecycleBin => true,

            NavigationNodeKind.Favorites => true,

            NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp
                or NavigationNodeKind.Tftp or NavigationNodeKind.NetworkGroup => true,

            _ => false

        };



    public bool IsNavigable =>

        Kind is NavigationNodeKind.Drive

            or NavigationNodeKind.Directory

            or NavigationNodeKind.Archive

            or NavigationNodeKind.File

            or NavigationNodeKind.RecycleBin

            or NavigationNodeKind.Favorites

            or NavigationNodeKind.Ftp

            or NavigationNodeKind.Sftp

            or NavigationNodeKind.Scp

            or NavigationNodeKind.Tftp;



    [ObservableProperty]

    private bool _childrenLoaded;



    // Token de geração para preenchimento incremental: cada (re)carregamento incrementa este
    // valor; um lote de streaming em curso aborta assim que deteta que a geração mudou (o nó
    // foi recolhido/recarregado), evitando duplicar filhos ou escrever num nó já obsoleto.
    public int LoadGeneration { get; set; }

    /// <summary>
    /// Não nulo enquanto uma pasta grande ainda está sendo preenchida em lotes (os filhos só entram na árvore no fim):
    /// termina o preenchimento agora, de uma vez. A navegação por caminho (Localizar, Duplicados) usa isto para o
    /// arquivo procurado existir como nó antes de ser selecionado.
    /// </summary>
    public Action? CompleteChunkedFillNow { get; set; }



    public bool IsPlaceholder { get; init; }



    [ObservableProperty]

    private int _folderCount;



    [ObservableProperty]

    private int _fileCount;



    public string SubtitleText =>

        IsFolder

            ? $"\uE8B7 {FolderCount}   \uE8A5 {FileCount}"

            : string.Empty;



    public bool ShowDirectoryCountSummary =>
        (Kind is NavigationNodeKind.Directory or NavigationNodeKind.Archive)
        && (!IsNetworkClientProfile || ChildrenLoaded);



    public string ModifiedDateText => ShowsFileMetadata && LastModified.HasValue

        ? LastModified.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.CurrentCulture)

        : string.Empty;



    public string ModifiedTimeText => (ShowsFileMetadata || Kind == NavigationNodeKind.Directory) && LastModified.HasValue

        ? LastModified.Value.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)

        : string.Empty;



    public string FileSizeText => ShowsFileMetadata && SizeInBytes.HasValue

        ? FormatSize(SizeInBytes.Value)

        : string.Empty;



    public string FileSummary

    {

        get

        {

            if (!IsLocalFile)

            {

                return string.Empty;

            }



            var parts = new List<string>();

            if (LastModified.HasValue)

            {

                parts.Add(ModifiedDateText);

                parts.Add(ModifiedTimeText);

            }



            if (SizeInBytes.HasValue)

            {

                parts.Add(FileSizeText);

            }



            return string.Join("   ", parts);

        }

    }



    public bool HasFileSummary => !string.IsNullOrEmpty(FileSummary);

    public bool ShowFileMetadata => false;



    public string DetailText => ShowsFileMetadata

        ? SizeInBytes.HasValue

            ? $"{FormatDate(LastModified)}   {FormatSize(SizeInBytes.Value)}"

            : FormatDate(LastModified)

        : string.Empty;



    public RangeObservableCollection<NavigationTreeNodeViewModel> Children { get; } = new();



    // Metadados de hierarquia orientados a dados (profundidade, posição entre irmãos e
    // continuações dos ancestrais). Ficam no ViewModel — e não no container — para que a
    // virtualização por reciclagem nunca reutilize um container com linhas-guia obsoletas:
    // ao reciclar, o novo DataContext traz consigo os valores corretos via binding.

    [ObservableProperty]

    private int _treeDepth;



    [ObservableProperty]

    private bool _isLastSiblingNode;



    [ObservableProperty]

    private string _ancestorContinuations = string.Empty;



    public System.Windows.Thickness ContentMargin =>

        new(TreeDepth * DoraExplorer.App.Controls.TreeViewHierarchy.IndentSize, 0, 0, 0);

    public double MarqueeGutterHitWidth =>
        TreeDepth * DoraExplorer.App.Controls.TreeViewHierarchy.IndentSize;

    partial void OnTreeDepthChanged(int value)
    {
        OnPropertyChanged(nameof(ContentMargin));
        OnPropertyChanged(nameof(MarqueeGutterHitWidth));
    }



    public void SetHierarchyMetadata(int depth, bool isLastSibling, string ancestorContinuations)
    {
        TreeDepth = depth;
        IsLastSiblingNode = isLastSibling;
        AncestorContinuations = ancestorContinuations;
    }



    /// <summary>
    /// Verdadeiro depois que a pasta foi fechada: reabrir mostra uma listagem nova. Enquanto continua aberta,
    /// os avisos repetidos de "expandida" (o WPF os levanta quando o programa abre a pasta por código, e a
    /// navegação por caminho passa por cada pasta) não devem recarregá-la de novo.
    /// </summary>
    public bool ReloadOnNextExpand { get; set; }

    /// <summary>O Windows não deixou listar o que há nesta pasta (falta de permissão): ela aparece vazia, mas não está.</summary>
    [ObservableProperty]
    private bool _listingDenied;

    /// <summary>Contagens de pastas e arquivos na linha; numa pasta que o Windows não deixou contar, o cadeado ocupa o lugar delas.</summary>
    public bool ShowDirectoryCounts => !ListingDenied;

    partial void OnListingDeniedChanged(bool value) => OnPropertyChanged(nameof(ShowDirectoryCounts));

    [ObservableProperty]

    private bool _isExpanded;



    [ObservableProperty]

    private bool _isRenaming;



    [ObservableProperty]

    private string _renameText = string.Empty;
    partial void OnIsRenamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFileMetadataFooter));

        if (!value)
        {
            RenameText = Name;
        }
    }

    partial void OnFolderCountChanged(int value)

    {

        OnPropertyChanged(nameof(SubtitleText));

    }



    partial void OnFileCountChanged(int value)

    {

        OnPropertyChanged(nameof(SubtitleText));

    }



    partial void OnChildrenLoadedChanged(bool value)

    {

        OnPropertyChanged(nameof(SubtitleText));

    }



    internal static string FormatSize(long bytes)

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

            ? $"{size:0} {units[unit]}"

            : $"{size:0.##} {units[unit]}";

    }



    private static string FormatDate(DateTime? date) =>

        date?.ToString("dd MMM., HH:mm", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;

}

