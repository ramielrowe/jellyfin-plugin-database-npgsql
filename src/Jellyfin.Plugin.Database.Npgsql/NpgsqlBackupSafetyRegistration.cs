using Jellyfin.Server.Implementations.SystemBackupService;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Carries proof from normal plugin registration into the independently loaded early provider.
/// </summary>
internal sealed class NpgsqlBackupSafetyRegistration
{
    private readonly IServiceCollection services;
    private readonly ServiceDescriptor decoratorDescriptor;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlBackupSafetyRegistration"/> class.</summary>
    /// <param name="services">The host service collection, retained through final plugin registration.</param>
    /// <param name="decoratorDescriptor">The exact decorator descriptor installed by this plugin.</param>
    internal NpgsqlBackupSafetyRegistration(
        IServiceCollection services,
        ServiceDescriptor decoratorDescriptor)
    {
        this.services = services;
        this.decoratorDescriptor = decoratorDescriptor;
    }

    /// <summary>Returns whether no later plugin replaced or ambiguously appended IBackupService.</summary>
    /// <returns><see langword="true"/> only for the exact final descriptor.</returns>
    internal bool IsExactFinalRegistration()
    {
        var registrations = this.services
            .Where(descriptor => descriptor.ServiceType == typeof(IBackupService))
            .ToArray();
        return registrations.Length == 1
            && ReferenceEquals(registrations[0], this.decoratorDescriptor);
    }
}
