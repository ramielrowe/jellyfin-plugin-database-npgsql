using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>
/// Resolves explicit external infrastructure or starts the opt-in pinned container.
/// </summary>
public sealed class PostgreSqlTestEnvironment : IAsyncDisposable
{
    private readonly PostgreSqlTestContainer? _container;
    private readonly PostgreSqlClusterLease _clusterLease;

    private PostgreSqlTestEnvironment(
        PostgreSqlTestOptions options,
        string administratorConnectionString,
        PostgreSqlTestContainer? container,
        PostgreSqlClusterLease clusterLease)
    {
        Options = options;
        AdministratorConnectionString = administratorConnectionString;
        _container = container;
        _clusterLease = clusterLease;
    }

    public PostgreSqlTestOptions Options { get; }

    public string AdministratorConnectionString { get; }

    public static async Task<PostgreSqlTestEnvironment> CreateAsync(
        PostgreSqlTestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(options.MissingInfrastructureMessage);
        }

        if (!options.UseContainer && !options.ExternalExclusive)
        {
            throw new InvalidOperationException(
                $"External automatic PostgreSQL test resource management requires "
                + $"{PostgreSqlTestOptions.ExternalExclusiveEnvironmentVariable}=true and the documented exclusive-cluster marker. No connection or mutation was attempted.");
        }

        if (options.UseContainer)
        {
            var container = await PostgreSqlTestContainer.StartAsync(options, cancellationToken).ConfigureAwait(false);
            try
            {
                var lease = await PostgreSqlClusterLease.AcquireAsync(
                    container.AdministratorConnectionString,
                    harnessOwnsContainer: true,
                    externalExclusiveOptIn: false,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(lease, cancellationToken).ConfigureAwait(false);
                    return new PostgreSqlTestEnvironment(options, container.AdministratorConnectionString, container, lease);
                }
                catch
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                await container.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var externalLease = await PostgreSqlClusterLease.AcquireAsync(
            options.AdministratorConnectionString!,
            harnessOwnsContainer: false,
            options.ExternalExclusive,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(externalLease, cancellationToken).ConfigureAwait(false);
            return new PostgreSqlTestEnvironment(options, options.AdministratorConnectionString!, null, externalLease);
        }
        catch
        {
            await externalLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<PostgreSqlTestDatabase> CreateDatabaseAsync(
        CancellationToken cancellationToken,
        Action<TestResourceIdentity>? resourceCreated = null)
        => PostgreSqlTestDatabase.CreateAsync(
            _clusterLease,
            Options.WorkerId,
            cancellationToken,
            resourceCreated);

    public Task<PostgreSqlTestDatabase[]> CreateDatabasesAsync(
        System.Collections.Generic.IEnumerable<string> workerIds,
        CancellationToken cancellationToken,
        Action<string, TestResourceIdentity>? resourceCreated = null)
        => PostgreSqlTestDatabase.CreateManyAsync(_clusterLease, workerIds, cancellationToken, resourceCreated);

    public Task ReclaimStaleResourcesAsync(CancellationToken cancellationToken)
        => PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(_clusterLease, cancellationToken);

    public Task ExecuteAdministrativeControlAsync(string sql, CancellationToken cancellationToken)
        => ExecuteAdministrativeControlsAsync([sql], cancellationToken);

    public Task ExecuteAdministrativeControlsAsync(
        System.Collections.Generic.IEnumerable<string> statements,
        CancellationToken cancellationToken)
        => _clusterLease.RunAsync(
            async (connection, token) =>
            {
                foreach (var statement in statements)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _clusterLease.DisposeAsync().ConfigureAwait(false);
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
