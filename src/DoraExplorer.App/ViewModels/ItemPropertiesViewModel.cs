using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.ViewModels;

/// <summary>Uma linha da janela Propriedades: o nome da informação e o valor, que pode chegar depois (totais de pasta, SHA-256).</summary>
public sealed partial class PropertyRowViewModel(string label, string value) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private string _value = value;
}

/// <summary>Um grupo de linhas com título (Geral, Datas, Atributos, Detalhes, Segurança...).</summary>
public sealed class PropertySectionViewModel(string title, IReadOnlyList<PropertyRowViewModel> rows)
{
    public string Title { get; } = title;

    /// <summary>O título em caixa alta, como os rótulos PASTA e ARQUIVOS da barra central.</summary>
    public string HeaderText => Title.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));

    public IReadOnlyList<PropertyRowViewModel> Rows { get; } = rows;
}

/// <summary>
/// O que a janela Propriedades mostra para um item, vários itens, uma unidade ou um item que não está no disco local (servidor,
/// dentro de arquivo compactado). Monta o texto em português e completa em segundo plano o que demora: o que há dentro de pastas
/// e o SHA-256 e o MD5, que a pessoa pede. Fechar a janela (<see cref="Dispose"/>) cancela o que estiver rodando.
/// </summary>
public sealed partial class ItemPropertiesViewModel : ObservableObject, IDisposable
{
    public const string CalculatingText = "Calculando…";
    public const string HashIdleText = "Ainda não calculado";
    public const string HashButtonIdle = "Calcular SHA-256 e MD5";
    public const string HashButtonRunning = "Cancelar cálculo";

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IReadOnlyList<NavigationTreeNodeViewModel> _nodes;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _hashCancellation;
    private PropertyRowViewModel? _hashRow;
    private PropertyRowViewModel? _md5Row;
    private string? _hashPath;
    private readonly object _gate = new();
    private bool _totalsFinished;

    public ItemPropertiesViewModel(IReadOnlyList<NavigationTreeNodeViewModel> nodes)
    {
        _nodes = nodes;
        if (nodes.Count == 1)
        {
            var node = nodes[0];
            Name = node.Name;
            Title = $"Propriedades de {node.Name}";
            Subtitle = node.Kind switch
            {
                NavigationNodeKind.Drive => "Unidade",
                NavigationNodeKind.Directory => "Pasta de arquivos",
                _ => "Arquivo"
            };
            IconPath = node.TargetPath;
            IconIsDirectory = node.Kind is NavigationNodeKind.Directory or NavigationNodeKind.Drive;
            CopyablePath = node.TargetPath;
        }
        else
        {
            Name = $"{nodes.Count} itens";
            Title = $"Propriedades de {nodes.Count} itens";
            Subtitle = "Itens selecionados";
        }
    }

    public string Title { get; }

    public string Name { get; }

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>Caminho de onde sai o ícone do cabeçalho (o do Windows para esse item); vazio para vários itens.</summary>
    public string? IconPath { get; }

    public bool IconIsDirectory { get; }

    /// <summary>O que o botão "Copiar caminho" copia (o caminho completo, ou os de todos os itens, um por linha).</summary>
    public string? CopyablePath { get; private set; }

    public ObservableCollection<PropertySectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _canComputeHash;

    [ObservableProperty]
    private string _hashButtonText = HashButtonIdle;

    /// <summary>Cria o modelo para os itens dados (a seleção, ou o item sob o cursor).</summary>
    public static ItemPropertiesViewModel Create(IReadOnlyList<NavigationTreeNodeViewModel> nodes) => new(nodes);

