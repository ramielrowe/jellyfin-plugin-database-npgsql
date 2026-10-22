using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class StockServerLifecycleTests
{
    private const string Baseline = "20261009061101_InitialPostgreSqlBaseline";
    private const string Successor = "20261009061446_AddActivityLogTypeDateIndex";
    private const string UuidAggregates = "20261009113458_AddUuidMinMaxAggregates";
    private const string SerializeTextInterning = "20261011031154_SerializeTextInterningMisses";
    private const string PredecessorVersion = "0.0.9.0";
    private const string PredecessorDirectory = "Npgsql Database_" + PredecessorVersion;

    private static readonly string[] PackageFiles =
    [
        "Jellyfin.Plugin.Database.Npgsql.dll",
        "Jellyfin.Plugin.Database.Npgsql.deps.json",
        "LICENSE",
        "Npgsql.EntityFrameworkCore.PostgreSQL.dll",
        "Npgsql.dll",
        "THIRD-PARTY-NOTICES.md",
        "meta.json",
    ];

    [Fact]
    public async Task PredecessorFixture_IsImmutableAuditableAndIntegrityPinned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "predecessor");
        var manifest = JsonSerializer.Deserialize<PredecessorFixtureManifest>(
            await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "fixture-manifest.json"), cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The predecessor fixture manifest was empty.");
        Assert.Equal(PredecessorVersion, manifest.Version);
        Assert.Equal(40, manifest.HistoricalSourceCommit.Length);
        Assert.Equal(Successor, manifest.LastMigration);
        Assert.Equal(51, manifest.ExpectedHistoryRows);

        var sourceRoot = Path.Combine(fixtureRoot, "source-tree");
        Assert.Equal(
            manifest.SourceSha256,
            await PristineJellyfinPublish.HashDirectoryAsync(sourceRoot, cancellationToken));
        var fixtureProject = XDocument.Load(Path.Combine(
            fixtureRoot,
            "Jellyfin.Plugin.Database.Npgsql.PredecessorFixture.csproj"));
        var compileIncludes = fixtureProject.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Equal(new[] { "source-tree/**/*.cs" }, compileIncludes);
        Assert.DoesNotContain("../../../src", fixtureProject.ToString(), StringComparison.Ordinal);

        var buildDirectory = Path.Combine(fixtureRoot, "bin", "Release", "net10.0");
        var assemblyPath = Path.Combine(buildDirectory, PluginMetadata.AssemblyFileName);
        Assert.True(File.Exists(assemblyPath), "Build the immutable predecessor fixture in Release before running its audit.");

        // The DLL embeds its portable PDB's ID, and the PDB records the compiler and the
        // runtime it ran on, so any other SDK or a newer patch runtime installed beside
        // it changes the pinned hash. Name that cause rather than a bare hash mismatch.
        var (compilerVersion, runtimeVersion) = ReadCompilerToolchain(Path.ChangeExtension(assemblyPath, ".pdb"));
        Assert.True(
            compilerVersion == manifest.Toolchain.CompilerVersion && runtimeVersion == manifest.Toolchain.RuntimeVersion,
            $"The predecessor fixture was compiled by Roslyn {compilerVersion} on .NET {runtimeVersion}. Its pinned hash "
                + $"requires SDK {manifest.Toolchain.SdkVersion} (Roslyn {manifest.Toolchain.CompilerVersion}) running on "
                + $"its bundled runtime {manifest.Toolchain.RuntimeVersion}, with no newer patch runtime installed beside it.");
        Assert.Equal(manifest.AssemblySha256, await HashFileAsync(assemblyPath, cancellationToken));
        var assemblyText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(assemblyPath, cancellationToken));
        Assert.Contains(Successor, assemblyText, StringComparison.Ordinal);
        Assert.Contains(PredecessorVersion, assemblyText, StringComparison.Ordinal);
        foreach (var forbidden in manifest.ForbiddenPostReleaseSymbols)
        {
            Assert.DoesNotContain(forbidden, assemblyText, StringComparison.Ordinal);
        }

        var currentPackage = Path.Combine(
            repositoryRoot,
            "artifacts",
            "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
        Assert.True(File.Exists(currentPackage), "Build the audited current plugin ZIP before running the predecessor audit.");
        using var artifacts = HostTestArtifactScope.Create("predecessor-integrity");
        var package = CreatePredecessorPackage(repositoryRoot, currentPackage, artifacts.CaseDirectory);
        var packageHash = await HashFileAsync(package, cancellationToken);
        Assert.True(
            string.Equals(manifest.PackageSha256, packageHash, StringComparison.Ordinal),
            $"Predecessor package SHA-256 changed: {packageHash}");
        using var archive = ZipFile.OpenRead(package);
        Assert.Equal(PackageFiles.Order(StringComparer.Ordinal), archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StockHost_ConnectionFailuresAreBoundedRedactedAndNeverFallbackOrMutate()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(
            databaseOptions.IsConfigured && IsCurrentHostConfigured(),
            "Configure PostgreSQL and the integrity-pinned current stock host to run lifecycle connection failures.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-lifecycle-connections");
        var publish = await PristineJellyfinPublish.CreateAsync(
            PristineJellyfinPublishOptions.FromEnvironment(),
            Path.Combine(artifacts.CaseDirectory, "host"),
            cancellationToken);
        var package = CurrentPackage(repositoryRoot);
        var originalSchema = await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken);
        Assert.Empty(originalSchema);
        var valid = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
        {
            Timeout = 2,
            CommandTimeout = 2,
        };

        var wrongPassword = "wrong-password-" + Guid.NewGuid().ToString("N");
        var wrongCredentials = new NpgsqlConnectionStringBuilder(valid.ConnectionString) { Password = wrongPassword };
        await AssertStockStartupFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            "wrong-credentials",
            wrongCredentials.ConnectionString,
            [wrongPassword, valid.Password!],
            ["28P01", "password authentication failed"],
            cancellationToken);
        Assert.Equal(originalSchema, await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken));

        var missingDatabaseName = TestResourceNames.Create("missing").DatabaseName;
        var missingDatabase = new NpgsqlConnectionStringBuilder(valid.ConnectionString) { Database = missingDatabaseName };
        await AssertStockStartupFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            "missing-database",
            missingDatabase.ConnectionString,
            [valid.Password!],
            ["does not exist"],
            cancellationToken);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            missingDatabaseName,
            cancellationToken));
        Assert.Equal(originalSchema, await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken));

        using (var refusedPort = LoopbackPortReservation.Acquire(Path.Combine(artifacts.CaseDirectory, ".refused-port-leases")))
        {
            var refused = new NpgsqlConnectionStringBuilder(valid.ConnectionString)
            {
                Host = "127.0.0.1",
                Port = refusedPort.Port,
            };
            await AssertStockStartupFailureAsync(
                publish,
                package,
                artifacts.CaseDirectory,
                repositoryRoot,
                "network-unreachable",
                refused.ConnectionString,
                [valid.Password!],
                ["operation has timed out", $"127.0.0.1:{refusedPort.Port}"],
                cancellationToken);
        }

        Assert.Equal(originalSchema, await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken));
        await database.ExecuteAsync("REVOKE CREATE ON SCHEMA public FROM pg_database_owner", cancellationToken);
        try
        {
            await AssertStockStartupFailureAsync(
                publish,
                package,
                artifacts.CaseDirectory,
                repositoryRoot,
                "insufficient-permissions",
                valid.ConnectionString,
                [valid.Password!],
                ["permission denied"],
                cancellationToken);
            Assert.Equal(originalSchema, await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken));
        }
        finally
        {
            await database.ExecuteAsync("GRANT CREATE ON SCHEMA public TO pg_database_owner", CancellationToken.None);
        }

        await publish.VerifyUnchangedAsync(cancellationToken);
    }

    [Fact]
    public async Task StockHost_SelectedProviderStatesAreDistinctAndNeverFallback()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(
            databaseOptions.IsConfigured && IsCurrentHostConfigured(),
            "Configure PostgreSQL and the integrity-pinned current stock host to run selected-provider failures.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-lifecycle-provider-states");
        var publish = await PristineJellyfinPublish.CreateAsync(
            PristineJellyfinPublishOptions.FromEnvironment(),
            Path.Combine(artifacts.CaseDirectory, "host"),
            cancellationToken);
        var package = CurrentPackage(repositoryRoot);
        var originalSchema = await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken);
        var secret = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password!;

        await AssertProviderSelectionFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            database.RuntimeConnectionString,
            "missing-folder",
            installPackage: false,
            deleteAssembly: false,
            deleteMetadata: false,
            pluginName: PluginMetadata.InstallationDirectory,
            expectedDiagnostic: "requested custom database plugin",
            secret,
            cancellationToken);
        await AssertProviderSelectionFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            database.RuntimeConnectionString,
            "deleted-assembly",
            installPackage: true,
            deleteAssembly: true,
            deleteMetadata: false,
            pluginName: PluginMetadata.InstallationDirectory,
            expectedDiagnostic: "Could not find the requested assembly",
            secret,
            cancellationToken);
        await AssertProviderSelectionFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            database.RuntimeConnectionString,
            "ambiguous-prefix",
            installPackage: true,
            deleteAssembly: false,
            deleteMetadata: false,
            pluginName: PluginMetadata.InstallationDirectory[..PluginMetadata.InstallationDirectory.LastIndexOf('.')],
            expectedDiagnostic: "exact folder and PluginName",
            secret,
            cancellationToken);

        var wrongPassword = "metadata-proof-" + Guid.NewGuid().ToString("N");
        var wrongCredentials = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
        {
            Password = wrongPassword,
            Timeout = 2,
            CommandTimeout = 2,
        };
        await AssertProviderSelectionFailureAsync(
            publish,
            package,
            artifacts.CaseDirectory,
            repositoryRoot,
            wrongCredentials.ConnectionString,
            "deleted-metadata-still-early-loads",
            installPackage: true,
            deleteAssembly: false,
            deleteMetadata: true,
            pluginName: PluginMetadata.InstallationDirectory,
            expectedDiagnostic: "ordinary plugin metadata is missing, disabled, malformed",
            wrongPassword,
            cancellationToken);

        Assert.Equal(originalSchema, await ReadPublicSchemaAsync(database.RuntimeConnectionString, cancellationToken));
        await publish.VerifyUnchangedAsync(cancellationToken);
    }

    [Fact]
    public async Task StockHost_FullSystemRestoreEntryPointsRejectBeforeProtectedStateMutation()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(
            databaseOptions.IsConfigured && IsCurrentHostConfigured(),
            "Configure PostgreSQL and the integrity-pinned current stock host to run the full-system restore guard proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-restore-guard");
        var publish = await PristineJellyfinPublish.CreateAsync(
            PristineJellyfinPublishOptions.FromEnvironment(),
            Path.Combine(artifacts.CaseDirectory, "host"),
            cancellationToken);
        var layout = JellyfinHostLayout.Create(artifacts.CaseDirectory, publish, "restore-guard");
        var package = CurrentPackage(repositoryRoot);
        var pluginDirectory = layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
        layout.WriteDatabaseConfiguration(
            database.RuntimeConnectionString,
            PluginMetadata.InstallationDirectory,
            PluginMetadata.AssemblyFileName);
        var leaseRoot = Path.Combine(layout.RunRoot, ".port-leases");
        using var initialHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
        using var initialHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
        layout.WriteNetworkConfiguration(initialHttpPort.Port, initialHttpsPort.Port);
        var adminPassword = "restore-guard-admin-" + Guid.NewGuid().ToString("N");
        var databasePassword = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password!;
        var redactor = new SensitiveDataRedactor(database.RuntimeConnectionString, databasePassword, adminPassword);
        var ffmpeg = PrepareFakeMediaTools(repositoryRoot, artifacts.CaseDirectory);
        string adminToken;

        await using (var host = JellyfinHostProcess.StartForRestartableLifecycle(
            layout,
            initialHttpPort,
            redactor,
            ffmpeg))
        {
            await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
            _ = await host.GetStringAsync("Startup/User", cancellationToken);
            await host.PostJsonAsync(
                "Startup/User",
                JsonSerializer.Serialize(new { Name = "restore-guard-admin", Password = adminPassword }),
                authorization: null,
                cancellationToken);
            await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
            adminToken = await AuthenticateAsync(host, "restore-guard-admin", adminPassword, "restore-guard", cancellationToken);
            redactor.Add(adminToken);

            var backupDirectory = Path.Combine(layout.DataDirectory, "data", "backups");
            Directory.CreateDirectory(backupDirectory);
            var archives = new[]
            {
                WriteDatabaseRestoreArchive(backupDirectory, "postgresql-database.zip", "postgresql"),
                WriteDatabaseRestoreArchive(backupDirectory, "sqlite-database.zip", "sqlite"),
                WriteDatabaseRestoreArchive(backupDirectory, "unknown-database.zip", "unknown"),
                WriteCorruptRestoreArchive(backupDirectory, "corrupt-database.zip"),
            };
            var protectedState = await WaitForProtectedStateStableAsync(
                layout,
                database.RuntimeConnectionString,
                cancellationToken);
            foreach (var archive in archives)
            {
                var exception = await Assert.ThrowsAsync<HttpRequestException>(() => host.PostJsonAsync(
                    "Backup/Restore",
                    JsonSerializer.Serialize(new { ArchiveFileName = Path.GetFileName(archive) }),
                    Authorization(adminToken),
                    cancellationToken));
                Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
                Assert.False(host.HasExited);
                var after = await CaptureProtectedStateAsync(layout, database.RuntimeConnectionString, cancellationToken);
                Assert.Equal(protectedState.FilesystemHash, after.FilesystemHash);
                Assert.Equal(protectedState.DatabaseHash, after.DatabaseHash);
                Assert.Empty(FindSqliteFiles(layout));
            }

            await host.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
        }

        var startupArchive = Path.Combine(layout.DataDirectory, "data", "backups", "postgresql-database.zip");
        await AssertStartupRestoreRejectedWithoutMutationAsync(
            layout,
            database.RuntimeConnectionString,
            leaseRoot,
            redactor,
            ffmpeg,
            startupArchive,
            expectedDiagnostic: NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage,
            cancellationToken);

        var metadataPath = Path.Combine(pluginDirectory, "meta.json");
        var activeMetadata = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        var disabledMetadata = activeMetadata.Replace(
            "\"status\": \"Active\"",
            "\"status\": \"Disabled\"",
            StringComparison.Ordinal);
        Assert.NotEqual(activeMetadata, disabledMetadata);
        await File.WriteAllTextAsync(metadataPath, disabledMetadata, cancellationToken);
        await AssertStartupRestoreRejectedWithoutMutationAsync(
            layout,
            database.RuntimeConnectionString,
            leaseRoot,
            redactor,
            ffmpeg,
            startupArchive,
            expectedDiagnostic: NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage,
            cancellationToken);

        await File.WriteAllTextAsync(metadataPath, activeMetadata, cancellationToken);
        var heldMetadata = Path.Combine(artifacts.CaseDirectory, "held-meta.json");
        File.Move(metadataPath, heldMetadata);
        try
        {
            await AssertStartupRestoreRejectedWithoutMutationAsync(
                layout,
                database.RuntimeConnectionString,
                leaseRoot,
                redactor,
                ffmpeg,
                startupArchive,
                expectedDiagnostic: NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage,
                cancellationToken);
        }
        finally
        {
            File.Move(heldMetadata, metadataPath);
        }

        await File.WriteAllTextAsync(metadataPath, "{ malformed ordinary plugin metadata", cancellationToken);
        try
        {
            await AssertStartupRestoreRejectedWithoutMutationAsync(
                layout,
                database.RuntimeConnectionString,
                leaseRoot,
                redactor,
                ffmpeg,
                startupArchive,
                expectedDiagnostic: NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage,
                cancellationToken);
        }
        finally
        {
            await File.WriteAllTextAsync(metadataPath, activeMetadata, CancellationToken.None);
        }

        await File.WriteAllBytesAsync(
            Path.Combine(pluginDirectory, "MalformedLateRegistration.dll"),
            "not a managed assembly"u8.ToArray(),
            cancellationToken);
        await AssertStartupRestoreRejectedWithoutMutationAsync(
            layout,
            database.RuntimeConnectionString,
            leaseRoot,
            redactor,
            ffmpeg,
            startupArchive,
            expectedDiagnostic: NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage,
            cancellationToken);

        Assert.Empty(FindSqliteFiles(layout));
        await publish.VerifyUnchangedAsync(cancellationToken);
    }

    [Fact]
    public async Task PackagedPredecessor_FreshStartUpgradeRestart_PreservesDataAndExactHistory()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        Assert.SkipUnless(
            databaseOptions.IsConfigured
                && (!string.IsNullOrWhiteSpace(publishDirectory) || !string.IsNullOrWhiteSpace(publishArchive)),
            "Configure the real PostgreSQL fixture and an integrity-pinned pristine Jellyfin publish to run the stock lifecycle proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-lifecycle-upgrade");
        var password = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password
            ?? throw new InvalidOperationException("The isolated runtime fixture did not generate a password.");
        var evidenceRedactor = new SensitiveDataRedactor(database.RuntimeConnectionString, password);
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifacts.CaseDirectory, "host"),
                cancellationToken);
            var layout = JellyfinHostLayout.Create(artifacts.CaseDirectory, publish, "stock-lifecycle");
            var currentPackage = Path.Combine(
                repositoryRoot,
                "artifacts",
                "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
            Assert.True(File.Exists(currentPackage), "Build the audited current plugin ZIP before running the lifecycle proof.");
            var predecessorPackage = CreatePredecessorPackage(repositoryRoot, currentPackage, artifacts.CaseDirectory);
            var currentHash = await HashFileAsync(currentPackage, cancellationToken);
            var predecessorHash = await HashFileAsync(predecessorPackage, cancellationToken);

            layout.InstallPluginZip(predecessorPackage, PredecessorDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PredecessorDirectory,
                PluginMetadata.AssemblyFileName);
            var leaseRoot = Path.Combine(artifacts.CaseDirectory, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(leaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(leaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var adminPassword = "lifecycle-admin-" + Guid.NewGuid().ToString("N");
            var userPassword = "lifecycle-user-" + Guid.NewGuid().ToString("N");
            evidenceRedactor.Add(adminPassword);
            evidenceRedactor.Add(userPassword);
            var redactor = new SensitiveDataRedactor(
                database.RuntimeConnectionString,
                password,
                adminPassword,
                userPassword);
            var ffmpeg = PrepareFakeMediaTools(repositoryRoot, artifacts.CaseDirectory);
            WriteMediaFixture(layout.MediaDirectory);
            string adminToken;

            await using (var predecessor = JellyfinHostProcess.StartForRestartableLifecycle(
                layout,
                httpPort,
                redactor,
                ffmpeg))
            {
                await predecessor.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                _ = await predecessor.GetStringAsync("Startup/User", cancellationToken);
                await predecessor.PostJsonAsync(
                    "Startup/User",
                    JsonSerializer.Serialize(new { Name = "lifecycle-admin", Password = adminPassword }),
                    authorization: null,
                    cancellationToken);
                await predecessor.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
                adminToken = await AuthenticateAsync(
                    predecessor,
                    "lifecycle-admin",
                    adminPassword,
                    "predecessor",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                await predecessor.PostJsonAsync(
                    "Users/New",
                    JsonSerializer.Serialize(new { Name = "upgrade-user-雪", Password = userPassword }),
                    Authorization(adminToken),
                    cancellationToken);
                await predecessor.PostJsonAsync(
                    "Library/VirtualFolders?name="
                        + Uri.EscapeDataString("Lifecycle Movies 雪")
                        + "&collectionType=movies&paths="
                        + Uri.EscapeDataString(layout.MediaDirectory)
                        + "&refreshLibrary=true",
                    JsonSerializer.Serialize(new
                    {
                        LibraryOptions = new
                        {
                            EnableRealtimeMonitor = false,
                            SaveLocalMetadata = false,
                            EnableInternetProviders = false
                        }
                    }),
                    Authorization(adminToken),
                    cancellationToken);
                await predecessor.PostJsonAsync(
                    "Library/Refresh",
                    "{}",
                    Authorization(adminToken),
                    cancellationToken);
                await WaitForMediaRowAsync(database.RuntimeConnectionString, cancellationToken);
                await predecessor.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }

            var predecessorDiagnostics = ReadCollectedLogs(layout);
            Assert.Contains("Npgsql database provider 0.0.9.0 loaded", predecessorDiagnostics, StringComparison.Ordinal);
            Assert.Contains("Skipping disabled plugin", predecessorDiagnostics, StringComparison.Ordinal);

            var predecessorHistory = await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken);
            Assert.Equal(ExpectedHistory(includeUuidAggregates: false), predecessorHistory);
            Assert.Equal(2, await CountUsersAsync(database.RuntimeConnectionString, cancellationToken));

            // A staged update that is interrupted before the atomic database.xml
            // switch must leave the exact predecessor selection usable. The
            // partial directory is deliberately disabled so ordinary discovery
            // cannot mistake it for an installable release.
            var interruptedDirectory = Path.Combine(
                layout.DataDirectory,
                "plugins",
                PluginMetadata.InstallationDirectory + ".partial");
            Directory.CreateDirectory(interruptedDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(interruptedDirectory, "meta.json"),
                (await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "meta.json"), cancellationToken))
                    .Replace("\"status\": \"Active\"", "\"status\": \"Disabled\"", StringComparison.Ordinal),
                cancellationToken);
            using (var interruptedHttpPort = LoopbackPortReservation.Acquire(leaseRoot))
            using (var interruptedHttpsPort = LoopbackPortReservation.Acquire(leaseRoot))
            {
                layout.WriteNetworkConfiguration(interruptedHttpPort.Port, interruptedHttpsPort.Port);
                await using var interrupted = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    interruptedHttpPort,
                    redactor,
                    ffmpeg);
                await interrupted.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                adminToken = await AuthenticateAsync(
                    interrupted,
                    "lifecycle-admin",
                    adminPassword,
                    "interrupted-update",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                await interrupted.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }

            Directory.Delete(interruptedDirectory, recursive: true);

            var currentPluginDirectory = layout.InstallPluginZip(currentPackage, PluginMetadata.InstallationDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);
            using var upgradedHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
            using var upgradedHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
            layout.WriteNetworkConfiguration(upgradedHttpPort.Port, upgradedHttpsPort.Port);

            await using (var upgraded = JellyfinHostProcess.StartForRestartableLifecycle(
                layout,
                upgradedHttpPort,
                redactor,
                ffmpeg))
            {
                await upgraded.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                adminToken = await AuthenticateAsync(
                    upgraded,
                    "lifecycle-admin",
                    adminPassword,
                    "upgraded",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                using var users = JsonDocument.Parse(await upgraded.GetStringAsync(
                    "Users",
                    Authorization(adminToken),
                    cancellationToken));
                var names = users.RootElement.EnumerateArray()
                    .Select(user => user.GetProperty("Name").GetString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(new[] { "lifecycle-admin", "upgrade-user-雪" }.Order(StringComparer.Ordinal), names);
                await WaitForMediaAsync(upgraded, Authorization(adminToken), cancellationToken);
                await upgraded.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }

            var upgradedHistory = await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken);
            Assert.Equal(ExpectedHistory(includeUuidAggregates: true), upgradedHistory);
            Assert.Equal(2, await CountUsersAsync(database.RuntimeConnectionString, cancellationToken));
            var predecessorDirectory = Path.Combine(layout.DataDirectory, "plugins", PredecessorDirectory);
            if (!Directory.Exists(predecessorDirectory))
            {
                // Stock ordinary-plugin housekeeping may remove the disabled
                // predecessor after the successful update. Reinstalling the
                // checksummed prior artifact is the documented rollback step.
                layout.InstallPluginZip(predecessorPackage, PredecessorDirectory);
            }

            Assert.True(Directory.Exists(predecessorDirectory));
            Assert.True(Directory.Exists(currentPluginDirectory));

            // A deliberate package rollback removes the newer duplicate-GUID
            // ordinary plugin from discovery and selects the restored exact
            // predecessor directory. The database retains the newer lineage;
            // operators must use the migration snapshot when an actual schema
            // rollback is required.
            var heldCurrentForRollback = Path.Combine(artifacts.CaseDirectory, "held-current-for-rollback");
            Directory.Move(currentPluginDirectory, heldCurrentForRollback);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PredecessorDirectory,
                PluginMetadata.AssemblyFileName);
            try
            {
                using var rollbackHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
                using var rollbackHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
                layout.WriteNetworkConfiguration(rollbackHttpPort.Port, rollbackHttpsPort.Port);
                await using var rollback = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    rollbackHttpPort,
                    redactor,
                    ffmpeg);
                await rollback.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                adminToken = await AuthenticateAsync(
                    rollback,
                    "lifecycle-admin",
                    adminPassword,
                    "package-rollback",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                await rollback.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally
            {
                Directory.Move(heldCurrentForRollback, currentPluginDirectory);
            }

            Assert.Contains(
                "Npgsql database provider 0.0.9.0 loaded",
                ReadCollectedLogs(layout),
                StringComparison.Ordinal);
            Assert.Equal(upgradedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);

            using var restartedHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
            using var restartedHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
            layout.WriteNetworkConfiguration(restartedHttpPort.Port, restartedHttpsPort.Port);
            await using (var restarted = JellyfinHostProcess.StartForRestartableLifecycle(
                layout,
                restartedHttpPort,
                redactor,
                ffmpeg))
            {
                await restarted.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                using var publicInfo = JsonDocument.Parse(await restarted.GetStringAsync(
                    "System/Info/Public",
                    cancellationToken));
                Assert.False(string.IsNullOrWhiteSpace(publicInfo.RootElement.GetProperty("Id").GetString()));
                adminToken = await AuthenticateAsync(
                    restarted,
                    "lifecycle-admin",
                    adminPassword,
                    "restarted",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                await WaitForMediaAsync(restarted, Authorization(adminToken), cancellationToken);
                await restarted.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }

            Assert.Equal(upgradedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
            Assert.Equal(2, await CountUsersAsync(database.RuntimeConnectionString, cancellationToken));

            var metadataPath = Path.Combine(currentPluginDirectory, "meta.json");
            var activeMetadata = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
            await File.WriteAllTextAsync(
                metadataPath,
                Encoding.UTF8.GetString(activeMetadata)
                    .Replace("\"status\": \"Active\"", "\"status\": \"Disabled\"", StringComparison.Ordinal),
                cancellationToken);
            try
            {
                using var disabledHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
                using var disabledHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
                layout.WriteNetworkConfiguration(disabledHttpPort.Port, disabledHttpsPort.Port);
                await using var disabled = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    disabledHttpPort,
                    redactor,
                    ffmpeg);
                await disabled.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(30), cancellationToken);
                await disabled.TerminateAsync();
                Assert.Contains(
                    "ordinary plugin metadata is missing, disabled, malformed",
                    ReadCollectedLogs(layout),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(upgradedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
                Assert.Empty(FindSqliteFiles(layout));
            }
            finally
            {
                await File.WriteAllBytesAsync(metadataPath, activeMetadata, CancellationToken.None);
            }

            var heldPluginDirectory = Path.Combine(artifacts.CaseDirectory, "uninstalled-current-plugin");
            Directory.Move(currentPluginDirectory, heldPluginDirectory);
            try
            {
                using var uninstalledHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
                using var uninstalledHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
                layout.WriteNetworkConfiguration(uninstalledHttpPort.Port, uninstalledHttpsPort.Port);
                await using var uninstalled = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    uninstalledHttpPort,
                    redactor,
                    ffmpeg);
                await uninstalled.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(30), cancellationToken);
                await uninstalled.TerminateAsync();
                Assert.Contains(
                    "requested custom database plugin",
                    ReadCollectedLogs(layout),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(upgradedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
                Assert.Empty(FindSqliteFiles(layout));
            }
            finally
            {
                Directory.Move(heldPluginDirectory, currentPluginDirectory);
            }

            var providerAssembly = Path.Combine(currentPluginDirectory, PluginMetadata.AssemblyFileName);
            var heldProviderAssembly = Path.Combine(artifacts.CaseDirectory, "missing-provider-fixture.dll");
            File.Move(providerAssembly, heldProviderAssembly);
            try
            {
                using var missingHttpPort = LoopbackPortReservation.Acquire(leaseRoot);
                using var missingHttpsPort = LoopbackPortReservation.Acquire(leaseRoot);
                layout.WriteNetworkConfiguration(missingHttpPort.Port, missingHttpsPort.Port);
                await using var missingProvider = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    missingHttpPort,
                    redactor,
                    ffmpeg);
                await missingProvider.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(30), cancellationToken);
                await missingProvider.TerminateAsync();
                var diagnostics = ReadCollectedLogs(layout);
                Assert.Contains("Could not find the requested assembly", diagnostics, StringComparison.Ordinal);
                Assert.DoesNotContain(password, diagnostics, StringComparison.Ordinal);
                Assert.Empty(FindSqliteFiles(layout));
                Assert.Equal(upgradedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
            }
            finally
            {
                File.Move(heldProviderAssembly, providerAssembly);
            }

            using (var recoveryHttpPort = LoopbackPortReservation.Acquire(leaseRoot))
            using (var recoveryHttpsPort = LoopbackPortReservation.Acquire(leaseRoot))
            {
                layout.WriteNetworkConfiguration(recoveryHttpPort.Port, recoveryHttpsPort.Port);
                await using var recovered = JellyfinHostProcess.StartForRestartableLifecycle(
                    layout,
                    recoveryHttpPort,
                    redactor,
                    ffmpeg);
                await recovered.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                adminToken = await AuthenticateAsync(
                    recovered,
                    "lifecycle-admin",
                    adminPassword,
                    "recovered",
                    cancellationToken);
                redactor.Add(adminToken);
                evidenceRedactor.Add(adminToken);
                await recovered.StopGracefullyAsync(adminToken, TimeSpan.FromSeconds(30), cancellationToken);
            }

            Assert.Empty(FindSqliteFiles(layout));
            await publish.VerifyUnchangedAsync(cancellationToken);
            Console.WriteLine(
                $"Stock lifecycle PASS: host={publish.IntegrityHash}; predecessor={predecessorHash}; current={currentHash}; "
                + $"history={upgradedHistory.Length}; database={database.SanitizedRuntimeConnectionString}");
        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, evidenceRedactor);
            throw new InvalidOperationException(
                $"The stock lifecycle proof failed. Bounded sanitized diagnostics are in '{artifacts.RetainedEvidenceDirectory}' "
                + $"({artifacts.RetainedEvidenceBytes} bytes); raw case scratch was removed.",
                exception);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static string WriteDatabaseRestoreArchive(string backupDirectory, string fileName, string origin)
    {
        var path = Path.Combine(backupDirectory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(new
            {
                ServerVersion = "12.2.0",
                BackupEngineVersion = "0.2.0",
                DateCreated = DateTimeOffset.UnixEpoch,
                DatabaseTables = Array.Empty<string>(),
                Options = new { Database = true, Metadata = false, Trickplay = false, Subtitles = false },
                Origin = origin,
            }));
        }

        var config = archive.CreateEntry("Config/restore-mutation-sentinel.xml", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(config.Open(), new UTF8Encoding(false)))
        {
            writer.Write("restore must never extract this file");
        }

        var database = archive.CreateEntry("Database/HistoryRow.json", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(database.Open(), new UTF8Encoding(false)))
        {
            writer.Write("[]");
        }

        return path;
    }

    private static string WriteCorruptRestoreArchive(string backupDirectory, string fileName)
    {
        var path = Path.Combine(backupDirectory, fileName);
        File.WriteAllBytes(path, "corrupt archive with database provenance"u8.ToArray());
        return path;
    }

    private static async Task AssertStartupRestoreRejectedWithoutMutationAsync(
        JellyfinHostLayout layout,
        string connectionString,
        string leaseRoot,
        SensitiveDataRedactor redactor,
        string ffmpeg,
        string archivePath,
        string? expectedDiagnostic,
        CancellationToken cancellationToken)
    {
        using var httpPort = LoopbackPortReservation.Acquire(leaseRoot);
        using var httpsPort = LoopbackPortReservation.Acquire(leaseRoot);
        layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
        var before = await CaptureProtectedStateAsync(layout, connectionString, cancellationToken);
        await using var host = JellyfinHostProcess.StartForRestartableLifecycle(
            layout,
            httpPort,
            redactor,
            ffmpeg,
            ["--restore-archive", archivePath]);
        await host.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(25), cancellationToken);
        await host.TerminateAsync();
        var after = await CaptureProtectedStateAsync(layout, connectionString, cancellationToken);
        Assert.Equal(before.FilesystemHash, after.FilesystemHash);
        Assert.Equal(before.DatabaseHash, after.DatabaseHash);
        Assert.Empty(FindSqliteFiles(layout));
        if (expectedDiagnostic is not null)
        {
            var diagnostics = ReadCollectedLogs(layout);
            if (!diagnostics.Contains(expectedDiagnostic, StringComparison.OrdinalIgnoreCase))
            {
                var tail = diagnostics.Length > 16_384 ? diagnostics[^16_384..] : diagnostics;
                throw new InvalidOperationException(
                    $"Expected startup diagnostic '{expectedDiagnostic}' was absent. Sanitized diagnostic tail:\n{tail}");
            }
        }
    }

    private static async Task<ProtectedState> CaptureProtectedStateAsync(
        JellyfinHostLayout layout,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var filesystemHash = await HashProtectedTreesAsync(
            [
                ("Config", layout.ConfigurationDirectory),
                ("Data", Path.Combine(layout.DataDirectory, "data")),
                ("Root", Path.Combine(layout.DataDirectory, "root")),
            ],
            cancellationToken);
        var databaseHash = await HashDatabaseStateAsync(connectionString, cancellationToken);
        return new ProtectedState(filesystemHash, databaseHash);
    }

    private static async Task<ProtectedState> WaitForProtectedStateStableAsync(
        JellyfinHostLayout layout,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var previous = await CaptureProtectedStateAsync(layout, connectionString, cancellationToken);
        var stableObservations = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            var current = await CaptureProtectedStateAsync(layout, connectionString, cancellationToken);
            if (current == previous)
            {
                stableObservations++;
                if (stableObservations == 3)
                {
                    return current;
                }
            }
            else
            {
                previous = current;
                stableObservations = 0;
            }
        }

        throw new InvalidOperationException("The stock host protected state did not become stable before restore testing.");
    }

    private static async Task<string> HashProtectedTreesAsync(
        IReadOnlyList<(string Name, string Path)> roots,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in roots.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            Append(root.Name);
            if (!Directory.Exists(root.Path))
            {
                Append("missing");
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root.Path, "*", SearchOption.AllDirectories)
                         .Prepend(root.Path)
                         .OrderBy(path => Path.GetRelativePath(root.Path, path), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Append("D:" + Path.GetRelativePath(root.Path, directory).Replace(Path.DirectorySeparatorChar, '/'));
                Append(GetMode(directory));
            }

            foreach (var file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(root.Path, path), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Append("F:" + Path.GetRelativePath(root.Path, file).Replace(Path.DirectorySeparatorChar, '/'));
                Append(GetMode(file));
                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    hash.AppendData(buffer, 0, count);
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        static string GetMode(string path)
            => OperatingSystem.IsWindows()
                ? "windows"
                : Convert.ToString((int)File.GetUnixFileMode(path), 8);
    }

    private static async Task<string> HashDatabaseStateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await AppendQueryAsync(
            """
            SELECT table_name || '|' || ordinal_position::text || '|' || column_name || '|' || data_type || '|' || udt_name || '|' || is_nullable || '|' || coalesce(column_default, '') || '|' || is_identity || '|' || is_generated
            FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, ordinal_position
            """);
        await AppendQueryAsync(
            """
            SELECT c.relname || '|' || coalesce(pg_get_constraintdef(con.oid, true), '')
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
            ORDER BY c.relname, con.conname
            """);
        await AppendQueryAsync(
            "SELECT indexname || '|' || indexdef FROM pg_catalog.pg_indexes WHERE schemaname = 'public' ORDER BY indexname");
        await AppendQueryAsync(
            """
            SELECT p.proname || '|' || pg_get_function_identity_arguments(p.oid) || '|' || p.prokind::text || '|' || p.prorettype::regtype::text || '|' || p.prosrc || '|' || coalesce(p.probin, '')
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public'
            ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)
            """);

        var tables = new List<string>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'public' ORDER BY tablename";
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            Append("TABLE:" + table);
            var quoted = '"' + table.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await AppendQueryAsync(
                $"SELECT row_to_json(snapshot_row)::text FROM (SELECT * FROM {quoted}) snapshot_row ORDER BY row_to_json(snapshot_row)::text");
        }

        var sequences = new List<string>();
        await using (var sequenceCommand = connection.CreateCommand())
        {
            sequenceCommand.CommandText = "SELECT sequencename FROM pg_catalog.pg_sequences WHERE schemaname = 'public' ORDER BY sequencename";
            await using var reader = await sequenceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sequences.Add(reader.GetString(0));
            }
        }

        foreach (var sequence in sequences)
        {
            Append("SEQUENCE:" + sequence);
            var quoted = '"' + sequence.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await AppendQueryAsync($"SELECT last_value::text || '|' || is_called::text FROM {quoted}");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());

        async Task AppendQueryAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Append(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
            }
        }

        void Append(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
    }

    private sealed record ProtectedState(string FilesystemHash, string DatabaseHash);

    private static bool IsCurrentHostConfigured()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PristineJellyfinPublishOptions.ArchiveEnvironmentVariable));

    private static string CurrentPackage(string repositoryRoot)
    {
        var package = Path.Combine(
            repositoryRoot,
            "artifacts",
            "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
        Assert.True(File.Exists(package), "Build the audited current plugin ZIP before running lifecycle tests.");
        return package;
    }

    private static async Task AssertStockStartupFailureAsync(
        PristineJellyfinPublish publish,
        string package,
        string caseRoot,
        string repositoryRoot,
        string caseName,
        string connectionString,
        string[] secrets,
        string[] expectedDiagnostics,
        CancellationToken cancellationToken)
    {
        var layout = JellyfinHostLayout.Create(caseRoot, publish, caseName);
        layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
        layout.WriteDatabaseConfiguration(
            connectionString,
            PluginMetadata.InstallationDirectory,
            PluginMetadata.AssemblyFileName);
        var leaseRoot = Path.Combine(layout.RunRoot, ".port-leases");
        using var httpPort = LoopbackPortReservation.Acquire(leaseRoot);
        using var httpsPort = LoopbackPortReservation.Acquire(leaseRoot);
        layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
        var redactor = new SensitiveDataRedactor(secrets.Prepend(connectionString).ToArray());
        var ffmpeg = PrepareFakeMediaTools(repositoryRoot, layout.RunRoot);
        var stopwatch = Stopwatch.StartNew();
        await using var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg);
        await host.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(20), cancellationToken);
        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(35));
        await host.TerminateAsync();
        var diagnostics = ReadCollectedLogs(layout);
        foreach (var expected in expectedDiagnostics)
        {
            if (!diagnostics.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected startup diagnostic '{expected}' was absent. Sanitized diagnostics:\n{diagnostics}");
            }
        }

        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Password=", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE DATABASE ", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(FindSqliteFiles(layout));
    }

    private static async Task AssertProviderSelectionFailureAsync(
        PristineJellyfinPublish publish,
        string package,
        string caseRoot,
        string repositoryRoot,
        string connectionString,
        string caseName,
        bool installPackage,
        bool deleteAssembly,
        bool deleteMetadata,
        string pluginName,
        string expectedDiagnostic,
        string secret,
        CancellationToken cancellationToken)
    {
        var layout = JellyfinHostLayout.Create(caseRoot, publish, caseName);
        if (installPackage)
        {
            var pluginDirectory = layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            if (deleteAssembly)
            {
                File.Delete(Path.Combine(pluginDirectory, PluginMetadata.AssemblyFileName));
            }

            if (deleteMetadata)
            {
                File.Delete(Path.Combine(pluginDirectory, "meta.json"));
            }
        }

        layout.WriteDatabaseConfiguration(connectionString, pluginName, PluginMetadata.AssemblyFileName);
        var leaseRoot = Path.Combine(layout.RunRoot, ".port-leases");
        using var httpPort = LoopbackPortReservation.Acquire(leaseRoot);
        using var httpsPort = LoopbackPortReservation.Acquire(leaseRoot);
        layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
        var redactor = new SensitiveDataRedactor(connectionString, secret);
        var ffmpeg = PrepareFakeMediaTools(repositoryRoot, layout.RunRoot);
        await using var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg);
        await host.WaitForExpectedStartupFailureAsync(TimeSpan.FromSeconds(20), cancellationToken);
        await host.TerminateAsync();
        var diagnostics = ReadCollectedLogs(layout);
        Assert.Contains(expectedDiagnostic, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        Assert.Empty(FindSqliteFiles(layout));
    }

    private static async Task<string[]> ReadPublicSchemaAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind || ':' || name
            FROM (
                SELECT c.relkind::text AS kind, c.relname AS name
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
                UNION ALL
                SELECT 'f', p.proname
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public'
            ) objects
            ORDER BY kind, name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static string CreatePredecessorPackage(
        string repositoryRoot,
        string currentPackage,
        string caseDirectory)
    {
        var buildDirectory = Path.Combine(
            repositoryRoot,
            "tests",
            "fixtures",
            "predecessor",
            "bin",
            "Release",
            "net10.0");
        var packagePath = Path.Combine(caseDirectory, "jellyfin-plugin-database-npgsql_" + PredecessorVersion + ".zip");
        foreach (var file in PackageFiles.Where(file => !file.StartsWith("Npgsql", StringComparison.Ordinal)))
        {
            if (!File.Exists(Path.Combine(buildDirectory, file)))
            {
                throw new FileNotFoundException(
                    "Build the checked-in predecessor fixture in Release before running the stock lifecycle proof.",
                    Path.Combine(buildDirectory, file));
            }
        }

        using var currentArchive = ZipFile.OpenRead(currentPackage);
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var file in PackageFiles.Order(StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(file, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = Convert.ToInt32("100644", 8) << 16;
            using var input = file.StartsWith("Npgsql", StringComparison.Ordinal)
                ? currentArchive.GetEntry(file)?.Open()
                    ?? throw new InvalidDataException($"The current package did not contain required runtime dependency '{file}'.")
                : File.OpenRead(Path.Combine(buildDirectory, file));
            using var output = entry.Open();
            input.CopyTo(output);
        }

        return packagePath;
    }

    private static async Task<string> AuthenticateAsync(
        JellyfinHostProcess host,
        string username,
        string password,
        string launch,
        CancellationToken cancellationToken)
    {
        var json = await host.PostJsonAsync(
            "Users/AuthenticateByName",
            JsonSerializer.Serialize(new { Username = username, Pw = password }),
            $"MediaBrowser Client=\"npgsql-lifecycle\", Device=\"stock tests\", DeviceId=\"lifecycle-{launch}\", Version=\"0.1\"",
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("AccessToken").GetString()
            ?? throw new InvalidOperationException("The stock host returned an empty authentication token.");
    }

    private static string Authorization(string token) => "MediaBrowser Token=\"" + token + "\"";

    private static string[] ExpectedHistory(bool includeUuidAggregates)
    {
        IEnumerable<string> result = NpgsqlHostMigrationAudit.AuditedMigrationIds
            .Append(Baseline)
            .Append(Successor);
        if (includeUuidAggregates)
        {
            result = result.Append(UuidAggregates).Append(SerializeTextInterning);
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> ReadHistoryAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async Task<long> CountUsersAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM public.\"Users\"";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task WaitForMediaRowAsync(string connectionString, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM public.\"BaseItems\" WHERE \"Name\" = 'Lifecycle Movie 雪'";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException("The HTTP-driven predecessor scan did not persist the lifecycle movie.");
    }

    private static async Task WaitForMediaAsync(
        JellyfinHostProcess host,
        string authorization,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var document = JsonDocument.Parse(await host.GetStringAsync(
                    "Items?Recursive=true&IncludeItemTypes=Movie&Fields=Genres,People&EnableTotalRecordCount=true",
                    authorization,
                    cancellationToken));
                var items = document.RootElement.GetProperty("Items").EnumerateArray().ToArray();
                if (document.RootElement.GetProperty("TotalRecordCount").GetInt32() == 1
                    && items[0].GetProperty("Name").GetString() == "Lifecycle Movie 雪"
                    && items[0].GetProperty("Genres").EnumerateArray().Any(value => value.GetString() == "Upgrade Science")
                    && items[0].GetProperty("People").EnumerateArray().Any(
                        person => person.GetProperty("Name").GetString() == "Lifecycle Actor"))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException("The lifecycle movie did not remain available through the untouched host API.", lastError);
    }

    private static string[] FindSqliteFiles(JellyfinHostLayout layout)
        => new[] { layout.ConfigurationDirectory, layout.CacheDirectory, layout.DataDirectory }
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".db" or ".sqlite" or ".sqlite3")
            .ToArray();

    private static string ReadCollectedLogs(JellyfinHostLayout layout)
        => string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(layout.CollectedLogDirectory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

    private static string PrepareFakeMediaTools(string repositoryRoot, string caseDirectory)
    {
        var toolsDirectory = Path.Combine(caseDirectory, "media-tools");
        Directory.CreateDirectory(toolsDirectory);
        var ffmpeg = Path.Combine(toolsDirectory, "ffmpeg");
        var ffprobe = Path.Combine(toolsDirectory, "ffprobe");
        File.Copy(Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh"), ffmpeg);
        File.Copy(Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffprobe.sh"), ffprobe);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(ffmpeg, mode);
            File.SetUnixFileMode(ffprobe, mode);
        }

        return ffmpeg;
    }

    private static void WriteMediaFixture(string mediaDirectory)
    {
        File.WriteAllText(
            Path.Combine(mediaDirectory, "lifecycle-movie.strm"),
            "https://example.invalid/lifecycle-movie.mp4\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(mediaDirectory, "lifecycle-movie.nfo"),
            "<movie><title>Lifecycle Movie 雪</title><genre>Upgrade Science</genre>"
                + "<actor><name>Lifecycle Actor</name><role>Lead</role><type>Actor</type></actor></movie>",
            new UTF8Encoding(false));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static (string? CompilerVersion, string? RuntimeVersion) ReadCompilerToolchain(string pdbPath)
    {
        // Roslyn's compilation-options record: null-terminated UTF-8 name/value pairs.
        var compilationOptionsKind = new Guid("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        foreach (var handle in reader.CustomDebugInformation)
        {
            var information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) != compilationOptionsKind)
            {
                continue;
            }

            var fields = Encoding.UTF8.GetString(reader.GetBlobBytes(information.Value)).Split('\0');
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index + 1 < fields.Length; index += 2)
            {
                options[fields[index]] = fields[index + 1];
            }

            return (options.GetValueOrDefault("compiler-version"), options.GetValueOrDefault("runtime-version"));
        }

        return (null, null);
    }

    private sealed record PredecessorFixtureManifest(
        string Version,
        string HistoricalSourceCommit,
        string SourceSha256,
        string AssemblySha256,
        string PackageSha256,
        PredecessorFixtureToolchain Toolchain,
        string LastMigration,
        int ExpectedHistoryRows,
        string[] ForbiddenPostReleaseSymbols);

    private sealed record PredecessorFixtureToolchain(
        string SdkVersion,
        string CompilerVersion,
        string RuntimeVersion);
}
