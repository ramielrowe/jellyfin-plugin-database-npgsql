using System.Runtime.CompilerServices;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Defers the provider-owned restore data source until adjacent Npgsql dependencies are loaded.</summary>
internal static class NpgsqlRestoreDataSource
{
    /// <summary>Creates a secret-capable data source behind an assembly-neutral handle.</summary>
    /// <param name="settings">The validated provider settings.</param>
    /// <returns>The opaque provider-owned data source.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static object Create(NpgsqlConnectionSettings settings)
    {
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        settings.ConfigureDataSource?.Invoke(builder);
        return settings.Host.Contains(',', StringComparison.Ordinal)
            ? builder.BuildMultiHost()
            : builder.Build();
    }

    /// <summary>Restores a snapshot through the secret-capable data source.</summary>
    /// <param name="handle">The opaque data-source handle.</param>
    /// <param name="backupRoot">The provider backup root.</param>
    /// <param name="key">The opaque backup key.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A task that represents the restore.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Task RestoreAsync(object handle, string backupRoot, string key, CancellationToken cancellationToken)
        => NpgsqlMigrationBackup.RestoreAsync(
            (NpgsqlDataSource)handle,
            backupRoot,
            key,
            cancellationToken);

    /// <summary>Disposes an opaque provider-owned data source.</summary>
    /// <param name="handle">The opaque data-source handle.</param>
    /// <returns>A task that represents disposal.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ValueTask DisposeAsync(object handle)
        => ((NpgsqlDataSource)handle).DisposeAsync();
}
