using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

public static class FileOpenService
{
    public static bool TryOpen(string filePath, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errorMessage = "Caminho do ficheiro inválido.";
            return false;
        }

        if (!File.Exists(filePath))
        {
            errorMessage = $"O ficheiro não existe: {filePath}";
            return false;
        }

        if (NetworkFileService.TryParseRemotePath(filePath, out _, out _, out _))
        {
            errorMessage = "Abrir ficheiros remotos ainda não está disponível para este protocolo.";
            return false;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(extension) &&
                FileAssociationService.TryGetUserOverrideExecutable(extension, out var overrideExecutable) &&
                !string.IsNullOrWhiteSpace(overrideExecutable))
            {
                Process.Start(new ProcessStartInfo(overrideExecutable, $"\"{filePath}\"")
                {
                    UseShellExecute = true
                });
                return true;
            }

            Process.Start(new ProcessStartInfo(filePath)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
        {
            // O Windows pediu confirmação de administrador para abrir o programa e o usuário recusou: não é erro.
            AppLogService.Info($"Abertura cancelada pelo usuário na confirmação do Windows: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao abrir '{filePath}'.", ex);
            errorMessage = FileErrorText.Describe(ex, filePath);
            return false;
        }
    }

    // ERROR_CANCELLED: o usuário recusou a confirmação de administrador (UAC) ao abrir o programa.
    private const int ErrorCancelledByUser = 1223;

    // HRESULT_FROM_WIN32(ERROR_CANCELLED): o usuário fechou a janela "Abrir com" sem escolher.
    private const int OpenWithCancelled = unchecked((int)0x800704C7);

    private const uint OaifAllowRegistration = 0x1;

    private const uint OaifExec = 0x4;

    /// <summary>
    /// Abre a janela "Abrir com" do Windows para o arquivo. Usa SHOpenWithDialog: o antigo
    /// <c>rundll32 shell32.dll,OpenAs_RunDLL</c> termina sem mostrar nada no Windows 11 recente.
    /// A janela é modal e precisa rodar numa thread de interface (STA), com a janela do app como dona.
    /// Escolher um programa abre o arquivo com ele; marcar "Sempre" muda o padrão do Windows.
    /// </summary>
    /// <returns>Falso se o usuário cancelou ou se houve erro (então <paramref name="errorMessage"/> diz o motivo, ou fica vazio no cancelamento).</returns>
    public static bool TryShowNativeOpenWithDialog(string filePath, IntPtr owner, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errorMessage = "Caminho do ficheiro inválido.";
            return false;
        }

        if (!File.Exists(filePath))
        {
            errorMessage = $"O ficheiro não existe: {filePath}";
            return false;
        }

        if (NetworkFileService.TryParseRemotePath(filePath, out _, out _, out _))
        {
            errorMessage = "Abrir ficheiros remotos ainda não está disponível para este protocolo.";
            return false;
        }

        try
        {
            var info = new OpenAsInfo
            {
                File = filePath,
                Class = null,
                Flags = OaifAllowRegistration | OaifExec
            };

            var result = SHOpenWithDialog(owner, ref info);
            if (result == 0)
            {
                return true;
            }

            if (result != OpenWithCancelled)
            {
                AppLogService.Warning($"A janela 'Abrir com' do Windows falhou para '{filePath}' (HRESULT 0x{result:X8}).");
                errorMessage = $"O Windows não conseguiu abrir a janela \"Abrir com\" (código 0x{result:X8}).";
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao abrir diálogo nativo 'Abrir com' para '{filePath}'.", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string File;

        public string? Class;

        public uint Flags;
    }

    public static void OpenFiles(IEnumerable<string> filePaths)
    {
        var errors = new List<string>();

        foreach (var path in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryOpen(path, out var error) && !string.IsNullOrWhiteSpace(error))
            {
                errors.Add($"{Path.GetFileName(path)}: {error}");
            }
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                string.Join(Environment.NewLine, errors),
                "Dora Explorer",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }
}
