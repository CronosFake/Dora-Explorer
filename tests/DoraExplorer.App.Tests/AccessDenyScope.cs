using System.Security.AccessControl;
using System.Security.Principal;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Cria falta de permissão de verdade (uma regra NTFS de "negar" para o usuário do teste) em pastas e arquivos temporários,
/// e desfaz tudo no <see cref="Dispose"/> para a pasta de teste poder ser apagada. A regra de negar vale até para quem é
/// administrador, então o teste dá o mesmo resultado rodando ou não elevado. Nada fora da pasta de teste é tocado.
/// </summary>
internal sealed class AccessDenyScope : IDisposable
{
    private readonly List<Action> _restore = [];

    public void Deny(string path, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);

        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
            _restore.Add(() =>
            {
                var current = info.GetAccessControl();
                current.RemoveAccessRuleSpecific(rule);
                info.SetAccessControl(current);
            });
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
            _restore.Add(() =>
            {
                var current = info.GetAccessControl();
                current.RemoveAccessRuleSpecific(rule);
                info.SetAccessControl(current);
            });
        }
    }

    /// <summary>Arquivo que não se apaga nem se renomeia: nega "excluir" nele e "excluir itens" na pasta dele.</summary>
    public void MakeUndeletable(string file)
    {
        Deny(file, FileSystemRights.Delete);
        Deny(Path.GetDirectoryName(file)!, FileSystemRights.DeleteSubdirectoriesAndFiles);
    }

    public void Dispose()
    {
        for (var i = _restore.Count - 1; i >= 0; i--)
        {
            _restore[i]();
        }

        _restore.Clear(); // pode ser chamado antes do fim do teste (para "devolver" a permissão) e de novo ao descartar
    }
}
