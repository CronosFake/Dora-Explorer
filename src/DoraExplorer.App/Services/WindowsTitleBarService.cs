using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DoraExplorer.App.Services;

public static class WindowsTitleBarService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window, bool isDarkTheme)
    {
        if (window.IsLoaded || PresentationSource.FromVisual(window) is not null)
        {
            ApplyInternal(window, isDarkTheme);
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyInternal(window, isDarkTheme);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;
            ApplyInternal(window, isDarkTheme);
        }

        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
    }

    public static void ApplyToAllWindows(bool isDarkTheme)
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Apply(window, isDarkTheme);
        }
    }

    private static void ApplyInternal(Window window, bool isDarkTheme)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var immersiveDarkMode = isDarkTheme ? 1 : 0;
        SetAttribute(handle, DwmwaUseImmersiveDarkMode, immersiveDarkMode);
        SetAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, immersiveDarkMode);

        if (isDarkTheme)
        {
            SetAttribute(handle, DwmwaCaptionColor, ToColorRef(Color.FromRgb(32, 32, 32)));
            SetAttribute(handle, DwmwaTextColor, ToColorRef(Color.FromRgb(232, 232, 232)));
            SetAttribute(handle, DwmwaBorderColor, ToColorRef(Color.FromRgb(59, 59, 59)));
        }
        else
        {
            SetAttribute(handle, DwmwaCaptionColor, ToColorRef(Color.FromRgb(243, 243, 243)));
            SetAttribute(handle, DwmwaTextColor, ToColorRef(Color.FromRgb(26, 26, 26)));
            SetAttribute(handle, DwmwaBorderColor, ToColorRef(Color.FromRgb(229, 229, 229)));
        }
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
