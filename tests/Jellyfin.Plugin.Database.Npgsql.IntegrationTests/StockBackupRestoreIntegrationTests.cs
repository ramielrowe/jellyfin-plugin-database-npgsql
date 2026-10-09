using System.IO.Compression;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class StockBackupRestoreIntegrationTests
{
    [Fact]
    public async Task UntouchedHost_PublicBackupApiRoundTripsBoundPostgreSqlArchive()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        Assert.SkipUnless(
            databaseOptions.IsConfigured
                && (!string.IsNullOrWhiteSpace(publishDirectory) || !string.IsNullOrWhiteSpace(publishArchive)),
            "Configure PostgreSQL and the integrity-pinned pristine Jellyfin publish to run stock backup/restore.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("stock-backup-restore");
        var databasePassword = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password;
        var redactor = new SensitiveDataRedactor(database.RuntimeConnectionString, databasePassword);
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifacts.CaseDirectory, "host"),
                cancellationToken);
            var layout = JellyfinHostLayout.Create(artifacts.CaseDirectory, publish, "stock-backup-restore");
            layout.InstallPluginZip(
                Path.Combine(repositoryRoot, "artifacts", "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip"),
                PluginMetadata.InstallationDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            var portLeaseRoot = Path.Combine(artifacts.ArtifactRoot, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var password = "restore-test-" + Guid.NewGuid().ToString("N");
            redactor.Add(password);

            await using var host = JellyfinHostProcess.StartForRestartableLifecycle(layout, httpPort, redactor, ffmpeg);
            await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
            await WaitForStartupApiAsync(host, cancellationToken);
            host.ExpectScheduledRestart();
            _ = await host.PostJsonAsync(
                "Startup/User",
                JsonSerializer.Serialize(new { Name = "restore-admin", Password = password }),
                authorization: null,
                cancellationToken);
            _ = await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
            var authentication = await host.PostJsonAsync(
                "Users/AuthenticateByName",
                JsonSerializer.Serialize(new { Username = "restore-admin", Pw = password }),
                "MediaBrowser Client=\"npgsql-restore\", Device=\"tests\", DeviceId=\"restore\", Version=\"0.1\"",
                cancellationToken);
            using var authenticationDocument = JsonDocument.Parse(authentication);
            var token = authenticationDocument.RootElement.GetProperty("AccessToken").GetString()!;
            redactor.Add(token);
            var authorization = "MediaBrowser Token=\"" + token + "\"";

            var longKey = string.Concat(Enumerable.Repeat("long-key-雪-", 96));
            string[] expectedHistory;
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                var activity = new ActivityLog("stock backup sentinel", "BackupRestore", Guid.Empty);
                seed.ActivityLogs.Add(activity);
                seed.Entry(activity).Property(nameof(ActivityLog.Id)).CurrentValue = 777;
                var user = new User("related-user", "auth", "reset");
                user.Permissions.Add(new Permission(
                    PermissionKind.EnableMediaPlayback,
                    false));
                user.Preferences.Add(new Preference(
                    PreferenceKind.AllowedTags,
                    "one,two"));
                seed.Users.Add(user);
                seed.CustomItemDisplayPreferences.Add(
                    new CustomItemDisplayPreferences(user.Id, Guid.NewGuid(), "restore", longKey, "preserved"));
                var displayPreferences = new DisplayPreferences(user.Id, Guid.NewGuid(), "restore");
                var homeSection = new HomeSection
                {
                    Order = 7,
                    Type = HomeSectionType.SmallLibraryTiles,
                };
                displayPreferences.HomeSections.Add(homeSection);
                seed.DisplayPreferences.Add(displayPreferences);
                seed.Entry(displayPreferences).Property(nameof(DisplayPreferences.Id)).CurrentValue = 778;
                seed.Entry(homeSection).Property(nameof(HomeSection.Id)).CurrentValue = 779;
                _ = SeedEveryIdentityWithExplicitIds(seed, 900);
                await seed.SaveChangesAsync(cancellationToken);
                expectedHistory = (await seed.Database.GetAppliedMigrationsAsync(cancellationToken))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }

            var backupJson = await CreateBackupWhenIdleAsync(host, authorization, cancellationToken);
            using var backupDocument = JsonDocument.Parse(backupJson);
            var backupPath = backupDocument.RootElement.GetProperty("Path").GetString()!;
            using (var archive = await ZipFile.OpenReadAsync(backupPath, cancellationToken))
            {
                Assert.NotNull(archive.GetEntry(NpgsqlBackupArchive.MarkerEntryName));
            }

            await using (var mutate = CreateContext(database.RuntimeConnectionString))
            {
                await mutate.ActivityLogs.ExecuteDeleteAsync(cancellationToken);
                await mutate.CustomItemDisplayPreferences.ExecuteDeleteAsync(cancellationToken);
            }

            _ = await host.PostJsonAsync(
                "Backup/Restore",
                JsonSerializer.Serialize(new { ArchiveFileName = Path.GetFileName(backupPath) }),
                authorization,
                cancellationToken);
            await WaitForRestoreHandoffAsync(
                host,
                database.RuntimeConnectionString,
                context => context.ActivityLogs.AnyAsync(row => row.Name == "stock backup sentinel", cancellationToken),
                cancellationToken);

            using var restartedHttpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var restartedHttpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(restartedHttpPort.Port, restartedHttpsPort.Port);
            await using var restarted = JellyfinHostProcess.StartForRestartableLifecycle(
                layout,
                restartedHttpPort,
                redactor,
                ffmpeg);
            await restarted.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);

            await using (var verify = CreateContext(database.RuntimeConnectionString))
            {
                var restored = await verify.ActivityLogs.SingleAsync(
                    row => row.Name == "stock backup sentinel",
                    cancellationToken);
                Assert.Equal(777, restored.Id);
                Assert.Equal("preserved", await verify.CustomItemDisplayPreferences
                    .Where(row => row.Key == longKey)
                    .Select(row => row.Value)
                    .SingleAsync(cancellationToken));
                var user = await verify.Users
                    .Include(row => row.Permissions)
                    .Include(row => row.Preferences)
                    .SingleAsync(row => row.Username == "related-user", cancellationToken);
                Assert.Single(user.Permissions);
                Assert.Single(user.Preferences);
                var restoredHomeSection = await verify.Set<HomeSection>().SingleAsync(
                    row => row.Order == 7,
                    cancellationToken);
                Assert.Equal(779, restoredHomeSection.Id);
                Assert.Equal(778, restoredHomeSection.DisplayPreferencesId);
                Assert.Equal(HomeSectionType.SmallLibraryTiles, restoredHomeSection.Type);
                Assert.Equal(
                    expectedHistory,
                    (await verify.Database.GetAppliedMigrationsAsync(cancellationToken)).Order(StringComparer.Ordinal));
                await AssertEveryIdentityRestoredAsync(verify, 900, cancellationToken);
                var generatedIds = InsertEveryIdentity(verify);
                await verify.SaveChangesAsync(cancellationToken);
                Assert.All(generatedIds.Select(getId => getId()), id => Assert.True(id > 900));
            }

            restarted.ExpectScheduledRestart();
            _ = await restarted.PostJsonAsync(
                "Backup/Restore",
                JsonSerializer.Serialize(new { ArchiveFileName = Path.GetFileName(backupPath) }),
                authorization,
                cancellationToken);
            await WaitForRestoreHandoffAsync(
                restarted,
                database.RuntimeConnectionString,
                async context => !await context.Users.AnyAsync(
                    row => row.Username == "identity-generated-owner",
                    cancellationToken),
                cancellationToken);

            using var repeatedHttpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var repeatedHttpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(repeatedHttpPort.Port, repeatedHttpsPort.Port);
            await using var repeated = JellyfinHostProcess.StartForRestartableLifecycle(
                layout,
                repeatedHttpPort,
                redactor,
                ffmpeg);
            await repeated.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
            await using (var repeatedVerify = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(777, await repeatedVerify.ActivityLogs
                    .Where(row => row.Name == "stock backup sentinel")
                    .Select(row => row.Id)
                    .SingleAsync(cancellationToken));
                Assert.DoesNotContain(
                    "identity-generated-owner",
                    await repeatedVerify.Users.Select(row => row.Username).ToArrayAsync(cancellationToken));
                Assert.Equal(779, await repeatedVerify.Set<HomeSection>()
                    .Where(row => row.Order == 7)
                    .Select(row => row.Id)
                    .SingleAsync(cancellationToken));
            }

            var unknownProviderPath = Path.Combine(Path.GetDirectoryName(backupPath)!, "unknown-provider.zip");
            File.Copy(backupPath, unknownProviderPath);
            using (var unknownProvider = await ZipFile.OpenAsync(
                unknownProviderPath,
                ZipArchiveMode.Update,
                cancellationToken))
            {
                unknownProvider.GetEntry(NpgsqlBackupArchive.MarkerEntryName)!.Delete();
                unknownProvider.GetEntry(NpgsqlBackupArchive.HomeSectionsEntryName)!.Delete();
            }

            var rejected = await Assert.ThrowsAsync<HttpRequestException>(
                () => repeated.PostJsonAsync(
                    "Backup/Restore",
                    JsonSerializer.Serialize(new { ArchiveFileName = Path.GetFileName(unknownProviderPath) }),
                    authorization,
                    cancellationToken));
            Assert.Contains("500", rejected.Message, StringComparison.Ordinal);
            Assert.False(repeated.HasExited);
            await using (var rejectionVerify = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(777, await rejectionVerify.ActivityLogs
                    .Where(row => row.Name == "stock backup sentinel")
                    .Select(row => row.Id)
                    .SingleAsync(cancellationToken));
            }

            await repeated.StopGracefullyAsync(token, TimeSpan.FromSeconds(30), cancellationToken);
            await publish.VerifyUnchangedAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, redactor);
            throw;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ProviderPurge_SaveAndReseedAreAtomicAndCoverEveryIdentity()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(context, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("before restore", "BackupRestore", Guid.Empty));
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                using (NpgsqlRestoreScope.Enter())
                {
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                }

                var restored = SeedEveryIdentityWithExplicitIds(context, 900);
                await context.SaveChangesAsync(cancellationToken);
                Assert.Equal(900, restored.Id);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(["restored explicit id"], await context.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
                var generatedIds = InsertEveryIdentity(context);
                await context.SaveChangesAsync(cancellationToken);
                Assert.All(generatedIds.Select(getId => getId()), id => Assert.True(id > 900));
                await AssertEveryIdentityAdvancesPastCurrentMaximumAsync(context, cancellationToken);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task FailedStockSaveRollsBackPurgeAndRestoredRows()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(context, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("must survive", "BackupRestore", Guid.Empty));
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                using (NpgsqlRestoreScope.Enter())
                {
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                }

                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO public.\"ActivityLogs\" (\"Id\", \"Name\", \"Type\", \"UserId\", \"DateCreated\", \"LogSeverity\", \"RowVersion\") VALUES (700, 'first', 'BackupRestore', '00000000-0000-0000-0000-000000000000', CURRENT_TIMESTAMP, 2, 1)",
                    cancellationToken);
                var duplicate = new ActivityLog("duplicate", "BackupRestore", Guid.Empty);
                context.ActivityLogs.Add(duplicate);
                context.Entry(duplicate).Property(nameof(ActivityLog.Id)).CurrentValue = 700;
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
            }

            await using (var verification = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(["must survive"], await verification.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UnscopedStockRestoreRepairsHistoryAndCannotPurgeApplicationTables()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(context, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("must remain", "BackupRestore", Guid.Empty));
                await context.SaveChangesAsync(cancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '2026%'",
                    cancellationToken);

                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                var exception = await Assert.ThrowsAsync<NotSupportedException>(
                    () => provider.PurgeDatabase(context, GetStockTableNames(context)));
                Assert.Contains("safety decorator is inactive", exception.Message, StringComparison.Ordinal);
            }

            await using (var verification = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal("must remain", await verification.ActivityLogs.Select(row => row.Name).SingleAsync(cancellationToken));
                var expectedHistory = NpgsqlHostMigrationAudit.AuditedMigrationIds
                    .Concat(verification.Database.GetMigrations())
                    .Order(StringComparer.Ordinal);
                Assert.Equal(expectedHistory, (await verification.Database.GetAppliedMigrationsAsync(cancellationToken)).Order(StringComparer.Ordinal));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BackupScopeUsesOneRepeatableReadSnapshotDuringConcurrentWrites()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var readerContext = CreateContext(database.RuntimeConnectionString);
            await readerContext.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            using (NpgsqlBackupScope.Enter())
            await using (var transaction = await readerContext.Database.BeginTransactionAsync(cancellationToken))
            {
                Assert.Equal(System.Data.IsolationLevel.RepeatableRead, transaction.GetDbTransaction().IsolationLevel);
                var before = await readerContext.ActivityLogs.CountAsync(cancellationToken);
                Assert.NotNull(NpgsqlBackupScope.HomeSectionsJson);

                await using (var writerContext = CreateContext(database.RuntimeConnectionString))
                {
                    writerContext.ActivityLogs.Add(new ActivityLog("concurrent", "BackupRestore", Guid.Empty));
                    await writerContext.SaveChangesAsync(cancellationToken);
                }

                Assert.Equal(before, await readerContext.ActivityLogs.CountAsync(cancellationToken));
            }

            await using var verification = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(1, await verification.ActivityLogs.CountAsync(cancellationToken));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CanceledStockSaveRollsBackThePurge()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(context, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("must survive cancellation", "BackupRestore", Guid.Empty));
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                using (NpgsqlRestoreScope.Enter())
                {
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                }

                context.ActivityLogs.Add(new ActivityLog("must roll back", "BackupRestore", Guid.Empty));
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(canceled.Token));
            }

            await using var verification = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(
                ["must survive cancellation"],
                await verification.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
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

    private static ActivityLog SeedEveryIdentityWithExplicitIds(JellyfinDbContext context, int id)
    {
        var user = new User("identity-restore-owner", "auth", "reset");
        context.Users.Add(user);
        var activity = AddWithId(context, new ActivityLog("restored explicit id", "BackupRestore", user.Id), id);
        _ = AddWithId(context, new AccessSchedule(DynamicDayOfWeek.Monday, 1, 2, user.Id), id);
        _ = AddWithId(context, new CustomItemDisplayPreferences(user.Id, Guid.NewGuid(), "restore", "identity-restored-key", "value"), id);
        var display = AddWithId(context, new DisplayPreferences(user.Id, Guid.NewGuid(), "restore"), id);
        var home = new HomeSection { Order = 1, Type = HomeSectionType.SmallLibraryTiles };
        display.HomeSections.Add(home);
        _ = AddWithId(context, home, id);
        _ = AddWithId(context, new ImageInfo("/identity-restored-image"), id);
        _ = AddWithId(context, new ItemDisplayPreferences(user.Id, Guid.NewGuid(), "restore"), id);
        _ = AddWithId(context, new Permission(PermissionKind.EnableMediaPlayback, true) { UserId = user.Id }, id);
        _ = AddWithId(context, new Preference(PreferenceKind.AllowedTags, "restored") { UserId = user.Id }, id);
        _ = AddWithId(context, new ApiKey("identity-restored-api"), id);
        _ = AddWithId(context, new Device(user.Id, "restore", "1", "restored", "identity-restored-device"), id);
        _ = AddWithId(context, new DeviceOptions("identity-restored-device"), id);
        return activity;
    }

    private static List<Func<int>> InsertEveryIdentity(JellyfinDbContext context)
    {
        var user = new User("identity-generated-owner", "auth", "reset");
        context.Users.Add(user);
        var activity = new ActivityLog("after restore", "BackupRestore", user.Id);
        var access = new AccessSchedule(DynamicDayOfWeek.Tuesday, 2, 3, user.Id);
        var custom = new CustomItemDisplayPreferences(user.Id, Guid.NewGuid(), "generated", "identity-generated-key", "value");
        var display = new DisplayPreferences(user.Id, Guid.NewGuid(), "generated");
        var home = new HomeSection { Order = 2, Type = HomeSectionType.SmallLibraryTiles };
        display.HomeSections.Add(home);
        var image = new ImageInfo("/identity-generated-image");
        var itemDisplay = new ItemDisplayPreferences(user.Id, Guid.NewGuid(), "generated");
        var permission = new Permission(PermissionKind.EnableMediaPlayback, true) { UserId = user.Id };
        var preference = new Preference(PreferenceKind.AllowedTags, "generated") { UserId = user.Id };
        var apiKey = new ApiKey("identity-generated-api");
        var device = new Device(user.Id, "generated", "1", "generated", "identity-generated-device");
        var deviceOptions = new DeviceOptions("identity-generated-device");
        context.AddRange(
            activity,
            access,
            custom,
            display,
            image,
            itemDisplay,
            permission,
            preference,
            apiKey,
            device,
            deviceOptions);
        return
        [
            () => activity.Id,
            () => access.Id,
            () => custom.Id,
            () => display.Id,
            () => home.Id,
            () => image.Id,
            () => itemDisplay.Id,
            () => permission.Id,
            () => preference.Id,
            () => apiKey.Id,
            () => device.Id,
            () => deviceOptions.Id,
        ];
    }

    private static T AddWithId<T>(JellyfinDbContext context, T entity, int id)
        where T : class
    {
        context.Add(entity);
        context.Entry(entity).Property("Id").CurrentValue = id;
        return entity;
    }

    private static async Task AssertEveryIdentityRestoredAsync(
        JellyfinDbContext context,
        int id,
        CancellationToken cancellationToken)
    {
        Assert.True(await context.AccessSchedules.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.ActivityLogs.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.CustomItemDisplayPreferences.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.DisplayPreferences.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.Set<HomeSection>().AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.ImageInfos.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.ItemDisplayPreferences.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.Permissions.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.Preferences.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.ApiKeys.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.Devices.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.True(await context.DeviceOptions.AnyAsync(row => row.Id == id, cancellationToken));
    }

    private static async Task WaitForStartupApiAsync(
        JellyfinHostProcess host,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            try
            {
                _ = await host.GetStringAsync("Startup/User", linked.Token);
                return;
            }
            catch (HttpRequestException) when (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), linked.Token);
            }
        }
    }

    private static async Task WaitForRestoreHandoffAsync(
        JellyfinHostProcess host,
        string connectionString,
        Func<JellyfinDbContext, Task<bool>> restoreCompleted,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!host.HasExited)
        {
            try
            {
                await using var context = CreateContext(connectionString);
                if (await restoreCompleted(context))
                {
                    break;
                }
            }
            catch (NpgsqlException) when (!linked.IsCancellationRequested)
            {
                // The stock host may be disposing its pool between the
                // scheduled shutdown and an in-process restart.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), linked.Token);
        }

        await host.TerminateAsync();
    }

    private static async Task<string> CreateBackupWhenIdleAsync(
        JellyfinHostProcess host,
        string authorization,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            try
            {
                return await host.PostJsonAsync(
                    "Backup/Create",
                    "{\"Database\":true,\"Metadata\":false,\"Subtitles\":false,\"Trickplay\":false}",
                    authorization,
                    linked.Token);
            }
            catch (HttpRequestException) when (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), linked.Token);
            }
        }
    }

    private static string[] GetStockTableNames(JellyfinDbContext context)
        => typeof(JellyfinDbContext).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
            .Select(property => context.Model.FindEntityType(property.PropertyType.GetGenericArguments()[0])!.GetSchemaQualifiedTableName()!)
            .ToArray();

    private static async Task SeedHostHistoryAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        foreach (var migrationId in NpgsqlHostMigrationAudit.AuditedMigrationIds)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {PluginMetadata.SupportedDatabaseAbi})",
                cancellationToken);
        }
    }

    private static async Task AssertEveryIdentityAdvancesPastCurrentMaximumAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var identities = new List<(string Schema, string Table, string Column, string Sequence)>();
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT n.nspname, c.relname, a.attname, s.relname "
                + "FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attidentity <> '' "
                + "JOIN pg_catalog.pg_depend d ON d.refobjid = c.oid AND d.refobjsubid = a.attnum AND d.deptype = 'i' "
                + "JOIN pg_catalog.pg_class s ON s.oid = d.objid AND s.relkind = 'S' "
                + "WHERE n.nspname = 'public' ORDER BY c.relname, a.attname";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                identities.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        Assert.Equal(13, identities.Count);
        foreach (var identity in identities)
        {
            var table = Quote(identity.Schema) + "." + Quote(identity.Table);
            var column = Quote(identity.Column);
            var sequence = Quote(identity.Schema) + "." + Quote(identity.Sequence);
            var maximum = await ScalarAsync<long?>(context, $"SELECT pg_catalog.max({column})::bigint FROM {table}", cancellationToken) ?? 0;
            var next = await ScalarAsync<long>(context, $"SELECT pg_catalog.nextval('{sequence}'::pg_catalog.regclass)", cancellationToken);
            Assert.True(next > maximum, $"Identity {table}.{column} generated {next} after maximum {maximum}.");
        }
    }

    private static async Task<T?> ScalarAsync<T>(
        JellyfinDbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? default : (T?)result;
    }

    private static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
