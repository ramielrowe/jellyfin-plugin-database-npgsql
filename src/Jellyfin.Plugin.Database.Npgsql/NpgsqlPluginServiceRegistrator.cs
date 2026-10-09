using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Decorates the registered stock backup service without replacing its implementation.
/// </summary>
public sealed class NpgsqlPluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);
        var stockDescriptor = serviceCollection.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IBackupService))
            ?? throw new InvalidOperationException("Jellyfin did not register its stock IBackupService before plugin service registration.");
        if (stockDescriptor.Lifetime is not ServiceLifetime.Singleton)
        {
            throw new NotSupportedException("The audited Jellyfin backup service lifetime is no longer singleton.");
        }

        var configurationDescriptor = serviceCollection.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IServerConfigurationManager));
        var configurationManager = configurationDescriptor?.ImplementationInstance as IServerConfigurationManager
            ?? throw new InvalidOperationException(
                "Jellyfin did not expose its configuration manager instance before plugin service registration.");
        var databaseConfiguration = configurationManager.GetConfiguration("database") as DatabaseConfigurationOptions;
        var isActiveProvider = string.Equals(
            databaseConfiguration?.DatabaseType,
            "PLUGIN_PROVIDER",
            StringComparison.OrdinalIgnoreCase);

        serviceCollection.Remove(stockDescriptor);
        serviceCollection.AddSingleton(
            serviceProvider => new StockBackupService(CreateStockService(serviceProvider, stockDescriptor)));
        var decoratorDescriptor = ServiceDescriptor.Singleton<IBackupService>(serviceProvider =>
            new NpgsqlBackupServiceDecorator(
                serviceProvider.GetRequiredService<StockBackupService>().Value,
                isActiveProvider,
                serviceProvider));
        serviceCollection.Add(decoratorDescriptor);
        serviceCollection.AddSingleton(
            new NpgsqlBackupSafetyRegistration(serviceCollection, decoratorDescriptor));
    }

    private static IBackupService CreateStockService(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IBackupService instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IBackupService)descriptor.ImplementationFactory(serviceProvider);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IBackupService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        }

        throw new NotSupportedException("The stock IBackupService registration has an unsupported descriptor shape.");
    }

    private sealed record StockBackupService(IBackupService Value);
}
