using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller.SystemBackupService;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Preserves stock backup inspection and creation while disabling the stock full-system restore path.
/// </summary>
internal sealed class NpgsqlFullSystemRestoreGuard : IBackupService
{
    /// <summary>Gets the stable, secret-free diagnostic returned by every full-system restore entry point.</summary>
    internal const string UnsupportedRestoreMessage =
        "Jellyfin full-system restore is unsupported while the Npgsql database provider is selected. "
        + "No Config, Data, Root, migration-history, PostgreSQL, or SQLite state was changed. "
        + "Use a provider migration backup only for automatic migration rollback, or migrate data with a separately reviewed operator procedure.";

    private readonly IBackupService inner;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlFullSystemRestoreGuard"/> class.</summary>
    /// <param name="inner">The stock Jellyfin backup service.</param>
    internal NpgsqlFullSystemRestoreGuard(IBackupService inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
        => this.inner.CreateBackupAsync(backupOptions);

    /// <inheritdoc />
    public Task<BackupManifestDto[]> EnumerateBackups()
        => this.inner.EnumerateBackups();

    /// <inheritdoc />
    public Task<BackupManifestDto?> GetBackupManifest(string archivePath)
        => this.inner.GetBackupManifest(archivePath);

    /// <inheritdoc />
    public Task RestoreBackupAsync(string archivePath)
        => Task.FromException(new NotSupportedException(UnsupportedRestoreMessage));

    /// <inheritdoc />
    public void ScheduleRestoreAndRestartServer(string archivePath)
        => throw new NotSupportedException(UnsupportedRestoreMessage);
}
