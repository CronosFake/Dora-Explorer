namespace DoraExplorer.App.Services;

public sealed class RememberPathSettingsService
{
    private readonly AppSettingsService _settingsService;

    public RememberPathSettingsService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool IsEnabled { get; private set; }

    public string? LeftPanePath { get; private set; }

    public string? RightPanePath { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        var settings = _settingsService.LoadRememberPathSettings();
        IsEnabled = settings.IsEnabled;
        LeftPanePath = settings.LeftPanePath;
        RightPanePath = settings.RightPanePath;
    }

    public void SetEnabled(bool value)
    {
        if (IsEnabled == value)
        {
            return;
        }

        IsEnabled = value;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SavePanePaths(string leftPath, string rightPath)
    {
        if (!IsEnabled)
        {
            return;
        }

        LeftPanePath = leftPath;
        RightPanePath = rightPath;
        Persist();
    }

    private void Persist() =>
        _settingsService.SaveRememberPathSettings(IsEnabled, LeftPanePath, RightPanePath);
}
