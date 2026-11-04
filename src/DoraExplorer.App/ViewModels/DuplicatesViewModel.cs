using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Converters;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.ViewModels;

/// <summary>
/// Uma linha da lista de duplicados. A lista é plana (e não agrupada) para continuar virtualizada com milhares de arquivos: cada grupo
/// começa com uma linha de cabeçalho (<see cref="IsHeader"/>), seguida das cópias.
/// </summary>
public sealed class DuplicateRowViewModel
{
    public bool IsHeader { get; init; }

    /// <summary>Nome do arquivo; no cabeçalho, o resumo do grupo ("3 cópias · 1,2 MB cada · 2,4 MB recuperáveis").</summary>
    public required string Name { get; init; }

    /// <summary>Pasta da cópia; no cabeçalho, o que une o grupo (nome e tamanho, ou o começo do SHA-256).</summary>
    public string Folder { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;

    public string ModifiedText { get; init; } = string.Empty;

    /// <summary>Caminho completo da cópia (vazio no cabeçalho).</summary>
    public string FullPath { get; init; } = string.Empty;
}

/// <summary>
/// A janela "Itens duplicados": a pasta, o modo (padrão ou profundo), se inclui subpastas, a procura em andamento (com cancelar) e a
/// lista de grupos. A procura em si é do <see cref="IDuplicateFinderService"/>; aqui ficam o estado e os textos, para testar sem janela.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly IDuplicateFinderService _finder;
    private CancellationTokenSource? _cancellation;

    public DuplicatesViewModel(IDuplicateFinderService finder, string rootPath, DuplicateMode mode, bool includeSubfolders = true)
    {
        _finder = finder;
        _rootPath = rootPath;
        _mode = mode;
        _includeSubfolders = includeSubfolders;
        _statusText = "Pronto para procurar.";
    }

