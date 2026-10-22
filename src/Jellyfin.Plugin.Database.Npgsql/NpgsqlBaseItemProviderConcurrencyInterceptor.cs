using System.Data.Common;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Resolves concurrently rewritten item provider IDs inside Jellyfin-owned transactions.
/// </summary>
/// <remarks>
/// Jellyfin rewrites an existing item's provider rows wholesale: it deletes them with
/// <c>ExecuteDelete</c>, then inserts the item's current set through <c>SaveChanges</c>,
/// all in one explicit transaction. Under READ COMMITTED, a concurrent save of the same
/// item cannot delete rows the other transaction inserted after its statement snapshot,
/// so both inserts reach the unique index. Resolve only Added providers whose owning
/// item is tracked as Modified; inserts for new items and every other provider write
/// retain EF's ordinary constraint path.
/// </remarks>
internal sealed class NpgsqlBaseItemProviderConcurrencyInterceptor : SaveChangesInterceptor
{
    private const string ResolveSql = """
        INSERT INTO public."BaseItemProviders" AS target
            ("ItemId", "ProviderId", "ProviderValue")
        VALUES (@itemId, @providerId, @providerValue)
        ON CONFLICT ("ItemId", "ProviderIdInternId") DO UPDATE SET
            "ProviderValue" = EXCLUDED."ProviderValue"
        RETURNING target."ProviderIdInternId";
        """;

    private NpgsqlBaseItemProviderConcurrencyInterceptor()
    {
    }

    /// <summary>Gets the stateless shared interceptor.</summary>
    internal static NpgsqlBaseItemProviderConcurrencyInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && context.Database.CurrentTransaction is not null)
        {
            ResolveRewrittenProviders(context);
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
            await ResolveRewrittenProvidersAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static void ResolveRewrittenProviders(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var entries = AddedProvidersOfModifiedItems(context);
        if (entries.Length == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        foreach (var entry in entries)
        {
            using var command = CreateResolveCommand(connection, transaction, entry.Entity);
            var internId = (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The item-provider resolver returned no canonical identifier."));
            entry.Property<long>("ProviderIdInternId").CurrentValue = internId;
            entry.State = EntityState.Unchanged;
        }
    }

    private static async Task ResolveRewrittenProvidersAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.DetectChanges();
        var entries = AddedProvidersOfModifiedItems(context);
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
                var internId = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The item-provider resolver returned no canonical identifier."));
                entry.Property<long>("ProviderIdInternId").CurrentValue = internId;
                entry.State = EntityState.Unchanged;
            }
        }
    }

    private static EntityEntry<BaseItemProvider>[] AddedProvidersOfModifiedItems(DbContext context)
    {
        // A new item's providers cannot be inserted ahead of EF's batch without violating
        // their foreign key, and concurrent creation already conflicts on the item itself.
        var modifiedItemIds = context.ChangeTracker
            .Entries<BaseItemEntity>()
            .Where(entry => entry.State == EntityState.Modified)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        if (modifiedItemIds.Count == 0)
        {
            return [];
        }

        return context.ChangeTracker
            .Entries<BaseItemProvider>()
            .Where(entry => entry.State == EntityState.Added && modifiedItemIds.Contains(entry.Entity.ItemId))
            .OrderBy(entry => entry.Entity.ItemId)
            .ThenBy(entry => entry.Entity.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    private static DbCommand CreateResolveCommand(
        DbConnection connection,
        DbTransaction transaction,
        BaseItemProvider provider)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ResolveSql;
        AddParameter(command, "itemId", provider.ItemId);
        AddParameter(command, "providerId", provider.ProviderId);
        AddParameter(command, "providerValue", provider.ProviderValue);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
