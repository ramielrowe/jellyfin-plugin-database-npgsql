using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Owns the external transaction which spans the complete stock restore call.</summary>
#pragma warning disable SA1600 // Internal transaction members are documented as one coordinated lifecycle.
#pragma warning disable CA2000 // Ownership transfers to RestoreState on success and is disposed on every failure.
#pragma warning disable CA2007 // Await-using resource acquisition cannot carry ConfigureAwait inline.
internal static class NpgsqlRestoreCoordinator
{
    private static readonly ConditionalWeakTable<JellyfinDbContext, RestoreState> States = new();

    internal static bool IsRegistered(JellyfinDbContext context) => States.TryGetValue(context, out _);

    internal static void RegisterLegacy(JellyfinDbContext context, IDbContextTransaction transaction)
    {
        var state = new RestoreState(transaction);
        if (!States.TryAdd(context, state))
        {
            throw new InvalidOperationException("A PostgreSQL restore transaction is already active for this context.");
        }

        NpgsqlRestoreScope.SetTransactionState(state);
    }

    internal static async Task BeginAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!NpgsqlRestoreScope.IsActive)
        {
            return;
        }

        if (NpgsqlRestoreScope.GetTransactionState<RestoreState>() is not null)
        {
            throw new InvalidOperationException("The stock PostgreSQL restore requested more than one database context.");
        }

        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The PostgreSQL restore context has no connection string.");
        var connection = new NpgsqlConnection(connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            context.Database.SetDbConnection(connection, contextOwnsConnection: false);
            _ = await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            var state = new RestoreState(connection, transaction, NpgsqlRestoreScope.RestoreId);
            if (!States.TryAdd(context, state))
            {
                throw new InvalidOperationException("A PostgreSQL restore transaction is already active for this context.");
            }

            NpgsqlRestoreScope.SetTransactionState(state);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task PrepareAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (context is not JellyfinDbContext jellyfinContext
            || !States.TryGetValue(jellyfinContext, out var state))
        {
            return;
        }

        if (state.Prepared)
        {
            return;
        }

        try
        {
            if (NpgsqlRestoreScope.ArchivePath is not null)
            {
                await RestoreHomeSectionsAsync(jellyfinContext, cancellationToken).ConfigureAwait(false);
            }

            await NpgsqlDatabaseOperations.ReseedIdentitiesAsync(jellyfinContext, cancellationToken).ConfigureAwait(false);
            if (state.RestoreId is { } restoreId)
            {
                await jellyfinContext.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({GetMarkerId(restoreId)}, {ProductInfo.GetVersion()})",
                    cancellationToken).ConfigureAwait(false);
            }

            state.Prepared = true;
        }
        catch
        {
            await RollbackAsync(state).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task CommitCurrentAsync(CancellationToken cancellationToken)
    {
        var state = NpgsqlRestoreScope.GetTransactionState<RestoreState>()
            ?? throw new InvalidOperationException("The stock PostgreSQL restore returned without an active database transaction.");
        if (!state.Prepared)
        {
            throw new InvalidOperationException("The stock PostgreSQL restore returned before its database rows were prepared.");
        }

        try
        {
            await state.CommitAsync(cancellationToken).ConfigureAwait(false);
            state.Completed = true;
        }
#pragma warning disable CA1031 // A confirmed durable marker intentionally converts lost commit acknowledgement to success.
        catch (Exception commitException)
#pragma warning restore CA1031
        {
            bool committed;
            try
            {
                using var resolutionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                committed = state.RestoreId is { } restoreId
                    && state.ConnectionString is { } connectionString
                    && await MarkerCommittedAsync(
                        connectionString,
                        restoreId,
                        resolutionDeadline.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any inability to resolve commit status requires durable startup recovery.
            catch (Exception resolutionException)
#pragma warning restore CA1031
            {
                state.Completed = true;
                try
                {
                    await DisposeAsync(state).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Recovery-required state must preserve the resolution failure even if disposal also fails.
                catch
#pragma warning restore CA1031
                {
                    // Startup recovery owns the durable commit-intent journal.
                }

                throw new NpgsqlRestoreRecoveryRequiredException(
                    "PostgreSQL restore commit acknowledgement is indeterminate and the durable marker could not be queried. The host must stop; startup recovery will reconcile the journal before database use.",
                    new AggregateException(commitException, resolutionException));
            }

            if (!committed)
            {
                try
                {
                    await state.RollbackAsync().ConfigureAwait(false);
                }
                finally
                {
                    state.Completed = true;
                    await DisposeAsync(state).ConfigureAwait(false);
                }

                ExceptionDispatchInfo.Capture(commitException).Throw();
            }

            state.Completed = true;
            await DisposeAsync(state).ConfigureAwait(false);
            return;
        }

        try
        {
            await DisposeAsync(state).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A confirmed commit is the success boundary; cleanup cannot retroactively report restore failure.
        catch
#pragma warning restore CA1031
        {
            // The transaction is already durably committed. Reporting a
            // disposal failure would make callers compensate files while the
            // database can no longer roll back, manufacturing a mixed result.
        }
    }

    internal static async Task RollbackCurrentAsync()
    {
        var state = NpgsqlRestoreScope.GetTransactionState<RestoreState>();
        if (state is not null)
        {
            await RollbackAsync(state).ConfigureAwait(false);
        }
    }

    internal static async Task FailAsync(DbContext context)
    {
        if (context is JellyfinDbContext jellyfinContext
            && States.TryGetValue(jellyfinContext, out var state))
        {
            await RollbackAsync(state).ConfigureAwait(false);
        }
    }

    internal static string GetMarkerId(Guid restoreId)
        => "99991231235959_JellyfinNpgsqlRestore_" + restoreId.ToString("N");

    internal static async Task<bool> MarkerCommittedAsync(
        string connectionString,
        Guid restoreId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = $1)",
            connection);
        _ = command.Parameters.AddWithValue(GetMarkerId(restoreId));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal static async Task DeleteMarkerAsync(
        string connectionString,
        Guid restoreId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = $1",
            connection);
        _ = command.Parameters.AddWithValue(GetMarkerId(restoreId));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(RestoreState state)
    {
        if (state.Completed)
        {
            return;
        }

        try
        {
            await state.RollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            state.Completed = true;
            await DisposeAsync(state).ConfigureAwait(false);
        }
    }

    private static async Task DisposeAsync(RestoreState state)
    {
        NpgsqlRestoreScope.ClearTransactionState();
        await state.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task RestoreHomeSectionsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
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

    internal sealed class RestoreState
    {
        private readonly NpgsqlConnection? connection;
        private readonly NpgsqlTransaction? transaction;
        private readonly IDbContextTransaction? legacyTransaction;

        internal RestoreState(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? restoreId)
        {
            this.connection = connection;
            this.transaction = transaction;
            this.RestoreId = restoreId;
        }

        internal RestoreState(IDbContextTransaction legacyTransaction)
        {
            this.legacyTransaction = legacyTransaction;
        }

        internal bool Prepared { get; set; }

        internal Guid? RestoreId { get; }

        internal string? ConnectionString => this.connection?.ConnectionString;

        internal bool Completed { get; set; }

        internal Task CommitAsync(CancellationToken cancellationToken)
            => this.legacyTransaction is null
                ? this.transaction!.CommitAsync(cancellationToken)
                : this.legacyTransaction.CommitAsync(cancellationToken);

        internal Task RollbackAsync()
            => this.legacyTransaction is null
                ? this.transaction!.RollbackAsync(CancellationToken.None)
                : this.legacyTransaction.RollbackAsync(CancellationToken.None);

        internal async Task DisposeAsync()
        {
            if (this.legacyTransaction is not null)
            {
                await this.legacyTransaction.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await this.transaction!.DisposeAsync().ConfigureAwait(false);
            await this.connection!.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record HomeSectionRow(int Id, int DisplayPreferencesId, int Order, int Type);

#pragma warning disable CA1032 // Internal sentinel is constructed only with both recovery context and cause.
    internal sealed class NpgsqlRestoreRecoveryRequiredException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);
#pragma warning restore CA1032
}
#pragma warning restore CA2000
#pragma warning restore CA2007
#pragma warning restore SA1600
