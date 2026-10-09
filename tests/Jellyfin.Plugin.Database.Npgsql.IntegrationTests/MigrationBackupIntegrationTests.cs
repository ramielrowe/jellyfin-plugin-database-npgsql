using System.Runtime.InteropServices;
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
    private const string UuidAggregates = "20261009113458_AddUuidMinMaxAggregates";
    private const string HistorySchemaSqlForTest = """
        CREATE TABLE public."__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """;

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
                await context.GetService<IMigrator>().MigrateAsync(UuidAggregates, cancellationToken);
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
                Assert.True(await context.Database.SqlQueryRaw<bool>(
                    "SELECT to_regprocedure('public.min(uuid)') IS NOT NULL AS \"Value\"")
                    .SingleAsync(cancellationToken));
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

            var dataFile = Directory.EnumerateFiles(directory, "*.copy").First(path => new FileInfo(path).Length > 16);
            if (!OperatingSystem.IsWindows())
            {
                var symlinkTarget = Path.Combine(scratch, "symlink-target.copy");
                File.Move(dataFile, symlinkTarget);
                File.CreateSymbolicLink(dataFile, symlinkTarget);
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => NpgsqlMigrationBackup.RestoreAsync(source.RuntimeConnectionString, scratch, key, cancellationToken));
                File.Delete(dataFile);
                File.Move(symlinkTarget, dataFile);
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

    [Theory]
    [InlineData("flush-existing-root")]
    [InlineData("flush-existing-root-parent")]
    [InlineData("flush-table-0000")]
    [InlineData("flush-schema")]
    [InlineData("flush-manifest")]
    [InlineData("flush-completion")]
    [InlineData("flush-partial-directory")]
    [InlineData("flush-root-before-rename")]
    [InlineData("before-rename")]
    [InlineData("after-rename")]
    [InlineData("flush-root-after-rename")]
    public async Task PublicationFailure_NeverReturnsAKeyAndLeavesNoPublishedOrPartialSnapshot(string failurePoint)
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
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("disk-full", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var observedPoints = new List<string>();
            using var faults = NpgsqlBackupFileSystem.InjectFaults(point =>
            {
                observedPoints.Add(point);
                if (string.Equals(point, failurePoint, StringComparison.Ordinal))
                {
                    throw new IOException("Injected no-space-left or durable-publication failure.");
                }
            });
            await Assert.ThrowsAsync<IOException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            if (!failurePoint.StartsWith("flush-existing-root", StringComparison.Ordinal))
            {
                Assert.Contains(observedPoints, point => point.StartsWith("flush-root-after-delete-", StringComparison.Ordinal));
            }
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

    [Theory]
    [InlineData("write-table-0000")]
    [InlineData("write-schema")]
    [InlineData("write-manifest")]
    public async Task PartialWriteFailure_NeverPublishesAndRemovesThePartiallyWrittenArtifact(string writePoint)
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
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("partial-write", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            using (NpgsqlBackupFileSystem.InjectWriteFailure(writePoint, 7))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            }

            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
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
    public async Task FirstUse_DurablySyncsTheNewRootAndItsExistingApplicationDataParentBeforePublication()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var parent = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(parent, "NpgsqlMigrationBackups");
        Directory.CreateDirectory(parent);
        try
        {
            var factory = new TestContextFactory(database.RuntimeConnectionString);
            var points = new List<string>();
            using (NpgsqlBackupFileSystem.InjectFaults(points.Add))
            {
                var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.True(points.IndexOf("flush-new-root") >= 0);
                Assert.True(points.IndexOf("flush-root-parent-after-create") > points.IndexOf("flush-new-root"));
                Assert.True(points.IndexOf("flush-manifest") > points.IndexOf("flush-root-parent-after-create"));
                Assert.True(points.IndexOf("flush-completion") > points.IndexOf("flush-manifest"));
                Assert.True(points.IndexOf("flush-partial-directory") > points.IndexOf("flush-completion"));
                Assert.True(points.IndexOf("flush-root-before-rename") > points.IndexOf("flush-partial-directory"));
                Assert.True(points.IndexOf("before-rename") > points.IndexOf("flush-root-before-rename"));
                Assert.True(points.IndexOf("after-rename") > points.IndexOf("before-rename"));
                Assert.True(points.IndexOf("flush-root-after-rename") > points.IndexOf("after-rename"));
                await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
            }

            Directory.Delete(scratch);
            var failedParentSyncs = 0;
            using (NpgsqlBackupFileSystem.InjectFaults(point =>
            {
                if (string.Equals(point, "flush-root-parent-after-create", StringComparison.Ordinal)
                    || string.Equals(point, "flush-existing-root-parent", StringComparison.Ordinal))
                {
                    failedParentSyncs++;
                    throw new IOException("Injected parent directory sync failure.");
                }
            }))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
                await Assert.ThrowsAsync<IOException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            }

            Assert.Equal(2, failedParentSyncs);
            Assert.True(Directory.Exists(scratch));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            var retryKey = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            await NpgsqlMigrationBackup.DeleteAsync(scratch, retryKey);

            var missingParentRoot = Path.Combine(parent, "missing-parent", "NpgsqlMigrationBackups");
            await Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, missingParentRoot, cancellationToken));
            Assert.False(Directory.Exists(Path.Combine(parent, "missing-parent")));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PopulatedSnapshot_RejectsExtraObjectsColumnDriftAndAlteredIdentitySequenceBeforePublication()
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
            await using var context = factory.CreateDbContext();
            await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);

            await context.Database.ExecuteSqlRawAsync("CREATE VIEW public.\"UnexpectedView\" AS SELECT 1 AS \"Value\"", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP VIEW public.\"UnexpectedView\"", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("CREATE TYPE public.\"UnexpectedType\" AS ENUM ('unexpected')", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP TYPE public.\"UnexpectedType\"", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("CREATE SEQUENCE public.\"UnexpectedSequence\"", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP SEQUENCE public.\"UnexpectedSequence\"", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" ALTER COLUMN \"Overview\" TYPE character varying(1024)", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" ALTER COLUMN \"Overview\" TYPE text", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"KeyframeData\" ALTER COLUMN \"KeyframeTicks\" TYPE bigint[][] USING \"KeyframeTicks\"::bigint[][]", cancellationToken);
            Assert.Equal(2, await context.Database.SqlQueryRaw<int>(
                "SELECT attribute.attndims AS \"Value\" FROM pg_catalog.pg_attribute attribute WHERE attribute.attrelid = 'public.\"KeyframeData\"'::regclass AND attribute.attname = 'KeyframeTicks'")
                .SingleAsync(cancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"KeyframeData\" ALTER COLUMN \"KeyframeTicks\" TYPE bigint[] USING \"KeyframeTicks\"::bigint[]", cancellationToken);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
                "SELECT attribute.attndims AS \"Value\" FROM pg_catalog.pg_attribute attribute WHERE attribute.attrelid = 'public.\"KeyframeData\"'::regclass AND attribute.attname = 'KeyframeTicks'")
                .SingleAsync(cancellationToken));

            await context.Database.ExecuteSqlRawAsync("GRANT SELECT (\"Overview\") ON TABLE public.\"ActivityLogs\" TO PUBLIC", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("REVOKE SELECT (\"Overview\") ON TABLE public.\"ActivityLogs\" FROM PUBLIC", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" REPLICA IDENTITY FULL", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" REPLICA IDENTITY DEFAULT", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("COMMENT ON TABLE public.\"ActivityLogs\" IS 'unsupported backup drift'", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("COMMENT ON TABLE public.\"ActivityLogs\" IS NULL", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" SET (autovacuum_enabled = false)", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" RESET (autovacuum_enabled)", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" SET (toast.autovacuum_enabled = false)", cancellationToken);
            Assert.Contains(
                "autovacuum_enabled=false",
                await context.Database.SqlQueryRaw<string>(
                    "SELECT option_value AS \"Value\" FROM pg_catalog.pg_class parent JOIN pg_catalog.pg_class toast ON toast.oid = parent.reltoastrelid CROSS JOIN LATERAL unnest(toast.reloptions) option_value WHERE parent.oid = 'public.\"ActivityLogs\"'::regclass")
                    .ToListAsync(cancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"ActivityLogs\" RESET (toast.autovacuum_enabled)", cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER INDEX public.\"IX_ActivityLogs_DateCreated\" SET (fillfactor = 80)", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("ALTER INDEX public.\"IX_ActivityLogs_DateCreated\" RESET (fillfactor)", cancellationToken);

            await context.Database.ExecuteSqlRawAsync(
                "CREATE STATISTICS public.\"UnexpectedStatistics\" ON \"Name\", \"Type\" FROM public.\"ActivityLogs\"",
                cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("DROP STATISTICS public.\"UnexpectedStatistics\"", cancellationToken);

            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER SEQUENCE public."ActivityLogs_Id_seq" RENAME TO "__swapped_identity_sequence";
                ALTER SEQUENCE public."CustomItemDisplayPreferences_Id_seq" RENAME TO "ActivityLogs_Id_seq";
                ALTER SEQUENCE public."__swapped_identity_sequence" RENAME TO "CustomItemDisplayPreferences_Id_seq";
                """,
                cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER SEQUENCE public."ActivityLogs_Id_seq" RENAME TO "__swapped_identity_sequence";
                ALTER SEQUENCE public."CustomItemDisplayPreferences_Id_seq" RENAME TO "ActivityLogs_Id_seq";
                ALTER SEQUENCE public."__swapped_identity_sequence" RENAME TO "CustomItemDisplayPreferences_Id_seq";
                """,
                cancellationToken);

            await context.Database.ExecuteSqlRawAsync("ALTER SEQUENCE public.\"ActivityLogs_Id_seq\" CACHE 2", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
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
    public async Task PopulatedSnapshot_RejectsRestrictedOwnerPublicationMembershipWithoutMutation()
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
            await using var context = factory.CreateDbContext();
            await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
            var privileges = await context.Database.SqlQueryRaw<bool>(
                "SELECT NOT rolsuper AND NOT rolreplication AS \"Value\" FROM pg_catalog.pg_roles WHERE rolname = current_user")
                .SingleAsync(cancellationToken);
            Assert.True(privileges);

            await context.Database.ExecuteSqlRawAsync(
                "CREATE PUBLICATION \"JellyfinBackupContractPublication\" FOR TABLE public.\"ActivityLogs\"",
                cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));

            var membershipCount = await context.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM pg_catalog.pg_publication_rel membership JOIN pg_catalog.pg_class relation ON relation.oid = membership.prrelid JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public'")
                .SingleAsync(cancellationToken);
            Assert.Equal(1, membershipCount);
            Assert.True(await context.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('public.\"ActivityLogs\"') IS NOT NULL AS \"Value\"")
                .SingleAsync(cancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));

            await context.Database.ExecuteSqlRawAsync(
                "DROP PUBLICATION \"JellyfinBackupContractPublication\"",
                cancellationToken);
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
    public async Task PopulatedSnapshot_RejectsCrossSchemaDependenciesBeforePublication()
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
            long sentinelId;
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                var sentinel = new ActivityLog("dependency-boundary", "MigrationBackup", Guid.Empty);
                context.ActivityLogs.Add(sentinel);
                await context.SaveChangesAsync(cancellationToken);
                sentinelId = sentinel.Id;

                Assert.True(await context.Database.SqlQueryRaw<bool>(
                    "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AS \"Value\" FROM pg_catalog.pg_roles WHERE rolname = current_user")
                    .SingleAsync(cancellationToken));
                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE SCHEMA backup_auxiliary;
                    CREATE TABLE backup_auxiliary.unrelated (id integer PRIMARY KEY, value text NOT NULL);
                    INSERT INTO backup_auxiliary.unrelated VALUES (1, 'preserve me');
                    CREATE VIEW backup_auxiliary.unrelated_view AS SELECT id, value FROM backup_auxiliary.unrelated;
                    CREATE EXTENSION hstore WITH SCHEMA backup_auxiliary;
                    """,
                    cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    "CREATE VIEW backup_auxiliary.public_view AS SELECT \"Id\", \"Name\" FROM public.\"ActivityLogs\"",
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.True(await RelationExistsAsync(context, "backup_auxiliary.public_view", cancellationToken));
                await context.Database.ExecuteSqlRawAsync("DROP VIEW backup_auxiliary.public_view", cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    "CREATE MATERIALIZED VIEW backup_auxiliary.public_materialized AS SELECT count(*) AS count FROM public.\"ActivityLogs\"",
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.True(await RelationExistsAsync(context, "backup_auxiliary.public_materialized", cancellationToken));
                await context.Database.ExecuteSqlRawAsync("DROP MATERIALIZED VIEW backup_auxiliary.public_materialized", cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE backup_auxiliary.public_reference (id integer PRIMARY KEY, activity_log_id bigint NOT NULL REFERENCES public.\"ActivityLogs\" (\"Id\"))",
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.True(await RelationExistsAsync(context, "backup_auxiliary.public_reference", cancellationToken));
                await context.Database.ExecuteSqlRawAsync("DROP TABLE backup_auxiliary.public_reference", cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE backup_auxiliary.users (id uuid PRIMARY KEY);
                    INSERT INTO backup_auxiliary.users VALUES ('00000000-0000-0000-0000-000000000000');
                    ALTER TABLE public."ActivityLogs"
                      ADD CONSTRAINT "FK_ActivityLogs_BackupAuxiliary_UserId"
                      FOREIGN KEY ("UserId") REFERENCES backup_auxiliary.users (id);
                    """,
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM pg_catalog.pg_constraint WHERE conname = 'FK_ActivityLogs_BackupAuxiliary_UserId'")
                    .SingleAsync(cancellationToken));
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE public.\"ActivityLogs\" DROP CONSTRAINT \"FK_ActivityLogs_BackupAuxiliary_UserId\"; DROP TABLE backup_auxiliary.users",
                    cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE backup_auxiliary.activity_log_child () INHERITS (public.\"ActivityLogs\")",
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.True(await RelationExistsAsync(context, "backup_auxiliary.activity_log_child", cancellationToken));
                await context.Database.ExecuteSqlRawAsync("DROP TABLE backup_auxiliary.activity_log_child", cancellationToken);

                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE public.\"ExtensionMemberProbe\" (id integer); ALTER EXTENSION hstore ADD TABLE public.\"ExtensionMemberProbe\"",
                    cancellationToken);
                await AssertDependencyBackupRejectedAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
                Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM pg_catalog.pg_depend WHERE classid = 'pg_catalog.pg_class'::regclass AND objid = 'public.\"ExtensionMemberProbe\"'::regclass AND refclassid = 'pg_catalog.pg_extension'::regclass AND deptype = 'e'")
                    .SingleAsync(cancellationToken));
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER EXTENSION hstore DROP TABLE public.\"ExtensionMemberProbe\"; DROP TABLE public.\"ExtensionMemberProbe\"",
                    cancellationToken);
            }

            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("dependency-boundary", (await context.ActivityLogs.SingleAsync(row => row.Id == sentinelId, cancellationToken)).Name);
                Assert.Equal("preserve me", await context.Database.SqlQueryRaw<string>(
                    "SELECT value AS \"Value\" FROM backup_auxiliary.unrelated_view WHERE id = 1")
                    .SingleAsync(cancellationToken));
            }

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
    public async Task PopulatedSnapshot_PreservesUnrelatedCrossSchemaObjectsAndGlobalInvariants()
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
            long sentinelId;
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                var sentinel = new ActivityLog("dependency-boundary", "MigrationBackup", Guid.Empty);
                context.ActivityLogs.Add(sentinel);
                await context.SaveChangesAsync(cancellationToken);
                sentinelId = sentinel.Id;
                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE SCHEMA backup_auxiliary;
                    CREATE TABLE backup_auxiliary.unrelated (id integer PRIMARY KEY, value text NOT NULL);
                    INSERT INTO backup_auxiliary.unrelated VALUES (1, 'preserve me');
                    CREATE VIEW backup_auxiliary.unrelated_view AS SELECT id, value FROM backup_auxiliary.unrelated;
                    CREATE EXTENSION hstore WITH SCHEMA backup_auxiliary;
                    """,
                    cancellationToken);
            }

            var key = await NpgsqlMigrationBackup.CreateAsync(
                factory,
                database.RuntimeConnectionString,
                scratch,
                cancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM public.\"ActivityLogs\"", cancellationToken);
            }

            await NpgsqlMigrationBackup.RestoreAsync(
                database.RuntimeConnectionString,
                scratch,
                key,
                cancellationToken);

            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("dependency-boundary", (await context.ActivityLogs.SingleAsync(row => row.Id == sentinelId, cancellationToken)).Name);
                Assert.Equal("preserve me", await context.Database.SqlQueryRaw<string>(
                    "SELECT value AS \"Value\" FROM backup_auxiliary.unrelated_view WHERE id = 1")
                    .SingleAsync(cancellationToken));
                Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM pg_catalog.pg_extension WHERE extname = 'hstore' AND extnamespace = 'backup_auxiliary'::regnamespace")
                    .SingleAsync(cancellationToken));
                Assert.Equal("standard public schema", await context.Database.SqlQueryRaw<string>(
                    "SELECT obj_description('public'::regnamespace, 'pg_namespace') AS \"Value\"")
                    .SingleAsync(cancellationToken));
            }

            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
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

    [Theory]
    [InlineData("before-delete-completed", true)]
    [InlineData("after-delete-completed", false)]
    [InlineData("flush-root-after-delete-completed", false)]
    public async Task CompletedDeletion_RetryDurablySyncsTheRootAfterInjectedFailure(
        string failurePoint,
        bool directorySurvivesFailure)
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
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
            }

            var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            using (NpgsqlBackupFileSystem.InjectFaults(point =>
            {
                if (string.Equals(point, failurePoint, StringComparison.Ordinal))
                {
                    throw new IOException("Injected completed backup deletion failure.");
                }
            }))
            {
                await Assert.ThrowsAsync<IOException>(() => NpgsqlMigrationBackup.DeleteAsync(scratch, key));
            }

            Assert.Equal(directorySurvivesFailure, Directory.Exists(Path.Combine(scratch, key)));
            var retryPoints = new List<string>();
            using (NpgsqlBackupFileSystem.InjectFaults(retryPoints.Add))
            {
                await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
            }

            Assert.Contains("flush-root-after-delete-completed", retryPoints);
            if (directorySurvivesFailure)
            {
                Assert.Contains("after-delete-completed", retryPoints);
            }

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
    public async Task SentinelSnapshots_RejectExactSchemaOwnershipAclColumnAndRelationDrift()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var scratch = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var migrationZeroDrifts = new[]
            {
                "ALTER SCHEMA public OWNER TO CURRENT_USER",
                "GRANT CREATE ON SCHEMA public TO PUBLIC",
                "COMMENT ON SCHEMA public IS 'drifted migration zero schema'",
            };
            foreach (var drift in migrationZeroDrifts)
            {
                await using var database = await environment.CreateDatabaseAsync(cancellationToken);
                var factory = new TestContextFactory(database.RuntimeConnectionString);
                await using (var connection = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await using var command = new global::Npgsql.NpgsqlCommand(drift, connection);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await Assert.ThrowsAsync<NotSupportedException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
                global::Npgsql.NpgsqlConnection.ClearAllPools();
            }

            var historyDrifts = new[]
            {
                "ALTER TABLE public.\"__EFMigrationsHistory\" ALTER COLUMN \"ProductVersion\" DROP NOT NULL",
                "ALTER TABLE public.\"__EFMigrationsHistory\" ALTER COLUMN \"ProductVersion\" SET DEFAULT 'drifted'",
                "GRANT SELECT ON TABLE public.\"__EFMigrationsHistory\" TO PUBLIC",
                "ALTER TABLE public.\"__EFMigrationsHistory\" OWNER TO pg_database_owner",
                "ALTER TABLE public.\"__EFMigrationsHistory\" REPLICA IDENTITY FULL",
                "COMMENT ON TABLE public.\"__EFMigrationsHistory\" IS 'drifted history schema'",
            };
            foreach (var drift in historyDrifts)
            {
                await using var database = await environment.CreateDatabaseAsync(cancellationToken);
                var factory = new TestContextFactory(database.RuntimeConnectionString);
                await using (var connection = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = HistorySchemaSqlForTest + drift;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await Assert.ThrowsAsync<NotSupportedException>(
                    () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
                global::Npgsql.NpgsqlConnection.ClearAllPools();
            }
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
    public async Task Restore_ConsumesImmutableStagedBytesAndRejectsActiveLeasedTransactionsWithoutHanging()
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
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("immutable-stage", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            var directory = Path.Combine(scratch, key);
            var dataFile = Directory.EnumerateFiles(directory, "*.copy").First();
            string? hardLink = null;
            if (!OperatingSystem.IsWindows())
            {
                hardLink = Path.Combine(scratch, "hardlink-copy");
                Assert.Equal(0, CreateHardLinkUnix(dataFile, hardLink));
            }

            await using (var blocker = new global::Npgsql.NpgsqlConnection(database.RuntimeConnectionString))
            {
                await blocker.OpenAsync(cancellationToken);
                await using var blockedTransaction = await blocker.BeginTransactionAsync(cancellationToken);
                await using var lockCommand = new global::Npgsql.NpgsqlCommand("LOCK TABLE public.\"ActivityLogs\" IN ACCESS EXCLUSIVE MODE", blocker, blockedTransaction);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
                var started = DateTime.UtcNow;
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, key, CancellationToken.None));
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
                await blockedTransaction.RollbackAsync(cancellationToken);
            }

            var replacementManifest = Path.Combine(scratch, "replacement-manifest");
            var replacementData = Path.Combine(scratch, "replacement-data");
            await File.WriteAllTextAsync(replacementManifest, "atomically swapped after validation", cancellationToken);
            await File.WriteAllTextAsync(replacementData, "atomically swapped after validation", cancellationToken);
            using (NpgsqlBackupFileSystem.InjectFaults(point =>
            {
                if (!string.Equals(point, "validated-immutable-stage-ready", StringComparison.Ordinal))
                {
                    return;
                }

                File.Move(replacementManifest, Path.Combine(directory, "manifest.json"), overwrite: true);
                File.Move(replacementData, dataFile, overwrite: true);
                if (hardLink is not null)
                {
                    File.WriteAllText(hardLink, "hard-linked source changed after validation");
                }
            }))
            {
                await NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, key, cancellationToken);
            }

            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("immutable-stage", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
            }

            await NpgsqlMigrationBackup.DeleteAsync(scratch, key);
            if (hardLink is not null)
            {
                File.Delete(hardLink);
            }
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
    public async Task Restore_PartialStagingWriteFailureLeavesTheDatabaseAndPublishedBackupUntouched()
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
            await using (var context = factory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("captured", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var key = await NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                context.ActivityLogs.Add(new ActivityLog("must-survive-staging-failure", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, key, "manifest.json"), cancellationToken))!.AsObject();
            var firstTableFile = manifest["tables"]!.AsArray()[0]!["file"]!.GetValue<string>();
            using (NpgsqlBackupFileSystem.InjectWriteFailure("write-restore-stage-" + firstTableFile, 7))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, key, cancellationToken));
            }

            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal(2, await context.ActivityLogs.CountAsync(cancellationToken));
                Assert.Contains(
                    await context.ActivityLogs.Select(row => row.Name).ToListAsync(cancellationToken),
                    name => string.Equals(name, "must-survive-staging-failure", StringComparison.Ordinal));
            }

            Assert.Equal(
                new[] { key },
                Directory.EnumerateDirectories(scratch).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            await NpgsqlMigrationBackup.RestoreAsync(database.RuntimeConnectionString, scratch, key, cancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                Assert.Equal("captured", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
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
    public async Task Restore_RejectsSameNameAndOidOnADifferentPostgreSqlSystem()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        Assert.SkipUnless(options.UseContainer, "The cross-cluster identity proof requires two harness-owned disposable containers.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var identity = TestResourceNames.Create("binding");
        await using var sourceEnvironment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var targetEnvironment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var source = await sourceEnvironment.CreateDatabaseAsync(cancellationToken, fixedIdentity: identity);
        await using var target = await targetEnvironment.CreateDatabaseAsync(cancellationToken, fixedIdentity: identity);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var scratch = Path.Combine(repositoryRoot, ".artifacts", "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var sourceFactory = new TestContextFactory(source.RuntimeConnectionString);
            var targetFactory = new TestContextFactory(target.RuntimeConnectionString);
            await using (var context = sourceFactory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("source-system", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = targetFactory.CreateDbContext())
            {
                await context.GetService<IMigrator>().MigrateAsync(Successor, cancellationToken);
                context.ActivityLogs.Add(new ActivityLog("target-system", "MigrationBackup", Guid.NewGuid()));
                await context.SaveChangesAsync(cancellationToken);
            }

            var sourceIdentity = await ReadServerIdentityAsync(source.RuntimeConnectionString, cancellationToken);
            var targetIdentity = await ReadServerIdentityAsync(target.RuntimeConnectionString, cancellationToken);
            Assert.Equal(source.DatabaseName, target.DatabaseName);
            Assert.Equal(sourceIdentity.DatabaseOid, targetIdentity.DatabaseOid);
            Assert.NotEqual(sourceIdentity.SystemIdentifier, targetIdentity.SystemIdentifier);

            var key = await NpgsqlMigrationBackup.CreateAsync(
                sourceFactory,
                source.RuntimeConnectionString,
                scratch,
                cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlMigrationBackup.RestoreAsync(target.RuntimeConnectionString, scratch, key, cancellationToken));
            await using (var context = targetFactory.CreateDbContext())
            {
                Assert.Equal("target-system", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
            }

            await RewriteManifestAsync(
                Path.Combine(scratch, key),
                manifest => manifest["selectedServer"] = "127.0.0.254:1",
                cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlMigrationBackup.RestoreAsync(source.RuntimeConnectionString, scratch, key, cancellationToken));
            await using (var context = sourceFactory.CreateDbContext())
            {
                Assert.Equal("source-system", (await context.ActivityLogs.SingleAsync(cancellationToken)).Name);
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

    private static async Task<bool> IndexExistsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var result = await context.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass('public.\"IX_ActivityLogs_Npgsql_Type_DateCreated\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
        return result;
    }

    private static async Task AssertDependencyBackupRejectedAsync(
        IDbContextFactory<JellyfinDbContext> factory,
        string connectionString,
        string scratch,
        CancellationToken cancellationToken)
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => NpgsqlMigrationBackup.CreateAsync(factory, connectionString, scratch, cancellationToken));
        Assert.Contains("dependencies across the provider-owned public schema boundary", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    private static async Task<bool> RelationExistsAsync(
        JellyfinDbContext context,
        string relation,
        CancellationToken cancellationToken)
        => await context.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass({0}) IS NOT NULL AS \"Value\"",
            relation)
            .SingleAsync(cancellationToken);

    private static async Task<(long DatabaseOid, string SystemIdentifier)> ReadServerIdentityAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new global::Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT database.oid::bigint, control.system_identifier::text FROM pg_catalog.pg_database database CROSS JOIN pg_catalog.pg_control_system() control WHERE database.datname = current_database()";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (reader.GetInt64(0), reader.GetString(1));
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

#pragma warning disable CA2101 // Unix test paths are explicitly marshalled as UTF-8.
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
#pragma warning restore CA2101

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