    /// <summary>Lê o que falta, em segundo plano, e preenche as seções. Termina quando os totais de pasta também terminam.</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (_nodes.Count == 1)
            {
                await LoadSingleAsync(_nodes[0]).ConfigureAwait(true);
            }
            else
            {
                await LoadSeveralAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // a janela foi fechada antes de terminar
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// A janela foi fechada: cancela o que estiver rodando. As fontes de cancelamento não são descartadas aqui de propósito: a leitura
    /// em andamento ainda consulta o <c>Token</c> ao terminar, e descartá-las faria essa consulta lançar <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _hashCancellation?.Cancel();
    }

    // ---- um item ----

    private async Task LoadSingleAsync(NavigationTreeNodeViewModel node)
    {
        var path = node.TargetPath;
        if (string.IsNullOrWhiteSpace(path) || !IsLocalPath(path))
        {
            Subtitle = DescribeOutsideKind(node);
            Sections.Add(OutsideDiskSection(node));
            return;
        }

        if (node.Kind == NavigationNodeKind.Drive)
        {
            var drive = await Task.Run(() => ItemPropertiesService.ReadDrive(path), _lifetime.Token).ConfigureAwait(true);
            Subtitle = "Unidade";
            Sections.Add(DriveSection(path, drive));
            return;
        }

        var properties = await Task.Run(() => ItemPropertiesService.Read(path), _lifetime.Token).ConfigureAwait(true);
        if (properties is null)
        {
            Sections.Add(new PropertySectionViewModel("Geral", [new("Situação", "Este item não existe mais ou não pode ser lido.")]));
            return;
        }

        var openWith = properties.IsDirectory || string.Equals(properties.Extension, ".exe", StringComparison.OrdinalIgnoreCase)
            ? null
            : await Task.Run(() => FileAssociationService.GetDefaultApplicationName(path), _lifetime.Token).ConfigureAwait(true);

        Subtitle = properties.IsDirectory || properties.Extension.Length == 0
            ? properties.TypeDescription
            : $"{properties.TypeDescription} ({properties.Extension})";

        var general = new List<PropertyRowViewModel> { new("Tipo", Subtitle) };
        if (openWith is not null)
        {
            general.Add(new("Abre com", openWith == FileAssociationService.FallbackApplicationName ? "Nenhum aplicativo associado" : openWith));
        }

        general.Add(new("Local", properties.Location ?? string.Empty));
        general.Add(new("Caminho", properties.Path));

        PropertyRowViewModel? contents = null;
        PropertyRowViewModel size;
        PropertyRowViewModel onDisk;
        if (properties.IsDirectory)
        {
            contents = new("Conteúdo", CalculatingText);
            size = new("Tamanho", CalculatingText);
            onDisk = new("Tamanho em disco", CalculatingText);
            general.Add(contents);
        }
        else
        {
            size = new("Tamanho", SizeText(properties.SizeBytes ?? 0));
            onDisk = new("Tamanho em disco", SizeText(properties.SizeOnDiskBytes ?? 0));
        }

        general.Add(size);
        general.Add(onDisk);
        Sections.Add(new PropertySectionViewModel("Geral", general));

        Sections.Add(new PropertySectionViewModel("Datas",
        [
            new("Criado", DateText(properties.Created)),
            new("Modificado", DateText(properties.Modified)),
            new("Último acesso", DateText(properties.Accessed))
        ]));

        var attributes = new List<PropertyRowViewModel>
        {
            new("Atributos", AttributesText(properties.Attributes)),
            new("Favorito", FavoritesService.IsFavorite(path) ? "Sim" : "Não")
        };
        if (!string.IsNullOrWhiteSpace(properties.LinkTarget))
        {
            attributes.Add(new("Aponta para", properties.LinkTarget));
        }

        Sections.Add(new PropertySectionViewModel("Atributos", attributes));

        var details = DetailRows(properties);
        if (details.Count > 0)
        {
            Sections.Add(new PropertySectionViewModel("Detalhes", details));
        }

        Sections.Add(new PropertySectionViewModel("Segurança",
        [
            new("Proprietário", properties.Owner ?? "Não foi possível ler (sem permissão)")
        ]));

        if (!properties.IsDirectory)
        {
            // SHA-256 primeiro (o que serve para conferir integridade com segurança); o MD5 vem junto, na mesma leitura do arquivo, para
            // comparar com sites que só publicam MD5.
            _hashRow = new PropertyRowViewModel("SHA-256", HashIdleText);
            _md5Row = new PropertyRowViewModel("MD5", HashIdleText);
            _hashPath = path;
            CanComputeHash = true;
            Sections.Add(new PropertySectionViewModel("Verificação", [_hashRow, _md5Row]));
        }

        IsLoading = false;

        if (contents is not null)
        {
            await FillTotalsAsync([path], contents, size, onDisk).ConfigureAwait(true);
        }
    }

