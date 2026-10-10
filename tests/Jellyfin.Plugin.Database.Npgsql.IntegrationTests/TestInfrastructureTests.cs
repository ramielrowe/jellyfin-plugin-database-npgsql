using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class TestInfrastructureTests
{
    [Fact]
    public void HostArtifactScope_ForcedHostProofFailureRetainsBoundedDiagnosticsAndDeletesCopiedHost()
    {
        var tempRoot = CreateTemporaryDirectory();
        const string Secret = "forced-host-proof-secret";
        try
        {
            var artifacts = HostTestArtifactScope.Create("forced-host-proof", Path.Combine(tempRoot, "artifact-root"));
            var caseRoot = artifacts.CaseDirectory;
            string retainedEvidence;
            try
            {
                var copiedHost = Path.Combine(caseRoot, "host");
                Directory.CreateDirectory(copiedHost);
                using (var largeHostFile = File.Create(Path.Combine(copiedHost, "jellyfin.dll")))
                {
                    largeHostFile.SetLength(32L * 1024 * 1024);
                }

                var collectedLogs = Path.Combine(caseRoot, "runs", "collected-logs");
                Directory.CreateDirectory(collectedLogs);
                File.WriteAllText(
                    Path.Combine(collectedLogs, "jellyfin.log"),
                    Secret + new string('x', HostTestArtifactScope.MaximumRetainedEvidenceBytes + 1024));

                try
                {
                    throw new InvalidOperationException("Intentionally forced host proof failure containing " + Secret);
                }
                catch (Exception exception)
                {
                    artifacts.RetainFailureEvidence(exception, new SensitiveDataRedactor(Secret));
                }

                retainedEvidence = Assert.IsType<string>(artifacts.RetainedEvidenceDirectory);
                Assert.InRange(artifacts.RetainedEvidenceBytes, 1, HostTestArtifactScope.MaximumRetainedEvidenceBytes);
            }
            finally
            {
                artifacts.Dispose();
            }

            Assert.False(Directory.Exists(caseRoot));
            Assert.True(Directory.Exists(retainedEvidence));
            Assert.False(Directory.Exists(Path.Combine(retainedEvidence, "host")));
            var evidenceFiles = Directory.EnumerateFiles(retainedEvidence, "*", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(evidenceFiles);
            Assert.All(
                evidenceFiles,
                path => Assert.DoesNotContain(Secret, File.ReadAllText(path), StringComparison.Ordinal));
            Assert.InRange(
                evidenceFiles.Sum(path => new FileInfo(path).Length),
                1,
                HostTestArtifactScope.MaximumRetainedEvidenceBytes + 256);

            var successfulCase = HostTestArtifactScope.Create("successful-host-proof", Path.Combine(tempRoot, "artifact-root"));
            var successfulCaseRoot = successfulCase.CaseDirectory;
            Directory.CreateDirectory(Path.Combine(successfulCaseRoot, "host"));
            successfulCase.Dispose();
            Assert.False(Directory.Exists(successfulCaseRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostArtifactScope_RefusesCleanupWhenOwnershipMarkerChanges()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var artifacts = HostTestArtifactScope.Create("marker-proof", Path.Combine(tempRoot, "artifact-root"));
            var caseRoot = artifacts.CaseDirectory;
            var marker = Assert.Single(Directory.EnumerateFiles(caseRoot, ".*", SearchOption.TopDirectoryOnly));
            var expectedMarker = File.ReadAllText(marker);
            File.WriteAllText(marker, "changed ownership");

            var exception = Assert.Throws<InvalidOperationException>(artifacts.Dispose);
            Assert.Contains("ownership marker", exception.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(caseRoot));

            File.WriteAllText(marker, expectedMarker);
            artifacts.Dispose();
            Assert.False(Directory.Exists(caseRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResourceNames_AreUniqueWorkerScopedAndDestructivelyValidated()
    {
        var first = TestResourceNames.Create("worker 1");
        var second = TestResourceNames.Create("worker 2");

        Assert.NotEqual(first.DatabaseName, second.DatabaseName);
        Assert.Contains("worker_1", first.DatabaseName, StringComparison.Ordinal);
        Assert.True(TestResourceNames.IsOwnedDatabase(first.DatabaseName));
        Assert.True(TestResourceNames.IsOwnedRole(first.RoleName));
        Assert.True(TestResourceNames.IsOwnedContainer(first.ContainerName));

        Assert.False(TestResourceNames.IsOwnedDatabase("jellyfin"));
        Assert.False(TestResourceNames.IsOwnedDatabase("jellyfin_test_db_production"));
        Assert.False(TestResourceNames.IsOwnedRole("postgres"));
        Assert.Throws<InvalidOperationException>(() => TestResourceNames.EnsureOwnedDatabase("postgres"));
        Assert.Throws<InvalidOperationException>(() => TestResourceNames.EnsureOwnedRole("jellyfin"));
        Assert.Throws<InvalidOperationException>(() => TestResourceNames.EnsureOwnedContainer("postgres"));
    }

    [Fact]
    public void ConnectionDiagnostics_AreSanitizedAndInvalidValuesAreNeverEchoed()
    {
        const string Secret = "never-print-this";
        const string Passfile = "/private/credentials.pgpass";
        var redacted = SensitiveDataRedactor.RedactConnectionString(
            $"Host=localhost;Database=jellyfin_npgsql_test_admin;Username=test;Password={Secret};Passfile={Passfile}");

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Passfile, redacted, StringComparison.Ordinal);
        Assert.Contains("Host=localhost", redacted, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PostgreSqlTestDatabase.ValidateAdministratorConnectionString(
                $"Host=localhost;Password={Secret};Unknown Keyword=value"));
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres:16.6")]
    [InlineData("postgres@sha256:abc")]
    [InlineData("@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void ContainerImageValidation_RejectsMutableOrMalformedReferences(string image)
        => Assert.False(PostgreSqlTestOptions.IsDigestPinnedImage(image));

    [Fact]
    public void ContainerImageValidation_AcceptsDefaultPinnedReference()
    {
        Assert.Equal(
            "postgres:16.6@sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36",
            PostgreSqlTestOptions.DefaultContainerImage);
        Assert.True(PostgreSqlTestOptions.IsDigestPinnedImage(PostgreSqlTestOptions.DefaultContainerImage));
    }

    [Fact]
    public async Task MissingPostgreSqlOptIn_FailsBeforeAnyExternalCommandWithActionableMessage()
    {
        var options = new PostgreSqlTestOptions();

        Assert.False(options.IsConfigured);
        Assert.Contains(PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable, options.MissingInfrastructureMessage, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlTestOptions.UseContainerEnvironmentVariable, options.MissingInfrastructureMessage, StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlTestEnvironment.CreateAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal(options.MissingInfrastructureMessage, exception.Message);
    }

    [Fact]
    public async Task ExternalPostgreSqlWithoutExclusiveContract_FailsBeforeConnectionOrMutation()
    {
        var options = new PostgreSqlTestOptions
        {
            AdministratorConnectionString = "Host=203.0.113.1;Database=jellyfin_npgsql_test_admin;Username=test;Password=not-used",
            ExternalExclusive = false
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlTestEnvironment.CreateAsync(options, TestContext.Current.CancellationToken));
        Assert.Contains(PostgreSqlTestOptions.ExternalExclusiveEnvironmentVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("No connection or mutation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PristinePublishAndPluginInstall_AreRelocatableAndIntegrityChecked()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "unrelated", "downloaded-publish");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "test-host", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "dependency.dll"), "dependency", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var destination = Path.Combine(tempRoot, "moved-plugin-work", "host-copy");
            var publish = await PristineJellyfinPublish.CreateAsync(
                new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                destination,
                TestContext.Current.CancellationToken);
            await publish.VerifyUnchangedAsync(TestContext.Current.CancellationToken);

            var package = Path.Combine(tempRoot, "plugin.zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("plugin.dll");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("plugin");
            }

            var layout = JellyfinHostLayout.Create(Path.Combine(tempRoot, "artifacts"), publish, "worker-a");
            var pluginDirectory = layout.InstallPluginZip(package, "Npgsql Database_0.1.0.0");
            layout.WriteDatabaseConfiguration(
                "Host=localhost;Database=test;Username=test;Password=secret",
                "Npgsql Database_0.1.0.0",
                "plugin.dll");
            layout.WriteNetworkConfiguration(18096, 18920);
            const string LogSecret = "must-not-survive-log-collection";
            await File.WriteAllTextAsync(
                Path.Combine(layout.LogDirectory, "jellyfin.log"),
                "safe before " + LogSecret + " safe after",
                TestContext.Current.CancellationToken);
            layout.CollectSanitizedLogs(new SensitiveDataRedactor(LogSecret));

            Assert.True(File.Exists(Path.Combine(pluginDirectory, "plugin.dll")));
            Assert.True(File.Exists(Path.Combine(layout.ConfigurationDirectory, "database.xml")));
            var collectedLog = await File.ReadAllTextAsync(
                Assert.Single(Directory.EnumerateFiles(layout.CollectedLogDirectory)),
                TestContext.Current.CancellationToken);
            Assert.Contains("<redacted>", collectedLog, StringComparison.Ordinal);
            Assert.DoesNotContain(LogSecret, collectedLog, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(destination), publish.DirectoryPath);
            Assert.NotEqual(Path.GetFullPath(source), publish.DirectoryPath);
            await publish.VerifyUnchangedAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PristinePublish_RejectsMutationAndUnsafeZipPaths()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "original", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var publish = await PristineJellyfinPublish.CreateAsync(
                new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                Path.Combine(tempRoot, "copy"),
                TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(publish.EntryAssemblyPath, "changed", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => publish.VerifyUnchangedAsync(TestContext.Current.CancellationToken));

            var unsafeZip = Path.Combine(tempRoot, "unsafe.zip");
            using (var archive = ZipFile.Open(unsafeZip, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("../escape.dll");
            }

            Assert.Throws<InvalidOperationException>(
                () => PristineJellyfinPublish.ExtractZipSafely(unsafeZip, Path.Combine(tempRoot, "zip-output")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PristinePublish_RejectsSourceMutationBetweenVerificationAndCopy()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            var assembly = Path.Combine(source, "jellyfin.dll");
            await File.WriteAllTextAsync(assembly, "original", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PristineJellyfinPublish.CreateAsync(
                    new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                    Path.Combine(tempRoot, "copy"),
                    TestContext.Current.CancellationToken,
                    () => File.WriteAllText(assembly, "raced mutation")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SafeHostPublishStager_RejectsRootAndNestedLinksAndSpecialNodes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            var hash = await SafeHostPublishStager.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var ownedRoot = Path.Combine(tempRoot, "owned");
            await CreateOwnedArtifactRootAsync(ownedRoot);
            var destination = Path.Combine(ownedRoot, "host-publish-input");

            var rootLink = Path.Combine(tempRoot, "root-link");
            Directory.CreateSymbolicLink(rootLink, source);
            var rootException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    rootLink,
                    destination,
                    hash,
                    TestContext.Current.CancellationToken));
            Assert.Contains("symbolic-link components", rootException.Message, StringComparison.OrdinalIgnoreCase);

            var nestedLink = Path.Combine(source, "nested-link");
            File.CreateSymbolicLink(nestedLink, Path.Combine(source, "jellyfin.dll"));
            var nestedException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    destination,
                    hash,
                    TestContext.Current.CancellationToken));
            Assert.Contains("symbolic link", nestedException.Message, StringComparison.OrdinalIgnoreCase);
            File.Delete(nestedLink);

            var fifo = Path.Combine(source, "unexpected-pipe");
            var mkfifo = await ExternalCommand.RunAsync(
                "/usr/bin/mkfifo",
                [fifo],
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, mkfifo.ExitCode);
            var fifoException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    destination,
                    hash,
                    TestContext.Current.CancellationToken));
            Assert.Contains("special filesystem node", fifoException.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SafeHostPublishStager_RejectsOverlapBadHashAndPostValidationMutationBeforeCopying()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            var assembly = Path.Combine(source, "jellyfin.dll");
            await File.WriteAllTextAsync(assembly, "host", TestContext.Current.CancellationToken);
            var sentinel = Path.Combine(source, "large-sentinel.bin");
            await using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var block = new byte[64 * 1024];
                for (var index = 0; index < 512; index++)
                {
                    await stream.WriteAsync(block, TestContext.Current.CancellationToken);
                }
            }

            var hash = await SafeHostPublishStager.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var overlapDestination = Path.Combine(source, "host-publish-input");
            var overlapException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    overlapDestination,
                    hash,
                    TestContext.Current.CancellationToken));
            Assert.Contains("must not overlap", overlapException.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(overlapDestination));

            var ownedRoot = Path.Combine(tempRoot, "owned");
            await CreateOwnedArtifactRootAsync(ownedRoot);
            var badHashDestination = Path.Combine(ownedRoot, "host-publish-input");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    badHashDestination,
                    new string('0', 64),
                    TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(badHashDestination));

            var racedDestination = badHashDestination;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    racedDestination,
                    hash,
                    TestContext.Current.CancellationToken,
                    () => File.WriteAllText(assembly, "evil")));
            Assert.False(Directory.Exists(racedDestination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SafeHostPublishStager_ContainsConcurrentIntermediateSourceAndDestinationLinkSwaps()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            var nested = Path.Combine(source, "nested");
            var outside = Path.Combine(tempRoot, "outside");
            var ownedRoot = Path.Combine(tempRoot, "owned");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(outside);
            await CreateOwnedArtifactRootAsync(ownedRoot);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(nested, "dependency.dll"), "dependency", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outside, "dependency.dll"), "outside", TestContext.Current.CancellationToken);
            var hash = await SafeHostPublishStager.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var destination = Path.Combine(ownedRoot, "host-publish-input");

            var sourceRace = await Assert.ThrowsAnyAsync<Exception>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    destination,
                    hash,
                    TestContext.Current.CancellationToken,
                    () =>
                    {
                        Directory.Move(nested, nested + "-original");
                        Directory.CreateSymbolicLink(nested, outside);
                    }));
            Assert.Contains("open", sourceRace.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "dependency.dll"), TestContext.Current.CancellationToken));

            Directory.Delete(nested);
            Directory.Move(nested + "-original", nested);
            var outsideEscapedFile = Path.Combine(outside, "jellyfin.dll");
            var destinationRace = await Assert.ThrowsAnyAsync<Exception>(
                () => SafeHostPublishStager.StageAsync(
                    source,
                    destination,
                    hash,
                    TestContext.Current.CancellationToken,
                    () => Directory.CreateSymbolicLink(destination, outside)));
            Assert.Contains("destination", destinationRace.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outsideEscapedFile));
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "dependency.dll"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SafeHostPublishStager_RejectsSparseFilesBeforeDestinationCreation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            await using (var sparse = new FileStream(Path.Combine(source, "sparse.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                sparse.SetLength(32L * 1024 * 1024);
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SafeHostPublishStager.HashDirectoryAsync(source, TestContext.Current.CancellationToken));
            Assert.Contains("sparse file", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StageHostPublishCommand_NormalizesRestrictiveModesRejectsEscapeAndMarkerCleanupSucceeds()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var helper = Path.Combine(repository, "tests", "Jellyfin.Plugin.Database.Npgsql.TestHarness", "bin", "Release", "net10.0", "Jellyfin.Plugin.Database.Npgsql.TestHarness.dll");
        var sourceRoot = CreateTemporaryDirectory();
        var ownedRoot = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-release-verify-" + Guid.NewGuid().ToString("N"));
        var escapedDestination = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-stage-escape-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nested = Path.Combine(sourceRoot, "nested");
            Directory.CreateDirectory(nested);
            var assembly = Path.Combine(sourceRoot, "jellyfin.dll");
            var dependency = Path.Combine(nested, "dependency.dll");
            await File.WriteAllTextAsync(assembly, "host", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(dependency, "dependency", TestContext.Current.CancellationToken);
            var hash = await SafeHostPublishStager.HashDirectoryAsync(sourceRoot, TestContext.Current.CancellationToken);
            File.SetUnixFileMode(sourceRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(assembly, UnixFileMode.UserRead);
            File.SetUnixFileMode(dependency, UnixFileMode.UserRead);
            Directory.CreateDirectory(ownedRoot);
            await File.WriteAllTextAsync(
                Path.Combine(ownedRoot, ".jellyfin-npgsql-owned-artifacts"),
                "Owned by Jellyfin.Plugin.Database.Npgsql.TestHarness\n",
                TestContext.Current.CancellationToken);

            var escape = await ExternalCommand.RunAsync(
                "dotnet",
                [helper, "stage-host-publish", sourceRoot, ownedRoot, "../" + Path.GetFileName(escapedDestination), hash],
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(0, escape.ExitCode);
            Assert.False(Directory.Exists(escapedDestination));

            var stage = await ExternalCommand.RunAsync(
                "dotnet",
                [helper, "stage-host-publish", sourceRoot, ownedRoot, "host-publish-input", hash],
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, stage.ExitCode);
            var staged = Path.Combine(ownedRoot, "host-publish-input");
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(staged));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(staged, "jellyfin.dll")));

            var cleanup = await ExternalCommand.RunAsync(
                "dotnet",
                [helper, "delete-isolated-artifacts", ownedRoot],
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, cleanup.ExitCode);
            Assert.False(Directory.Exists(ownedRoot));
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                File.SetUnixFileMode(sourceRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var nested = Path.Combine(sourceRoot, "nested");
                if (Directory.Exists(nested))
                {
                    File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                Directory.Delete(sourceRoot, recursive: true);
            }

            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PristinePublish_RejectsArchiveMutationDuringExtractionWindow()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(tempRoot, "host.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("jellyfin.dll");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("original");
            }

            var hash = await PristineJellyfinPublish.HashFileAsync(archivePath, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(
                () => PristineJellyfinPublish.CreateAsync(
                    new PristineJellyfinPublishOptions { ArchivePath = archivePath, Sha256 = hash },
                    Path.Combine(tempRoot, "copy"),
                    TestContext.Current.CancellationToken,
                    () => File.AppendAllText(archivePath, "mutation")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StructuralConfigurationEscapesInjectionAndCleanupRetainsOnlyRedactedEvidence()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var publish = await PristineJellyfinPublish.CreateAsync(
                new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                Path.Combine(tempRoot, "publish"),
                TestContext.Current.CancellationToken);
            var layout = JellyfinHostLayout.Create(Path.Combine(tempRoot, "artifacts"), publish, "../../hostile");
            const string Secret = "secret<&\"';sed-command";
            var connection = "Host=localhost;Password=" + Secret;
            layout.WriteDatabaseConfiguration(connection, "folder<&", "plugin.dll");
            layout.WriteNetworkConfiguration(1, 65535);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(layout.ConfigurationDirectory));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(layout.ConfigurationDirectory, "database.xml")));
            }
            var document = XDocument.Load(Path.Combine(layout.ConfigurationDirectory, "database.xml"));
            Assert.Equal(connection, document.Root?.Element("CustomProviderOptions")?.Element("ConnectionString")?.Value);
            Assert.Single(document.Descendants("ConnectionString"));
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.WriteNetworkConfiguration(0, 80));
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.WriteNetworkConfiguration(80, 80));
            await File.WriteAllTextAsync(Path.Combine(layout.LogDirectory, "raw.log"), "before " + Secret, TestContext.Current.CancellationToken);
            var unreadableSecret = Path.Combine(layout.DataDirectory, "secret.pfx");
            await File.WriteAllTextAsync(unreadableSecret, Secret, TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(unreadableSecret, UnixFileMode.None);
            }

            layout.RetainOnlySanitizedEvidence(new SensitiveDataRedactor(Secret));
            Assert.False(Directory.Exists(layout.ConfigurationDirectory));
            Assert.False(Directory.Exists(layout.DataDirectory));
            var retained = Directory.GetFiles(layout.CollectedLogDirectory, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(retained);
            Assert.All(retained, path => Assert.DoesNotContain(Secret, File.ReadAllText(path), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SecretScanner_DiscardsAllEvidenceWhenPrivateKeyIsDetected()
    {
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var publish = await PristineJellyfinPublish.CreateAsync(
                new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                Path.Combine(tempRoot, "publish"),
                TestContext.Current.CancellationToken);
            var layout = JellyfinHostLayout.Create(Path.Combine(tempRoot, "artifacts"), publish);
            await File.WriteAllTextAsync(Path.Combine(layout.LogDirectory, "safe.log"), "bounded evidence", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(layout.LogDirectory, "key.log"), "-----BEGIN PRIVATE KEY-----", TestContext.Current.CancellationToken);

            Assert.Throws<InvalidOperationException>(() => layout.RetainOnlySanitizedEvidence(new SensitiveDataRedactor()));
            Assert.False(Directory.Exists(layout.CollectedLogDirectory));
            Assert.False(Directory.Exists(layout.LogDirectory));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://attacker.invalid/")]
    [InlineData("//attacker.invalid/path")]
    [InlineData("/absolute")]
    [InlineData("\\\\attacker.invalid\\path")]
    public void HostRequests_RejectAbsoluteOrAuthorityTargets(string target)
        => Assert.False(JellyfinHostProcess.IsSafeRelativeRequestTarget(target));

    [Fact]
    public void HostRequests_AcceptRelativeTargets()
        => Assert.True(JellyfinHostProcess.IsSafeRelativeRequestTarget("Startup/User"));

    [Fact]
    public void ListenerParser_AcceptsOnlyOneExactLoopbackListener()
    {
        const int Port = 8096;
        const string Loopback4 = " 0: 0100007F:1FA0 00000000:0000 0A 0:0 00:00000000 00000000 1000 0 12345";
        const string Wildcard4 = " 0: 00000000:1FA0 00000000:0000 0A 0:0 00:00000000 00000000 1000 0 12346";
        const string NonLoopback4 = " 0: 0200007F:1FA0 00000000:0000 0A 0:0 00:00000000 00000000 1000 0 12347";
        const string Loopback6 = " 0: 00000000000000000000000001000000:1FA0 00000000000000000000000000000000:0000 0A 0:0 00:00000000 00000000 1000 0 12348";

        Assert.True(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([Loopback4], [], Port, out var ipv4Inode));
        Assert.Equal("12345", ipv4Inode);
        Assert.True(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([], [Loopback6], Port, out var ipv6Inode));
        Assert.Equal("12348", ipv6Inode);
        Assert.False(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([Wildcard4], [], Port, out _));
        Assert.False(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([NonLoopback4], [], Port, out _));
        Assert.False(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([Loopback4, Wildcard4], [], Port, out _));
        Assert.False(JellyfinHostProcess.TryFindSingleLoopbackListenerInode([Loopback4], [Loopback6], Port, out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("80;w /tmp/injected")]
    [InlineData("80\nq")]
    public async Task SmokePortValidation_RejectsInjection(string value)
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var result = await ExternalCommand.RunAsync(
            "/bin/bash",
            new[] { Path.Combine(root, "tests", "black-box-smoke.sh"), "--validate-port", "TEST_PORT", value },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task SmokeShellCleanup_TerminatesDetachedProcessGroupAndInheritedPipes()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var result = await ExternalCommand.RunAsync(
            "/bin/bash",
            new[] { Path.Combine(root, "tests", "black-box-smoke.sh"), "--test-detached-cleanup" },
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);
        result.EnsureSuccess("Running the detached-child shell cleanup regression", new SensitiveDataRedactor());
    }

    [Fact]
    public async Task SmokeShellSupervisor_PublicationFailureFailsClosedWithoutDiagnosticsLeak()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var result = await ExternalCommand.RunAsync(
            "/bin/bash",
            new[] { Path.Combine(root, "tests", "black-box-smoke.sh"), "--test-supervisor-publication-failure" },
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);
        result.EnsureSuccess("Running the identity-publication failure regression", new SensitiveDataRedactor());
    }

    [Fact]
    public async Task SmokeShellContainerReaper_ValidatesBeforeMutationAndSerializesWorkers()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var result = await ExternalCommand.RunAsync(
            "/bin/bash",
            new[] { Path.Combine(root, "tests", "black-box-smoke.sh"), "--test-container-reaper" },
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);
        result.EnsureSuccess("Running the shell stale-container reaper regressions", new SensitiveDataRedactor());
    }

    [Fact]
    public async Task LinuxSupervisor_ReapsRootExitsFirstSetsidDoubleForkWithInheritedPipe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        var pidFile = Path.Combine(root, "detached.pid");
        var logFile = Path.Combine(root, "supervised.log");
        try
        {
            using var supervisor = LinuxProcessSupervisor.Start(
                root,
                logFile,
                new SensitiveDataRedactor("supervisor-regression-secret"),
                [
                    "/bin/sh", "-c",
                    $"(sh -c \"setsid sh -c 'trap \\\"\\\" TERM; echo \\$\\$ > \\\"{pidFile}\\\"; printf supervisor-; sleep 0.05; printf regression-secret-inherited-pipe-output; sleep 30' & exit 0\" &) ; exit 0"
                ]);
            Assert.False(LinuxProcessSupervisor.IdentityMatches(
                supervisor.SupervisorIdentity.ProcessId,
                supervisor.SupervisorIdentity.StartTime + 1));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await supervisor.Process.WaitForExitAsync(timeout.Token);
            Assert.True(File.Exists(pidFile));
            var detachedPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(detachedPid));
            var output = await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken);
            Assert.Contains("<redacted>-inherited-pipe-output", output, StringComparison.Ordinal);
            Assert.DoesNotContain("supervisor-regression-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PortReservations_AreCrossProcessDistinctAndListenerOwnershipIsProcessSpecific()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var first = LoopbackPortReservation.Acquire(root);
            using var second = LoopbackPortReservation.Acquire(root);
            Assert.NotEqual(first.Port, second.Port);
            var competingLease = await ExternalCommand.RunAsync(
                "flock",
                new[] { "-n", Path.Combine(root, first.Port + ".lock"), "-c", "true" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(0, competingLease.ExitCode);
            first.ReleaseSocketForLaunch();
            using var listener = new TcpListener(IPAddress.Loopback, first.Port);
            listener.Start();
            Assert.True(JellyfinHostProcess.ProcessOwnsListeningPort(Environment.ProcessId, first.Port));
            Assert.False(JellyfinHostProcess.ProcessOwnsListeningPort(1, first.Port));
            var firstLock = Path.Combine(root, first.Port + ".lock");
            var secondLock = Path.Combine(root, second.Port + ".lock");
            first.Dispose();
            second.Dispose();
            Assert.True(File.Exists(firstLock));
            Assert.True(File.Exists(secondLock));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCommand_CapsCapturedOutputWhileContinuingToDrain()
    {
        var result = await ExternalCommand.RunAsync(
            "/bin/sh",
            new[] { "-c", "head -c 2097152 /dev/zero | tr '\\0' x; head -c 2097152 /dev/zero | tr '\\0' y >&2" },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardOutput.Length, 1, 1024 * 1024);
        Assert.InRange(result.StandardError.Length, 1, 1024 * 1024);
    }

    [Fact]
    public async Task HostOutputStreaming_BoundsNewlineFreeFloodAndRedactsAcrossChunks()
    {
        const string Secret = "cross-chunk-host-secret";
        var root = CreateTemporaryDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "stdout-flood.log");
            var destinationPath = Path.Combine(root, "sanitized.log");
            await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var flood = Enumerable.Repeat((byte)'x', 8192).ToArray();
                await source.WriteAsync(flood.AsMemory(0, 4090), TestContext.Current.CancellationToken);
                await source.WriteAsync(Encoding.UTF8.GetBytes(Secret), TestContext.Current.CancellationToken);
                for (var index = 0; index < 2048; index++)
                {
                    await source.WriteAsync(flood, TestContext.Current.CancellationToken);
                }
            }

            using var input = new StreamReader(sourcePath, Encoding.UTF8, false, 4096);
            await using var outputStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var output = new StreamWriter(outputStream, new UTF8Encoding(false), 4096, leaveOpen: true);
            await JellyfinHostProcess.CopySanitizedOutputAsync(input, output, new SensitiveDataRedactor(Secret), 1024 * 1024);
            Assert.InRange(outputStream.Length, 1, 1024 * 1024);
            var sanitized = await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken);
            Assert.Contains("<redacted>", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, sanitized, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LogCollection_StreamsHugeFilesWithinPerFileLimit()
    {
        const string Secret = "cross-chunk-log-secret";
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(tempRoot, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            var hash = await PristineJellyfinPublish.HashDirectoryAsync(source, TestContext.Current.CancellationToken);
            var publish = await PristineJellyfinPublish.CreateAsync(
                new PristineJellyfinPublishOptions { PublishDirectory = source, Sha256 = hash },
                Path.Combine(tempRoot, "publish"),
                TestContext.Current.CancellationToken);
            var layout = JellyfinHostLayout.Create(Path.Combine(tempRoot, "artifacts"), publish);
            var log = Path.Combine(layout.LogDirectory, "huge.log");
            await using (var stream = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var chunk = Enumerable.Repeat((byte)'z', 8192).ToArray();
                await stream.WriteAsync(chunk.AsMemory(0, 4090), TestContext.Current.CancellationToken);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Secret), TestContext.Current.CancellationToken);
                for (var index = 0; index < 2048; index++)
                {
                    await stream.WriteAsync(chunk, TestContext.Current.CancellationToken);
                }
            }

            layout.CollectSanitizedLogs(new SensitiveDataRedactor(Secret));
            var retained = Assert.Single(Directory.EnumerateFiles(layout.CollectedLogDirectory));
            Assert.InRange(new FileInfo(retained).Length, 1, 4 * 1024 * 1024);
            var retainedText = await File.ReadAllTextAsync(retained, TestContext.Current.CancellationToken);
            Assert.Contains("<redacted>", retainedText, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, retainedText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrebuiltHashHelper_EmitsExactlyOneDigestDespiteStderrWarning()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var helper = Path.Combine(root, "tests", "Jellyfin.Plugin.Database.Npgsql.TestHarness", "bin", "Release", "net10.0", "Jellyfin.Plugin.Database.Npgsql.TestHarness.dll");
        var tempRoot = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "jellyfin.dll"), "host", TestContext.Current.CancellationToken);
            var result = await ExternalCommand.RunAsync(
                "/bin/sh",
                new[] { "-c", "echo simulated-warning >&2; exec dotnet \"$1\" hash-publish \"$2\"", "hash-test", helper, tempRoot },
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.Matches("^[0-9a-f]{64}\\n?$", result.StandardOutput);
            Assert.Contains("simulated-warning", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCommand_TimeoutTerminatesProcessTree()
    {
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => ExternalCommand.RunAsync(
                "/bin/sh",
                new[] { "-c", "sleep 30" },
                TimeSpan.FromMilliseconds(250),
                TestContext.Current.CancellationToken));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExternalCommand_TimeoutDoesNotHangOnDetachedInheritedOutput()
    {
        var tempRoot = CreateTemporaryDirectory();
        var pidFile = Path.Combine(tempRoot, "descendant.pid");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => ExternalCommand.RunAsync(
                    "/bin/sh",
                    new[] { "-c", $"setsid sh -c 'trap \"\" TERM; echo $$ > \"{pidFile}\"; sleep 30' & sleep 30" },
                    TimeSpan.FromMilliseconds(500),
                    TestContext.Current.CancellationToken));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12));
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken), out var pid))
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task CreateOwnedArtifactRootAsync(string path)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(
            Path.Combine(path, ".jellyfin-npgsql-owned-artifacts"),
            "Owned by Jellyfin.Plugin.Database.Npgsql.TestHarness\n",
            TestContext.Current.CancellationToken);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-test-infra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
