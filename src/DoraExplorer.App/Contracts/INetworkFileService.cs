using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface INetworkFileService
{
    bool TryConnectAndList(NetworkConnectionInfo connection, out IReadOnlyList<FileSystemEntry> entries, out string? errorMessage);

    Task<(bool Success, IReadOnlyList<FileSystemEntry> Entries, string? ErrorMessage)> ConnectAndListAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default);

    bool TryExecuteTransfer(NetworkConnectionInfo connection, out string? errorMessage);

    Task<(bool Success, string? ErrorMessage)> ExecuteTransferAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default);

    /// <summary>Valida credenciais com login e logout limpo (sem manter sessão).</summary>
    Task<(bool Success, string? ErrorMessage)> TestConnectionAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default);
}
