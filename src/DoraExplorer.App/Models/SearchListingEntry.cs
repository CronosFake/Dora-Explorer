namespace DoraExplorer.App.Models;

public readonly record struct SearchListingEntry(string Name, bool IsDirectory, long? Size);
