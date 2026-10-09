using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
            var reseedSql = BuildIdentityReseedSql(dbContext.Model);
            NpgsqlRestoreCoordinator.Register(dbContext, transaction, reseedSql);
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
    /// Builds sequence repair statements for every identity column owned by the finalized model.
    /// </summary>
    /// <param name="model">The finalized Jellyfin model.</param>
    /// <returns>Exact-schema-qualified, model-derived reseed SQL.</returns>
    internal static string BuildIdentityReseedSql(IReadOnlyModel model)
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

        return string.Join(
            ";\n",
            columns.OrderBy(item => item.Schema, StringComparer.Ordinal)
                .ThenBy(item => item.Table, StringComparer.Ordinal)
                .ThenBy(item => item.Column, StringComparer.Ordinal)
                .Select(item =>
                {
                    var qualifiedTable = $"{QuoteIdentifier(item.Schema)}.{QuoteIdentifier(item.Table)}";
                    var column = QuoteIdentifier(item.Column);
                    var tableLiteral = qualifiedTable.Replace("'", "''", StringComparison.Ordinal);
                    var columnLiteral = item.Column.Replace("'", "''", StringComparison.Ordinal);
                    return $"SELECT pg_catalog.setval(pg_catalog.pg_get_serial_sequence('{tableLiteral}', '{columnLiteral}')::pg_catalog.regclass, COALESCE(pg_catalog.max({column})::bigint, 1), pg_catalog.max({column}) IS NOT NULL) FROM {qualifiedTable}";
                })) + ";";
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
}
