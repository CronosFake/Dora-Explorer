using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Índice persistente (SQLite) das listagens de diretórios locais. Guarda cada listagem
/// SEM filtro (com as flags oculto/sistema) e aplica o filtro na leitura, de modo a ser
/// independente das opções de exibição. Serve para:
///  - pintar pastas já visitadas instantaneamente ao arrancar/reabrir (sobrevive à sessão);
///  - obter contagens (pastas/ficheiros) sem reler o disco (elimina o problema N+1);
///  - ser revalidado em segundo plano contra o disco real.
///
/// O índice é sempre um acelerador ("hint"): nunca substitui a verdade do disco. Qualquer
/// falha de SQLite degrada com segurança (o disco continua a ser lido).
/// </summary>
public sealed class DirectoryIndexService : IDirectoryIndexService
{
    private const int SchemaVersion = 1;
    private const int MaintenanceRetentionDays = 90;
    private const long VacuumThresholdBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly SqliteConnection? _connection;
    private readonly bool _enabled;
    private readonly Channel<IndexStoreRequest>? _storeChannel;
    private readonly Task? _storeWorker;

    // Ordem das leituras e das invalidações. A gravação é feita em segundo plano, em fila: um pedido lido ANTES de
    // Invalidate(pasta) pode ainda estar esperando quando a pasta é invalidada, e gravá-lo recolocaria a listagem antiga
    // (arquivo apagado voltava, pasta nova sumia). Cada pedido leva o número do momento em que a leitura começou; a gravação
    // descarta o que for mais velho que a última invalidação da pasta. Só é lido e escrito dentro de _gate.
    private long _sequence;
    private readonly Dictionary<string, long> _invalidatedAt = new(StringComparer.Ordinal);

    // Pastas cuja última leitura do disco falhou por falta de permissão. Uma pasta assim NÃO é gravada no índice: sairia
    // "vazia" e, depois de reiniciar como administrador, o cache continuaria dizendo que ela está vazia.
    private readonly ConcurrentDictionary<string, byte> _denied = new(StringComparer.Ordinal);

    private sealed record RawEntry(
        string Name,
        bool IsDirectory,
        long? Size,
        DateTime? Modified,
        bool Hidden,
        bool System);

    private sealed record IndexStoreRequest(string Key, List<RawEntry> Raw, DateTime MtimeUtc, long Sequence);

