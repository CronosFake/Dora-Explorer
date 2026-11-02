using System.Windows;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.Views.Dialogs;

public partial class DeleteChoiceDialog : Window
{
    public DeleteChoiceDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public FileDeleteMode? SelectedMode { get; private set; }

    public static FileDeleteMode? TryChoose(Window owner, IReadOnlyList<string> paths)
    {
        // Servidores não têm Lixeira: só existe a exclusão definitiva, então a escolha vira uma confirmação.
        if (paths.All(path => NetworkFileService.TryParseRemotePath(path, out _, out _, out _)))
        {
            return MessageDialog.Confirm(
                owner,
                $"Deseja excluir {DescribeTargets(paths)} do servidor? Servidores não têm Lixeira: esta ação não pode ser desfeita.",
                "Excluir no servidor")
                ? FileDeleteMode.Permanent
                : null;
        }

        var dialog = new DeleteChoiceDialog(BuildChoiceMessage(paths));
        DialogChrome.Apply(dialog, owner);
        return dialog.ShowDialog() == true ? dialog.SelectedMode : null;
    }

    public static bool ConfirmShortcut(Window owner, IReadOnlyList<string> paths, FileDeleteMode mode)
    {
        var message = mode == FileDeleteMode.Permanent
            ? $"Deseja excluir permanentemente {DescribeTargets(paths)}? Esta ação não pode ser desfeita."
            : $"Deseja mover {DescribeTargets(paths)} para a lixeira?";

        var title = mode == FileDeleteMode.Permanent ? "Excluir permanentemente" : "Mover para a lixeira";
        return MessageDialog.Confirm(owner, message, title);
    }

    public static string DescribeTargets(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "os itens selecionados";
        }

        if (paths.Count == 1)
        {
            return $"'{DisplayName(paths[0])}'";
        }

        return $"{paths.Count} itens";
    }

    /// <summary>Nome do item; nos servidores, o último trecho do caminho já sem a codificação da URL.</summary>
    private static string DisplayName(string path) =>
        NetworkFileService.TryParseRemotePath(path, out _, out _, out var remotePath)
            ? remotePath.TrimEnd('/').Split('/')[^1]
            : Path.GetFileName(path);

    private static string BuildChoiceMessage(IReadOnlyList<string> paths) =>
        $"Como deseja excluir {DescribeTargets(paths)}?";

    private void RecycleBin_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = FileDeleteMode.RecycleBin;
        DialogResult = true;
    }

    private void Permanent_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = FileDeleteMode.Permanent;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
