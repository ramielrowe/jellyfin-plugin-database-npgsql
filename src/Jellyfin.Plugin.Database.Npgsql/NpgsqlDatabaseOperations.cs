using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Contains Npgsql operations that are reachable only after package validation succeeds.
/// </summary>
internal static class NpgsqlDatabaseOperations
{
    /// <summary>
    /// Runs bounded, allowlisted PostgreSQL maintenance against model-owned tables.
    /// </summary>
    /// <param name="factory">The initialized Jellyfin context factory.</param>
    /// <param name="command">The allowlisted maintenance command.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing maintenance.</returns>
    internal static async Task ExecuteMaintenanceAsync(
        IDbContextFactory<JellyfinDbContext> factory,
        string command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command is not ("ANALYZE" or "VACUUM (ANALYZE)"))
        {
            throw new ArgumentException("The PostgreSQL maintenance command is not allowlisted.", nameof(command));
        }

        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var sql = BuildMaintenanceSql(context.Model, command);
            if (sql is not null)
            {
                // VACUUM intentionally runs without a transaction. The command
                // timeout configured during Initialise bounds the server work.
                await context.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Purges only tables mapped by the supplied Jellyfin model.
    /// </summary>
    /// <param name="dbContext">The active Jellyfin context.</param>
    /// <param name="tableNames">Mapped schema-qualified table names, or null for all mapped tables.</param>
    /// <returns>A task representing the purge.</returns>
    internal static async Task PurgeDatabaseAsync(
        JellyfinDbContext dbContext,
        IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var requestedNames = tableNames?.Distinct(StringComparer.Ordinal).ToArray();
        var sql = BuildPurgeSql(dbContext.Model, requestedNames);
        if (sql is null)
        {
            return;
        }

        if (NpgsqlRestoreScope.IsActive)
        {
            await PurgeForStockRestoreAsync(dbContext, requestedNames).ConfigureAwait(false);
            return;
        }

        var stockTables = GetStockTableNames(dbContext);
        if (requestedNames is not null
            && requestedNames.Order(StringComparer.Ordinal).SequenceEqual(stockTables, StringComparer.Ordinal))
        {
            await RepairExpectedHistoryAsync(
                dbContext,
                dbContext.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray()).ConfigureAwait(false);
            throw new NotSupportedException(
                "Refusing an unvalidated stock PostgreSQL restore purge because the regular Npgsql plugin safety decorator is inactive. "
                + "The expected migration history was repaired and application tables were not purged.");
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false)
            : null;

        await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync().ConfigureAwait(false);
        }
    }

