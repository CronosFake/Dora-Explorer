using System.Collections.Concurrent;
using System.Security.Cryptography;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Acha arquivos duplicados numa pasta local, de dois jeitos.
/// <para><b>Padrão</b> (<see cref="DuplicateMode.Standard"/>): agrupa por nome (sem diferenciar maiúsculas) e tamanho. Não lê o conteúdo,
/// então é rápido mesmo em pastas enormes, mas dois arquivos diferentes com o mesmo nome e tamanho aparecem como duplicados.</para>
/// <para><b>Profundo</b> (<see cref="DuplicateMode.Deep"/>): agrupa por tamanho e confirma pelo SHA-256 do conteúdo, qualquer que seja
/// o nome. Só lê arquivos que têm o mesmo tamanho de outro, e antes de ler inteiro compara os primeiros
/// <see cref="PrefixBytes"/> bytes (a maioria dos falsos candidatos cai aí, sem ler o arquivo todo).</para>
/// Arquivos vazios são ignorados nos dois (todos seriam "iguais"); atalhos para pastas (junções e links) não são seguidos, para
/// não contar em dobro nem entrar em ciclo; arquivos que existem só na nuvem (OneDrive) não são lidos no profundo, para não
/// baixá-los.
/// </summary>
public sealed class DuplicateFinderService : IDuplicateFinderService
{
    /// <summary>Quantos bytes do começo se comparam antes de ler o arquivo inteiro (modo profundo).</summary>
    public const int PrefixBytes = 64 * 1024;

    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline;

    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    private readonly int _maxParallelism;

    public DuplicateFinderService(int? maxParallelism = null)
    {
        // O gargalo é o disco: mais que 4 leituras ao mesmo tempo atrapalham um disco comum em vez de ajudar.
        _maxParallelism = maxParallelism ?? Math.Clamp(Environment.ProcessorCount, 1, 4);
    }

    public Task<DuplicateScanResult> FindAsync(
        DuplicateScanRequest request,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(request, progress, cancellationToken), cancellationToken);

