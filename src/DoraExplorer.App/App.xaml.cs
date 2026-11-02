using System.Windows;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DoraExplorer.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceGuard? _singleInstanceGuard;

    public static IServiceProvider Services { get; private set; } = null!;

    public static ThemeManager ThemeManager { get; private set; } = null!;

    public static AppSettingsService SettingsService { get; private set; } = null!;

    public static ExplorerViewSettingsService ExplorerViewSettings { get; private set; } = null!;

    public static PaneDisplaySettingsService PaneDisplaySettings { get; private set; } = null!;

    public static RememberPathSettingsService RememberPathSettings { get; private set; } = null!;

    public static DirectoryIndexService DirectoryIndex { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Aberto como administrador pelo próprio app: espera o processo antigo fechar e soltar a trava de instância única.
        if (StartupArguments.TryGetParentProcessId(e.Args, out var parentProcessId))
        {
            StartupArguments.WaitForParentExit(parentProcessId, TimeSpan.FromSeconds(15));
        }

        if (!SingleInstanceGuard.TryAcquire(out _singleInstanceGuard))
        {
            MessageBox.Show(
                "O Dora Explorer já está em execução.",
                "Dora Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppLogService.Initialize();
        RegisterGlobalExceptionHandlers();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services => services.AddDoraExplorerServices())
                .Build();

            _host.Start();
            Services = _host.Services;

            SettingsService = Services.GetRequiredService<AppSettingsService>();
            SettingsService.ProtectLegacyPasswords();
            FileAssociationService.UsePersistence(
                SettingsService.LoadDefaultApplications(),
                SettingsService.SaveDefaultApplication,
                SettingsService.RemoveDefaultApplication);
            ThemeManager = Services.GetRequiredService<ThemeManager>();
            ExplorerViewSettings = Services.GetRequiredService<ExplorerViewSettingsService>();
            ExplorerViewSettings.Load();
            PaneDisplaySettings = Services.GetRequiredService<PaneDisplaySettingsService>();
            PaneDisplaySettings.Load();
            RememberPathSettings = Services.GetRequiredService<RememberPathSettingsService>();
            RememberPathSettings.Load();
            DirectoryIndex = Services.GetRequiredService<DirectoryIndexService>();
            DirectoryIndex.ScheduleMaintenance();
            ThemeManager.Initialize();

            // Serviços de rede arrancam sempre parados; limpa estado preso e não mata processos externos.
            var networkSessions = Services.GetRequiredService<Contracts.INetworkSessionStore>();
            networkSessions.ClearAll();
            var networkServers = Services.GetRequiredService<Contracts.INetworkServerService>();
            networkServers.StopAll();
            Services.GetRequiredService<NetworkClientHealthService>().Start();
            var networkSettings = SettingsService.LoadNetworkSettings();
            NetworkPortGuard.ForceStopOwnedListeners(
            [
                networkSettings.Ftp.Port,
                networkSettings.Sftp.Port,
                networkSettings.Scp.Port,
                networkSettings.Tftp.Port
            ]);

            var mainWindow = new MainWindow(Services.GetRequiredService<ViewModels.MainWindowViewModel>());
            mainWindow.Show();

            ThemeManager.ApplyTitleBar(mainWindow);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao iniciar a aplicação", ex);

            // Relançar aqui cairia no DispatcherUnhandledException (Handled = true) e deixaria
            // um processo sem janela, preso ao mutex de instância única.
            MessageBox.Show(
                $"Não foi possível iniciar o Dora Explorer.\n\n{ex.Message}\n\nDetalhes em: {AppLogService.LogsDirectory}",
                "Dora Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Services.GetService<NetworkClientHealthService>()?.Dispose();
            _host?.Services.GetService<Contracts.INetworkServerService>()?.StopAll();
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao parar servidores de rede no encerramento", ex);
        }

        AppLogService.Shutdown();
        ThemeManager?.Dispose();
        DirectoryIndex?.Dispose();
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        var uiExceptionCount = 0;

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogService.Error("Exceção não tratada na interface (UI)", args.Exception);

            if (++uiExceptionCount > 20)
            {
                AppLogService.Error("Demasiadas exceções na interface — a encerrar para evitar loop.");
                Shutdown(1);
                return;
            }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogService.Error("Exceção não tratada no domínio da aplicação", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogService.Error("Exceção não observada em tarefa assíncrona", args.Exception);
            args.SetObserved();
        };
    }
}
