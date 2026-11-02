using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

public sealed class ElevationService : IElevationService
{
    // ERROR_CANCELLED: o usuário recusou a confirmação do Windows (UAC).
    private const int ErrorCancelledByUser = 1223;

    private readonly Func<bool> _isElevated;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly Func<string?> _executablePath;
    private readonly Action _shutdown;
    private readonly Lazy<bool> _elevated;

    public ElevationService()
        : this(IsProcessElevated, Process.Start, () => Environment.ProcessPath, RequestShutdown)
    {
    }

    /// <summary>Colaboradores trocáveis: os testes conferem o que seria feito sem abrir o aviso do Windows.</summary>
    internal ElevationService(
        Func<bool> isElevated,
        Func<ProcessStartInfo, Process?> start,
        Func<string?> executablePath,
        Action shutdown)
    {
        _isElevated = isElevated;
        _start = start;
        _executablePath = executablePath;
        _shutdown = shutdown;
        _elevated = new Lazy<bool>(isElevated);
    }

    public bool IsElevated => _elevated.Value;

    public bool TryRestartElevated(out string? errorMessage)
    {
        errorMessage = null;

        if (IsElevated)
        {
            errorMessage = "O Dora Explorer já está sendo executado como administrador.";
            return false;
        }

        var executable = _executablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            errorMessage = "Não foi possível reiniciar como administrador: o caminho do programa é desconhecido.";
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = StartupArguments.BuildRestartArguments(Environment.ProcessId),
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
            };

            using var started = _start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
        {
            AppLogService.Info("Reinício como administrador cancelado pelo usuário na confirmação do Windows.");
            return false;
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex))
        {
            AppLogService.Error("Falha ao reiniciar como administrador", ex);
            errorMessage = $"Não foi possível reiniciar como administrador ({ex.Message}).";
            return false;
        }

        AppLogService.Info("Reiniciando como administrador.");
        _shutdown();
        return true;
    }

    /// <summary>O token do processo está elevado (o usuário confirmou o aviso do Windows ou o UAC está desligado e a conta é administradora).</summary>
    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RequestShutdown() =>
        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
}
