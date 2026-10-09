using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

            var predecessorHistory = await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken);
            Assert.Equal(ExpectedHistory(includeUuidAggregates: false), predecessorHistory);
            Assert.Equal(2, await CountUsersAsync(database.RuntimeConnectionString, cancellationToken));

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
                _ = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => missingProvider.WaitUntilReadyAsync(TimeSpan.FromSeconds(30), cancellationToken));
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
            var entry = archive.CreateEntry(file, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
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
            result = result.Append(UuidAggregates);
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
}
