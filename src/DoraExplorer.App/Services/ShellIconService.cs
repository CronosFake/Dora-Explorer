using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public static class ShellIconService
{
    // Ícones de ficheiros são partilhados por extensão (tal como no Explorer): dois ".txt"
    // têm o mesmo ícone. Como as pastas grandes trazem milhares de ficheiros, cachear por
    // extensão evita milhares de chamadas P/Invoke ao shell. Os ImageSource ficam congelados
    // (Freeze), pelo que são seguros para reutilizar entre threads. O conjunto é pequeno e
    // limitado (uma entrada por extensão distinta).
    private static readonly ConcurrentDictionary<string, ImageSource?> FileIconByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    // Extensões cujo ícone é específico do próprio ficheiro (não da extensão): não devem ser
    // cacheadas para não trocar o ícone real por outro do mesmo tipo.
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".url", ".scr"
    };

    /// <summary>
    /// Ícone de ficheiro reaproveitando cache por extensão. Para tipos com ícone próprio
    /// (executáveis, atalhos, etc.) devolve o ícone real sem cachear.
    /// </summary>
    public static ImageSource? GetCachedFileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Caminhos virtuais dora-ftp://… — o shell não os resolve; usar só o nome/extensão.
        if (path.StartsWith("dora-", StringComparison.OrdinalIgnoreCase))
        {
            var name = GetRemoteEntryName(path);
            return string.IsNullOrEmpty(name)
                ? GetGenericFileIcon()
                : GetCachedFileIcon(name);
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension) || PerFileIconExtensions.Contains(extension))
        {
            return GetFileIcon(path, isDirectory: false);
        }

        // Arquivos compactados ganham ícone próprio quando o Windows só tem o genérico (extensão que um programa como o
        // WinRAR registrou sem ícone). A decisão é por extensão, como o resto do cache.
        if (IsArchiveName(path))
        {
            return FileIconByExtension.GetOrAdd(
                extension,
                _ => ChooseArchiveIcon(GetFileIcon(path, isDirectory: false), HasGenericWindowsIcon(path)));
        }

        return FileIconByExtension.GetOrAdd(extension, _ => GetFileIcon(path, isDirectory: false));
    }

    /// <summary>Ícone de pasta/ficheiro remoto (por nome), sem depender do caminho virtual.</summary>
    public static ImageSource? GetRemoteItemIcon(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return GetGenericFolderIcon();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return GetGenericFileIcon();
        }

        return GetCachedFileIcon(name.Trim());
    }

    public static ImageSource? GetNetworkLocationIcon() => GetShellDllIcon(18);

    public static ImageSource? GetNetworkServerIcon() => GetShellDllIcon(15);

    public static ImageSource? GetGenericFolderIcon() =>
        GetFileIcon("Folder", isDirectory: true) ?? GetShellDllIcon(3);

    public static ImageSource? GetGenericFileIcon() =>
        GetFileIcon("file", isDirectory: false) ?? GetShellDllIcon(0);

    /// <summary>Arquivos compactados que o app abre como pasta (.zip, .rar, .7z, .tar, .tar.gz, .tgz e .gz), pelo nome.</summary>
    internal static bool IsArchiveName(string name) => ArchivePathHelper.IsArchiveFile(name);

    /// <summary>
    /// O ícone do Windows vale quando existe um de verdade; o desenhado entra no lugar do genérico (extensão que o
    /// Windows não conhece, ou que um programa como o WinRAR registrou sem ícone) ou quando o Windows não devolve nada.
    /// </summary>
    internal static ImageSource ChooseArchiveIcon(ImageSource? windowsIcon, bool windowsIconIsGeneric) =>
        windowsIcon is null || windowsIconIsGeneric ? ArchiveIcon : windowsIcon;

    /// <summary>
    /// Ícone de arquivo compactado: folha com zíper e uma faixa na base. Desenhado em vetor numa grade de 16x16 e congelado,
    /// então vale para qualquer thread e fica nítido em qualquer escala de tela.
    /// </summary>
    internal static ImageSource ArchiveIcon => DrawnArchiveIcon.Value;

    private static readonly Lazy<ImageSource> DrawnArchiveIcon = new(CreateArchiveIcon);

    private static ImageSource CreateArchiveIcon()
    {
        static SolidColorBrush Fill(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

        var paper = Fill(0xF3, 0xF0, 0xE8);
        var fold = Fill(0xD6, 0xD0, 0xBF);
        var teeth = Fill(0x5A, 0x52, 0x44);
        var pull = Fill(0xE0, 0x93, 0x2B);
        var band = Fill(0x2E, 0x80, 0x89);
        var outline = new Pen(Fill(0x8C, 0x84, 0x72), 1) { LineJoin = PenLineJoin.Miter };

        var page = Geometry.Parse("M2.5,0.5 L10,0.5 L13.5,4 L13.5,15.5 L2.5,15.5 Z");
        var group = new DrawingGroup();

        // Retângulo transparente: fixa o tamanho da imagem em 16x16 mesmo que o desenho não encoste nas bordas.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(paper, null, page));
        group.Children.Add(new GeometryDrawing(band, null, new RectangleGeometry(new Rect(2.5, 12, 11, 3.5))));
        group.Children.Add(new GeometryDrawing(fold, null, Geometry.Parse("M10,0.5 L10,4 L13.5,4 Z")));
        group.Children.Add(new GeometryDrawing(null, outline, page));

        // Zíper: dentes alternando entre duas colunas, descendo do topo até o cursor.
        for (var i = 0; i < 8; i++)
        {
            var x = i % 2 == 0 ? 6.0 : 7.5;
            group.Children.Add(new GeometryDrawing(teeth, null, new RectangleGeometry(new Rect(x, 1.2 + (i * 1.3), 1.6, 0.9))));
        }

        group.Children.Add(new GeometryDrawing(pull, null, new RectangleGeometry(new Rect(5.7, 10.2, 3.6, 1.6))));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// True quando o Windows dá a este nome o mesmo ícone de uma extensão sem tipo registrado. A comparação é pelo
    /// índice na lista de ícones do sistema (não cria ícone, então não há o que destruir).
    /// </summary>
    private static bool HasGenericWindowsIcon(string path) =>
        SystemIconIndexOf(Path.GetFileName(path)) == UnknownTypeIconIndex.Value;

    private static readonly Lazy<int> UnknownTypeIconIndex = new(() => SystemIconIndexOf("arquivo.dora-sem-tipo"));

    private static int SystemIconIndexOf(string name)
    {
        var shfi = new Shfileinfo();
        var result = SHGetFileInfo(
            name,
            FileAttributeNormal,
            ref shfi,
            (uint)Marshal.SizeOf<Shfileinfo>(),
            ShgfiSysIconIndex | ShgfiSmallIcon | ShgfiUseFileAttributes);
        return result == IntPtr.Zero ? -1 : shfi.IIcon;
    }

    private static string GetRemoteEntryName(string virtualPath)
    {
        var trimmed = virtualPath.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
    }

    public const string RecycleBinParsingName = @"::{645FF040-5081-101B-9F19-00AA003F27D2}";
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint ShgfiPidL = 0x000000008;
    private const uint ShgfiSysIconIndex = 0x000004000;
    private const uint ShgsiIcon = 0x000000100;
    private const uint ShgsiSmallIcon = 0x000000001;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int SfgaoFolder = 0x00000010;

    private enum StockIconId
    {
        RecyclerEmpty = 31,
        RecyclerFull = 32
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint CbSize;
        public IntPtr HIcon;
        public int ISysImageIndex;
        public int IIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shqueryrbinfo
    {
        public int CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileinfo
    {
        public IntPtr HIcon;
        public int IIcon;
        public uint DwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzTypeName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string SzDisplayName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref Shfileinfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHGetFileInfo(
        IntPtr pidl,
        uint dwFileAttributes,
        ref Shfileinfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(StockIconId siid, uint uFlags, ref StockIconInfo psii);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr hwnd,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref Shqueryrbinfo pSHQueryRBInfo);

    public static ImageSource? GetFileIcon(string path, bool isDirectory, int size = 16, bool touchDisk = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var shfi = new Shfileinfo();
        var flags = ShgfiIcon | ShgfiSmallIcon;
        var queryPath = path;

        if (isDirectory && !queryPath.EndsWith('\\'))
        {
            queryPath += "\\";
        }

        // touchDisk = false evita acessar o caminho (unidade de rede offline travaria a thread da UI).
        var exists = touchDisk && (isDirectory ? Directory.Exists(queryPath) : File.Exists(queryPath));
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;

        if (!exists)
        {
            flags |= ShgfiUseFileAttributes;
        }

        _ = SHGetFileInfo(queryPath, attributes, ref shfi, (uint)Marshal.SizeOf<Shfileinfo>(), flags);

        if (shfi.HIcon == IntPtr.Zero)
        {
            return null;
        }

        return CreateImageSourceFromHIcon(shfi.HIcon, size);
    }

    public static ImageSource? GetDriveIcon(string drivePath, bool isReady = true) =>
        GetFileIcon(drivePath, isDirectory: true, touchDisk: isReady);

    public static ImageSource? GetFolderIcon(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath) &&
            folderPath.StartsWith("dora-", StringComparison.OrdinalIgnoreCase))
        {
            return GetGenericFolderIcon();
        }

        return GetFileIcon(folderPath, isDirectory: true);
    }

    public static ImageSource? GetNetworkIcon() => GetShellDllIcon(17);

    public static ImageSource? GetSpecialIcon(string path) =>
        path.StartsWith("::", StringComparison.Ordinal)
            ? GetShellNamespaceIcon(path)
            : GetFileIcon(path, isDirectory: true);

    public static ImageSource? GetRecycleBinIcon(int size = 16)
    {
        var isEmpty = IsRecycleBinEmpty();
        var stockIcon = GetStockIcon(isEmpty ? StockIconId.RecyclerEmpty : StockIconId.RecyclerFull, size);
        if (stockIcon is not null)
        {
            return stockIcon;
        }

        var pidlIcon = GetShellNamespaceIconFromPidl(RecycleBinParsingName, size);
        if (pidlIcon is not null)
        {
            return pidlIcon;
        }

        return GetShellDllIcon(isEmpty ? 31 : 32, size);
    }

    public static ImageSource? GetShellNamespaceIcon(string parsingName, int size = 16) =>
        GetShellNamespaceIconFromPidl(parsingName, size)
        ?? GetShellNamespaceIconFromParsingName(parsingName, size);

    private static bool IsRecycleBinEmpty()
    {
        var info = new Shqueryrbinfo { CbSize = Marshal.SizeOf<Shqueryrbinfo>() };
        return SHQueryRecycleBin(string.Empty, ref info) == 0 && info.I64NumItems == 0;
    }

    private static ImageSource? GetStockIcon(StockIconId iconId, int size)
    {
        var info = new StockIconInfo { CbSize = (uint)Marshal.SizeOf<StockIconInfo>() };
        if (SHGetStockIconInfo(iconId, ShgsiIcon | ShgsiSmallIcon, ref info) != 0 || info.HIcon == IntPtr.Zero)
        {
            return null;
        }

        return CreateImageSourceFromHIcon(info.HIcon, size);
    }

    private static ImageSource? GetShellNamespaceIconFromPidl(string parsingName, int size)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, SfgaoFolder, out _) != 0 || pidl == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetFileInfoFromPidl(pidl, size);
        }
        finally
        {
            ILFree(pidl);
        }
    }

    private static ImageSource? GetFileInfoFromPidl(IntPtr pidl, int size)
    {
        var shfi = new Shfileinfo();
        var flags = ShgfiIcon | ShgfiSmallIcon | ShgfiPidL;
        var result = SHGetFileInfo(pidl, 0, ref shfi, (uint)Marshal.SizeOf<Shfileinfo>(), flags);

        if (result == IntPtr.Zero || shfi.HIcon == IntPtr.Zero)
        {
            return null;
        }

        return CreateImageSourceFromHIcon(shfi.HIcon, size);
    }

    private static ImageSource? GetShellNamespaceIconFromParsingName(string parsingName, int size)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        var shfi = new Shfileinfo();
        var flags = ShgfiIcon | ShgfiSmallIcon;
        _ = SHGetFileInfo(parsingName, 0, ref shfi, (uint)Marshal.SizeOf<Shfileinfo>(), flags);

        if (shfi.HIcon == IntPtr.Zero)
        {
            return null;
        }

        return CreateImageSourceFromHIcon(shfi.HIcon, size);
    }

    public static ImageSource? FromHIcon(IntPtr hIcon, int size = 16) =>
        CreateImageSourceFromHIcon(hIcon, size);

    private static ImageSource? CreateImageSourceFromHIcon(IntPtr hIcon, int size)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(size, size));

            source.Freeze();
            return source;
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    public static ImageSource? GetShellDllIcon(int iconIndex, int size = 16)
    {
        var dllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
        _ = ExtractIconEx(dllPath, iconIndex, out var largeIcon, out var smallIcon, 1);

        // O Windows devolve os dois tamanhos e quem chama é que destrói: sem isto cada chamada vazava um HICON.
        if (largeIcon != IntPtr.Zero)
        {
            _ = DestroyIcon(largeIcon);
        }

        if (smallIcon == IntPtr.Zero)
        {
            return null;
        }

        return CreateImageSourceFromHIcon(smallIcon, size);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
}
