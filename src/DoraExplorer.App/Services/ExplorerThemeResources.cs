using System.Windows;
using System.Windows.Media;

namespace DoraExplorer.App.Services;

public static class ExplorerThemeResources
{
    public static ResourceDictionary Create(bool isDark, Color accent)
    {
        var colors = isDark ? CreateDarkColors(accent) : CreateLightColors(accent);
        return CreateDictionary(colors);
    }

    private static ExplorerColors CreateLightColors(Color accent)
    {
        var panelBackground = Color.FromRgb(255, 255, 255);
        var windowBackground = Color.FromRgb(243, 243, 243);

        var textForeground = Color.FromRgb(0x1A, 0x1A, 0x1A);

        return new ExplorerColors(
            WindowBackground: windowBackground,
            PanelBackground: panelBackground,
            Border: Color.FromRgb(229, 229, 229),
            Accent: accent,
            MenuBackground: windowBackground,
            MenuForeground: textForeground,
            MenuHoverBackground: Color.FromRgb(229, 229, 229),
            PathBarBackground: panelBackground,
            CenterToolbarBackground: windowBackground,
            CenterToolbarForeground: textForeground,
            CenterToolbarButtonBackground: Colors.Transparent,
            CenterToolbarButtonIconForeground: textForeground,
            CenterToolbarButtonHoverBackground: Color.FromRgb(245, 245, 245),
            Foreground: textForeground,
            ListItemSelectedBackground: Blend(accent, panelBackground, 0.18),
            ListItemHoverBackground: Color.FromRgb(245, 245, 245),
            ListItemSelectedForeground: textForeground,
            ButtonHoverBackground: Color.FromRgb(245, 245, 245),
            InputBackground: panelBackground,
            GridViewHeaderBackground: Color.FromRgb(249, 249, 249),
            GridViewHeaderForeground: textForeground,
            ScrollBarThumb: WithAlpha(accent, 170),
            ScrollBarThumbHover: WithAlpha(accent, 210),
            ScrollBarThumbPressed: WithAlpha(accent, 255),
            TreeFocusBorder: accent,
            TreeHierarchyLine: WithAlpha(accent, 255),
            TreeItemSelectedBorder: WithAlpha(accent, SelectionMutedAlpha),
            TreeItemSelectedMutedBorder: WithAlpha(accent, SelectionMutedAlpha),
            TreeItemSelectedMutedBackground: WithAlpha(Lighten(accent, 0.85), SelectionMutedAlpha),
            TreeItemSelectedBackground: Lighten(accent, 0.85),
            TreeItemSeparator: Color.FromArgb(48, 100, 100, 100),
            TreeItemMetaForeground: Color.FromArgb(175, 90, 90, 90),
            UsageNormal: accent,
            UsageWarning: Color.FromRgb(0xD9, 0x82, 0x00),
            UsageCritical: Color.FromRgb(0xD1, 0x34, 0x38),
            UsageTrack: Color.FromArgb(48, 100, 100, 100));
    }

    private static ExplorerColors CreateDarkColors(Color accent)
    {
        // Painéis um degrau mais claros que a janela: cada painel lê como um cartão sobre o fundo.
        var panelBackground = Color.FromRgb(40, 40, 40);
        var windowBackground = Color.FromRgb(32, 32, 32);

        var textForeground = Color.FromRgb(0xE8, 0xE8, 0xE8);

        // No tema escuro a cor é ligeiramente clareada para contrastar com o fundo.
        var accentBright = Lighten(accent, 0.12);
        var treeItemSelectedBackground = Blend(accent, panelBackground, 0.18);

        return new ExplorerColors(
            WindowBackground: windowBackground,
            PanelBackground: panelBackground,
            Border: Color.FromRgb(59, 59, 59),
            Accent: accent,
            MenuBackground: windowBackground,
            MenuForeground: textForeground,
            MenuHoverBackground: Color.FromRgb(58, 58, 58),
            PathBarBackground: Color.FromRgb(34, 34, 34),
            CenterToolbarBackground: Color.FromRgb(37, 37, 37),
            CenterToolbarForeground: textForeground,
            CenterToolbarButtonBackground: Colors.Transparent,
            CenterToolbarButtonIconForeground: textForeground,
            CenterToolbarButtonHoverBackground: Color.FromRgb(58, 58, 58),
            Foreground: textForeground,
            ListItemSelectedBackground: Blend(accent, panelBackground, 0.55),
            ListItemHoverBackground: Color.FromRgb(58, 58, 58),
            ListItemSelectedForeground: textForeground,
            ButtonHoverBackground: Color.FromRgb(58, 58, 58),
            InputBackground: Color.FromRgb(45, 45, 45),
            GridViewHeaderBackground: Color.FromRgb(45, 45, 45),
            GridViewHeaderForeground: textForeground,
            ScrollBarThumb: WithAlpha(accentBright, 170),
            ScrollBarThumbHover: WithAlpha(accentBright, 210),
            ScrollBarThumbPressed: WithAlpha(accentBright, 255),
            TreeFocusBorder: accent,
            TreeHierarchyLine: WithAlpha(accentBright, 255),
            TreeItemSelectedBorder: WithAlpha(accentBright, SelectionMutedAlpha),
            TreeItemSelectedMutedBorder: WithAlpha(accent, SelectionMutedAlpha),
            TreeItemSelectedMutedBackground: WithAlpha(treeItemSelectedBackground, SelectionMutedAlpha),
            TreeItemSelectedBackground: treeItemSelectedBackground,
            TreeItemSeparator: Color.FromArgb(56, 180, 180, 180),
            TreeItemMetaForeground: Color.FromArgb(168, 255, 255, 255),
            UsageNormal: accentBright,
            UsageWarning: Color.FromRgb(0xF5, 0xA5, 0x24),
            UsageCritical: Color.FromRgb(0xF0, 0x5A, 0x5A),
            UsageTrack: Color.FromArgb(56, 180, 180, 180));
    }