    private static async Task PurgeForStockRestoreAsync(
        JellyfinDbContext dbContext,
        string[]? requestedNames)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Stock PostgreSQL restore unexpectedly entered PurgeDatabase with an existing transaction.");
        }

        if (NpgsqlRestoreScope.ArchivePath is { } archivePath)
        {
            // Validate in the early-provider load context too. This makes the
            // process-wide Activity carrier a signal, not an authorization.
            await NpgsqlBackupArchive.ValidateAsync(archivePath, CancellationToken.None).ConfigureAwait(false);
        }

        var stockTables = GetStockTableNames(dbContext);
        var actualRequestedNames = requestedNames?.Order(StringComparer.Ordinal).ToArray() ?? stockTables;
        if (!actualRequestedNames.SequenceEqual(stockTables, StringComparer.Ordinal))
        {
            var missing = stockTables.Except(actualRequestedNames, StringComparer.Ordinal);
            var unknown = actualRequestedNames.Except(stockTables, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Stock PostgreSQL restore did not request the exact audited Jellyfin table set. Missing: [{string.Join(", ", missing)}]. "
                + $"Unknown: [{string.Join(", ", unknown)}].");
        }

        var pluginMigrations = dbContext.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        var expectedHistory = NpgsqlHostMigrationAudit.AuditedMigrationIds
            .Concat(pluginMigrations)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualHistory = (await dbContext.Database.GetAppliedMigrationsAsync().ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualHistory.SequenceEqual(expectedHistory, StringComparer.Ordinal))
        {
            await RepairExpectedHistoryAsync(dbContext, pluginMigrations).ConfigureAwait(false);
            var missing = expectedHistory.Except(actualHistory, StringComparer.Ordinal);
            var unknown = actualHistory.Except(expectedHistory, StringComparer.Ordinal);
            throw new NotSupportedException(
                $"The PostgreSQL restore archive has an incompatible migration lineage. Missing: [{string.Join(", ", missing)}]. "
                + $"Unknown: [{string.Join(", ", unknown)}]. The current lineage was repaired and application tables were not purged.");
        }

        var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            // Stock enumerates only public DbSet properties. HomeSection is a
            // separately mapped navigation table and must be purged with its
            // owning DisplayPreferences rows to satisfy its foreign key.
            var purgeSql = BuildPurgeSql(dbContext.Model, null)
                ?? throw new InvalidOperationException("The audited Jellyfin model contains no tables to restore.");
            purgeSql = purgeSql.Replace(
                " RESTART IDENTITY",
                $", {QuoteTableName("public", "__JellyfinTextNodes")} RESTART IDENTITY",
                StringComparison.Ordinal);
            await dbContext.Database.ExecuteSqlRawAsync(purgeSql).ConfigureAwait(false);
            NpgsqlRestoreCoordinator.Register(dbContext, transaction);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RepairExpectedHistoryAsync(
        JellyfinDbContext dbContext,
        IReadOnlyList<string> pluginMigrations)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM public.\"__EFMigrationsHistory\"").ConfigureAwait(false);
        foreach (var migrationId in NpgsqlHostMigrationAudit.AuditedMigrationIds)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {PluginMetadata.SupportedDatabaseAbi})").ConfigureAwait(false);
        }

        foreach (var migrationId in pluginMigrations)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {ProductInfo.GetVersion()})").ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static string[] GetStockTableNames(JellyfinDbContext dbContext)
        => typeof(JellyfinDbContext)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
            .Select(property => dbContext.Model.FindEntityType(property.PropertyType.GetGenericArguments()[0])!.GetSchemaQualifiedTableName()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Gets every identity column owned by the finalized model.
    /// </summary>
    /// <param name="model">The finalized Jellyfin model.</param>
    /// <returns>Exact-schema-qualified, model-derived identity columns.</returns>
    internal static IReadOnlyList<IdentityColumn> GetIdentityColumns(IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var columns = new HashSet<(string Schema, string Table, string Column)>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var schema = entityType.GetSchema() ?? "public";
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                var strategy = property.FindAnnotation("Npgsql:ValueGenerationStrategy")?.Value?.ToString();
                if (strategy is null || !strategy.StartsWith("Identity", StringComparison.Ordinal))
                {
                    continue;
                }

                var column = property.GetColumnName(storeObject)
                    ?? throw new InvalidOperationException($"Identity property '{property.DeclaringType.Name}.{property.Name}' has no table column.");
                columns.Add((schema, table, column));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException("The audited Jellyfin PostgreSQL model unexpectedly contains no identity columns.");
        }

        // The bounded long-text trie is intentionally provider-owned rather
        // than part of Jellyfin's CLR model. Stock backup restores its logical
        // source strings, so triggers rebuild the trie and its identity too.
        columns.Add(("public", "__JellyfinTextNodes", "Id"));

        return columns.OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Table, StringComparer.Ordinal)
            .ThenBy(item => item.Column, StringComparer.Ordinal)
            .Select(item => new IdentityColumn(item.Schema, item.Table, item.Column))
            .ToArray();
    }

    /// <summary>
    /// Transactionally positions every identity sequence after restored explicit values.
    /// </summary>
    /// <remarks>
    /// PostgreSQL <c>setval</c> is deliberately forbidden here because its effects survive
    /// transaction rollback. <c>ALTER SEQUENCE ... RESTART WITH</c> is transactional, so a
    /// cancellation, broken connection, later reseed failure, or commit failure restores the
    /// exact pre-restore <c>last_value</c>/<c>is_called</c> state together with the tables.
    /// All target values are calculated and validated before the first sequence is changed.
    /// </remarks>
    /// <param name="context">The active stock restore context and transaction.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing validation and transactional reseeding.</returns>
    internal static async Task ReseedIdentitiesAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("PostgreSQL restore identity repair requires the active restore transaction.");
        var connection = context.Database.GetDbConnection();
        var plans = new List<IdentityRestart>();
        foreach (var identity in GetIdentityColumns(context.Model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = $"{QuoteIdentifier(identity.Schema)}.{QuoteIdentifier(identity.Table)}";
            var column = QuoteIdentifier(identity.Column);
            var tableLiteral = table.Replace("'", "''", StringComparison.Ordinal);
            var columnLiteral = identity.Column.Replace("'", "''", StringComparison.Ordinal);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT sequence_namespace.nspname,
                       sequence_class.relname,
                       sequence.seqmin,
                       sequence.seqmax,
                       sequence.seqincrement,
                       sequence.seqstart,
                       sequence.seqcycle,
                       values.minimum_value,
                       values.maximum_value
                  FROM pg_catalog.pg_sequence AS sequence
                  JOIN pg_catalog.pg_class AS sequence_class ON sequence_class.oid = sequence.seqrelid
                  JOIN pg_catalog.pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence_class.relnamespace
                 CROSS JOIN (
                       SELECT pg_catalog.min({column})::bigint AS minimum_value,
                              pg_catalog.max({column})::bigint AS maximum_value
                         FROM {table}
                 ) AS values
                 WHERE sequence.seqrelid = pg_catalog.pg_get_serial_sequence('{tableLiteral}', '{columnLiteral}')::pg_catalog.regclass
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Identity column '{identity.Schema}.{identity.Table}.{identity.Column}' has no owned PostgreSQL sequence.");
            }

            var sequenceSchema = reader.GetString(0);
            var sequenceName = reader.GetString(1);
            var minimum = reader.GetInt64(2);
            var maximum = reader.GetInt64(3);
            var increment = reader.GetInt64(4);
            var start = reader.GetInt64(5);
            var cycle = reader.GetBoolean(6);
            var tableMinimum = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? (long?)null
                : reader.GetInt64(7);
            var tableMaximum = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                ? (long?)null
                : reader.GetInt64(8);
            await reader.DisposeAsync().ConfigureAwait(false);

            var target = CalculateIdentityRestart(
                identity,
                minimum,
                maximum,
                increment,
                start,
                cycle,
                tableMinimum,
                tableMaximum);
            plans.Add(new IdentityRestart(sequenceSchema, sequenceName, target));
        }

        var restartOrdinal = 0;
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"ALTER SEQUENCE {QuoteIdentifier(plan.Schema)}.{QuoteIdentifier(plan.Name)} RESTART WITH {plan.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            restartOrdinal++;
            await NpgsqlRestoreScope.ReachIdentityRestartCheckpointAsync(
                restartOrdinal,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static long CalculateIdentityRestart(
        IdentityColumn identity,
        long sequenceMinimum,
        long sequenceMaximum,
        long increment,
        long start,
        bool cycle,
        long? tableMinimum,
        long? tableMaximum)
    {
        if (increment == 0)
        {
            throw new InvalidOperationException($"Identity sequence for '{identity}' has an invalid zero increment.");
        }

        if (tableMinimum is null)
        {
            return start;
        }

        long target;
        try
        {
            target = increment > 0
                ? checked(tableMaximum!.Value + increment)
                : checked(tableMinimum.Value + increment);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException($"Identity values for '{identity}' cannot be advanced without overflow.", exception);
        }

        target = increment > 0 ? Math.Max(start, target) : Math.Min(start, target);
        if (target > sequenceMaximum || target < sequenceMinimum)
        {
            if (!cycle)
            {
                throw new InvalidOperationException(
                    $"Restored identities for '{identity}' exhaust its non-cycling sequence range before any sequence was changed.");
            }

            target = increment > 0 ? sequenceMinimum : sequenceMaximum;
            if (target >= tableMinimum.Value && target <= tableMaximum!.Value)
            {
                throw new InvalidOperationException(
                    $"Restored identities for cycling sequence '{identity}' leave no provably collision-free next value.");
            }
        }

        return target;
    }

    /// <summary>
    /// Retires only the pool identified by this provider's normalized connection string.
    /// </summary>
    /// <param name="connectionString">The normalized connection string.</param>
    internal static void ClearPool(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    /// <summary>
    /// Builds maintenance SQL for model-owned tables.
    /// </summary>
    /// <param name="model">The finalized Jellyfin model.</param>
    /// <param name="command">An allowlisted PostgreSQL maintenance command.</param>
    /// <returns>The SQL, or null when the model contains no tables.</returns>
    internal static string? BuildMaintenanceSql(IReadOnlyModel model, string command)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (command is not ("ANALYZE" or "VACUUM (ANALYZE)"))
        {
            throw new ArgumentException("The PostgreSQL maintenance command is not allowlisted.", nameof(command));
        }

        var tables = GetModelTables(model).Values
            .Select(table => QuoteTableName(table.Schema, table.Table))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return tables.Length == 0 ? null : $"{command} {string.Join(", ", tables)}";
    }

    /// <summary>
    /// Builds purge SQL after validating every requested table against the model.
    /// </summary>
    /// <param name="model">The finalized Jellyfin model.</param>
    /// <param name="tableNames">Mapped schema-qualified table names, or null for all mapped tables.</param>
    /// <returns>The SQL, or null when no tables were requested.</returns>
    internal static string? BuildPurgeSql(IReadOnlyModel model, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(model);
        var modelTables = GetModelTables(model);
        var requestedNames = tableNames?.Distinct(StringComparer.Ordinal).ToArray()
            ?? modelTables.Keys.Order(StringComparer.Ordinal).ToArray();
        if (requestedNames.Length == 0)
        {
            return null;
        }

        var quotedTables = new List<string>(requestedNames.Length);
        foreach (var requestedName in requestedNames)
        {
            if (!modelTables.TryGetValue(requestedName, out var table))
            {
                throw new InvalidOperationException(
                    $"Refusing to purge PostgreSQL table '{requestedName}' because it is not mapped by the Jellyfin database model.");
            }

            quotedTables.Add(QuoteTableName(table.Schema, table.Table));
        }

        // A single statement lets PostgreSQL validate every foreign-key
        // relationship before mutation. Deliberately omit CASCADE.
        return $"TRUNCATE TABLE {string.Join(", ", quotedTables)} RESTART IDENTITY";
    }

    /// <summary>
    /// Quotes a PostgreSQL identifier by doubling embedded quote characters.
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>A quoted identifier.</returns>
    internal static string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static Dictionary<string, (string? Schema, string Table)> GetModelTables(IReadOnlyModel model)
        => model.GetEntityTypes()
            .Select(entityType => new
            {
                SchemaQualifiedName = entityType.GetSchemaQualifiedTableName(),
                Schema = entityType.GetSchema(),
                Table = entityType.GetTableName(),
            })
            .Where(table => table.SchemaQualifiedName is not null && table.Table is not null)
            .GroupBy(table => table.SchemaQualifiedName!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (group.First().Schema, group.First().Table!),
                StringComparer.Ordinal);

    private static string QuoteTableName(string? schema, string table)
        => $"{QuoteIdentifier(string.IsNullOrEmpty(schema) ? "public" : schema)}.{QuoteIdentifier(table)}";

    /// <summary>Identifies one exact model-owned identity column.</summary>
    /// <param name="Schema">The PostgreSQL schema.</param>
    /// <param name="Table">The PostgreSQL table.</param>
    /// <param name="Column">The PostgreSQL identity column.</param>
    internal sealed record IdentityColumn(string Schema, string Table, string Column);

    private sealed record IdentityRestart(string Schema, string Name, long Value);
}