    // Troca única (ReplaceAll) em vez de um Add por linha: são dezenas de milhares de linhas numa lista ligada à tela.
    public RangeObservableCollection<DuplicateRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _rootPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStandardMode))]
    [NotifyPropertyChangedFor(nameof(IsDeepMode))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private DuplicateMode _mode;

    [ObservableProperty]
    private bool _includeSubfolders;

    /// <summary>
    /// Incluir <c>Windows</c>, <c>Program Files</c>, <c>ProgramData</c> e as pastas ocultas do sistema quando a pasta é a raiz de uma
    /// unidade. Desligado por padrão: lá os duplicados são links físicos e a procura demora muito mais.
    /// </summary>
    [ObservableProperty]
    private bool _includeSystemFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanChangeOptions))]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>Quanto da etapa atual já foi feito (0 a 1). Só vale quando <see cref="IsProgressIndeterminate"/> é falso.</summary>
    [ObservableProperty]
    private double _progressFraction;

    /// <summary>Sem como dizer quanto falta (listando as pastas): a barra anda de um lado para o outro.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    // ---- ordem da lista (clique no cabeçalho Nome ou Tamanho)

    /// <summary>Por que campo os GRUPOS estão ordenados. Começa pelo tamanho, do maior para o menor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHeaderText))]
    [NotifyPropertyChangedFor(nameof(SizeHeaderText))]
    private DuplicateSortField _sortField = DuplicateSortField.Size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHeaderText))]
    [NotifyPropertyChangedFor(nameof(SizeHeaderText))]
    private bool _sortDescending = true;

    /// <summary>"Nome", com a seta de direção se a lista está ordenada por ele.</summary>
    public string NameHeaderText => "Nome" + SortIndicator(DuplicateSortField.Name);

    /// <summary>"Tamanho", com a seta de direção se a lista está ordenada por ele.</summary>
    public string SizeHeaderText => "Tamanho" + SortIndicator(DuplicateSortField.Size);

    private string SortIndicator(DuplicateSortField field) =>
        SortField != field ? string.Empty : SortDescending ? " ▼" : " ▲";

    /// <summary>
    /// Clique no cabeçalho: ordenar pela coluna já ordenada inverte a direção; outra coluna ordena por ela (nome de A a Z, tamanho do
    /// maior para o menor, como nos painéis). Reordena os grupos que já estão na tela, sem procurar de novo.
    /// </summary>
    public void SortBy(DuplicateSortField field)
    {
        if (SortField == field)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortField = field;
            SortDescending = field == DuplicateSortField.Size;
        }

        if (LastResult is { } result)
        {
            FillRows(result);
        }
    }

    /// <summary>O último resultado completo (nulo antes da primeira procura ou se cancelada).</summary>
    public DuplicateScanResult? LastResult { get; private set; }

    public bool CanStart => !IsScanning && !string.IsNullOrWhiteSpace(RootPath);

    public bool CanChangeOptions => !IsScanning;

    // Os dois botões de opção ligam em propriedades de verdadeiro/falso; marcar um desmarca o outro pelo próprio Mode.
    public bool IsStandardMode
    {
        get => Mode == DuplicateMode.Standard;
        set
        {
            if (value)
            {
                Mode = DuplicateMode.Standard;
            }
        }
    }

    public bool IsDeepMode
    {
        get => Mode == DuplicateMode.Deep;
        set
        {
            if (value)
            {
                Mode = DuplicateMode.Deep;
            }
        }
    }

    public string ModeDescription => Mode == DuplicateMode.Standard
        ? "Compara o nome e o tamanho dos arquivos. É rápido e não lê o conteúdo; dois arquivos diferentes com o mesmo nome e tamanho aparecem juntos."
        : "Compara o tamanho e o hash (SHA-256) do conteúdo, qualquer que seja o nome. É certeiro, mas lê os arquivos que têm o tamanho de outro e demora mais.";

    /// <summary>Procura de novo com as opções atuais. Uma procura em andamento é cancelada antes.</summary>
    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            StatusText = "Escolha uma pasta para procurar.";
            return;
        }

        Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        IsScanning = true;
        Rows.ReplaceAll([]);
        LastResult = null;
        SummaryText = string.Empty;
        StatusText = "Listando as pastas...";
        IsProgressIndeterminate = true;
        ProgressFraction = 0;

        // Criado na thread da tela: os avisos de andamento chegam a ela, mesmo vindos do trabalho em segundo plano.
        var progress = new Progress<DuplicateScanProgress>(report =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                StatusText = DuplicateText.Progress(report);
                var fraction = DuplicateText.Fraction(report);
                IsProgressIndeterminate = fraction is null;
                ProgressFraction = fraction ?? 0;
            }
        });

        try
        {
            var result = await _finder
                .FindAsync(new DuplicateScanRequest(RootPath, Mode, IncludeSubfolders, IncludeSystemFolders), progress, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                StatusText = "Procura cancelada.";
                return;
            }

            LastResult = result;
            FillRows(result);
            IsProgressIndeterminate = false;
            ProgressFraction = 1;
            SummaryText = DuplicateText.Summary(result);
            StatusText = DuplicateText.Finished(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Procura cancelada.";
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            AppLogService.Error($"Falha ao procurar duplicados em {RootPath}", ex);
            StatusText = "Não foi possível concluir a procura. " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                IsScanning = false;
            }

            cancellation.Dispose();
        }
    }

    /// <summary>Cancela a procura em andamento (não faz nada se não há uma).</summary>
    public void Cancel()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // a procura terminou entre o teste e o cancelamento
        }
    }

    private void FillRows(DuplicateScanResult result)
    {
        var rows = new List<DuplicateRowViewModel>();
        foreach (var group in DuplicateSorter.Sort(result.Groups, SortField, SortDescending))
        {
            rows.Add(DuplicateText.Header(group, Mode));
            foreach (var file in group.Files)
            {
                rows.Add(DuplicateText.Row(file));
            }
        }

        Rows.ReplaceAll(rows);
    }
}

/// <summary>Campo pelo qual a janela de duplicados ordena os grupos.</summary>
public enum DuplicateSortField
{
    /// <summary>Tamanho de cada cópia (o que o grupo mostra como "X cada").</summary>
    Size,

    /// <summary>Nome do primeiro arquivo do grupo.</summary>
    Name
}

/// <summary>A ordem dos grupos na janela de duplicados.</summary>
public static class DuplicateSorter
{
    /// <summary>
    /// Ordena os grupos por tamanho (de cada cópia) ou por nome (do primeiro arquivo). A direção vale só para o campo escolhido; o
    /// desempate é sempre o mesmo, para a ordem não mudar de uma procura para outra: tamanho, espaço recuperável (maior primeiro),
    /// nome e caminho.
    /// </summary>
    public static List<DuplicateGroup> Sort(IEnumerable<DuplicateGroup> groups, DuplicateSortField field, bool descending)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<DuplicateGroup> ordered = field == DuplicateSortField.Size
            ? (descending ? groups.OrderByDescending(g => g.SizeInBytes) : groups.OrderBy(g => g.SizeInBytes))
                .ThenByDescending(g => g.WastedBytes)
                .ThenBy(g => g.Files[0].Name, comparer)
            : (descending ? groups.OrderByDescending(g => g.Files[0].Name, comparer) : groups.OrderBy(g => g.Files[0].Name, comparer))
                .ThenByDescending(g => g.SizeInBytes)
                .ThenByDescending(g => g.WastedBytes);

