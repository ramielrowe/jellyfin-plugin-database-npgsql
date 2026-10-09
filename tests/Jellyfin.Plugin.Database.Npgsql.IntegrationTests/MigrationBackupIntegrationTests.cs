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

            using var faults = NpgsqlBackupFileSystem.InjectFaults(point =>
            {
                if (string.Equals(point, failurePoint, StringComparison.Ordinal))
                {
                    throw new IOException("Injected no-space-left or durable-publication failure.");
                }
            });
            await Assert.ThrowsAsync<IOException>(
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

            await context.Database.ExecuteSqlRawAsync("GRANT SELECT (\"Overview\") ON TABLE public.\"ActivityLogs\" TO PUBLIC", cancellationToken);
            await Assert.ThrowsAsync<NotSupportedException>(
                () => NpgsqlMigrationBackup.CreateAsync(factory, database.RuntimeConnectionString, scratch, cancellationToken));
            await context.Database.ExecuteSqlRawAsync("REVOKE SELECT (\"Overview\") ON TABLE public.\"ActivityLogs\" FROM PUBLIC", cancellationToken);

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
