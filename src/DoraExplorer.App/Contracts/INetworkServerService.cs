using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface INetworkServerService : IDisposable
{
    bool IsRunning(NetworkProtocol protocol);

    bool TryStart(NetworkConnectionInfo config, out string? errorMessage);

    void StopServer(NetworkProtocol protocol);

    void StopAll();

    NetworkConnectionInfo? GetRunningConfig(NetworkProtocol protocol);

    event EventHandler? StateChanged;
}
