using System.IO;
using System.Text.Json;

namespace DoraExplorer.App.Services;

/// <summary>Impressão digital SHA-256 (sem o prefixo "SHA256:") e algoritmo da chave de um servidor SSH.</summary>
public sealed record KnownHostEntry(string Algorithm, string Sha256);

/// <summary>Chaves de servidores SSH já aprovadas pelo usuário (equivalente ao known_hosts do OpenSSH).</summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _sync = new();

    public KnownHostsStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoraExplorer",
            "known_hosts.json");

    public static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public KnownHostEntry? TryGet(string host, int port)
    {
        lock (_sync)
        {
            return Load().TryGetValue(Key(host, port), out var entry) ? entry : null;
        }
    }

    public void Save(string host, int port, KnownHostEntry entry)
    {
        lock (_sync)
        {
            var all = Load();
            all[Key(host, port)] = entry;

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Grava num temporário e troca: um corte de energia não deixa o arquivo pela metade.
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(tempPath, _path, overwrite: true);
        }
    }

    // Arquivo ilegível => nenhum servidor é considerado conhecido (falha de forma segura: pede confirmação de novo).
    private Dictionary<string, KnownHostEntry> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    return new Dictionary<string, KnownHostEntry>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Não foi possível ler as chaves de servidores SSH conhecidas: {ex.Message}");
        }

        return new Dictionary<string, KnownHostEntry>(StringComparer.Ordinal);
    }
}
