using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class PluginMetadataTests
{
    [Fact]
    public void MetadataUsesPermanentPackageIdentity()
    {
        Assert.Equal("Npgsql Database", PluginMetadata.Name);
        Assert.Equal("8a6e4c5e-2185-45df-a30f-cc828529330b", PluginMetadata.Id.ToString());
    }
}
