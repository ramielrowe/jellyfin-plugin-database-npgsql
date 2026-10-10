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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Preserves the stock backup implementation while adding provider binding and preflight validation.
/// </summary>
internal sealed class NpgsqlBackupServiceDecorator : IBackupService
{
    private static readonly SemaphoreSlim ProviderOperationGate = new(1, 1);

    private static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(10);
    private static readonly Action<ILogger, string, Exception?> LogShutdownFault = LoggerMessage.Define<string>(
        LogLevel.Critical,
        new EventId(9101, "NpgsqlRestoreShutdownStalled"),
        "{Message}");

    private static string? terminalShutdownFault;
    private static TaskCompletionSource shutdownFaultSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IBackupService inner;
    private readonly bool isActiveProvider;
    private readonly IServiceProvider? services;
    private readonly IHostApplicationLifetime? applicationLifetime;
    private readonly ILogger<NpgsqlBackupServiceDecorator>? logger;

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
        this.applicationLifetime = services?.GetService<IHostApplicationLifetime>();
        this.logger = services?.GetService<ILogger<NpgsqlBackupServiceDecorator>>();
        var applicationPaths = services?.GetService<IServerApplicationPaths>();
        if (applicationPaths is not null)
        {
            NpgsqlScratchSpace.ConfigureRoot(applicationPaths.DataPath);
        }
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
        await WaitForProviderGateAsync(deadline.Token).ConfigureAwait(false);
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

        WaitForProviderGateAsync(deadline.Token).GetAwaiter().GetResult();
        var handoffStarted = false;
        ShutdownGateRelease? release = null;
        CancellationTokenRegistration stoppedRegistration = default;
        try
        {
            using var context = this.services?
                .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
                .CreateDbContext();
            NpgsqlBackupArchive.ValidateAsync(
                archivePath,
                context?.Model,
                deadline.Token).GetAwaiter().GetResult();
            if (this.applicationLifetime is null)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL restore cannot schedule a restart because the host application lifetime is unavailable. The provider operation gate remains fail-closed.");
            }

            if (this.applicationLifetime.ApplicationStopped.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL restore cannot schedule a restart after the host lifetime has already stopped.");
            }

            release = new ShutdownGateRelease(this.logger, GetShutdownDeadline());
            stoppedRegistration = this.applicationLifetime.ApplicationStopped.Register(release.Release);
            NpgsqlBackupFileSystem.Checkpoint("schedule-restore-before-publication");
            ThrowIfTerminalShutdownFault();
            this.inner.ScheduleRestoreAndRestartServer(archivePath);
            handoffStarted = true;
        }
        finally
        {
            if (!handoffStarted)
            {
                release?.Abandon();
                stoppedRegistration.Dispose();
                ProviderOperationGate.Release();
            }
        }
    }

    /// <summary>Clears only the observable watchdog state after a test has acknowledged shutdown.</summary>
    internal static void ResetShutdownFaultForTests()
    {
        Volatile.Write(ref terminalShutdownFault, null);
        Volatile.Write(
            ref shutdownFaultSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>Publishes the terminal shutdown signal at an exact synchronous test boundary.</summary>
    /// <param name="message">The observable fail-closed diagnostic.</param>
    internal static void InjectTerminalShutdownFaultForTests(string message)
    {
        Volatile.Write(ref terminalShutdownFault, message);
        Volatile.Read(ref shutdownFaultSignal).TrySetResult();
    }

#pragma warning disable SA1201 // Gate callback is kept beside the scheduling operation it protects.
    private sealed class ShutdownGateRelease
    {
        private int released;

        internal ShutdownGateRelease(ILogger? logger, TimeSpan deadline)
        {
            _ = this.WatchAsync(logger, deadline);
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                ProviderOperationGate.Release();
            }
        }

        internal void Abandon() => _ = Interlocked.Exchange(ref this.released, 1);

        private async Task WatchAsync(ILogger? logger, TimeSpan deadline)
        {
            await Task.Delay(deadline).ConfigureAwait(false);
            if (Volatile.Read(ref this.released) != 0)
            {
                return;
            }

            var message = $"Jellyfin did not reach ApplicationStopped within {deadline}. PostgreSQL backup and restore remain disabled fail-closed; inspect host shutdown logs and restart the process.";
            Interlocked.CompareExchange(ref terminalShutdownFault, message, null);
            Volatile.Read(ref shutdownFaultSignal).TrySetResult();
            if (logger is not null)
            {
                LogShutdownFault(logger, message, null);
            }
        }
    }

    private static void ThrowIfTerminalShutdownFault()
    {
        if (Volatile.Read(ref terminalShutdownFault) is { } fault)
        {
            throw new InvalidOperationException(fault);
        }
    }

    private static async Task WaitForProviderGateAsync(CancellationToken cancellationToken)
    {
        ThrowIfTerminalShutdownFault();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = ProviderOperationGate.WaitAsync(linked.Token);
        var faultSignal = Volatile.Read(ref shutdownFaultSignal).Task;
        if (await Task.WhenAny(wait, faultSignal).ConfigureAwait(false) == faultSignal)
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Prevent the abandoned waiter from acquiring the gate later.
            }

            ThrowIfTerminalShutdownFault();
        }

        await wait.ConfigureAwait(false);
        ThrowIfTerminalShutdownFault();
    }

    private static TimeSpan GetShutdownDeadline()
    {
        const int defaultSeconds = 30;
        var configured = Environment.GetEnvironmentVariable("JELLYFIN_NPGSQL_SHUTDOWN_DEADLINE_SECONDS");
        return int.TryParse(configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 1 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultSeconds);
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
        await WaitForProviderGateAsync(deadline.Token).ConfigureAwait(false);
        var keepGateHeld = false;
        try
        {
            IDbContextFactory<JellyfinDbContext>? contextFactory = null;
            IReadOnlyList<HistoryRow>? originalHistory = null;
            string? connectionString = null;
            if (this.services is not null)
            {
                contextFactory = this.services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
                var context = await contextFactory.CreateDbContextAsync(deadline.Token).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    model = context.Model;
                    connectionString = context.Database.GetConnectionString();
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
            using var scope = NpgsqlRestoreScope.Enter(snapshot.Path, fileSnapshot?.RestoreId);
            using var databaseScope = coordinatedFactory is null
                ? null
                : NpgsqlRestoreScope.Enter(snapshot.Path, fileSnapshot?.RestoreId);
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
                    fileSnapshot?.MarkCommitIntent();
                    await NpgsqlRestoreCoordinator.CommitCurrentAsync(deadline.Token).ConfigureAwait(false);
                }

                fileSnapshot?.Commit();
                if (fileSnapshot is not null && connectionString is not null)
                {
                    try
                    {
                        await NpgsqlRestoreCoordinator.DeleteMarkerAsync(
                            connectionString,
                            fileSnapshot.RestoreId,
                            deadline.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Cleanup after a durable committed journal is deferred to startup recovery.
                    catch
#pragma warning restore CA1031
                    {
                        fileSnapshot.DeferCleanup();
                        this.applicationLifetime?.StopApplication();

                        // The committed journal remains authoritative and startup retries marker cleanup.
                    }
                }
            }
            catch (NpgsqlRestoreCoordinator.NpgsqlRestoreRecoveryRequiredException)
            {
                keepGateHeld = true;
                this.applicationLifetime?.StopApplication();
                throw;
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
            if (!keepGateHeld)
            {
                ProviderOperationGate.Release();
            }
        }
    }
}
#pragma warning restore SA1201
