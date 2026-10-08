using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
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
        var sql = BuildPurgeSql(dbContext.Model, tableNames);
        if (sql is null)
        {
            return;
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
