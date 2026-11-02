namespace DoraExplorer.App.Models;

public sealed class PaneDisplayOptions
{
    public bool ShowFavorites { get; set; } = true;

    public bool ShowRecycleBin { get; set; } = true;

    public bool ShowFtp { get; set; } = true;

    public bool ShowSftp { get; set; } = true;

    public bool ShowScp { get; set; } = true;

    public bool ShowTftp { get; set; } = true;
}
