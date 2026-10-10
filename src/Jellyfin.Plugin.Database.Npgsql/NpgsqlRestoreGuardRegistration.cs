using Jellyfin.Server.Implementations.SystemBackupService;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Carries verifiable registration evidence from Jellyfin's ordinary plugin loader to the early provider.
/// </summary>
internal sealed class NpgsqlRestoreGuardRegistration
{
    private readonly IServiceCollection services;
    private readonly ServiceDescriptor guardDescriptor;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlRestoreGuardRegistration"/> class.</summary>
    /// <param name="services">The host service collection, retained until provider initialization.</param>
    /// <param name="guardDescriptor">The exact guard descriptor installed by this package.</param>
    internal NpgsqlRestoreGuardRegistration(IServiceCollection services, ServiceDescriptor guardDescriptor)
    {
        this.services = services;
        this.guardDescriptor = guardDescriptor;
    }

    /// <summary>Determines whether the guard remains the one unambiguous final backup-service registration.</summary>
    /// <returns><see langword="true"/> only when later registration did not replace or append the service.</returns>
    internal bool IsExactFinalRegistration()
    {
        var registrations = this.services
            .Where(descriptor => descriptor.ServiceType == typeof(IBackupService))
            .ToArray();
        return registrations.Length == 1
            && ReferenceEquals(registrations[0], this.guardDescriptor);
    }
}
