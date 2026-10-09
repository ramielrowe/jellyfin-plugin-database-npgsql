using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>
/// Holds the cooperative, cluster-wide mutation lease used by every harness actor.
/// PostgreSQL drops databases by name and cannot atomically bind DROP DATABASE to
/// an OID, so this lease is safe only on the explicitly exclusive test clusters
/// validated here. A malicious or non-cooperating superuser can still defeat it.
/// </summary>
public sealed class PostgreSqlClusterLease : IAsyncDisposable
{
    public const string ExternalDatabaseName = "jellyfin_npgsql_test_admin";
    public const string ExternalMarker = "jellyfin-npgsql-exclusive-test-cluster-v1";
    private const int AdvisoryKeyNamespace = 0x4A464E50; // JFNP
    private const int AdvisoryKeyResource = 0x47544553; // GTES
    private readonly NpgsqlConnection _connection;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private PostgreSqlClusterLease(NpgsqlConnection connection, string connectionString, string clusterIdentity)
    {
        _connection = connection;
        _connectionString = connectionString;
        ClusterIdentity = clusterIdentity;
    }

    public string ConnectionString => _connectionString;

    /// <summary>Gets PostgreSQL's stable system identifier for local lease-path namespacing.</summary>
    public string ClusterIdentity { get; }

    public static async Task<PostgreSqlClusterLease> AcquireAsync(
        string administratorConnectionString,
        bool harnessOwnsContainer,
        bool externalExclusiveOptIn,
        CancellationToken cancellationToken)
    {
        var builder = PostgreSqlTestDatabase.ValidateAdministratorConnectionString(administratorConnectionString);
        if (!harnessOwnsContainer)
        {
            if (!externalExclusiveOptIn)
            {
                throw new InvalidOperationException(
                    $"Automatic PostgreSQL test resource creation and cleanup on an external cluster requires "
                    + $"{PostgreSqlTestOptions.ExternalExclusiveEnvironmentVariable}=true, a database named '{ExternalDatabaseName}', "
                    + $"and its database comment set to '{ExternalMarker}'. No mutation was attempted.");
            }

            if (!string.Equals(builder.Database, ExternalDatabaseName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The exclusive external test cluster must use the exact administrator database '{ExternalDatabaseName}'. No mutation was attempted.");
            }
        }

        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock($1, $2)";
            command.Parameters.AddWithValue(AdvisoryKeyNamespace);
            command.Parameters.AddWithValue(AdvisoryKeyResource);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException(
                    "Another Npgsql test harness owns the PostgreSQL cluster mutation lease; no mutation was attempted.");
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT current_setting('server_version_num')::integer, "
                + "rolsuper OR (rolcreatedb AND rolcreaterole), "
                + "shobj_description((SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database()), 'pg_database') "
                + "FROM pg_catalog.pg_roles WHERE rolname = current_user";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || reader.GetInt32(0) != 160006
                    || !reader.GetBoolean(1))
                {
                    throw new InvalidOperationException(
                        "The exclusive PostgreSQL test cluster must be exact version 16.6 and the administrator must have CREATEDB and CREATEROLE. No mutation was attempted.");
                }

                if (!harnessOwnsContainer
                    && (reader.IsDBNull(2) || !string.Equals(reader.GetString(2), ExternalMarker, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"External database '{ExternalDatabaseName}' must have the exact exclusivity comment '{ExternalMarker}'. No mutation was attempted.");
                }
            }

            if (!harnessOwnsContainer)
            {
                command.CommandText = "SELECT count(*) FROM pg_catalog.pg_stat_activity a "
                    + "JOIN pg_catalog.pg_roles r ON r.rolname = a.usename "
                    + "WHERE a.pid <> pg_backend_pid() AND a.backend_type = 'client backend' "
                    + "AND (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole)";
                var competingPrivilegedSessions = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (competingPrivilegedSessions != 0)
                {
                    throw new InvalidOperationException(
                        "The external test cluster has another privileged client session; exclusivity could not be verified and no mutation was attempted.");
                }
            }

            command.CommandText = "SELECT system_identifier::text FROM pg_catalog.pg_control_system()";
            var clusterIdentity = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (clusterIdentity is null
                || clusterIdentity.Length is < 10 or > 32
                || !clusterIdentity.All(char.IsAsciiDigit))
            {
                throw new InvalidOperationException(
                    "The exclusive PostgreSQL test cluster did not return one stable numeric system identifier; no mutation was attempted.");
            }

            return new PostgreSqlClusterLease(connection, builder.ConnectionString, clusterIdentity);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<T> RunAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The PostgreSQL cluster mutation lease connection is no longer open; refusing mutation.");
            }

            return await action(_connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<NpgsqlConnection, CancellationToken, Task> action, CancellationToken cancellationToken)
        => RunAsync(
            async (connection, token) =>
            {
                await action(connection, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
