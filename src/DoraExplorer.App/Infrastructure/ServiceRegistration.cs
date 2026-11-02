using DoraExplorer.App.Contracts;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DoraExplorer.App.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddDoraExplorerServices(this IServiceCollection services)
    {
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<DirectoryIndexService>();
        services.AddSingleton<IDirectoryIndexService>(sp => sp.GetRequiredService<DirectoryIndexService>());
        services.AddSingleton<ExplorerViewSettingsService>();
        services.AddSingleton<PaneDisplaySettingsService>();
        services.AddSingleton<RememberPathSettingsService>();
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<INetworkSessionStore, NetworkSessionStore>();
        services.AddSingleton<INetworkServerService, NetworkServerService>();
        services.AddSingleton<NetworkServerLogStore>();
        services.AddSingleton<INetworkFileService, NetworkFileService>();
        services.AddSingleton<IRemoteFileService, RemoteFileService>();
        services.AddSingleton<NetworkClientHealthService>();
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<IArchiveService, ArchiveService>();
        services.AddSingleton<ILocalFileSystemService, LocalFileSystemService>();
        services.AddSingleton<INavigationTreeService, NavigationTreeService>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddTransient<MainWindowViewModel>();
        return services;
    }
}
