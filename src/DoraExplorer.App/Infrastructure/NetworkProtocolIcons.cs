using System.Windows.Media;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Ícones dos protocolos de rede na árvore: o SMB (uma janela de quatro vidraças inclinada, a ideia do Windows), o NFS (um
/// pinguim, a ideia do Linux), o FTP (pasta com setas de ida e volta), o SFTP (a pasta com um cadeado), o SCP (um terminal)
/// e o TFTP (duas setas em anel, o mesmo sentido do botão do menu central). São desenhos próprios, simples, feitos numa
/// grade de 16x16: nada copiado dos logotipos oficiais (as vidraças são azuis, não as quatro cores da Microsoft, e o
/// pinguim tem formas e proporções próprias). Cada protocolo existe de dois jeitos: <c>*Icon</c>, colorido, para a linha
/// da árvore (cores e bordas que se leem nos dois temas), e <c>*Mark</c>, só o **contorno** (linhas, sem preenchimento),
/// para os botões do menu central: o botão desenha o contorno com traço fino na cor do tema, como os outros ícones
/// daquele menu. Tudo congelado: vale em qualquer thread.
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

    // Pasta (FTP e SFTP): aba e fundo mais escuros atrás, frente mais clara.
    private const string FolderBack = "M1,2.8 L6.1,2.8 L7.6,4.4 L15,4.4 L15,13.6 L1,13.6 Z";
    private const string FolderFront = "M1,5.8 L15,5.8 L15,13.6 L1,13.6 Z";
    private const string ArrowUp = "M5.6,6.4 L8,8.9 L6.35,8.9 L6.35,12.8 L4.85,12.8 L4.85,8.9 L3.2,8.9 Z";
    private const string ArrowDown = "M10.4,12.8 L8,10.3 L9.65,10.3 L9.65,6.4 L11.15,6.4 L11.15,10.3 L12.8,10.3 Z";
    private const string ArrowUpLeft = "M5.2,6.6 L7.6,9.4 L6.0,9.4 L6.0,12.8 L4.4,12.8 L4.4,9.4 L2.8,9.4 Z";

    // Cadeado (SFTP): corpo, argola e buraco da fechadura.
    private const string LockBody =
        "M8.9,9 H13.9 Q14.5,9 14.5,9.6 V14.4 Q14.5,15 13.9,15 H8.9 Q8.3,15 8.3,14.4 V9.6 Q8.3,9 8.9,9 Z";

    private const string LockShackle = "M9.8,9 V7.9 C9.8,6.3 13,6.3 13,7.9 V9";

    private const string LockKeyhole =
        "M10.6,11.4 a0.8,0.8 0 1,0 1.6,0 a0.8,0.8 0 1,0 -1.6,0 Z M11,11.9 H11.8 L12.1,13.7 H10.7 Z";

    // Terminal (SCP): janela, barra de título, o ">" e o cursor.
    private const string TerminalWindow =
        "M1.5,2.5 H14.5 Q15,2.5 15,3 V13 Q15,13.5 14.5,13.5 H1.5 Q1,13.5 1,13 V3 Q1,2.5 1.5,2.5 Z";

    private const string TerminalTitleBar = "M1.5,2.5 H14.5 Q15,2.5 15,3 V4.9 H1 V3 Q1,2.5 1.5,2.5 Z";
    private const string TerminalPrompt = "M3.6,6.8 L6.4,8.8 L3.6,10.8";
    private const string TerminalCursor = "M8,11.4 H11.8";

    // Setas em anel (TFTP): a de cima e a de baixo, esta igual à primeira girada 180 graus.
    private const string SyncTopArc = "M3.4,7.6 C3.6,5.1 5.7,3.3 8.1,3.3 C9.5,3.3 10.7,3.9 11.6,4.8";
    private const string SyncTopHead = "M13.7,2.6 L13.4,6.5 L9.6,5.3 Z";
    private const string SyncBottomArc = "M12.6,8.4 C12.4,10.9 10.3,12.7 7.9,12.7 C6.5,12.7 5.3,12.1 4.4,11.2";
    private const string SyncBottomHead = "M2.3,13.4 L2.6,9.5 L6.4,10.7 Z";

    // ---- Contornos (botões do menu central): só linhas, desenhadas com traço de 1 na grade de 16x16 ----------------
    // Ficam a pelo menos 0,5 das bordas para o traço não ser cortado.

    private const string FolderOutline = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z";

    private const string FtpOutlineArrows =
        "M5.2,11.6 V7.4 M3.5,9.1 L5.2,7.4 L6.9,9.1 M10.8,7.4 V11.6 M9.1,9.9 L10.8,11.6 L12.5,9.9";

    private const string SftpOutlineArrow = "M4.2,11.6 V7.6 M2.7,9.1 L4.2,7.6 L5.7,9.1";

    private const string SftpOutlineLock =
        "M9.5,8.7 V7.9 C9.5,6.4 12.1,6.4 12.1,7.9 V8.7 M8.8,8.7 H12.8 V11.9 H8.8 Z";

    private const string ScpOutline =
        "M2.5,2.5 H13.5 Q15,2.5 15,4 V12 Q15,13.5 13.5,13.5 H2.5 Q1,13.5 1,12 V4 Q1,2.5 2.5,2.5 Z " +
        "M1,5.3 H15 M3.8,7.6 L6,9.2 L3.8,10.8 M8,11 H11.6";

    // Duas setas em anel com a ponta aberta (um "L"): a de cima e a de baixo, esta a de cima girada 180 graus.
    private const string TftpOutline =
        "M3.4,7.6 C3.6,5.1 5.7,3.3 8.1,3.3 C9.5,3.3 10.7,3.9 11.9,5.1 M13.4,2.8 V5.9 H10.3 " +
        "M12.6,8.4 C12.4,10.9 10.3,12.7 7.9,12.7 C6.5,12.7 5.3,12.1 4.1,10.9 M2.6,13.2 V10.1 H5.7";

    private const string SmbOutline =
        "M1.5,2.9 L6.8,2.2 L6.8,6.9 L1.5,7.6 Z M9.2,1.9 L14.5,1.2 L14.5,6.2 L9.2,6.9 Z " +
        "M1.5,9.4 L6.8,8.7 L6.8,13.4 L1.5,14.1 Z M9.2,8.9 L14.5,8.2 L14.5,13 L9.2,13.7 Z";

    // Pinguim: o corpo e a barriga do desenho colorido, olhos como pontos, o bico e os pés como dois traços.
    private const string NfsOutlineDetails =
        "M6.3,2.9 a0.4,0.4 0 1,0 0.8,0 a0.4,0.4 0 1,0 -0.8,0 M8.9,2.9 a0.4,0.4 0 1,0 0.8,0 a0.4,0.4 0 1,0 -0.8,0 " +
        "M3.8,15.3 H6.2 M9.8,15.3 H12.2";

    private static Lazy<Geometry> Outline(params string[] parts) => new(() => Frozen(Geometry.Parse(string.Join(' ', parts))));

    private static readonly Lazy<Geometry> FtpMarkGeometry = Outline(FolderOutline, FtpOutlineArrows);

    private static readonly Lazy<Geometry> SftpMarkGeometry = Outline(FolderOutline, SftpOutlineArrow, SftpOutlineLock);

    private static readonly Lazy<Geometry> ScpMarkGeometry = Outline(ScpOutline);

    private static readonly Lazy<Geometry> TftpMarkGeometry = Outline(TftpOutline);

    private static readonly Lazy<Geometry> SmbMarkGeometry = Outline(SmbOutline);

    private static readonly Lazy<Geometry> NfsMarkGeometry = Outline(PenguinBody, PenguinBelly, PenguinBeak, NfsOutlineDetails);

    private static readonly Lazy<ImageSource> SmbDrawing = new(() =>
        Drawing(
            Shape.Filled("#57B8FF", WindowTopLeft),
            Shape.Filled("#2F9CFF", WindowTopRight),
            Shape.Filled("#2F9CFF", WindowBottomLeft),
            Shape.Filled("#1573E6", WindowBottomRight)));

    // O corpo tem uma borda clara: sem ela o pinguim escuro some no tema escuro.
    private static readonly Lazy<ImageSource> NfsDrawing = new(() =>
        Drawing(
            Shape.Filled("#3A4152", PenguinBody, rimColor: "#9AA3B2", rimWidth: 0.55),
            Shape.Filled("#F4F4F2", PenguinBelly),
            Shape.Filled("#F4F4F2", PenguinEyes),
            Shape.Filled("#F2A33A", PenguinBeak),
            Shape.Filled("#F2A33A", PenguinFeet)));

    private static readonly Lazy<ImageSource> FtpDrawing = new(() =>
        Drawing(
            Shape.Filled("#D9962B", FolderBack),
            Shape.Filled("#F5C04F", FolderFront),
            Shape.Filled("#0F8F7C", ArrowUp),
            Shape.Filled("#2563EB", ArrowDown)));

    private static readonly Lazy<ImageSource> SftpDrawing = new(() =>
        Drawing(
            Shape.Filled("#D9962B", FolderBack),
            Shape.Filled("#F5C04F", FolderFront),
            Shape.Filled("#0F8F7C", ArrowUpLeft),
            Shape.Stroked("#1B6B34", 1.2, LockShackle),
            Shape.Filled("#2FA84F", LockBody, rimColor: "#1B6B34", rimWidth: 0.5),
            Shape.Filled("#F4F4F2", LockKeyhole)));

    private static readonly Lazy<ImageSource> ScpDrawing = new(() =>
        Drawing(
            Shape.Filled("#4A5266", TerminalWindow, rimColor: "#9AA3B2", rimWidth: 0.5),
            Shape.Filled("#6B7589", TerminalTitleBar),
            Shape.Stroked("#5BE08A", 1.3, TerminalPrompt),
            Shape.Stroked("#E6E9EF", 1.3, TerminalCursor)));

    private static readonly Lazy<ImageSource> TftpDrawing = new(() =>
        Drawing(
            Shape.Stroked("#F0892A", 1.7, SyncTopArc),
            Shape.Filled("#F0892A", SyncTopHead),
            Shape.Stroked("#2563EB", 1.7, SyncBottomArc),
            Shape.Filled("#2563EB", SyncBottomHead)));

    /// <summary>Contorno da pasta com setas de ida e volta (FTP), para o botão do menu central.</summary>
    public static Geometry FtpMark => FtpMarkGeometry.Value;

    /// <summary>Contorno da pasta com cadeado (SFTP), para o botão do menu central.</summary>
    public static Geometry SftpMark => SftpMarkGeometry.Value;

    /// <summary>Contorno do terminal (SCP), para o botão do menu central.</summary>
    public static Geometry ScpMark => ScpMarkGeometry.Value;

    /// <summary>Contorno das duas setas em anel (TFTP), para o botão do menu central.</summary>
    public static Geometry TftpMark => TftpMarkGeometry.Value;

    /// <summary>Contorno da janela do SMB, para o botão do menu central.</summary>
    public static Geometry SmbMark => SmbMarkGeometry.Value;

    /// <summary>Contorno do pinguim do NFS, para o botão do menu central.</summary>
    public static Geometry NfsMark => NfsMarkGeometry.Value;

    /// <summary>Janela do SMB colorida (árvore).</summary>
    public static ImageSource SmbIcon => SmbDrawing.Value;

    /// <summary>Pinguim do NFS colorido (árvore).</summary>
    public static ImageSource NfsIcon => NfsDrawing.Value;

    /// <summary>Pasta com setas de ida e volta (FTP).</summary>
    public static ImageSource FtpIcon => FtpDrawing.Value;

    /// <summary>Pasta com cadeado (SFTP).</summary>
    public static ImageSource SftpIcon => SftpDrawing.Value;

    /// <summary>Terminal (SCP).</summary>
    public static ImageSource ScpIcon => ScpDrawing.Value;

    /// <summary>Duas setas em anel (TFTP).</summary>
    public static ImageSource TftpIcon => TftpDrawing.Value;

    /// <summary>Ícone do protocolo na árvore; nulo só para um protocolo que ainda não tenha desenho (cai no ícone de rede do Windows).</summary>
    public static ImageSource? TreeIconFor(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => FtpIcon,
            NetworkProtocol.Sftp => SftpIcon,
            NetworkProtocol.Scp => ScpIcon,
            NetworkProtocol.Tftp => TftpIcon,
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

    /// <summary>Uma forma do desenho: preenchida (com borda opcional) ou só um traço.</summary>
    private readonly record struct Shape(string Data, Brush? Fill, Pen? Outline)
    {
        public static Shape Filled(string color, string data, string? rimColor = null, double rimWidth = 0) =>
            new(data, Brush(color), rimColor is null ? null : RoundPen(rimColor, rimWidth));

        public static Shape Stroked(string color, double width, string data) =>
            new(data, null, RoundPen(color, width));
    }

    private static Pen RoundPen(string color, double width)
    {
        var pen = new Pen(Brush(color), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static ImageSource Drawing(params Shape[] shapes)
    {
        var group = new DrawingGroup();

        // Retângulo transparente: fixa o tamanho da imagem em 16x16 mesmo que o desenho não encoste nas bordas.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16))));
        foreach (var shape in shapes)
        {
            var geometry = Geometry.Parse(shape.Data);
            geometry.Freeze();
            group.Children.Add(new GeometryDrawing(shape.Fill, shape.Outline, geometry));
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
