using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller.SystemBackupService;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Preserves the stock backup implementation while adding provider binding and preflight validation.
/// </summary>
internal sealed class NpgsqlBackupServiceDecorator : IBackupService
{
    private readonly IBackupService inner;
    private readonly bool isActiveProvider;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlBackupServiceDecorator"/> class.</summary>
    /// <param name="inner">The exact stock Jellyfin service registered by the host.</param>
    /// <param name="provider">The selected database provider.</param>
    internal NpgsqlBackupServiceDecorator(IBackupService inner, IJellyfinDatabaseProvider provider)
    {
        this.inner = inner;
        var providerType = provider.GetType();
        this.isActiveProvider = string.Equals(
                providerType.FullName,
                typeof(NpgsqlDatabaseProvider).FullName,
                StringComparison.Ordinal)
            && providerType.Assembly.ManifestModule.ModuleVersionId
                == typeof(NpgsqlDatabaseProvider).Assembly.ManifestModule.ModuleVersionId;
    }

    /// <inheritdoc />
    public async Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
    {
        ArgumentNullException.ThrowIfNull(backupOptions);
        if (!this.isActiveProvider)
        {
            return await this.inner.CreateBackupAsync(backupOptions).ConfigureAwait(false);
        }

        using var scope = NpgsqlBackupScope.Enter();
        var manifest = await this.inner.CreateBackupAsync(backupOptions).ConfigureAwait(false);
        var homeSectionsJson = backupOptions.Database
            ? NpgsqlBackupScope.HomeSectionsJson
                ?? throw new InvalidOperationException("The PostgreSQL backup transaction did not capture HomeSection rows.")
            : "[]";
        await NpgsqlBackupArchive.BindAsync(
            manifest.Path,
            backupOptions.Database,
            homeSectionsJson,
            CancellationToken.None).ConfigureAwait(false);
        return manifest;
    }

    /// <inheritdoc />
    public Task<BackupManifestDto[]> EnumerateBackups() => this.inner.EnumerateBackups();

    /// <inheritdoc />
    public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
        => this.inner.GetBackupManifest(archivePath);

    /// <inheritdoc />
    public async Task RestoreBackupAsync(string archivePath)
    {
        if (!this.isActiveProvider)
        {
            await this.inner.RestoreBackupAsync(archivePath).ConfigureAwait(false);
            return;
        }

        using var snapshot = await NpgsqlBackupArchive.CreateValidatedSnapshotAsync(
            archivePath,
            CancellationToken.None).ConfigureAwait(false);
        using var scope = NpgsqlRestoreScope.Enter(snapshot.Path);
        await this.inner.RestoreBackupAsync(snapshot.Path).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ScheduleRestoreAndRestartServer(string archivePath)
    {
        if (this.isActiveProvider)
        {
            NpgsqlBackupArchive.ValidateAsync(archivePath, CancellationToken.None).GetAwaiter().GetResult();
        }

        this.inner.ScheduleRestoreAndRestartServer(archivePath);
    }
}
