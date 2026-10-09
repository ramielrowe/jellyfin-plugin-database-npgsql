using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Database.Npgsql.Migrations.Design;

/// <summary>Creates the production Jellyfin context for EF migration commands.</summary>
public sealed class NpgsqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=127.0.0.1;Port=1;Database=jellyfin_design;Username=jellyfin_design;Password=design-only;Pooling=false;Timeout=1;Command Timeout=1";

    /// <inheritdoc />
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        options.UseNpgsql(
            ReadConnectionString(args),
            npgsql => npgsql.MigrationsAssembly(typeof(NpgsqlDatabaseProvider).Assembly.GetName().Name));
        options
            .ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>()
            .AddInterceptors(NpgsqlQueryExpressionInterceptor.Instance);

        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static string ReadConnectionString(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--connection", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("--connection requires a non-empty value.", nameof(args));
            }

            return args[index + 1];
        }

        return PlaceholderConnectionString;
    }
}
