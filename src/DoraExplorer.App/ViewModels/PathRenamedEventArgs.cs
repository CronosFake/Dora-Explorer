namespace DoraExplorer.App.ViewModels;

/// <summary>Aviso de que um item foi renomeado: o caminho novo dele.</summary>
public sealed class PathRenamedEventArgs(string newPath) : EventArgs
{
    public string NewPath { get; } = newPath;
}
