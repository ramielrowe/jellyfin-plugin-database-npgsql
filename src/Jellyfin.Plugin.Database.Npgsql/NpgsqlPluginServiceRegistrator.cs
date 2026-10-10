using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Installs the full-system restore guard when this package is the selected early database provider.
/// </summary>
public sealed class NpgsqlPluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        if (!IsSelectedProvider(serviceCollection))
        {
            return;
        }

        var stockDescriptor = serviceCollection.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IBackupService))
            ?? throw new InvalidOperationException(
                "Jellyfin did not register its stock backup service before the Npgsql restore guard.");
        if (stockDescriptor.Lifetime != ServiceLifetime.Singleton)
        {
            throw new NotSupportedException(
                "The Jellyfin backup-service lifetime differs from the audited 12.2 singleton contract; the Npgsql restore guard was not installed.");
        }

        serviceCollection.Remove(stockDescriptor);
        var guardDescriptor = ServiceDescriptor.Singleton<IBackupService>(serviceProvider =>
            new NpgsqlFullSystemRestoreGuard(CreateStockService(serviceProvider, stockDescriptor)));
        serviceCollection.Add(guardDescriptor);
        serviceCollection.AddSingleton(new NpgsqlRestoreGuardRegistration(serviceCollection, guardDescriptor));
    }

    private static bool IsSelectedProvider(IServiceCollection serviceCollection)
    {
        var configurationDescriptor = serviceCollection.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IServerConfigurationManager));
        var configurationManager = configurationDescriptor?.ImplementationInstance as IServerConfigurationManager
            ?? throw new InvalidOperationException(
                "Jellyfin did not expose its configuration manager before Npgsql plugin service registration.");
        var databaseConfiguration = configurationManager.GetConfiguration("database") as DatabaseConfigurationOptions;
        var custom = databaseConfiguration?.CustomProviderOptions;
        return string.Equals(databaseConfiguration?.DatabaseType, "PLUGIN_PROVIDER", StringComparison.OrdinalIgnoreCase)
            && string.Equals(custom?.PluginAssembly, PluginMetadata.AssemblyFileName, StringComparison.Ordinal);
    }

    private static IBackupService CreateStockService(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
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

        throw new NotSupportedException("The Jellyfin backup-service registration has an unsupported descriptor shape.");
    }
}
