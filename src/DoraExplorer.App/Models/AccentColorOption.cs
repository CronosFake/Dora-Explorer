using System.Globalization;
using System.Windows.Media;

namespace DoraExplorer.App.Models;

public sealed record AccentColorOption(string Name, Color Color)
{
    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
}

public static class AccentColorPalette
{
    // Cor de destaque padrão do software.
    public static readonly AccentColorOption Default =
        new("Laranja", Color.FromRgb(0xFF, 0x77, 0x00));

    // 24 matizes equidistantes na roda cromática (15°), começando pelo azul.
    public static readonly IReadOnlyList<AccentColorOption> Options = new[]
    {
        new AccentColorOption("Azul", Color.FromRgb(0x29, 0x62, 0xFF)),
        new AccentColorOption("Cobalto", Color.FromRgb(0x30, 0x4F, 0xFE)),
        new AccentColorOption("Índigo", Color.FromRgb(0x53, 0x6D, 0xFE)),
        new AccentColorOption("Violeta", Color.FromRgb(0x7C, 0x4D, 0xFF)),
        new AccentColorOption("Roxo", Color.FromRgb(0xAA, 0x00, 0xFF)),
        new AccentColorOption("Magenta", Color.FromRgb(0xD5, 0x00, 0xF9)),
        new AccentColorOption("Fúcsia", Color.FromRgb(0xE9, 0x1E, 0x63)),
        new AccentColorOption("Rosa", Color.FromRgb(0xF5, 0x00, 0x57)),
        new AccentColorOption("Vermelho", Color.FromRgb(0xF4, 0x43, 0x36)),
        new AccentColorOption("Escarlate", Color.FromRgb(0xFF, 0x17, 0x44)),
        Default,
        new AccentColorOption("Tangerina", Color.FromRgb(0xFF, 0x91, 0x00)),
        new AccentColorOption("Âmbar", Color.FromRgb(0xFF, 0xAB, 0x00)),
        new AccentColorOption("Amarelo", Color.FromRgb(0xFF, 0xC4, 0x00)),
        new AccentColorOption("Lima", Color.FromRgb(0xC6, 0xF4, 0x00)),
        new AccentColorOption("Verde-limão", Color.FromRgb(0x76, 0xFF, 0x03)),
        new AccentColorOption("Verde", Color.FromRgb(0x00, 0xE6, 0x76)),
        new AccentColorOption("Esmeralda", Color.FromRgb(0x00, 0xC8, 0x53)),
        new AccentColorOption("Verde-água", Color.FromRgb(0x1D, 0xE9, 0xB6)),
        new AccentColorOption("Turquesa", Color.FromRgb(0x00, 0xBF, 0xA5)),
        new AccentColorOption("Ciano", Color.FromRgb(0x00, 0xE5, 0xFF)),
        new AccentColorOption("Céu", Color.FromRgb(0x00, 0xB0, 0xFF)),
        new AccentColorOption("Azul claro", Color.FromRgb(0x40, 0xC4, 0xFF)),
        new AccentColorOption("Safira", Color.FromRgb(0x29, 0x79, 0xFF)),
    };

    public static Color ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            var value = hex.TrimStart('#');
            if (value.Length == 6 &&
                int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return Color.FromRgb(
                    (byte)((rgb >> 16) & 0xFF),
                    (byte)((rgb >> 8) & 0xFF),
                    (byte)(rgb & 0xFF));
            }
        }

        return Default.Color;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
