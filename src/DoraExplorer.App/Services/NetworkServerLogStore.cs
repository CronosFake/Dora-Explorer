using System.Text;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Buffer de logs por servidor de rede. Limpo apenas ao Iniciar; persiste ao Parar.
/// </summary>
public sealed class NetworkServerLogStore
{
    private readonly object _sync = new();
    private readonly Dictionary<NetworkProtocol, StringBuilder> _buffers = new();

    public event EventHandler<NetworkProtocol>? Changed;

    public void Clear(NetworkProtocol protocol)
    {
        lock (_sync)
        {
            _buffers[protocol] = new StringBuilder();
        }

        Changed?.Invoke(this, protocol);
    }

    public void Append(NetworkProtocol protocol, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_sync)
        {
            if (!_buffers.TryGetValue(protocol, out var buffer))
            {
                buffer = new StringBuilder();
                _buffers[protocol] = buffer;
            }

            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.Append(line);
        }

        Changed?.Invoke(this, protocol);
    }

    public string GetText(NetworkProtocol protocol)
    {
        lock (_sync)
        {
            return _buffers.TryGetValue(protocol, out var buffer)
                ? buffer.ToString()
                : string.Empty;
        }
    }
}
