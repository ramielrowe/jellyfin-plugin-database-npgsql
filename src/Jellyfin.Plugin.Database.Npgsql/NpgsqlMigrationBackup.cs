using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

#pragma warning disable CA2007 // Every substantive asynchronous operation explicitly avoids context capture; await-using disposal has no context overload syntax.
#pragma warning disable CA2100 // Dynamic statements contain only catalog identifiers captured from PostgreSQL and quoted by Quote, or checksum-validated provider-generated SQL.

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Creates and restores provider-owned migration snapshots without external PostgreSQL clients.</summary>
internal static partial class NpgsqlMigrationBackup
{
    private const int FormatVersion = 2;
    private const string BaselineMigration = "20261009061101_InitialPostgreSqlBaseline";
    private const string SuccessorMigration = "20261009061446_AddActivityLogTypeDateIndex";
    private const string ManifestFile = "manifest.json";
    private const string CompletionFile = "complete";
    private const string HistorySchemaSql = """
        CREATE TABLE public."__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """;

    private const long AdvisoryLockKey = 0x4A464E5047424B50;
    private static readonly TimeSpan RestoreDeadline = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Creates a consistent, atomically published snapshot.</summary>
    /// <param name="contextFactory">The initialized Jellyfin context factory.</param>
    /// <param name="connectionString">The validated private PostgreSQL connection string.</param>
    /// <param name="backupRoot">The provider-owned backup root.</param>
    /// <param name="cancellationToken">A token that cancels snapshot creation.</param>
    /// <returns>The opaque completed backup key.</returns>
    public static async Task<string> CreateAsync(
        IDbContextFactory<JellyfinDbContext> contextFactory,
        string connectionString,
        string backupRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? partialDirectory = null;
        string? unpublishedFinalDirectory = null;
        try
        {
            PrepareRoot(backupRoot);
            ScavengePartialDirectories(backupRoot);
            var key = "npgsql-v1-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            partialDirectory = ResolveKeyDirectory(backupRoot, key + ".partial", allowPartial: true);
            Directory.CreateDirectory(partialDirectory);
            SetDirectoryPermissions(partialDirectory);

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);

            var identity = await ReadIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var tables = await ReadTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var empty = tables.Count == 0;
            var historyOnly = IsHistoryOnly(tables);
            if (empty)
            {
                await ValidateMigrationZeroAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
            else if (historyOnly)
            {
                await ValidateHistoryOnlyBaselineAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            var migration = await ReadSourceMigrationAsync(connection, transaction, empty || historyOnly, cancellationToken).ConfigureAwait(false);
            string? schemaSql = null;
            string? schemaFingerprint = null;
            if (historyOnly)
            {
                schemaSql = HistorySchemaSql;
            }
            else if (!empty)
            {
                await ValidateSupportedModelAsync(context.Model, connection, transaction, tables, cancellationToken).ConfigureAwait(false);
                tables = await OrderTablesForRestoreAsync(connection, transaction, tables, cancellationToken).ConfigureAwait(false);
                schemaSql = GenerateSupportedSchemaSql(context, migration!);
                if (string.IsNullOrWhiteSpace(schemaSql))
                {
                    throw new InvalidOperationException("The provider could not generate the rollback schema before migration; no backup was published.");
                }

                schemaFingerprint = await ReadSchemaFingerprintAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await ValidateGeneratedSchemaContractAsync(
                    connection,
                    transaction,
                    schemaSql,
                    schemaFingerprint,
                    cancellationToken).ConfigureAwait(false);
            }

            var tableSnapshots = new List<TableSnapshot>(tables.Count);
            for (var index = 0; index < tables.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = tables[index];
                var copiedColumns = table.Columns.Where(column => string.Equals(column.Generated, "NEVER", StringComparison.Ordinal)).ToArray();
                var fileName = string.Create(CultureInfo.InvariantCulture, $"table-{index:D4}.copy");
                var filePath = Path.Combine(partialDirectory, fileName);
                var copySql = $"COPY {Quote(table.Schema)}.{Quote(table.Name)} ({string.Join(", ", copiedColumns.Select(column => Quote(column.Name)))}) TO STDOUT (FORMAT BINARY)";
                await using (var output = CreatePrivateFile(filePath))
                await using (var copy = await connection.BeginRawBinaryCopyAsync(copySql, cancellationToken).ConfigureAwait(false))
                {
                    await copy.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    NpgsqlBackupFileSystem.FlushFile(output, $"flush-table-{index:D4}");
                }

                tableSnapshots.Add(new TableSnapshot(
                    table.Schema,
                    table.Name,
                    table.Columns,
                    fileName,
                    new FileInfo(filePath).Length,
                    await HashFileAsync(filePath, cancellationToken).ConfigureAwait(false)));
            }

            var sequences = empty
                ? []
                : await ReadSequencesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var foreignKeys = empty
                ? []
                : await ReadForeignKeysAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            schemaFingerprint ??= empty
                ? HashString("empty")
                : await ReadSchemaFingerprintAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            string? schemaFile = null;
            string? schemaHash = null;
            if (schemaSql is not null)
            {
                schemaFile = "schema.sql";
                var schemaPath = Path.Combine(partialDirectory, schemaFile);
                await using (var stream = CreatePrivateFile(schemaPath))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    await writer.WriteAsync(schemaSql.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    NpgsqlBackupFileSystem.FlushFile(stream, "flush-schema");
                }

                schemaHash = await HashFileAsync(schemaPath, cancellationToken).ConfigureAwait(false);
            }

            var manifest = new BackupManifest(
                FormatVersion,
                PluginMetadata.Version,
                DateTimeOffset.UtcNow,
                identity.Database,
                identity.DatabaseOid,
                identity.SystemIdentifier,
                identity.SelectedServer,
                identity.Binding,
                migration,
                empty,
                historyOnly,
                schemaFingerprint,
                schemaFile,
                schemaHash,
                tableSnapshots,
                sequences,
                foreignKeys);
            var manifestPath = Path.Combine(partialDirectory, ManifestFile);
            await using (var stream = CreatePrivateFile(manifestPath))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                NpgsqlBackupFileSystem.FlushFile(stream, "flush-manifest");
            }

            var manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var completionPath = Path.Combine(partialDirectory, CompletionFile);
            await using (var stream = CreatePrivateFile(completionPath))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(manifestHash + "\n"), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                NpgsqlBackupFileSystem.FlushFile(stream, "flush-completion");
            }

            NpgsqlBackupFileSystem.FlushDirectory(partialDirectory, "flush-partial-directory");

            unpublishedFinalDirectory = ResolveKeyDirectory(backupRoot, key, allowPartial: false);
            NpgsqlBackupFileSystem.PublishDirectory(partialDirectory, unpublishedFinalDirectory, backupRoot);
            partialDirectory = null;
            unpublishedFinalDirectory = null;
            return key;
        }
        finally
        {
            if (partialDirectory is not null)
            {
                TryDeleteOwnedDirectory(partialDirectory);
            }

            if (unpublishedFinalDirectory is not null)
            {
                TryDeleteOwnedDirectory(unpublishedFinalDirectory);
            }

            OperationGate.Release();
        }
    }

    /// <summary>Validates and transactionally restores a prior snapshot.</summary>
    /// <param name="connectionString">The validated private PostgreSQL connection string.</param>
    /// <param name="backupRoot">The provider-owned backup root.</param>
    /// <param name="key">The opaque completed backup key.</param>
    /// <param name="cancellationToken">A token that cancels restore before commit.</param>
    /// <returns>A task that represents the restore.</returns>
    public static async Task RestoreAsync(
        string connectionString,
        string backupRoot,
        string key,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await RestoreAsync(dataSource, backupRoot, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates and transactionally restores a prior snapshot through the provider's configured data source.</summary>
    /// <param name="dataSource">The provider-owned data source, including in-memory secret-file configuration.</param>
    /// <param name="backupRoot">The provider-owned backup root.</param>
    /// <param name="key">The opaque completed backup key.</param>
    /// <param name="cancellationToken">A token that cancels restore before commit.</param>
    /// <returns>A task that represents the restore.</returns>
    public static async Task RestoreAsync(
        NpgsqlDataSource dataSource,
        string backupRoot,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RestoreDeadline);
        var operationToken = deadline.Token;
        var gateAcquired = false;
        try
        {
            await OperationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateAcquired = true;
            await using var backup = await StageValidatedBackupAsync(backupRoot, key, operationToken).ConfigureAwait(false);
            var manifest = backup.Manifest;

            dataSource.Clear();
            await using var connection = await dataSource.OpenConnectionAsync(operationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(connection, null, operationToken).ConfigureAwait(false);
            if (!string.Equals(identity.Database, manifest.Database, StringComparison.Ordinal)
                || identity.DatabaseOid != manifest.DatabaseOid
                || !string.Equals(identity.SystemIdentifier, manifest.SystemIdentifier, StringComparison.Ordinal)
                || !string.Equals(identity.SelectedServer, manifest.SelectedServer, StringComparison.Ordinal)
                || !FixedHashEquals(identity.Binding, manifest.TargetBinding))
            {
                throw new InvalidDataException("The PostgreSQL migration backup belongs to a different database target; the database was not changed.");
            }

            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, operationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '25s'; SET LOCAL idle_in_transaction_session_timeout = '25s'",
                operationToken).ConfigureAwait(false);
            await EnsureNoActiveDatabaseUsersAsync(connection, transaction, operationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", operationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public AUTHORIZATION pg_database_owner; GRANT USAGE ON SCHEMA public TO PUBLIC",
                operationToken).ConfigureAwait(false);
            if (!manifest.Empty)
            {
                await ExecuteNonQueryAsync(connection, transaction, backup.SchemaSql!, operationToken).ConfigureAwait(false);
                foreach (var foreignKey in manifest.ForeignKeys)
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE {Quote(foreignKey.Schema)}.{Quote(foreignKey.Table)} ALTER CONSTRAINT {Quote(foreignKey.Name)} DEFERRABLE INITIALLY DEFERRED",
                        operationToken).ConfigureAwait(false);
                }

                await ExecuteNonQueryAsync(connection, transaction, "SET CONSTRAINTS ALL DEFERRED", operationToken).ConfigureAwait(false);
                var truncateSql = "TRUNCATE TABLE " + string.Join(", ", manifest.Tables.Select(table => $"{Quote(table.Schema)}.{Quote(table.Name)}"));
                await ExecuteNonQueryAsync(connection, transaction, truncateSql, operationToken).ConfigureAwait(false);
                foreach (var table in manifest.Tables)
                {
                    var copiedColumns = table.Columns.Where(column => string.Equals(column.Generated, "NEVER", StringComparison.Ordinal)).ToArray();
                    var copySql = $"COPY {Quote(table.Schema)}.{Quote(table.Name)} ({string.Join(", ", copiedColumns.Select(column => Quote(column.Name)))}) FROM STDIN (FORMAT BINARY)";
                    var input = backup.TableFiles[table.File];
                    input.Position = 0;
                    await using var copy = await connection.BeginRawBinaryCopyAsync(copySql, operationToken).ConfigureAwait(false);
                    await input.CopyToAsync(copy, operationToken).ConfigureAwait(false);
                }

                foreach (var sequence in manifest.Sequences)
                {
                    await using var command = new NpgsqlCommand("SELECT setval(@name::regclass, @value, @called)", connection, transaction);
                    command.Parameters.AddWithValue("name", $"{Quote(sequence.Schema)}.{Quote(sequence.Name)}");
                    command.Parameters.AddWithValue("value", sequence.LastValue);
                    command.Parameters.AddWithValue("called", sequence.IsCalled);
                    command.CommandTimeout = 25;
                    await command.ExecuteNonQueryAsync(operationToken).ConfigureAwait(false);
                }

                await ExecuteNonQueryAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE", operationToken).ConfigureAwait(false);
                foreach (var foreignKey in manifest.ForeignKeys)
                {
                    var characteristics = foreignKey.Deferrable
                        ? foreignKey.InitiallyDeferred ? "DEFERRABLE INITIALLY DEFERRED" : "DEFERRABLE INITIALLY IMMEDIATE"
                        : "NOT DEFERRABLE";
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE {Quote(foreignKey.Schema)}.{Quote(foreignKey.Table)} ALTER CONSTRAINT {Quote(foreignKey.Name)} {characteristics}",
                        operationToken).ConfigureAwait(false);
                }

                var restoredFingerprint = await ReadSchemaFingerprintAsync(connection, transaction, operationToken).ConfigureAwait(false);
                if (!FixedHashEquals(restoredFingerprint, manifest.SchemaFingerprint))
                {
                    throw new InvalidDataException("The restored PostgreSQL schema did not match the captured schema; the restore transaction was rolled back.");
                }
            }

            await transaction.CommitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The PostgreSQL migration restore exceeded its internal safety deadline; the restore transaction was rolled back.",
                exception);
        }
        finally
        {
            if (gateAcquired)
            {
                OperationGate.Release();
            }
        }
    }

    /// <summary>Deletes an atomically completed snapshot; missing keys are an idempotent success.</summary>
    /// <param name="backupRoot">The provider-owned backup root.</param>
    /// <param name="key">The opaque completed backup key.</param>
    /// <returns>A task that represents deletion.</returns>
    public static async Task DeleteAsync(string backupRoot, string key)
    {
        await OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = ResolveKeyDirectory(backupRoot, key, allowPartial: false);
            if (!Directory.Exists(directory))
            {
                return;
            }

            RejectLinks(directory, backupRoot);
            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private static async Task<DatabaseIdentity> ReadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT current_database(), database.oid::bigint, control.system_identifier::text,
                   coalesce(inet_server_addr()::text, 'local') || ':' || inet_server_port()::text
            FROM pg_catalog.pg_database database
            CROSS JOIN pg_catalog.pg_control_system() control
            WHERE database.datname = current_database()
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction)
        {
            CommandTimeout = 10,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return the current database identity.");
        }

        var database = reader.GetString(0);
        var databaseOid = reader.GetInt64(1);
        var systemIdentifier = reader.GetString(2);
        var selectedServer = reader.GetString(3);
        var binding = string.Create(
            CultureInfo.InvariantCulture,
            $"postgresql-system:{systemIdentifier}|database:{database}|oid:{databaseOid}|server:{selectedServer}");
        return new DatabaseIdentity(database, databaseOid, systemIdentifier, selectedServer, HashString(binding));
    }

    private static async Task<string?> ReadSourceMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool empty,
        CancellationToken cancellationToken)
    {
        if (empty)
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" IN (@baseline, @successor) ORDER BY \"MigrationId\" DESC LIMIT 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("baseline", BaselineMigration);
        command.Parameters.AddWithValue("successor", SuccessorMigration);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (result is not (BaselineMigration or SuccessorMigration))
        {
            throw new NotSupportedException(
                "The populated PostgreSQL database does not have a supported plugin migration baseline. Refusing migration before mutation.");
        }

        return result;
    }

    private static string GenerateSupportedSchemaSql(JellyfinDbContext context, string sourceMigration)
    {
        var selectedMigrations = sourceMigration == BaselineMigration
            ? new[] { BaselineMigration }
            : new[] { BaselineMigration, SuccessorMigration };
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var historyRepository = context.GetService<IHistoryRepository>();
        var script = new StringBuilder(historyRepository.GetCreateScript());
        foreach (var migrationId in selectedMigrations)
        {
            if (!migrationsAssembly.Migrations.TryGetValue(migrationId, out var migrationType))
            {
                throw new NotSupportedException(
                    $"The provider migration '{migrationId}' is unavailable; no rollback-capable backup was published.");
            }

            var migration = migrationsAssembly.CreateMigration(migrationType, context.Database.ProviderName!);
            foreach (var command in sqlGenerator.Generate(
                migration.UpOperations,
                context.Model,
                MigrationsSqlGenerationOptions.NoTransactions))
            {
                script.AppendLine(command.CommandText);
            }
        }

        return script.ToString();
    }

    private static async Task<List<TableMetadata>> ReadTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT c.table_schema, c.table_name, c.column_name, c.ordinal_position,
                   c.udt_schema, c.udt_name, c.is_nullable, coalesce(c.column_default, ''),
                   c.is_identity, c.identity_generation, c.is_generated, coalesce(c.generation_expression, ''),
                   pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   c.character_maximum_length, c.numeric_precision, c.numeric_scale,
                   c.datetime_precision, coalesce(c.collation_schema, ''), coalesce(c.collation_name, '')
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            JOIN pg_catalog.pg_namespace namespace ON namespace.nspname = c.table_schema
            JOIN pg_catalog.pg_class relation
              ON relation.relnamespace = namespace.oid AND relation.relname = c.table_name
            JOIN pg_catalog.pg_attribute attribute
              ON attribute.attrelid = relation.oid AND attribute.attname = c.column_name
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tables = new List<TableMetadata>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var table = tables.LastOrDefault(candidate => candidate.Schema == schema && candidate.Name == name);
            if (table is null)
            {
                table = new TableMetadata(schema, name, []);
                tables.Add(table);
            }

            table.Columns.Add(new ColumnMetadata(
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6) == "YES",
                reader.GetString(7),
                reader.GetString(8) == "YES",
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                await ReadNullableInt32Async(reader, 13, cancellationToken).ConfigureAwait(false),
                await ReadNullableInt32Async(reader, 14, cancellationToken).ConfigureAwait(false),
                await ReadNullableInt32Async(reader, 15, cancellationToken).ConfigureAwait(false),
                await ReadNullableInt32Async(reader, 16, cancellationToken).ConfigureAwait(false),
                reader.GetString(17),
                reader.GetString(18)));
        }

        return tables;
    }

    private static async Task<int?> ReadNullableInt32Async(NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(ordinal);

    private static async Task<List<TableMetadata>> OrderTablesForRestoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<TableMetadata> tables,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT child_ns.nspname, child.relname, parent_ns.nspname, parent.relname
            FROM pg_catalog.pg_constraint constraint_definition
            JOIN pg_catalog.pg_class child ON child.oid = constraint_definition.conrelid
            JOIN pg_catalog.pg_namespace child_ns ON child_ns.oid = child.relnamespace
            JOIN pg_catalog.pg_class parent ON parent.oid = constraint_definition.confrelid
            JOIN pg_catalog.pg_namespace parent_ns ON parent_ns.oid = parent.relnamespace
            WHERE constraint_definition.contype = 'f'
              AND child_ns.nspname = 'public'
              AND parent_ns.nspname = 'public'
            ORDER BY child_ns.nspname, child.relname, parent_ns.nspname, parent.relname
            """;
        var dependencies = tables.ToDictionary(
            table => $"{table.Schema}.{table.Name}",
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var child = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var parent = $"{reader.GetString(2)}.{reader.GetString(3)}";
            if (!string.Equals(child, parent, StringComparison.Ordinal)
                && dependencies.TryGetValue(child, out var childDependencies)
                && dependencies.ContainsKey(parent))
            {
                childDependencies.Add(parent);
            }
        }

        var remaining = tables.ToDictionary(table => $"{table.Schema}.{table.Name}", StringComparer.Ordinal);
        var ordered = new List<TableMetadata>(tables.Count);
        while (remaining.Count != 0)
        {
            var ready = remaining.Keys
                .Where(name => dependencies[name].All(dependency => !remaining.ContainsKey(dependency)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                ready = [remaining.Keys.Order(StringComparer.Ordinal).First()];
            }

            foreach (var name in ready)
            {
                ordered.Add(remaining[name]);
                remaining.Remove(name);
            }
        }

        return ordered;
    }

    private static async Task ValidateSupportedModelAsync(
        IModel model,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<TableMetadata> actualTables,
        CancellationToken cancellationToken)
    {
        ValidateSupportedModelTables(model, actualTables);
        await ValidateSupportedSchemaObjectsAsync(connection, transaction, actualTables, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSupportedModelTables(IModel model, IReadOnlyList<TableMetadata> actualTables)
    {
        var expectedTables = model.GetEntityTypes()
            .Select(entity => (Schema: entity.GetSchema() ?? "public", Name: entity.GetTableName()))
            .Where(table => table.Name is not null)
            .Select(table => $"{table.Schema}.{table.Name}")
            .Distinct(StringComparer.Ordinal)
            .Append("public.__EFMigrationsHistory")
            .Append("public.__JellyfinTextNodes")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = actualTables.Select(table => $"{table.Schema}.{table.Name}").Order(StringComparer.Ordinal).ToArray();
        if (!expectedTables.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                "The populated PostgreSQL schema contains unsupported or missing tables. Refusing migration before mutation because an exact rollback cannot be guaranteed.");
        }

        var expectedColumns = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .GroupBy(entity => $"{entity.GetSchema() ?? "public"}.{entity.GetTableName()}", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(entity => entity.GetProperties().Select(property => property.GetColumnName(
                        StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))))
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        expectedColumns["public.__EFMigrationsHistory"] = ["MigrationId", "ProductVersion"];
        expectedColumns["public.__JellyfinTextNodes"] = ["Chunk", "Id", "IsTerminal", "ParentId"];
        var expectedColumnDefinitions = model.GetRelationalModel().Tables
            .ToDictionary(
                table => $"{table.Schema ?? "public"}.{table.Name}",
                table => table.Columns.ToDictionary(
                    column => column.Name,
                    column => (column.StoreType, column.IsNullable),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        expectedColumnDefinitions["public.__EFMigrationsHistory"] = new Dictionary<string, (string StoreType, bool IsNullable)>(StringComparer.Ordinal)
        {
            ["MigrationId"] = ("character varying(150)", false),
            ["ProductVersion"] = ("character varying(32)", false),
        };
        expectedColumnDefinitions["public.__JellyfinTextNodes"] = new Dictionary<string, (string StoreType, bool IsNullable)>(StringComparer.Ordinal)
        {
            ["Chunk"] = ("bytea", false),
            ["Id"] = ("bigint", false),
            ["IsTerminal"] = ("boolean", false),
            ["ParentId"] = ("bigint", false),
        };
        foreach (var table in actualTables)
        {
            var tableName = $"{table.Schema}.{table.Name}";
            var columns = table.Columns.Select(column => column.Name).Order(StringComparer.Ordinal).ToArray();
            if (!expectedColumns[tableName].SequenceEqual(columns, StringComparer.Ordinal))
            {
                throw new NotSupportedException(
                    $"The populated PostgreSQL table '{tableName}' has unsupported or missing columns. Refusing migration before mutation because an exact rollback cannot be guaranteed.");
            }

            foreach (var column in table.Columns)
            {
                var expected = expectedColumnDefinitions[tableName][column.Name];
                if (!string.Equals(expected.StoreType, column.FormattedType, StringComparison.Ordinal)
                    || expected.IsNullable != column.Nullable)
                {
                    throw new NotSupportedException(
                        $"The populated PostgreSQL column '{tableName}.{column.Name}' has an unsupported type or nullability. Refusing migration before mutation.");
                }
            }
        }
    }

    private static async Task ValidateSupportedSchemaObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<TableMetadata> tables,
        CancellationToken cancellationToken)
    {
        const string UnsupportedObjectsSql = """
            SELECT
              (SELECT count(*) FROM pg_catalog.pg_class relation
               JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
               WHERE namespace.nspname = 'public' AND relation.relkind IN ('v', 'm', 'f', 'p', 'c'))
              + (SELECT count(*) FROM pg_catalog.pg_policy policy
                 JOIN pg_catalog.pg_class relation ON relation.oid = policy.polrelid
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = 'public')
              + (SELECT count(*) FROM pg_catalog.pg_class relation
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = 'public' AND (relation.relrowsecurity OR relation.relforcerowsecurity))
              + (SELECT count(*) FROM pg_catalog.pg_collation collation_definition
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = collation_definition.collnamespace
                 WHERE namespace.nspname = 'public')
              + (SELECT count(*) FROM pg_catalog.pg_type type_definition
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_definition.typnamespace
                 WHERE namespace.nspname = 'public' AND type_definition.typtype IN ('d', 'e', 'r', 'm'))
            """;
        await using (var command = new NpgsqlCommand(UnsupportedObjectsSql, connection, transaction))
        {
            var unsupportedCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (unsupportedCount != 0)
            {
                throw new NotSupportedException(
                    "The populated PostgreSQL schema contains unsupported views, materialized views, policies, row security, partitions, foreign tables, collations, or user-defined types. Refusing migration before mutation.");
            }
        }

        const string SchemaPropertiesSql = """
            SELECT pg_get_userbyid(namespace.nspowner) = 'pg_database_owner'
               AND namespace.nspacl::text = '{pg_database_owner=UC/pg_database_owner,=U/pg_database_owner}'
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname = 'public'
            """;
        await using (var command = new NpgsqlCommand(SchemaPropertiesSql, connection, transaction))
        {
            var supported = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as bool?;
            if (supported is not true)
            {
                throw new NotSupportedException(
                    "The populated PostgreSQL public schema has unsupported ownership or grants. Refusing migration before mutation.");
            }
        }

        var expectedFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "__jellyfin_ascii_lower(text)",
            "__jellyfin_ascii_prefix(text)",
            "__jellyfin_intern_BaseItemProviders()",
            "__jellyfin_intern_CustomItemDisplayPreferences()",
            "__jellyfin_intern_ItemValues()",
            "__jellyfin_intern_PeopleBaseItemMap()",
            "__jellyfin_intern_UserData()",
            "__jellyfin_intern_text(text)",
            "__jellyfin_sqlite_like(text, text)",
            "__jellyfin_sqlite_like_escape(text, text, text)",
        };
        var functions = await ReadStringSetAsync(
            connection,
            transaction,
            "SELECT p.proname || '(' || pg_catalog.oidvectortypes(p.proargtypes) || ')' FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' ORDER BY 1",
            cancellationToken).ConfigureAwait(false);
        if (!expectedFunctions.SetEquals(functions))
        {
            throw new NotSupportedException("The populated PostgreSQL schema contains unsupported or missing functions. Refusing migration before mutation.");
        }

        var expectedTriggers = new HashSet<string>(StringComparer.Ordinal)
        {
            "BaseItemProviders.TR_BaseItemProviders_Intern",
            "CustomItemDisplayPreferences.TR_CustomItemDisplayPreferences_Intern",
            "ItemValues.TR_ItemValues_Intern",
            "PeopleBaseItemMap.TR_PeopleBaseItemMap_Intern",
            "UserData.TR_UserData_Intern",
        };
        var triggers = await ReadStringSetAsync(
            connection,
            transaction,
            "SELECT relation.relname || '.' || trigger.tgname FROM pg_catalog.pg_trigger trigger JOIN pg_catalog.pg_class relation ON relation.oid = trigger.tgrelid JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public' AND NOT trigger.tgisinternal ORDER BY 1",
            cancellationToken).ConfigureAwait(false);
        if (!expectedTriggers.SetEquals(triggers))
        {
            throw new NotSupportedException("The populated PostgreSQL schema contains unsupported or missing triggers. Refusing migration before mutation.");
        }

        var identityColumns = tables
            .SelectMany(table => table.Columns
                .Where(column => column.Identity)
                .Select(column => $"{table.Schema}.{table.Name}.{column.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        var sequences = await ReadSequencesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var ownedColumns = sequences
            .Select(sequence => $"{sequence.OwnedTable}.{sequence.OwnedColumn}")
            .ToHashSet(StringComparer.Ordinal);
        if (!identityColumns.SetEquals(ownedColumns)
            || sequences.Any(sequence => !IsStandardIdentitySequence(sequence)))
        {
            throw new NotSupportedException(
                "The populated PostgreSQL schema contains unsupported, unowned, missing, or altered sequences. Refusing migration before mutation.");
        }
    }

    private static bool IsStandardIdentitySequence(SequenceSnapshot sequence)
    {
        var maximum = sequence.DataType switch
        {
            "smallint" => short.MaxValue,
            "integer" => int.MaxValue,
            "bigint" => long.MaxValue,
            _ => 0,
        };
        return sequence.StartValue == 1
            && sequence.MinimumValue == 1
            && sequence.MaximumValue == maximum
            && sequence.IncrementBy == 1
            && !sequence.Cycle
            && sequence.CacheSize == 1
            && sequence.OwnedTable.Length != 0
            && sequence.OwnedColumn.Length != 0;
    }

    private static async Task<HashSet<string>> ReadStringSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ValidateMigrationZeroAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT
              (SELECT count(*) FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public')
              + (SELECT count(*) FROM pg_catalog.pg_proc routine JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace WHERE namespace.nspname = 'public')
              + (SELECT count(*) FROM pg_catalog.pg_collation col JOIN pg_catalog.pg_namespace namespace ON namespace.oid = col.collnamespace WHERE namespace.nspname = 'public')
              + (SELECT count(*) FROM pg_catalog.pg_type type_def JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_def.typnamespace WHERE namespace.nspname = 'public')
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        var objectCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (objectCount != 0)
        {
            throw new NotSupportedException(
                "The database has no supported tables but its public schema is not migration zero. Refusing migration before mutation because an exact rollback cannot be guaranteed.");
        }
    }

    private static bool IsHistoryOnly(IReadOnlyList<TableMetadata> tables)
        => tables.Count == 1
            && string.Equals(tables[0].Schema, "public", StringComparison.Ordinal)
            && string.Equals(tables[0].Name, "__EFMigrationsHistory", StringComparison.Ordinal);

    private static async Task ValidateHistoryOnlyBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT
              (SELECT count(*) FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public') = 2
              AND (SELECT count(*) FROM pg_catalog.pg_proc routine JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace WHERE namespace.nspname = 'public') = 0
              AND (SELECT count(*) FROM pg_catalog.pg_collation col JOIN pg_catalog.pg_namespace namespace ON namespace.oid = col.collnamespace WHERE namespace.nspname = 'public') = 0
              AND (SELECT count(*) FROM pg_catalog.pg_type type_def JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_def.typnamespace WHERE namespace.nspname = 'public') = 2
              AND (SELECT count(*) FROM pg_catalog.pg_constraint constraint_definition JOIN pg_catalog.pg_class relation ON relation.oid = constraint_definition.conrelid JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public') = 1
              AND (SELECT pg_get_constraintdef(constraint_definition.oid, true) FROM pg_catalog.pg_constraint constraint_definition WHERE constraint_definition.conname = 'PK___EFMigrationsHistory') = 'PRIMARY KEY ("MigrationId")'
              AND (SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory' AND column_name = 'MigrationId') = 150
              AND (SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory' AND column_name = 'ProductVersion') = 32
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        var supported = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!supported)
        {
            throw new NotSupportedException(
                "The pre-migration history-only database is not the exact vanilla Jellyfin seed schema. Refusing migration before mutation.");
        }
    }

    private static async Task<List<SequenceSnapshot>> ReadSequencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var sequences = new List<SequenceSnapshot>();
        const string Sql = """
            SELECT sequences.schemaname, sequences.sequencename, sequences.data_type::text,
                   sequences.start_value, sequences.min_value, sequences.max_value,
                   sequences.increment_by, sequences.cycle, sequences.cache_size,
                   coalesce(table_namespace.nspname, ''), coalesce(table_relation.relname, ''),
                   coalesce(table_attribute.attname, '')
            FROM pg_catalog.pg_sequences sequences
            JOIN pg_catalog.pg_namespace sequence_namespace ON sequence_namespace.nspname = sequences.schemaname
            JOIN pg_catalog.pg_class sequence_relation
              ON sequence_relation.relnamespace = sequence_namespace.oid
             AND sequence_relation.relname = sequences.sequencename
             AND sequence_relation.relkind = 'S'
            LEFT JOIN pg_catalog.pg_depend dependency
              ON dependency.classid = 'pg_class'::regclass
             AND dependency.objid = sequence_relation.oid
             AND dependency.deptype IN ('a', 'i')
            LEFT JOIN pg_catalog.pg_class table_relation ON table_relation.oid = dependency.refobjid
            LEFT JOIN pg_catalog.pg_namespace table_namespace ON table_namespace.oid = table_relation.relnamespace
            LEFT JOIN pg_catalog.pg_attribute table_attribute
              ON table_attribute.attrelid = table_relation.oid
             AND table_attribute.attnum = dependency.refobjsubid
            WHERE sequences.schemaname = 'public'
            ORDER BY sequences.schemaname, sequences.sequencename
            """;
        await using var list = new NpgsqlCommand(Sql, connection, transaction);
        await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new List<SequenceDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(new SequenceDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetBoolean(7),
                reader.GetInt64(8),
                reader.GetString(9).Length == 0 ? string.Empty : reader.GetString(9) + "." + reader.GetString(10),
                reader.GetString(11)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (var definition in definitions)
        {
            await using var command = new NpgsqlCommand($"SELECT last_value, is_called FROM {Quote(definition.Schema)}.{Quote(definition.Name)}", connection, transaction);
            await using var valueReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await valueReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            sequences.Add(new SequenceSnapshot(
                definition.Schema,
                definition.Name,
                definition.DataType,
                definition.StartValue,
                definition.MinimumValue,
                definition.MaximumValue,
                definition.IncrementBy,
                definition.Cycle,
                definition.CacheSize,
                definition.OwnedTable,
                definition.OwnedColumn,
                valueReader.GetInt64(0),
                valueReader.GetBoolean(1)));
        }

        return sequences;
    }

    private static async Task<List<ForeignKeySnapshot>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT namespace.nspname, relation.relname, constraint_definition.conname,
                   constraint_definition.condeferrable, constraint_definition.condeferred
            FROM pg_catalog.pg_constraint constraint_definition
            JOIN pg_catalog.pg_class relation ON relation.oid = constraint_definition.conrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE constraint_definition.contype = 'f' AND namespace.nspname = 'public'
            ORDER BY namespace.nspname, relation.relname, constraint_definition.conname
            """;
        var foreignKeys = new List<ForeignKeySnapshot>();
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeys.Add(new ForeignKeySnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return foreignKeys;
    }

    private static async Task<string> ReadSchemaFingerprintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        string schema = "public")
        => HashString(await ReadSchemaCanonicalAsync(connection, transaction, cancellationToken, schema).ConfigureAwait(false));

    private static async Task<string> ReadSchemaCanonicalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        string schema = "public")
    {
        const string Sql = """
            SELECT string_agg(definition, E'\n' ORDER BY definition)
            FROM (
              SELECT 'relation|' || namespace.nspname || '|' || relation.relname || '|' || relation.relkind::text || '|' || relation.relpersistence::text || '|' || pg_get_userbyid(relation.relowner) || '|' || coalesce(relation.relacl::text, '') || '|' || relation.relrowsecurity || '|' || relation.relforcerowsecurity || '|' || coalesce(relation.reloptions::text, '') AS definition
              FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
              WHERE namespace.nspname = 'public' AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
              UNION ALL
              SELECT 'column|' || namespace.nspname || '|' || relation.relname || '|' || attribute.attname || '|' || attribute.attnum || '|' || pg_catalog.format_type(attribute.atttypid, attribute.atttypmod) || '|' || attribute.attnotnull || '|' || attribute.attidentity::text || '|' || attribute.attgenerated::text || '|' || coalesce(collation_namespace.nspname || '.' || collation_definition.collname, '') || '|' || coalesce(pg_get_expr(default_definition.adbin, default_definition.adrelid), '') || '|' || coalesce(attribute.attacl::text, '')
              FROM pg_catalog.pg_attribute attribute
              JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
              LEFT JOIN pg_catalog.pg_attrdef default_definition ON default_definition.adrelid = relation.oid AND default_definition.adnum = attribute.attnum
              LEFT JOIN pg_catalog.pg_collation collation_definition ON collation_definition.oid = attribute.attcollation AND attribute.attcollation <> 0
              LEFT JOIN pg_catalog.pg_namespace collation_namespace ON collation_namespace.oid = collation_definition.collnamespace
              WHERE namespace.nspname = 'public' AND attribute.attnum > 0 AND NOT attribute.attisdropped
              UNION ALL
              SELECT 'constraint|' || n.nspname || '|' || c.relname || '|' || con.conname || '|' || con.contype::text || '|' || con.condeferrable || '|' || con.condeferred || '|' || con.convalidated || '|' || pg_get_constraintdef(con.oid, true)
              FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'
              UNION ALL
              SELECT 'index|' || namespace.nspname || '|' || relation.relname || '|' || index_relation.relname || '|' || index_definition.indisunique || '|' || index_definition.indisprimary || '|' || index_definition.indisvalid || '|' || index_definition.indisready || '|' || index_definition.indislive || '|' || pg_get_indexdef(index_relation.oid)
              FROM pg_catalog.pg_index index_definition
              JOIN pg_catalog.pg_class relation ON relation.oid = index_definition.indrelid
              JOIN pg_catalog.pg_class index_relation ON index_relation.oid = index_definition.indexrelid
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
              WHERE namespace.nspname = 'public'
              UNION ALL
              SELECT 'function|' || n.nspname || '|' || p.proname || '|' || pg_catalog.oidvectortypes(p.proargtypes) || '|' || pg_get_userbyid(p.proowner) || '|' || coalesce(p.proacl::text, '') || '|' || p.prosecdef || '|' || p.provolatile::text || '|' || p.proparallel::text || '|' || pg_get_functiondef(p.oid) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public'
              UNION ALL
              SELECT 'trigger|' || n.nspname || '|' || c.relname || '|' || t.tgname || '|' || t.tgenabled::text || '|' || pg_get_triggerdef(t.oid, true) FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal
              UNION ALL
              SELECT 'policy|' || namespace.nspname || '|' || relation.relname || '|' || policy.polname || '|' || policy.polcmd::text || '|' || policy.polpermissive || '|' || policy.polroles::text || '|' || coalesce(pg_get_expr(policy.polqual, policy.polrelid), '') || '|' || coalesce(pg_get_expr(policy.polwithcheck, policy.polrelid), '')
              FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class relation ON relation.oid = policy.polrelid JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public'
              UNION ALL
              SELECT 'sequence|' || sequences.schemaname || '|' || sequences.sequencename || '|' || sequences.data_type::text || '|' || sequences.start_value || '|' || sequences.min_value || '|' || sequences.max_value || '|' || sequences.increment_by || '|' || sequences.cycle || '|' || sequences.cache_size || '|' || sequences.sequenceowner
              FROM pg_catalog.pg_sequences sequences WHERE sequences.schemaname = 'public'
              UNION ALL
              SELECT 'extension|' || extension.extname || '|' || extension.extversion || '|' || namespace.nspname
              FROM pg_catalog.pg_extension extension JOIN pg_catalog.pg_namespace namespace ON namespace.oid = extension.extnamespace WHERE namespace.nspname = 'public'
            ) definitions
            """;
        var parameterizedSql = Sql.Replace("'public'", "@schema", StringComparison.Ordinal);
        await using var command = new NpgsqlCommand(parameterizedSql, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        var canonical = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? string.Empty;
        canonical = canonical
            .Replace(Quote(schema), "<schema>", StringComparison.Ordinal)
            .Replace(schema, "<schema>", StringComparison.Ordinal);
        return canonical;
    }

    private static async Task ValidateGeneratedSchemaContractAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaSql,
        string actualFingerprint,
        CancellationToken cancellationToken)
    {
        const string Savepoint = "jellyfin_backup_schema_contract";
        var schema = "jellyfin_backup_contract_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var actualCanonical = await ReadSchemaCanonicalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!FixedHashEquals(actualFingerprint, HashString(actualCanonical)))
        {
            throw new InvalidOperationException("The PostgreSQL schema changed while its migration backup contract was being validated.");
        }

        await ExecuteNonQueryAsync(connection, transaction, $"SAVEPOINT {Savepoint}; CREATE SCHEMA {Quote(schema)}", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"SET LOCAL search_path = {Quote(schema)}, pg_catalog",
                cancellationToken).ConfigureAwait(false);
            var isolatedSql = schemaSql
                .Replace("public.", Quote(schema) + ".", StringComparison.Ordinal)
                .Replace("pg_catalog, public", "pg_catalog, " + Quote(schema), StringComparison.Ordinal);
            await ExecuteNonQueryAsync(connection, transaction, isolatedSql, cancellationToken).ConfigureAwait(false);
            var expectedCanonical = await ReadSchemaCanonicalAsync(
                connection,
                transaction,
                cancellationToken,
                schema).ConfigureAwait(false);
            if (!string.Equals(actualCanonical, expectedCanonical, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "The populated PostgreSQL schema differs from the exact provider-generated migration schema. Refusing migration before application-schema mutation.");
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"ROLLBACK TO SAVEPOINT {Savepoint}; RELEASE SAVEPOINT {Savepoint}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = 25;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureNoActiveDatabaseUsersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT count(*)
            FROM pg_catalog.pg_stat_activity
            WHERE datid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
              AND pid <> pg_backend_pid()
              AND backend_type = 'client backend'
              AND state IS DISTINCT FROM 'idle'
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction)
        {
            CommandTimeout = 5,
        };
        var active = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (active != 0)
        {
            throw new InvalidOperationException(
                "The PostgreSQL migration restore detected another active database session. Stop concurrent database work and retry; the database was not changed.");
        }
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != FormatVersion
            || !string.Equals(manifest.ProviderVersion, PluginMetadata.Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Database)
            || string.IsNullOrWhiteSpace(manifest.SystemIdentifier)
            || string.IsNullOrWhiteSpace(manifest.SelectedServer)
            || !Sha256Pattern().IsMatch(manifest.TargetBinding)
            || !Sha256Pattern().IsMatch(manifest.SchemaFingerprint)
            || manifest.Tables is null
            || manifest.Sequences is null
            || manifest.ForeignKeys is null
            || (manifest.Empty && (manifest.HistoryOnly || manifest.Tables.Count != 0 || manifest.SchemaFile is not null))
            || (manifest.HistoryOnly && (manifest.Empty || manifest.SourceMigration is not null || manifest.SchemaFile is null || manifest.Tables.Count != 1))
            || (!manifest.Empty && !manifest.HistoryOnly && (manifest.SourceMigration is not (BaselineMigration or SuccessorMigration) || manifest.SchemaFile is null || manifest.SchemaHash is null)))
        {
            throw new InvalidDataException("The PostgreSQL migration backup manifest is incompatible or malformed; the database was not changed.");
        }
    }

    private static async Task<ValidatedBackup> StageValidatedBackupAsync(
        string backupRoot,
        string key,
        CancellationToken cancellationToken)
    {
        var directory = ResolveKeyDirectory(backupRoot, key, allowPartial: false);
        RejectLinks(directory, backupRoot);
        var stageDirectory = Path.Combine(
            Path.GetFullPath(backupRoot),
            ".restore-v2-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial");
        Directory.CreateDirectory(stageDirectory);
        SetDirectoryPermissions(stageDirectory);
        var tableFiles = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            var completionBytes = await ReadBoundedSourceFileAsync(
                directory,
                CompletionFile,
                128,
                cancellationToken).ConfigureAwait(false);
            var expectedManifestHash = Encoding.ASCII.GetString(completionBytes).Trim();
            var manifestBytes = await ReadBoundedSourceFileAsync(
                directory,
                ManifestFile,
                16 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            var actualManifestHash = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            if (!FixedHashEquals(expectedManifestHash, actualManifestHash))
            {
                throw new InvalidDataException("The PostgreSQL migration backup manifest checksum is invalid; the database was not changed.");
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("The PostgreSQL migration backup manifest is empty.");
            ValidateManifest(manifest);
            var age = DateTimeOffset.UtcNow - manifest.CreatedUtc;
            if (age > TimeSpan.FromDays(7) || age < TimeSpan.FromMinutes(-5))
            {
                throw new InvalidDataException("The PostgreSQL migration backup is stale and cannot be restored automatically.");
            }

            var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
            {
                ManifestFile,
                CompletionFile,
            };
            if (manifest.SchemaFile is not null && !expectedFiles.Add(manifest.SchemaFile))
            {
                throw new InvalidDataException("The PostgreSQL migration backup manifest contains duplicate file names; the database was not changed.");
            }

            foreach (var table in manifest.Tables)
            {
                if (!expectedFiles.Add(table.File))
                {
                    throw new InvalidDataException("The PostgreSQL migration backup manifest contains duplicate file names; the database was not changed.");
                }
            }

            var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path)!)
                .ToHashSet(StringComparer.Ordinal);
            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any()
                || !expectedFiles.SetEquals(actualFiles))
            {
                throw new InvalidDataException("The PostgreSQL migration backup contains missing or unexpected artifacts; the database was not changed.");
            }

            string? schemaSql = null;
            if (manifest.SchemaFile is not null)
            {
                var schemaBytes = await ReadBoundedSourceFileAsync(
                    directory,
                    manifest.SchemaFile,
                    64 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                if (!FixedHashEquals(manifest.SchemaHash!, Convert.ToHexStringLower(SHA256.HashData(schemaBytes))))
                {
                    throw new InvalidDataException("The PostgreSQL migration backup schema checksum is invalid; the database was not changed.");
                }

                schemaSql = new UTF8Encoding(false, true).GetString(schemaBytes);
            }

            foreach (var table in manifest.Tables)
            {
                ValidateSafeArtifactName(table.File);
                var sourcePath = Path.Combine(directory, table.File);
                RejectSourceArtifact(sourcePath);
                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var stagedPath = Path.Combine(stageDirectory, table.File);
                FileStream? staged = null;
                try
                {
#pragma warning disable CA2000 // Ownership is transferred to ValidatedBackup, or the finally block disposes the stream before propagating failure.
                    staged = CreatePrivateFile(stagedPath);
#pragma warning restore CA2000
                    await source.CopyToAsync(staged, cancellationToken).ConfigureAwait(false);
                    await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (staged.Length != table.Length)
                    {
                        throw new InvalidDataException($"Backup data length for table '{table.Schema}.{table.Name}' is invalid; the database was not changed.");
                    }

                    staged.Position = 0;
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(staged, cancellationToken).ConfigureAwait(false));
                    if (!FixedHashEquals(table.Sha256, hash))
                    {
                        throw new InvalidDataException("The PostgreSQL migration backup data checksum is invalid; the database was not changed.");
                    }

                    staged.Position = 0;
                    tableFiles.Add(table.File, staged);
                    staged = null;
                }
                finally
                {
                    if (staged is not null)
                    {
                        await staged.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            NpgsqlBackupFileSystem.Checkpoint("validated-immutable-stage-ready");
            return new ValidatedBackup(stageDirectory, manifest, schemaSql, tableFiles);
        }
        catch
        {
            foreach (var stream in tableFiles.Values)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            TryDeleteOwnedDirectory(stageDirectory);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedSourceFileAsync(
        string directory,
        string fileName,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        ValidateSafeArtifactName(fileName);
        var path = Path.Combine(directory, fileName);
        RejectSourceArtifact(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumLength)
        {
            throw new InvalidDataException("The PostgreSQL migration backup artifact is unreasonably large; the database was not changed.");
        }

        using var memory = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static void ValidateSafeArtifactName(string fileName)
    {
        if (!SafeFilePattern().IsMatch(fileName) && fileName is not (ManifestFile or CompletionFile))
        {
            throw new InvalidDataException("The PostgreSQL migration backup contains an unsafe file name; the database was not changed.");
        }
    }

    private static void RejectSourceArtifact(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The PostgreSQL migration backup references a missing or unsafe file; the database was not changed.");
        }
    }

    private static void PrepareRoot(string root)
    {
        if (Directory.Exists(root))
        {
            RejectLinks(root, root);
        }

        Directory.CreateDirectory(root);
        RejectLinks(root, root);
        SetDirectoryPermissions(root);
    }

    private static string ResolveKeyDirectory(string root, string key, bool allowPartial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var valid = allowPartial ? PartialKeyPattern().IsMatch(key) : KeyPattern().IsMatch(key);
        if (!valid)
        {
            throw new ArgumentException("The PostgreSQL migration backup key is malformed.", nameof(key));
        }

        var canonicalRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, key));
        if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The PostgreSQL migration backup key escapes the backup directory.", nameof(key));
        }

        return path;
    }

    private static void RejectLinks(string path, string root)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        while (current.Length >= canonicalRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var info = new DirectoryInfo(current);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    throw new InvalidDataException("Symbolic links are not accepted in the PostgreSQL migration backup path.");
                }
            }

            if (string.Equals(current, canonicalRoot, StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void ScavengePartialDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*.partial", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (PartialKeyPattern().IsMatch(name) || RestorePartialPattern().IsMatch(name))
            {
                TryDeleteOwnedDirectory(directory);
            }
        }
    }

    private static void TryDeleteOwnedDirectory(string directory)
    {
        try
        {
            RejectLinks(directory, Path.GetDirectoryName(directory)!);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        SetFilePermissions(path);
        return stream;
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string HashString(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedHashEquals(string left, string right)
    {
        if (!Sha256Pattern().IsMatch(left) || !Sha256Pattern().IsMatch(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    [GeneratedRegex("^npgsql-v1-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^npgsql-v1-[0-9a-f]{32}\\.partial$", RegexOptions.CultureInvariant)]
    private static partial Regex PartialKeyPattern();

    [GeneratedRegex("^\\.restore-v2-[0-9a-f]{32}\\.partial$", RegexOptions.CultureInvariant)]
    private static partial Regex RestorePartialPattern();

    [GeneratedRegex("^(?:schema\\.sql|table-[0-9]{4}\\.copy)$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFilePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Pattern();

    private sealed record DatabaseIdentity(
        string Database,
        long DatabaseOid,
        string SystemIdentifier,
        string SelectedServer,
        string Binding);

    private sealed record BackupManifest(
        int FormatVersion,
        string ProviderVersion,
        DateTimeOffset CreatedUtc,
        string Database,
        long DatabaseOid,
        string SystemIdentifier,
        string SelectedServer,
        string TargetBinding,
        string? SourceMigration,
        bool Empty,
        bool HistoryOnly,
        string SchemaFingerprint,
        string? SchemaFile,
        string? SchemaHash,
        IReadOnlyList<TableSnapshot> Tables,
        IReadOnlyList<SequenceSnapshot> Sequences,
        IReadOnlyList<ForeignKeySnapshot> ForeignKeys);

    private sealed record TableSnapshot(
        string Schema,
        string Name,
        IReadOnlyList<ColumnMetadata> Columns,
        string File,
        long Length,
        string Sha256);

    private sealed record TableMetadata(string Schema, string Name, List<ColumnMetadata> Columns);

    private sealed record ColumnMetadata(
        string Name,
        int Ordinal,
        string TypeSchema,
        string TypeName,
        bool Nullable,
        string Default,
        bool Identity,
        string? IdentityGeneration,
        string Generated,
        string GenerationExpression,
        string FormattedType,
        int? CharacterMaximumLength,
        int? NumericPrecision,
        int? NumericScale,
        int? DateTimePrecision,
        string CollationSchema,
        string CollationName);

    private sealed record SequenceDefinition(
        string Schema,
        string Name,
        string DataType,
        long StartValue,
        long MinimumValue,
        long MaximumValue,
        long IncrementBy,
        bool Cycle,
        long CacheSize,
        string OwnedTable,
        string OwnedColumn);

    private sealed record SequenceSnapshot(
        string Schema,
        string Name,
        string DataType,
        long StartValue,
        long MinimumValue,
        long MaximumValue,
        long IncrementBy,
        bool Cycle,
        long CacheSize,
        string OwnedTable,
        string OwnedColumn,
        long LastValue,
        bool IsCalled);

    private sealed record ForeignKeySnapshot(string Schema, string Table, string Name, bool Deferrable, bool InitiallyDeferred);

    private sealed class ValidatedBackup(
        string stageDirectory,
        BackupManifest manifest,
        string? schemaSql,
        IReadOnlyDictionary<string, FileStream> tableFiles) : IAsyncDisposable
    {
        public BackupManifest Manifest { get; } = manifest;

        public string? SchemaSql { get; } = schemaSql;

        public IReadOnlyDictionary<string, FileStream> TableFiles { get; } = tableFiles;

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in this.TableFiles.Values)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            TryDeleteOwnedDirectory(stageDirectory);
        }
    }
}
