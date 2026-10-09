using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Delegates every ordinary context to Jellyfin's pooled factory and attaches the one stock
/// restore context to a transaction whose lifetime is owned by the outer backup decorator.
/// </summary>
#pragma warning disable SA1600 // Interface implementation members inherit their contract documentation.
#pragma warning disable CA2025 // Synchronous factory completion is required by IDbContextFactory and awaited immediately.
internal sealed class NpgsqlRestoreDbContextFactory(
    IDbContextFactory<JellyfinDbContext> inner,
    ILogger<JellyfinDbContext> logger,
    IJellyfinDatabaseProvider provider,
    IEntityFrameworkCoreLockingBehavior lockingBehavior)
    : IDbContextFactory<JellyfinDbContext>
{
    public JellyfinDbContext CreateDbContext()
    {
        if (!NpgsqlRestoreScope.IsActive)
        {
            return inner.CreateDbContext();
        }

        var context = this.CreateUnpooledContext();
        try
        {
            NpgsqlRestoreCoordinator.BeginAsync(context, CancellationToken.None).GetAwaiter().GetResult();
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public async Task<JellyfinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (!NpgsqlRestoreScope.IsActive)
        {
            return await inner.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var context = this.CreateUnpooledContext();
        try
        {
            await NpgsqlRestoreCoordinator.BeginAsync(context, cancellationToken).ConfigureAwait(false);
            return context;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<JellyfinDbContext> CreateRestoreDbContextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = this.CreateUnpooledContext();
        try
        {
            await NpgsqlRestoreCoordinator.BeginAsync(context, cancellationToken).ConfigureAwait(false);
            return context;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private JellyfinDbContext CreateUnpooledContext()
    {
        using var template = inner.CreateDbContext();
        var options = (DbContextOptions<JellyfinDbContext>)template.GetService<IDbContextOptions>();
        return new JellyfinDbContext(
            options,
            logger,
            provider,
            lockingBehavior);
    }
}
#pragma warning restore CA2025
#pragma warning restore SA1600
