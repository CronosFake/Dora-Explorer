using System.Windows.Media;
using Microsoft.Win32;

namespace DoraExplorer.App.Services;

public static class WindowsAccentColorService
{
    private const string DwmKeyPath = @"Software\Microsoft\Windows\DWM";
    private const string ColorizationColorValueName = "ColorizationColor";
    private static readonly Color DefaultAccent = Color.FromRgb(0, 120, 212);

    public static Color GetAccentColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DwmKeyPath);
        var value = key?.GetValue(ColorizationColorValueName);

        if (value is int colorization)
        {
            return Color.FromRgb(
                (byte)(colorization & 0xFF),
                (byte)((colorization >> 8) & 0xFF),
                (byte)((colorization >> 16) & 0xFF));
        }

        return DefaultAccent;
    }
}
