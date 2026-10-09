using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class MigrationIntegrationTests
{
    private const string Baseline = "20261009061101_InitialPostgreSqlBaseline";
    private const string Successor = "20261009061446_AddActivityLogTypeDateIndex";
    private const string CoreNormalizedUsername = "20260522092304_UpdateNormalizedUsername";
    private const string CoreDisableLegacyAuthorization = "20260531160000_DisableLegacyAuthorization";

    [Fact]
    public async Task UntouchedJellyfin_FreshSetupSeedsAuditedLegacyRowsAndRunsSetupRoutines()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        Assert.SkipUnless(
            databaseOptions.IsConfigured
                && (!string.IsNullOrWhiteSpace(publishDirectory) || !string.IsNullOrWhiteSpace(publishArchive)),
            "Configure the real PostgreSQL fixture and an integrity-pinned pristine Jellyfin publish to run the fresh-setup migration proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        using var artifacts = HostTestArtifactScope.Create("migration-fresh-setup");
        var artifactRoot = artifacts.CaseDirectory;
        var databasePassword = new global::Npgsql.NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password;
        var redactor = new SensitiveDataRedactor(database.RuntimeConnectionString, databasePassword);
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifactRoot, "host"),
                cancellationToken);
            var layout = JellyfinHostLayout.Create(artifactRoot, publish, "migration-fresh-setup");
            var package = Path.Combine(
                repositoryRoot,
                "artifacts",
                "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
            Assert.True(File.Exists(package), "Build the audited plugin ZIP before running the fresh-setup migration proof.");
            layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);

            await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
            {
                await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                Assert.Equal("Healthy", (await host.GetStringAsync("health", cancellationToken)).Trim());
            }

            await publish.VerifyUnchangedAsync(cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            var expected = NpgsqlHostMigrationAudit.AuditedMigrationIds
                .Append(Baseline)
                .Append(Successor)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, await ReadHistoryAsync(context, cancellationToken));
        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, redactor);
            throw;
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestrictedRole_BaselineIsRepeatableAndSuccessorRoundTripsWithoutDataLoss()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            var migrator = context.GetService<IMigrator>();

            // The database already exists and is owned by the deliberately
            // restricted runtime role. Applying the same target twice must be
            // a true no-op on the second invocation.
            await migrator.MigrateAsync(Baseline, cancellationToken);
            await migrator.MigrateAsync(Baseline, cancellationToken);
            Assert.Equal([Baseline], await ReadHistoryAsync(context, cancellationToken));

            context.ActivityLogs.Add(new ActivityLog("migration sentinel", "Migration", Guid.NewGuid()));
            await context.SaveChangesAsync(cancellationToken);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({CoreNormalizedUsername}, {"12.2.0.0"}), ({CoreDisableLegacyAuthorization}, {"12.2.0.0"})",
                cancellationToken);

            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Equal(1, await context.ActivityLogs.CountAsync(cancellationToken));
            Assert.True(await IndexExistsAsync(context, cancellationToken));

            Assert.Equal(
                [CoreNormalizedUsername, CoreDisableLegacyAuthorization, Baseline, Successor],
                await ReadHistoryAsync(context, cancellationToken));

            Assert.Equal(5, await ScalarAsync<long>(context, "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname LIKE 'TR_%_Intern' AND NOT tgisinternal", cancellationToken));
            Assert.Equal(10, await ScalarAsync<long>(context, "SELECT count(*) FROM pg_catalog.pg_proc WHERE pronamespace = 'public'::regnamespace AND proname LIKE '__jellyfin_%'", cancellationToken));
            Assert.Equal(0, await ScalarAsync<long>(context, "SELECT count(*) FROM pg_catalog.pg_extension WHERE extname IN ('pgcrypto', 'citext')", cancellationToken));
            Assert.Equal(0, await ScalarAsync<long>(context, "SELECT count(*) FROM pg_catalog.pg_constraint WHERE conname IN ('PK_BaseItemProviders', 'PK_PeopleBaseItemMap', 'PK_UserData')", cancellationToken));
            Assert.True(await ScalarAsync<bool>(context, "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user", cancellationToken));

            await migrator.MigrateAsync(Baseline, cancellationToken);
            Assert.False(await IndexExistsAsync(context, cancellationToken));
            Assert.Equal(1, await context.ActivityLogs.CountAsync(cancellationToken));
            Assert.Equal(
                [CoreNormalizedUsername, CoreDisableLegacyAuthorization, Baseline],
                await ReadHistoryAsync(context, cancellationToken));

            var idempotentSql = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            await context.Database.ExecuteSqlRawAsync(idempotentSql, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(idempotentSql, cancellationToken);
            Assert.True(await IndexExistsAsync(context, cancellationToken));
            Assert.Equal(1, await context.ActivityLogs.CountAsync(cancellationToken));
            Assert.Equal(
                [CoreNormalizedUsername, CoreDisableLegacyAuthorization, Baseline, Successor],
                await ReadHistoryAsync(context, cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
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

    private static async Task<string[]> ReadHistoryAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async Task<bool> IndexExistsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
        => await ScalarAsync<bool>(
            context,
            "SELECT to_regclass('public.\"IX_ActivityLogs_Npgsql_Type_DateCreated\"') IS NOT NULL",
            cancellationToken);

    private static async Task<T> ScalarAsync<T>(
        JellyfinDbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
