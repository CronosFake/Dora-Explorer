using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using DoraExplorer.App.Models;
using Microsoft.Win32;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

public static class FileAssociationService
{
    public const string FallbackApplicationName = "Escolher aplicativo";

    private static DefaultApplicationOverrides _overrides = new();

    private static readonly bool ShellHandlerEnumerationAvailable = DetectShellHandlerEnumeration();

    public static string GetDefaultApplicationName(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return FallbackApplicationName;
            }

            var extension = Path.GetExtension(filePath);
            if (TryGetUsableOverride(extension, out var userOverride))
            {
                return userOverride.DisplayName;
            }

            var userChoice = TryResolveFromUserChoice(extension);
            if (IsValidDisplayName(userChoice.DisplayName))
            {
                return userChoice.DisplayName!;
            }

            var executable = TryResolveDefaultExecutable(filePath);
            if (!string.IsNullOrWhiteSpace(executable) && IsLaunchableExecutable(executable))
            {
                return GetExecutableDisplayName(executable);
            }

            var handlerName = TryGetRecommendedHandlerName(filePath);
            if (IsValidDisplayName(handlerName))
            {
                return handlerName!;
            }

            return FallbackApplicationName;
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao resolver aplicativo padrão para '{filePath}': {ex.Message}");
            return FallbackApplicationName;
        }
    }

    public static IReadOnlyList<AssociatedApplication> GetAssociatedApplications(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return [];
            }

            var extension = Path.GetExtension(filePath);
            var defaultName = GetDefaultApplicationName(filePath);
            var entries = new List<HandlerEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(extension))
            {
                EnumerateShellHandlers(filePath, extension, entries, seen);
            }

            if (entries.Count == 0)
            {
                EnumerateRegistryHandlers(extension, entries, seen);
            }

            var userChoice = TryResolveFromUserChoice(extension);
            if (IsValidDisplayName(userChoice.DisplayName) && seen.Add(userChoice.DisplayName!))
            {
                ImageSource? icon = null;
                if (!string.IsNullOrWhiteSpace(userChoice.ExecutablePath))
                {
                    icon = ShellIconService.GetFileIcon(userChoice.ExecutablePath, isDirectory: false);
                }

                entries.Add(new HandlerEntry(
                    userChoice.DisplayName!,
                    userChoice.ExecutablePath,
                    IsRecommended: true,
                    icon));
            }

            // O programa padrão do Windows sempre aparece na lista (aplicativos da Loja não têm caminho para executar),
            // mas "Escolher aplicativo" é só o aviso de que não há padrão, não um programa.
            if (IsValidDisplayName(defaultName) &&
                !string.Equals(defaultName, FallbackApplicationName, StringComparison.Ordinal) &&
                !entries.Exists(e => string.Equals(e.DisplayName, defaultName, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(new HandlerEntry(defaultName, null, IsRecommended: true, Icon: null));
            }

            var results = entries
                .Select(entry => new AssociatedApplication(
                    entry.DisplayName,
                    entry.ExecutablePath,
                    IsDefault: string.Equals(entry.DisplayName, defaultName, StringComparison.OrdinalIgnoreCase),
                    IsRecommended: entry.IsRecommended,
                    Icon: entry.Icon))
                .OrderBy(app => app.IsDefault ? 0 : 1)
                .ThenBy(app => app.IsRecommended ? 0 : 1)
                .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (results.Count > 0 && results.All(app => !app.IsDefault))
            {
                var first = results[0];
                results[0] = first with { IsDefault = true };
            }

            return results;
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao listar aplicativos associados para '{filePath}': {ex.Message}");
            return [];
        }
    }

    /// <summary>Avisa quando o aplicativo escolhido para uma extensão muda (o argumento é a extensão, ".txt").</summary>
    public static event EventHandler<string>? OverridesChanged;

    /// <summary>
    /// Liga as escolhas do usuário às configurações: carrega as guardadas e passa a gravar cada mudança.
    /// Chamado uma vez na abertura do programa.
    /// </summary>
    public static void UsePersistence(
        IReadOnlyDictionary<string, (string DisplayName, string? ExecutablePath)> saved,
        Action<string, string, string?> save,
        Action<string> remove) =>
        Install(new DefaultApplicationOverrides(saved, save, remove));

    internal static void Install(DefaultApplicationOverrides overrides)
    {
        _overrides = overrides;
        overrides.Changed += (_, extension) => OverridesChanged?.Invoke(null, extension);
    }

    public static void SetUserOverride(string filePath, string? executablePath, string displayName) =>
        _overrides.Set(Path.GetExtension(filePath), displayName, executablePath);

    public static bool ClearUserOverride(string filePath) => _overrides.Clear(Path.GetExtension(filePath));

    public static bool HasUserOverride(string filePath) => TryGetUsableOverride(Path.GetExtension(filePath), out _);

    /// <summary>
    /// A escolha do usuário só vale enquanto o programa escolhido existir: se ele foi desinstalado,
    /// volta a valer o padrão do Windows em vez de tentar abrir um arquivo que não está mais lá.
    /// </summary>
    private static bool TryGetUsableOverride(string? extension, out (string DisplayName, string? ExecutablePath) entry)
    {
        if (_overrides.TryGet(extension, out entry) &&
            (string.IsNullOrWhiteSpace(entry.ExecutablePath) || File.Exists(entry.ExecutablePath)))
        {
            return true;
        }

        entry = default;
        return false;
    }

    public static bool TryGetUserOverrideExecutable(string extension, out string? executablePath)
    {
        executablePath = null;
        if (!TryGetUsableOverride(extension, out var userOverride))
        {
            return false;
        }

        executablePath = userOverride.ExecutablePath;
        return !string.IsNullOrWhiteSpace(executablePath);
    }

    /// <summary>Programa que o sistema consegue executar direto (.exe, .com, .bat, .cmd).</summary>
    public static bool IsLaunchable(string path) => IsLaunchableExecutable(path);
    private static (string? DisplayName, string? ExecutablePath) TryResolveFromUserChoice(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return (null, null);
        }

        using var userChoiceKey = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
        var progId = userChoiceKey?.GetValue("ProgId") as string;
        if (string.IsNullOrWhiteSpace(progId))
        {
            return (null, null);
        }

        if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveAppXHandler(progId);
        }

        var executable = TryResolveExecutableFromProgId(progId);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return (null, null);
        }

        return (GetExecutableDisplayName(executable), executable);
    }

    private static (string? DisplayName, string? ExecutablePath) TryResolveAppXHandler(string progId)
    {
        using var applicationKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\Application");
        var applicationName = TryLoadIndirectString(applicationKey?.GetValue("ApplicationName") as string);

        using var openKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open");
        var packageId = openKey?.GetValue("PackageId") as string;
        var relativeExecutable = openKey?.GetValue("PackageRelativeExecutable") as string;
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(relativeExecutable))
        {
            return (applicationName, null);
        }

        var windowsAppsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        var executablePath = Path.Combine(windowsAppsRoot, packageId, relativeExecutable);
        if (!File.Exists(executablePath))
        {
            executablePath = TryFindWindowsAppExecutable(windowsAppsRoot, packageId, relativeExecutable);
        }

        // O nome oficial do aplicativo (o que a Loja e o menu Iniciar mostram) vale mais que a descrição do executável.
        var displayName = applicationName;
        if (!IsValidDisplayName(displayName) && !string.IsNullOrWhiteSpace(executablePath))
        {
            var executableDisplayName = GetExecutableDisplayName(executablePath);
            if (IsValidDisplayName(executableDisplayName) &&
                !string.Equals(executableDisplayName, FallbackApplicationName, StringComparison.Ordinal))
            {
                displayName = executableDisplayName;
            }
        }

        displayName ??= DeriveNameFromPackageId(packageId);
        return (displayName, executablePath);
    }

    private static string? TryFindWindowsAppExecutable(string windowsAppsRoot, string packageId, string relativeExecutable)
    {
        if (!Directory.Exists(windowsAppsRoot))
        {
            return null;
        }

        var familyPrefix = packageId.Split('_')[0];
        foreach (var directory in Directory.EnumerateDirectories(windowsAppsRoot, familyPrefix + "_*"))
        {
            var candidate = Path.Combine(directory, relativeExecutable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? DeriveNameFromPackageId(string packageId)
    {
        var family = packageId.Split('_')[0];
        var segments = family.Split('.');
        return segments.Length == 0 ? null : segments[^1];
    }

    private static string? TryLoadIndirectString(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(512);
            var length = (uint)buffer.Capacity;
            var hr = SHLoadIndirectString(source, buffer, ref length, IntPtr.Zero);
            if (hr is 0 or 1)
            {
                var value = buffer.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            // Ignorar falhas de resolução de recursos ms-resource.
        }

        return null;
    }

    private sealed record HandlerEntry(
        string DisplayName,
        string? ExecutablePath,
        bool IsRecommended,
        ImageSource? Icon);

    private static void EnumerateShellHandlers(
        string filePath,
        string extension,
        List<HandlerEntry> results,
        HashSet<string> seen)
    {
        if (!ShellHandlerEnumerationAvailable)
        {
            return;
        }

        try
        {
            var hr = SHAssocEnumHandlers(extension, AssocFilter.Recommended | AssocFilter.Extra, out var enumerator);
            if (hr != 0 || enumerator is null)
            {
                return;
            }

            try
            {
                while (true)
                {
                    hr = enumerator.Next(1, out var handler, out var fetched);
                    if (hr != 0 || fetched == 0 || handler is null)
                    {
                        break;
                    }

                    if (handler.GetUIName(out var uiName) != 0 || !IsValidDisplayName(uiName))
                    {
                        continue;
                    }

                    string? handlerName = null;
                    if (handler.GetName(out var progId) == 0 && !string.IsNullOrWhiteSpace(progId))
                    {
                        handlerName = progId;
                    }

                    // Aplicativo da Microsoft Store identificado só pelo AUMID: não há caminho para executar e o nome
                    // viraria "Microsoft". O programa padrão já entra na lista pelo caminho normal.
                    if (IsPackagedAppId(handlerName))
                    {
                        continue;
                    }

                    var executablePath = ResolveHandlerExecutable(handlerName);

                    var displayName = uiName;
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        displayName = ChooseHandlerDisplayName(uiName, GetDisplayNameForExecutable(executablePath), seen);
                    }

                    if (!seen.Add(displayName))
                    {
                        continue;
                    }

                    var isRecommended = false;
                    try
                    {
                        handler.IsRecommended(out isRecommended);
                    }
                    catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
                    {
                        // Alguns handlers não suportam IsRecommended de forma fiável.
                    }

                    ImageSource? icon = TryGetHandlerIcon(handler, filePath, executablePath);

                    results.Add(new HandlerEntry(displayName, executablePath, isRecommended, icon));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao enumerar handlers shell para '{extension}': {ex.Message}");
        }
    }

    private static ImageSource? TryGetHandlerIcon(IAssocHandler handler, string filePath, string? executablePath)
    {
        var pathPtr = IntPtr.Zero;
        try
        {
            pathPtr = Marshal.StringToHGlobalUni(filePath);
            handler.GetIcon(pathPtr, AssocHandlerFlags.None, out var hIcon);
            if (hIcon != IntPtr.Zero)
            {
                return ShellIconService.FromHIcon(hIcon);
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            // Ignorar falhas de ícone do handler.
        }
        finally
        {
            if (pathPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            return ShellIconService.GetFileIcon(executablePath, isDirectory: false);
        }

        return null;
    }

    private static void EnumerateRegistryHandlers(
        string? extension,
        List<HandlerEntry> results,
        HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return;
        }

        var openWithKey = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithList";
        using var key = Registry.CurrentUser.OpenSubKey(openWithKey);
        if (key is null)
        {
            return;
        }

        foreach (var valueName in key.GetValueNames())
        {
            if (valueName is not ("a" or "b" or "c" or "d" or "e" or "f" or "g" or "h" or "i" or "j" or "k" or "l" or "m"))
            {
                continue;
            }

            var exeName = key.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(exeName))
            {
                continue;
            }

            // Sem caminho existente não há como executar o programa: fica fora da lista.
            var executablePath = TryResolveAppPath(exeName) ?? TryResolveOpenWithListExecutable(exeName);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                continue;
            }

            var displayName = ChooseHandlerDisplayName(
                Path.GetFileNameWithoutExtension(exeName),
                GetDisplayNameForExecutable(executablePath),
                seen);
            if (!seen.Add(displayName))
            {
                continue;
            }

            var icon = ShellIconService.GetFileIcon(executablePath, isDirectory: false);
            results.Add(new HandlerEntry(displayName, executablePath, IsRecommended: false, icon));
        }
    }

    /// <summary>
    /// Identificador de aplicativo empacotado (AUMID), como <c>Microsoft.WindowsNotepad_8wekyb3d8bbwe!App</c>:
    /// não é um caminho nem um programa que dê para executar direto.
    /// </summary>
    internal static bool IsPackagedAppId(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Contains('!', StringComparison.Ordinal) &&
        !name.Contains('\\', StringComparison.Ordinal) &&
        !name.Contains('/', StringComparison.Ordinal) &&
        !name.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// Nome mostrado de um programa listado pelo shell. O nome do shell costuma ser o do executável ("firefox",
    /// "WINWORD"); nesse caso vale a descrição do próprio executável ("Firefox", "Microsoft Word"). Um nome já
    /// amigável do shell é mantido, e uma descrição que já está na lista não é repetida.
    /// </summary>
    internal static string ChooseHandlerDisplayName(string shellName, string? executableName, ISet<string> alreadyListed)
    {
        if (!IsValidDisplayName(executableName) ||
            string.Equals(executableName, shellName, StringComparison.Ordinal) ||
            alreadyListed.Contains(executableName!))
        {
            return shellName;
        }

        return executableName!;
    }

    /// <summary>
    /// Caminho real do programa de um handler do shell: o que o ProgID registra, ou o nome do executável
    /// ("firefox.exe") procurado em App Paths, na pasta do sistema e no PATH. Sem caminho existente, devolve nulo.
    /// </summary>
    private static string? ResolveHandlerExecutable(string? handlerName)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
        {
            return null;
        }

        var fromProgId = TryResolveExecutableFromProgId(handlerName);
        if (!string.IsNullOrWhiteSpace(fromProgId) && File.Exists(fromProgId))
        {
            return fromProgId;
        }

        if (!IsLaunchableExecutable(handlerName))
        {
            return null;
        }

        return TryResolveAppPath(handlerName) ?? TryResolveOpenWithListExecutable(handlerName);
    }

    private static string? TryResolveAppPath(string exeName)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (TryNormalizeExecutablePath(key?.GetValue(null) as string, out var normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? TryResolveOpenWithListExecutable(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return null;
        }

        if (Path.IsPathRooted(exeName) && File.Exists(exeName))
        {
            return exeName;
        }

        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemPath = Path.Combine(systemDir, exeName);
        if (File.Exists(systemPath))
        {
            return systemPath;
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in pathDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                // Ignorar entradas inválidas do PATH.
            }
        }

        return null;
    }

    private static string? TryResolveDefaultExecutable(string filePath)
    {
        foreach (var assoc in GetAssociationCandidates(filePath))
        {
            var flags = string.Equals(assoc, filePath, StringComparison.OrdinalIgnoreCase)
                ? AssocF.Init_ByExeName
                : AssocF.None;

            var executable = TryQueryAssocString(assoc, AssocStr.Executable, "open", flags)
                ?? TryQueryAssocString(assoc, AssocStr.Executable, null, flags);
            if (TryNormalizeExecutablePath(executable, out var normalized) && IsLaunchableExecutable(normalized))
            {
                return normalized;
            }

            var command = TryQueryAssocString(assoc, AssocStr.Command, "open", flags)
                ?? TryQueryAssocString(assoc, AssocStr.Command, null, flags);
            if (TryParseExecutableFromCommand(command, out normalized) && IsLaunchableExecutable(normalized))
            {
                return normalized;
            }
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        var progId = extensionKey?.GetValue(null) as string;
        if (!string.IsNullOrWhiteSpace(progId))
        {
            var fromProgId = TryResolveExecutableFromProgId(progId);
            if (!string.IsNullOrWhiteSpace(fromProgId) && IsLaunchableExecutable(fromProgId))
            {
                return fromProgId;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetAssociationCandidates(string filePath)
    {
        yield return filePath;

        var extension = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(extension))
        {
            yield return extension;
        }
    }

    private static string? TryGetRecommendedHandlerName(string filePath)
    {
        if (!ShellHandlerEnumerationAvailable)
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        try
        {
            var hr = SHAssocEnumHandlers(extension, AssocFilter.Recommended | AssocFilter.Extra, out var enumerator);
            if (hr != 0 || enumerator is null)
            {
                return null;
            }

            string? fallbackName = null;

            try
            {
                while (true)
                {
                    hr = enumerator.Next(1, out var handler, out var fetched);
                    if (hr != 0 || fetched == 0 || handler is null)
                    {
                        break;
                    }

                    if (handler.GetUIName(out var uiName) != 0 || !IsValidDisplayName(uiName))
                    {
                        continue;
                    }

                    try
                    {
                        handler.IsRecommended(out var isRecommended);
                        if (isRecommended)
                        {
                            return uiName;
                        }
                    }
                    catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
                    {
                        // Alguns handlers não suportam IsRecommended de forma fiável.
                    }

                    fallbackName ??= uiName;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            return fallbackName;
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao obter handler recomendado para '{extension}': {ex.Message}");
            return null;
        }
    }

    private static string? TryQueryAssocString(string assoc, AssocStr str, string? verb, AssocF flags = AssocF.None)
    {
        if (string.IsNullOrWhiteSpace(assoc))
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            var hr = AssocQueryString(flags, str, assoc, verb, buffer, ref size);
            if (hr is not (0 or 1))
            {
                return null;
            }

            var value = buffer.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            return null;
        }
    }

    private static string? TryResolveExecutableFromProgId(string progId)
    {
        if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveAppXHandler(progId).ExecutablePath;
        }

        using var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        var command = key?.GetValue(null) as string;
        return TryParseExecutableFromCommand(command, out var executable) ? executable : null;
    }

    private static bool TryParseExecutableFromCommand(string? command, out string executable)
    {
        executable = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        string candidate;

        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }

            candidate = trimmed[1..endQuote];
        }
        else
        {
            var spaceIndex = trimmed.IndexOf(' ');
            candidate = spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
        }

        return TryNormalizeExecutablePath(candidate, out executable);
    }

    private static bool TryNormalizeExecutablePath(string? path, out string executable)
    {
        executable = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Path.IsPathRooted(expanded))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                expanded);
        }

        if (!File.Exists(expanded))
        {
            return false;
        }

        if (IsAssociationPlaceholderExecutable(expanded))
        {
            return false;
        }

        executable = expanded;
        return true;
    }

    private static bool IsAssociationPlaceholderExecutable(string path) =>
        Path.GetFileName(path).Equals("OpenWith.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsLaunchableExecutable(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidDisplayName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.StartsWith("prop:", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("appx:", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains(';', StringComparison.Ordinal) &&
        !IsPlaceholderDisplayName(name);

    private static bool IsPlaceholderDisplayName(string name) =>
        name.Equals("Pick an app", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayNameForExecutable(string executablePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            return PickDisplayName(versionInfo.FileDescription, versionInfo.ProductName, executablePath);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            // Ignorar ficheiros inválidos ou inacessíveis.
        }

        return PickDisplayName(null, null, executablePath);
    }

    /// <summary>
    /// Escolhe o nome mostrado de um aplicativo. Alguns programas gravam o nome do arquivo na "descrição"
    /// (o Bloco de Notas da Microsoft Store traz literalmente "Notepad.exe"); a extensão ".exe" nunca é um nome.
    /// </summary>
    internal static string PickDisplayName(string? fileDescription, string? productName, string executablePath)
    {
        var description = StripExecutableExtension(fileDescription);
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var product = StripExecutableExtension(productName);
        if (!string.IsNullOrWhiteSpace(product))
        {
            return product;
        }

        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(fileName) ? FallbackApplicationName : fileName;
    }

    private static string? StripExecutableExtension(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd" })
        {
            if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed[..^extension.Length].TrimEnd();
                return stripped.Length == 0 ? null : stripped;
            }
        }

        return trimmed;
    }

    private static string GetExecutableDisplayName(string executablePath)
    {
        var name = GetDisplayNameForExecutable(executablePath);
        return IsValidDisplayName(name) ? name : FallbackApplicationName;
    }

    private static bool DetectShellHandlerEnumeration()
    {
        try
        {
            var hr = SHAssocEnumHandlers(".txt", AssocFilter.Recommended, out var enumerator);
            if (hr == 0 && enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            return false;
        }

        return false;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int AssocQueryString(
        AssocF flags,
        AssocStr str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder pszOut,
        ref int pcchOut);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHAssocEnumHandlers(
        string pszExtra,
        AssocFilter afFilter,
        [MarshalAs(UnmanagedType.Interface)] out IEnumAssocHandlers? ppEnum);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        ref uint pcchOutBuf,
        IntPtr ppvReserved);

    [Flags]
    private enum AssocF : uint
    {
        None = 0,
        Init_ByExeName = 0x2
    }

    private enum AssocStr : uint
    {
        Command = 1,
        Executable = 2
    }

    [Flags]
    private enum AssocFilter : uint
    {
        Recommended = 0x1,
        Extra = 0x2
    }

    [ComImport]
    [Guid("E7F87CA4-9F63-4169-833F-078B6EE3D16E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler? rgelt, out uint pceltFetched);

        void Skip(uint celt);

        void Reset();

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumAssocHandlers Clone();
    }

    [ComImport]
    [Guid("7847C113-C717-436B-A2F2-6B726AB1B439")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig]
        int GetName([MarshalAs(UnmanagedType.LPWStr)] out string psz);

        [PreserveSig]
        int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string psz);

        void GetIcon([In] IntPtr pszPath, [In] AssocHandlerFlags flags, out IntPtr phicon);

        void IsRecommended(out bool pbool);
    }

    private enum AssocHandlerFlags : uint
    {
        None = 0
    }
}
