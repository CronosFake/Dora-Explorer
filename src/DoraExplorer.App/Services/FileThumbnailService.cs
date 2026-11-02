using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Miniaturas para a prévia ao passar o mouse: imagens são decodificadas pelo WPF (já no tamanho pedido, sem carregar o
/// arquivo inteiro na resolução original) e PDFs pelo provedor de miniaturas do próprio Windows — o mesmo que o Explorer usa.
/// Sem provedor instalado para o tipo, devolve nulo: nunca se mostra uma prévia vazia.
/// </summary>
public static class FileThumbnailService
{
    // Imagens enormes (centenas de megapixels) levam segundos para decodificar: não valem uma prévia.
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(8);

    // O provedor de miniaturas pode carregar componentes pesados (leitor de PDF): no máximo duas extrações ao mesmo tempo.
    private static readonly SemaphoreSlim ShellGate = new(2);

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico"
        };

    private static readonly HashSet<string> ShellExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf"
        };

    public static bool CanLoadThumbnail(string path)
    {
        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension) || ShellExtensions.Contains(extension);
    }

    /// <summary>Miniatura de até <paramref name="pixelSize"/> pixels no maior lado. Bloqueia: chame fora da thread da interface.</summary>
    public static ImageSource? TryLoadThumbnail(string path, int pixelSize = 80)
    {
        if (!CanLoadThumbnail(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return ShellExtensions.Contains(Path.GetExtension(path))
                ? TryLoadShellThumbnail(path, pixelSize)
                : TryLoadImageThumbnail(path, pixelSize);
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex) || ex is OutOfMemoryException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Versão assíncrona para a interface: roda numa thread própria (o provedor de miniaturas do Windows exige STA),
    /// com limite de simultâneas e de tempo. Se estourar o tempo, o resultado tardio é descartado.
    /// </summary>
    public static async Task<ImageSource?> TryLoadThumbnailAsync(string path, int pixelSize, CancellationToken cancellationToken)
    {
        if (!CanLoadThumbnail(path))
        {
            return null;
        }

        await ShellGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var work = RunOnStaThread(() => TryLoadThumbnail(path, pixelSize));
            var finished = await Task.WhenAny(work, Task.Delay(ShellTimeout, cancellationToken)).ConfigureAwait(false);
            return finished == work ? await work.ConfigureAwait(false) : null;
        }
        finally
        {
            _ = ShellGate.Release();
        }
    }

    private static Task<ImageSource?> RunOnStaThread(Func<ImageSource?> work)
    {
        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true, // um provedor travado não pode impedir o programa de fechar
            Name = "Dora.Thumbnail"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static BitmapImage? TryLoadImageThumbnail(string path, int pixelSize)
    {
        if (new FileInfo(path).Length > MaxImageBytes)
        {
            return null;
        }

        // Lê só as dimensões (sem decodificar) para limitar o MAIOR lado; limitar só a largura deixaria uma imagem alta enorme.
        int width;
        int height;
        using (var probe = File.OpenRead(path))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        if (Math.Max(width, height) > pixelSize)
        {
            if (width >= height)
            {
                image.DecodePixelWidth = pixelSize;
            }
            else
            {
                image.DecodePixelHeight = pixelSize;
            }
        }

        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? TryLoadShellThumbnail(string path, int pixelSize)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0)
        {
            return null;
        }

        try
        {
            // Só miniatura de verdade: sem provedor para o tipo, o Windows devolveria o ícone genérico, que não diz nada.
            var hr = factory.GetImage(new NativeSize { Width = pixelSize, Height = pixelSize }, ShellImageFlags.BiggerSizeOk | ShellImageFlags.ThumbnailOnly, out var bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                _ = DeleteObject(bitmap);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [Flags]
    private enum ShellImageFlags
    {
        BiggerSizeOk = 0x1,
        ThumbnailOnly = 0x8
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellImageFlags flags, out IntPtr phbm);
    }
}