    private static List<PropertyRowViewModel> DetailRows(ItemProperties properties)
    {
        var rows = new List<PropertyRowViewModel>();
        if (properties.Executable is { } exe)
        {
            AddIfAny(rows, "Descrição", exe.Description);
            AddIfAny(rows, "Versão do arquivo", exe.FileVersion);
            AddIfAny(rows, "Produto", exe.ProductName);
            AddIfAny(rows, "Versão do produto", exe.ProductVersion);
            AddIfAny(rows, "Empresa", exe.Company);
            AddIfAny(rows, "Direitos autorais", exe.Copyright);
            AddIfAny(rows, "Nome original", exe.OriginalFileName);
        }

        if (properties.ImageSize is { } image)
        {
            rows.Add(new("Dimensões", $"{image.Width.ToString("N0", Br)} × {image.Height.ToString("N0", Br)} pixels"));
        }

        return rows;
    }

    private static void AddIfAny(List<PropertyRowViewModel> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new(label, value));
        }
    }

    private static PropertySectionViewModel DriveSection(string root, DriveDetails? drive)
    {
        if (drive is null)
        {
            return new PropertySectionViewModel("Geral", [new("Unidade", root), new("Situação", "A unidade não está pronta ou não pode ser lida.")]);
        }

        var used = drive.TotalBytes - drive.FreeBytes;
        var percent = drive.TotalBytes > 0 ? (double)used / drive.TotalBytes : 0;
        return new PropertySectionViewModel("Geral",
        [
            new("Unidade", drive.Root),
            new("Rótulo", string.IsNullOrWhiteSpace(drive.Label) ? "(sem rótulo)" : drive.Label),
            new("Tipo", DriveTypeText(drive.DriveType)),
            new("Sistema de arquivos", drive.Format),
            new("Capacidade", SizeText(drive.TotalBytes)),
            new("Em uso", $"{SizeText(used)} ({percent.ToString("P0", Br)})"),
            new("Livre", SizeText(drive.FreeBytes))
        ]);
    }

    private static string DriveTypeText(string type) => type switch
    {
        "Fixed" => "Disco local",
        "Removable" => "Disco removível",
        "Network" => "Unidade de rede",
        "CDRom" => "Unidade de CD ou DVD",
        "Ram" => "Disco na memória",
        _ => type
    };

    private static string DescribeOutsideKind(NavigationTreeNodeViewModel node) =>
        node.Kind is NavigationNodeKind.Directory or NavigationNodeKind.Drive ? "Pasta" : "Arquivo";

    private static PropertySectionViewModel OutsideDiskSection(NavigationTreeNodeViewModel node)
    {
        var rows = new List<PropertyRowViewModel>
        {
            new("Nome", node.Name),
            new("Caminho", node.TargetPath ?? string.Empty)
        };

        if (node.SizeInBytes is { } bytes && node.Kind is not (NavigationNodeKind.Directory or NavigationNodeKind.Drive))
        {
            rows.Add(new("Tamanho", SizeText(bytes)));
        }

        if (node.LastModified is { } modified)
        {
            rows.Add(new("Modificado", DateText(modified)));
        }

        rows.Add(new("Observação", "Este item não está no disco deste computador (servidor ou dentro de um arquivo compactado): só há o que a listagem informa."));
        return new PropertySectionViewModel("Geral", rows);
    }

    // ---- vários itens ----

    private async Task LoadSeveralAsync()
    {
        var folders = _nodes.Count(node => node.Kind is NavigationNodeKind.Directory or NavigationNodeKind.Drive);
        var files = _nodes.Count - folders;
        Subtitle = $"{Plural(files, "arquivo", "arquivos")} e {Plural(folders, "pasta", "pastas")}";

        var local = _nodes.Where(node => !string.IsNullOrWhiteSpace(node.TargetPath) && IsLocalPath(node.TargetPath!)).ToList();
        var localPaths = local.Select(node => node.TargetPath!).ToList();
        var outside = _nodes.Count - local.Count;
        CopyablePath = string.Join(Environment.NewLine, _nodes.Select(node => node.TargetPath).Where(path => !string.IsNullOrWhiteSpace(path)));

        var parents = localPaths.Select(path => Path.GetDirectoryName(path.TrimEnd('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var location = parents.Count == 1 && !string.IsNullOrWhiteSpace(parents[0]) ? parents[0]! : "Vários locais";

        var contents = new PropertyRowViewModel("Contém", CalculatingText);
        var size = new PropertyRowViewModel("Tamanho", CalculatingText);
        var onDisk = new PropertyRowViewModel("Tamanho em disco", CalculatingText);
        var general = new List<PropertyRowViewModel>
        {
            new("Itens", $"{_nodes.Count.ToString("N0", Br)} ({Subtitle})"),
            new("Local", location),
            contents,
            size,
            onDisk,
            new("Favoritos", $"{localPaths.Count(FavoritesService.IsFavorite)} de {localPaths.Count}")
        };

        if (outside > 0)
        {
            general.Add(new("Fora do disco", $"{Plural(outside, "item", "itens")} de servidor ou de dentro de arquivo compactado não entram nas somas."));
        }

        Sections.Add(new PropertySectionViewModel("Geral", general));
        IsLoading = false;

        if (localPaths.Count == 0)
        {
            contents.Value = size.Value = onDisk.Value = "—";
            return;
        }

        await FillTotalsAsync(localPaths, contents, size, onDisk).ConfigureAwait(true);
    }

    // ---- o que demora ----

    private async Task FillTotalsAsync(
        IReadOnlyList<string> paths,
        PropertyRowViewModel contents,
        PropertyRowViewModel size,
        PropertyRowViewModel onDisk)
    {
        _totalsFinished = false;
        void Apply(FolderTotals totals, bool final)
        {
            var suffix = final ? string.Empty : " (contando…)";
            contents.Value = totals.Files + totals.Folders == 0 && final
                ? "Vazia"
                : $"{Plural(totals.Files, "arquivo", "arquivos")}, {Plural(totals.Folders, "pasta", "pastas")}{suffix}";
            size.Value = SizeText(totals.Bytes) + suffix;
            onDisk.Value = SizeText(totals.BytesOnDisk) + suffix;
            if (final && totals.Unreadable > 0)
            {
                contents.Value += $" — {Plural(totals.Unreadable, "pasta ou arquivo", "pastas ou arquivos")} sem permissão não {(totals.Unreadable == 1 ? "foi contado" : "foram contados")}";
            }
        }

        // Os avisos de andamento podem chegar depois do resultado final: a trava garante que o final é sempre o último a escrever.
        var progress = new Progress<FolderTotals>(totals =>
        {
            lock (_gate)
            {
                if (!_totalsFinished)
                {
                    Apply(totals, final: false);
                }
            }
        });

        try
        {
            var result = await ItemPropertiesService.ComputeTotalsAsync(paths, progress, _lifetime.Token).ConfigureAwait(true);
            lock (_gate)
            {
                _totalsFinished = true;
                Apply(result, final: true);
            }
        }
        finally
        {
            lock (_gate)
            {
                _totalsFinished = true;
            }
        }
    }

    /// <summary>
    /// Calcula o SHA-256 e o MD5 do arquivo numa leitura só (ou cancela, se já estiver calculando). Os resultados vão para as linhas
    /// "SHA-256" e "MD5"; o andamento, o cancelamento e a falha de leitura aparecem nas duas.
    /// </summary>
    public async Task ComputeHashAsync()
    {
        if (_hashRow is null || _md5Row is null || _hashPath is null)
        {
            return;
        }

        if (_hashCancellation is not null)
        {
            _hashCancellation.Cancel();
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _hashCancellation = cancellation;
        HashButtonText = HashButtonRunning;
        var waiting = $"Calculando… {0.ToString("P0", Br)}";
        _hashRow.Value = waiting;
        _md5Row.Value = waiting;
        var sha256Row = _hashRow;
        var md5Row = _md5Row;
        var finished = false;
        void Show(string sha256, string md5, bool final)
        {
            lock (_gate)
            {
                if (!finished)
                {
                    sha256Row.Value = sha256;
                    md5Row.Value = md5;
                    finished = final;
                }
            }
        }

        var progress = new Progress<double>(fraction =>
        {
            var text = $"Calculando… {fraction.ToString("P0", Br)}";
            Show(text, text, final: false);
        });

        try
        {
            var hashes = await ItemPropertiesService.ComputeHashesAsync(_hashPath, progress, cancellation.Token).ConfigureAwait(true);
            Show(hashes.Sha256, hashes.Md5, final: true);
        }
        catch (OperationCanceledException)
        {
            Show("Cálculo cancelado", "Cálculo cancelado", final: true);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            var message = "Não foi possível ler o arquivo: " + FileErrorText.WithoutMarker(FileErrorText.Describe(ex, _hashPath));
            Show(message, message, final: true);
        }
        finally
        {
            lock (_gate)
            {
                finished = true;
            }

            _hashCancellation = null;
            HashButtonText = HashButtonIdle;
        }
    }

    // ---- texto ----

    internal static bool IsLocalPath(string path) =>
        !NetworkFileService.TryParseRemotePath(path, out _, out _, out _) && !ArchivePathHelper.IsVirtualArchivePath(path);

    internal static string SizeText(long bytes) =>
        $"{NavigationTreeNodeViewModel.FormatSize(bytes)} ({bytes.ToString("N0", Br)} bytes)";

    internal static string DateText(DateTime? value) =>
        value is { } date ? date.ToString("dddd, d 'de' MMMM 'de' yyyy, HH:mm:ss", Br) : "—";

    internal static string Plural(long count, string one, string many) =>
        $"{count.ToString("N0", Br)} {(count == 1 ? one : many)}";

    internal static string AttributesText(FileAttributes attributes)
    {
        var names = new List<string>();
        void Add(FileAttributes flag, string text)
        {
            if (attributes.HasFlag(flag))
            {
                names.Add(text);
            }
        }

        Add(FileAttributes.ReadOnly, "Somente leitura");
        Add(FileAttributes.Hidden, "Oculto");
        Add(FileAttributes.System, "Sistema");
        Add(FileAttributes.Archive, "Pronto para arquivamento");
        Add(FileAttributes.Compressed, "Compactado");
        Add(FileAttributes.Encrypted, "Criptografado");
        Add(FileAttributes.ReparsePoint, "Link ou ponto de junção");
        Add(FileAttributes.SparseFile, "Esparso");
        Add(FileAttributes.Offline, "Offline (na nuvem)");
        Add(FileAttributes.Temporary, "Temporário");
        Add(FileAttributes.NotContentIndexed, "Fora do índice de pesquisa");
        return names.Count == 0 ? "Nenhum" : string.Join(", ", names);
    }
}
