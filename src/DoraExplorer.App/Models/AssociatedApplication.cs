using System.Windows.Media;

namespace DoraExplorer.App.Models;

public sealed record AssociatedApplication(
    string DisplayName,
    string? ExecutablePath,
    bool IsDefault,
    bool IsRecommended = false,
    ImageSource? Icon = null);
