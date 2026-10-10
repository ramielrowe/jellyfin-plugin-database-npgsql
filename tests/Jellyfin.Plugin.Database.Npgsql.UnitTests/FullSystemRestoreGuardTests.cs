using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class FullSystemRestoreGuardTests
{
    [Theory]
    [InlineData("postgresql-database.zip")]
    [InlineData("sqlite-database.zip")]
    [InlineData("unknown-database.zip")]
    [InlineData("corrupt-database-secret.zip")]
    public async Task EveryDirectRestoreIsRejectedWithoutInspectingOrDelegating(string archivePath)
    {
        var inner = new RecordingBackupService();
        var guard = new NpgsqlFullSystemRestoreGuard(inner);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => guard.RestoreBackupAsync(archivePath));

        Assert.Equal(NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage, exception.Message);
        Assert.DoesNotContain(archivePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.RestoreCalls);
        Assert.Equal(0, inner.ScheduleCalls);
    }

    [Theory]
    [InlineData("postgresql-database.zip")]
    [InlineData("sqlite-database.zip")]
    [InlineData("unknown-database.zip")]
    [InlineData("corrupt-database-secret.zip")]
    public void EveryScheduledRestoreIsRejectedWithoutRestartOrDelegation(string archivePath)
    {
        var inner = new RecordingBackupService();
        var guard = new NpgsqlFullSystemRestoreGuard(inner);

        var exception = Assert.Throws<NotSupportedException>(
            () => guard.ScheduleRestoreAndRestartServer(archivePath));

        Assert.Equal(NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage, exception.Message);
        Assert.DoesNotContain(archivePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.RestoreCalls);
        Assert.Equal(0, inner.ScheduleCalls);
    }

    [Fact]
    public async Task ReadAndBackupOperationsRemainStockOperations()
    {
        var inner = new RecordingBackupService();
        var guard = new NpgsqlFullSystemRestoreGuard(inner);
        var options = new BackupOptionsDto { Database = true };

        var created = await guard.CreateBackupAsync(options);
        var enumerated = await guard.EnumerateBackups();
        var manifest = await guard.GetBackupManifest("manifest-only.zip");

        Assert.Same(inner.Manifest, created);
        Assert.Same(inner.Manifest, Assert.Single(enumerated));
        Assert.Same(inner.Manifest, manifest);
        Assert.Equal(1, inner.CreateCalls);
        Assert.Equal(1, inner.EnumerateCalls);
        Assert.Equal(1, inner.ManifestCalls);
    }

    [Fact]
    public void EarlyProviderProofRequiresTheExactUnambiguousFinalDescriptor()
    {
        var services = new ServiceCollection();
        var guardDescriptor = ServiceDescriptor.Singleton<IBackupService>(
            new NpgsqlFullSystemRestoreGuard(new RecordingBackupService()));
        ((ICollection<ServiceDescriptor>)services).Add(guardDescriptor);
        services.AddSingleton(new NpgsqlRestoreGuardRegistration(services, guardDescriptor));
        using (var exactProvider = services.BuildServiceProvider())
        {
            NpgsqlDatabaseProvider.ValidateFullSystemRestoreGuard(exactProvider);
        }

        services.AddSingleton<IBackupService>(new RecordingBackupService());
        using var overriddenProvider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateFullSystemRestoreGuard(overriddenProvider));
        Assert.Contains("restore guard is unavailable or was replaced", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection or restore was attempted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyProviderProofFailsClosedWhenLateRegistrationIsAbsent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBackupService>(new RecordingBackupService());
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateFullSystemRestoreGuard(serviceProvider));

        Assert.Contains("disabled, deleted, malformed, absent", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EarlyProviderProofSkipsContainerWithoutAnyRestoreEntryPoint()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        NpgsqlDatabaseProvider.ValidateFullSystemRestoreGuard(serviceProvider);
    }

    [Theory]
    [InlineData("--restore-archive", true)]
    [InlineData("--restore-archive=/secret/archive.zip", true)]
    [InlineData("--RESTORE-ARCHIVE", true)]
    [InlineData("--restore-archive-other", false)]
    [InlineData("archive.zip", false)]
    public void EarlyProviderDetectsOnlyThePublicRestoreOption(string argument, bool expected)
    {
        Assert.Equal(
            expected,
            NpgsqlDatabaseProvider.HasFullSystemRestoreArgument(["jellyfin.dll", argument]));
    }

    [Fact]
    public void OrdinaryPluginMetadataMustMatchTheActiveReleaseExactly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "npgsql-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var valid = $$"""
                {
                  "guid": "{{PluginMetadata.Id}}",
                  "name": "{{PluginMetadata.Name}}",
                  "status": "Active",
                  "targetAbi": "{{PluginMetadata.SupportedDatabaseAbi}}",
                  "version": "{{PluginMetadata.Version}}",
                  "assemblies": ["{{PluginMetadata.AssemblyFileName}}"]
                }
                """;
            var metadataPath = Path.Combine(directory, "meta.json");
            File.WriteAllText(metadataPath, valid);
            NpgsqlDatabaseProvider.ValidateOrdinaryPluginMetadata(directory);

            foreach (var invalid in new[]
            {
                valid.Replace("\"Active\"", "\"Disabled\"", StringComparison.Ordinal),
                valid.Replace(PluginMetadata.Version, "99.0.0.0", StringComparison.Ordinal),
                "{ malformed metadata",
            })
            {
                File.WriteAllText(metadataPath, invalid);
                var exception = Assert.Throws<InvalidOperationException>(
                    () => NpgsqlDatabaseProvider.ValidateOrdinaryPluginMetadata(directory));
                Assert.Contains("metadata is missing, disabled, malformed", exception.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(directory, exception.Message, StringComparison.Ordinal);
            }

            File.Delete(metadataPath);
            var missing = Assert.Throws<InvalidOperationException>(
                () => NpgsqlDatabaseProvider.ValidateOrdinaryPluginMetadata(directory));
            Assert.Contains("No database connection or restore was attempted", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingBackupService : IBackupService
    {
        internal BackupManifestDto Manifest { get; } = new()
        {
            ServerVersion = new Version(12, 2),
            BackupEngineVersion = new Version(0, 2),
            DateCreated = DateTimeOffset.UnixEpoch,
            Path = "not-secret.zip",
            Options = new BackupOptionsDto(),
        };

        internal int CreateCalls { get; private set; }

        internal int EnumerateCalls { get; private set; }

        internal int ManifestCalls { get; private set; }

        internal int RestoreCalls { get; private set; }

        internal int ScheduleCalls { get; private set; }

        public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
        {
            CreateCalls++;
            return Task.FromResult(Manifest);
        }

        public Task<BackupManifestDto[]> EnumerateBackups()
        {
            EnumerateCalls++;
            return Task.FromResult(new[] { Manifest });
        }

        public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
        {
            ManifestCalls++;
            return Task.FromResult<BackupManifestDto?>(Manifest);
        }

        public Task RestoreBackupAsync(string archivePath)
        {
            RestoreCalls++;
            return Task.CompletedTask;
        }

        public void ScheduleRestoreAndRestartServer(string archivePath)
        {
            ScheduleCalls++;
        }
    }
}
