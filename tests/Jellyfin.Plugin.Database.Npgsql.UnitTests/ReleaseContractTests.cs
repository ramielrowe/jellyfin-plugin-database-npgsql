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
        Assert.Contains("command -v grep", script, StringComparison.Ordinal);
        Assert.Contains("--include='project.assets.json'", script, StringComparison.Ordinal);
        Assert.Contains("--include='*.nuget.dgspec.json'", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("[ \"$grep_status\" -le 1 ]", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("rg --hidden", script, StringComparison.Ordinal);

        var sourcePhase = script.IndexOf("restore_without_build_servers \"$SOLUTION\"", StringComparison.Ordinal);
        var relocation = script.IndexOf("relocate-isolated", StringComparison.Ordinal);
        var relocatedRestore = script.IndexOf(
            "restore_without_build_servers \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\"",
            StringComparison.Ordinal);
        var firstMetadataAssertion = script.IndexOf(MetadataAssertion, StringComparison.Ordinal);
        var relocatedBuild = script.IndexOf(
            "build_without_build_servers \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\"",
            StringComparison.Ordinal);
        var stagedHostPublish = script.IndexOf(
            "stage-host-publish \\",
            StringComparison.Ordinal);
        var blackBox = script.IndexOf("\"$RELOCATED/tests/black-box-smoke.sh\"", StringComparison.Ordinal);
        var finalMetadataAssertion = script.LastIndexOf(MetadataAssertion, StringComparison.Ordinal);
        var broadPathScan = script.IndexOf("path_leaks=$(grep", StringComparison.Ordinal);
        var success = script.IndexOf("echo \"Release verification passed:", StringComparison.Ordinal);
        var cleanupTrap = script.IndexOf("trap cleanup EXIT", StringComparison.Ordinal);

        string[] relocatedArtifactOperations =
        [
            "build_without_build_servers \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\"",
            "msbuild_without_build_servers \"$RELOCATED/build/Package.proj\"",
            "dotnet test --project \"$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.UnitTests",
            "dotnet test --project \"$RELOCATED/tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests",
            "python3 -m unittest discover -s \"$RELOCATED/tests/scripts\"",
            "--project \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\"",
            "python3 \"$RELOCATED/scripts/audit-vulnerabilities.py\"",
            "STANDALONE_RELOCATED_COPY=true",
            "relocated_hash=$(hash_file",
            "relocated_symbol_hash=$(hash_file",
            "stage-host-publish \\",
            "\"$RELOCATED/tests/black-box-smoke.sh\"",
        ];

        Assert.True(sourcePhase >= 0 && sourcePhase < relocation);
        Assert.True(relocation < relocatedRestore);
        Assert.Contains(
            "restore_without_build_servers \"$RELOCATED/Jellyfin.Plugin.Database.Npgsql.slnx\" --locked-mode\n" + MetadataAssertion,
            script,
            StringComparison.Ordinal);
        Assert.True(relocatedRestore < firstMetadataAssertion);
        Assert.True(firstMetadataAssertion < relocatedBuild);
        Assert.True(relocatedBuild < stagedHostPublish);
        Assert.True(stagedHostPublish < blackBox);
        Assert.True(blackBox < finalMetadataAssertion);
        Assert.True(finalMetadataAssertion < broadPathScan);
        Assert.True(broadPathScan < success);
        Assert.True(HasFinalScansAfterBlackBox(script));
        Assert.True(cleanupTrap >= 0 && cleanupTrap < relocatedRestore);
        Assert.DoesNotContain("cp --archive", script, StringComparison.Ordinal);
        Assert.Contains("${JELLYFIN_HOST_SHA256:-}", script, StringComparison.Ordinal);
        Assert.Contains("${UNSUPPORTED_JELLYFIN_HOST_SHA256:-}", script, StringComparison.Ordinal);
        Assert.Contains("validate_black_box_inputs", script, StringComparison.Ordinal);
        Assert.Contains("release verification rejects JELLYFIN_HOST_REPOSITORY", script, StringComparison.Ordinal);
        Assert.Contains("release verification rejects JELLYFIN_UNSUPPORTED_HOST_REPOSITORY", script, StringComparison.Ordinal);
        Assert.Contains("release verification rejects UNSUPPORTED_JELLYFIN_HOST_REPOSITORY", script, StringComparison.Ordinal);
        Assert.Contains("RUN_BLACK_BOX=true requires an exact prebuilt JELLYFIN_HOST_PUBLISH", script, StringComparison.Ordinal);
        Assert.Contains("RUN_BLACK_BOX=true requires a 64-hex JELLYFIN_HOST_SHA256", script, StringComparison.Ordinal);
        Assert.Contains(
            "export FORBIDDEN_PARENT_CHECKOUT=\"$ORIGINAL_CHECKOUT\"\n    \"$RELOCATED/tests/black-box-smoke.sh\"",
            script,
            StringComparison.Ordinal);
        var blackBoxScript = File.ReadAllText(Path.Combine(root, "tests", "black-box-smoke.sh"));
        Assert.Equal(
            2,
            blackBoxScript.Split("${FORBIDDEN_PARENT_CHECKOUT:-}", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("if [ -n \"${JELLYFIN_HOST_PUBLISH:-}\" ]", script, StringComparison.Ordinal);
        Assert.All(
            relocatedArtifactOperations,
            operation =>
            {
                var operationIndex = script.LastIndexOf(operation, StringComparison.Ordinal);
                Assert.True(operationIndex > firstMetadataAssertion, $"Missing or misplaced relocated operation: {operation}");
                Assert.True(operationIndex < finalMetadataAssertion, $"Relocated operation runs after final metadata scan: {operation}");
            });
        Assert.DoesNotContain(
            "\"$RELOCATED/tests/black-box-smoke.sh\"",
            script[(finalMetadataAssertion + MetadataAssertion.Length)..success],
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseVerifierRejectsThePriorPrematureFinalScanOrdering()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-release.sh"));
        const string BlackBoxCommand = "    \"$RELOCATED/tests/black-box-smoke.sh\"\n";
        const string Success = "echo \"Release verification passed:";

        Assert.Contains(BlackBoxCommand, script, StringComparison.Ordinal);
        var priorOrdering = script.Replace(BlackBoxCommand, string.Empty, StringComparison.Ordinal);
        priorOrdering = priorOrdering.Replace(Success, BlackBoxCommand + Success, StringComparison.Ordinal);

        Assert.False(HasFinalScansAfterBlackBox(priorOrdering));
    }

    [Theory]
    [InlineData(".github/workflows/linuxserver-image.yml")]
    [InlineData(".github/workflows/kubernetes-seed-image.yml")]
    public void ContainerPublishWorkflowKeepsRegistryCredentialsAvailableForAttestation(string relativePath)
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, relativePath));
        var publishStart = workflow.IndexOf("  publish:\n", StringComparison.Ordinal);

        Assert.True(publishStart >= 0);
        var publish = workflow[publishStart..];
        var setupBuildx = publish.IndexOf("docker/setup-buildx-action@", StringComparison.Ordinal);
        var login = publish.IndexOf("docker/login-action@", StringComparison.Ordinal);
        var push = publish.IndexOf("docker/build-push-action@", StringComparison.Ordinal);
        var attest = publish.IndexOf("actions/attest-build-provenance@", StringComparison.Ordinal);

        Assert.True(setupBuildx >= 0 && setupBuildx < login);
        Assert.True(login < push);
        Assert.True(push < attest);
        Assert.DoesNotContain("$GITHUB_ENV", publish, StringComparison.Ordinal);
        Assert.Contains("index:org.opencontainers.image.source=", publish, StringComparison.Ordinal);
        Assert.Contains("index:org.opencontainers.image.description=", publish, StringComparison.Ordinal);
        Assert.Contains("index:org.opencontainers.image.licenses=GPL-3.0-only", publish, StringComparison.Ordinal);
        Assert.Contains(
            "DOCKER_CONFIG=\"$anonymous_config\" docker buildx imagetools inspect",
            publish,
            StringComparison.Ordinal);
        Assert.Contains("EXPECTED_DIGEST: ${{ steps.push.outputs.digest }}", publish, StringComparison.Ordinal);
        Assert.Contains("--format '{{json .Manifest}}'", publish, StringComparison.Ordinal);
        Assert.Contains("[[ \"$actual_digest\" != \"$EXPECTED_DIGEST\" ]]", publish, StringComparison.Ordinal);
        Assert.Contains("verify_anonymous_ref \"$IMAGE_NAME:$version\"", publish, StringComparison.Ordinal);
        Assert.Contains("verify_anonymous_ref \"$IMAGE_NAME:sha-$short_sha\"", publish, StringComparison.Ordinal);
        Assert.Contains("verify_anonymous_ref \"$IMAGE_NAME:latest\"", publish, StringComparison.Ordinal);
        Assert.Contains("verify_anonymous_ref \"$IMAGE_NAME@$EXPECTED_DIGEST\"", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationRunsOnTheDefaultBranch()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("branches: [main, master]", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationUsesWorkspaceAbsoluteVanillaHostPath()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("JELLYFIN_HOST_PUBLISH: ${{ github.workspace }}/host", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("JELLYFIN_HOST_PUBLISH: ../host", workflow, StringComparison.Ordinal);
    }

    private static bool HasFinalScansAfterBlackBox(string script)
    {
        const string MetadataAssertion = "assert_relocated_obj_metadata \"$ORIGINAL_CHECKOUT\" \"$RELOCATED\"";
        var blackBox = script.IndexOf("\"$RELOCATED/tests/black-box-smoke.sh\"", StringComparison.Ordinal);
        var finalMetadataAssertion = script.LastIndexOf(MetadataAssertion, StringComparison.Ordinal);
        var broadPathScan = script.IndexOf("path_leaks=$(grep", StringComparison.Ordinal);
        var success = script.IndexOf("echo \"Release verification passed:", StringComparison.Ordinal);

        return blackBox >= 0 &&
            blackBox < finalMetadataAssertion &&
            finalMetadataAssertion < broadPathScan &&
            broadPathScan < success;
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
