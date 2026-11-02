namespace DoraExplorer.App.Models;

public sealed class ExplorerViewOptions
{
    public bool ShowHiddenFiles { get; set; }

    public bool ShowSystemFiles { get; set; }

    public bool ShowExternalDrives { get; set; } = true;

    public bool ShowUnmappedPartitions { get; set; }

    public bool ShowDefaultApplication { get; set; } = true;

    /// <summary>Linhas compactas em colunas (Nome, Tipo, Modificado, Tamanho) em vez de duas linhas por item.</summary>
    public bool CompactRows { get; set; } = true;
}
