using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Preserves the stock backup implementation while adding provider binding and preflight validation.
/// </summary>
internal sealed class NpgsqlBackupServiceDecorator : IBackupService
{
    private readonly IBackupService inner;
    private readonly bool isActiveProvider;
    private readonly IServiceProvider? services;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlBackupServiceDecorator"/> class without a runtime model.</summary>
    /// <param name="inner">The exact stock Jellyfin service.</param>
    /// <param name="isActiveProvider">Whether PostgreSQL behavior is active.</param>
    internal NpgsqlBackupServiceDecorator(IBackupService inner, bool isActiveProvider)
        : this(inner, isActiveProvider, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NpgsqlBackupServiceDecorator"/> class.</summary>
    /// <param name="inner">The exact stock Jellyfin service registered by the host.</param>
    /// <param name="isActiveProvider">Whether database.xml selected the custom PostgreSQL provider.</param>
    /// <param name="services">The completed host service provider.</param>
    internal NpgsqlBackupServiceDecorator(IBackupService inner, bool isActiveProvider, IServiceProvider? services)
    {
        this.inner = inner;
        this.isActiveProvider = isActiveProvider;
        this.services = services;
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
        if (backupOptions.Database)
        {
            var homeSectionsPath = NpgsqlBackupScope.HomeSectionsSnapshotPath;
            if (string.IsNullOrWhiteSpace(homeSectionsPath) || !File.Exists(homeSectionsPath))
            {
                throw new InvalidOperationException("The PostgreSQL backup transaction did not capture HomeSection rows.");
            }

            await NpgsqlBackupArchive.BindWithHomeSectionsAsync(
                manifest.Path,
                homeSectionsPath,
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await NpgsqlBackupArchive.BindAsync(
                manifest.Path,
                databaseIncluded: false,
                CancellationToken.None).ConfigureAwait(false);
        }

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

        if (this.services is null)
        {
            await this.RestoreValidatedAsync(archivePath, null).ConfigureAwait(false);
            return;
        }

        var context = await this.services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
            .CreateDbContextAsync().ConfigureAwait(false);
        await using var configuredContext = context.ConfigureAwait(false);
        await this.RestoreValidatedAsync(archivePath, context.Model).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ScheduleRestoreAndRestartServer(string archivePath)
    {
        if (this.isActiveProvider)
        {
            using var context = this.services?
                .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
                .CreateDbContext();
            NpgsqlBackupArchive.ValidateAsync(
                archivePath,
                context?.Model,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        this.inner.ScheduleRestoreAndRestartServer(archivePath);
    }

    private async Task RestoreValidatedAsync(
        string archivePath,
        Microsoft.EntityFrameworkCore.Metadata.IModel? model)
    {
        using var snapshot = await NpgsqlBackupArchive.CreateValidatedSnapshotAsync(
            archivePath,
            model,
            CancellationToken.None).ConfigureAwait(false);
        using var scope = NpgsqlRestoreScope.Enter(snapshot.Path);
        await this.inner.RestoreBackupAsync(snapshot.Path).ConfigureAwait(false);
    }
}
