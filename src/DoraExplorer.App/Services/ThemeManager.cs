using System.Windows;
using System.Windows.Media;
using DoraExplorer.App.Models;
using Microsoft.Win32;

namespace DoraExplorer.App.Services;

public sealed class ThemeManager : IDisposable
{
    private readonly AppSettingsService _settingsService;
    private ResourceDictionary? _colorThemeDictionary;
    private bool _isWatchingSystemTheme;

    public ThemeManager(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public AppThemeMode ThemeMode { get; private set; } = AppThemeMode.System;

    public Color AccentColor { get; private set; } = AccentColorPalette.Default.Color;

    public bool IsDarkTheme { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        ThemeMode = _settingsService.LoadThemeMode();
        AccentColor = AccentColorPalette.ParseColor(_settingsService.LoadAccentColorHex());
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (ThemeMode == AppThemeMode.System)
        {
            _isWatchingSystemTheme = true;
        }
    }

    public void SetThemeMode(AppThemeMode themeMode)
    {
        if (ThemeMode == themeMode)
        {
            return;
        }

        ThemeMode = themeMode;
        _settingsService.SaveThemeMode(themeMode);

        if (themeMode == AppThemeMode.System)
        {
            _isWatchingSystemTheme = true;
        }
        else
        {
            _isWatchingSystemTheme = false;
        }

        ApplyTheme();
    }

    public void SetAccentColor(Color accentColor)
    {
        if (AccentColor == accentColor)
        {
            return;
        }

        AccentColor = accentColor;
        _settingsService.SaveAccentColorHex(AccentColorPalette.ToHex(accentColor));
        ApplyTheme();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    public void ApplyTitleBar(Window window)
    {
        WindowsTitleBarService.Apply(window, IsDarkTheme);
    }

    private void ApplyTheme()
    {
        var useDarkTheme = ThemeMode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            AppThemeMode.System => !WindowsThemeService.IsWindowsAppsLightTheme(),
            _ => false
        };

        var newThemeDictionary = ExplorerThemeResources.Create(useDarkTheme, AccentColor);
        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

        if (_colorThemeDictionary is not null)
        {
            mergedDictionaries.Remove(_colorThemeDictionary);
        }

        _colorThemeDictionary = newThemeDictionary;
        mergedDictionaries.Add(_colorThemeDictionary);

        IsDarkTheme = useDarkTheme;
        WindowsTitleBarService.ApplyToAllWindows(useDarkTheme);

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var shouldApply = e.Category switch
        {
            UserPreferenceCategory.Color => true,
            UserPreferenceCategory.General => _isWatchingSystemTheme,
            _ => false
        };

        // Este evento vem de uma thread do sistema: Invoke (síncrono) pode travar com a UI ocupada
        // e Application.Current é nulo durante o encerramento.
        if (shouldApply && Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(ApplyTheme);
        }
    }
}
