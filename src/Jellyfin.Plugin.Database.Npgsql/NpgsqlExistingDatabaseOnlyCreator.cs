using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Prevents Jellyfin's first-start path from creating or deleting a PostgreSQL database.
/// Operators must provision the selected database and restricted login explicitly.
/// </summary>
internal sealed class NpgsqlExistingDatabaseOnlyCreator : RelationalDatabaseCreator
{
    private const string HasTablesSql = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND c.relkind IN ('r', 'p'))
        """;

    private readonly IRelationalConnection connection;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlExistingDatabaseOnlyCreator" /> class.</summary>
    /// <param name="dependencies">The relational database-creator dependencies.</param>
    /// <param name="connection">The context's PostgreSQL connection.</param>
    public NpgsqlExistingDatabaseOnlyCreator(
        RelationalDatabaseCreatorDependencies dependencies,
        IRelationalConnection connection)
        : base(dependencies)
    {
        this.connection = connection;
    }

    /// <inheritdoc />
    public override bool Exists()
    {
        try
        {
            this.connection.Open(errorsExpected: true);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw MissingDatabase(exception);
        }
        finally
        {
            this.connection.Close();
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await this.connection.OpenAsync(cancellationToken, errorsExpected: true).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw MissingDatabase(exception);
        }
        finally
        {
            await this.connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override void Create() => throw PhysicalDatabaseMutationNotSupported();

    /// <inheritdoc />
    public override Task CreateAsync(CancellationToken cancellationToken = default)
        => Task.FromException(PhysicalDatabaseMutationNotSupported());

    /// <inheritdoc />
    public override void Delete() => throw PhysicalDatabaseMutationNotSupported();

    /// <inheritdoc />
    public override Task DeleteAsync(CancellationToken cancellationToken = default)
        => Task.FromException(PhysicalDatabaseMutationNotSupported());

    /// <inheritdoc />
    public override bool HasTables()
    {
        this.connection.Open();
        try
        {
            using var command = this.connection.DbConnection.CreateCommand();
            command.CommandText = HasTablesSql;
            return command.ExecuteScalar() is true;
        }
        finally
        {
            this.connection.Close();
        }
    }

    /// <inheritdoc />
    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        await this.connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = this.connection.DbConnection.CreateCommand();
            command.CommandText = HasTablesSql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        finally
        {
            await this.connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static InvalidOperationException MissingDatabase(PostgresException exception)
        => new(
            "The configured PostgreSQL database does not exist. Provision the database and restricted login before starting Jellyfin; "
            + "the Npgsql plugin will not create a physical database and no database mutation was attempted.",
            exception);

    private static NotSupportedException PhysicalDatabaseMutationNotSupported()
        => new(
            "The Npgsql plugin does not create or delete physical PostgreSQL databases. "
            + "Provision the selected database explicitly before starting Jellyfin.");
}
