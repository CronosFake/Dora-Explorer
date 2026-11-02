using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// O app monta os serviços por injeção de dependência ao abrir. Um serviço novo cujo construtor pede algo que não foi
/// registrado só estoura na hora de abrir o app; este teste pega isso antes, sem criar nenhum serviço (então não toca em
/// <c>%AppData%\DoraExplorer</c>).
/// </summary>
public sealed class ServiceRegistrationTests
{
    [Fact]
    public void Every_registered_service_has_all_its_dependencies_registered()
    {
        var services = new ServiceCollection().AddDoraExplorerServices();

        var build = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }).Dispose();

        build.Should().NotThrow();
    }

    [Theory]
    [InlineData(typeof(IArchiveService), typeof(ArchiveService))]
    [InlineData(typeof(IElevationService), typeof(ElevationService))]
    [InlineData(typeof(IFileOperationService), typeof(FileOperationService))]
    [InlineData(typeof(ILocalFileSystemService), typeof(LocalFileSystemService))]
    [InlineData(typeof(INavigationTreeService), typeof(NavigationTreeService))]
    [InlineData(typeof(IRemoteFileService), typeof(RemoteFileService))]
    [InlineData(typeof(INetworkServerService), typeof(NetworkServerService))]
    public void Each_contract_is_served_by_its_implementation(Type contract, Type implementation)
    {
        var services = new ServiceCollection().AddDoraExplorerServices();

        var registration = services.Single(d => d.ServiceType == contract);

        registration.ImplementationType.Should().Be(implementation);
        registration.Lifetime.Should().Be(ServiceLifetime.Singleton, "o estado compartilhado do app vive num só objeto");
    }

    [Fact]
    public void The_administrator_startup_option_is_one_shared_object_so_the_menu_and_the_app_start_agree()
    {
        var services = new ServiceCollection().AddDoraExplorerServices();

        var registration = services.Single(d => d.ServiceType == typeof(AdministratorStartupService));

        registration.ImplementationType.Should().Be(typeof(AdministratorStartupService));
        registration.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_main_window_gets_a_fresh_view_model_each_time()
    {
        var services = new ServiceCollection().AddDoraExplorerServices();

        services.Single(d => d.ServiceType == typeof(MainWindowViewModel)).Lifetime
            .Should().Be(ServiceLifetime.Transient);
    }
}