    public DuplicateScanResult Find(
        DuplicateScanRequest request,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RootPath) || !Directory.Exists(request.RootPath))
        {
            return DuplicateScanResult.Empty;
        }

        var files = ListFiles(request.RootPath, request.IncludeSubfolders, progress, cancellationToken);

        return request.Mode == DuplicateMode.Standard
            ? FindByNameAndSize(files)
            : FindByContent(files, progress, cancellationToken);
    }

    // ---- listagem

    private static List<DuplicateFile> ListFiles(
        string root,
        bool includeSubfolders,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<DuplicateFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        var lastReport = Environment.TickCount64;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ListingOptions).ToList();
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                continue; // pasta que sumiu ou que o Windows não deixa listar: segue com as outras
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo)
                {
                    if (includeSubfolders && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry.FullName);
                    }

                    continue;
                }

                if (entry is not FileInfo file)
                {
                    continue;
                }

                long length;
                try
                {
                    length = file.Length;
                }
                catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
                {
                    continue;
                }

                if (length <= 0)
                {
                    continue;
                }

                files.Add(new DuplicateFile(
                    file.FullName,
                    file.Name,
                    file.DirectoryName ?? directory,
                    length,
                    SafeLastWrite(file)));
            }

            if (progress is not null && Environment.TickCount64 - lastReport >= 120)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new DuplicateScanProgress(DuplicateScanPhase.Listing, files.Count, 0, 0, directory));
            }
        }

        progress?.Report(new DuplicateScanProgress(DuplicateScanPhase.Listing, files.Count, 0, 0, root));
        return files;
    }

    private static DateTime? SafeLastWrite(FileInfo file)
    {
        try
        {
            return file.LastWriteTime;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    // ---- padrão: nome e tamanho

    private static DuplicateScanResult FindByNameAndSize(List<DuplicateFile> files)
    {
        var groups = files
            .GroupBy(file => (Name: file.Name.ToUpperInvariant(), file.SizeInBytes))
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateGroup(
                $"{group.First().Name} · {group.Key.SizeInBytes}",
                group.Key.SizeInBytes,
                Ordered(group)))
            .ToList();

        return new DuplicateScanResult(OrderGroups(groups), files.Count, 0, 0);
    }

    // ---- profundo: tamanho, começo do arquivo e hash do arquivo inteiro

    private DuplicateScanResult FindByContent(
        List<DuplicateFile> files,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Só vale ler arquivo que tem o tamanho de outro: tamanho diferente é arquivo diferente.
        var candidates = files
            .GroupBy(file => file.SizeInBytes)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();

        var unreadable = 0;
        var read = 0;
        var lastReport = Environment.TickCount64;

        void ReportRead(string path)
        {
            var now = Environment.TickCount64;
            if (progress is null || now - lastReport < 120)
            {
                return;
            }

            lastReport = now;
            progress.Report(new DuplicateScanProgress(DuplicateScanPhase.Reading, files.Count, candidates.Count, Volatile.Read(ref read), path));
        }

        // Etapa 1: o começo de cada arquivo (e, nos pequenos, o arquivo inteiro, que já é o hash final).
        var prefixHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RunParallel(candidates, file =>
        {
            var hash = TryHash(file, Math.Min(file.SizeInBytes, PrefixBytes));
            if (hash is null)
            {
                Interlocked.Increment(ref unreadable);
                return;
            }

            prefixHashes[file.FullPath] = hash;
            Interlocked.Increment(ref read);
            ReportRead(file.FullPath);
        }, cancellationToken);

        var afterPrefix = candidates
            .Where(file => prefixHashes.ContainsKey(file.FullPath))
            .GroupBy(file => (file.SizeInBytes, Prefix: prefixHashes[file.FullPath]))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();

        // Etapa 2: o arquivo inteiro, só dos que ainda empatam. Arquivo de até PrefixBytes já foi lido inteiro na etapa 1.
        var fullHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RunParallel(afterPrefix, file =>
        {
            if (file.SizeInBytes <= PrefixBytes)
            {
                fullHashes[file.FullPath] = prefixHashes[file.FullPath];
                return;
            }

            var hash = TryHash(file, file.SizeInBytes);
            if (hash is null)
            {
                Interlocked.Increment(ref unreadable);
                return;
            }

            fullHashes[file.FullPath] = hash;
            ReportRead(file.FullPath);
        }, cancellationToken);

        var groups = afterPrefix
            .Where(file => fullHashes.ContainsKey(file.FullPath))
            .GroupBy(file => (file.SizeInBytes, Hash: fullHashes[file.FullPath]))
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateGroup(group.Key.Hash, group.Key.SizeInBytes, Ordered(group)))
            .ToList();

        progress?.Report(new DuplicateScanProgress(DuplicateScanPhase.Reading, files.Count, candidates.Count, candidates.Count, string.Empty));
        return new DuplicateScanResult(OrderGroups(groups), files.Count, candidates.Count - unreadable, unreadable);
    }

    private void RunParallel(List<DuplicateFile> items, Action<DuplicateFile> action, CancellationToken cancellationToken) =>
        Parallel.ForEach(
            items,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _maxParallelism },
            action);

    /// <summary>SHA-256 dos primeiros <paramref name="byteCount"/> bytes; <see langword="null"/> se o arquivo não pode ser lido.</summary>
    private static string? TryHash(DuplicateFile file, long byteCount)
    {
        try
        {
            if ((File.GetAttributes(file.FullPath) & CloudOnly) != 0)
            {
                return null; // só existe na nuvem: ler baixaria o arquivo
            }

            using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            var remaining = byteCount;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break; // o arquivo encolheu enquanto se lia: o que veio até aqui é o que vale
                }

                sha.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    // ---- ordem

    private static List<DuplicateFile> Ordered(IEnumerable<DuplicateFile> files) =>
        files
            .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<DuplicateGroup> OrderGroups(List<DuplicateGroup> groups) =>
        groups
            .OrderByDescending(group => group.WastedBytes)
            .ThenBy(group => group.Files[0].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Files[0].FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
