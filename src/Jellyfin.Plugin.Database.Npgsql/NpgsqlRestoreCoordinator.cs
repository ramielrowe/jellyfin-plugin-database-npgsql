using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Extends the stock purge and following SaveChanges into one restore transaction.
/// </summary>
internal static class NpgsqlRestoreCoordinator
{
    private static readonly ConditionalWeakTable<JellyfinDbContext, RestoreState> States = new();

    /// <summary>Returns whether the context is inside the atomic stock restore transaction.</summary>
    /// <param name="context">The candidate Jellyfin database context.</param>
    /// <returns><see langword="true"/> when the context owns an active restore transaction.</returns>
    internal static bool IsRegistered(JellyfinDbContext context) => States.TryGetValue(context, out _);

    /// <summary>Registers the transaction opened by the provider's stock restore purge.</summary>
    /// <param name="context">The stock restore context.</param>
    /// <param name="transaction">The transaction that owns purge and restored inserts.</param>
    internal static void Register(
        JellyfinDbContext context,
        IDbContextTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!States.TryAdd(context, new RestoreState(transaction)))
        {
            throw new InvalidOperationException("A PostgreSQL restore transaction is already active for this context.");
        }
    }

    /// <summary>Commits a successful restore after reseeding every model identity.</summary>
    /// <param name="context">The restore context.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing reseed, commit, and transaction disposal.</returns>
    internal static async Task CompleteAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (context is not JellyfinDbContext jellyfinContext
            || !States.TryGetValue(jellyfinContext, out var state))
        {
            return;
        }

        try
        {
            if (NpgsqlRestoreScope.ArchivePath is not null)
            {
                await RestoreHomeSectionsAsync(jellyfinContext, cancellationToken).ConfigureAwait(false);
            }

            await NpgsqlDatabaseOperations.ReseedIdentitiesAsync(
                jellyfinContext,
                cancellationToken).ConfigureAwait(false);
            await state.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await state.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            States.Remove(jellyfinContext);
            await state.Transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Rolls back a failed or canceled restore.</summary>
    /// <param name="context">The restore context.</param>
    /// <returns>A task representing rollback and transaction disposal.</returns>
    internal static async Task FailAsync(DbContext context)
    {
        if (context is not JellyfinDbContext jellyfinContext
            || !States.TryGetValue(jellyfinContext, out var state))
        {
            return;
        }

        States.Remove(jellyfinContext);
        try
        {
            await state.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await state.Transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RestoreHomeSectionsAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var archivePath = NpgsqlRestoreScope.ArchivePath
            ?? throw new InvalidOperationException("The stock PostgreSQL restore archive path is unavailable.");
        using var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var entry = archive.GetEntry(NpgsqlBackupArchive.HomeSectionsEntryName)
            ?? throw new InvalidDataException("The bound PostgreSQL archive has no HomeSection dataset.");
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<HomeSectionRow>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (row is null)
                {
                    throw new InvalidDataException("The bound PostgreSQL HomeSection dataset contains a null row.");
                }

                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO public.\"HomeSection\" (\"Id\", \"DisplayPreferencesId\", \"Order\", \"Type\") VALUES ({row.Id}, {row.DisplayPreferencesId}, {row.Order}, {row.Type})",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record RestoreState(IDbContextTransaction Transaction);

    private sealed record HomeSectionRow(int Id, int DisplayPreferencesId, int Order, int Type);
}
