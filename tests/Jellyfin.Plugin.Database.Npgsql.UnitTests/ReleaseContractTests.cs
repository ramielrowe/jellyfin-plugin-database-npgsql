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
            "tests/scripts/test_audit_vulnerabilities.py",
        };

        Assert.All(required, relative => Assert.True(File.Exists(Path.Combine(root, relative)), relative));
    }

    [Fact]
    public void ReleasePolicyPinsSymbolChecksumAndVulnerabilityAuditScope()
    {
        var root = FindRepositoryRoot();
        using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build", "release-policy.json")));
        var document = policy.RootElement;

        Assert.Equal("Jellyfin.Plugin.Database.Npgsql.pdb", document.GetProperty("symbolFile").GetString());
        Assert.Equal("Jellyfin.Plugin.Database.Npgsql.pdb.sha256", document.GetProperty("symbolChecksumFile").GetString());
        Assert.Equal(
            ["https://api.nuget.org/v3/index.json"],
            document.GetProperty("vulnerabilitySources").EnumerateArray().Select(item => item.GetString()).ToArray());

        var projects = document
            .GetProperty("vulnerabilityAuditProjects")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(8, projects.Length);
        Assert.Equal(projects.Length, projects.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj", projects);
        Assert.Contains("tools/Jellyfin.Plugin.Database.Npgsql.Packaging/Jellyfin.Plugin.Database.Npgsql.Packaging.csproj", projects);
    }

    [Fact]
    public void ReleaseVerifierIsolatesSourceAndRelocatedBuildServersAndChecksGeneratedMetadata()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-release.sh"));

        Assert.Contains("export DOTNET_CLI_USE_MSBUILD_SERVER=0", script, StringComparison.Ordinal);
        Assert.Contains("export MSBUILDDISABLENODEREUSE=1", script, StringComparison.Ordinal);
        Assert.Contains("export UseSharedCompilation=false", script, StringComparison.Ordinal);
        Assert.Contains("dotnet restore \"$@\" --disable-build-servers", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build \"$@\" --disable-build-servers", script, StringComparison.Ordinal);
        Assert.Contains("-nodeReuse:false -property:UseSharedCompilation=false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\ndotnet restore ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\ndotnet build ", script, StringComparison.Ordinal);

        const string MetadataAssertion = "assert_relocated_obj_metadata \"$ORIGINAL_CHECKOUT\" \"$RELOCATED\"";
        Assert.Equal(2, script.Split(MetadataAssertion, StringSplitOptions.None).Length - 1);
        Assert.Contains("--glob 'project.assets.json'", script, StringComparison.Ordinal);
        Assert.Contains("--glob '*.nuget.dgspec.json'", script, StringComparison.Ordinal);

        var sourcePhase = script.IndexOf("restore_without_build_servers \"$SOLUTION\"", StringComparison.Ordinal);
        var relocation = script.IndexOf("relocate-isolated", StringComparison.Ordinal);
        var relocatedPhase = script.IndexOf(
            "restore_without_build_servers \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\"",
            StringComparison.Ordinal);
        Assert.True(sourcePhase >= 0 && sourcePhase < relocation);
        Assert.True(relocation < relocatedPhase);
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
