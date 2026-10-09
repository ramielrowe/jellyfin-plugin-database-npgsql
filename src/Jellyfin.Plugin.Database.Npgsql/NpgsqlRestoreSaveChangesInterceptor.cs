using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Finishes or rolls back the restore transaction around stock SaveChanges.
/// </summary>
internal sealed class NpgsqlRestoreSaveChangesInterceptor : SaveChangesInterceptor
{
    private NpgsqlRestoreSaveChangesInterceptor()
    {
    }

    /// <summary>Gets the shared stateless interceptor.</summary>
    internal static NpgsqlRestoreSaveChangesInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is Jellyfin.Database.Implementations.JellyfinDbContext context
            && NpgsqlRestoreCoordinator.IsRegistered(context))
        {
            await NpgsqlRestoreIdentityHydrator.HydrateAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await NpgsqlRestoreCoordinator.PrepareAsync(
                eventData.Context,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await NpgsqlRestoreCoordinator.FailAsync(eventData.Context).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await NpgsqlRestoreCoordinator.FailAsync(eventData.Context).ConfigureAwait(false);
        }
    }
}
