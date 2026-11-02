namespace DoraExplorer.App.ViewModels;

public sealed class BreadcrumbSegmentViewModel
{
    public BreadcrumbSegmentViewModel(string name, string fullPath, bool isNavigable, bool isFirst, bool isCurrent)
    {
        Name = name;
        FullPath = fullPath;
        IsNavigable = isNavigable;
        IsFirst = isFirst;
        IsCurrent = isCurrent;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsNavigable { get; }

    public bool IsFirst { get; }

    public bool IsCurrent { get; }
}
