using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class BackupArchiveTests : IDisposable
{
    private static readonly string[] DatabaseEntries =
    [
        "AccessSchedules",
        "ActivityLogs",
        "AncestorIds",
        "ApiKeys",
        "AttachmentStreamInfos",
        "BaseItemImageInfos",
        "BaseItemMetadataFields",
        "BaseItemProviders",
        "BaseItemTrailerTypes",
        "BaseItems",
        "Chapters",
        "CustomItemDisplayPreferences",
        "DeviceOptions",
        "Devices",
        "DisplayPreferences",
        "HistoryRow",
        "ImageInfos",
        "ItemDisplayPreferences",
        "ItemValues",
        "ItemValuesMap",
        "KeyframeData",
        "LinkedChildren",
        "MediaSegments",
        "MediaStreamInfos",
        "PeopleBaseItemMap",
        "Peoples",
        "Permissions",
        "Preferences",
        "TrickplayInfos",
        "UserData",
        "Users",
    ];

    private readonly string testRoot = Path.Combine(
        AppContext.BaseDirectory,
        "jellyfin-npgsql-backup-archive-tests-" + Guid.NewGuid().ToString("N"));

    public BackupArchiveTests() => Directory.CreateDirectory(this.testRoot);

    public void Dispose()
    {
        if (Directory.Exists(this.testRoot))
        {
            Directory.Delete(this.testRoot, true);
        }
    }

    [Fact]
    public async Task Binding_RoundTripsCompleteStockShapeAndRejectsChangedEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(path, true, cancellationToken);
        await NpgsqlBackupArchive.ValidateAsync(path, cancellationToken);

        using (var archive = await ZipFile.OpenAsync(path, ZipArchiveMode.Update, cancellationToken))
        {
            var entry = archive.GetEntry("Database/Users.json")!;
            entry.Delete();
            entry = archive.CreateEntry("Database/Users.json");
            await using var stream = await entry.OpenAsync(cancellationToken);
            await stream.WriteAsync("[{\"Id\":\"changed\"}]"u8.ToArray(), cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(path, cancellationToken));
        Assert.Contains("integrity binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreDecorator_RejectsUnknownAndPartialArchivesBeforeStockService()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new RecordingBackupService(this.CreateStockArchive());
        var decorator = new NpgsqlBackupServiceDecorator(inner, true);

        var unknown = await Assert.ThrowsAsync<NotSupportedException>(
            () => decorator.RestoreBackupAsync(inner.ArchivePath));
        Assert.Contains("provider binding is missing", unknown.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.RestoreCalls);

        var partial = this.CreateStockArchive(omit: "Users");
        var bindingFailure = await Assert.ThrowsAsync<NotSupportedException>(
            () => NpgsqlBackupArchive.BindAsync(partial, true, cancellationToken));
        Assert.Contains("missing [Database/Users.json]", bindingFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(this.testRoot),
            candidate => candidate.EndsWith(".npgsql-binding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDecorator_UsesStockServiceThenAddsExactProviderBinding()
    {
        var inner = new RecordingBackupService(this.CreateStockArchive());
        var decorator = new NpgsqlBackupServiceDecorator(inner, true);

        var manifest = await decorator.CreateBackupAsync(new BackupOptionsDto { Database = true });

        Assert.Equal(1, inner.CreateCalls);
        Assert.Equal(inner.ArchivePath, manifest.Path);
        await NpgsqlBackupArchive.ValidateAsync(manifest.Path, TestContext.Current.CancellationToken);
        using var archive = await ZipFile.OpenReadAsync(manifest.Path, TestContext.Current.CancellationToken);
        Assert.NotNull(archive.GetEntry(NpgsqlBackupArchive.MarkerEntryName));
    }

    [Theory]
    [InlineData("\"Provider\":\"Npgsql Database\"", "\"Provider\":\"SQLite\"")]
    [InlineData("\"PluginVersion\":\"0.1.0.0\"", "\"PluginVersion\":\"99.0.0.0\"")]
    [InlineData("\"DatabaseAbi\":\"12.2.0.0\"", "\"DatabaseAbi\":\"13.0.0.0\"")]
    public async Task Validation_RejectsWrongProviderOrVersionBinding(string current, string replacement)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(path, true, cancellationToken);
        await ReplaceEntryAsync(
            path,
            NpgsqlBackupArchive.MarkerEntryName,
            value => value.Replace(current, replacement, StringComparison.Ordinal),
            cancellationToken);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => NpgsqlBackupArchive.ValidateAsync(path, cancellationToken));
        Assert.Contains("provider/version binding is not exactly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsTruncationAndCancellationLeavesNoScratchFile()
    {
        var path = this.CreateStockArchive();
        var originalHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(File.OpenRead(path), TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NpgsqlBackupArchive.BindAsync(path, true, canceled.Token));
        Assert.Equal(originalHash, Convert.ToHexStringLower(await SHA256.HashDataAsync(File.OpenRead(path), TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(this.testRoot),
            candidate => candidate.EndsWith(".npgsql-binding", StringComparison.Ordinal));

        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(32);
        }

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidatedSnapshot_IsMarkerOwnedAndDeletesOnlyItsOwnFile()
    {
        var path = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);

        string snapshotPath;
        using (var snapshot = await NpgsqlBackupArchive.CreateValidatedSnapshotAsync(
            path,
            TestContext.Current.CancellationToken))
        {
            snapshotPath = snapshot.Path;
            Assert.StartsWith(this.testRoot, snapshotPath, StringComparison.Ordinal);
            Assert.StartsWith(".jellyfin-npgsql-restore-", Path.GetFileName(snapshotPath), StringComparison.Ordinal);
            Assert.True(File.Exists(snapshotPath));
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(snapshotPath));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task IdentityHydration_RejectsDuplicateRestoredIdsBeforeSave()
    {
        var path = this.CreateStockArchive(activityLogsJson: "[{\"Id\":42},{\"Id\":42}]");
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var context = CreateContext();
        context.ActivityLogs.Add(new ActivityLog("first", "BackupRestore", Guid.Empty));
        context.ActivityLogs.Add(new ActivityLog("second", "BackupRestore", Guid.Empty));

        using var scope = NpgsqlRestoreScope.Enter(path);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlRestoreIdentityHydrator.HydrateAsync(context, TestContext.Current.CancellationToken));
        Assert.Contains("duplicate 'Id' value '42'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelPreflight_AcceptsExactEmptyModelAndHistory()
    {
        var path = this.CreateStockArchive(historyJson: CreateExactHistoryJson());
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var context = CreateContext();

        await NpgsqlBackupArchive.ValidateAsync(
            path,
            context.Model,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void EarlyProvider_RequiresExactBackupDecoratorBeforeDatabaseAccess()
    {
        var stock = new RecordingBackupService(this.CreateStockArchive());
        var unsafeServices = new ServiceCollection()
            .AddSingleton<IBackupService>(stock)
            .BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateBackupSafetyDecorator(unsafeServices));
        Assert.Contains("refusing to start", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No restore or database connection was attempted", exception.Message, StringComparison.Ordinal);

        var safeCollection = new ServiceCollection();
        var safeDescriptor = ServiceDescriptor.Singleton<IBackupService>(
            new NpgsqlBackupServiceDecorator(stock, true));
        ((ICollection<ServiceDescriptor>)safeCollection).Add(safeDescriptor);
        safeCollection.AddSingleton(new NpgsqlBackupSafetyRegistration(safeCollection, safeDescriptor));
        var safeServices = safeCollection.BuildServiceProvider();
        NpgsqlDatabaseProvider.ValidateBackupSafetyDecorator(safeServices);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("duplicate-identity")]
    public async Task ModelPreflight_RejectsMalformedIdentityRowsBeforeRestore(string corruption)
    {
        var activity = new ActivityLog("preflight", "BackupRestore", Guid.Empty);
        using var context = CreateContext();
        context.Entry(activity).Property(nameof(ActivityLog.Id)).CurrentValue = 42;
        var validJson = JsonSerializer.Serialize(new[] { activity });
        var wrongType = JsonNode.Parse(validJson)!;
        wrongType.AsArray()[0]![nameof(ActivityLog.Id)] = "wrong";
        var activityJson = corruption switch
        {
            "missing" => "[{}]",
            "wrong-type" => wrongType.ToJsonString(),
            "duplicate-identity" => "[" + validJson[1..^1] + "," + validJson[1..^1] + "]",
            _ => throw new InvalidOperationException(),
        };
        var path = this.CreateStockArchive(
            activityLogsJson: activityJson,
            historyJson: CreateExactHistoryJson());
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(
                path,
                context.Model,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModelPreflight_RejectsDuplicateNonIdentityKeyBeforeRestore()
    {
        var user = new User("duplicate-user", "auth", "reset");
        var serialized = JsonSerializer.Serialize(user);
        var path = this.CreateStockArchive(
            usersJson: $"[{serialized},{serialized}]",
            historyJson: CreateExactHistoryJson());
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(
                path,
                context.Model,
                TestContext.Current.CancellationToken));
        Assert.Contains("duplicate key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelPreflight_RejectsDuplicateUniqueValueBeforeRestore()
    {
        var first = new User("duplicate-unique-user", "auth", "reset");
        var second = new User("duplicate-unique-user", "auth", "reset");
        var path = this.CreateStockArchive(
            usersJson: JsonSerializer.Serialize(new[] { first, second }),
            historyJson: CreateExactHistoryJson());
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(
                path,
                context.Model,
                TestContext.Current.CancellationToken));
        Assert.Contains("duplicate unique value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelPreflight_RejectsUnresolvedForeignKeyBeforeRestore()
    {
        var permission = new Permission(PermissionKind.EnableMediaPlayback, true)
        {
            UserId = Guid.NewGuid(),
        };
        using var context = CreateContext();
        context.Entry(permission).Property(nameof(Permission.Id)).CurrentValue = 73;
        var path = this.CreateStockArchive(
            historyJson: CreateExactHistoryJson(),
            permissionsJson: JsonSerializer.Serialize(new[] { permission }));
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(
                path,
                context.Model,
                TestContext.Current.CancellationToken));
        Assert.Contains("unresolved relationship", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsCompressionBombAndEntryCountBeforeReadingPayloads()
    {
        var compressedBomb = this.CreateStockArchive(
            usersJson: "[" + new string(' ', 2 * 1024 * 1024) + "]");
        var ratioFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.BindAsync(compressedBomb, true, TestContext.Current.CancellationToken));
        Assert.Contains("compression ratio", ratioFailure.Message, StringComparison.Ordinal);

        var tooManyEntries = Path.Combine(this.testRoot, "too-many-entries.zip");
        using (var archive = ZipFile.Open(tooManyEntries, ZipArchiveMode.Create))
        {
            for (var index = 0; index <= NpgsqlBackupArchive.MaximumEntryCount; index++)
            {
                _ = archive.CreateEntry($"empty/{index}.txt");
            }
        }

        var countFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(tooManyEntries, TestContext.Current.CancellationToken));
        Assert.Contains("entry-count", countFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsOversizedMarkerBeforeDeserialization()
    {
        var path = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry(NpgsqlBackupArchive.MarkerEntryName)!.Delete();
            var marker = archive.CreateEntry(NpgsqlBackupArchive.MarkerEntryName, CompressionLevel.NoCompression);
            using var stream = marker.Open();
            var buffer = new byte[65536];
            var remaining = NpgsqlBackupArchive.MaximumMarkerBytes + 1;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                stream.Write(buffer, 0, count);
                remaining -= count;
            }
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.ValidateAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("marker exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binding_RejectsExcessiveJsonDepthAndStringLength()
    {
        var depth = this.CreateStockArchive(
            usersJson: "[{\"Unknown\":" + new string('[', 65) + "0" + new string(']', 65) + "}]",
            databaseCompression: CompressionLevel.NoCompression);
        var depthFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.BindAsync(depth, true, TestContext.Current.CancellationToken));
        Assert.Contains("malformed", depthFailure.Message, StringComparison.Ordinal);

        var oversizedString = this.CreateStockArchive(
            usersJson: "[{\"Unknown\":\"" + new string('x', NpgsqlBackupArchive.MaximumJsonStringLength + 1) + "\"}]",
            databaseCompression: CompressionLevel.NoCompression);
        var stringFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.BindAsync(oversizedString, true, TestContext.Current.CancellationToken));
        Assert.Contains("oversized string", stringFailure.Message, StringComparison.Ordinal);
    }

    private string CreateStockArchive(
        string? omit = null,
        string usersJson = "[]",
        string activityLogsJson = "[]",
        string historyJson = "[]",
        string permissionsJson = "[]",
        CompressionLevel databaseCompression = CompressionLevel.Optimal)
    {
        var path = Path.Combine(this.testRoot, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in DatabaseEntries.Where(name => !string.Equals(name, omit, StringComparison.Ordinal)))
        {
            var entry = archive.CreateEntry("Database/" + name + ".json", databaseCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(name switch
            {
                "Users" => usersJson,
                "ActivityLogs" => activityLogsJson,
                "HistoryRow" => historyJson,
                "Permissions" => permissionsJson,
                _ => "[]",
            });
        }

        var manifest = archive.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifest.Open()))
        {
            writer.Write("{\"ServerVersion\":\"12.2.0.0\",\"BackupEngineVersion\":\"0.2.0\",\"DateCreated\":\"2026-10-09T00:00:00Z\",\"DatabaseTables\":[],\"Options\":{\"Database\":true}}");
        }

        return path;
    }

    private static string CreateExactHistoryJson()
        => JsonSerializer.Serialize(
            NpgsqlHostMigrationAudit.AuditedMigrationIds
                .Concat(
                [
                    "20261009061101_InitialPostgreSqlBaseline",
                    "20261009061446_AddActivityLogTypeDateIndex",
                    "20261009113458_AddUuidMinMaxAggregates",
                ])
                .Select(id => new { MigrationId = id, ProductVersion = PluginMetadata.SupportedDatabaseAbi }));

    private static JellyfinDbContext CreateContext()
    {
        const string connectionString =
            "Host=127.0.0.1;Port=1;Database=backup_contract;Username=backup_contract;Password=not-used;Pooling=false;Timeout=1";
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(options, connectionString, null);
        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task ReplaceEntryAsync(
        string archivePath,
        string entryPath,
        Func<string, string> replace,
        CancellationToken cancellationToken)
    {
        using var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, cancellationToken);
        var entry = archive.GetEntry(entryPath)!;
        string value;
        var readStream = await entry.OpenAsync(cancellationToken);
        await using (readStream.ConfigureAwait(false))
        using (var reader = new StreamReader(readStream))
        {
            value = await reader.ReadToEndAsync(cancellationToken);
        }

        entry.Delete();
        entry = archive.CreateEntry(entryPath);
        var writeStream = await entry.OpenAsync(cancellationToken);
        await using (writeStream.ConfigureAwait(false))
        await using (var writer = new StreamWriter(writeStream))
        {
            await writer.WriteAsync(replace(value).AsMemory(), cancellationToken);
        }
    }

    private sealed class RecordingBackupService(string archivePath) : IBackupService
    {
        internal string ArchivePath { get; } = archivePath;

        internal int CreateCalls { get; private set; }

        internal int RestoreCalls { get; private set; }

        public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
        {
            this.CreateCalls++;
            if (NpgsqlBackupScope.IsActive)
            {
                var snapshotPath = NpgsqlBackupScope.RequireHomeSectionsSnapshotPath();
                File.WriteAllText(snapshotPath, "[]");
            }

            return Task.FromResult(new BackupManifestDto
            {
                ServerVersion = new Version(12, 2),
                BackupEngineVersion = new Version(0, 2),
                DateCreated = DateTimeOffset.UtcNow,
                Path = this.ArchivePath,
                Options = backupOptions,
            });
        }

        public Task<BackupManifestDto[]> EnumerateBackups()
            => Task.FromResult(Array.Empty<BackupManifestDto>());

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
            => Task.FromResult<BackupManifestDto?>(null);

        public Task RestoreBackupAsync(string archivePath)
        {
            this.RestoreCalls++;
            return Task.CompletedTask;
        }

        public void ScheduleRestoreAndRestartServer(string archivePath) => this.RestoreCalls++;
    }
}
