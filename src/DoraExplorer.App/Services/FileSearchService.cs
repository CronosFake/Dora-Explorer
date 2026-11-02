using System.Collections.Concurrent;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class FileSearchService : IFileSearchService
{
    public const int DefaultMaxResults = 5000;

    private static readonly EnumerationOptions SearchEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private readonly IDirectoryIndexService _index;
    private readonly int _maxResults;
    private readonly int _maxParallelism;

    public FileSearchService(
        IDirectoryIndexService index,
        int maxResults = DefaultMaxResults,
        int? maxParallelism = null)
    {
        _index = index;
        _maxResults = maxResults;
        _maxParallelism = maxParallelism ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    public FileSearchResults Search(
        string rootPath,
        string pattern,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            return new FileSearchResults { Items = [] };
        }

        var normalizedPattern = NormalizePattern(pattern);
        if (string.IsNullOrEmpty(normalizedPattern))
        {
            return new FileSearchResults { Items = [] };
        }

        var results = new ConcurrentBag<SearchResultItem>();
        var state = new SearchState(_maxResults);

        try
        {
            WalkDirectory(rootPath, normalizedPattern, recursive, results, state, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha na pesquisa em {rootPath}", ex);
        }

        var items = results
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppLogService.Info(
            $"Pesquisa '{normalizedPattern}' em {rootPath}: {items.Count} resultado(s)" +
            (state.LimitReached ? " (limite)" : string.Empty));

        return new FileSearchResults
        {
            Items = items,
            LimitReached = state.LimitReached
        };
    }

    private void WalkDirectory(
        string directoryPath,
        string pattern,
        bool recursive,
        ConcurrentBag<SearchResultItem> results,
        SearchState state,
        CancellationToken cancellationToken)
    {
        if (state.LimitReached)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        List<string> subdirectories;
        if (_index.TryGetSearchListing(directoryPath, out var indexedListing))
        {
            subdirectories = CollectMatches(directoryPath, pattern, indexedListing, results, state, cancellationToken);
        }
        else
        {
            subdirectories = CollectMatchesFromDisk(directoryPath, pattern, results, state, cancellationToken);
        }

        if (!recursive || subdirectories.Count == 0 || state.LimitReached)
        {
            return;
        }

        if (subdirectories.Count == 1)
        {
            WalkDirectory(subdirectories[0], pattern, true, results, state, cancellationToken);
            return;
        }

        Parallel.ForEach(
            subdirectories,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxParallelism
            },
            subdirectory => WalkDirectory(subdirectory, pattern, true, results, state, cancellationToken));
    }

    private static List<string> CollectMatches(
        string directoryPath,
        string pattern,
        IReadOnlyList<SearchListingEntry> entries,
        ConcurrentBag<SearchResultItem> results,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var subdirectories = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.LimitReached)
            {
                break;
            }

            if (IsMatch(entry.Name, pattern) && state.TryAddResult())
            {
                results.Add(CreateResult(directoryPath, entry.Name, entry.IsDirectory, entry.Size));
            }

            if (entry.IsDirectory)
            {
                subdirectories.Add(Path.Combine(directoryPath, entry.Name));
            }
        }

        return subdirectories;
    }

    private static List<string> CollectMatchesFromDisk(
        string directoryPath,
        string pattern,
        ConcurrentBag<SearchResultItem> results,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var subdirectories = new List<string>();

        try
        {
            foreach (var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", SearchEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.LimitReached)
                {
                    break;
                }

                var isDirectory = entry is DirectoryInfo;
                if (IsMatch(entry.Name, pattern) && state.TryAddResult())
                {
                    long? size = null;
                    if (entry is FileInfo fileInfo)
                    {
                        size = fileInfo.Length;
                    }

                    results.Add(CreateResult(directoryPath, entry.Name, isDirectory, size));
                }

                if (isDirectory)
                {
                    subdirectories.Add(entry.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppLogService.Info($"Sem acesso ao enumerar (ignorado): {directoryPath}");
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Sem acesso a {directoryPath}: {ex.Message}");
        }

        return subdirectories;
    }

    private static SearchResultItem CreateResult(
        string parentDirectory,
        string name,
        bool isDirectory,
        long? sizeInBytes)
    {
        return new SearchResultItem
        {
            Name = name,
            FullPath = Path.Combine(parentDirectory, name),
            IsDirectory = isDirectory,
            SizeInBytes = isDirectory ? null : sizeInBytes
        };
    }

    private static string NormalizePattern(string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('*') && trimmed.EndsWith('*'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsMatch(string name, string pattern) =>
        name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private sealed class SearchState(int maxResults)
    {
        private int _count;

        public bool LimitReached { get; private set; }

        public bool TryAddResult()
        {
            if (LimitReached)
            {
                return false;
            }

            var count = Interlocked.Increment(ref _count);
            if (count > maxResults)
            {
                LimitReached = true;
                return false;
            }

            return true;
        }
    }
}
