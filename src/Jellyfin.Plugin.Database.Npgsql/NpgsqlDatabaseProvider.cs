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
/// Npgsql database provider loaded by Jellyfin's early custom-provider loader.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="NpgsqlDatabaseProvider"/> class.
/// </remarks>
/// <param name="logger">The host logger.</param>
public sealed partial class NpgsqlDatabaseProvider(ILogger<NpgsqlDatabaseProvider> logger) : IJellyfinDatabaseProvider
{
    private const string BackupSchemaPrefix = "jellyfin_migration_backup_";
    private const string ForeignKeyMetadataTable = "__JellyfinMigrationBackupForeignKeys";
    private static readonly Regex BackupKeyPattern = MyRegex();

    private readonly ILogger<NpgsqlDatabaseProvider> logger = logger;
    private string? connectionString;

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc />
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        var hostDatabaseAssembly = typeof(JellyfinDbContext).Assembly;
        var hostDatabaseVersion = hostDatabaseAssembly.GetName().Version
            ?? throw new InvalidOperationException("Jellyfin.Database.Implementations did not expose an assembly version.");
        if (!SupportsHostDatabaseAbi(hostDatabaseVersion))
        {
            throw new NotSupportedException(
                $"Jellyfin.Plugin.Database.Npgsql {PluginMetadata.Version} supports Jellyfin database ABI {PluginMetadata.SupportedDatabaseAbi}; "
                + $"the host loaded {hostDatabaseVersion}. Install a plugin release that supports this Jellyfin version, or restore the previous "
                + "Jellyfin version and provider package. No database connection was attempted.");
        }

        var customOptions = databaseConfiguration.CustomProviderOptions
            ?? throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions in database.xml. No database connection was attempted.");
        this.ValidateSelectedDirectory(customOptions.PluginName);
        var dependencyAssemblies = LoadAdjacentDependencies();

