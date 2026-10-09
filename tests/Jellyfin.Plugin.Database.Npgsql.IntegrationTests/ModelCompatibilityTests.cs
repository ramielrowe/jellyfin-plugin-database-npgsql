using System.Data;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Jellyfin.Plugin.Database.Npgsql.TestProvider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class ModelCompatibilityTests
{
    [Fact]
    public async Task FinalModel_CreatesAndHandlesLongTextNormalDetachedRestoreUpdateAndDelete()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var suffix = string.Concat(Enumerable.Repeat("雪\\x41_%😀", 1500));
            var providerId = "provider-" + suffix;
            var providerValue = "value-" + suffix;
            var role = "role-" + suffix;
            var customDataKey = "custom-" + suffix;
            var itemValueText = "genre-" + suffix;
            var preferenceKey = "preference-" + suffix;
            var itemId = Guid.NewGuid();
            var personId = Guid.NewGuid();
            Guid userId;
            int preferenceId;

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var item = CreateBaseItem(itemId, suffix);
                var person = new People { Id = personId, Name = "Mixed Case Person", PersonType = "Actor" };
                var user = new User("model-user", "test-auth", "test-reset");
                userId = user.Id;
                context.AddRange(item, person, user);
                context.BaseItemProviders.Add(new BaseItemProvider
                {
                    Item = item,
                    ItemId = itemId,
                    ProviderId = providerId,
                    ProviderValue = providerValue
                });
                context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
                {
                    Item = item,
                    ItemId = itemId,
                    People = person,
                    PeopleId = personId,
                    Role = role
                });
                context.UserData.Add(new UserData
                {
                    Item = item,
                    ItemId = itemId,
                    User = user,
                    UserId = user.Id,
                    CustomDataKey = customDataKey
                });
                context.ItemValues.Add(CreateItemValue(itemValueText));
                var preference = new CustomItemDisplayPreferences(
                    user.Id,
                    itemId,
                    "test-client",
                    preferenceKey,
                    "first");
                context.CustomItemDisplayPreferences.Add(preference);

                await context.SaveChangesAsync(cancellationToken);
                preferenceId = preference.Id;
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(providerId, await context.BaseItemProviders.Select(value => value.ProviderId).SingleAsync(cancellationToken));
                Assert.Equal(role, await context.PeopleBaseItemMap.Select(value => value.Role).SingleAsync(cancellationToken));
                Assert.Equal(customDataKey, await context.UserData.Select(value => value.CustomDataKey).SingleAsync(cancellationToken));
                Assert.Equal(itemValueText, await context.ItemValues.Select(value => value.Value).SingleAsync(cancellationToken));
                Assert.Equal(preferenceKey, await context.CustomItemDisplayPreferences.Select(value => value.Key).SingleAsync(cancellationToken));
                var item = await context.BaseItems.SingleAsync(value => value.Id == itemId, cancellationToken);
                Assert.Equal(DateTimeKind.Utc, item.DateCreated!.Value.Kind);
            }

            var detachedProvider = new BaseItemProvider
            {
                ItemId = itemId,
                ProviderId = providerId,
                ProviderValue = "detached-update",
                Item = null!
            };
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var entry = context.Update(detachedProvider);
                Assert.Equal(EntityState.Modified, entry.State);
                await context.SaveChangesAsync(cancellationToken);
            }

            var updatedPreferenceKey = "updated-" + suffix;
            CustomItemDisplayPreferences detachedPreference;
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                detachedPreference = await context.CustomItemDisplayPreferences
                    .AsNoTracking()
                    .SingleAsync(value => value.Id == preferenceId, cancellationToken);
            }

            detachedPreference.Key = updatedPreferenceKey;
            detachedPreference.Value = "detached";
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(EntityState.Modified, context.Update(detachedPreference).State);
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                const string restoreProvider = "restore-雪-\\-%_";
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"BaseItemProviders\" (\"ItemId\", \"ProviderId\", \"ProviderValue\") VALUES ({itemId}, {restoreProvider}, {"restore-value"})",
                    cancellationToken);
                Assert.Equal(
                    restoreProvider,
                    await context.BaseItemProviders
                        .Where(provider => provider.ProviderId == restoreProvider)
                        .Select(provider => provider.ProviderId)
                        .SingleAsync(cancellationToken));

                context.Remove(await context.BaseItemProviders.SingleAsync(
                    provider => provider.ProviderId == providerId,
                    cancellationToken));
                await context.SaveChangesAsync(cancellationToken);
                Assert.False(await context.BaseItemProviders.AnyAsync(
                    provider => provider.ProviderId == providerId,
                    cancellationToken));
            }
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ChunkInterning_ForcedSharedChunkCollisionAllowsDistinctValuesRejectsDuplicatesAndSerializesRaces()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var common = new string('x', NpgsqlTextInterning.ChunkSize * 8);
            var firstValue = common + "-first";
            var secondValue = common + "-second";

            // Both values are forced through the same eight bounded identity nodes.
            // Unlike a digest key, a collision in this accelerator/prefix path does
            // not merge the distinct terminal identities.
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                context.ItemValues.AddRange(CreateItemValue(firstValue), CreateItemValue(secondValue));
                await context.SaveChangesAsync(cancellationToken);
                var internIds = await context.ItemValues
                    .Where(value => value.Value == firstValue || value.Value == secondValue)
                    .Select(value => EF.Property<long>(value, "ValueInternId"))
                    .ToArrayAsync(cancellationToken);
                Assert.Equal(2, internIds.Distinct().Count());
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                context.ItemValues.Add(CreateItemValue(firstValue));
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => context.SaveChangesAsync(cancellationToken));
            }

            var raceValue = common + "-race";
            await using var first = CreateContext(database.RuntimeConnectionString);
            await using var second = CreateContext(database.RuntimeConnectionString);
            first.ItemValues.Add(CreateItemValue(raceValue));
            second.ItemValues.Add(CreateItemValue(raceValue));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSave = SaveAfterSignalAsync(first, start.Task, cancellationToken);
            var secondSave = SaveAfterSignalAsync(second, start.Task, cancellationToken);
            start.SetResult();
            var outcomes = await Task.WhenAll(firstSave, secondSave);
            Assert.Equal(1, outcomes.Count(outcome => outcome));
            Assert.Equal(1, outcomes.Count(outcome => !outcome));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task QueryInterceptor_ProvidesSqliteCompatibleActualExpressionSemantics()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            context.ActivityLogs.AddRange(
                new ActivityLog("Library SCAN 100%_done", "Test", Guid.Empty),
                new ActivityLog("Path \\Root", "Test", Guid.Empty),
                new ActivityLog("CAFÉ", "Test", Guid.Empty));
            context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = "Mixed Case Person", PersonType = "Actor" });
            context.BaseItems.AddRange(
                new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "Null", SortName = null },
                new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "Value", SortName = "Alpha" });
            await context.SaveChangesAsync(cancellationToken);

            Assert.Single(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%scan 100\\%\\_done"))
                .ToArrayAsync(cancellationToken));
            Assert.Single(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%\\\\Root"))
                .ToArrayAsync(cancellationToken));
            Assert.Empty(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%café%"))
                .ToArrayAsync(cancellationToken));
            Assert.Single(await context.Peoples
                .Where(person => person.Name.StartsWith("MIXED"))
                .ToArrayAsync(cancellationToken));

            var ascending = await context.BaseItems
                .OrderBy(item => item.SortName)
                .Select(item => item.SortName)
                .ToArrayAsync(cancellationToken);
            Assert.Null(ascending[0]);
            var descending = await context.BaseItems
                .OrderByDescending(item => item.SortName)
                .Select(item => item.SortName)
                .ToArrayAsync(cancellationToken);
            Assert.Null(descending[^1]);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ReplacementIndexes_AreUsableAndNoPrivilegedExtensionIsInstalled()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            var probes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IX_BaseItems_Path"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"Path\" = 'not-present'",
                ["IX_BaseItems_Name"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"Name\" = 'not-present'",
                ["IX_BaseItems_CleanName"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"CleanName\" = 'not-present'",
                ["IX_BaseItems_PresentationUniqueKey"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"PresentationUniqueKey\" = 'not-present'",
                ["IX_BaseItems_SeriesPresentationUniqueKey"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"SeriesPresentationUniqueKey\" = 'not-present'",
                ["IX_BaseItems_SeriesName"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"SeriesName\" = 'not-present'",
                ["IX_BaseItems_SortName"] = "SELECT \"Id\" FROM \"BaseItems\" WHERE \"SortName\" = 'not-present'",
                ["IX_BaseItems_Npgsql_Type"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"Type\" LIMIT 1",
                ["IX_BaseItems_Npgsql_Type_TopParentId"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"Type\", \"TopParentId\" LIMIT 1",
                ["IX_BaseItems_Npgsql_Type_IsFolder_IsVirtualItem"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"Type\", \"IsFolder\", \"IsVirtualItem\" LIMIT 1",
                ["IX_BaseItems_Npgsql_IsFolder_TopParentId_IsVirtualItem"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"IsFolder\", \"TopParentId\", \"IsVirtualItem\" LIMIT 1",
                ["IX_BaseItems_Npgsql_MediaType_TopParentId_IsVirtualItem"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"MediaType\", \"TopParentId\", \"IsVirtualItem\" LIMIT 1",
                ["IX_BaseItems_Npgsql_Type_ParentIndexNumber_IndexNumber"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"Type\", \"ParentIndexNumber\", \"IndexNumber\" LIMIT 1",
                ["IX_BaseItems_Npgsql_Type_TopParentId_IsVirtualItem_DateCreated"] = "SELECT \"Id\" FROM \"BaseItems\" ORDER BY \"Type\", \"TopParentId\", \"IsVirtualItem\", \"DateCreated\" LIMIT 1"
            };

            foreach (var (expectedIndexFragment, sql) in probes)
            {
                var plan = await ExplainWithOnlyIndexAsync(
                    context,
                    expectedIndexFragment,
                    sql,
                    cancellationToken);
                Assert.Contains("Index", plan, StringComparison.Ordinal);
                Assert.Contains(expectedIndexFragment, plan, StringComparison.Ordinal);
            }

            var extensionCount = await ScalarAsync<long>(
                context,
                "SELECT count(*) FROM pg_catalog.pg_extension WHERE extname IN ('pgcrypto', 'citext')",
                cancellationToken);
            Assert.Equal(0, extensionCount);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UntouchedJellyfin_StartsAndCreatesApplicationDataAgainstPrecreatedCurrentModel()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        var hostConfigured = !string.IsNullOrWhiteSpace(publishDirectory)
            || !string.IsNullOrWhiteSpace(publishArchive);
        Assert.SkipUnless(
            databaseOptions.IsConfigured && hostConfigured,
            "Configure the real PostgreSQL fixture and an integrity-pinned pristine Jellyfin publish to run the full-host model proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var artifactRoot = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-model-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        var succeeded = false;
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifactRoot, "host"),
                cancellationToken);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            var vanillaHistory = await CollectVanillaMigrationHistoryAsync(
                publish,
                artifactRoot,
                ffmpeg,
                cancellationToken);

            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var migrationId = Assert.Single(context.Database.GetMigrations());
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" character varying(32) NOT NULL)",
                    cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {"10.0.3"})",
                    cancellationToken);
                foreach (var (vanillaMigrationId, productVersion) in vanillaHistory)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({vanillaMigrationId}, {productVersion}) ON CONFLICT (\"MigrationId\") DO NOTHING",
                        cancellationToken);
                }
            }

            var layout = JellyfinHostLayout.Create(artifactRoot, publish, "model-compatibility");
            var package = Path.Combine(
                repositoryRoot,
                "artifacts",
                "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
            Assert.True(File.Exists(package), "Build the audited plugin ZIP before running the full-host model proof.");
            var pluginDirectory = layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            File.Copy(
                typeof(TestOnlyNpgsqlDatabaseProvider).Assembly.Location,
                Path.Combine(pluginDirectory, TestOnlyNpgsqlDatabaseProvider.AssemblyFileName));
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                TestOnlyNpgsqlDatabaseProvider.AssemblyFileName);
            var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var password = "model-host-" + Guid.NewGuid().ToString("N");
            var redactor = new SensitiveDataRedactor(password, database.RuntimeConnectionString);
            await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
            {
                await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                Assert.Equal("Healthy", (await host.GetStringAsync("health", cancellationToken)).Trim());
                _ = await host.GetStringAsync("Startup/User", cancellationToken);
                await host.PostJsonAsync(
                    "Startup/User",
                    JsonSerializer.Serialize(new { Name = "model-admin", Password = password }),
                    authorization: null,
                    cancellationToken);
                await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
                var authentication = await host.PostJsonAsync(
                    "Users/AuthenticateByName",
                    JsonSerializer.Serialize(new { Username = "model-admin", Pw = password }),
                    $"MediaBrowser Client=\"npgsql-model\", Device=\"tests\", DeviceId=\"{Guid.NewGuid():N}\", Version=\"0.1\"",
                    cancellationToken);
                using var document = JsonDocument.Parse(authentication);
                var token = document.RootElement.GetProperty("AccessToken").GetString();
                Assert.False(string.IsNullOrWhiteSpace(token));
                redactor.Add(token);
                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
            }

            await publish.VerifyUnchangedAsync(cancellationToken);
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.True(await context.Users.AnyAsync(user => user.Username == "model-admin", cancellationToken));
            }

            succeeded = true;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The pristine-host model proof failed; isolated diagnostics remain under '{artifactRoot}'.",
                exception);
        }
        finally
        {
            if (succeeded && Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }

            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task<IReadOnlyList<(string MigrationId, string ProductVersion)>> CollectVanillaMigrationHistoryAsync(
        PristineJellyfinPublish publish,
        string artifactRoot,
        string ffmpeg,
        CancellationToken cancellationToken)
    {
        var layout = JellyfinHostLayout.Create(artifactRoot, publish, "sqlite-history-seed");
        var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
        using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
        using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
        layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
        var password = "history-seed-" + Guid.NewGuid().ToString("N");
        var redactor = new SensitiveDataRedactor(password);
        var rows = new List<(string MigrationId, string ProductVersion)>();
        await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
        {
            await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
            _ = await host.GetStringAsync("Startup/User", cancellationToken);
            await host.PostJsonAsync(
                "Startup/User",
                JsonSerializer.Serialize(new { Name = "history-seed", Password = password }),
                authorization: null,
                cancellationToken);
            await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
            var authentication = await host.PostJsonAsync(
                "Users/AuthenticateByName",
                JsonSerializer.Serialize(new { Username = "history-seed", Pw = password }),
                $"MediaBrowser Client=\"npgsql-history-seed\", Device=\"tests\", DeviceId=\"{Guid.NewGuid():N}\", Version=\"0.1\"",
                cancellationToken);
            using var document = JsonDocument.Parse(authentication);
            var token = document.RootElement.GetProperty("AccessToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            redactor.Add(token);

            var databasePath = Path.Combine(layout.DataDirectory, "data", "jellyfin.db");
            Assert.True(File.Exists(databasePath));
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static BaseItemEntity CreateBaseItem(Guid id, string suffix)
        => new()
        {
            Id = id,
            Type = "Movie",
            MediaType = "Video",
            Name = "Unicode model test",
            Path = "/media/" + suffix,
            SortName = suffix,
            CleanName = suffix,
            PresentationUniqueKey = suffix,
            DateCreated = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Unspecified)
        };

    private static ItemValue CreateItemValue(string value)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = value,
            CleanValue = value
        };

    private static async Task<bool> SaveAfterSignalAsync(
        JellyfinDbContext context,
        Task signal,
        CancellationToken cancellationToken)
    {
        await signal;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static async Task CreateSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
        await context.Database.ExecuteSqlRawAsync(NpgsqlTextInterning.InstallSql, cancellationToken);
        Assert.Equal(
            NpgsqlModelCustomizer.ModelContract,
            context.GetService<IDesignTimeModel>().Model[NpgsqlModelCustomizer.ModelContractAnnotation]);
    }

    private static JellyfinDbContext CreateContext(string connectionString)
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(builder, connectionString, null);
        return new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task<string> ExplainWithOnlyIndexAsync(
        JellyfinDbContext context,
        string retainedIndex,
        string sql,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.GetDbConnection()
            .BeginTransactionAsync(cancellationToken);
        var indexes = new List<string>();
        await using (var catalog = context.Database.GetDbConnection().CreateCommand())
        {
            catalog.Transaction = transaction;
            catalog.CommandText = "SELECT indexname FROM pg_catalog.pg_indexes WHERE schemaname = 'public' AND tablename = 'BaseItems' AND indexname <> @retained AND indexname <> 'PK_BaseItems'";
            var parameter = catalog.CreateParameter();
            parameter.ParameterName = "retained";
            parameter.Value = retainedIndex;
            catalog.Parameters.Add(parameter);
            await using var reader = await catalog.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add(reader.GetString(0));
            }
        }

        foreach (var index in indexes)
        {
            await using var drop = context.Database.GetDbConnection().CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP INDEX public.\"" + index.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var lines = new List<string>();
        await using (var disableScan = context.Database.GetDbConnection().CreateCommand())
        {
            disableScan.Transaction = transaction;
            disableScan.CommandText = "SET LOCAL enable_seqscan = off";
            await disableScan.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "EXPLAIN (COSTS OFF) " + sql;
            await using var planReader = await command.ExecuteReaderAsync(cancellationToken);
            while (await planReader.ReadAsync(cancellationToken))
            {
                lines.Add(planReader.GetString(0));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<T> ScalarAsync<T>(
        JellyfinDbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlModelCollection
{
    public const string Name = "PostgreSQL model compatibility";
}
