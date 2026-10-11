using System.Data.Common;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Database.Npgsql.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Resolves concurrently inserted user-data snapshots inside Jellyfin-owned transactions.
/// </summary>
/// <remarks>
/// Jellyfin checks for an existing logical user-data key before tracking an insert or
/// update. The check and write are intentionally in one explicit transaction, but two
/// playback requests can still both observe a missing row. Resolve only Added entries;
/// ordinary tracked updates retain Jellyfin's existing last-writer-wins behavior.
/// </remarks>
internal sealed class NpgsqlUserDataConcurrencyInterceptor : SaveChangesInterceptor
{
    private const string ResolveSql = """
        INSERT INTO public."UserData" AS target
            ("CustomDataKey", "ItemId", "UserId", "Rating", "PlaybackPositionTicks",
             "PlayCount", "IsFavorite", "LastPlayedDate", "Played", "AudioStreamIndex",
             "SubtitleStreamIndex", "Likes", "RetentionDate")
        VALUES
            (@customDataKey, @itemId, @userId, @rating, @playbackPositionTicks,
             @playCount, @isFavorite, @lastPlayedDate, @played, @audioStreamIndex,
             @subtitleStreamIndex, @likes, @retentionDate)
        ON CONFLICT ("ItemId", "UserId", "CustomDataKeyInternId") DO UPDATE SET
            "Rating" = EXCLUDED."Rating",
            "PlaybackPositionTicks" = EXCLUDED."PlaybackPositionTicks",
            "PlayCount" = EXCLUDED."PlayCount",
            "IsFavorite" = EXCLUDED."IsFavorite",
            "LastPlayedDate" = EXCLUDED."LastPlayedDate",
            "Played" = EXCLUDED."Played",
            "AudioStreamIndex" = EXCLUDED."AudioStreamIndex",
            "SubtitleStreamIndex" = EXCLUDED."SubtitleStreamIndex",
            "Likes" = EXCLUDED."Likes",
            "RetentionDate" = EXCLUDED."RetentionDate"
        RETURNING target."CustomDataKeyInternId";
        """;

    private NpgsqlUserDataConcurrencyInterceptor()
    {
    }

    /// <summary>Gets the stateless shared interceptor.</summary>
    internal static NpgsqlUserDataConcurrencyInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && context.Database.CurrentTransaction is not null)
        {
            ResolveAddedUserData(context);
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
            await ResolveAddedUserDataAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static void ResolveAddedUserData(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var entries = AddedUserData(context);
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
                ?? throw new InvalidOperationException("The user-data resolver returned no canonical identifier."));
            entry.Property<long>("CustomDataKeyInternId").CurrentValue = internId;
            entry.State = EntityState.Unchanged;
        }
    }

    private static async Task ResolveAddedUserDataAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.DetectChanges();
        var entries = AddedUserData(context);
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
                    ?? throw new InvalidOperationException("The user-data resolver returned no canonical identifier."));
                entry.Property<long>("CustomDataKeyInternId").CurrentValue = internId;
                entry.State = EntityState.Unchanged;
            }
        }
    }

    private static Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<UserData>[] AddedUserData(DbContext context)
        => context.ChangeTracker
            .Entries<UserData>()
            .Where(entry => entry.State == EntityState.Added)
            .OrderBy(entry => entry.Entity.ItemId)
            .ThenBy(entry => entry.Entity.UserId)
            .ThenBy(entry => entry.Entity.CustomDataKey, StringComparer.Ordinal)
            .ToArray();

    private static DbCommand CreateResolveCommand(
        DbConnection connection,
        DbTransaction transaction,
        UserData userData)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ResolveSql;
        AddParameter(command, "customDataKey", userData.CustomDataKey);
        AddParameter(command, "itemId", userData.ItemId);
        AddParameter(command, "userId", userData.UserId);
        AddParameter(command, "rating", userData.Rating);
        AddParameter(command, "playbackPositionTicks", userData.PlaybackPositionTicks);
        AddParameter(command, "playCount", userData.PlayCount);
        AddParameter(command, "isFavorite", userData.IsFavorite);
        AddParameter(command, "lastPlayedDate", NormalizeUtc(userData.LastPlayedDate));
        AddParameter(command, "played", userData.Played);
        AddParameter(command, "audioStreamIndex", userData.AudioStreamIndex);
        AddParameter(command, "subtitleStreamIndex", userData.SubtitleStreamIndex);
        AddParameter(command, "likes", userData.Likes);
        AddParameter(command, "retentionDate", NormalizeUtc(userData.RetentionDate));
        return command;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue ? UtcDateTimeValueConverter.Normalize(value.Value) : null;

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