    public DirectoryIndexService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoraExplorer",
            "index.db"))
    {
    }

    /// <summary>Banco em outro caminho (os testes usam uma pasta temporária, nunca o índice do usuário).</summary>
    internal DirectoryIndexService(string dbPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();
            InitializeSchema(_connection);
            ForgetEmptyListings(_connection);
            _enabled = true;

            var channel = Channel.CreateUnbounded<IndexStoreRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _storeChannel = channel;
            _storeWorker = Task.Run(() => ProcessStoreQueueAsync(channel.Reader));
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao iniciar o índice de diretórios — a funcionar sem cache.", ex);
            _connection = null;
            _enabled = false;
        }
    }

    /// <summary>
    /// Esquece as pastas guardadas como vazias. Uma sessão antiga (sem permissão para listar) pode ter gravado uma pasta
    /// protegida como vazia; reler uma pasta vazia custa quase nada, então o índice não confia nelas.
    /// </summary>
    private static void ForgetEmptyListings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM directories WHERE NOT EXISTS (SELECT 1 FROM entries WHERE entries.parent_path = directories.path);";
        command.ExecuteNonQuery();
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS directories (
                path         TEXT PRIMARY KEY,
                dir_mtime    INTEGER NOT NULL,
                last_indexed INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entries (
                parent_path TEXT NOT NULL,
                name        TEXT NOT NULL,
                is_dir      INTEGER NOT NULL,
                size        INTEGER,
                modified    INTEGER,
                is_hidden   INTEGER NOT NULL,
                is_system   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_entries_parent ON entries(parent_path);
            """;
        create.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        version.ExecuteNonQuery();
    }

    /// <summary>Devolve a listagem em cache (filtrada) ou null se a pasta nunca foi indexada.</summary>
    public IReadOnlyList<FileSystemEntry>? TryGetEntries(string path, ExplorerViewOptions view)
    {
        if (!_enabled)
        {
            return null;
        }

        var key = Key(path);
        if (key is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                if (!DirectoryExists(key))
                {
                    return null;
                }

                var raw = ReadEntries(key);
                return ToFiltered(path, raw, view);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao ler índice: {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Lê o disco (uma única enumeração), devolve a listagem filtrada e agenda a gravação
    /// no índice em segundo plano (para não bloquear a thread chamadora).
    /// </summary>
    public IReadOnlyList<FileSystemEntry> ReadFromDiskAndQueueStore(string path, ExplorerViewOptions view)
    {
        var sequence = NextSequence(); // antes de ler o disco: uma invalidação que ocorra durante a leitura descarta este pedido
        var raw = EnumerateRaw(path, out var mtimeUtc, out var accessDenied);
        if (!NoteListing(path, accessDenied))
        {
            QueueStore(path, raw, mtimeUtc, sequence);
        }

        return ToFiltered(path, raw, view);
    }

    /// <summary>Contagens (pastas/ficheiros) a partir do índice, sem tocar no disco.</summary>
    public bool TryGetCounts(string path, ExplorerViewOptions view, out int folders, out int files)
    {
        folders = 0;
        files = 0;

        if (!_enabled)
        {
            return false;
        }

        var key = Key(path);
        if (key is null)
        {
            return false;
        }

        try
        {
            lock (_gate)
            {
                if (!DirectoryExists(key))
                {
                    return false;
                }

                foreach (var entry in ReadEntries(key))
                {
                    if (!Passes(entry, view))
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        folders++;
                    }
                    else
                    {
                        files++;
                    }
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao contar via índice: {path}", ex);
            return false;
        }
    }

    /// <summary>Lê o disco, indexa e devolve as contagens filtradas. Usar em segundo plano.</summary>
    public (int Folders, int Files) ComputeCountsFromDisk(string path, ExplorerViewOptions view)
    {
        var sequence = NextSequence();
        var raw = EnumerateRaw(path, out var mtimeUtc, out var accessDenied);
        if (!NoteListing(path, accessDenied))
        {
            QueueStore(path, raw, mtimeUtc, sequence);
        }

        var folders = 0;
        var files = 0;
        foreach (var entry in raw)
        {
            if (!Passes(entry, view))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                folders++;
            }
            else
            {
                files++;
            }
        }

        return (folders, files);
    }

    public bool WasAccessDenied(string path) => Key(path) is { } key && _denied.ContainsKey(key);

    /// <summary>Anota se a leitura foi negada (ou desfaz a anotação, se deu certo). Devolve <paramref name="accessDenied"/>.</summary>
    private bool NoteListing(string path, bool accessDenied)
    {
        if (Key(path) is { } key)
        {
            if (accessDenied)
            {
                _denied[key] = 0;
            }
            else
            {
                _denied.TryRemove(key, out _);
            }
        }

        return accessDenied;
    }

    /// <summary>True se a pasta está indexada e o mtime coincide com o do disco (não mudou).</summary>
    public bool IsIndexedAndCurrent(string path)
    {
        if (!_enabled)
        {
            return false;
        }

        var key = Key(path);
        if (key is null)
        {
            return false;
        }

        try
        {
            long storedMtime;
            lock (_gate)
            {
                using var command = _connection!.CreateCommand();
                command.CommandText = "SELECT dir_mtime FROM directories WHERE path = $p LIMIT 1;";
                command.Parameters.AddWithValue("$p", key);
                var result = command.ExecuteScalar();
                if (result is null || result is DBNull)
                {
                    return false;
                }

                storedMtime = Convert.ToInt64(result);
            }

            var currentMtime = Directory.GetLastWriteTimeUtc(path).Ticks;
            return storedMtime == currentMtime;
        }
        catch (Exception ex) when (ExpectedFailures.IsIndexStore(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Listagem bruta de uma pasta indexada e atualizada, para acelerar pesquisas sem reler o disco.
    /// </summary>
    public bool TryGetSearchListing(string path, out IReadOnlyList<SearchListingEntry> listing)
    {
        listing = [];

        if (!_enabled || !IsIndexedAndCurrent(path))
        {
            return false;
        }

        var key = Key(path);
        if (key is null)
        {
            return false;
        }

        try
        {
            lock (_gate)
            {
                if (!DirectoryExists(key))
                {
                    return false;
                }

                listing = ReadEntries(key)
                    .Select(e => new SearchListingEntry(e.Name, e.IsDirectory, e.Size))
                    .ToList();
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao ler índice para pesquisa: {path}", ex);
            return false;
        }
    }

    /// <summary>Remove a pasta do índice (usar após operações de ficheiro na pasta).</summary>
    public void Invalidate(string path)
    {
        if (!_enabled)
        {
            return;
        }

        var key = Key(path);
        if (key is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                _invalidatedAt[key] = NextSequence();
                _denied.TryRemove(key, out _);

                using var transaction = _connection!.BeginTransaction();

                using (var deleteEntries = _connection.CreateCommand())
                {
                    deleteEntries.Transaction = transaction;
                    deleteEntries.CommandText = "DELETE FROM entries WHERE parent_path = $p;";
                    deleteEntries.Parameters.AddWithValue("$p", key);
                    deleteEntries.ExecuteNonQuery();
                }

                using (var deleteDir = _connection.CreateCommand())
                {
                    deleteDir.Transaction = transaction;
                    deleteDir.CommandText = "DELETE FROM directories WHERE path = $p;";
                    deleteDir.Parameters.AddWithValue("$p", key);
                    deleteDir.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao invalidar índice: {path}", ex);
        }
    }

    private bool DirectoryExists(string key)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT 1 FROM directories WHERE path = $p LIMIT 1;";
        command.Parameters.AddWithValue("$p", key);
        return command.ExecuteScalar() is not null;
    }

    private List<RawEntry> ReadEntries(string key)
    {
        var entries = new List<RawEntry>();
        using var command = _connection!.CreateCommand();
        command.CommandText =
            "SELECT name, is_dir, size, modified, is_hidden, is_system FROM entries WHERE parent_path = $p;";
        command.Parameters.AddWithValue("$p", key);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new RawEntry(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : new DateTime(reader.GetInt64(3)),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0));
        }

        return entries;
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private void QueueStore(string path, List<RawEntry> raw, DateTime mtimeUtc, long sequence)
    {
        if (!_enabled || _storeChannel is null)
        {
            return;
        }

        var key = Key(path);
        if (key is null)
        {
            return;
        }

        _storeChannel.Writer.TryWrite(new IndexStoreRequest(key, raw, mtimeUtc, sequence));
    }

    private async Task ProcessStoreQueueAsync(ChannelReader<IndexStoreRequest> reader)
    {
        try
        {
            await foreach (var request in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Store(request.Key, request.Raw, request.MtimeUtc, request.Sequence);
                }
                catch (Exception ex)
                {
                    AppLogService.Error($"Falha ao gravar índice: {request.Key}", ex);
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Encerramento normal.
        }
    }

    public void ScheduleMaintenance()
    {
        if (!_enabled)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PerformMaintenance();
            }
            catch (Exception ex)
            {
                AppLogService.Error("Falha na manutenção do índice", ex);
            }
        });
    }

    private void PerformMaintenance()
    {
        lock (_gate)
        {
            if (_connection is null)
            {
                return;
            }

            var cutoffTicks = DateTime.UtcNow.AddDays(-MaintenanceRetentionDays).Ticks;
            using (var purge = _connection.CreateCommand())
            {
                purge.CommandText =
                    """
                    DELETE FROM entries WHERE parent_path IN (
                        SELECT path FROM directories WHERE last_indexed < $cutoff
                    );
                    DELETE FROM directories WHERE last_indexed < $cutoff;
                    """;
                purge.Parameters.AddWithValue("$cutoff", cutoffTicks);
                purge.ExecuteNonQuery();
            }

            var dbPath = _connection.DataSource;
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                var size = new FileInfo(dbPath).Length;
                if (size >= VacuumThresholdBytes)
                {
                    using var vacuum = _connection.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                    AppLogService.Info($"Índice compactado (VACUUM): {dbPath}");
                }
            }
        }
    }

    private void Store(string key, List<RawEntry> raw, DateTime mtimeUtc, long sequence)
    {
        lock (_gate)
        {
            if (_invalidatedAt.TryGetValue(key, out var invalidatedAt) && sequence < invalidatedAt)
            {
                return; // lido antes de a pasta ser invalidada: está velho e não pode voltar para o índice
            }

            using var transaction = _connection!.BeginTransaction();

            using (var deleteEntries = _connection.CreateCommand())
            {
                deleteEntries.Transaction = transaction;
                deleteEntries.CommandText = "DELETE FROM entries WHERE parent_path = $p;";
                deleteEntries.Parameters.AddWithValue("$p", key);
                deleteEntries.ExecuteNonQuery();
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO entries (parent_path, name, is_dir, size, modified, is_hidden, is_system)
                    VALUES ($p, $name, $dir, $size, $modified, $hidden, $system);
                    """;
                var pName = insert.CreateParameter(); pName.ParameterName = "$name"; insert.Parameters.Add(pName);
                var pDir = insert.CreateParameter(); pDir.ParameterName = "$dir"; insert.Parameters.Add(pDir);
                var pSize = insert.CreateParameter(); pSize.ParameterName = "$size"; insert.Parameters.Add(pSize);
                var pModified = insert.CreateParameter(); pModified.ParameterName = "$modified"; insert.Parameters.Add(pModified);
                var pHidden = insert.CreateParameter(); pHidden.ParameterName = "$hidden"; insert.Parameters.Add(pHidden);
                var pSystem = insert.CreateParameter(); pSystem.ParameterName = "$system"; insert.Parameters.Add(pSystem);
                insert.Parameters.AddWithValue("$p", key);

                foreach (var entry in raw)
                {
                    pName.Value = entry.Name;
                    pDir.Value = entry.IsDirectory ? 1 : 0;
                    pSize.Value = (object?)entry.Size ?? DBNull.Value;
                    pModified.Value = (object?)entry.Modified?.Ticks ?? DBNull.Value;
                    pHidden.Value = entry.Hidden ? 1 : 0;
                    pSystem.Value = entry.System ? 1 : 0;
                    insert.ExecuteNonQuery();
                }
            }

            using (var upsertDir = _connection.CreateCommand())
            {
                upsertDir.Transaction = transaction;
                upsertDir.CommandText =
                    """
                    INSERT INTO directories (path, dir_mtime, last_indexed)
                    VALUES ($p, $mtime, $indexed)
                    ON CONFLICT(path) DO UPDATE SET dir_mtime = $mtime, last_indexed = $indexed;
                    """;
                upsertDir.Parameters.AddWithValue("$p", key);
                upsertDir.Parameters.AddWithValue("$mtime", mtimeUtc.Ticks);
                upsertDir.Parameters.AddWithValue("$indexed", DateTime.UtcNow.Ticks);
                upsertDir.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static List<RawEntry> EnumerateRaw(string path, out DateTime mtimeUtc, out bool accessDenied)
    {
        var entries = new List<RawEntry>();
        accessDenied = false;

        try
        {
            mtimeUtc = Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            mtimeUtc = DateTime.MinValue;
        }

        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                try
                {
                    var attributes = info.Attributes;
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    entries.Add(new RawEntry(
                        info.Name,
                        isDirectory,
                        isDirectory ? null : (info as FileInfo)?.Length,
                        info.LastWriteTime,
                        (attributes & FileAttributes.Hidden) != 0,
                        (attributes & FileAttributes.System) != 0));
                }
                catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
                {
                    // Ignora entradas individuais inacessíveis.
                }
            }
        }
        catch (IOException ex) when (ExpectedFailures.IsLockedVolume(ex))
        {
            AppLogService.Info($"Unidade bloqueada pelo BitLocker (ignorado): {path}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Pastas protegidas do sistema (ex.: várias subpastas de System32) são esperadas e
            // não representam erro — registamos ao nível informativo para não poluir o log.
            accessDenied = ex is UnauthorizedAccessException;
            entries.Clear(); // o que veio antes da falha é parcial: o chamador trata a pasta como ilegível
            AppLogService.Info($"Sem acesso ao enumerar (ignorado): {path}");
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao enumerar para índice: {path}", ex);
        }

        return entries;
    }

    private static IReadOnlyList<FileSystemEntry> ToFiltered(
        string path,
        IEnumerable<RawEntry> raw,
        ExplorerViewOptions view)
    {
        var result = new List<FileSystemEntry>();
        foreach (var entry in raw)
        {
            if (!Passes(entry, view))
            {
                continue;
            }

            result.Add(new FileSystemEntry
            {
                Name = entry.Name,
                FullPath = Path.Combine(path, entry.Name),
                IsDirectory = entry.IsDirectory,
                SizeInBytes = entry.Size,
                LastModified = entry.Modified,
                IsHidden = entry.Hidden,
                IsSystem = entry.System
            });
        }

        return result;
    }

    private static bool Passes(RawEntry entry, ExplorerViewOptions view)
    {
        if (!view.ShowHiddenFiles && entry.Hidden)
        {
            return false;
        }

        if (!view.ShowSystemFiles && entry.System)
        {
            return false;
        }

        return true;
    }

    private static string? Key(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _storeChannel?.Writer.TryComplete();

            if (_storeWorker is not null)
            {
                _storeWorker.Wait(TimeSpan.FromSeconds(5));
            }

            lock (_gate)
            {
                _connection?.Close();
                _connection?.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao fechar o índice de diretórios: {ex.Message}");
        }
    }
}
