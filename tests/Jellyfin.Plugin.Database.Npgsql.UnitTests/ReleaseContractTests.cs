using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void ProviderAssemblyAndMetadataUseOneExactReleaseIdentity()
    {
        var assembly = typeof(PluginMetadata).Assembly.GetName();
        Assert.Equal(new Version(PluginMetadata.Version), assembly.Version);
        Assert.Equal("Jellyfin.Plugin.Database.Npgsql", assembly.Name);

        var root = FindRepositoryRoot();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json")));
        var document = metadata.RootElement;
        Assert.Equal(PluginMetadata.Id.ToString(), document.GetProperty("guid").GetString());
        Assert.Equal(PluginMetadata.Name, document.GetProperty("name").GetString());
        Assert.Equal(PluginMetadata.Version, document.GetProperty("version").GetString());
        Assert.Equal(PluginMetadata.SupportedDatabaseAbi, document.GetProperty("targetAbi").GetString());
        Assert.Equal("Active", document.GetProperty("status").GetString());
        Assert.False(document.GetProperty("autoUpdate").GetBoolean());
        Assert.Equal(
            [PluginMetadata.AssemblyFileName],
            document.GetProperty("assemblies").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public void ExactVersionQualifiedSelectionCannotMatchAnOlderRelease()
    {
        var installedDirectories = new[]
        {
            "Npgsql Database_0.0.8.0",
            "Npgsql Database_0.0.9.0",
            PluginMetadata.InstallationDirectory,
            PluginMetadata.InstallationDirectory + ".partial",
        };

        var selected = installedDirectories
            .Where(directory => directory.StartsWith(PluginMetadata.InstallationDirectory, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .First();

        Assert.Equal(PluginMetadata.InstallationDirectory, selected);
        Assert.DoesNotContain(
            installedDirectories.Where(directory => directory != PluginMetadata.InstallationDirectory),
            directory => PluginMetadata.InstallationDirectory.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PackageAndDependencyPolicyFilesArePresentInStandaloneTree()
    {
        var root = FindRepositoryRoot();
        var required = new[]
        {
            ".github/workflows/ci.yml",
            "build/Package.proj",
            "build.yaml",
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "scripts/audit-build.sh",
            "scripts/audit-dependencies.py",
            "scripts/audit-vulnerabilities.py",
            "scripts/verify-release.sh",
        };

        Assert.All(required, relative => Assert.True(File.Exists(Path.Combine(root, relative)), relative));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Jellyfin.Plugin.Database.Npgsql.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find the standalone plugin repository root.");
    }
}
