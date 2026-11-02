namespace DoraExplorer.App.Models;

/// <summary>Andamento de uma cópia ou movimentação: bytes e arquivos feitos e o arquivo atual.</summary>
public readonly record struct FileOperationProgress(
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    string CurrentFile)
{
    /// <summary>Fração concluída (0 a 1); 0 enquanto o total ainda não é conhecido.</summary>
    public double Fraction =>
        BytesTotal > 0
            ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
            : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1) : 0;

    public bool IsTotalKnown => BytesTotal > 0 || FilesTotal > 0;
}
