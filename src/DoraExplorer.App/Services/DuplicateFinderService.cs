using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using Microsoft.Win32.SafeHandles;

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
/// baixá-los. Começando na raiz de uma unidade, as pastas do sistema ficam de fora (ver <see cref="DuplicateScanRequest.IncludeSystemFolders"/>).
/// <para>Velocidade: o custo está em abrir cada arquivo (o antivírus o examina na primeira abertura), não em calcular a hash. Por isso
/// as leituras rodam em paralelo (<see cref="DuplicateWorkers"/>, conforme o tipo de disco) e cada arquivo é lido com o mínimo de
/// trabalho: um só <c>OpenHandle</c>, buffer emprestado de um <see cref="ArrayPool{T}"/> e, nos pequenos, a hash de uma só vez.</para>
/// </summary>
public sealed class DuplicateFinderService : IDuplicateFinderService
{
    /// <summary>Quantos bytes do começo se comparam antes de ler o arquivo inteiro (modo profundo).</summary>
    public const int PrefixBytes = 64 * 1024;

    /// <summary>Tamanho do bloco ao ler um arquivo inteiro: blocos grandes cortam o número de chamadas ao sistema.</summary>
    private const int FullReadBufferBytes = 1024 * 1024;

    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline;

    /// <summary>
    /// Pastas do topo de uma unidade que a procura ignora por padrão. Além destas, qualquer pasta do topo com o atributo "sistema"
    /// (como <c>$Recycle.Bin</c> e <c>System Volume Information</c>) também fica de fora.
    /// </summary>
    public static IReadOnlyList<string> SystemFolderNames { get; } =
    [
        "Windows",
        "Windows.old",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "$WinREAgent",
        "Config.Msi",
        "PerfLogs"
    ];

    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    /// <summary>Intervalo mínimo entre dois avisos de andamento: a tela não precisa de mais, e o <c>Progress</c> posta cada um na thread dela.</summary>
    public const int DefaultReportIntervalMilliseconds = 120;

    private readonly int? _fixedParallelism;
    private readonly int _reportInterval;

    /// <param name="maxParallelism">Leituras ao mesmo tempo; sem valor, escolhe pelo tipo de disco da pasta (<see cref="DuplicateWorkers"/>).</param>
    public DuplicateFinderService(int? maxParallelism = null)
        : this(maxParallelism, DefaultReportIntervalMilliseconds)
    {
    }

    /// <summary>Com intervalo próprio entre avisos (os testes usam 0 para ver um aviso por bloco lido).</summary>
    internal DuplicateFinderService(int? maxParallelism, int reportIntervalMilliseconds)
    {
        _fixedParallelism = maxParallelism;
        _reportInterval = reportIntervalMilliseconds;
    }

