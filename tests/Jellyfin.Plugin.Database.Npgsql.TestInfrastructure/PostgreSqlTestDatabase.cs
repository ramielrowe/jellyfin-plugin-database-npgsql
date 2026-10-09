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
    private readonly PostgreSqlClusterLease _clusterLease;
    private readonly NpgsqlDataSource _runtimeDataSource;
    private readonly string _leaseId;
    private readonly FileStream _ownerLease;
    private readonly uint _databaseOid;
    private readonly uint _roleOid;
    private bool _disposed;

    private PostgreSqlTestDatabase(
        PostgreSqlClusterLease clusterLease,
        string runtimeConnectionString,
        TestResourceIdentity identity,
        NpgsqlDataSource runtimeDataSource,
        string leaseId,
        uint databaseOid,
        uint roleOid,
        FileStream ownerLease)
    {
        _clusterLease = clusterLease;
        RuntimeConnectionString = runtimeConnectionString;
        Identity = identity;
        _runtimeDataSource = runtimeDataSource;
        _leaseId = leaseId;
        _databaseOid = databaseOid;
        _roleOid = roleOid;
        _ownerLease = ownerLease;
    }

    public TestResourceIdentity Identity { get; }

    public string DatabaseName => Identity.DatabaseName;

    public string RoleName => Identity.RoleName;

    public string RuntimeConnectionString { get; }

    public string SanitizedRuntimeConnectionString => SensitiveDataRedactor.RedactConnectionString(RuntimeConnectionString);

    public static string TestScope => GetScope();

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        PostgreSqlClusterLease clusterLease,
        string? workerId,
        CancellationToken cancellationToken,
        Action<TestResourceIdentity>? resourceCreated = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(clusterLease);
        var administratorBuilder = ValidateAdministratorConnectionString(clusterLease.ConnectionString);
        var identity = TestResourceNames.Create(workerId);
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var leaseId = Guid.NewGuid().ToString("N");
        var ownerLease = AcquireOwnerLease(clusterLease.ClusterIdentity, leaseId);
        var ownershipTransferred = false;
        var roleCreated = false;
        var databaseCreated = false;
        uint? roleOid = null;
        uint? databaseOid = null;
        NpgsqlDataSource? runtimeDataSource = null;

        try
        {
            await clusterLease.RunAsync(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE ROLE " + QuoteOwnedRole(identity.RoleName)
                    + " LOGIN PASSWORD " + QuoteLiteral(runtimePassword)
                    + " NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT "
                    + RuntimeConnectionLimit;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                roleCreated = true;
                command.CommandText = "COMMENT ON ROLE " + QuoteOwnedRole(identity.RoleName)
                    + " IS " + QuoteLiteral(CurrentOwnershipMarker(leaseId));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                command.CommandText = "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = $1";
                command.Parameters.AddWithValue(identity.RoleName);
                roleOid = (uint)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                command.Parameters.Clear();

                command.CommandText = "CREATE DATABASE " + QuoteOwnedDatabase(identity.DatabaseName)
                    + " OWNER " + QuoteOwnedRole(identity.RoleName)
                    + " CONNECTION LIMIT = 8";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                databaseCreated = true;
                command.CommandText = "COMMENT ON DATABASE " + QuoteOwnedDatabase(identity.DatabaseName)
                    + " IS " + QuoteLiteral(CurrentOwnershipMarker(leaseId));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                command.CommandText = "SELECT oid FROM pg_catalog.pg_database WHERE datname = $1 AND datdba::bigint = $2";
                command.Parameters.AddWithValue(identity.DatabaseName);
                command.Parameters.AddWithValue((long)roleOid.Value);
                databaseOid = (uint)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                command.Parameters.Clear();
            }, cancellationToken).ConfigureAwait(false);

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
                clusterLease,
                runtimeConnectionString,
                identity,
                runtimeDataSource,
                leaseId,
                databaseOid.GetValueOrDefault(),
                roleOid.GetValueOrDefault(),
                ownerLease);
            ownershipTransferred = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            await CleanupFailedCreationAsync(
                clusterLease,
                runtimeDataSource,
                identity,
                databaseCreated,
                roleCreated,
                leaseId,
                databaseOid,
                roleOid).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            await CleanupFailedCreationAsync(
                clusterLease,
                runtimeDataSource,
                identity,
                databaseCreated,
                roleCreated,
                leaseId,
                databaseOid,
                roleOid).ConfigureAwait(false);
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
                clusterLease,
                runtimeDataSource,
                identity,
                databaseCreated,
                roleCreated,
                leaseId,
                databaseOid,
                roleOid).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                ownerLease.Dispose();
            }
        }
    }

    public static async Task<PostgreSqlTestDatabase[]> CreateManyAsync(
        PostgreSqlClusterLease clusterLease,
        IEnumerable<string> workerIds,
        CancellationToken cancellationToken,
        Action<string, TestResourceIdentity>? resourceCreated = null)
    {
        var tasks = workerIds.Select(workerId => CreateAsync(
            clusterLease,
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
        PostgreSqlClusterLease clusterLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clusterLease);
        await clusterLease.RunAsync(async (connection, token) =>
        {
            var staleResources = new List<(TestResourceIdentity Identity, string LeaseId, uint DatabaseOid, uint RoleOid)>();
            var staleRoles = new List<(string RoleName, string LeaseId, uint RoleOid)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT d.datname, r.rolname, shobj_description(d.oid, 'pg_database'), "
                    + "shobj_description(r.oid, 'pg_authid'), d.oid, r.oid FROM pg_catalog.pg_database d "
                    + "JOIN pg_catalog.pg_roles r ON r.oid = d.datdba "
                    + "WHERE d.datname LIKE 'jellyfin_test_db_%' AND r.rolname LIKE 'jellyfin_test_role_%'";
                await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var name = reader.GetString(0);
                        var role = reader.GetString(1);
                        var databaseMarker = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var roleMarker = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var suffix = name[TestResourceNames.DatabasePrefix.Length..];
                        if (TestResourceNames.IsOwnedDatabase(name)
                            && TestResourceNames.IsOwnedRole(role)
                            && string.Equals(role, TestResourceNames.RolePrefix + suffix, StringComparison.Ordinal)
                            && string.Equals(databaseMarker, roleMarker, StringComparison.Ordinal)
                            && IsStaleOwnershipMarker(databaseMarker, clusterLease.ClusterIdentity, out var leaseId))
                        {
                            staleResources.Add((
                                new TestResourceIdentity(name, role, TestResourceNames.ContainerPrefix + Guid.NewGuid().ToString("N")),
                                leaseId,
                                reader.GetFieldValue<uint>(4),
                                reader.GetFieldValue<uint>(5)));
                        }
                    }
                }

                command.CommandText = "SELECT r.rolname, shobj_description(r.oid, 'pg_authid'), r.oid "
                    + "FROM pg_catalog.pg_roles r LEFT JOIN pg_catalog.pg_database d ON d.datdba = r.oid "
                    + "WHERE r.rolname LIKE 'jellyfin_test_role_%' AND d.oid IS NULL";
                await using var roleReader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await roleReader.ReadAsync(token).ConfigureAwait(false))
                {
                    var roleName = roleReader.GetString(0);
                    var marker = roleReader.IsDBNull(1) ? null : roleReader.GetString(1);
                    if (TestResourceNames.IsOwnedRole(roleName)
                        && IsStaleOwnershipMarker(marker, clusterLease.ClusterIdentity, out var leaseId))
                    {
                        staleRoles.Add((roleName, leaseId, roleReader.GetFieldValue<uint>(2)));
                    }
                }
            }

            foreach (var stale in staleResources)
            {
                using var staleLease = AcquireOwnerLease(clusterLease.ClusterIdentity, stale.LeaseId);
                await DropResourcesUnderLeaseAsync(
                    connection,
                    stale.Identity,
                    true,
                    true,
                    stale.LeaseId,
                    stale.DatabaseOid,
                    stale.RoleOid).ConfigureAwait(false);
            }

            foreach (var staleRole in staleRoles)
            {
                using var staleLease = AcquireOwnerLease(clusterLease.ClusterIdentity, staleRole.LeaseId);
                await DropResourcesUnderLeaseAsync(
                    connection,
                    new TestResourceIdentity(
                        TestResourceNames.DatabasePrefix + Guid.NewGuid().ToString("N"),
                        staleRole.RoleName,
                        TestResourceNames.ContainerPrefix + Guid.NewGuid().ToString("N")),
                    false,
                    true,
                    staleRole.LeaseId,
                    null,
                    staleRole.RoleOid).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ValidateServerVersionAsync(
        string administratorConnectionString,
        int expectedVersionNumber,
        CancellationToken cancellationToken)
    {
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('server_version_num')::integer";
        var versionNumber = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (versionNumber != expectedVersionNumber)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL integration fixture requires server_version_num {expectedVersionNumber}, but the configured server reports {versionNumber}.");
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
            await DropResourcesAsync(_clusterLease, Identity, true, true, _leaseId, _databaseOid, _roleOid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Unable to remove test-only database '{DatabaseName}' and role '{RoleName}'. Remove only those exact resources before rerunning.");
        }
        finally
        {
            _ownerLease.Dispose();
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

    private static bool IsStaleOwnershipMarker(string? marker, string clusterIdentity, out string leaseId)
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
            using var lease = AcquireOwnerLease(clusterIdentity, leaseId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static FileStream AcquireOwnerLease(string clusterIdentity, string leaseId)
    {
        if (clusterIdentity.Length is < 10 or > 32 || !clusterIdentity.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException("The PostgreSQL cluster lease namespace is malformed.");
        }

        var path = GetLeasePath("database-owners", clusterIdentity, leaseId + ".lock");
        return SecureFileSystem.OpenLease(path);
    }

    private static string GetLeasePath(string category, string clusterIdentity, string name)
        => Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", category, clusterIdentity, name);

    private static string GetScope()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
    }

    private static async Task CleanupFailedCreationAsync(
        PostgreSqlClusterLease clusterLease,
        NpgsqlDataSource? runtimeDataSource,
        TestResourceIdentity identity,
        bool dropDatabase,
        bool dropRole,
        string leaseId,
        uint? databaseOid,
        uint? roleOid)
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
            await DropResourcesAsync(
                clusterLease,
                identity,
                dropDatabase,
                dropRole,
                leaseId,
                databaseOid,
                roleOid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Setup failed and cleanup could not remove test-only database '{identity.DatabaseName}' and role '{identity.RoleName}'. "
                + "Remove only those exact resources before rerunning.");
        }
    }

    private static async Task DropResourcesAsync(
        PostgreSqlClusterLease clusterLease,
        TestResourceIdentity identity,
        bool dropDatabase,
        bool dropRole,
        string leaseId,
        uint? expectedDatabaseOid,
        uint? expectedRoleOid)
    {
        TestResourceNames.EnsureOwnedDatabase(identity.DatabaseName);
        TestResourceNames.EnsureOwnedRole(identity.RoleName);
        await clusterLease.RunAsync(
            (connection, _) => DropResourcesUnderLeaseAsync(
                connection,
                identity,
                dropDatabase,
                dropRole,
                leaseId,
                expectedDatabaseOid,
                expectedRoleOid),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DropResourcesUnderLeaseAsync(
        NpgsqlConnection connection,
        TestResourceIdentity identity,
        bool dropDatabase,
        bool dropRole,
        string leaseId,
        uint? expectedDatabaseOid,
        uint? expectedRoleOid)
    {
        TestResourceNames.EnsureOwnedDatabase(identity.DatabaseName);
        TestResourceNames.EnsureOwnedRole(identity.RoleName);
        var expectedMarker = CurrentOwnershipMarker(leaseId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT oid, datdba, shobj_description(oid, 'pg_database') "
            + "FROM pg_catalog.pg_database WHERE datname = $1";
        command.Parameters.AddWithValue(identity.DatabaseName);
        uint? databaseOid = null;
        uint? databaseOwnerOid = null;
        string? databaseMarker = null;
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                databaseOid = reader.GetFieldValue<uint>(0);
                databaseOwnerOid = reader.GetFieldValue<uint>(1);
                databaseMarker = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT oid, shobj_description(oid, 'pg_authid') FROM pg_catalog.pg_roles WHERE rolname = $1";
        command.Parameters.AddWithValue(identity.RoleName);
        uint? roleOid = null;
        string? roleMarker = null;
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                roleOid = reader.GetFieldValue<uint>(0);
                roleMarker = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        if (databaseOid.HasValue
            && (!roleOid.HasValue
                || databaseOwnerOid != roleOid
                || (expectedDatabaseOid.HasValue && databaseOid != expectedDatabaseOid)
                || (expectedRoleOid.HasValue && roleOid != expectedRoleOid)
                || !string.Equals(databaseMarker, expectedMarker, StringComparison.Ordinal)
                || !string.Equals(roleMarker, expectedMarker, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Refusing cleanup of PostgreSQL database '{identity.DatabaseName}' and role '{identity.RoleName}': "
                + "their exact OIDs, paired ownership, and lease markers do not match this harness lease.");
        }

        if (!databaseOid.HasValue
            && roleOid.HasValue
            && ((expectedRoleOid.HasValue && roleOid != expectedRoleOid)
                || !string.Equals(roleMarker, expectedMarker, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Refusing cleanup of PostgreSQL role '{identity.RoleName}': its exact OID and lease marker do not match this harness lease.");
        }

        command.Parameters.Clear();
        if (dropDatabase && databaseOid.HasValue)
        {
            var tombstone = TestResourceNames.Create("drop").DatabaseName;
            command.CommandText = "ALTER DATABASE " + QuoteOwnedDatabase(identity.DatabaseName) + " RENAME TO " + QuoteOwnedDatabase(tombstone);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            command.CommandText = "SELECT d.datdba, shobj_description(d.oid, 'pg_database'), r.oid, "
                + "shobj_description(r.oid, 'pg_authid') FROM pg_catalog.pg_database d "
                + "JOIN pg_catalog.pg_roles r ON r.oid = d.datdba "
                + "WHERE d.datname = $1 AND d.oid::bigint = $2 AND r.rolname = $3";
            command.Parameters.AddWithValue(tombstone);
            command.Parameters.AddWithValue((long)databaseOid.Value);
            command.Parameters.AddWithValue(identity.RoleName);
            await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false)
                    || reader.GetFieldValue<uint>(0) != roleOid
                    || reader.IsDBNull(1)
                    || !string.Equals(reader.GetString(1), expectedMarker, StringComparison.Ordinal)
                    || reader.GetFieldValue<uint>(2) != roleOid
                    || reader.IsDBNull(3)
                    || !string.Equals(reader.GetString(3), expectedMarker, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Refusing to drop claimed PostgreSQL database '{tombstone}' (originally '{identity.DatabaseName}'): "
                        + "its exact OID, owner, or lease marker changed.");
                }
            }

            command.Parameters.Clear();
            command.CommandText = "DROP DATABASE " + QuoteOwnedDatabase(tombstone) + " WITH (FORCE)";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (dropRole && roleOid.HasValue)
        {
            var roleTombstone = TestResourceNames.Create("drop").RoleName;
            command.Parameters.Clear();
            command.CommandText = "SELECT shobj_description(oid, 'pg_authid') FROM pg_catalog.pg_roles "
                + "WHERE rolname = $1 AND oid::bigint = $2";
            command.Parameters.AddWithValue(identity.RoleName);
            command.Parameters.AddWithValue((long)roleOid.Value);
            var currentMarker = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) as string;
            if (!string.Equals(currentMarker, expectedMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to claim PostgreSQL role '{identity.RoleName}': its exact OID or lease marker changed.");
            }

            command.Parameters.Clear();
            command.CommandText = "ALTER ROLE " + QuoteOwnedRole(identity.RoleName) + " RENAME TO " + QuoteOwnedRole(roleTombstone);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            command.CommandText = "SELECT shobj_description(oid, 'pg_authid') FROM pg_catalog.pg_roles WHERE rolname = $1 AND oid::bigint = $2";
            command.Parameters.AddWithValue(roleTombstone);
            command.Parameters.AddWithValue((long)roleOid.Value);
            var marker = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) as string;
            if (!string.Equals(marker, expectedMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to drop claimed PostgreSQL role '{roleTombstone}' (originally '{identity.RoleName}'): "
                    + "its exact OID or lease marker changed.");
            }

            command.Parameters.Clear();
            command.CommandText = "DROP ROLE " + QuoteOwnedRole(roleTombstone);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
