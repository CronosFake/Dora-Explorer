using Microsoft.Win32;

namespace DoraExplorer.App.Services;

public static class WindowsThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    public static bool IsWindowsAppsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        var value = key?.GetValue(AppsUseLightThemeValueName);

        return value switch
        {
            int intValue => intValue > 0,
            _ => true
        };
    }
}
