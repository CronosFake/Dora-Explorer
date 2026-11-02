using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Cópia e movimentação de arquivos pela API do Windows (CopyFileEx / MoveFileWithProgress), a mesma do Explorer:
/// avisa o andamento a cada bloco, cancela no meio de um arquivo grande e preserva datas, atributos e fluxos
/// alternativos (como o "Zone.Identifier", que marca arquivos baixados da internet).
/// </summary>
internal static class NativeFileCopy
{
    private const uint CopyFileFailIfExists = 0x1;
    private const uint CopyFileNoBuffering = 0x1000;
    private const uint MoveFileCopyAllowed = 0x2;
    private const uint MoveFileWriteThrough = 0x8;
    private const uint ProgressContinue = 0;
    private const uint ProgressCancel = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorRequestAborted = 1235;

    // Acima disto o cache do sistema só atrapalha (a cópia expulsaria o resto da memória de arquivos).
    private const long NoBufferingThreshold = 512L * 1024 * 1024;

    /// <param name="onProgress">Recebe o total de bytes já copiados deste arquivo.</param>
    public static void CopyFile(string source, string destination, Action<long> onProgress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var length = new FileInfo(source).Length;
        var flags = CopyFileFailIfExists | (length >= NoBufferingThreshold ? CopyFileNoBuffering : 0u);
        var cancel = 0;
        var callback = CreateCallback(onProgress, cancellationToken);

        if (!CopyFileEx(source, destination, callback, IntPtr.Zero, ref cancel, flags))
        {
            ThrowLastError(Marshal.GetLastWin32Error(), source, destination, "copiar", cancellationToken);
        }

        GC.KeepAlive(callback);
    }

    /// <summary>Move um arquivo; entre unidades copia e depois apaga a origem, com andamento e cancelamento.</summary>
    public static void MoveFile(string source, string destination, Action<long> onProgress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var callback = CreateCallback(onProgress, cancellationToken);
        if (!MoveFileWithProgress(source, destination, callback, IntPtr.Zero, MoveFileCopyAllowed | MoveFileWriteThrough))
        {
            ThrowLastError(Marshal.GetLastWin32Error(), source, destination, "mover", cancellationToken);
        }

        GC.KeepAlive(callback);
    }

    private static CopyProgressRoutine CreateCallback(Action<long> onProgress, CancellationToken cancellationToken) =>
        (_, transferred, _, _, _, _, _, _, _) =>
        {
            // Uma exceção que escapasse daqui derrubaria o processo (é código nativo chamando de volta).
            try
            {
                onProgress(transferred);
                return cancellationToken.IsCancellationRequested ? ProgressCancel : ProgressContinue;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return ProgressCancel;
            }
        };

    private static void ThrowLastError(int error, string path, string destination, string verb, CancellationToken cancellationToken)
    {
        if (error == ErrorRequestAborted)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var message = $"{Path.GetFileName(path)}: {new Win32Exception(error).Message}";
        if (error == ErrorAccessDenied)
        {
            throw new TransferAccessDeniedException(path, destination, verb, message);
        }

        throw new IOException(message, new Win32Exception(error));
    }

    private delegate uint CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        uint callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName,
        string newFileName,
        CopyProgressRoutine? progressRoutine,
        IntPtr data,
        ref int cancel,
        uint copyFlags);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileWithProgressW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileWithProgress(
        string existingFileName,
        string newFileName,
        CopyProgressRoutine? progressRoutine,
        IntPtr data,
        uint flags);
}

/// <summary>
/// Acumula o andamento de uma operação e o repassa no máximo a cada 100 ms (centenas de chamadas por segundo
/// inundariam a interface). Também avisa o vigia de inatividade: enquanto há bytes andando, a operação não expira.
/// </summary>
internal sealed class TransferProgressTracker(IProgress<FileOperationProgress>? progress, Action? keepAlive)
{
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency / 10;
    private static readonly long KeepAliveIntervalTicks = Stopwatch.Frequency / 4;

    private long _lastReport;
    private long _lastKeepAlive;
    private long _bytesTotal;
    private int _filesTotal;
    private long _bytesDone;
    private int _filesDone;
    private long _currentFileBytes;
    private string _currentFile = string.Empty;
    private bool _reportedOnce;

