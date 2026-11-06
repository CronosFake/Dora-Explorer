using System.IO;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Sessão NFS para o gerenciador de arquivos. O NFS não tem "caminho" do servidor inteiro: ele exporta pastas
/// ("/srv/dados") que o cliente monta. Aqui os caminhos são os do servidor ("/srv/dados/fotos"): a raiz "/" e as
/// pastas intermediárias aparecem como pastas virtuais montadas a partir da lista de exportações, e o primeiro
/// pedaço do caminho que coincide com uma exportação vira a raiz de verdade (a mais longa que servir).
/// </summary>
internal sealed class NfsRemoteSession : IRemoteSession
{
    private const uint NewDirectoryMode = 0x1ED; // 0755
    private const uint NewFileMode = 0x1A4; // 0644

    private readonly NfsClient _client;
    private readonly Dictionary<string, byte[]> _mounted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notMountable = new(StringComparer.Ordinal);
    private IReadOnlyList<string[]>? _exports;

    private NfsRemoteSession(NfsClient client) => _client = client;

    public static NfsRemoteSession Open(NetworkConnectionInfo connection)
    {
        if (!RpcIdentity.TryParse(connection.Username, out var identity))
        {
            throw new IOException("O usuário NFS deve ser o número do usuário e do grupo no servidor, como 1000:1000 (vazio = 0:0).");
        }

        var port = connection.Port > 0 ? connection.Port : NetworkKinds.DefaultPort(NetworkProtocol.Nfs);
        return new NfsRemoteSession(NfsClient.Connect(connection.Host, port, identity, TimeSpan.FromSeconds(15)));
    }

    public RemoteEntry? Stat(string remotePath)
    {
        var segments = Split(remotePath);
        return Resolve(segments) switch
        {
            null => null,
            VirtualFolder => new RemoteEntry(segments.Length == 0 ? "/" : segments[^1], true, 0),
            RealItem item => ToEntry(segments.Length == 0 ? "/" : segments[^1], item.Attributes),
            _ => null
        };
    }

    public IReadOnlyList<RemoteEntry> List(string remoteDirectory)
    {
        var segments = Split(remoteDirectory);
        var location = Resolve(segments) ?? throw new IOException($"A pasta não existe no servidor: {remoteDirectory}");

        if (location is VirtualFolder)
        {
            return VirtualChildren(LoadExports(), segments)
                .Select(name => new RemoteEntry(name, true, 0))
                .ToList();
        }

        var real = (RealItem)location;
        if (!real.Attributes.IsDirectory)
        {
            throw new IOException($"O item não é uma pasta: {remoteDirectory}");
        }

        var result = new List<RemoteEntry>();
        foreach (var entry in _client.ReadDirectory(real.Handle))
        {
            var attributes = entry.Attributes ?? _client.Lookup(real.Handle, entry.Name)?.Attributes;
            if (attributes is null)
            {
                continue; // sumiu entre a listagem e a consulta
            }

            result.Add(ToEntry(entry.Name, attributes));
        }

        return result;
    }

    public void CreateDirectory(string remoteDirectory)
    {
        var (parent, name) = ParentFolder(remoteDirectory);
        _ = _client.MakeDirectory(parent, name, NewDirectoryMode);
    }

    public void DeleteFile(string remotePath)
    {
        var (parent, name) = ParentFolder(remotePath);
        _client.Remove(parent, name);
    }

    public void DeleteEmptyDirectory(string remoteDirectory)
    {
        var (parent, name) = ParentFolder(remoteDirectory);
        _client.RemoveDirectory(parent, name);
    }

    public void Rename(string fromPath, string toPath)
    {
        var (fromParent, fromName) = ParentFolder(fromPath);
        var (toParent, toName) = ParentFolder(toPath);
        _client.Rename(fromParent, fromName, toParent, toName);
    }

