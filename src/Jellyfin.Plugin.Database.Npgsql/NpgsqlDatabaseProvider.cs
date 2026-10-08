using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Minimal provider used to test Jellyfin's custom database provider loading contract.
/// </summary>
public sealed class NpgsqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupSchemaPrefix = "jellyfin_migration_backup_";
    private const string ForeignKeyMetadataTable = "__JellyfinMigrationBackupForeignKeys";
    private static readonly Regex BackupKeyPattern = new(
        "^" + BackupSchemaPrefix + "[0-9a-f]{32}$",
        RegexOptions.CultureInvariant);
    private readonly ILogger<NpgsqlDatabaseProvider> _logger;
    private string? _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="logger">The host logger.</param>
    public NpgsqlDatabaseProvider(ILogger<NpgsqlDatabaseProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc />
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var hostDatabaseAssembly = typeof(JellyfinDbContext).Assembly;
        var hostDatabaseVersion = hostDatabaseAssembly.GetName().Version
            ?? throw new InvalidOperationException("Jellyfin.Database.Implementations did not expose an assembly version.");
        if (!IsSupportedHostDatabaseAbi(hostDatabaseVersion))
        {
            throw new NotSupportedException(
                $"Jellyfin.Plugin.Database.Npgsql 0.0.1 supports Jellyfin database ABI 12.2.x and 13.0.x; the host loaded {hostDatabaseVersion}. "
                + "The provider stopped before opening or changing the database.");
        }

        var connectionString = databaseConfiguration.CustomProviderOptions?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The Npgsql feasibility provider requires CustomProviderOptions.ConnectionString.");
        }

        _connectionString = connectionString;

        _logger.LogInformation(
            "Npgsql feasibility provider loaded from {AssemblyLocation} in {LoadContext}",
            GetType().Assembly.Location,
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(GetType().Assembly)?.Name ?? "Default");

        options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(Assembly.GetExecutingAssembly().GetName().Name));
        options.ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>();

        LogAssembly("provider", GetType().Assembly);
        LogAssembly("host database", hostDatabaseAssembly);
        LogAssembly("EF Core", typeof(DbContext).Assembly);
        LogAssembly("Npgsql EF", typeof(NpgsqlDbContextOptionsBuilderExtensions).Assembly);
        LogAssembly("Npgsql", typeof(NpgsqlConnection).Assembly);
    }

    /// <inheritdoc />
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc />
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc />
    public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var backupSchema = BackupSchemaPrefix + Guid.NewGuid().ToString("N");
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, $"CREATE SCHEMA {Quote(backupSchema)}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"SET LOCAL search_path TO {Quote(backupSchema)}, public", cancellationToken).ConfigureAwait(false);

            var tableNames = await ReadStringsAsync(
                connection,
                transaction,
                "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'public' ORDER BY tablename",
                cancellationToken).ConfigureAwait(false);

            foreach (var tableName in tableNames)
            {
                var source = $"public.{Quote(tableName)}";
                var target = $"{Quote(backupSchema)}.{Quote(tableName)}";
                await ExecuteAsync(connection, transaction, $"CREATE TABLE {target} (LIKE {source} INCLUDING ALL)", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, $"INSERT INTO {target} OVERRIDING SYSTEM VALUE SELECT * FROM {source}", cancellationToken).ConfigureAwait(false);
            }

            var foreignKeys = await ReadRowsAsync(
                connection,
                transaction,
                """
                SELECT relation.relname, constraint_record.conname, pg_get_constraintdef(constraint_record.oid)
                FROM pg_catalog.pg_constraint AS constraint_record
                JOIN pg_catalog.pg_class AS relation ON relation.oid = constraint_record.conrelid
                JOIN pg_catalog.pg_namespace AS namespace_record ON namespace_record.oid = relation.relnamespace
                WHERE namespace_record.nspname = 'public' AND constraint_record.contype = 'f'
                ORDER BY relation.relname, constraint_record.conname
                """,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE TABLE {Quote(backupSchema)}.{Quote(ForeignKeyMetadataTable)} (\"TableName\" text NOT NULL, \"ConstraintName\" text NOT NULL, \"Definition\" text NOT NULL)",
                cancellationToken).ConfigureAwait(false);
            foreach (var foreignKey in foreignKeys)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {Quote(backupSchema)}.{Quote(ForeignKeyMetadataTable)} VALUES ({Literal(foreignKey[0])}, {Literal(foreignKey[1])}, {Literal(foreignKey[2])})",
                    cancellationToken).ConfigureAwait(false);
            }

            var identityColumns = await ReadRowsAsync(
                connection,
                transaction,
                """
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND is_identity = 'YES'
                ORDER BY table_name, column_name
                """,
                cancellationToken).ConfigureAwait(false);

            foreach (var identityColumn in identityColumns)
            {
                var target = $"{Quote(backupSchema)}.{Quote(identityColumn[0])}";
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"SELECT setval(pg_get_serial_sequence('{target.Replace("'", "''", StringComparison.Ordinal)}', '{identityColumn[1].Replace("'", "''", StringComparison.Ordinal)}'), COALESCE(MAX({Quote(identityColumn[1])}), 1), COUNT(*) > 0) FROM {target}",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created PostgreSQL migration snapshot in schema {BackupSchema} with {TableCount} tables", backupSchema, tableNames.Count);
            return backupSchema;
        }
    }

    /// <inheritdoc />
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        ValidateBackupKey(key);
        var connection = CreateConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DROP SCHEMA public CASCADE", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"ALTER SCHEMA {Quote(key)} RENAME TO public", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "SET LOCAL search_path TO public", cancellationToken).ConfigureAwait(false);
            var foreignKeys = await ReadRowsAsync(
                connection,
                transaction,
                $"SELECT \"TableName\", \"ConstraintName\", \"Definition\" FROM public.{Quote(ForeignKeyMetadataTable)} ORDER BY \"TableName\", \"ConstraintName\"",
                cancellationToken).ConfigureAwait(false);
            foreach (var foreignKey in foreignKeys)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"ALTER TABLE public.{Quote(foreignKey[0])} ADD CONSTRAINT {Quote(foreignKey[1])} {foreignKey[2]}",
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, $"DROP TABLE public.{Quote(ForeignKeyMetadataTable)}", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Restored PostgreSQL migration snapshot {BackupSchema}", key);
        }
    }

    /// <inheritdoc />
    public async Task DeleteBackup(string key)
    {
        ValidateBackupKey(key);
        var connection = CreateConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"DROP SCHEMA {Quote(key)} CASCADE", CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Deleted PostgreSQL migration snapshot {BackupSchema}", key);
        }
    }

    /// <inheritdoc />
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => Task.CompletedTask;

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Literal(string value) => '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';

    private static bool IsSupportedHostDatabaseAbi(Version version)
        => (version.Major == 12 && version.Minor == 2)
            || (version.Major == 13 && version.Minor == 0);

    private void LogAssembly(string role, Assembly assembly)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
        _logger.LogInformation(
            "Npgsql feasibility {AssemblyRole}: {AssemblyName} from {AssemblyLocation} in load context {LoadContext}",
            role,
            assembly.GetName(),
            assembly.Location,
            loadContext?.Name ?? "Default");
    }

    private static void ValidateBackupKey(string key)
    {
        if (!BackupKeyPattern.IsMatch(key))
        {
            throw new ArgumentException("The migration backup key is invalid.", nameof(key));
        }
    }

    private NpgsqlConnection CreateConnection()
        => new(_connectionString ?? throw new InvalidOperationException("The Npgsql provider has not been initialized."));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(commandText, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
        => (await ReadRowsAsync(connection, transaction, commandText, cancellationToken).ConfigureAwait(false))
            .Select(row => row[0])
            .ToList();

    private static async Task<List<string[]>> ReadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        await using var command = new NpgsqlCommand(commandText, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.GetString(index);
            }

            rows.Add(row);
        }

        return rows;
    }
}
