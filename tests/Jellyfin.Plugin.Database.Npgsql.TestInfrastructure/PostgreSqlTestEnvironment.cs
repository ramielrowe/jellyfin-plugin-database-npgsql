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

    private PostgreSqlTestEnvironment(
        PostgreSqlTestOptions options,
        string administratorConnectionString,
        PostgreSqlTestContainer? container)
    {
        Options = options;
        AdministratorConnectionString = administratorConnectionString;
        _container = container;
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

        if (options.UseContainer)
        {
            var container = await PostgreSqlTestContainer.StartAsync(options, cancellationToken).ConfigureAwait(false);
            try
            {
                await PostgreSqlTestDatabase.ValidateServerVersionAsync(container.AdministratorConnectionString, 160006, cancellationToken).ConfigureAwait(false);
                await PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(container.AdministratorConnectionString, cancellationToken).ConfigureAwait(false);
                return new PostgreSqlTestEnvironment(options, container.AdministratorConnectionString, container);
            }
            catch
            {
                await container.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        _ = PostgreSqlTestDatabase.ValidateAdministratorConnectionString(options.AdministratorConnectionString!);
        await PostgreSqlTestDatabase.ValidateServerVersionAsync(options.AdministratorConnectionString!, 160006, cancellationToken).ConfigureAwait(false);
        await PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(options.AdministratorConnectionString!, cancellationToken).ConfigureAwait(false);
        return new PostgreSqlTestEnvironment(options, options.AdministratorConnectionString!, null);
    }

    public Task<PostgreSqlTestDatabase> CreateDatabaseAsync(
        CancellationToken cancellationToken,
        Action<TestResourceIdentity>? resourceCreated = null)
        => PostgreSqlTestDatabase.CreateAsync(
            AdministratorConnectionString,
            Options.WorkerId,
            cancellationToken,
            resourceCreated);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
