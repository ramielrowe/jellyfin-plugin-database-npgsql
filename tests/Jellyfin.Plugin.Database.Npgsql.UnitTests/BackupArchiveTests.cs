using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    public async Task Validation_RequiresExactAgreementBetweenMarkerManifestAndDatabaseEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var included = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(included, true, cancellationToken);
        await ReplaceEntryAsync(
            included,
            NpgsqlBackupArchive.MarkerEntryName,
            value => value.Replace("\"DatabaseIncluded\":true", "\"DatabaseIncluded\":false", StringComparison.Ordinal),
            cancellationToken);
        var falseMarker = await Assert.ThrowsAsync<NotSupportedException>(
            () => NpgsqlBackupArchive.ValidateAsync(included, cancellationToken));
        Assert.Contains("binding and stock manifest disagree", falseMarker.Message, StringComparison.Ordinal);

        var excluded = this.CreateStockArchive(databaseIncluded: false);
        await NpgsqlBackupArchive.BindAsync(excluded, false, cancellationToken);
        await ReplaceEntryAsync(
            excluded,
            NpgsqlBackupArchive.MarkerEntryName,
            value => value.Replace("\"DatabaseIncluded\":false", "\"DatabaseIncluded\":true", StringComparison.Ordinal),
            cancellationToken);
        var trueMarker = await Assert.ThrowsAsync<NotSupportedException>(
            () => NpgsqlBackupArchive.ValidateAsync(excluded, cancellationToken));
        Assert.Contains("binding and stock manifest disagree", trueMarker.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-entry")]
    [InlineData("duplicate-options")]
    [InlineData("duplicate-database")]
    [InlineData("wrong-type")]
    public async Task Validation_RejectsMissingDuplicateOrAmbiguousStockManifest(string corruption)
    {
        var path = this.CreateStockArchive();
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var archive = await ZipFile.OpenAsync(path, ZipArchiveMode.Update, cancellationToken))
        {
            var manifest = archive.GetEntry("manifest.json")!;
            if (corruption is "missing")
            {
                manifest.Delete();
            }
            else if (corruption is "duplicate-entry")
            {
                var duplicate = archive.CreateEntry("manifest.json");
                await using var writer = new StreamWriter(await duplicate.OpenAsync(cancellationToken));
                await writer.WriteAsync("{}".AsMemory(), cancellationToken);
            }
            else
            {
                manifest.Delete();
                var replacement = archive.CreateEntry("manifest.json");
                await using var writer = new StreamWriter(await replacement.OpenAsync(cancellationToken));
                await writer.WriteAsync((corruption switch
                {
                    "duplicate-options" => "{\"Options\":{\"Database\":true},\"Options\":{\"Database\":true}}",
                    "duplicate-database" => "{\"Options\":{\"Database\":true,\"Database\":true}}",
                    "wrong-type" => "{\"Options\":{\"Database\":\"true\"}}",
                    _ => throw new InvalidOperationException(),
                }).AsMemory(), cancellationToken);
            }
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => NpgsqlBackupArchive.BindAsync(path, true, cancellationToken));
    }

    public static TheoryData<string> UnsafeZipNames => new()
    {
        "Config/a",
        "Config//a",
        "Config/./a",
        "Config\\a",
        "Config/a:stream",
        "Config/a. ",
    };

    [Theory]
    [MemberData(nameof(UnsafeZipNames))]
    public async Task Binding_RejectsStockExtractionAliasesBeforeMutation(string alias)
    {
        var path = this.CreateStockArchive();
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            _ = archive.CreateEntry("Config/a");
            _ = archive.CreateEntry(alias);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken));
        Assert.Contains(
            alias == "Config/a" ? "duplicate entry" : "unsafe entry name",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Binding_RejectsCaseUnicodeAndFileDirectoryAliasesBeforeMutation()
    {
        var aliases = new[]
        {
            new[] { "Config/Case.json", "config/case.json" },
            new[] { "Config/caf\u00e9.json", "Config/cafe\u0301.json" },
            new[] { "Config/node", "Config/node/child.json" },
        };
        foreach (var pair in aliases)
        {
            var path = this.CreateStockArchive();
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                _ = archive.CreateEntry(pair[0]);
                _ = archive.CreateEntry(pair[1]);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken));
            Assert.True(
                exception.Message.Contains("collision", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("alias", StringComparison.OrdinalIgnoreCase));
        }
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
        UnixFileMode? originalMode = null;
        if (!OperatingSystem.IsWindows())
        {
            originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(path, originalMode.Value);
        }

        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(originalMode, File.GetUnixFileMode(path));
        }

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
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(snapshotPath));
            }
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
    public async Task RestoreFilesystemSnapshot_CompensatesExactFilesModesAndCreatedDirectories()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new TestApplicationPaths(Path.Combine(this.testRoot, "filesystem"));
        paths.CreateDirectories();
        var existing = Path.Combine(paths.ConfigurationDirectoryPath, "system.xml");
        await File.WriteAllTextAsync(existing, "before", cancellationToken);
        var originalDirectoryTime = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(paths.ConfigurationDirectoryPath, originalDirectoryTime);
        UnixFileMode? originalMode = null;
        UnixFileMode? originalDirectoryMode = null;
        if (!OperatingSystem.IsWindows())
        {
            originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(existing, originalMode.Value);
            originalDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(paths.ConfigurationDirectoryPath, originalDirectoryMode.Value);
        }

        var archivePath = Path.Combine(this.testRoot, "filesystem.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var manifestWriter = new StreamWriter(manifest.Open()))
            {
                manifestWriter.Write("{\"Options\":{\"Database\":true}}");
            }

            _ = archive.CreateEntry("Config/system.xml");
            _ = archive.CreateEntry("Data/new/created.json");
            _ = archive.CreateEntry("Root/root.json");
            _ = archive.CreateEntry("Data/metadata/internal.json");
            _ = archive.CreateEntry("Data/metadata-default/default.json");
        }

        await using (var snapshot = await NpgsqlRestoreFileSnapshot.CaptureAsync(
            archivePath,
            paths,
            cancellationToken))
        {
            await File.WriteAllTextAsync(existing, "after", cancellationToken);
            Directory.CreateDirectory(Path.Combine(paths.DataPath, "new"));
            await File.WriteAllTextAsync(Path.Combine(paths.DataPath, "new", "created.json"), "created", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(paths.RootFolderPath, "root.json"), "created", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(paths.InternalMetadataPath, "internal.json"), "created", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultInternalMetadataPath, "default.json"), "created", cancellationToken);

            await snapshot.RollbackAsync(cancellationToken);
        }

        Assert.Equal("before", await File.ReadAllTextAsync(existing, cancellationToken));
        if (!OperatingSystem.IsWindows() && originalMode is { } mode)
        {
            Assert.Equal(mode, File.GetUnixFileMode(existing));
            Assert.Equal(originalDirectoryMode, File.GetUnixFileMode(paths.ConfigurationDirectoryPath));
        }

        Assert.Equal(originalDirectoryTime, Directory.GetLastWriteTimeUtc(paths.ConfigurationDirectoryPath));

        Assert.False(Directory.Exists(Path.Combine(paths.DataPath, "new")));
        Assert.False(File.Exists(Path.Combine(paths.RootFolderPath, "root.json")));
        Assert.False(File.Exists(Path.Combine(paths.InternalMetadataPath, "internal.json")));
        Assert.False(File.Exists(Path.Combine(paths.DefaultInternalMetadataPath, "default.json")));
    }

    [Fact]
    public async Task RestoreFilesystemSnapshot_RejectsSymlinkAndHardLinkTargets()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix link safety contract.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new TestApplicationPaths(Path.Combine(this.testRoot, "links"));
        paths.CreateDirectories();
        var archivePath = Path.Combine(this.testRoot, "links.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("Config/linked.xml");
        }

        var outside = Path.Combine(this.testRoot, "outside.xml");
        await File.WriteAllTextAsync(outside, "outside", cancellationToken);
        var target = Path.Combine(paths.ConfigurationDirectoryPath, "linked.xml");
        File.CreateSymbolicLink(target, outside);
        var symbolic = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlRestoreFileSnapshot.CaptureAsync(archivePath, paths, cancellationToken));
        Assert.Contains("symbolic/reparse", symbolic.Message, StringComparison.Ordinal);
        File.Delete(target);

        Assert.Equal(0, CreateHardLink(outside, target));
        var hard = await Assert.ThrowsAsync<InvalidDataException>(
            () => NpgsqlRestoreFileSnapshot.CaptureAsync(archivePath, paths, cancellationToken));
        Assert.Contains("hard links", hard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreFilesystemSnapshot_RejectsRootSwapAndNeverMutatesOutsideTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix root-swap contract.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new TestApplicationPaths(Path.Combine(this.testRoot, "root-swap"));
        paths.CreateDirectories();
        var archivePath = Path.Combine(this.testRoot, "root-swap.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using (var manifestWriter = new StreamWriter(manifest.Open()))
            {
                await manifestWriter.WriteAsync("{\"Options\":{\"Database\":true}}".AsMemory(), cancellationToken);
            }

            var entry = archive.CreateEntry("Config/system.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("restored".AsMemory(), cancellationToken);
        }

        var outside = Path.Combine(this.testRoot, "outside-root");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "system.xml");
        await File.WriteAllTextAsync(outsideFile, "outside", cancellationToken);
        var movedRoot = paths.ConfigurationDirectoryPath + ".original";
        await using (var snapshot = await NpgsqlRestoreFileSnapshot.CaptureAsync(
            archivePath,
            paths,
            cancellationToken))
        {
            Directory.Move(paths.ConfigurationDirectoryPath, movedRoot);
            Directory.CreateSymbolicLink(paths.ConfigurationDirectoryPath, outside);
            var failure = await Assert.ThrowsAsync<InvalidDataException>(
                () => snapshot.ApplyAsync(cancellationToken));
            Assert.Contains("symbolic/reparse", failure.Message, StringComparison.Ordinal);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile, cancellationToken));
            Directory.Delete(paths.ConfigurationDirectoryPath);
            Directory.Move(movedRoot, paths.ConfigurationDirectoryPath);
        }
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    public async Task RestoreFilesystemSnapshot_FinalTargetSwapCannotRedirectMutation(string swapKind)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux handle-relative replacement contract.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new TestApplicationPaths(Path.Combine(this.testRoot, "target-swap-" + swapKind));
        paths.CreateDirectories();
        var target = Path.Combine(paths.ConfigurationDirectoryPath, "system.xml");
        await File.WriteAllTextAsync(target, "before", cancellationToken);
        var outside = Path.Combine(this.testRoot, "outside-" + swapKind + ".xml");
        await File.WriteAllTextAsync(outside, "outside", cancellationToken);
        var archivePath = Path.Combine(this.testRoot, "target-swap-" + swapKind + ".zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using (var manifestWriter = new StreamWriter(manifest.Open()))
            {
                await manifestWriter.WriteAsync("{\"Options\":{\"Database\":true}}".AsMemory(), cancellationToken);
            }

            var entry = archive.CreateEntry("Config/system.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("restored".AsMemory(), cancellationToken);
        }

        await using var snapshot = await NpgsqlRestoreFileSnapshot.CaptureAsync(
            archivePath,
            paths,
            cancellationToken);
        File.Delete(target);
        if (string.Equals(swapKind, "symlink", StringComparison.Ordinal))
        {
            File.CreateSymbolicLink(target, outside);
        }
        else
        {
            Assert.Equal(0, CreateHardLink(outside, target));
        }

        await snapshot.ApplyAsync(cancellationToken);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside, cancellationToken));
        Assert.Equal("restored", await File.ReadAllTextAsync(target, cancellationToken));

        await snapshot.RollbackAsync(cancellationToken);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside, cancellationToken));
        Assert.Equal("before", await File.ReadAllTextAsync(target, cancellationToken));
    }

    [Fact]
    public async Task Validation_AlreadyCanceledDoesNotOpenOrCopyArchive()
    {
        var path = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = Directory.GetFiles(this.testRoot, ".jellyfin-npgsql-restore-*.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NpgsqlBackupArchive.CreateValidatedSnapshotAsync(path, cancellation.Token));

        Assert.Equal(before, Directory.GetFiles(this.testRoot, ".jellyfin-npgsql-restore-*.zip"));
    }

    [Fact]
    public async Task ProviderGate_SerializesRestoreAgainstBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var restorePath = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(restorePath, true, cancellationToken);
        var createPath = this.CreateStockArchive(databaseIncluded: false);
        var inner = new BlockingBackupService(restorePath, createPath);
        var decorator = new NpgsqlBackupServiceDecorator(inner, true);

        var restore = decorator.RestoreBackupAsync(restorePath);
        await inner.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var backup = decorator.CreateBackupAsync(new BackupOptionsDto { Database = false });
        var prematureBackup = await Task.WhenAny(
            inner.BackupEntered.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken));
        Assert.NotSame(inner.BackupEntered.Task, prematureBackup);

        inner.Release.TrySetResult();
        await Task.WhenAll(restore, backup).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(1, inner.MaximumConcurrency);
    }

    [Fact]
    public async Task ProviderGate_RemainsHeldThroughScheduledShutdownWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var restorePath = this.CreateStockArchive();
        await NpgsqlBackupArchive.BindAsync(restorePath, true, cancellationToken);
        var createPath = this.CreateStockArchive(databaseIncluded: false);
        var inner = new ScheduledBackupService(createPath);
        var decorator = new NpgsqlBackupServiceDecorator(inner, true);

        decorator.ScheduleRestoreAndRestartServer(restorePath);
        var backup = decorator.CreateBackupAsync(new BackupOptionsDto { Database = false });
        var premature = await Task.WhenAny(
            backup,
            Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken));
        Assert.NotSame(backup, premature);
        _ = await backup.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert.Equal(1, inner.ScheduleCalls);
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

    [Theory]
    [InlineData("migration-too-long")]
    [InlineData("version-too-long")]
    [InlineData("version-wrong-type")]
    [InlineData("version-null")]
    [InlineData("version-arbitrary")]
    [InlineData("wrong-order")]
    public async Task ModelPreflight_RejectsInvalidExactHistoryContract(string corruption)
    {
        var rows = JsonNode.Parse(CreateExactHistoryJson())!.AsArray();
        switch (corruption)
        {
            case "migration-too-long":
                rows[0]!["MigrationId"] = "20260101000000_" + new string('x', 136);
                break;
            case "version-too-long":
                rows[0]!["ProductVersion"] = new string('1', 33);
                break;
            case "version-wrong-type":
                rows[0]!["ProductVersion"] = 122;
                break;
            case "version-null":
                rows[0]!["ProductVersion"] = null;
                break;
            case "version-arbitrary":
                rows[0]!["ProductVersion"] = "12.2.0.1";
                break;
            case "wrong-order":
                var first = rows[0]!.DeepClone();
                var second = rows[1]!.DeepClone();
                rows[0] = second;
                rows[1] = first;
                break;
            default:
                throw new InvalidOperationException();
        }

        var path = this.CreateStockArchive(historyJson: rows.ToJsonString());
        await NpgsqlBackupArchive.BindAsync(path, true, TestContext.Current.CancellationToken);
        using var context = CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(
            () => NpgsqlBackupArchive.ValidateAsync(
                path,
                context.Model,
                TestContext.Current.CancellationToken));
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
    public async Task ModelPreflight_LargeUniqueDatasetKeepsManagedRetentionBoundedAndCleansIndex()
    {
        const int rowCount = 100000;
        const long maximumRetainedManagedBytes = 64L * 1024 * 1024;
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = this.CreateStockArchive(historyJson: CreateExactHistoryJson());
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("Database/Users.json")!.Delete();
            var entry = archive.CreateEntry("Database/Users.json", CompressionLevel.Fastest);
            await using var stream = entry.Open();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartArray();
            for (var index = 0; index < rowCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonSerializer.Serialize(
                    writer,
                    new User(
                        "bounded-validation-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "auth",
                        "reset"));
            }

            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken);
        }

        await NpgsqlBackupArchive.BindAsync(path, true, cancellationToken);
        using var context = CreateContext();
        var scratchBefore = Directory.GetDirectories(
            Path.GetTempPath(),
            ".jellyfin-npgsql-validation-*",
            SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.Ordinal);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);

        await NpgsqlBackupArchive.ValidateAsync(path, context.Model, cancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedGrowth = GC.GetTotalMemory(forceFullCollection: true) - managedBefore;
        Assert.True(
            retainedGrowth < maximumRetainedManagedBytes,
            $"Validation retained {retainedGrowth} managed bytes for {rowCount} rows.");
        Assert.Empty(Directory.GetDirectories(
            Path.GetTempPath(),
            ".jellyfin-npgsql-validation-*",
            SearchOption.TopDirectoryOnly).Except(scratchBefore, StringComparer.Ordinal));
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
        CompressionLevel databaseCompression = CompressionLevel.Optimal,
        bool databaseIncluded = true)
    {
        var path = Path.Combine(this.testRoot, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in DatabaseEntries.Where(name => databaseIncluded && !string.Equals(name, omit, StringComparison.Ordinal)))
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
            writer.Write($"{{\"ServerVersion\":\"12.2.0.0\",\"BackupEngineVersion\":\"0.2.0\",\"DateCreated\":\"2026-10-09T00:00:00Z\",\"DatabaseTables\":[],\"Options\":{{\"Database\":{databaseIncluded.ToString().ToLowerInvariant()}}}}}");
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
                .Select(id => new
                {
                    MigrationId = id,
                    ProductVersion = id.StartsWith("20261009", StringComparison.Ordinal)
                        ? ProductInfo.GetVersion()
                        : PluginMetadata.SupportedDatabaseAbi,
                }));

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