        return ordered.ThenBy(g => g.Files[0].FullPath, comparer).ToList();
    }
}

/// <summary>Os textos da janela de duplicados, separados para serem testados.</summary>
public static class DuplicateText
{
    private static readonly FileSizeConverter SizeConverter = new();

    public static string Size(long bytes) =>
        (string)SizeConverter.Convert(bytes, typeof(string), null, CultureInfo.CurrentCulture);

    public static DuplicateRowViewModel Header(DuplicateGroup group, DuplicateMode mode) => new()
    {
        IsHeader = true,
        Name = $"{group.Files.Count} cópias · {Size(group.SizeInBytes)} cada · {Size(group.WastedBytes)} recuperáveis",
        Folder = mode == DuplicateMode.Deep
            ? "SHA-256 " + (group.Key.Length > 16 ? group.Key[..16] + "…" : group.Key)
            : "Nome e tamanho iguais"
    };

    public static DuplicateRowViewModel Row(DuplicateFile file) => new()
    {
        Name = file.Name,
        Folder = file.Folder,
        SizeText = Size(file.SizeInBytes),
        ModifiedText = file.LastModified?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty,
        FullPath = file.FullPath
    };

    /// <summary>
    /// O texto de andamento de cada etapa. A de leitura inteira (etapa 2) mostra os BYTES lidos e a porcentagem: ela pode levar minutos
    /// com o disco a 100%, e um texto parado no último número da etapa anterior parecia que o programa tinha travado.
    /// </summary>
    public static string Progress(DuplicateScanProgress report) => report.Phase switch
    {
        DuplicateScanPhase.Listing => $"Listando as pastas... {report.FilesScanned:N0} arquivo(s) vistos",
        DuplicateScanPhase.Reading =>
            $"Etapa 1 de 2: comparando o começo dos arquivos... {report.FilesRead:N0} de {report.FilesToRead:N0} arquivo(s)",
        _ =>
            $"Etapa 2 de 2: lendo por inteiro os arquivos que ainda empatam, para confirmar... {report.FilesRead:N0} de {report.FilesToRead:N0} arquivo(s)" +
            (report.BytesToRead > 0
                ? $" · {Size(report.BytesRead)} de {Size(report.BytesToRead)} ({Percent(report.BytesRead, report.BytesToRead)})"
                : string.Empty)
    };

    /// <summary>Quanto da etapa atual está feito (0 a 1), ou <see langword="null"/> quando não há como saber (listando as pastas).</summary>
    public static double? Fraction(DuplicateScanProgress report) => report.Phase switch
    {
        DuplicateScanPhase.Reading when report.FilesToRead > 0 => Math.Clamp((double)report.FilesRead / report.FilesToRead, 0, 1),
        DuplicateScanPhase.Confirming when report.BytesToRead > 0 => Math.Clamp((double)report.BytesRead / report.BytesToRead, 0, 1),
        _ => null
    };

    private static string Percent(long part, long total) =>
        (total <= 0 ? 0 : Math.Clamp(part * 100 / total, 0, 100)).ToString(CultureInfo.CurrentCulture) + "%";

    /// <summary>Resumo no alto da lista: quantos grupos, quantos arquivos e quanto espaço se recupera.</summary>
    public static string Summary(DuplicateScanResult result) => result.Groups.Count == 0
        ? "Nenhum arquivo duplicado."
        : $"{result.Groups.Count} grupo(s) · {result.TotalDuplicateFiles} arquivos · {Size(result.TotalWastedBytes)} recuperáveis";

    /// <summary>Linha de status ao terminar: o que foi visto e, se houve, o que não pôde ser lido.</summary>
    public static string Finished(DuplicateScanResult result)
    {
        var text = $"Concluído: {result.FilesScanned} arquivo(s) vistos";
        if (result.FilesRead > 0)
        {
            text += $", {result.FilesRead} lidos";
        }

        if (result.Unreadable > 0)
        {
            text += $". {result.Unreadable} não puderam ser lidos (em uso, sem permissão ou só na nuvem)";
        }

        text += ".";
        if (result.SkippedSystemFolders.Count > 0)
        {
            text += $" Pastas do sistema ignoradas: {string.Join(", ", result.SkippedSystemFolders)} (marque \"Incluir pastas do sistema\" para incluí-las).";
        }

        return text;
    }
}
