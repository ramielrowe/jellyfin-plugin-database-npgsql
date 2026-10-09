using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Gives stock logical backups one PostgreSQL snapshot across every table.
/// </summary>
internal sealed class NpgsqlBackupTransactionInterceptor : DbTransactionInterceptor
{
    private NpgsqlBackupTransactionInterceptor()
    {
    }

    /// <summary>Gets the shared stateless interceptor.</summary>
    internal static NpgsqlBackupTransactionInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        if (!NpgsqlBackupScope.IsActive || result.HasResult)
        {
            return result;
        }

        var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        NpgsqlBackupScope.SetHomeSectionsJson(CaptureHomeSections(connection, transaction));
        return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (!NpgsqlBackupScope.IsActive || result.HasResult)
        {
            return result;
        }

        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        NpgsqlBackupScope.SetHomeSectionsJson(
            await CaptureHomeSectionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false));
        return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
    }

    private static string CaptureHomeSections(DbConnection connection, DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT \"Id\", \"DisplayPreferencesId\", \"Order\", \"Type\" FROM public.\"HomeSection\" ORDER BY \"Id\"";
        using var reader = command.ExecuteReader();
        var rows = new List<HomeSectionRow>();
        while (reader.Read())
        {
            rows.Add(new HomeSectionRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return JsonSerializer.Serialize(rows);
    }

    private static async Task<string> CaptureHomeSectionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT \"Id\", \"DisplayPreferencesId\", \"Order\", \"Type\" FROM public.\"HomeSection\" ORDER BY \"Id\"";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<HomeSectionRow>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(new HomeSectionRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
                }

                return JsonSerializer.Serialize(rows);
            }
        }
    }

    private sealed record HomeSectionRow(int Id, int DisplayPreferencesId, int Order, int Type);
}
