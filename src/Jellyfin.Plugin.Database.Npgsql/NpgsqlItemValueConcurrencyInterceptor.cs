using System.Data.Common;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Resolves concurrently discovered item values inside Jellyfin-owned transactions.
/// </summary>
/// <remarks>
/// Jellyfin reads existing item values and creates missing values and their maps in one
/// explicit transaction. Two transactions can therefore both create the same logical
/// value. PostgreSQL must choose one physical row, and the losing transaction must use
/// that row's identifier in its pending maps. Replaying only <c>SaveChanges</c> cannot
/// do that safely because the losing change tracker still contains its proposed ID.
/// </remarks>
internal sealed class NpgsqlItemValueConcurrencyInterceptor : SaveChangesInterceptor
{
    private const string ResolveSql = """
        INSERT INTO public."ItemValues" AS target
            ("ItemValueId", "CleanValue", "Type", "Value")
        VALUES (@itemValueId, @cleanValue, @type, @value)
        ON CONFLICT ("Type", "ValueInternId") DO UPDATE
            SET "ItemValueId" = target."ItemValueId"
        RETURNING target."ItemValueId", target."ValueInternId";
        """;

    private NpgsqlItemValueConcurrencyInterceptor()
    {
    }

    /// <summary>Gets the stateless shared interceptor.</summary>
    internal static NpgsqlItemValueConcurrencyInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && context.Database.CurrentTransaction is not null)
        {
            ResolveAddedItemValues(context);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && context.Database.CurrentTransaction is not null)
        {
            await ResolveAddedItemValuesAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static void ResolveAddedItemValues(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var mapEntries = AddedItemValueMaps(context);
        var entries = AddedMappedItemValues(context, mapEntries);
        if (entries.Length == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        foreach (var entry in entries)
        {
            using var command = CreateResolveCommand(connection, transaction, entry.Entity);
            using var reader = command.ExecuteReader();
            var resolved = ReadResolvedValue(reader);
            RebindAndDetach(entry, resolved, mapEntries);
        }
    }

    private static async Task ResolveAddedItemValuesAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.DetectChanges();
        var mapEntries = AddedItemValueMaps(context);
        var entries = AddedMappedItemValues(context, mapEntries);
        if (entries.Length == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        foreach (var entry in entries)
        {
            var command = CreateResolveCommand(connection, transaction, entry.Entity);
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var resolved = await ReadResolvedValueAsync(reader, cancellationToken).ConfigureAwait(false);
                    RebindAndDetach(entry, resolved, mapEntries);
                }
            }
        }
    }

    private static EntityEntry<ItemValue>[] AddedMappedItemValues(
        DbContext context,
        IReadOnlyCollection<EntityEntry<ItemValueMap>> mapEntries)
        => context.ChangeTracker
            .Entries<ItemValue>()
            .Where(entry => entry.State == EntityState.Added)
            .Where(entry => mapEntries.Any(map => map.Entity.ItemValueId == entry.Entity.ItemValueId))
            .OrderBy(entry => entry.Entity.Type)
            .ThenBy(entry => entry.Entity.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Entity.ItemValueId)
            .ToArray();

    private static EntityEntry<ItemValueMap>[] AddedItemValueMaps(DbContext context)
        => context.ChangeTracker
            .Entries<ItemValueMap>()
            .Where(entry => entry.State == EntityState.Added)
            .ToArray();

    private static DbCommand CreateResolveCommand(
        DbConnection connection,
        DbTransaction transaction,
        ItemValue itemValue)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ResolveSql;
        AddParameter(command, "itemValueId", itemValue.ItemValueId);
        AddParameter(command, "cleanValue", itemValue.CleanValue);
        AddParameter(command, "type", (int)itemValue.Type);
        AddParameter(command, "value", itemValue.Value);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static ResolvedItemValue ReadResolvedValue(DbDataReader reader)
    {
        if (!reader.Read())
        {
            throw new InvalidOperationException("The item-value resolver returned no canonical identifier.");
        }

        return new ResolvedItemValue(reader.GetGuid(0), reader.GetInt64(1));
    }

    private static async Task<ResolvedItemValue> ReadResolvedValueAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The item-value resolver returned no canonical identifier.");
        }

        return new ResolvedItemValue(reader.GetGuid(0), reader.GetInt64(1));
    }

    private static void RebindAndDetach(
        EntityEntry<ItemValue> itemValueEntry,
        ResolvedItemValue resolved,
        IEnumerable<EntityEntry<ItemValueMap>> mapEntries)
    {
        var proposedId = itemValueEntry.Entity.ItemValueId;
        var dependentMaps = mapEntries
            .Where(entry => entry.Entity.ItemValueId == proposedId)
            .ToArray();
        if (resolved.Id == proposedId)
        {
            // This transaction inserted the canonical row directly. Marking it
            // unchanged preserves EF relationship fixup without a second INSERT.
            itemValueEntry.Property<long>("ValueInternId").CurrentValue = resolved.InternId;
            itemValueEntry.State = EntityState.Unchanged;
            return;
        }

        // Detaching a losing principal also detaches its Added dependants through
        // relationship fixup. Re-add each map after replacing the stale foreign key.
        itemValueEntry.State = EntityState.Detached;
        foreach (var mapEntry in dependentMaps)
        {
            mapEntry.Entity.ItemValue = null!;
            mapEntry.Entity.ItemValueId = resolved.Id;
            mapEntry.State = EntityState.Added;
        }
    }

    private sealed record ResolvedItemValue(Guid Id, long InternId);
}
