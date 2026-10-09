using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Retries whole implicit EF operations while remaining compatible with the explicit
/// transactions used by Jellyfin's core migration routines.
/// </summary>
/// <remarks>
/// EF's stock retry strategy rejects every command issued inside a transaction that
/// the caller started. Jellyfin legitimately owns such transactions. This strategy
/// therefore disables retries for the lifetime of a caller-owned transaction, while
/// retaining whole-operation retries for ordinary <c>SaveChanges</c> calls whose
/// implicit transaction EF can safely replay from the beginning.
/// </remarks>
internal sealed class NpgsqlHostCompatibleRetryingExecutionStrategy : NpgsqlRetryingExecutionStrategy
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlHostCompatibleRetryingExecutionStrategy"/> class.</summary>
    /// <param name="dependencies">The EF execution-strategy dependencies.</param>
    public NpgsqlHostCompatibleRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(
            dependencies,
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(2),
            errorCodesToAdd: ["40P01", "40001"])
    {
        this.context = dependencies.CurrentContext.Context;
    }

    /// <inheritdoc />
    protected override void OnFirstExecution()
    {
        if (this.context.Database.CurrentTransaction is null)
        {
            base.OnFirstExecution();
        }
    }

    /// <inheritdoc />
    protected override bool ShouldRetryOn(Exception? exception)
        => this.context.Database.CurrentTransaction is null && base.ShouldRetryOn(exception);
}
