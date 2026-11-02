using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class ExplorerViewSettingsService
{
    private readonly AppSettingsService _settingsService;

    public ExplorerViewSettingsService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public ExplorerViewOptions Options { get; private set; } = new();

    public event EventHandler? Changed;

    public event EventHandler? DefaultApplicationVisibilityChanged;

    public event EventHandler? RowDensityChanged;

    public void Load()
    {
        Options = _settingsService.LoadExplorerViewOptions();
    }

    public void SetShowHiddenFiles(bool value) => Update(o => o.ShowHiddenFiles = value, Options.ShowHiddenFiles, value);

    public void SetShowSystemFiles(bool value) => Update(o => o.ShowSystemFiles = value, Options.ShowSystemFiles, value);

    public void SetShowExternalDrives(bool value) => Update(o => o.ShowExternalDrives = value, Options.ShowExternalDrives, value);

    public void SetShowLockedDrives(bool value) => Update(o => o.ShowLockedDrives = value, Options.ShowLockedDrives, value);

    public void SetShowUnmappedPartitions(bool value) =>
        Update(o => o.ShowUnmappedPartitions = value, Options.ShowUnmappedPartitions, value);

    public void SetShowDefaultApplication(bool value)
    {
        if (Options.ShowDefaultApplication == value)
        {
            return;
        }

        Options.ShowDefaultApplication = value;
        _settingsService.SaveExplorerViewOptions(Options);
        DefaultApplicationVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCompactRows(bool value)
    {
        if (Options.CompactRows == value)
        {
            return;
        }

        Options.CompactRows = value;
        _settingsService.SaveExplorerViewOptions(Options);
        RowDensityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Update(Action<ExplorerViewOptions> apply, bool currentValue, bool newValue)
    {
        if (currentValue == newValue)
        {
            return;
        }

        apply(Options);
        _settingsService.SaveExplorerViewOptions(Options);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
