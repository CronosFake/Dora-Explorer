using System.IO;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Services;

/// <summary>Textos da janela de progresso (separados da janela para poderem ser testados).</summary>
internal static class TransferProgressFormatter
{
    public static string Title(bool move, int itemCount, string destinationDirectory) =>
        $"{(move ? "Movendo" : "Copiando")} {ItemsText(itemCount)} para {FolderName(destinationDirectory)}";

    public static string CompressTitle(int itemCount, ArchiveFormat format, string destinationDirectory) =>
        $"Compactando {ItemsText(itemCount)} ({(format == ArchiveFormat.Zip ? "ZIP" : "TAR.GZ")}) em {FolderName(destinationDirectory)}";

    private static string ItemsText(int itemCount) => itemCount == 1 ? "1 item" : $"{itemCount} itens";

    private static string FolderName(string directory)
    {
        var name = Path.GetFileName(directory.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? directory : name;
    }

    /// <summary>"1,2 GB de 4,8 GB · 3 de 10 arquivos · 48 MB/s · restam 01:12".</summary>
    public static string Describe(FileOperationProgress progress, double bytesPerSecond)
    {
        if (!progress.IsTotalKnown)
        {
            return "Calculando…";
        }

        var parts = new List<string>();
        if (progress.BytesTotal > 0)
        {
            parts.Add($"{NavigationTreeNodeViewModel.FormatSize(progress.BytesDone)} de {NavigationTreeNodeViewModel.FormatSize(progress.BytesTotal)}");
            if (progress.FilesTotal > 1)
            {
                parts.Add($"{progress.FilesDone} de {progress.FilesTotal} arquivos");
            }
        }
        else
        {
            parts.Add($"{progress.FilesDone} de {progress.FilesTotal} arquivos");
        }

        if (bytesPerSecond >= 1024)
        {
            parts.Add($"{NavigationTreeNodeViewModel.FormatSize((long)bytesPerSecond)}/s");

            var remainingBytes = progress.BytesTotal - progress.BytesDone;
            if (remainingBytes > 0)
            {
                parts.Add("restam " + FormatDuration(TimeSpan.FromSeconds(remainingBytes / bytesPerSecond)));
            }
        }

        return string.Join(" · ", parts);
    }

    public static string FormatDuration(TimeSpan time)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(time.TotalSeconds));
        return rounded.TotalHours >= 1
            ? $"{(int)rounded.TotalHours}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes:00}:{rounded.Seconds:00}";
    }
}
