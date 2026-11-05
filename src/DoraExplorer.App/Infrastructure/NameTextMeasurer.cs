using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DoraExplorer.App.Infrastructure;

/// <summary>Mede, em pixels, o texto do nome de uma linha da árvore (Segoe UI 13, como no modelo da linha).</summary>
public static class NameTextMeasurer
{
    private const double FontSize = 13;

    // folga: o desenho do texto passa um pouco da largura medida (arredondamento e sub-pixels)
    private const double Slack = 2;

    private const int CacheLimit = 4096;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Dictionary<string, double> Cache = new(StringComparer.Ordinal);

    /// <summary>Só na thread da interface (usa o cache sem trava).</summary>
    public static double Measure(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (Cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var pixelsPerDip = Application.Current?.MainWindow is { } window ? VisualTreeHelper.GetDpi(window).PixelsPerDip : 1.0;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Face,
            FontSize,
            Brushes.Black,
            pixelsPerDip);
        var width = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + Slack;

        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[text] = width;
        return width;
    }
}
