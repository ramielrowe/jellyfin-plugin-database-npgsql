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
    private const int FormatVersion = 1;
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

            var identity = await ReadIdentityAsync(connection, transaction, connectionString, cancellationToken).ConfigureAwait(false);
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
            if (historyOnly)
            {
                schemaSql = HistorySchemaSql;
            }
            else if (!empty)
            {
                ValidateSupportedModel(context.Model, tables);
                tables = await OrderTablesForRestoreAsync(connection, transaction, tables, cancellationToken).ConfigureAwait(false);
                var migrator = context.GetService<IMigrator>();
                schemaSql = migrator.GenerateScript(
                    fromMigration: Migration.InitialDatabase,
                    toMigration: migration,
                    options: MigrationsSqlGenerationOptions.NoTransactions);
                if (string.IsNullOrWhiteSpace(schemaSql))
                {
                    throw new InvalidOperationException("The provider could not generate the rollback schema before migration; no backup was published.");
                }
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
            var schemaFingerprint = empty
                ? HashString("empty")
                : await ReadSchemaFingerprintAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            string? schemaFile = null;
            string? schemaHash = null;
            if (schemaSql is not null)
            {
                schemaFile = "schema.sql";
                var schemaPath = Path.Combine(partialDirectory, schemaFile);
                await File.WriteAllTextAsync(schemaPath, schemaSql, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                SetFilePermissions(schemaPath);
                schemaHash = await HashFileAsync(schemaPath, cancellationToken).ConfigureAwait(false);
            }

            var manifest = new BackupManifest(
                FormatVersion,
                PluginMetadata.Version,
                DateTimeOffset.UtcNow,
                identity.Database,
                identity.DatabaseOid,
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
            }

            var manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var completionPath = Path.Combine(partialDirectory, CompletionFile);
            await File.WriteAllTextAsync(completionPath, manifestHash + "\n", Encoding.ASCII, cancellationToken).ConfigureAwait(false);
            SetFilePermissions(completionPath);

            var finalDirectory = ResolveKeyDirectory(backupRoot, key, allowPartial: false);
            Directory.Move(partialDirectory, finalDirectory);
            partialDirectory = null;
            return key;
        }
        finally
        {
            if (partialDirectory is not null)
            {
                TryDeleteOwnedDirectory(partialDirectory);
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
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = ResolveKeyDirectory(backupRoot, key, allowPartial: false);
            RejectLinks(directory, backupRoot);
            var manifestPath = Path.Combine(directory, ManifestFile);
            var completionPath = Path.Combine(directory, CompletionFile);
            if (!File.Exists(manifestPath) || !File.Exists(completionPath))
            {
                throw new InvalidDataException("The PostgreSQL migration backup is incomplete; the database was not changed.");
            }

            var expectedManifestHash = (await File.ReadAllTextAsync(completionPath, cancellationToken).ConfigureAwait(false)).Trim();
            var actualManifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (!FixedHashEquals(expectedManifestHash, actualManifestHash))
            {
                throw new InvalidDataException("The PostgreSQL migration backup manifest checksum is invalid; the database was not changed.");
            }

            BackupManifest manifest;
            await using (var stream = File.OpenRead(manifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The PostgreSQL migration backup manifest is empty.");
            }

            ValidateManifest(manifest);
            var age = DateTimeOffset.UtcNow - manifest.CreatedUtc;
            if (age > TimeSpan.FromDays(7) || age < TimeSpan.FromMinutes(-5))
            {
                throw new InvalidDataException("The PostgreSQL migration backup is stale and cannot be restored automatically.");
            }

            if (manifest.SchemaFile is not null)
            {
                await ValidateFileAsync(directory, manifest.SchemaFile, manifest.SchemaHash!, cancellationToken).ConfigureAwait(false);
            }

            foreach (var table in manifest.Tables)
            {
                await ValidateFileAsync(directory, table.File, table.Sha256, cancellationToken).ConfigureAwait(false);
                var length = new FileInfo(Path.Combine(directory, table.File)).Length;
                if (length != table.Length)
                {
                    throw new InvalidDataException($"Backup data length for table '{table.Schema}.{table.Name}' is invalid; the database was not changed.");
                }
            }

            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(connection, null, connectionString, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(identity.Database, manifest.Database, StringComparison.Ordinal)
                || identity.DatabaseOid != manifest.DatabaseOid
                || !FixedHashEquals(identity.Binding, manifest.TargetBinding))
            {
                throw new InvalidDataException("The PostgreSQL migration backup belongs to a different database target; the database was not changed.");
            }

            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public AUTHORIZATION pg_database_owner; GRANT USAGE ON SCHEMA public TO PUBLIC",
                cancellationToken).ConfigureAwait(false);
            if (!manifest.Empty)
            {
                var schemaSql = await File.ReadAllTextAsync(Path.Combine(directory, manifest.SchemaFile!), cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction, schemaSql, cancellationToken).ConfigureAwait(false);
                foreach (var foreignKey in manifest.ForeignKeys)
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE {Quote(foreignKey.Schema)}.{Quote(foreignKey.Table)} ALTER CONSTRAINT {Quote(foreignKey.Name)} DEFERRABLE INITIALLY DEFERRED",
                        cancellationToken).ConfigureAwait(false);
                }

                await ExecuteNonQueryAsync(connection, transaction, "SET CONSTRAINTS ALL DEFERRED", cancellationToken).ConfigureAwait(false);
                var truncateSql = "TRUNCATE TABLE " + string.Join(", ", manifest.Tables.Select(table => $"{Quote(table.Schema)}.{Quote(table.Name)}"));
                await ExecuteNonQueryAsync(connection, transaction, truncateSql, cancellationToken).ConfigureAwait(false);
                foreach (var table in manifest.Tables)
                {
                    var copiedColumns = table.Columns.Where(column => string.Equals(column.Generated, "NEVER", StringComparison.Ordinal)).ToArray();
                    var copySql = $"COPY {Quote(table.Schema)}.{Quote(table.Name)} ({string.Join(", ", copiedColumns.Select(column => Quote(column.Name)))}) FROM STDIN (FORMAT BINARY)";
                    await using var input = File.OpenRead(Path.Combine(directory, table.File));
                    await using var copy = await connection.BeginRawBinaryCopyAsync(copySql, cancellationToken).ConfigureAwait(false);
                    await input.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                }

                foreach (var sequence in manifest.Sequences)
                {
                    await using var command = new NpgsqlCommand("SELECT setval(@name::regclass, @value, @called)", connection, transaction);
                    command.Parameters.AddWithValue("name", $"{Quote(sequence.Schema)}.{Quote(sequence.Name)}");
                    command.Parameters.AddWithValue("value", sequence.LastValue);
                    command.Parameters.AddWithValue("called", sequence.IsCalled);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await ExecuteNonQueryAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE", cancellationToken).ConfigureAwait(false);
                foreach (var foreignKey in manifest.ForeignKeys)
                {
                    var characteristics = foreignKey.Deferrable
                        ? foreignKey.InitiallyDeferred ? "DEFERRABLE INITIALLY DEFERRED" : "DEFERRABLE INITIALLY IMMEDIATE"
                        : "NOT DEFERRABLE";
                    await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE {Quote(foreignKey.Schema)}.{Quote(foreignKey.Table)} ALTER CONSTRAINT {Quote(foreignKey.Name)} {characteristics}",
                        cancellationToken).ConfigureAwait(false);
                }

                var restoredFingerprint = await ReadSchemaFingerprintAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (!FixedHashEquals(restoredFingerprint, manifest.SchemaFingerprint))
                {
                    throw new InvalidDataException("The restored PostgreSQL schema did not match the captured schema; the restore transaction was rolled back.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OperationGate.Release();
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
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), oid::bigint FROM pg_catalog.pg_database WHERE datname = current_database()", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return the current database identity.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var binding = string.Join('|', builder.Host, builder.Port.ToString(CultureInfo.InvariantCulture), builder.Database, builder.Username);
        return new DatabaseIdentity(reader.GetString(0), reader.GetInt64(1), HashString(binding));
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

    private static async Task<List<TableMetadata>> ReadTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT c.table_schema, c.table_name, c.column_name, c.ordinal_position,
                   c.udt_schema, c.udt_name, c.is_nullable, coalesce(c.column_default, ''),
                   c.is_identity, c.identity_generation, c.is_generated, coalesce(c.generation_expression, '')
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
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
                reader.GetString(11)));
        }

        return tables;
    }

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

    private static void ValidateSupportedModel(IModel model, IReadOnlyList<TableMetadata> actualTables)
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
        foreach (var table in actualTables)
        {
            var tableName = $"{table.Schema}.{table.Name}";
            var columns = table.Columns.Select(column => column.Name).Order(StringComparer.Ordinal).ToArray();
            if (!expectedColumns[tableName].SequenceEqual(columns, StringComparer.Ordinal))
            {
                throw new NotSupportedException(
                    $"The populated PostgreSQL table '{tableName}' has unsupported or missing columns. Refusing migration before mutation because an exact rollback cannot be guaranteed.");
            }
        }
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
        await using var list = new NpgsqlCommand(
            "SELECT schemaname, sequencename FROM pg_catalog.pg_sequences WHERE schemaname = 'public' ORDER BY schemaname, sequencename",
            connection,
            transaction);
        await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<(string Schema, string Name)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add((reader.GetString(0), reader.GetString(1)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (var name in names)
        {
            await using var command = new NpgsqlCommand($"SELECT last_value, is_called FROM {Quote(name.Schema)}.{Quote(name.Name)}", connection, transaction);
            await using var valueReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await valueReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            sequences.Add(new SequenceSnapshot(name.Schema, name.Name, valueReader.GetInt64(0), valueReader.GetBoolean(1)));
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
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT string_agg(definition, E'\n' ORDER BY definition)
            FROM (
              SELECT 'column|' || table_schema || '|' || table_name || '|' || column_name || '|' || ordinal_position || '|' || udt_schema || '|' || udt_name || '|' || is_nullable || '|' || coalesce(column_default, '') || '|' || is_identity || '|' || coalesce(identity_generation, '') || '|' || is_generated || '|' || coalesce(generation_expression, '') AS definition
              FROM information_schema.columns WHERE table_schema = 'public'
              UNION ALL
              SELECT 'constraint|' || n.nspname || '|' || c.relname || '|' || con.conname || '|' || pg_get_constraintdef(con.oid, true)
              FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'
              UNION ALL
              SELECT 'index|' || schemaname || '|' || tablename || '|' || indexname || '|' || indexdef FROM pg_catalog.pg_indexes WHERE schemaname = 'public'
              UNION ALL
              SELECT 'function|' || n.nspname || '|' || p.proname || '|' || pg_get_functiondef(p.oid) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public'
              UNION ALL
              SELECT 'trigger|' || n.nspname || '|' || c.relname || '|' || t.tgname || '|' || pg_get_triggerdef(t.oid, true) FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal
            ) definitions
            """;
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        var canonical = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? string.Empty;
        return HashString(canonical);
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != FormatVersion
            || !string.Equals(manifest.ProviderVersion, PluginMetadata.Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Database)
            || !Sha256Pattern().IsMatch(manifest.TargetBinding)
            || !Sha256Pattern().IsMatch(manifest.SchemaFingerprint)
            || (manifest.Empty && (manifest.HistoryOnly || manifest.Tables.Count != 0 || manifest.SchemaFile is not null))
            || (manifest.HistoryOnly && (manifest.Empty || manifest.SourceMigration is not null || manifest.SchemaFile is null || manifest.Tables.Count != 1))
            || (!manifest.Empty && !manifest.HistoryOnly && (manifest.SourceMigration is not (BaselineMigration or SuccessorMigration) || manifest.SchemaFile is null || manifest.SchemaHash is null)))
        {
            throw new InvalidDataException("The PostgreSQL migration backup manifest is incompatible or malformed; the database was not changed.");
        }
    }

    private static async Task ValidateFileAsync(string directory, string fileName, string expectedHash, CancellationToken cancellationToken)
    {
        if (!SafeFilePattern().IsMatch(fileName))
        {
            throw new InvalidDataException("The PostgreSQL migration backup contains an unsafe file name; the database was not changed.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The PostgreSQL migration backup references a missing or unsafe file; the database was not changed.");
        }

        var actualHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!FixedHashEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException("The PostgreSQL migration backup data checksum is invalid; the database was not changed.");
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
        foreach (var directory in Directory.EnumerateDirectories(root, "npgsql-v1-*.partial", SearchOption.TopDirectoryOnly))
        {
            if (PartialKeyPattern().IsMatch(Path.GetFileName(directory)))
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
            Access = FileAccess.Write,
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

    [GeneratedRegex("^(?:schema\\.sql|table-[0-9]{4}\\.copy)$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFilePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Pattern();

    private sealed record DatabaseIdentity(string Database, long DatabaseOid, string Binding);

    private sealed record BackupManifest(
        int FormatVersion,
        string ProviderVersion,
        DateTimeOffset CreatedUtc,
        string Database,
        long DatabaseOid,
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
        string GenerationExpression);

    private sealed record SequenceSnapshot(string Schema, string Name, long LastValue, bool IsCalled);

    private sealed record ForeignKeySnapshot(string Schema, string Table, string Name, bool Deferrable, bool InitiallyDeferred);
}
