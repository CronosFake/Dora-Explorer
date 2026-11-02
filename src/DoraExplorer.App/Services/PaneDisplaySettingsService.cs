using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class PaneDisplaySettingsService
{
    private readonly AppSettingsService _settingsService;

    public PaneDisplaySettingsService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public PaneDisplayOptions LeftPane { get; private set; } = new();

    public PaneDisplayOptions RightPane { get; private set; } = new();

    public void Load()
    {
        LeftPane = _settingsService.LoadLeftPaneDisplayOptions();
        RightPane = _settingsService.LoadRightPaneDisplayOptions();
    }

    public void SaveLeftPane() => _settingsService.SaveLeftPaneDisplayOptions(LeftPane);

    public void SaveRightPane() => _settingsService.SaveRightPaneDisplayOptions(RightPane);
}
