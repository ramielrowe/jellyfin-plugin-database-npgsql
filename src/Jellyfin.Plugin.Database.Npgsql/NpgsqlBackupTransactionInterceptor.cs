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
        CaptureHomeSections(connection, transaction, NpgsqlBackupScope.RequireHomeSectionsSnapshotPath());
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
        await CaptureHomeSectionsAsync(
            connection,
            transaction,
            NpgsqlBackupScope.RequireHomeSectionsSnapshotPath(),
            cancellationToken).ConfigureAwait(false);
        return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
    }

    private static void CaptureHomeSections(
        DbConnection connection,
        DbTransaction transaction,
        string snapshotPath)
    {
        try
        {
            using var output = CreateSnapshotFile(snapshotPath, asynchronous: false);
            using var writer = new Utf8JsonWriter(output);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT \"Id\", \"DisplayPreferencesId\", \"Order\", \"Type\" FROM public.\"HomeSection\" ORDER BY \"Id\"";
            using var reader = command.ExecuteReader();
            writer.WriteStartArray();
            long rows = 0;
            while (reader.Read())
            {
                WriteRow(writer, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                CheckCaptureBounds(writer, output, ++rows);
            }

            writer.WriteEndArray();
            writer.Flush();
            CheckCaptureBounds(writer, output, rows);
        }
        catch
        {
            File.Delete(snapshotPath);
            throw;
        }
    }

    private static async Task CaptureHomeSectionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = CreateSnapshotFile(snapshotPath, asynchronous: true);
            await using (output.ConfigureAwait(false))
            using (var writer = new Utf8JsonWriter(output))
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
                        writer.WriteStartArray();
                        long rows = 0;
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            WriteRow(writer, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                            if (++rows % 1024 == 0)
                            {
                                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }

                            CheckCaptureBounds(writer, output, rows);
                        }

                        writer.WriteEndArray();
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        CheckCaptureBounds(writer, output, rows);
                    }
                }
            }
        }
        catch
        {
            File.Delete(snapshotPath);
            throw;
        }
    }

    private static void WriteRow(
        Utf8JsonWriter writer,
        int id,
        int displayPreferencesId,
        int order,
        int type)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Id", id);
        writer.WriteNumber("DisplayPreferencesId", displayPreferencesId);
        writer.WriteNumber("Order", order);
        writer.WriteNumber("Type", type);
        writer.WriteEndObject();
    }

    private static FileStream CreateSnapshotFile(string path, bool asynchronous)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 65536,
            Options = FileOptions.SequentialScan | (asynchronous ? FileOptions.Asynchronous : FileOptions.None),
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void CheckCaptureBounds(Utf8JsonWriter writer, FileStream output, long rows)
    {
        if (rows > NpgsqlBackupArchive.MaximumDatabaseRows
            || output.Position + writer.BytesPending > NpgsqlBackupArchive.MaximumEntryBytes)
        {
            throw new InvalidDataException("The HomeSection backup exceeds the supported bounded dataset limits.");
        }
    }
}