    /// <summary>Quando há quem acompanhe, vale a pena descobrir os totais antes de começar.</summary>
    public bool WantsTotals => progress is not null;

    public void SetTotals(long bytes, int files)
    {
        _bytesTotal = bytes;
        _filesTotal = files;
        Report(force: true);
    }

    public void BeginFile(string path)
    {
        _currentFile = Path.GetFileName(path);
        _currentFileBytes = 0;
        KeepAlive(force: true);
        Report(force: true);
    }

    public void FileProgress(long transferred)
    {
        _bytesDone += transferred - _currentFileBytes;
        _currentFileBytes = transferred;
        KeepAlive(force: false);
        Report(force: false);
    }

    public void EndFile(long fileLength)
    {
        _bytesDone += Math.Max(0, fileLength - _currentFileBytes);
        _currentFileBytes = 0;
        _filesDone++;
        Report(force: false);
    }

    public void Complete() => Report(force: true);

    /// <summary>Sinal de vida de trabalho que não copia bytes (varredura de pastas, remoção da origem).</summary>
    public void Heartbeat() => KeepAlive(force: false);

    private void KeepAlive(bool force)
    {
        if (keepAlive is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (force || now - _lastKeepAlive >= KeepAliveIntervalTicks)
        {
            _lastKeepAlive = now;
            keepAlive();
        }
    }

    private void Report(bool force)
    {
        if (progress is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && _reportedOnce && now - _lastReport < ReportIntervalTicks)
        {
            return;
        }

        _lastReport = now;
        _reportedOnce = true;
        progress.Report(new FileOperationProgress(_bytesDone, _bytesTotal, _filesDone, _filesTotal, _currentFile));
    }
}

/// <summary>Soma o tamanho do que será copiado (sem seguir links e junções, como a própria cópia).</summary>
internal static class TransferScanner
{
    public static (long Bytes, int Files) Scan(IEnumerable<string> paths, Action? heartbeat, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var files = 0;
        foreach (var path in paths)
        {
            var (itemBytes, itemFiles) = ScanItem(path, heartbeat, cancellationToken);
            bytes += itemBytes;
            files += itemFiles;
        }

        return (bytes, files);
    }

    public static (long Bytes, int Files) ScanItem(string path, Action? heartbeat, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(path))
            {
                return (new FileInfo(path).Length, 1);
            }

            if (!Directory.Exists(path))
            {
                return (0, 0);
            }

            long bytes = 0;
            var files = 0;
            var pending = new Stack<string>();
            pending.Push(path);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                heartbeat?.Invoke();

                foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options))
                {
                    if (entry is FileInfo file)
                    {
                        bytes += file.Length;
                        files++;
                    }
                    else if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }

            return (bytes, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0); // sem total, a barra fica sem porcentagem, mas a operação segue
        }
    }
}

/// <summary>Soma o andamento de vários itens (cada um reporta de zero) num único andamento geral.</summary>
internal sealed class TransferProgressAggregator(IProgress<FileOperationProgress> inner, long grandBytes, int grandFiles)
    : IProgress<FileOperationProgress>
{
    private readonly object _sync = new();
    private long _baseBytes;
    private int _baseFiles;

    public void CompleteItem(long itemBytes, int itemFiles)
    {
        lock (_sync)
        {
            _baseBytes += itemBytes;
            _baseFiles += itemFiles;
        }
    }

    public void Report(FileOperationProgress value)
    {
        long bytesBase;
        int filesBase;
        lock (_sync)
        {
            bytesBase = _baseBytes;
            filesBase = _baseFiles;
        }

        inner.Report(new FileOperationProgress(
            bytesBase + value.BytesDone,
            Math.Max(grandBytes, bytesBase + value.BytesTotal),
            filesBase + value.FilesDone,
            Math.Max(grandFiles, filesBase + value.FilesTotal),
            value.CurrentFile));
    }
}

/// <summary>
/// Envolve a leitura de um arquivo para avisar o andamento a cada bloco lido e abortar quando o usuário cancela.
/// Usado ao compactar, onde o ZIP/TAR lê o arquivo por conta própria.
/// </summary>
internal sealed class ProgressReadStream(FileStream inner, TransferProgressTracker tracker, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = inner.Read(buffer);
        tracker.FileProgress(inner.Position);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}