    /// <summary>A pasta é a raiz de uma unidade (C:\, D:\) ou de um compartilhamento de rede (\\servidor\pasta)?</summary>
    public static bool IsDriveRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Uma pasta do topo de uma unidade que não entra na procura por padrão (pelo nome, ou por ser do sistema).</summary>
    public static bool IsSystemFolder(string name, FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.System) ||
        SystemFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

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
        return Find(request, SkipsSystemFolders(request) ? IsSystemFolder : null, progress, cancellationToken);
    }

    /// <summary>
    /// As pastas do sistema ficam de fora quando a procura começa na raiz de uma unidade e o pedido não as inclui. Numa pasta comum
    /// (mesmo uma que se chame Windows) nada é ignorado: quem escolheu a pasta sabe o que quer.
    /// </summary>
    internal static bool SkipsSystemFolders(DuplicateScanRequest request) =>
        !request.IncludeSystemFolders && !string.IsNullOrWhiteSpace(request.RootPath) && IsDriveRoot(request.RootPath);

    /// <summary>
    /// A procura com a regra "pastas do topo que ficam de fora" dada. <see cref="Find(DuplicateScanRequest, IProgress{DuplicateScanProgress}?, CancellationToken)"/>
    /// só a liga na raiz de uma unidade; separada para poder ser testada numa pasta comum (os testes nunca varrem uma unidade de verdade).
    /// </summary>
    internal DuplicateScanResult Find(
        DuplicateScanRequest request,
        Func<string, FileAttributes, bool>? skipTopLevelFolder,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RootPath) || !Directory.Exists(request.RootPath))
        {
            return DuplicateScanResult.Empty;
        }

        var skipped = new List<string>();
        var files = ListFiles(request.RootPath, request.IncludeSubfolders, skipTopLevelFolder, skipped, _reportInterval, progress, cancellationToken);

        var result = request.Mode == DuplicateMode.Standard
            ? FindByNameAndSize(files)
            : FindByContent(files, _fixedParallelism ?? DuplicateWorkers.ForPath(request.RootPath), _reportInterval, progress, cancellationToken);

        return skipped.Count == 0 ? result : result with { SkippedSystemFolders = skipped };
    }

    // ---- listagem

    private static List<DuplicateFile> ListFiles(
        string root,
        bool includeSubfolders,
        Func<string, FileAttributes, bool>? skipTopLevelFolder,
        List<string> skipped,
        int reportInterval,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<DuplicateFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        var isTopLevel = true;
        var lastReport = Environment.TickCount64;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var topLevel = isTopLevel;
            isTopLevel = false;

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
                        if (topLevel && skipTopLevelFolder is not null && skipTopLevelFolder(entry.Name, entry.Attributes))
                        {
                            skipped.Add(entry.Name);
                            continue;
                        }

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
                    SafeLastWrite(file),
                    file.Attributes));
            }

            if (progress is not null && Environment.TickCount64 - lastReport >= reportInterval)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new DuplicateScanProgress(DuplicateScanPhase.Listing, files.Count, 0, 0, directory));
            }
        }

        skipped.Sort(StringComparer.OrdinalIgnoreCase);
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

    private static DuplicateScanResult FindByContent(
        List<DuplicateFile> files,
        int workers,
        int reportInterval,
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
            if (progress is null || now - lastReport < reportInterval)
            {
                return;
            }

            lastReport = now;
            progress.Report(new DuplicateScanProgress(DuplicateScanPhase.Reading, files.Count, candidates.Count, Volatile.Read(ref read), path));
        }

        // Etapa 1: o começo de cada arquivo (e, nos pequenos, o arquivo inteiro, que já é o hash final).
        var prefixHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RunParallel(candidates, workers, file =>
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
        // É a etapa que pode levar minutos (centenas de GB com o disco a 100%): o andamento é por BYTES e sai a cada bloco lido,
        // senão a tela ficava parada no último número da etapa 1 e parecia travada.
        var toConfirm = afterPrefix.Where(file => file.SizeInBytes > PrefixBytes).ToList();
        var confirmBytesTotal = toConfirm.Sum(file => file.SizeInBytes);
        var confirmedFiles = 0;
        long confirmedBytes = 0;
        long lastConfirmReport = 0;

        void ReportConfirm(string path, bool force)
        {
            var now = Environment.TickCount64;
            if (progress is null || (!force && now - Interlocked.Read(ref lastConfirmReport) < reportInterval))
            {
                return;
            }

            Interlocked.Exchange(ref lastConfirmReport, now);
            progress.Report(new DuplicateScanProgress(
                DuplicateScanPhase.Confirming,
                files.Count,
                toConfirm.Count,
                Volatile.Read(ref confirmedFiles),
                path,
                confirmBytesTotal,
                Interlocked.Read(ref confirmedBytes)));
        }

        if (toConfirm.Count > 0)
        {
            ReportConfirm(string.Empty, force: true);
        }

        var fullHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RunParallel(afterPrefix, workers, file =>
        {
            if (file.SizeInBytes <= PrefixBytes)
            {
                fullHashes[file.FullPath] = prefixHashes[file.FullPath];
                return;
            }

            var hash = TryHash(file, file.SizeInBytes, blockRead =>
            {
                Interlocked.Add(ref confirmedBytes, blockRead);
                ReportConfirm(file.FullPath, force: false);
            });
            Interlocked.Increment(ref confirmedFiles);
            if (hash is null)
            {
                Interlocked.Increment(ref unreadable);
                return;
            }

            fullHashes[file.FullPath] = hash;
            ReportConfirm(file.FullPath, force: false);
        }, cancellationToken);

        if (toConfirm.Count > 0)
        {
            ReportConfirm(string.Empty, force: true); // o último aviso mostra a etapa completa, e não o último que passou pelo intervalo
        }

        var groups = afterPrefix
            .Where(file => fullHashes.ContainsKey(file.FullPath))
            .GroupBy(file => (file.SizeInBytes, Hash: fullHashes[file.FullPath]))
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateGroup(group.Key.Hash, group.Key.SizeInBytes, Ordered(group)))
            .ToList();

        progress?.Report(new DuplicateScanProgress(DuplicateScanPhase.Reading, files.Count, candidates.Count, candidates.Count, string.Empty));
        return new DuplicateScanResult(OrderGroups(groups), files.Count, candidates.Count - unreadable, unreadable);
    }

    private static void RunParallel(List<DuplicateFile> items, int workers, Action<DuplicateFile> action, CancellationToken cancellationToken) =>
        Parallel.ForEach(
            items,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers },
            action);

    /// <summary>SHA-256 dos primeiros <paramref name="byteCount"/> bytes; <see langword="null"/> se o arquivo não pode ser lido.</summary>
    private static string? TryHash(DuplicateFile file, long byteCount, Action<int>? onBlockRead = null)
    {
        if ((file.Attributes & CloudOnly) != 0)
        {
            return null; // só existe na nuvem: ler baixaria o arquivo
        }

        try
        {
            // Até PrefixBytes basta uma leitura e sem leitura adiantada (SequentialScan puxaria muito mais que os 64 KB pedidos);
            // arquivo inteiro maior que isso é lido em sequência.
            var small = byteCount <= PrefixBytes;
            using var handle = File.OpenHandle(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                small ? FileOptions.None : FileOptions.SequentialScan);

            return small ? HashSmall(handle, (int)byteCount) : HashLarge(handle, byteCount, onBlockRead);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }

    private static string HashSmall(SafeFileHandle handle, int byteCount)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PrefixBytes);
        try
        {
            var total = 0;
            while (total < byteCount)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(total, byteCount - total), total);
                if (read <= 0)
                {
                    break; // o arquivo encolheu enquanto se lia: o que veio até aqui é o que vale
                }

                total += read;
            }

            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, total)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string HashLarge(SafeFileHandle handle, long byteCount, Action<int>? onBlockRead)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(FullReadBufferBytes);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < byteCount)
            {
                var wanted = (int)Math.Min(FullReadBufferBytes, byteCount - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, wanted), offset);
                if (read <= 0)
                {
                    break; // o arquivo encolheu enquanto se lia
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
                onBlockRead?.Invoke(read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