    public void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken)
    {
        var item = Resolve(Split(remotePath)) as RealItem ?? throw new FileNotFoundException($"O arquivo não existe no servidor: {remotePath}");
        if (item.Attributes.IsDirectory)
        {
            throw new IOException($"O item é uma pasta: {remotePath}");
        }

        var buffer = new byte[_client.ReadBlockBytes];
        ulong offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = _client.Read(item.Handle, offset, buffer, buffer.Length, out var eof);
            if (read > 0)
            {
                destination.Write(buffer, 0, read);
                offset += (ulong)read;
                progress((long)offset);
            }

            if (eof || read == 0)
            {
                break;
            }
        }
    }

    public void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (parent, name) = ParentFolder(remotePath);
        var handle = _client.CreateOrTruncate(parent, name, NewFileMode);

        var buffer = new byte[_client.WriteBlockBytes];
        ulong offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            var sent = 0;
            while (sent < read)
            {
                var chunk = new byte[read - sent];
                Buffer.BlockCopy(buffer, sent, chunk, 0, chunk.Length);
                var accepted = _client.Write(handle, offset + (ulong)sent, chunk, chunk.Length);
                if (accepted <= 0)
                {
                    throw new IOException("O servidor não aceitou os dados enviados.");
                }

                sent += accepted;
            }

            offset += (ulong)read;
            progress((long)offset);
        }
    }

    public void Dispose()
    {
        foreach (var export in _mounted.Keys)
        {
            _client.Unmount(export);
        }

        _client.Dispose();
    }

    // ---- Resolução de caminhos ---------------------------------------------------------------

    private abstract record Location;

    /// <summary>"/" ou uma pasta acima das exportações: não existe no servidor, só serve de caminho até elas.</summary>
    private sealed record VirtualFolder : Location;

    private sealed record RealItem(byte[] Handle, NfsAttributes Attributes) : Location;

    private Location? Resolve(string[] segments)
    {
        var exports = LoadExports();
        var export = LongestExport(exports, segments);

        if (export is null && exports.Count == 0)
        {
            export = MountLongestPrefix(segments);
        }

        if (export is null)
        {
            // Pasta que só existe no caminho até uma exportação ("/srv" para "/srv/dados"), ou a raiz.
            return segments.Length == 0 || exports.Any(e => StartsWith(e, segments)) ? new VirtualFolder() : null;
        }

        var current = MountExport(export);
        var attributes = _client.GetAttributes(current);
        foreach (var segment in segments.Skip(export.Length))
        {
            if (!attributes.IsDirectory)
            {
                return null;
            }

            var found = _client.Lookup(current, segment);
            if (found is null)
            {
                return null;
            }

            (current, attributes) = found.Value;
        }

        return new RealItem(current, attributes);
    }

    private (byte[] Handle, string Name) ParentFolder(string remotePath)
    {
        var segments = Split(remotePath);
        if (segments.Length == 0)
        {
            throw new IOException("Escolha uma pasta compartilhada do servidor.");
        }

        var parent = Resolve(segments[..^1]);
        if (parent is not RealItem { Attributes.IsDirectory: true } real)
        {
            throw new IOException("Aqui só dá para escolher uma das pastas compartilhadas do servidor: abra uma delas primeiro.");
        }

        return (real.Handle, segments[^1]);
    }

    private IReadOnlyList<string[]> LoadExports()
    {
        if (_exports is not null)
        {
            return _exports;
        }

        try
        {
            _exports = _client.ListExports()
                .Select(Split)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or XdrException or TimeoutException)
        {
            _exports = []; // servidor que não lista as exportações: o caminho informado é montado direto
        }

        return _exports;
    }

    private string[]? MountLongestPrefix(string[] segments)
    {
        for (var length = segments.Length; length >= 1; length--)
        {
            var candidate = segments[..length];
            var key = Join(candidate);
            if (_notMountable.Contains(key))
            {
                continue;
            }

            try
            {
                _ = MountExport(candidate);
                return candidate;
            }
            catch (NfsException ex) when (ex.Status is NfsStatus.NoEnt or NfsStatus.Access or NfsStatus.NotDir or NfsStatus.Perm)
            {
                _notMountable.Add(key);
            }
        }

        return null;
    }

    private byte[] MountExport(string[] export)
    {
        var path = Join(export);
        if (!_mounted.TryGetValue(path, out var handle))
        {
            handle = _client.Mount(path);
            _mounted[path] = handle;
        }

        return handle;
    }

    /// <summary>A exportação que cobre o caminho, a mais específica quando uma contém outra ("/srv" e "/srv/dados").</summary>
    internal static string[]? LongestExport(IReadOnlyList<string[]> exports, string[] segments)
    {
        string[]? best = null;
        foreach (var candidate in exports)
        {
            if (StartsWith(segments, candidate) && (best is null || candidate.Length > best.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Nomes das pastas virtuais logo abaixo de <paramref name="segments"/> no caminho até as exportações.</summary>
    internal static IReadOnlyList<string> VirtualChildren(IReadOnlyList<string[]> exports, string[] segments) =>
        exports
            .Where(export => export.Length > segments.Length && StartsWith(export, segments))
            .Select(export => export[segments.Length])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static string[] SplitPath(string? path) => Split(path);

    private static RemoteEntry ToEntry(string name, NfsAttributes attributes) =>
        new(name, attributes.IsDirectory, attributes.IsDirectory ? 0 : (long)Math.Min(attributes.Size, long.MaxValue), attributes.IsLink, attributes.ModifiedLocal);

    private static string[] Split(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string[] segments) => "/" + string.Join('/', segments);

    private static bool StartsWith(string[] segments, string[] prefix)
    {
        if (prefix.Length > segments.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(segments[i], prefix[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
