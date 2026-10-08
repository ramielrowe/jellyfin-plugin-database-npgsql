using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>
/// Owns one test-only database and one restricted login. Both are removed on disposal.
/// </summary>
public sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private const int RuntimeConnectionLimit = 6;
    private readonly string _administratorConnectionString;
    private readonly NpgsqlDataSource _runtimeDataSource;
    private bool _disposed;

    private PostgreSqlTestDatabase(
        string administratorConnectionString,
        string runtimeConnectionString,
        TestResourceIdentity identity,
        NpgsqlDataSource runtimeDataSource)
    {
        _administratorConnectionString = administratorConnectionString;
        RuntimeConnectionString = runtimeConnectionString;
        Identity = identity;
        _runtimeDataSource = runtimeDataSource;
    }

    public TestResourceIdentity Identity { get; }

    public string DatabaseName => Identity.DatabaseName;

    public string RoleName => Identity.RoleName;

    public string RuntimeConnectionString { get; }

    public string SanitizedRuntimeConnectionString => SensitiveDataRedactor.RedactConnectionString(RuntimeConnectionString);

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        string administratorConnectionString,
        string? workerId,
        CancellationToken cancellationToken,
        Action<TestResourceIdentity>? resourceCreated = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var administratorBuilder = ValidateAdministratorConnectionString(administratorConnectionString);
        var normalizedAdministratorConnection = administratorBuilder.ConnectionString;
        var identity = TestResourceNames.Create(workerId);
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var roleCreateAttempted = false;
        var databaseCreateAttempted = false;
        NpgsqlDataSource? runtimeDataSource = null;

        try
        {
            await using (var administratorDataSource = NpgsqlDataSource.Create(normalizedAdministratorConnection))
            await using (var connection = await administratorDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT rolsuper OR (rolcreatedb AND rolcreaterole) FROM pg_catalog.pg_roles WHERE rolname = current_user";
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    throw new InvalidOperationException(
                        "The explicit PostgreSQL test administrator must have CREATEDB and CREATEROLE (or be the isolated container administrator).");
                }

                command.CommandText = "CREATE ROLE " + QuoteOwnedRole(identity.RoleName)
                    + " LOGIN PASSWORD " + QuoteLiteral(runtimePassword)
                    + " NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT "
                    + RuntimeConnectionLimit;
                roleCreateAttempted = true;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                command.CommandText = "CREATE DATABASE " + QuoteOwnedDatabase(identity.DatabaseName)
                    + " OWNER " + QuoteOwnedRole(identity.RoleName)
                    + " CONNECTION LIMIT = 8";
                databaseCreateAttempted = true;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            resourceCreated?.Invoke(identity);
            cancellationToken.ThrowIfCancellationRequested();

            var runtimeBuilder = new NpgsqlConnectionStringBuilder(administratorBuilder.ConnectionString)
            {
                Database = identity.DatabaseName,
                Username = identity.RoleName,
                Password = runtimePassword,
                ApplicationName = "Jellyfin Npgsql test " + identity.DatabaseName,
                Pooling = true,
                MinPoolSize = 0,
                MaxPoolSize = 4,
                ConnectionIdleLifetime = 30,
                Timeout = 10,
                CommandTimeout = 30,
                IncludeErrorDetail = false,
                PersistSecurityInfo = false
            };
            var runtimeConnectionString = runtimeBuilder.ConnectionString;
            runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);

            await using (var runtimeConnection = await runtimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = runtimeConnection.CreateCommand())
            {
                command.CommandText = "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole "
                    + "AND NOT rolreplication AND NOT rolbypassrls AND rolconnlimit = " + RuntimeConnectionLimit
                    + " FROM pg_catalog.pg_roles WHERE rolname = current_user";
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    throw new InvalidOperationException(
                        "The generated PostgreSQL runtime login was not restricted as expected; refusing to run integration tests.");
                }
            }

            return new PostgreSqlTestDatabase(
                normalizedAdministratorConnection,
                runtimeConnectionString,
                identity,
                runtimeDataSource);
        }
        catch (OperationCanceledException)
        {
            await CleanupFailedCreationAsync(
                normalizedAdministratorConnection,
                runtimeDataSource,
                identity,
                databaseCreateAttempted,
                roleCreateAttempted).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            await CleanupFailedCreationAsync(
                normalizedAdministratorConnection,
                runtimeDataSource,
                identity,
                databaseCreateAttempted,
                roleCreateAttempted).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Unable to create an isolated PostgreSQL test database and restricted runtime role. "
                + "Verify the sanitized administrator endpoint and its test-only CREATEDB/CREATEROLE permissions."
                + (ex is PostgresException postgresException
                    ? " PostgreSQL SQLSTATE: " + postgresException.SqlState + "."
                    : string.Empty));
        }
        catch
        {
            await CleanupFailedCreationAsync(
                normalizedAdministratorConnection,
                runtimeDataSource,
                identity,
                databaseCreateAttempted,
                roleCreateAttempted).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _runtimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _runtimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static NpgsqlConnectionStringBuilder ValidateAdministratorConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"{PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable} is not a valid PostgreSQL connection string.");
        }

        if (string.IsNullOrWhiteSpace(builder.Host)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("The explicit PostgreSQL test administrator connection must specify Host, Database, and Username.");
        }

        if (!builder.Database.Contains(TestResourceNames.AdministratorDatabaseMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The administrator database must contain the safety marker '{TestResourceNames.AdministratorDatabaseMarker}'.");
        }

        if (builder.Host.Contains(',', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The PostgreSQL test administrator must target exactly one host.");
        }

        builder.Pooling = false;
        builder.Timeout = Math.Clamp(builder.Timeout, 1, 15);
        builder.CommandTimeout = Math.Clamp(builder.CommandTimeout, 1, 30);
        builder.IncludeErrorDetail = false;
        builder.PersistSecurityInfo = false;
        return builder;
    }

    public static async Task<bool> DatabaseExistsAsync(
        string administratorConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        TestResourceNames.EnsureOwnedDatabase(databaseName);
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_catalog.pg_database WHERE datname = $1)";
        command.Parameters.AddWithValue(databaseName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static async Task<bool> RoleExistsAsync(
        string administratorConnectionString,
        string roleName,
        CancellationToken cancellationToken)
    {
        TestResourceNames.EnsureOwnedRole(roleName);
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = $1)";
        command.Parameters.AddWithValue(roleName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _runtimeDataSource.DisposeAsync().ConfigureAwait(false);
        try
        {
            await DropResourcesAsync(_administratorConnectionString, Identity, dropDatabase: true, dropRole: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Unable to remove test-only database '{DatabaseName}' and role '{RoleName}'. Remove only those exact resources before rerunning.");
        }

        GC.SuppressFinalize(this);
    }

    private static string QuoteOwnedDatabase(string name)
    {
        TestResourceNames.EnsureOwnedDatabase(name);
        return '"' + name + '"';
    }

    private static string QuoteOwnedRole(string name)
    {
        TestResourceNames.EnsureOwnedRole(name);
        return '"' + name + '"';
    }

    private static string QuoteLiteral(string value)
        => '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';

    private static async Task CleanupFailedCreationAsync(
        string administratorConnectionString,
        NpgsqlDataSource? runtimeDataSource,
        TestResourceIdentity identity,
        bool dropDatabase,
        bool dropRole)
    {
        if (runtimeDataSource is not null)
        {
            try
            {
                await runtimeDataSource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Continue to the more important database and role cleanup.
            }
        }

        try
        {
            await DropResourcesAsync(administratorConnectionString, identity, dropDatabase, dropRole).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Setup failed and cleanup could not remove test-only database '{identity.DatabaseName}' and role '{identity.RoleName}'. "
                + "Remove only those exact resources before rerunning.");
        }
    }

    private static async Task DropResourcesAsync(
        string administratorConnectionString,
        TestResourceIdentity identity,
        bool dropDatabase,
        bool dropRole)
    {
        TestResourceNames.EnsureOwnedDatabase(identity.DatabaseName);
        TestResourceNames.EnsureOwnedRole(identity.RoleName);
        await using var connection = new NpgsqlConnection(administratorConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (dropDatabase)
        {
            command.CommandText = "DROP DATABASE IF EXISTS " + QuoteOwnedDatabase(identity.DatabaseName) + " WITH (FORCE)";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (dropRole)
        {
            command.CommandText = "DROP ROLE IF EXISTS " + QuoteOwnedRole(identity.RoleName);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
