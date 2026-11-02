using System.ComponentModel;
using System.Text.RegularExpressions;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Texto, em português, das falhas ao mexer em arquivos e pastas. As mensagens que o .NET devolve vêm em inglês e com o prefixo
/// de caminho longo (<c>\\?\C:\...</c>); aqui viram uma frase clara e, quando o motivo é falta de permissão, a frase leva uma
/// marca invisível (<see cref="AccessDeniedMarker"/>) para a janela de aviso saber que pode oferecer reiniciar como administrador.
/// </summary>
internal static partial class FileErrorText
{
    /// <summary>
    /// Caractere invisível (separador invisível, U+2063) no fim de uma mensagem de "acesso negado". Atravessa sem perda as
    /// camadas que juntam várias falhas numa só mensagem; <see cref="ElevationPrompt"/> a reconhece e a remove antes de mostrar.
    /// </summary>
    public const char AccessDeniedMarker = '\u2063';

    private const string LongPathPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";

    // HRESULT_FROM_WIN32(código): 0x8007 nos 16 bits de cima, o código nos de baixo.
    private const int Win32Facility = unchecked((int)0x80070000);

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorDirNotEmpty = 145;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFilenameExcedRange = 206;
    private const int ErrorAccessDenied = 5;
    private const int ShellAccessDeniedSource = 0x78; // DE_ACCESSDENIEDSRC de SHFileOperation

    /// <summary>Caminho sem o prefixo <c>\\?\</c> (e <c>\\?\UNC\servidor\...</c> volta a ser <c>\\servidor\...</c>).</summary>
    public static string DisplayPath(string path)
    {
        if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[LongUncPrefix.Length..];
        }

        return path.StartsWith(LongPathPrefix, StringComparison.Ordinal) ? path[LongPathPrefix.Length..] : path;
    }

    /// <summary>
    /// Frase em português para uma falha ao mexer em <paramref name="itemPath"/>. Se a exceção cita um caminho (por exemplo,
    /// um arquivo dentro da pasta), é esse caminho que aparece. Falta de permissão sai marcada
    /// (<see cref="IsAccessDenied"/>); arquivo somente leitura não, porque ser administrador não resolve.
    /// </summary>
    public static string Describe(Exception exception, string itemPath)
    {
        var name = ItemName(itemPath);
        var inner = PathMentionedBy(exception);
        var shown = Tidy(inner ?? itemPath);

        if (exception is TransferAccessDeniedException transfer)
        {
            return $"{name}: acesso negado ao {transfer.Verb} '{Tidy(transfer.SourcePath)}' para '{Tidy(transfer.DestinationPath)}'.{AccessDeniedMarker}";
        }

        if (exception is UnauthorizedAccessException)
        {
            return IsReadOnlyFile(inner ?? itemPath)
                ? $"{name}: '{shown}' é somente leitura. Remova o atributo somente leitura e tente de novo."
                : AccessDenied(itemPath, inner);
        }

        if (exception is PathTooLongException)
        {
            return $"{name}: o caminho é longo demais.";
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"{name}: '{shown}' não existe mais.";
        }

        // Directory.Delete(recursive) avisa a falta de permissão com um IOException comum, sem código do Windows (por compatibilidade
        // com o .NET Framework): só a frase da mensagem a denuncia. Sem este caso, C:\inetpub saía em inglês e sem oferecer administrador.
        if (exception is IOException && DeniedSentence().IsMatch(exception.Message))
        {
            return AccessDenied(itemPath, inner);
        }

        return Win32CodeOf(exception) switch
        {
            ErrorAccessDenied => AccessDenied(itemPath, inner),
            ErrorSharingViolation or ErrorLockViolation => $"{name}: '{shown}' está em uso por outro programa.",
            ErrorDiskFull or ErrorHandleDiskFull => $"{name}: não há espaço livre no disco.",
            ErrorDirNotEmpty => $"{name}: a pasta '{shown}' não está vazia.",
            ErrorAlreadyExists or ErrorFileExists => $"{name}: já existe um item com esse nome.",
            ErrorFilenameExcedRange => $"{name}: o caminho é longo demais.",
            _ => $"{name}: não foi possível concluir a operação ({exception.Message})."
        };
    }

    /// <summary>Falta de permissão em <paramref name="itemPath"/> (ou em <paramref name="detailPath"/>, dentro dele), já marcada.</summary>
    public static string AccessDenied(string itemPath, string? detailPath = null) =>
        $"{ItemName(itemPath)}: acesso negado a '{Tidy(detailPath ?? itemPath)}'.{AccessDeniedMarker}";

    /// <summary>Falta de permissão em um ou mais itens de um grupo (não se sabe qual). Marcada.</summary>
    public static string AccessDeniedForSome(string action) =>
        $"{action}: acesso negado em um ou mais itens.{AccessDeniedMarker}";

    /// <summary>A pasta existe, mas o Windows não deixa listar o que há nela. Marcada.</summary>
    public static string ListingDenied(string folderPath) =>
        $"Sem permissão para abrir '{Tidy(folderPath)}'.{AccessDeniedMarker}";

    public static bool IsAccessDenied(string message) => message.Contains(AccessDeniedMarker, StringComparison.Ordinal);

    public static string WithoutMarker(string message) =>
        message.Replace(AccessDeniedMarker.ToString(), string.Empty, StringComparison.Ordinal);

    /// <summary>Códigos do Windows e do shell (SHFileOperation) que significam "acesso negado".</summary>
    public static bool IsAccessDeniedCode(int code) => code is ErrorAccessDenied or ShellAccessDeniedSource;

    private static string ItemName(string path)
    {
        var shown = DisplayPath(path);
        var trimmed = shown.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : shown;
    }

    /// <summary>Caminho para mostrar: sem <c>\\?\</c> e sem a barra final (a raiz, como <c>C:\</c>, fica como está).</summary>
    private static string Tidy(string path)
    {
        var shown = DisplayPath(path);
        return shown.Length > 3 ? shown.TrimEnd('\\', '/') : shown;
    }

    /// <summary>O .NET cita o caminho entre aspas simples na mensagem (em inglês e em português); <see cref="FileNotFoundException"/> o traz em <c>FileName</c>.</summary>
    private static string? PathMentionedBy(Exception exception)
    {
        if (exception is FileNotFoundException { FileName: { Length: > 0 } fileName })
        {
            return fileName;
        }

        foreach (Match match in QuotedPath().Matches(exception.Message))
        {
            var candidate = match.Groups[1].Value;
            if (Path.IsPathRooted(DisplayPath(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsReadOnlyFile(string path)
    {
        try
        {
            var plain = DisplayPath(path);
            return File.Exists(plain) && (File.GetAttributes(plain) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }
    }

    /// <summary>Código do Windows por trás da falha: a exceção interna (<c>Win32Exception</c>) ou o HRESULT do tipo 0x8007xxxx.</summary>
    private static int? Win32CodeOf(Exception exception)
    {
        if (exception is Win32Exception own)
        {
            return own.NativeErrorCode;
        }

        if (exception.InnerException is Win32Exception win32)
        {
            return win32.NativeErrorCode;
        }

        return (exception.HResult & unchecked((int)0xFFFF0000)) == Win32Facility ? exception.HResult & 0xFFFF : null;
    }

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedPath();

    /// <summary>A frase de "acesso negado" do .NET, em inglês e em português: <c>Access to the path '...' is denied.</c></summary>
    [GeneratedRegex(@"^(Access to the path|O acesso ao caminho) '.+' (is denied|foi negado)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeniedSentence();
}
