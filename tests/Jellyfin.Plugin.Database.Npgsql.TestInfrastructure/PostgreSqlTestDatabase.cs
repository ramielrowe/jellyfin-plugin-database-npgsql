using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private const string OwnershipMarkerPrefix = "jellyfin-npgsql-test-owner|";
    private readonly string _administratorConnectionString;
    private readonly NpgsqlDataSource _runtimeDataSource;
    private readonly string _leaseId;
    private readonly FileStream _ownerLease;
    private bool _disposed;

    private PostgreSqlTestDatabase(
        string administratorConnectionString,
        string runtimeConnectionString,
        TestResourceIdentity identity,
        NpgsqlDataSource runtimeDataSource,
        string leaseId,
        FileStream ownerLease)
    {
        _administratorConnectionString = administratorConnectionString;
        RuntimeConnectionString = runtimeConnectionString;
        Identity = identity;
        _runtimeDataSource = runtimeDataSource;
        _leaseId = leaseId;
        _ownerLease = ownerLease;
    }

    public TestResourceIdentity Identity { get; }

    public string DatabaseName => Identity.DatabaseName;

    public string RoleName => Identity.RoleName;

    public string RuntimeConnectionString { get; }

    public string SanitizedRuntimeConnectionString => SensitiveDataRedactor.RedactConnectionString(RuntimeConnectionString);

    public static string TestScope => GetScope();

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
        var leaseId = Guid.NewGuid().ToString("N");
        var ownerLease = AcquireOwnerLease(leaseId);
        var ownershipTransferred = false;
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
                command.CommandText = "COMMENT ON ROLE " + QuoteOwnedRole(identity.RoleName)
                    + " IS " + QuoteLiteral(CurrentOwnershipMarker(leaseId));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                command.CommandText = "CREATE DATABASE " + QuoteOwnedDatabase(identity.DatabaseName)
                    + " OWNER " + QuoteOwnedRole(identity.RoleName)
                    + " CONNECTION LIMIT = 8";
                databaseCreateAttempted = true;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "COMMENT ON DATABASE " + QuoteOwnedDatabase(identity.DatabaseName)
                    + " IS " + QuoteLiteral(CurrentOwnershipMarker(leaseId));
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

            var result = new PostgreSqlTestDatabase(
                normalizedAdministratorConnection,
                runtimeConnectionString,
                identity,
                runtimeDataSource,
                leaseId,
                ownerLease);
            ownershipTransferred = true;
            return result;
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
        finally
        {
            if (!ownershipTransferred)
            {
                ownerLease.Dispose();
                DeleteOwnerLease(leaseId);
            }
        }
    }

    public static async Task<PostgreSqlTestDatabase[]> CreateManyAsync(
        string administratorConnectionString,
        IEnumerable<string> workerIds,
        CancellationToken cancellationToken,
        Action<string, TestResourceIdentity>? resourceCreated = null)
    {
        var tasks = workerIds.Select(workerId => CreateAsync(
            administratorConnectionString,
            workerId,
            cancellationToken,
            identity => resourceCreated?.Invoke(workerId, identity))).ToArray();
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (var task in tasks.Where(task => task.IsCompletedSuccessfully))
            {
                await task.Result.DisposeAsync().ConfigureAwait(false);
            }

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

    public static async Task ReclaimStaleResourcesAsync(
        string administratorConnectionString,
        CancellationToken cancellationToken)
    {
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        await using var reclaimLease = await AcquireReclaimLeaseAsync(cancellationToken).ConfigureAwait(false);
        var staleDatabases = new List<string>();
        var staleRoles = new List<string>();
        var staleLeaseIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT datname, shobj_description(oid, 'pg_database') FROM pg_catalog.pg_database WHERE datname LIKE 'jellyfin_test_db_%'";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(0);
                    var marker = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (TestResourceNames.IsOwnedDatabase(name) && IsStaleOwnershipMarker(marker, out var leaseId))
                    {
                        staleDatabases.Add(name);
                        _ = staleLeaseIds.Add(leaseId);
                    }
                }
            }

            command.CommandText = "SELECT rolname, shobj_description(oid, 'pg_authid') FROM pg_catalog.pg_roles WHERE rolname LIKE 'jellyfin_test_role_%'";
            await using var roleReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await roleReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = roleReader.GetString(0);
                var marker = roleReader.IsDBNull(1) ? null : roleReader.GetString(1);
                if (TestResourceNames.IsOwnedRole(name) && IsStaleOwnershipMarker(marker, out var leaseId))
                {
                    staleRoles.Add(name);
                    _ = staleLeaseIds.Add(leaseId);
                }
            }
        }

        foreach (var database in staleDatabases)
        {
            var suffix = database[TestResourceNames.DatabasePrefix.Length..];
            var role = TestResourceNames.RolePrefix + suffix;
            await DropResourcesAsync(
                builder.ConnectionString,
                new TestResourceIdentity(database, role, TestResourceNames.ContainerPrefix + Guid.NewGuid().ToString("N")),
                dropDatabase: true,
                dropRole: staleRoles.Contains(role, StringComparer.Ordinal)).ConfigureAwait(false);
            _ = staleRoles.Remove(role);
        }

        foreach (var role in staleRoles)
        {
            await DropResourcesAsync(
                builder.ConnectionString,
                new TestResourceIdentity(TestResourceNames.DatabasePrefix + Guid.NewGuid().ToString("N"), role, TestResourceNames.ContainerPrefix + Guid.NewGuid().ToString("N")),
                dropDatabase: false,
                dropRole: true).ConfigureAwait(false);
        }

        foreach (var leaseId in staleLeaseIds)
        {
            DeleteOwnerLease(leaseId);
        }
    }

    public static async Task ValidateServerMajorAsync(
        string administratorConnectionString,
        int expectedMajor,
        CancellationToken cancellationToken)
    {
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('server_version_num')::integer";
        var versionNumber = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (versionNumber / 10000 != expectedMajor)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL integration fixture requires major version {expectedMajor}, but the configured server reports major {versionNumber / 10000}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _runtimeDataSource.DisposeAsync().ConfigureAwait(false);
            await DropResourcesAsync(_administratorConnectionString, Identity, dropDatabase: true, dropRole: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Unable to remove test-only database '{DatabaseName}' and role '{RoleName}'. Remove only those exact resources before rerunning.");
        }
        finally
        {
            _ownerLease.Dispose();
            DeleteOwnerLease(_leaseId);
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

    private static string CurrentOwnershipMarker(string leaseId)
        => OwnershipMarkerPrefix + GetScope() + "|" + leaseId;

    private static bool IsStaleOwnershipMarker(string? marker, out string leaseId)
    {
        leaseId = string.Empty;
        if (marker is null || !marker.StartsWith(OwnershipMarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = marker.Split('|');
        if (fields.Length != 3
            || !string.Equals(fields[1], GetScope(), StringComparison.Ordinal)
            || fields[2].Length != 32
            || !fields[2].All(Uri.IsHexDigit))
        {
            return false;
        }

        leaseId = fields[2];
        try
        {
            using var lease = AcquireOwnerLease(leaseId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static FileStream AcquireOwnerLease(string leaseId)
    {
        var path = GetLeasePath("database-owners", leaseId + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<FileStream> AcquireReclaimLeaseAsync(CancellationToken cancellationToken)
    {
        var path = GetLeasePath("database-reclaim", GetScope() + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 99)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Could not acquire the scoped stale-database reclamation lease.");
    }

    private static void DeleteOwnerLease(string leaseId)
    {
        var path = GetLeasePath("database-owners", leaseId + ".lock");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetLeasePath(string category, string name)
        => Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", category, name);

    private static string GetScope()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
    }

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
