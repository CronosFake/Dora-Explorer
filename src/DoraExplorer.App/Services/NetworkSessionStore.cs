using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class NetworkSessionStore : INetworkSessionStore
{
    private readonly Dictionary<string, NetworkConnectionInfo> _hostSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public void Register(NetworkConnectionInfo connection)
    {
        lock (_sync)
        {
            _hostSessions[GetHostKey(connection.Protocol, connection.Host)] = Clone(connection);
        }
    }

    public bool TryGetForPath(string path, out NetworkConnectionInfo connection)
    {
        connection = null!;

        if (!NetworkFileService.TryParseRemotePath(path, out var protocol, out var host, out var remotePath))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_hostSessions.TryGetValue(GetHostKey(protocol, host), out var stored))
            {
                return false;
            }

            connection = Clone(stored);
            connection.RemotePath = remotePath;
            return true;
        }
    }

    public bool HasProtocol(NetworkProtocol protocol)
    {
        var prefix = $"{protocol}:".ToLowerInvariant();
        lock (_sync)
        {
            foreach (var key in _hostSessions.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void UnregisterProtocol(NetworkProtocol protocol)
    {
        var prefix = $"{protocol}:".ToLowerInvariant();
        lock (_sync)
        {
            var keys = _hostSessions.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            foreach (var key in keys)
            {
                _hostSessions.Remove(key);
            }
        }
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            _hostSessions.Clear();
        }
    }

    private static string GetHostKey(NetworkProtocol protocol, string host) =>
        $"{protocol}:{host}".ToLowerInvariant();

    private static NetworkConnectionInfo Clone(NetworkConnectionInfo source) =>
        new()
        {
            Protocol = source.Protocol,
            DisplayName = source.DisplayName,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            RemotePath = source.RemotePath,
            LocalFilePath = source.LocalFilePath,
            RemoteFilePath = source.RemoteFilePath,
            IsUpload = source.IsUpload
        };
}