    private static ResourceDictionary CreateDictionary(ExplorerColors colors)
    {
        return new ResourceDictionary
        {
            ["WindowBackgroundColor"] = colors.WindowBackground,
            ["PanelBackgroundColor"] = colors.PanelBackground,
            ["BorderColor"] = colors.Border,
            ["AccentColor"] = colors.Accent,
            ["MenuBackgroundColor"] = colors.MenuBackground,
            ["MenuForegroundColor"] = colors.MenuForeground,
            ["MenuHoverBackgroundColor"] = colors.MenuHoverBackground,
            ["PathBarBackgroundColor"] = colors.PathBarBackground,
            ["CenterToolbarBackgroundColor"] = colors.CenterToolbarBackground,
            ["CenterToolbarForegroundColor"] = colors.CenterToolbarForeground,
            ["CenterToolbarButtonBackgroundColor"] = colors.CenterToolbarButtonBackground,
            ["CenterToolbarButtonIconForegroundColor"] = colors.CenterToolbarButtonIconForeground,
            ["CenterToolbarButtonHoverBackgroundColor"] = colors.CenterToolbarButtonHoverBackground,
            ["ForegroundColor"] = colors.Foreground,
            ["ListItemSelectedBackgroundColor"] = colors.ListItemSelectedBackground,
            ["ListItemHoverBackgroundColor"] = colors.ListItemHoverBackground,
            ["ListItemSelectedForegroundColor"] = colors.ListItemSelectedForeground,
            ["ButtonHoverBackgroundColor"] = colors.ButtonHoverBackground,
            ["InputBackgroundColor"] = colors.InputBackground,
            ["GridViewHeaderBackgroundColor"] = colors.GridViewHeaderBackground,
            ["GridViewHeaderForegroundColor"] = colors.GridViewHeaderForeground,
            ["ScrollBarThumbColor"] = colors.ScrollBarThumb,
            ["ScrollBarThumbHoverColor"] = colors.ScrollBarThumbHover,
            ["ScrollBarThumbPressedColor"] = colors.ScrollBarThumbPressed,
            ["TreeFocusBorderColor"] = colors.TreeFocusBorder,
            ["TreeHierarchyLineColor"] = colors.TreeHierarchyLine,
            ["TreeItemSelectedBorderColor"] = colors.TreeItemSelectedBorder,
            ["TreeItemSelectedMutedBorderColor"] = colors.TreeItemSelectedMutedBorder,
            ["TreeItemSelectedMutedBackgroundColor"] = colors.TreeItemSelectedMutedBackground,
            ["TreeItemSelectedBackgroundColor"] = colors.TreeItemSelectedBackground,
            ["TreeItemSeparatorColor"] = colors.TreeItemSeparator,
            ["TreeItemMetaForegroundColor"] = colors.TreeItemMetaForeground,
            ["UsageNormalColor"] = colors.UsageNormal,
            ["UsageWarningColor"] = colors.UsageWarning,
            ["UsageCriticalColor"] = colors.UsageCritical,
            ["UsageTrackColor"] = colors.UsageTrack,

            ["WindowBackgroundBrush"] = CreateBrush(colors.WindowBackground),
            ["PanelBackgroundBrush"] = CreateBrush(colors.PanelBackground),
            ["BorderBrushColor"] = CreateBrush(colors.Border),
            ["AccentBrush"] = CreateBrush(colors.Accent),
            ["MenuBackgroundBrush"] = CreateBrush(colors.MenuBackground),
            ["MenuForegroundBrush"] = CreateBrush(colors.MenuForeground),
            ["MenuHoverBackgroundBrush"] = CreateBrush(colors.MenuHoverBackground),
            ["PathBarBackgroundBrush"] = CreateBrush(colors.PathBarBackground),
            ["CenterToolbarBackgroundBrush"] = CreateBrush(colors.CenterToolbarBackground),
            ["CenterToolbarForegroundBrush"] = CreateBrush(colors.CenterToolbarForeground),
            ["CenterToolbarButtonBackgroundBrush"] = CreateBrush(colors.CenterToolbarButtonBackground),
            ["CenterToolbarButtonIconForegroundBrush"] = CreateBrush(colors.CenterToolbarButtonIconForeground),
            ["CenterToolbarButtonHoverBackgroundBrush"] = CreateBrush(colors.CenterToolbarButtonHoverBackground),
            ["ForegroundBrush"] = CreateBrush(colors.Foreground),
            ["ListItemSelectedBackgroundBrush"] = CreateBrush(colors.ListItemSelectedBackground),
            ["ListItemHoverBackgroundBrush"] = CreateBrush(colors.ListItemHoverBackground),
            ["ListItemSelectedForegroundBrush"] = CreateBrush(colors.ListItemSelectedForeground),
            ["ButtonHoverBackgroundBrush"] = CreateBrush(colors.ButtonHoverBackground),
            ["InputBackgroundBrush"] = CreateBrush(colors.InputBackground),
            ["GridViewHeaderBackgroundBrush"] = CreateBrush(colors.GridViewHeaderBackground),
            ["GridViewHeaderForegroundBrush"] = CreateBrush(colors.GridViewHeaderForeground),
            ["ScrollBarThumbBrush"] = CreateBrush(colors.ScrollBarThumb),
            ["ScrollBarThumbHoverBrush"] = CreateBrush(colors.ScrollBarThumbHover),
            ["ScrollBarThumbPressedBrush"] = CreateBrush(colors.ScrollBarThumbPressed),
            ["TreeFocusBorderBrush"] = CreateBrush(colors.TreeFocusBorder),
            ["TreeHierarchyLineBrush"] = CreateBrush(colors.TreeHierarchyLine),
            ["TreeItemSelectedBorderBrush"] = CreateBrush(colors.TreeItemSelectedBorder),
            ["TreeItemSelectedMutedBorderBrush"] = CreateBrush(colors.TreeItemSelectedMutedBorder),
            ["TreeItemSelectedMutedBackgroundBrush"] = CreateBrush(colors.TreeItemSelectedMutedBackground),
            ["TreeItemSelectedBackgroundBrush"] = CreateBrush(colors.TreeItemSelectedBackground),
            ["TreeItemSeparatorBrush"] = CreateBrush(colors.TreeItemSeparator),
            ["TreeItemMetaForegroundBrush"] = CreateBrush(colors.TreeItemMetaForeground),
            ["UsageNormalBrush"] = CreateBrush(colors.UsageNormal),
            ["UsageWarningBrush"] = CreateBrush(colors.UsageWarning),
            ["UsageCriticalBrush"] = CreateBrush(colors.UsageCritical),
            ["UsageTrackBrush"] = CreateBrush(colors.UsageTrack)
        };
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const byte SelectionMutedAlpha = 128; // 50% — mesma aparência do item focado, semi-transparente

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Lighten(Color color, double amount) =>
        Blend(Color.FromRgb(255, 255, 255), color, amount);

    private static Color Blend(Color foreground, Color background, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        return Color.FromRgb(
            (byte)(foreground.R * amount + background.R * (1 - amount)),
            (byte)(foreground.G * amount + background.G * (1 - amount)),
            (byte)(foreground.B * amount + background.B * (1 - amount)));
    }

    private readonly record struct ExplorerColors(
        Color WindowBackground,
        Color PanelBackground,
        Color Border,
        Color Accent,
        Color MenuBackground,
        Color MenuForeground,
        Color MenuHoverBackground,
        Color PathBarBackground,
        Color CenterToolbarBackground,
        Color CenterToolbarForeground,
        Color CenterToolbarButtonBackground,
        Color CenterToolbarButtonIconForeground,
        Color CenterToolbarButtonHoverBackground,
        Color Foreground,
        Color ListItemSelectedBackground,
        Color ListItemHoverBackground,
        Color ListItemSelectedForeground,
        Color ButtonHoverBackground,
        Color InputBackground,
        Color GridViewHeaderBackground,
        Color GridViewHeaderForeground,
        Color ScrollBarThumb,
        Color ScrollBarThumbHover,
        Color ScrollBarThumbPressed,
        Color TreeFocusBorder,
        Color TreeHierarchyLine,
        Color TreeItemSelectedBorder,
        Color TreeItemSelectedMutedBorder,
        Color TreeItemSelectedMutedBackground,
        Color TreeItemSelectedBackground,
        Color TreeItemSeparator,
        Color TreeItemMetaForeground,
        Color UsageNormal,
        Color UsageWarning,
        Color UsageCritical,
        Color UsageTrack);
}
