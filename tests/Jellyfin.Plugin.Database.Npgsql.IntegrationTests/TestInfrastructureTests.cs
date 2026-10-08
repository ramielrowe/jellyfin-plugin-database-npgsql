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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-test-infra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
