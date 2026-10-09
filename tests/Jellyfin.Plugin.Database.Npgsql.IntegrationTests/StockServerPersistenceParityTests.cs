using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class StockServerPersistenceParityTests
{
    [Fact]
    public async Task PackagedPlugin_UntouchedServerExercisesPublicApiAndMediaScanPersistence()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        Assert.SkipUnless(
            databaseOptions.IsConfigured
                && (!string.IsNullOrWhiteSpace(publishDirectory) || !string.IsNullOrWhiteSpace(publishArchive)),
            "Configure the real PostgreSQL fixture and an integrity-pinned pristine Jellyfin publish to run the stock-server parity proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-server-parity");
        var databasePassword = new global::Npgsql.NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password;
        var evidenceRedactor = new SensitiveDataRedactor(database.RuntimeConnectionString, databasePassword);
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifacts.CaseDirectory, "host"),
                cancellationToken);
            var layout = JellyfinHostLayout.Create(artifacts.CaseDirectory, publish, "stock-server-parity");
            var package = Path.Combine(
                repositoryRoot,
                "artifacts",
                "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
            Assert.True(File.Exists(package), "Build the audited plugin ZIP before running the stock-server parity proof.");
            layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);
            WriteMediaFixture(layout.MediaDirectory);

            var leaseRoot = Path.Combine(artifacts.CaseDirectory, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(leaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(leaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var adminPassword = "parity-admin-" + Guid.NewGuid().ToString("N");
            var userPassword = "parity-user-" + Guid.NewGuid().ToString("N");
            evidenceRedactor.Add(adminPassword);
            evidenceRedactor.Add(userPassword);
            var redactor = new SensitiveDataRedactor(
                database.RuntimeConnectionString,
                databasePassword,
                adminPassword,
                userPassword);
            var ffmpeg = PrepareFakeMediaTools(repositoryRoot, artifacts.CaseDirectory);

            await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
            {
                await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                _ = await host.GetStringAsync("Startup/User", cancellationToken);
                await host.PostJsonAsync(
                    "Startup/User",
                    JsonSerializer.Serialize(new { Name = "parity-admin", Password = adminPassword }),
                    authorization: null,
                    cancellationToken);
                await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);

                var deviceId = "stock-parity-" + Guid.NewGuid().ToString("N");
                var authentication = await host.PostJsonAsync(
                    "Users/AuthenticateByName",
                    JsonSerializer.Serialize(new { Username = "parity-admin", Pw = adminPassword }),
                    $"MediaBrowser Client=\"npgsql-parity\", Device=\"stock tests\", DeviceId=\"{deviceId}\", Version=\"0.1\"",
                    cancellationToken);
                using var authenticationDocument = JsonDocument.Parse(authentication);
                var token = authenticationDocument.RootElement.GetProperty("AccessToken").GetString();
                var adminId = Guid.Parse(authenticationDocument.RootElement.GetProperty("User").GetProperty("Id").GetString()!);
                Assert.False(string.IsNullOrWhiteSpace(token));
                redactor.Add(token);
                evidenceRedactor.Add(token);
                var authorization = "MediaBrowser Token=\"" + token + "\"";

                var createdUserJson = await host.PostJsonAsync(
                    "Users/New",
                    JsonSerializer.Serialize(new { Name = "MíXeD-用户", Password = userPassword }),
                    authorization,
                    cancellationToken);
                using var createdUserDocument = JsonDocument.Parse(createdUserJson);
                var createdUserId = Guid.Parse(createdUserDocument.RootElement.GetProperty("Id").GetString()!);
                await host.PostJsonAsync(
                    "Users/Configuration?userId=" + createdUserId,
                    JsonSerializer.Serialize(new
                    {
                        AudioLanguagePreference = "jpn",
                        OrderedViews = new[] { Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") },
                        RememberAudioSelections = true,
                        RememberSubtitleSelections = true
                    }),
                    authorization,
                    cancellationToken);

                var addLibraryPath = "Library/VirtualFolders?name="
                    + Uri.EscapeDataString("Parity Movies 雪")
                    + "&collectionType=movies&paths="
                    + Uri.EscapeDataString(layout.MediaDirectory)
                    + "&refreshLibrary=true";
                await host.PostJsonAsync(
                    addLibraryPath,
                    JsonSerializer.Serialize(new
                    {
                        LibraryOptions = new
                        {
                            EnableRealtimeMonitor = false,
                            SaveLocalMetadata = false,
                            EnableInternetProviders = false
                        }
                    }),
                    authorization,
                    cancellationToken);
                await host.PostJsonAsync("Library/Refresh", "{}", authorization, cancellationToken);

                var items = await WaitForMoviesAsync(host, authorization, cancellationToken);
                Assert.Equal(3, items.GetProperty("TotalRecordCount").GetInt32());
                var orderedNames = items.GetProperty("Items")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("Name").GetString())
                    .ToArray();
                Assert.Equal(
                    new[] { "Alpha Movie", "mixed Case Movie", "Été 用户 100%_done" }.Order(StringComparer.Ordinal),
                    orderedNames.Order(StringComparer.Ordinal));

                var unicodeItem = items.GetProperty("Items")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("Name").GetString() == "Été 用户 100%_done");
                var unicodeItemId = Guid.Parse(unicodeItem.GetProperty("Id").GetString()!);
                Assert.Contains(
                    unicodeItem.GetProperty("Genres").EnumerateArray().Select(value => value.GetString()),
                    value => value == "Sci-Fi 世界");
                Assert.Contains(
                    unicodeItem.GetProperty("People").EnumerateArray(),
                    person => person.GetProperty("Name").GetString() == "Zoë 李"
                        && person.GetProperty("Role").GetString() == "Lead_%_Actor");

                using var searchDocument = JsonDocument.Parse(await host.GetStringAsync(
                    "Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm="
                        + Uri.EscapeDataString("Été 用户 100%_done")
                        + "&EnableTotalRecordCount=true",
                    authorization,
                    cancellationToken));
                Assert.Equal(1, searchDocument.RootElement.GetProperty("TotalRecordCount").GetInt32());

                using var pageDocument = JsonDocument.Parse(await host.GetStringAsync(
                    "Items?Recursive=true&IncludeItemTypes=Movie&SortBy=SortName&SortOrder=Ascending&StartIndex=1&Limit=1&EnableTotalRecordCount=true",
                    authorization,
                    cancellationToken));
                Assert.Equal(3, pageDocument.RootElement.GetProperty("TotalRecordCount").GetInt32());
                Assert.Equal(
                    orderedNames[1],
                    pageDocument.RootElement.GetProperty("Items")[0].GetProperty("Name").GetString());

                await host.PostJsonAsync(
                    $"Users/{adminId}/FavoriteItems/{unicodeItemId}",
                    "{}",
                    authorization,
                    cancellationToken);
                await host.PostJsonAsync(
                    "Auth/Keys?app=" + Uri.EscapeDataString("parity-key-雪"),
                    "{}",
                    authorization,
                    cancellationToken);
                using var apiKeyDocument = JsonDocument.Parse(await host.GetStringAsync(
                    "Auth/Keys",
                    authorization,
                    cancellationToken));
                var apiKey = apiKeyDocument.RootElement.GetProperty("Items")
                    .EnumerateArray()
                    .Single(key => key.GetProperty("AppName").GetString() == "parity-key-雪")
                    .GetProperty("AccessToken")
                    .GetString();
                Assert.False(string.IsNullOrWhiteSpace(apiKey));
                redactor.Add(apiKey);
                evidenceRedactor.Add(apiKey);
                await host.DeleteAsync(
                    "Auth/Keys/" + Uri.EscapeDataString(apiKey!),
                    authorization,
                    cancellationToken);

                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
            }

            await publish.VerifyUnchangedAsync(cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(2, await context.Users.CountAsync(cancellationToken));
            var storedUser = await context.Users.SingleAsync(user => user.Username == "MíXeD-用户", cancellationToken);
            Assert.Equal("MÍXED-用户", storedUser.NormalizedUsername);
            Assert.Equal("jpn", storedUser.AudioLanguagePreference);
            var storedItem = await context.BaseItems
                .Include(item => item.Peoples!)
                .ThenInclude(map => map.People)
                .Include(item => item.ItemValues!)
                .ThenInclude(map => map.ItemValue)
                .Include(item => item.UserData!)
                .SingleAsync(item => item.Name == "Été 用户 100%_done", cancellationToken);
            Assert.Contains(storedItem.Peoples!, map => map.People.Name == "Zoë 李" && map.Role == "Lead_%_Actor");
            Assert.Contains(storedItem.ItemValues!, map => map.ItemValue.Value == "Sci-Fi 世界");
            Assert.Contains(storedItem.UserData!, data => data.IsFavorite);
            Assert.Contains(await context.Devices.Select(device => device.AppName).ToArrayAsync(cancellationToken), app => app == "npgsql-parity");
            Assert.DoesNotContain(await context.ApiKeys.Select(key => key.Name).ToArrayAsync(cancellationToken), name => name == "parity-key-雪");
        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, evidenceRedactor);
            throw new InvalidOperationException(
                $"The stock-server parity proof failed. Bounded sanitized diagnostics are in '{artifacts.RetainedEvidenceDirectory}' "
                + $"({artifacts.RetainedEvidenceBytes} bytes); its copied host and case scratch directory were removed.",
                exception);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task<JsonElement> WaitForMoviesAsync(
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
                    "Items?Recursive=true&IncludeItemTypes=Movie&Fields=People,Genres,MediaStreams,Chapters&SortBy=SortName&SortOrder=Ascending&Limit=10&EnableTotalRecordCount=true",
                    authorization,
                    cancellationToken));
                var root = document.RootElement;
                var items = root.GetProperty("Items").EnumerateArray().ToArray();
                var names = items
                    .Select(item => item.GetProperty("Name").GetString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var expectedNames = new[] { "Alpha Movie", "mixed Case Movie", "Été 用户 100%_done" }
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var unicode = items.SingleOrDefault(
                    item => item.GetProperty("Name").GetString() == "Été 用户 100%_done");
                if (root.GetProperty("TotalRecordCount").GetInt32() == 3
                    && names.SequenceEqual(expectedNames, StringComparer.Ordinal)
                    && unicode.ValueKind != JsonValueKind.Undefined
                    && unicode.GetProperty("Genres").GetArrayLength() > 0
                    && unicode.GetProperty("People").GetArrayLength() > 0)
                {
                    return root.Clone();
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException("The media scan did not expose exactly three movies within two minutes.", lastError);
    }

    private static void WriteMediaFixture(string mediaDirectory)
    {
        WriteMovie(
            mediaDirectory,
            "01-alpha",
            "Alpha Movie",
            "Alpha Movie",
            "Drama",
            "Alice Actor",
            "Lead");
        WriteMovie(
            mediaDirectory,
            "02-mixed",
            "mixed Case Movie",
            "mixed Case Movie",
            "Comedy",
            "Mixed Person",
            "Support");
        WriteMovie(
            mediaDirectory,
            "03-unicode",
            "Été 用户 100%_done",
            "Été 用户 100%_done",
            "Sci-Fi 世界",
            "Zoë 李",
            "Lead_%_Actor");
    }

    private static void WriteMovie(
        string mediaDirectory,
        string fileName,
        string title,
        string sortTitle,
        string genre,
        string person,
        string role)
    {
        var streamPath = Path.Combine(mediaDirectory, fileName + ".strm");
        var nfoPath = Path.Combine(mediaDirectory, fileName + ".nfo");
        File.WriteAllText(streamPath, "https://example.invalid/" + fileName + ".mp4\n", new UTF8Encoding(false));
        var nfo = "<movie><title>" + XmlEscape(title) + "</title><sorttitle>" + XmlEscape(sortTitle)
            + "</sorttitle><genre>" + XmlEscape(genre) + "</genre><actor><name>" + XmlEscape(person)
            + "</name><role>" + XmlEscape(role) + "</role><type>Actor</type></actor></movie>";
        File.WriteAllText(nfoPath, nfo, new UTF8Encoding(false));
    }

    private static string XmlEscape(string value)
        => System.Security.SecurityElement.Escape(value)!;

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

    private static JellyfinDbContext CreateContext(string connectionString)
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(options, connectionString, null);
        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
