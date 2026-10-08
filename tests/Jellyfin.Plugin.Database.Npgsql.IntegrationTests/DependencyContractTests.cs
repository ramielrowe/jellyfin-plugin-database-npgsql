using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class DependencyContractTests
{
    [Fact]
    public void PinnedDependenciesMatchTheProvenCompatibilityLine()
    {
        var jellyfinVersion = typeof(IJellyfinDatabaseProvider).Assembly.GetName().Version;

        Assert.NotNull(jellyfinVersion);
        Assert.Equal(12, jellyfinVersion.Major);
        Assert.Equal(2, jellyfinVersion.Minor);
        Assert.Equal(10, typeof(DbContext).Assembly.GetName().Version?.Major);
        Assert.Equal(10, typeof(NpgsqlConnection).Assembly.GetName().Version?.Major);
        Assert.Equal(10, typeof(NpgsqlDbContextOptionsBuilder).Assembly.GetName().Version?.Major);
    }
}
