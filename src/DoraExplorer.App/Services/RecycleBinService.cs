using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DoraExplorer.App.Models;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

public static class RecycleBinService
{
    private const int CSIDL_BITBUCKET = 0x0a;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shqueryrbinfo
    {
        public int CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref Shqueryrbinfo pSHQueryRBInfo);

    public static bool HasItems()
    {
        var info = new Shqueryrbinfo { CbSize = Marshal.SizeOf<Shqueryrbinfo>() };
        return SHQueryRecycleBin(string.Empty, ref info) == 0 && info.I64NumItems > 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // A confirmação é a do próprio app (MessageDialog); o Windows só mostra o progresso, que ajuda numa Lixeira grande.
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOSOUND = 0x00000004;

    // O que de fato esvazia a Lixeira do Windows. Os testes trocam por uma função de mentira: nunca esvazie a Lixeira do usuário num teste.
    internal static Func<int> EmptyNative { get; set; } = () =>
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOSOUND);

    // Quem diz se há itens (a consulta ao Windows); trocável pelos testes pelo mesmo motivo.
    internal static Func<bool> HasItemsNative { get; set; } = HasItems;

    /// <summary>
    /// Esvazia a Lixeira de todas as unidades (o mesmo que "Esvaziar Lixeira" do Windows). Já vazia não é erro. O pedido ao Windows
    /// vai por uma thread própria no modo STA, que é o que o shell espera, para não travar a tela numa Lixeira grande.
    /// </summary>
    public static bool TryEmpty(out string? errorMessage)
    {
        errorMessage = null;
        if (!HasItemsNative())
        {
            return true;
        }

        var result = 0;
        var thread = new Thread(() =>
        {
            try
            {
                result = EmptyNative();
            }
            catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
            {
                AppLogService.Warning($"Falha ao esvaziar a lixeira: {ex.Message}");
                result = ex.HResult != 0 ? ex.HResult : -1;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 0 = sucesso. 0x8000FFFF (E_UNEXPECTED) é o que algumas versões devolvem quando a Lixeira esvaziou sozinha no meio do pedido.
        if (result == 0 || (uint)result == 0x8000FFFFu)
        {
            AppLogService.Info("Lixeira esvaziada.");
            return true;
        }

        AppLogService.Warning($"Esvaziar a lixeira devolveu 0x{result:X8}.");
        errorMessage = "Não foi possível esvaziar a Lixeira. Algum item pode estar em uso ou sem permissão.";
        return false;
    }

    /// <summary>Caminhos (na Lixeira) de todos os itens que ela guarda, para restaurar tudo.</summary>
    public static IReadOnlyList<string> GetAllItemPaths() =>
        GetEntries()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FullPath))
            .Select(entry => entry.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static (int Folders, int Files) GetCounts()
    {
        var folders = 0;
        var files = 0;

        foreach (var entry in GetEntries())
        {
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

    public static IReadOnlyList<FileSystemEntry> GetEntries()
    {
        var shellEntries = TryGetEntriesFromShell();
        if (shellEntries.Count > 0)
        {
            return shellEntries;
        }

        return GetEntriesFromFilesystem();
    }

    private static List<FileSystemEntry> TryGetEntriesFromShell()
    {
        var entries = new List<FileSystemEntry>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return entries;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic recycleBin = shell.NameSpace(CSIDL_BITBUCKET);
            if (recycleBin is null)
            {
                return entries;
            }

            foreach (dynamic item in recycleBin.Items())
            {
                var name = (string)item.Name;
                var path = (string)item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var isFolder = (bool)item.IsFolder;
                long size = 0;
                try
                {
                    size = item.Size;
                }
                catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
                {
                    // Alguns itens da lixeira não expõem tamanho via Shell.
                }

                DateTime? modified = null;
                try
                {
                    modified = item.ModifyDate;
                }
                catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
                {
                    // Ignorar — a data pode não estar disponível para todos os itens.
                }

                entries.Add(new FileSystemEntry
                {
                    Name = name,
                    FullPath = path,
                    OriginalPath = TryResolveOriginalPath(path),
                    IsDirectory = isFolder,
                    SizeInBytes = isFolder ? null : size,
                    LastModified = modified,
                    IsRecycleBinItem = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao enumerar a lixeira via Shell: {ex.Message}");
        }

        return entries;
    }

    private static List<FileSystemEntry> GetEntriesFromFilesystem()
    {
        var entries = new List<FileSystemEntry>();
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(sid))
        {
            AppLogService.Warning("Não foi possível obter o SID do utilizador para ler a lixeira.");
            return entries;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var binPath = Path.Combine(drive.Name, "$Recycle.Bin", sid);
            if (!Directory.Exists(binPath))
            {
                continue;
            }

            try
            {
                foreach (var infoFile in Directory.EnumerateFiles(binPath, "$I*"))
                {
                    if (!TryParseInfoFile(infoFile, out var originalPath, out var size, out var deletedAt))
                    {
                        continue;
                    }

                    var suffix = Path.GetFileName(infoFile)[2..];
                    var dataFile = Path.Combine(binPath, "$R" + suffix);
                    var isDirectory = Directory.Exists(dataFile);
                    if (!isDirectory && !File.Exists(dataFile))
                    {
                        continue;
                    }

                    entries.Add(new FileSystemEntry
                    {
                        Name = GetDisplayName(originalPath),
                        FullPath = dataFile,
                        OriginalPath = originalPath,
                        IsDirectory = isDirectory,
                        SizeInBytes = isDirectory ? null : size,
                        LastModified = deletedAt,
                        IsRecycleBinItem = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogService.Warning($"Não foi possível ler a lixeira em {binPath}: {ex.Message}");
            }
        }

        return entries;
    }

    private static bool TryParseInfoFile(
        string infoPath,
        out string originalPath,
        out long size,
        out DateTime deletedAt)
    {
        originalPath = string.Empty;
        size = 0;
        deletedAt = default;

        try
        {
            using var stream = File.OpenRead(infoPath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 28)
            {
                return false;
            }

            var version = reader.ReadInt64();
            if (version == 2)
            {
                return TryParseInfoFileV2(reader, stream.Length, out originalPath, out size, out deletedAt);
            }

            if (version >= 24)
            {
                return TryParseInfoFileLegacy(reader, stream.Length, version, out originalPath, out size, out deletedAt);
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao ler metadados da lixeira: {infoPath}", ex);
            return false;
        }
    }

    private static bool TryParseInfoFileV2(
        BinaryReader reader,
        long streamLength,
        out string originalPath,
        out long size,
        out DateTime deletedAt)
    {
        originalPath = string.Empty;
        size = 0;
        deletedAt = default;

        if (streamLength < 28)
        {
            return false;
        }

        size = reader.ReadInt64();
        deletedAt = DateTime.FromFileTimeUtc(reader.ReadInt64()).ToLocalTime();
        _ = reader.ReadInt32();

        var remaining = (int)(streamLength - reader.BaseStream.Position);
        if (remaining <= 0)
        {
            return false;
        }

        originalPath = Encoding.Unicode
            .GetString(reader.ReadBytes(remaining))
            .TrimEnd('\0');

        return !string.IsNullOrWhiteSpace(originalPath);
    }

    private static bool TryParseInfoFileLegacy(
        BinaryReader reader,
        long streamLength,
        long headerSize,
        out string originalPath,
        out long size,
        out DateTime deletedAt)
    {
        originalPath = string.Empty;
        size = 0;
        deletedAt = default;

        size = reader.ReadInt64();
        deletedAt = DateTime.FromFileTimeUtc(reader.ReadInt64()).ToLocalTime();

        var remaining = (int)(streamLength - reader.BaseStream.Position);
        if (remaining <= 0)
        {
            return false;
        }

        originalPath = Encoding.Unicode
            .GetString(reader.ReadBytes(remaining))
            .TrimEnd('\0');

        return !string.IsNullOrWhiteSpace(originalPath);
    }

    private static string GetDisplayName(string originalPath)
    {
        var trimmed = originalPath.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return originalPath;
        }

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public static bool IsRecycleBinDataPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Contains(@"$Recycle.Bin\", StringComparison.OrdinalIgnoreCase);

    public static string? TryResolveOriginalPath(string? dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath) || !IsRecycleBinDataPath(dataPath))
        {
            return null;
        }

        var infoPath = GetInfoPathForData(dataPath);
        if (!File.Exists(infoPath))
        {
            return null;
        }

        return TryParseInfoFile(infoPath, out var originalPath, out _, out _)
            ? originalPath
            : null;
    }

    public static bool TryRestoreItems(IReadOnlyList<string> dataPaths, out string? errorMessage)
    {
        errorMessage = null;
        if (dataPaths.Count == 0)
        {
            errorMessage = "Selecione um item da lixeira para restaurar.";
            return false;
        }

        var failures = new List<string>();
        foreach (var dataPath in dataPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsRecycleBinDataPath(dataPath))
            {
                failures.Add($"{Path.GetFileName(dataPath)}: caminho inválido da lixeira.");
                continue;
            }

            if (!TryRestoreSingle(dataPath, out var itemError))
            {
                failures.Add(itemError ?? $"{Path.GetFileName(dataPath)}: falha ao restaurar.");
            }
        }

        if (failures.Count == 0)
        {
            return true;
        }

        errorMessage = failures.Count == 1
            ? failures[0]
            : string.Join(Environment.NewLine, failures);
        return failures.Count < dataPaths.Count;
    }

    public static bool TryDeletePermanently(IReadOnlyList<string> dataPaths, out string? errorMessage)
    {
        errorMessage = null;
        if (dataPaths.Count == 0)
        {
            errorMessage = "Selecione um item da lixeira para excluir.";
            return false;
        }

        var failures = new List<string>();
        foreach (var dataPath in dataPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsRecycleBinDataPath(dataPath))
            {
                failures.Add($"{Path.GetFileName(dataPath)}: caminho inválido da lixeira.");
                continue;
            }

            try
            {
                if (File.Exists(dataPath))
                {
                    File.Delete(dataPath);
                }
                else if (Directory.Exists(dataPath))
                {
                    Directory.Delete(dataPath, recursive: true);
                }
                else
                {
                    failures.Add($"{Path.GetFileName(dataPath)}: o item não existe.");
                    continue;
                }

                var infoPath = GetInfoPathForData(dataPath);
                if (File.Exists(infoPath))
                {
                    File.Delete(infoPath);
                }

                AppLogService.Info($"Excluído permanentemente da lixeira: {dataPath}");
            }
            catch (Exception ex)
            {
                failures.Add(FileErrorText.Describe(ex, dataPath));
                AppLogService.Error($"Falha ao excluir permanentemente da lixeira: {dataPath}", ex);
            }
        }

        if (failures.Count == 0)
        {
            return true;
        }

        errorMessage = failures.Count == 1
            ? failures[0]
            : string.Join(Environment.NewLine, failures);
        return failures.Count < dataPaths.Count;
    }

    private static bool TryRestoreSingle(string dataPath, out string? errorMessage)
    {
        errorMessage = null;

        if (TryRestoreViaShell(dataPath))
        {
            AppLogService.Info($"Restaurado da lixeira: {dataPath}");
            return true;
        }

        var infoPath = GetInfoPathForData(dataPath);
        if (!File.Exists(infoPath) ||
            !TryParseInfoFile(infoPath, out var originalPath, out _, out _))
        {
            errorMessage = $"{Path.GetFileName(dataPath)}: não foi possível obter o destino original.";
            return false;
        }

        try
        {
            var destinationDirectory = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (File.Exists(dataPath))
            {
                if (File.Exists(originalPath))
                {
                    errorMessage = $"{Path.GetFileName(originalPath)}: já existe um item com este nome no destino.";
                    return false;
                }

                File.Move(dataPath, originalPath);
            }
            else if (Directory.Exists(dataPath))
            {
                if (Directory.Exists(originalPath) || File.Exists(originalPath))
                {
                    errorMessage = $"{Path.GetFileName(originalPath)}: já existe um item com este nome no destino.";
                    return false;
                }

                Directory.Move(dataPath, originalPath);
            }
            else
            {
                errorMessage = $"{Path.GetFileName(dataPath)}: o item não existe.";
                return false;
            }

            if (File.Exists(infoPath))
            {
                File.Delete(infoPath);
            }

            AppLogService.Info($"Restaurado da lixeira: {dataPath} -> {originalPath}");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = FileErrorText.Describe(ex, dataPath);
            AppLogService.Error($"Falha ao restaurar da lixeira: {dataPath}", ex);
            return false;
        }
    }

    private static bool TryRestoreViaShell(string dataPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic recycleBin = shell.NameSpace(CSIDL_BITBUCKET);
            if (recycleBin is null)
            {
                return false;
            }

            foreach (dynamic item in recycleBin.Items())
            {
                var path = (string)item.Path;
                if (!string.Equals(path, dataPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var verb in new[] { "undelete", "restore", "RESTORE" })
                {
                    try
                    {
                        item.InvokeVerb(verb);
                        return true;
                    }
                    catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
                    {
                        // Tentar o próximo verbo conhecido.
                    }
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao restaurar via Shell: {dataPath} — {ex.Message}");
        }

        return false;
    }

    private static string GetInfoPathForData(string dataPath)
    {
        var directory = Path.GetDirectoryName(dataPath) ?? string.Empty;
        var fileName = Path.GetFileName(dataPath);
        if (fileName.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(directory, "$I" + fileName[2..]);
        }

        return dataPath;
    }
}
