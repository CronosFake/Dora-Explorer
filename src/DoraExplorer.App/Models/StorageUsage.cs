namespace DoraExplorer.App.Models;

public enum StorageUsageLevel
{
    Normal,
    Warning,
    Critical
}

public static class StorageUsage
{
    /// <summary>A partir de 80% de uso a barra da unidade passa a âmbar.</summary>
    public const double WarningRatio = 0.80;

    /// <summary>A partir de 90% de uso a barra da unidade passa a vermelha.</summary>
    public const double CriticalRatio = 0.90;

    public static StorageUsageLevel Classify(double usedRatio) =>
        usedRatio >= CriticalRatio ? StorageUsageLevel.Critical
        : usedRatio >= WarningRatio ? StorageUsageLevel.Warning
        : StorageUsageLevel.Normal;
}
