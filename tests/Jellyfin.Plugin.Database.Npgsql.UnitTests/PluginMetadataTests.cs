using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class PluginMetadataTests
{
    [Fact]
    public void MetadataUsesPermanentPackageIdentity()
    {
        Assert.Equal("Npgsql Database", PluginMetadata.Name);
        Assert.Equal("0.1.2.0", PluginMetadata.Version);
        Assert.Equal("Npgsql Database_0.1.2.0", PluginMetadata.InstallationDirectory);
        Assert.Equal("Jellyfin.Plugin.Database.Npgsql.dll", PluginMetadata.AssemblyFileName);
        Assert.Equal("12.2.0.0", PluginMetadata.SupportedDatabaseAbi);
        Assert.Equal("8a6e4c5e-2185-45df-a30f-cc828529330b", PluginMetadata.Id.ToString());
    }
}
