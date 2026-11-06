using System.Windows.Media;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Ícones do SMB (uma janela de quatro vidraças inclinada, a ideia do Windows) e do NFS (um pinguim, a ideia do Linux).
/// São desenhos próprios, simples, feitos numa grade de 16x16: nada copiado dos logotipos oficiais (as vidraças são
/// azuis, não as quatro cores da Microsoft, e o pinguim tem formas e proporções próprias). Cada um existe de dois
/// jeitos: <c>*Mark</c>, uma cor só, para os botões do menu central (que seguem o tema claro e escuro), e
/// <c>*Icon</c>, colorido, para a linha do protocolo na árvore. Tudo congelado: vale em qualquer thread.
/// </summary>
public static class NetworkProtocolIcons
{
    // Janela: quatro vidraças inclinadas para cima, à direita.
    private const string WindowTopLeft = "M1,2.3 L7.3,1.5 L7.3,7.3 L1,8.1 Z";
    private const string WindowTopRight = "M8.7,1.3 L15,0.5 L15,6.8 L8.7,7.5 Z";
    private const string WindowBottomLeft = "M1,9.5 L7.3,8.7 L7.3,14.5 L1,15.3 Z";
    private const string WindowBottomRight = "M8.7,8.9 L15,8.1 L15,13.9 L8.7,14.7 Z";

    // Pinguim: corpo, barriga, olhos, bico e pés.
    private const string PenguinBody =
        "M8,0.8 C5.7,0.8 4.6,2.6 4.6,4.5 C4.6,5.6 4.2,6.5 3.5,7.6 C2.5,9.2 2.1,10.9 2.7,12.3 C3.1,13.3 4,13.9 4.9,14.1 " +
        "L11.1,14.1 C12,13.9 12.9,13.3 13.3,12.3 C13.9,10.9 13.5,9.2 12.5,7.6 C11.8,6.5 11.4,5.6 11.4,4.5 C11.4,2.6 10.3,0.8 8,0.8 Z";

    private const string PenguinBelly =
        "M8,7.3 C6.5,7.3 5.5,8.9 5.5,10.6 C5.5,12.6 6.5,13.5 8,13.5 C9.5,13.5 10.5,12.6 10.5,10.6 C10.5,8.9 9.5,7.3 8,7.3 Z";

    // Dois círculos inteiros (centros em 6,7 e 9,3 na altura 2,9), cada um feito de dois arcos.
    private const string PenguinEyes =
        "M5.95,2.9 a0.75,0.75 0 1,0 1.5,0 a0.75,0.75 0 1,0 -1.5,0 Z " +
        "M8.55,2.9 a0.75,0.75 0 1,0 1.5,0 a0.75,0.75 0 1,0 -1.5,0 Z";

    private const string PenguinBeak = "M6.8,4.3 L9.2,4.3 L8,5.9 Z";

    private const string PenguinFeet =
        "M3.2,14.4 C3.2,13.9 4.6,13.8 5.8,14.1 C6.6,14.3 6.6,15.5 5.4,15.6 C4.2,15.7 3.2,15.2 3.2,14.4 Z " +
        "M12.8,14.4 C12.8,13.9 11.4,13.8 10.2,14.1 C9.4,14.3 9.4,15.5 10.6,15.6 C11.8,15.7 12.8,15.2 12.8,14.4 Z";

    private static readonly Lazy<Geometry> SmbMarkGeometry = new(() =>
        Frozen(Geometry.Parse(string.Join(' ', WindowTopLeft, WindowTopRight, WindowBottomLeft, WindowBottomRight))));

    // Uma cor só: a barriga, os olhos e o bico ficam vazados (preenchimento par-ímpar) e o fundo aparece por eles.
    private static readonly Lazy<Geometry> NfsMarkGeometry = new(() =>
        Frozen(Geometry.Parse("F0 " + string.Join(' ', PenguinBody, PenguinBelly, PenguinEyes, PenguinBeak, PenguinFeet))));

    private static readonly Lazy<ImageSource> SmbDrawing = new(() =>
        Drawing(
            (Brush("#57B8FF"), WindowTopLeft),
            (Brush("#2F9CFF"), WindowTopRight),
            (Brush("#2F9CFF"), WindowBottomLeft),
            (Brush("#1573E6"), WindowBottomRight)));

    private static readonly Lazy<ImageSource> NfsDrawing = new(() =>
        Drawing(
            (Brush("#2B2F36"), PenguinBody),
            (Brush("#F4F4F2"), PenguinBelly),
            (Brush("#F4F4F2"), PenguinEyes),
            (Brush("#F2A33A"), PenguinBeak),
            (Brush("#F2A33A"), PenguinFeet)));

    /// <summary>Janela do SMB numa cor só (botão do menu central).</summary>
    public static Geometry SmbMark => SmbMarkGeometry.Value;

    /// <summary>Pinguim do NFS numa cor só (botão do menu central).</summary>
    public static Geometry NfsMark => NfsMarkGeometry.Value;

    /// <summary>Janela do SMB colorida (árvore).</summary>
    public static ImageSource SmbIcon => SmbDrawing.Value;

    /// <summary>Pinguim do NFS colorido (árvore).</summary>
    public static ImageSource NfsIcon => NfsDrawing.Value;

    /// <summary>Ícone próprio do protocolo na árvore; nulo para os que usam o ícone de rede do Windows.</summary>
    public static ImageSource? TreeIconFor(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Smb => SmbIcon,
            NetworkProtocol.Nfs => NfsIcon,
            _ => null
        };

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static ImageSource Drawing(params (Brush Fill, string Data)[] shapes)
    {
        var group = new DrawingGroup();

        // Retângulo transparente: fixa o tamanho da imagem em 16x16 mesmo que o desenho não encoste nas bordas.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16))));
        foreach (var (fill, data) in shapes)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            group.Children.Add(new GeometryDrawing(fill, null, geometry));
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