#pragma warning disable CA2101 // LPUTF8Str explicitly matches Linux filesystem path encoding.
#pragma warning disable SYSLIB1054 // Test-only libc probe avoids enabling unsafe source-generated interop.
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CreateHardLink(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101

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

    private sealed class BlockingBackupService(string restorePath, string createPath) : IBackupService
    {
        private int concurrency;
        private int maximumConcurrency;

        internal TaskCompletionSource RestoreEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource BackupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int MaximumConcurrency => this.maximumConcurrency;

        public async Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
        {
            await this.EnterAsync(this.BackupEntered);
            return new BackupManifestDto
            {
                ServerVersion = new Version(12, 2),
                BackupEngineVersion = new Version(0, 2),
                DateCreated = DateTimeOffset.UtcNow,
                Path = createPath,
                Options = backupOptions,
            };
        }

        public Task<BackupManifestDto[]> EnumerateBackups()
            => Task.FromResult(Array.Empty<BackupManifestDto>());

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
            => Task.FromResult<BackupManifestDto?>(null);

        public async Task RestoreBackupAsync(string archivePath)
        {
            Assert.NotEqual(restorePath, archivePath);
            await this.EnterAsync(this.RestoreEntered);
        }

        public void ScheduleRestoreAndRestartServer(string archivePath)
            => throw new NotSupportedException();

        private async Task EnterAsync(TaskCompletionSource entered)
        {
            var current = Interlocked.Increment(ref this.concurrency);
            var observed = Volatile.Read(ref this.maximumConcurrency);
            while (current > observed)
            {
                var exchanged = Interlocked.CompareExchange(ref this.maximumConcurrency, current, observed);
                if (exchanged == observed)
                {
                    break;
                }

                observed = exchanged;
            }

            entered.TrySetResult();
            try
            {
                await this.Release.Task;
            }
            finally
            {
                _ = Interlocked.Decrement(ref this.concurrency);
            }
        }
    }

    private sealed class ScheduledBackupService(string createPath) : IBackupService
    {
        internal int ScheduleCalls { get; private set; }

        public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
            => Task.FromResult(new BackupManifestDto
            {
                ServerVersion = new Version(12, 2),
                BackupEngineVersion = new Version(0, 2),
                DateCreated = DateTimeOffset.UtcNow,
                Path = createPath,
                Options = backupOptions,
            });

        public Task<BackupManifestDto[]> EnumerateBackups()
            => Task.FromResult(Array.Empty<BackupManifestDto>());

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
            => Task.FromResult<BackupManifestDto?>(null);

        public Task RestoreBackupAsync(string archivePath)
            => throw new NotSupportedException();

        public void ScheduleRestoreAndRestartServer(string archivePath) => this.ScheduleCalls++;
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
}
