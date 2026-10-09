using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Preserves the stock backup implementation while adding provider binding and preflight validation.
/// </summary>
internal sealed class NpgsqlBackupServiceDecorator : IBackupService
{
    private static readonly SemaphoreSlim ProviderOperationGate = new(1, 1);

    private static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(10);

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

        using var deadline = new CancellationTokenSource(OperationDeadline);
        await ProviderOperationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
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
                    deadline.Token).ConfigureAwait(false);
            }
            else
            {
                await NpgsqlBackupArchive.BindAsync(
                    manifest.Path,
                    databaseIncluded: false,
                    deadline.Token).ConfigureAwait(false);
            }

            return manifest;
        }
        finally
        {
            ProviderOperationGate.Release();
        }
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

        await this.RestoreValidatedAsync(archivePath, null).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ScheduleRestoreAndRestartServer(string archivePath)
    {
        using var deadline = new CancellationTokenSource(OperationDeadline);
        if (!this.isActiveProvider)
        {
            this.inner.ScheduleRestoreAndRestartServer(archivePath);
            return;
        }

        ProviderOperationGate.Wait(deadline.Token);
        try
        {
            using var context = this.services?
                .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
                .CreateDbContext();
            NpgsqlBackupArchive.ValidateAsync(
                archivePath,
                context?.Model,
                deadline.Token).GetAwaiter().GetResult();
            this.inner.ScheduleRestoreAndRestartServer(archivePath);
        }
        finally
        {
            ProviderOperationGate.Release();
        }
    }

    private static async Task RestoreHistoryAsync(
        IDbContextFactory<JellyfinDbContext> contextFactory,
        IReadOnlyList<HistoryRow> originalHistory,
        CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var repository = context.GetService<IHistoryRepository>();
            await repository.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var current in await repository.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await context.Database.ExecuteSqlRawAsync(
                        repository.GetDeleteScript(current.MigrationId),
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var original in originalHistory)
                {
                    await context.Database.ExecuteSqlRawAsync(
                        repository.GetInsertScript(original),
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RestoreValidatedAsync(
        string archivePath,
        Microsoft.EntityFrameworkCore.Metadata.IModel? model)
    {
        using var deadline = new CancellationTokenSource(OperationDeadline);
        await ProviderOperationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            IDbContextFactory<JellyfinDbContext>? contextFactory = null;
            IReadOnlyList<HistoryRow>? originalHistory = null;
            if (this.services is not null)
            {
                contextFactory = this.services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
                var context = await contextFactory.CreateDbContextAsync(deadline.Token).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    model = context.Model;
                    originalHistory = (await context.GetService<IHistoryRepository>()
                        .GetAppliedMigrationsAsync(deadline.Token).ConfigureAwait(false))
                        .Select(row => new HistoryRow(row.MigrationId, row.ProductVersion))
                        .ToArray();
                }
            }

            using var snapshot = await NpgsqlBackupArchive.CreateValidatedSnapshotAsync(
                archivePath,
                model,
                deadline.Token).ConfigureAwait(false);
            var applicationPaths = this.services?.GetService<IServerApplicationPaths>();
#pragma warning disable CA2007 // The owned async-disposable is explicitly configured at every operation await.
            await using var fileSnapshot = applicationPaths is null
                ? null
                : await NpgsqlRestoreFileSnapshot.CaptureAsync(
                    snapshot.Path,
                    applicationPaths,
                    deadline.Token).ConfigureAwait(false);
#pragma warning restore CA2007
            var innerStarted = false;
            try
            {
                using var scope = NpgsqlRestoreScope.Enter(snapshot.Path);
                innerStarted = true;
                await this.inner.RestoreBackupAsync(snapshot.Path).ConfigureAwait(false);
                fileSnapshot?.Commit();
            }
            catch (Exception restoreException)
            {
                var compensationFailures = new List<Exception>();
                if (fileSnapshot is not null)
                {
                    using var compensationDeadline = new CancellationTokenSource(OperationDeadline);
                    try
                    {
                        await fileSnapshot.RollbackAsync(compensationDeadline.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Compensation must preserve and aggregate every possible restore failure.
                    catch (Exception compensationException)
#pragma warning restore CA1031
                    {
                        compensationFailures.Add(compensationException);
                    }
                }

                if (innerStarted && contextFactory is not null && originalHistory is not null)
                {
                    using var historyDeadline = new CancellationTokenSource(OperationDeadline);
                    try
                    {
                        await RestoreHistoryAsync(
                            contextFactory,
                            originalHistory,
                            historyDeadline.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Compensation must preserve and aggregate every possible restore failure.
                    catch (Exception compensationException)
#pragma warning restore CA1031
                    {
                        compensationFailures.Add(compensationException);
                    }
                }

                if (compensationFailures.Count != 0)
                {
                    throw new AggregateException(
                        "PostgreSQL restore failed and exact compensation also failed.",
                        new[] { restoreException }.Concat(compensationFailures));
                }

                throw;
            }
        }
        finally
        {
            ProviderOperationGate.Release();
        }
    }
}
