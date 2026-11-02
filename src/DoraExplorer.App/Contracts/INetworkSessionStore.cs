using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface INetworkSessionStore
{
    void Register(NetworkConnectionInfo connection);

    bool TryGetForPath(string path, out NetworkConnectionInfo connection);

    bool HasProtocol(NetworkProtocol protocol);

    void UnregisterProtocol(NetworkProtocol protocol);

    void ClearAll();
}
