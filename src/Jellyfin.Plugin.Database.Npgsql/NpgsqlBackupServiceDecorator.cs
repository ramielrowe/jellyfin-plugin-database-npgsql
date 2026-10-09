using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var handoffStarted = false;
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
            handoffStarted = true;
            _ = ReleaseGateAfterShutdownHandoffAsync();
        }
        finally
        {
            if (!handoffStarted)
            {
                ProviderOperationGate.Release();
            }
        }
    }

    private static async Task ReleaseGateAfterShutdownHandoffAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
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

    private static async Task RestoreDatabaseAsync(
        NpgsqlRestoreDbContextFactory contextFactory,
        IJellyfinDatabaseProvider databaseProvider,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateRestoreDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        using (var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false))
        {
            var historyEntry = archive.GetEntry("Database/HistoryRow.json")
                ?? throw new InvalidDataException("The validated PostgreSQL backup lost its migration history.");
            var historyStream = await historyEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            HistoryRow[] history;
            await using (historyStream.ConfigureAwait(false))
            {
                history = await JsonSerializer.DeserializeAsync<HistoryRow[]>(
                    historyStream,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The validated PostgreSQL migration history is empty.");
            }

            var historyRepository = context.GetService<IHistoryRepository>();
            foreach (var current in await historyRepository.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            {
                await context.Database.ExecuteSqlRawAsync(
                    historyRepository.GetDeleteScript(current.MigrationId),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var row in history)
            {
                await context.Database.ExecuteSqlRawAsync(
                    historyRepository.GetInsertScript(row),
                    cancellationToken).ConfigureAwait(false);
            }

            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var entityTypes = typeof(JellyfinDbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
                .Select(property => (
                    Property: property,
                    ClrType: property.PropertyType.GetGenericArguments()[0]))
                .ToArray();
            await databaseProvider.PurgeDatabase(
                context,
                entityTypes.Select(item => context.Model.FindEntityType(item.ClrType)!.GetSchemaQualifiedTableName()!))
                .ConfigureAwait(false);

            foreach (var entityType in entityTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry($"Database/{entityType.Property.Name}.json")
                    ?? throw new InvalidDataException(
                        $"The validated PostgreSQL backup lost table '{entityType.Property.Name}'.");
                var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonObject>(
                        stream,
                        cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        var entity = item?.Deserialize(entityType.ClrType)
                            ?? throw new InvalidDataException(
                                $"The validated PostgreSQL backup contains a null '{entityType.Property.Name}' row.");
                        context.Add(entity);
                    }
                }
            }

            // The provider assembly may be loaded once during Jellyfin's early
            // database bootstrap and again in the regular plugin load context.
            // Prepare explicitly in this load context so the decorator-owned
            // transaction cannot depend on an interceptor/static-state match
            // across those assembly boundaries.
            await NpgsqlRestoreIdentityHydrator.HydrateAsync(context, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NpgsqlRestoreCoordinator.PrepareAsync(context, cancellationToken).ConfigureAwait(false);
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
            var coordinatedFactory = this.services?.GetService<NpgsqlRestoreDbContextFactory>();
#pragma warning disable CA2007 // The owned async-disposable is explicitly configured at every operation await.
            await using var fileSnapshot = applicationPaths is null
                ? null
                : await NpgsqlRestoreFileSnapshot.CaptureAsync(
                    snapshot.Path,
                    applicationPaths,
                    deadline.Token).ConfigureAwait(false);
#pragma warning restore CA2007
            using var scope = NpgsqlRestoreScope.Enter(snapshot.Path);
            using var databaseScope = coordinatedFactory is null
                ? null
                : NpgsqlRestoreScope.Enter(snapshot.Path);
            try
            {
                await this.inner.RestoreBackupAsync(
                    fileSnapshot?.StockFilesystemArchivePath ?? snapshot.Path).ConfigureAwait(false);
                if (coordinatedFactory is not null)
                {
                    await RestoreDatabaseAsync(
                        coordinatedFactory,
                        this.services!.GetRequiredService<IJellyfinDatabaseProvider>(),
                        snapshot.Path,
                        deadline.Token).ConfigureAwait(false);
                }

                if (fileSnapshot is not null)
                {
                    await fileSnapshot.ApplyAsync(deadline.Token).ConfigureAwait(false);
                }

                if (coordinatedFactory is not null)
                {
                    await NpgsqlRestoreCoordinator.CommitCurrentAsync(deadline.Token).ConfigureAwait(false);
                }

                fileSnapshot?.Commit();
            }
            catch (Exception restoreException)
            {
                var compensationFailures = new List<Exception>();
                var hadCoordinatedTransaction = NpgsqlRestoreScope
                    .GetTransactionState<NpgsqlRestoreCoordinator.RestoreState>() is not null;
                try
                {
                    await NpgsqlRestoreCoordinator.RollbackCurrentAsync().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Compensation must preserve and aggregate every possible restore failure.
                catch (Exception compensationException)
#pragma warning restore CA1031
                {
                    compensationFailures.Add(compensationException);
                }

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

                if (!hadCoordinatedTransaction && contextFactory is not null && originalHistory is not null)
                {
                    using var historyDeadline = new CancellationTokenSource(OperationDeadline);
                    try
                    {
                        await RestoreHistoryAsync(contextFactory, originalHistory, historyDeadline.Token).ConfigureAwait(false);
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
