using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class MigrationBackupIntegrationTests
{
    private const string Baseline = "20261009061101_InitialPostgreSqlBaseline";
    private const string Successor = "20261009061446_AddActivityLogTypeDateIndex";

    [Fact]
    public async Task PopulatedSnapshot_RestoresSchemaRowsHistoryAndIdentityExactly()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var scratch = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var factory = new TestContextFactory(database.RuntimeConnectionString);
            long originalId;
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                var row = new ActivityLog("backup sentinel", "MigrationBackup", Guid.NewGuid());
                context.ActivityLogs.Add(row);
                await context.SaveChangesAsync(cancellationToken);
                originalId = row.Id;
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "ActivityLogs" ("Name", "Overview", "Type", "UserId", "DateCreated", "LogSeverity", "RowVersion")
                    SELECT 'stream-' || value, repeat('x', 512), 'MigrationBackup', '00000000-0000-0000-0000-000000000000', now(), 0, value
                    FROM generate_series(1, 4096) value
                    """,
                    cancellationToken);
            }

            var keys = await Task.WhenAll(
                NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken),
                NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            var key = keys[0];

            await using (var context = factory.CreateDbContext())
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"ActivityLogs\"", cancellationToken);
                await context.GetService<IMigrator>().MigrateAsync(Baseline, cancellationToken);
                await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"ForcedFailureMarker\" (\"Id\" integer NOT NULL)", cancellationToken);
                await Assert.ThrowsAsync<global::Npgsql.PostgresException>(
                    () => context.Database.ExecuteSqlRawAsync("SELECT 1 / 0", cancellationToken));
                Assert.False(await IndexExistsAsync(context, cancellationToken));
            }

            await NpgsqlMigrationBackup.RestoreAsync(
                database.RuntimeConnectionString,
                scratch,
                key,
                cancellationToken);

            var verificationKey = await NpgsqlMigrationBackup.CreateAsync(
                factory,
                database.RuntimeConnectionString,
                scratch,
                cancellationToken);
            var originalManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, key, "manifest.json"), cancellationToken))!.AsObject();
            var restoredManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, verificationKey, "manifest.json"), cancellationToken))!.AsObject();
            originalManifest.Remove("createdUtc");
            restoredManifest.Remove("createdUtc");
            Assert.True(JsonNode.DeepEquals(originalManifest, restoredManifest));

            await using (var context = factory.CreateDbContext())
            {
                var restored = await context.ActivityLogs.SingleAsync(row => row.Id == originalId, cancellationToken);
                Assert.Equal(originalId, restored.Id);
                Assert.Equal("backup sentinel", restored.Name);
                Assert.Equal(4097, await context.ActivityLogs.CountAsync(cancellationToken));
                Assert.True(await IndexExistsAsync(context, cancellationToken));
                Assert.False(await context.Database.SqlQueryRaw<bool>(
                    "SELECT to_regclass('public.\"ForcedFailureMarker\"') IS NOT NULL AS \"Value\"")
                    .SingleAsync(cancellationToken));
                var next = new ActivityLog("after restore", "MigrationBackup", Guid.NewGuid());
                context.ActivityLogs.Add(next);
                await context.SaveChangesAsync(cancellationToken);
                Assert.True(next.Id > originalId);
            }

            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
            await NpgsqlMigrationBackup.DeleteAsync(scratch, keys[1]);
            await NpgsqlMigrationBackup.DeleteAsync(scratch, verificationKey);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Snapshot_RejectsWrongTargetCorruptionAndSymlinkWithoutMutation()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var source = await environment.CreateDatabaseAsync(cancellationToken);
        await using var wrongTarget = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var scratch = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var factory = new TestContextFactory(source.RuntimeConnectionString);
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("unchanged", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var key = await NpgsqlMigrationBackup.CreateAsync(factory, source.RuntimeConnectionString, scratch, cancellationToken);
            var directory = Path.Combine(scratch, key);
            if (!OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416 // Guarded by the platform check above.
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
                Assert.All(
                    Directory.EnumerateFiles(directory),
                    file => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file)));
#pragma warning restore CA1416
            }

            await RewriteManifestAsync(
                directory,
                manifest => manifest["createdUtc"] = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(8)),
                cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlMigrationBackup.RestoreAsync(source.RuntimeConnectionString, scratch, key, cancellationToken));
            await RewriteManifestAsync(
                directory,
                manifest => manifest["createdUtc"] = DateTimeOffset.UtcNow,
                cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlMigrationBackup.RestoreAsync(wrongTarget.RuntimeConnectionString, scratch, key, cancellationToken));
            await using (var connection = new global::Npgsql.NpgsqlConnection(wrongTarget.RuntimeConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'";
                Assert.Equal(0L, await command.ExecuteScalarAsync(cancellationToken));
            }

            var dataFile = Directory.EnumerateFiles(directory, "*.copy").First(path => new FileInfo(path).Length > 16);
            await using (var stream = new FileStream(dataFile, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                stream.Position = 12;
                await stream.WriteAsync(new byte[] { 0x7f }, cancellationToken);
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlMigrationBackup.RestoreAsync(source.RuntimeConnectionString, scratch, key, cancellationToken));
            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("unchanged", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
            }

            var dataBytes = await File.ReadAllBytesAsync(dataFile, cancellationToken);
            dataBytes[0] = 0;
            await File.WriteAllBytesAsync(dataFile, dataBytes, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dataFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await RewriteManifestAsync(
                directory,
                manifest => manifest["tables"]!
                    .AsArray()
                    .Select(node => node!.AsObject())
                    .Single(node => node["file"]!.GetValue<string>() == Path.GetFileName(dataFile))["sha256"] = Convert.ToHexStringLower(SHA256.HashData(dataBytes)),
                cancellationToken);
            await Assert.ThrowsAnyAsync<global::Npgsql.NpgsqlException>(
                () => NpgsqlMigrationBackup.RestoreAsync(source.RuntimeConnectionString, scratch, key, cancellationToken));
            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("unchanged", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
                Assert.True(await IndexExistsAsync(context, cancellationToken));
            }

            if (!OperatingSystem.IsWindows())
            {
                var unsafeKey = "npgsql-v1-00000000000000000000000000000000";
                Directory.CreateSymbolicLink(Path.Combine(scratch, unsafeKey), directory);
                await Assert.ThrowsAsync<InvalidDataException>(() => NpgsqlMigrationBackup.DeleteAsync(scratch, unsafeKey));
            }

            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EmptySnapshot_RestoresMigrationZeroAndRejectsUnsafeKeys()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var scratch = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var factory = new TestContextFactory(database.RuntimeConnectionString);
            using (var canceled = new CancellationTokenSource())
            {
                await canceled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, canceled.Token));
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            }

            var crashRemnant = Path.Combine(scratch, "npgsql-v1-00000000000000000000000000000000.partial");
            Directory.CreateDirectory(crashRemnant);
            await File.WriteAllTextAsync(Path.Combine(crashRemnant, "orphan"), "incomplete", cancellationToken);
            var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            Assert.False(Directory.Exists(crashRemnant));
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            }

            await NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, key, cancellationToken);
            await using (var connection = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'";
                Assert.Equal(0L, await command.ExecuteScalarAsync(cancellationToken));
            }

            await Assert.ThrowsAsync<ArgumentException>(
                () => NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, "../escape", cancellationToken));
            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);

            await using (var connection = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE public."__EFMigrationsHistory" (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );
                    INSERT INTO public."__EFMigrationsHistory" VALUES ('seeded-code-migration', '12.2.0.0');
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var historyKey = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            }

            await NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, historyKey, cancellationToken);
            await using (var connection = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM public.\"__EFMigrationsHistory\"";
                Assert.Equal("seeded-code-migration", await command.ExecuteScalarAsync(cancellationToken));
            }

            await NpgsqlMigrationBackup.DeleteAsync(scratch, historyKey);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task<bool> IndexExistsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var result = await context.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass('public.\"IX_ActivityLogs_Npgsql_Type_DateCreated\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
        return result;
    }

    private static async Task RewriteManifestAsync(
        string directory,
        Action<JsonObject> update,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        update(manifest);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        var completionPath = Path.Combine(directory, "complete");
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken)));
        await File.WriteAllTextAsync(completionPath, manifestHash + "\n", cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(completionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext()
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
}
