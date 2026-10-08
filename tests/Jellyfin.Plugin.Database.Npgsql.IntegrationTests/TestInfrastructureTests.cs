using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class TestInfrastructureTests
{
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
    [InlineData("@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336")]
    public void ContainerImageValidation_RejectsMutableOrMalformedReferences(string image)
        => Assert.False(PostgreSqlTestOptions.IsDigestPinnedImage(image));

    [Fact]
    public void ContainerImageValidation_AcceptsDefaultPinnedReference()
        => Assert.True(PostgreSqlTestOptions.IsDigestPinnedImage(PostgreSqlTestOptions.DefaultContainerImage));

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
                Path.Combine(layout.CollectedLogDirectory, "logs_jellyfin.log"),
                TestContext.Current.CancellationToken);
            Assert.Contains("<redacted>", collectedLog, StringComparison.Ordinal);
            Assert.DoesNotContain(LogSecret, collectedLog, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Directory.GetParent(PluginRepository.FindRoot(AppContext.BaseDirectory))!.FullName,
                publish.DirectoryPath,
                StringComparison.Ordinal);
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-test-infra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
