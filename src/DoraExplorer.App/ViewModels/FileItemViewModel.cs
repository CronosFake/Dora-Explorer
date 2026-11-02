using CommunityToolkit.Mvvm.ComponentModel;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.ViewModels;

public partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(FileSystemEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        IsParentLink = entry.IsParentLink;
        SizeInBytes = entry.SizeInBytes;
        LastModified = entry.LastModified;
        Kind = entry.IsParentLink
            ? string.Empty
            : entry.IsDirectory
                ? "Pasta"
                : Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(Kind) && !entry.IsDirectory && !entry.IsParentLink)
        {
            Kind = "Arquivo";
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsParentLink { get; }

    public long? SizeInBytes { get; }

    public DateTime? LastModified { get; }

    public string Kind { get; }

    public string IconGlyph => IsParentLink
        ? "\uE74A"
        : IsDirectory
            ? "\uE8B7"
            : "\uE8A5";

    public string DetailText => IsDirectory
        ? Kind
        : SizeInBytes.HasValue
            ? $"{FormatSize(SizeInBytes.Value)} • {FormatDate(LastModified)}"
            : FormatDate(LastModified);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.##} {units[unit]}";
    }

    private static string FormatDate(DateTime? date) =>
        date?.ToString("dd/MM/yyyy HH:mm") ?? string.Empty;
}
