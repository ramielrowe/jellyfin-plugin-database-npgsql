using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Isolates the first executable reference to the plugin-shipped Npgsql EF assembly.
/// </summary>
internal static class NpgsqlOptionsBootstrap
{
    /// <summary>
    /// Configures EF only after the provider has validated and loaded both adjacent dependencies.
    /// </summary>
    /// <param name="options">The host database options builder.</param>
    /// <param name="connectionString">The configured PostgreSQL connection string.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(Assembly.GetExecutingAssembly().GetName().Name));
        options.ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>();
    }
}
