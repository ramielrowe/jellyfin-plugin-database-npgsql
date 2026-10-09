using System.IO.Compression;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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
                    await BeginCoordinatedRestoreAsync(context, cancellationToken);
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                    var restored = SeedEveryIdentityWithExplicitIds(context, 900);
                    await context.SaveChangesAsync(cancellationToken);
                    Assert.Equal(900, restored.Id);
                    await NpgsqlRestoreCoordinator.CommitCurrentAsync(cancellationToken);
                }
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
                    await BeginCoordinatedRestoreAsync(context, cancellationToken);
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO public.\"ActivityLogs\" (\"Id\", \"Name\", \"Type\", \"UserId\", \"DateCreated\", \"LogSeverity\", \"RowVersion\") VALUES (700, 'first', 'BackupRestore', '00000000-0000-0000-0000-000000000000', CURRENT_TIMESTAMP, 2, 1)",
                        cancellationToken);
                    var duplicate = new ActivityLog("duplicate", "BackupRestore", Guid.Empty);
                    context.ActivityLogs.Add(duplicate);
                    context.Entry(duplicate).Property(nameof(ActivityLog.Id)).CurrentValue = 700;
                    await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
                }
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
                Assert.True(File.Exists(NpgsqlBackupScope.HomeSectionsSnapshotPath));

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
                    await BeginCoordinatedRestoreAsync(context, cancellationToken);
                    await provider.PurgeDatabase(context, GetStockTableNames(context));
                    context.ActivityLogs.Add(new ActivityLog("must roll back", "BackupRestore", Guid.Empty));
                    using var canceled = new CancellationTokenSource();
                    canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(canceled.Token));
                }
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

    [Theory]
    [InlineData("extraction")]
    [InlineData("history")]
    [InlineData("reseed")]
    [InlineData("cancellation")]
    public async Task DecoratedRestoreCompensatesFilesHistoryAndDatabaseForEveryLateFailure(string failureStage)
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var scratch = Path.Combine(
            AppContext.BaseDirectory,
            "stock-restore-compensation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                await seed.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(seed, cancellationToken);
                seed.ActivityLogs.Add(new ActivityLog("must survive decorated restore failure", "BackupRestore", Guid.Empty));
                await seed.SaveChangesAsync(cancellationToken);
            }

            var factory = new TestDbContextFactory(database.RuntimeConnectionString);
            var paths = new TestApplicationPaths(Path.Combine(scratch, "paths"));
            paths.CreateDirectories();
            var protectedFile = Path.Combine(paths.ConfigurationDirectoryPath, "system.xml");
            await File.WriteAllTextAsync(protectedFile, "before", cancellationToken);
            UnixFileMode? protectedMode = null;
            if (!OperatingSystem.IsWindows())
            {
                protectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
                File.SetUnixFileMode(protectedFile, protectedMode.Value);
            }

            var archivePath = Path.Combine(scratch, "restore.zip");
            await using (var modelContext = CreateContext(database.RuntimeConnectionString))
            {
                await CreateExactEmptyStockArchiveAsync(archivePath, modelContext, cancellationToken);
            }

            var originalHistory = await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken);
            var services = new ServiceCollection()
                .AddSingleton<IDbContextFactory<JellyfinDbContext>>(factory)
                .AddSingleton<IServerApplicationPaths>(paths)
                .BuildServiceProvider();
            await using (services.ConfigureAwait(false))
            {
                var inner = new FailingRestoreService(
                    database.RuntimeConnectionString,
                    paths,
                    failureStage);
                var decorator = new NpgsqlBackupServiceDecorator(inner, true, services);
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => decorator.RestoreBackupAsync(archivePath));
                if (failure is AggregateException aggregate)
                {
                    throw aggregate.Flatten();
                }
            }

            Assert.Equal("before", await File.ReadAllTextAsync(protectedFile, cancellationToken));
            Assert.False(File.Exists(Path.Combine(paths.DataPath, "created", "partial.json")));
            if (!OperatingSystem.IsWindows() && protectedMode is { } mode)
            {
                Assert.Equal(mode, File.GetUnixFileMode(protectedFile));
            }

            Assert.Equal(originalHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
            await using var verification = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(
                ["must survive decorated restore failure"],
                await verification.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }

            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task FailureAfterSuccessfulStockSaveRollsBackRowsHistoryAndSequences()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var scratch = Path.Combine(Path.GetTempPath(), ".jellyfin-npgsql-post-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                await seed.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(seed, cancellationToken);
                seed.ActivityLogs.Add(new ActivityLog("post-save sentinel", "BackupRestore", Guid.Empty));
                await seed.SaveChangesAsync(cancellationToken);
            }

            var expectedHistory = await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken);
            var expectedSequences = await ReadAllSequenceStatesAsync(database.RuntimeConnectionString, cancellationToken);
            var archivePath = Path.Combine(scratch, "restore.zip");
            await using (var modelContext = CreateContext(database.RuntimeConnectionString))
            {
                await CreateExactEmptyStockArchiveAsync(archivePath, modelContext, cancellationToken);
            }

            var baseFactory = new TestDbContextFactory(database.RuntimeConnectionString);
            var restoreFactory = new NpgsqlRestoreDbContextFactory(
                baseFactory,
                NullLogger<JellyfinDbContext>.Instance,
                new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance),
                new Jellyfin.Database.Implementations.Locking.NoLockBehavior(
                    NullLogger<Jellyfin.Database.Implementations.Locking.NoLockBehavior>.Instance));
            var paths = new TestApplicationPaths(Path.Combine(scratch, "paths"));
            paths.CreateDirectories();
            var services = new ServiceCollection()
                .AddSingleton<IDbContextFactory<JellyfinDbContext>>(restoreFactory)
                .AddSingleton<IServerApplicationPaths>(paths)
                .BuildServiceProvider();
            await using (services.ConfigureAwait(false))
            {
                var inner = new PostSaveFailureRestoreService(restoreFactory);
                var decorator = new NpgsqlBackupServiceDecorator(inner, true, services);
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => decorator.RestoreBackupAsync(archivePath));
                Assert.Contains("after successful SaveChanges", failure.Message, StringComparison.Ordinal);
            }

            await using var verification = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(
                ["post-save sentinel"],
                await verification.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
            Assert.Equal(expectedHistory, await ReadHistoryAsync(database.RuntimeConnectionString, cancellationToken));
            Assert.Equal(expectedSequences, await ReadAllSequenceStatesAsync(database.RuntimeConnectionString, cancellationToken));
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }

            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreSupportsNegativeOnlyExplicitIdsAndRestartsAtLegalPositiveValues()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            await SeedHostHistoryAsync(context, cancellationToken);
            var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
            using (NpgsqlRestoreScope.Enter())
            {
                await BeginCoordinatedRestoreAsync(context, cancellationToken);
                await provider.PurgeDatabase(context, GetStockTableNames(context));
                _ = SeedEveryIdentityWithExplicitIds(context, -900);
                await context.SaveChangesAsync(cancellationToken);
                await NpgsqlRestoreCoordinator.CommitCurrentAsync(cancellationToken);
            }

            await using var verification = CreateContext(database.RuntimeConnectionString);
            var generatedIds = InsertEveryIdentity(verification);
            await verification.SaveChangesAsync(cancellationToken);
            Assert.All(generatedIds.Select(getId => getId()), id => Assert.True(id > 0));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    public static TheoryData<string, string> ReseedFailureMatrix => new()
    {
        { "failure", "first" },
        { "failure", "middle" },
        { "failure", "last" },
        { "cancellation", "first" },
        { "cancellation", "middle" },
        { "cancellation", "last" },
        { "connection", "first" },
        { "connection", "middle" },
        { "connection", "last" },
    };

    [Theory]
    [MemberData(nameof(ReseedFailureMatrix))]
    public async Task FailedReseedRollsBackTablesAndEveryExactSequenceState(string mode, string position)
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                await seed.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
                await SeedHostHistoryAsync(seed, cancellationToken);
                seed.ActivityLogs.Add(new ActivityLog("must survive reseed failure", "BackupRestore", Guid.Empty));
                await seed.SaveChangesAsync(cancellationToken);
                await seed.Database.ExecuteSqlRawAsync(
                    "SELECT pg_catalog.setval('public.\"AccessSchedules_Id_seq\"'::pg_catalog.regclass, 41, false)",
                    cancellationToken);
                await seed.Database.ExecuteSqlRawAsync(
                    "SELECT pg_catalog.setval('public.\"ActivityLogs_Id_seq\"'::pg_catalog.regclass, 37, true)",
                    cancellationToken);
            }

            var beforeSequences = await ReadAllSequenceStatesAsync(
                database.RuntimeConnectionString,
                cancellationToken);

            await using (var restore = CreateContext(database.RuntimeConnectionString))
            using (NpgsqlRestoreScope.Enter())
            {
                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                await BeginCoordinatedRestoreAsync(restore, cancellationToken);
                await provider.PurgeDatabase(restore, GetStockTableNames(restore));
                _ = SeedEveryIdentityWithExplicitIds(restore, 900);
                var identityCount = NpgsqlDatabaseOperations.GetIdentityColumns(restore.Model).Count;
                var failureOrdinal = position switch
                {
                    "first" => 1,
                    "middle" => Math.Max(2, identityCount / 2),
                    "last" => identityCount,
                    _ => throw new InvalidOperationException(),
                };
                NpgsqlRestoreScope.SetIdentityRestartCheckpoint(async (ordinal, _) =>
                {
                    if (ordinal != failureOrdinal)
                    {
                        return;
                    }

                    if (mode == "connection")
                    {
                        await restore.Database.CloseConnectionAsync().ConfigureAwait(false);
                    }
                    else if (mode == "cancellation")
                    {
                        throw new OperationCanceledException("Injected reseed cancellation.");
                    }
                    else
                    {
                        throw new InvalidOperationException("Injected reseed failure.");
                    }
                });
                _ = await Assert.ThrowsAnyAsync<Exception>(
                    async () =>
                    {
                        await restore.SaveChangesAsync(cancellationToken);
                        await NpgsqlRestoreCoordinator.CommitCurrentAsync(cancellationToken);
                    });
            }

            await using (var verification = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(
                    ["must survive reseed failure"],
                    await verification.ActivityLogs.Select(row => row.Name).ToArrayAsync(cancellationToken));
            }

            Assert.Equal(
                beforeSequences,
                await ReadAllSequenceStatesAsync(database.RuntimeConnectionString, cancellationToken));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePreservesCustomSequenceDefinitionAndReseedsPositiveRowsExactly()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            await SeedHostHistoryAsync(context, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "ALTER SEQUENCE public.\"ActivityLogs_Id_seq\" MINVALUE -100 MAXVALUE 1000 INCREMENT BY 7 START WITH 8 CYCLE",
                cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_catalog.setval('public.\"ActivityLogs_Id_seq\"'::pg_catalog.regclass, 99, true)",
                cancellationToken);
            var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
            using (NpgsqlRestoreScope.Enter())
            {
                await BeginCoordinatedRestoreAsync(context, cancellationToken);
                await provider.PurgeDatabase(context, GetStockTableNames(context));
                _ = SeedEveryIdentityWithExplicitIds(context, 900);
                await context.SaveChangesAsync(cancellationToken);
                await NpgsqlRestoreCoordinator.CommitCurrentAsync(cancellationToken);
            }

            var state = (await ReadAllSequenceStatesAsync(database.RuntimeConnectionString, cancellationToken))
                ["public.ActivityLogs_Id_seq"];
            Assert.Equal(-100, state.Minimum);
            Assert.Equal(1000, state.Maximum);
            Assert.Equal(7, state.Increment);
            Assert.Equal(8, state.Start);
            Assert.True(state.Cycle);
            Assert.Equal(907, state.LastValue);
            Assert.False(state.IsCalled);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreReseedsEmptyAndNegativeOnlyCustomDescendingSequencesExactly(bool negativeRows)
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            await SeedHostHistoryAsync(context, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "ALTER SEQUENCE public.\"ActivityLogs_Id_seq\" MINVALUE -1000 MAXVALUE 1000 INCREMENT BY -7 START WITH -1 NO CYCLE",
                cancellationToken);
            var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
            using (NpgsqlRestoreScope.Enter())
            {
                await BeginCoordinatedRestoreAsync(context, cancellationToken);
                await provider.PurgeDatabase(context, GetStockTableNames(context));
                if (negativeRows)
                {
                    _ = SeedEveryIdentityWithExplicitIds(context, -900);
                }

                await context.SaveChangesAsync(cancellationToken);
                await NpgsqlRestoreCoordinator.CommitCurrentAsync(cancellationToken);
            }
            var state = (await ReadAllSequenceStatesAsync(database.RuntimeConnectionString, cancellationToken))
                ["public.ActivityLogs_Id_seq"];
            Assert.Equal(-1000, state.Minimum);
            Assert.Equal(1000, state.Maximum);
            Assert.Equal(-7, state.Increment);
            Assert.Equal(-1, state.Start);
            Assert.False(state.Cycle);
            Assert.Equal(negativeRows ? -907 : -1, state.LastValue);
            Assert.False(state.IsCalled);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task CreateExactEmptyStockArchiveAsync(
        string archivePath,
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var history = await context.GetService<IHistoryRepository>()
            .GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var sourceNames = typeof(JellyfinDbContext)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
                .Select(property => property.Name)
                .Append(nameof(HistoryRow))
                .Order(StringComparer.Ordinal);
            foreach (var sourceName in sourceNames)
            {
                var entry = archive.CreateEntry("Database/" + sourceName + ".json");
                var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    if (string.Equals(sourceName, nameof(HistoryRow), StringComparison.Ordinal))
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            history,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            Array.Empty<object>(),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var config = archive.CreateEntry("Config/system.xml");
            var configStream = await config.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (configStream.ConfigureAwait(false))
            {
                await configStream.WriteAsync("archive"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            var created = archive.CreateEntry("Data/created/partial.json");
            var createdStream = await created.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (createdStream.ConfigureAwait(false))
            {
                await createdStream.WriteAsync("partial"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            var manifest = archive.CreateEntry("manifest.json");
            var manifestStream = await manifest.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                await manifestStream.WriteAsync(
                    "{\"ServerVersion\":\"12.2.0.0\",\"BackupEngineVersion\":\"0.2.0\",\"DateCreated\":\"2026-10-09T00:00:00Z\",\"DatabaseTables\":[],\"Options\":{\"Database\":true}}"u8.ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await NpgsqlBackupArchive.BindAsync(archivePath, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string MigrationId, string ProductVersion)[]> ReadHistoryAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        return (await context.GetService<IHistoryRepository>()
            .GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => (row.MigrationId, row.ProductVersion))
            .ToArray();
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

    private static async Task BeginCoordinatedRestoreAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            NpgsqlRestoreCoordinator.RegisterLegacy(context, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task<SortedDictionary<string, SequenceState>> ReadAllSequenceStatesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var states = new SortedDictionary<string, SequenceState>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT schemaname, sequencename, start_value, min_value, max_value, increment_by, cycle FROM pg_catalog.pg_sequences WHERE schemaname = 'public' ORDER BY sequencename",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var definitions = new List<(string Schema, string Name, long Start, long Minimum, long Maximum, long Increment, bool Cycle)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetBoolean(6)));
        }

        await reader.DisposeAsync();
        foreach (var definition in definitions)
        {
            await using var stateCommand = new NpgsqlCommand(
                $"SELECT last_value, is_called FROM {NpgsqlDatabaseOperations.QuoteIdentifier(definition.Schema)}.{NpgsqlDatabaseOperations.QuoteIdentifier(definition.Name)}",
                connection);
            await using var stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken);
            Assert.True(await stateReader.ReadAsync(cancellationToken));
            states.Add(
                definition.Schema + "." + definition.Name,
                new SequenceState(
                    definition.Start,
                    definition.Minimum,
                    definition.Maximum,
                    definition.Increment,
                    definition.Cycle,
                    stateReader.GetInt64(0),
                    stateReader.GetBoolean(1)));
        }

        return states;
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

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => CreateContext(connectionString);

        public ValueTask<JellyfinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(this.CreateDbContext());
        }
    }

    private sealed class FailingRestoreService(
        string connectionString,
        TestApplicationPaths paths,
        string failureStage) : IBackupService
    {
        public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
            => throw new NotSupportedException();

        public Task<BackupManifestDto[]> EnumerateBackups()
            => Task.FromResult(Array.Empty<BackupManifestDto>());

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
            => Task.FromResult<BackupManifestDto?>(null);

        public async Task RestoreBackupAsync(string archivePath)
        {
            await File.WriteAllTextAsync(
                Path.Combine(paths.ConfigurationDirectoryPath, "system.xml"),
                "mutated").ConfigureAwait(false);
            var createdDirectory = Path.Combine(paths.DataPath, "created");
            Directory.CreateDirectory(createdDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(createdDirectory, "partial.json"),
                "mutated").ConfigureAwait(false);

            switch (failureStage)
            {
                case "extraction":
                    throw new InvalidOperationException("Injected post-extraction failure.");
                case "cancellation":
                    throw new OperationCanceledException("Injected post-extraction cancellation.");
                case "history":
                    await using (var historyContext = CreateContext(connectionString))
                    {
                        await historyContext.Database.ExecuteSqlRawAsync(
                            "DELETE FROM public.\"__EFMigrationsHistory\"").ConfigureAwait(false);
                        await historyContext.Database.ExecuteSqlRawAsync(
                            "INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20250420000000_CreateNetworkConfiguration', '12.2.0.0')").ConfigureAwait(false);
                    }

                    throw new InvalidOperationException("Injected post-history failure.");
                case "reseed":
                    await using (var restore = CreateContext(connectionString))
                    {
                        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                        await provider.PurgeDatabase(restore, GetStockTableNames(restore));
                        try
                        {
                            await restore.Database.ExecuteSqlRawAsync(
                                "INSERT INTO public.\"ActivityLogs\" (\"Id\", \"Name\", \"Type\", \"UserId\", \"DateCreated\", \"LogSeverity\", \"RowVersion\") VALUES (900, 'partial', 'BackupRestore', '00000000-0000-0000-0000-000000000000', CURRENT_TIMESTAMP, 2, 1)").ConfigureAwait(false);
                            var last = NpgsqlDatabaseOperations.GetIdentityColumns(restore.Model).Count;
                            NpgsqlRestoreScope.SetIdentityRestartCheckpoint((ordinal, _) => ordinal == last
                                ? Task.FromException(new InvalidOperationException("Injected final reseed failure."))
                                : Task.CompletedTask);
                            await NpgsqlDatabaseOperations.ReseedIdentitiesAsync(
                                restore,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            await NpgsqlRestoreCoordinator.FailAsync(restore).ConfigureAwait(false);
                            throw;
                        }
                    }

                    throw new InvalidOperationException("The injected reseed failure did not run.");
                default:
                    throw new InvalidOperationException("Unknown injected failure stage.");
            }
        }

        public void ScheduleRestoreAndRestartServer(string archivePath)
            => throw new NotSupportedException();
    }

    private sealed class PostSaveFailureRestoreService(IDbContextFactory<JellyfinDbContext> factory) : IBackupService
    {
        public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
            => throw new NotSupportedException();

        public Task<BackupManifestDto[]> EnumerateBackups()
            => Task.FromResult(Array.Empty<BackupManifestDto>());

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
            => Task.FromResult<BackupManifestDto?>(null);

        public async Task RestoreBackupAsync(string archivePath)
        {
            var context = await factory.CreateDbContextAsync().ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
                await provider.PurgeDatabase(context, GetStockTableNames(context)).ConfigureAwait(false);
                await context.SaveChangesAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException("Injected failure after successful SaveChanges and context disposal.");
        }

        public void ScheduleRestoreAndRestartServer(string archivePath)
            => throw new NotSupportedException();
    }

    private sealed class TestApplicationPaths(string root) : IServerApplicationPaths
    {
        public string ProgramDataPath => root;

        public string WebPath => Path.Combine(root, "web");

        public string ProgramSystemPath => Path.Combine(root, "system");

        public string DataPath => Path.Combine(root, "data");

        public string ImageCachePath => Path.Combine(root, "cache", "images");

        public string PluginsPath => Path.Combine(root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(root, "config");

        public string SystemConfigurationFilePath => Path.Combine(this.ConfigurationDirectoryPath, "system.xml");

        public string CachePath => Path.Combine(root, "cache");

        public string TempDirectory => Path.Combine(root, "cache", "temp");

        public string VirtualDataPath => "%data%";

        public string TrickplayPath => Path.Combine(root, "data", "trickplay");

        public string BackupPath => Path.Combine(root, "data", "backups");

        public string RootFolderPath => Path.Combine(root, "root");

        public string DefaultUserViewsPath => Path.Combine(this.RootFolderPath, "views");

        public string PeoplePath => Path.Combine(this.RootFolderPath, "people");

        public string GenrePath => Path.Combine(this.RootFolderPath, "genres");

        public string MusicGenrePath => Path.Combine(this.RootFolderPath, "musicgenres");

        public string StudioPath => Path.Combine(this.RootFolderPath, "studios");

        public string YearPath => Path.Combine(this.RootFolderPath, "years");

        public string UserConfigurationDirectoryPath => Path.Combine(this.ConfigurationDirectoryPath, "users");

        public string DefaultInternalMetadataPath => Path.Combine(root, "default-metadata");

        public string InternalMetadataPath => Path.Combine(root, "metadata");

        public string VirtualInternalMetadataPath => "%metadata%";

        public string ArtistsPath => Path.Combine(this.RootFolderPath, "artists");

        public void CreateDirectories()
        {
            Directory.CreateDirectory(this.ConfigurationDirectoryPath);
            Directory.CreateDirectory(this.DataPath);
            Directory.CreateDirectory(this.RootFolderPath);
            Directory.CreateDirectory(this.InternalMetadataPath);
            Directory.CreateDirectory(this.DefaultInternalMetadataPath);
        }

        public void MakeSanityCheckOrThrow() => this.CreateDirectories();

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
            => Directory.CreateDirectory(path);
    }

    private sealed record SequenceState(
        long Start,
        long Minimum,
        long Maximum,
        long Increment,
        bool Cycle,
        long LastValue,
        bool IsCalled);
}
