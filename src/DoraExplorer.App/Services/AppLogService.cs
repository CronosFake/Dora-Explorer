using System.Globalization;
using System.Text;
using Serilog;
using Serilog.Events;

namespace DoraExplorer.App.Services;

public static class AppLogService
{
    private const long MaxLogFileBytes = 10L * 1024 * 1024;
    private const int RetainedFileCount = 14;

    private static readonly object SyncRoot = new();
    private static ILogger? _logger;

    public static string LogsDirectory { get; private set; } = string.Empty;

    public static void Initialize()
    {
        LogsDirectory = ResolveLogsDirectory();
        Directory.CreateDirectory(LogsDirectory);

        // O Serilog acrescenta a data (yyyyMMdd) ao nome por causa do RollingInterval.Day.
        var logFilePath = Path.Combine(LogsDirectory, "doraexplorer-.log");

        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: MaxLogFileBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                encoding: Encoding.UTF8,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Info("=== Dora Explorer iniciado ===");
        Info($"Versão: {typeof(App).Assembly.GetName().Version}");
        Info($"Executável: {AppContext.BaseDirectory}");
        Info($"Logs: {LogsDirectory}");
    }

    public static void Shutdown()
    {
        Info("=== Dora Explorer encerrado ===");
        lock (SyncRoot)
        {
            if (_logger is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _logger = null;
        }
    }

    public static void Info(string message) => Write(LogEventLevel.Information, message);

    public static void Warning(string message) => Write(LogEventLevel.Warning, message);

    public static void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Write(LogEventLevel.Error, message);
            return;
        }

        _logger?.Error(exception, message);
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[ERROR] {message}{Environment.NewLine}{exception}");
#endif
    }

    private static void Write(LogEventLevel level, string message)
    {
        if (_logger is null)
        {
            return;
        }

        _logger.Write(level, message);
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[{level.ToString().ToUpperInvariant()[..4]}] {message}");
#endif
    }

    private static string ResolveLogsDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "logs")),
            Path.GetFullPath(Path.Combine(baseDir, "logs")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DoraExplorer",
                "logs")
        };

        foreach (var candidate in candidates)
        {
            var parent = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
