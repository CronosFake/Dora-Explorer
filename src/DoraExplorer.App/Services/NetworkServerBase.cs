using System.Globalization;
using System.IO;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

internal interface IProtocolServer : IDisposable
{
    NetworkConnectionInfo Config { get; }

    bool IsRunning { get; }

    void Start();

    void DisposeWithoutClosingExternalPort();
}

internal abstract class ProtocolServerBase : IProtocolServer
{
    protected ProtocolServerBase(NetworkConnectionInfo config, string rootPath, NetworkServerLogStore logs)
    {
        Config = Clone(config);
        RootPath = rootPath;
        Logs = logs;
    }

    public NetworkConnectionInfo Config { get; }

    public string RootPath { get; }

    protected NetworkServerLogStore Logs { get; }

    public bool IsRunning { get; protected set; }

    protected bool ReadOnly => Config.ReadOnly;

    public abstract void Start();

    public void Dispose()
    {
        StopCore(closeListener: true);
        IsRunning = false;
    }

    public void DisposeWithoutClosingExternalPort()
    {
        StopCore(closeListener: false);
        IsRunning = false;
    }

    protected abstract void StopCore(bool closeListener);

    protected void Log(string message) => Logs.Append(Config.Protocol, message);

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
            IsUpload = source.IsUpload,
            ReadOnly = source.ReadOnly,
            ServerSecurity = source.ServerSecurity,
            PassivePorts = source.PassivePorts
        };
}

/// <summary>Garante que os caminhos pedidos por um cliente de rede ficam dentro da pasta compartilhada.</summary>
internal static class ServerPathGuard
{
    /// <summary>
    /// Verdadeiro se <paramref name="candidate"/> está dentro de <paramref name="rootPath"/> (ou é a própria raiz,
    /// quando <paramref name="allowRoot"/>). Atalhos, links simbólicos e junções que apontem para fora também
    /// são recusados — só comparar o texto do caminho deixaria a pasta compartilhada "vazar" por um link.
    /// </summary>
    public static bool IsInside(string rootPath, string candidate, bool allowRoot)
    {
        string root;
        string full;
        try
        {
            root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
            full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return allowRoot;
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !EscapesThroughLink(root, full);
    }

    private static bool EscapesThroughLink(string root, string full)
    {
        var current = root;
        foreach (var segment in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            try
            {
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : null;
                if (info is null)
                {
                    return false; // ainda não existe: não há link para resolver
                }

                if (info.LinkTarget is null)
                {
                    continue;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is null || !IsInside(root, target, allowRoot: true))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                return true; // não dá para provar que fica dentro: recusa
            }
        }

        return false;
    }
}

/// <summary>Faixa de portas passivas do FTP ("50000-50100").</summary>
internal readonly record struct PassivePortRange(int Start, int End)
{
    public static bool TryParse(string? text, out PassivePortRange range, out string? error)
    {
        range = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // vazio = portas automáticas
        }

        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            error = "Portas passivas: use o formato 50000-50100.";
            return false;
        }

        if (start < 1024 || end > 65535 || end < start)
        {
            error = "Portas passivas: use uma faixa entre 1024 e 65535, do menor para o maior.";
            return false;
        }

        range = new PassivePortRange(start, end);
        return true;
    }

    public bool IsEmpty => Start == 0 && End == 0;
}