        var connectionString = customOptions.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions.ConnectionString in database.xml. No database connection was attempted.");
        }

        this.connectionString = connectionString;

        LogProviderLoaded(
            this.logger,
            PluginMetadata.Version,
            this.GetType().Assembly.Location,
            AssemblyLoadContext.GetLoadContext(this.GetType().Assembly)?.Name ?? "Default");

        options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(Assembly.GetExecutingAssembly().GetName().Name));
        options.ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>();

        this.LogAssembly("provider", this.GetType().Assembly);
        this.LogAssembly("host database", hostDatabaseAssembly);
        this.LogAssembly("EF Core", typeof(DbContext).Assembly);
        this.LogAssembly("Npgsql", dependencyAssemblies[0]);
        this.LogAssembly("Npgsql EF", dependencyAssemblies[1]);
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
        var connection = this.CreateConnection();
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
                SELECT table_record.relname, column_record.attname, sequence_namespace.nspname, sequence_record.relname
                FROM pg_catalog.pg_class AS table_record
                JOIN pg_catalog.pg_namespace AS table_namespace ON table_namespace.oid = table_record.relnamespace
                JOIN pg_catalog.pg_attribute AS column_record ON column_record.attrelid = table_record.oid
                JOIN pg_catalog.pg_depend AS dependency_record
                    ON dependency_record.refobjid = table_record.oid
                    AND dependency_record.refobjsubid = column_record.attnum
                    AND dependency_record.classid = 'pg_catalog.pg_class'::regclass
                    AND dependency_record.deptype = 'i'
                JOIN pg_catalog.pg_class AS sequence_record
                    ON sequence_record.oid = dependency_record.objid
                    AND sequence_record.relkind = 'S'
                JOIN pg_catalog.pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence_record.relnamespace
                WHERE table_namespace.nspname = 'public'
                    AND table_record.relkind IN ('r', 'p')
                ORDER BY table_record.relname, column_record.attname
                """,
                cancellationToken).ConfigureAwait(false);

            foreach (var identityColumn in identityColumns)
            {
                var sourceSequence = $"{Quote(identityColumn[2])}.{Quote(identityColumn[3])}";
                var sourceState = await ReadRowsAsync(
                    connection,
                    transaction,
                    $"SELECT last_value::text, is_called::text FROM {sourceSequence}",
                    cancellationToken).ConfigureAwait(false);
                if (sourceState.Count != 1)
                {
                    throw new InvalidOperationException($"Could not read identity sequence state for {sourceSequence}.");
                }

                var targetSequence = await ReadRowsAsync(
                    connection,
                    transaction,
                    $"""
                    SELECT sequence_namespace.nspname, sequence_record.relname
                    FROM pg_catalog.pg_class AS table_record
                    JOIN pg_catalog.pg_namespace AS table_namespace ON table_namespace.oid = table_record.relnamespace
                    JOIN pg_catalog.pg_attribute AS column_record ON column_record.attrelid = table_record.oid
                    JOIN pg_catalog.pg_depend AS dependency_record
                        ON dependency_record.refobjid = table_record.oid
                        AND dependency_record.refobjsubid = column_record.attnum
                        AND dependency_record.classid = 'pg_catalog.pg_class'::regclass
                        AND dependency_record.deptype = 'i'
                    JOIN pg_catalog.pg_class AS sequence_record
                        ON sequence_record.oid = dependency_record.objid
                        AND sequence_record.relkind = 'S'
                    JOIN pg_catalog.pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence_record.relnamespace
                    WHERE table_namespace.nspname = {Literal(backupSchema)}
                        AND table_record.relname = {Literal(identityColumn[0])}
                        AND column_record.attname = {Literal(identityColumn[1])}
                    """,
                    cancellationToken).ConfigureAwait(false);
                if (targetSequence.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Could not identify the copied identity sequence for {backupSchema}.{identityColumn[0]}.{identityColumn[1]}.");
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"SELECT setval({Literal(Quote(targetSequence[0][0]) + "." + Quote(targetSequence[0][1]))}::regclass, {sourceState[0][0]}, {sourceState[0][1]})",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogBackupCreated(this.logger, backupSchema, tableNames.Count);
            return backupSchema;
        }
    }

    /// <inheritdoc />
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateBackupKey(key);
        var connection = this.CreateConnection();
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
            LogBackupRestored(this.logger, key);
        }
    }

    /// <inheritdoc />
    public async Task DeleteBackup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateBackupKey(key);
        var connection = this.CreateConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"DROP SCHEMA {Quote(key)} CASCADE", CancellationToken.None).ConfigureAwait(false);
            LogBackupDeleted(this.logger, key);
        }
    }

    /// <inheritdoc />
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => Task.CompletedTask;

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Literal(string value) => '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';

    /// <summary>
    /// Determines whether a host database assembly belongs to this release's supported ABI family.
    /// </summary>
    /// <param name="version">The host database assembly version.</param>
    /// <returns><see langword="true" /> when the ABI is supported.</returns>
    internal static bool SupportsHostDatabaseAbi(Version version)
        => version.Major == 12 && version.Minor == 2;

    private void ValidateSelectedDirectory(string pluginName)
    {
        var providerAssembly = this.GetType().Assembly;
        var loadContext = AssemblyLoadContext.GetLoadContext(providerAssembly);
        if (loadContext != AssemblyLoadContext.Default)
        {
            throw new InvalidOperationException(
                "The Npgsql database provider was instantiated from Jellyfin's later collectible plugin context instead of the early default "
                + "load context. Keep DatabaseType set to PLUGIN_PROVIDER and do not rely on plugin service registration. No database connection was attempted.");
        }

        var selectedDirectory = Path.GetFileName(Path.GetDirectoryName(providerAssembly.Location));
        if (string.IsNullOrWhiteSpace(pluginName)
            || !string.Equals(pluginName, PluginMetadata.InstallationDirectory, StringComparison.Ordinal)
            || !string.Equals(selectedDirectory, pluginName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Jellyfin selected the Npgsql provider from folder '{selectedDirectory}', but this release requires the exact folder and PluginName "
                + $"'{PluginMetadata.InstallationDirectory}'. Remove ambiguous prefix-matching folders, restore the complete release ZIP there, update "
                + "database.xml atomically, and restart. No database connection was attempted.");
        }
    }

    private static Assembly[] LoadAdjacentDependencies()
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(NpgsqlDatabaseProvider).Assembly.Location)
            ?? throw new InvalidOperationException("The Npgsql provider assembly has no installation directory.");
        var dependencies = new[]
        {
            "Npgsql.dll",
            "Npgsql.EntityFrameworkCore.PostgreSQL.dll",
        };
        var loaded = new Assembly[dependencies.Length];

        for (var index = 0; index < dependencies.Length; index++)
        {
            var dependencyPath = Path.Combine(pluginDirectory, dependencies[index]);
            if (!File.Exists(dependencyPath))
            {
                throw MissingDependency(dependencies[index], pluginDirectory, null);
            }

            try
            {
                loaded[index] = Assembly.LoadFrom(dependencyPath);
            }
            catch (Exception exception) when (exception is FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                throw MissingDependency(dependencies[index], pluginDirectory, exception);
            }

            var actualDirectory = Path.GetDirectoryName(loaded[index].Location);
            if (!string.Equals(actualDirectory, pluginDirectory, StringComparison.Ordinal)
                || AssemblyLoadContext.GetLoadContext(loaded[index]) != AssemblyLoadContext.Default)
            {
                throw new InvalidOperationException(
                    $"Required dependency '{dependencies[index]}' resolved from '{loaded[index].Location}' instead of the Npgsql plugin folder "
                    + $"'{pluginDirectory}' in the default load context. Remove stray copies, restore the complete release ZIP, and restart. "
                    + "No database connection was attempted.");
            }
        }

        return loaded;
    }

    private static InvalidOperationException MissingDependency(string dependency, string pluginDirectory, Exception? innerException)
        => new(
            $"Required Npgsql dependency '{dependency}' is missing or incompatible in '{pluginDirectory}'. Restore the complete "
            + $"Jellyfin.Plugin.Database.Npgsql {PluginMetadata.Version} ZIP in '{PluginMetadata.InstallationDirectory}', or reinstall the previous "
            + "compatible package before restarting. No database connection was attempted.",
            innerException);

    private void LogAssembly(string role, Assembly assembly)
    {
        if (!this.logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
#pragma warning disable CA1873 // IsEnabled above prevents evaluation when information logging is disabled.
        LogAssemblyOrigin(
            this.logger,
            role,
            assembly.GetName().ToString(),
            assembly.Location,
            loadContext?.Name ?? "Default");
#pragma warning restore CA1873
    }

    private static void ValidateBackupKey(string key)
    {
        if (!BackupKeyPattern.IsMatch(key))
        {
            throw new ArgumentException("The migration backup key is invalid.", nameof(key));
        }
    }

    private NpgsqlConnection CreateConnection()
        => new(this.connectionString ?? throw new InvalidOperationException("The Npgsql provider has not been initialized."));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(commandText, connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
        => [.. (await ReadRowsAsync(connection, transaction, commandText, cancellationToken).ConfigureAwait(false)).Select(row => row[0])];

    private static async Task<List<string[]>> ReadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        var command = new NpgsqlCommand(commandText, connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new string[reader.FieldCount];
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        row[index] = reader.GetString(index);
                    }

                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    [GeneratedRegex("^jellyfin_migration_backup_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Npgsql database provider {PluginVersion} loaded from {AssemblyLocation} in {LoadContext}")]
    private static partial void LogProviderLoaded(ILogger logger, string pluginVersion, string assemblyLocation, string loadContext);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Created PostgreSQL migration snapshot in schema {BackupSchema} with {TableCount} tables")]
    private static partial void LogBackupCreated(ILogger logger, string backupSchema, int tableCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Restored PostgreSQL migration snapshot {BackupSchema}")]
    private static partial void LogBackupRestored(ILogger logger, string backupSchema);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Deleted PostgreSQL migration snapshot {BackupSchema}")]
    private static partial void LogBackupDeleted(ILogger logger, string backupSchema);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Npgsql provider {AssemblyRole}: {AssemblyName} from {AssemblyLocation} in load context {LoadContext}")]
    private static partial void LogAssemblyOrigin(
        ILogger logger,
        string assemblyRole,
        string assemblyName,
        string assemblyLocation,
        string loadContext);
}
