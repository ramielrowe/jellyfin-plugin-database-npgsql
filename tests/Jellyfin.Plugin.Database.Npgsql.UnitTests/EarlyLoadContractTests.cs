using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class EarlyLoadContractTests
{
    [Fact]
    public void ReleaseAssemblyExportsExactlyOneConcreteDatabaseProvider()
    {
        var providerTypes = typeof(PluginMetadata).Assembly
            .GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IJellyfinDatabaseProvider).IsAssignableFrom(type))
            .ToArray();

        var providerType = Assert.Single(providerTypes);
        Assert.Equal(typeof(NpgsqlDatabaseProvider), providerType);
    }

    [Fact]
    public void ProviderConstructorUsesOnlyTheEarlyLoggerService()
    {
        var constructor = Assert.Single(typeof(NpgsqlDatabaseProvider).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(ILogger<NpgsqlDatabaseProvider>), parameter.ParameterType);
    }

    [Theory]
    [InlineData(12, 2, 0, 0, true)]
    [InlineData(12, 2, 99, 0, true)]
    [InlineData(12, 1, 0, 0, false)]
    [InlineData(12, 3, 0, 0, false)]
    [InlineData(13, 0, 0, 0, false)]
    public void SupportedHostGuardMatchesTheReleaseAbi(
        int major,
        int minor,
        int build,
        int revision,
        bool expected)
    {
        Assert.Equal(expected, NpgsqlDatabaseProvider.SupportsHostDatabaseAbi(new Version(major, minor, build, revision)));
    }

    [Fact]
    public void PrefixSelectedDirectoryIsRejectedBeforeConfigurationOrDatabaseAccess()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = PluginMetadata.InstallationDirectory + "-ambiguous",
                PluginAssembly = PluginMetadata.AssemblyFileName,
                ConnectionString = "Host=must-not-be-resolved.invalid;Database=must_not_connect"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(new DbContextOptionsBuilder(), configuration));

        Assert.Contains("exact folder and PluginName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
        Assert.Null(provider.DbContextFactory);
    }
}
