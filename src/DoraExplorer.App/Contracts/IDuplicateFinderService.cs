using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface IDuplicateFinderService
{
    /// <summary>
    /// Procura arquivos duplicados numa pasta local. Cancelar lança <see cref="OperationCanceledException"/>. Pasta que não existe
    /// devolve um resultado vazio.
    /// </summary>
    Task<DuplicateScanResult> FindAsync(
        DuplicateScanRequest request,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
