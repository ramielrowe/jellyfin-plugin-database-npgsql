using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Database.Npgsql.ValueConverters;
using Jellyfin.Server.Implementations.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Npgsql database provider loaded by Jellyfin's early custom-provider loader.
/// </summary>
public partial class NpgsqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string PluginProviderType = "PLUGIN_PROVIDER";

    private static readonly (string FileName, string AssemblyName, Version Version)[] RuntimeDependencies =
    [
        ("Npgsql.dll", "Npgsql", new Version(10, 0, 3, 0)),
        ("Npgsql.EntityFrameworkCore.PostgreSQL.dll", "Npgsql.EntityFrameworkCore.PostgreSQL", new Version(10, 0, 3, 0)),
    ];

    private readonly ILogger<NpgsqlDatabaseProvider> logger;
    private readonly IServiceProvider? services;
    private readonly string? applicationDataPath;
    private string? backupRoot;
    private string? connectionString;
    private object? restoreDataSource;

    /// <summary>Initializes a new instance of the <see cref="NpgsqlDatabaseProvider"/> class.</summary>
    /// <param name="logger">The host logger.</param>
    public NpgsqlDatabaseProvider(ILogger<NpgsqlDatabaseProvider> logger)
    {
        this.logger = logger;
    }

    /// <summary>Initializes a new instance of the <see cref="NpgsqlDatabaseProvider"/> class inside Jellyfin's dependency-injection container.</summary>
    /// <param name="services">The unmodified Jellyfin service provider.</param>
    /// <param name="logger">The host logger.</param>
    [ActivatorUtilitiesConstructor]
    public NpgsqlDatabaseProvider(IServiceProvider services, ILogger<NpgsqlDatabaseProvider> logger)
        : this(logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        const string ApplicationPathsTypeName = "MediaBrowser.Common.Configuration.IApplicationPaths, MediaBrowser.Common";
        var applicationPathsType = Type.GetType(ApplicationPathsTypeName, throwOnError: false);
        var applicationPaths = applicationPathsType is null ? null : services.GetService(applicationPathsType);
        this.applicationDataPath = applicationPathsType?.GetProperty("DataPath")?.GetValue(applicationPaths) as string;
        if (string.IsNullOrWhiteSpace(this.applicationDataPath))
        {
            throw new InvalidOperationException(
                "The Npgsql provider could not resolve Jellyfin's application data path for migration backups. No database connection was attempted.");
        }
    }

    /// <summary>Gets the exact folder accepted by the early provider loader.</summary>
    protected virtual string RequiredPluginDirectory => PluginMetadata.InstallationDirectory;

    /// <summary>Gets the exact provider assembly accepted by the early provider loader.</summary>
    protected virtual string RequiredPluginAssembly => PluginMetadata.AssemblyFileName;

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc />
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        if (HasFullSystemRestoreArgument(Environment.GetCommandLineArgs()))
        {
            throw new NotSupportedException(NpgsqlFullSystemRestoreGuard.UnsupportedRestoreMessage);
        }

        if (this.applicationDataPath is not null)
        {
            var earlyPluginDirectory = Path.GetDirectoryName(this.GetType().Assembly.Location)
                ?? throw MissingOrdinaryPluginMetadata();
            ValidateOrdinaryPluginMetadata(earlyPluginDirectory);
            ValidateFullSystemRestoreGuard(this.services!);
        }

        var hostDatabaseAssembly = typeof(JellyfinDbContext).Assembly;
        var hostDatabaseVersion = hostDatabaseAssembly.GetName().Version
            ?? throw new InvalidOperationException("Jellyfin.Database.Implementations did not expose an assembly version.");
        if (!SupportsHostDatabaseAbi(hostDatabaseVersion))
        {
            throw new NotSupportedException(
                $"Jellyfin.Plugin.Database.Npgsql {PluginMetadata.Version} supports Jellyfin database ABI {PluginMetadata.SupportedDatabaseAbi}; "
                + $"the host loaded {hostDatabaseVersion}. Install a plugin release that supports this Jellyfin version, or restore the previous "
                + "Jellyfin version and provider package. No database connection was attempted.");
        }

        NpgsqlHostMigrationAudit.ValidateLoadedHostIfPresent();

        var customOptions = ValidateDatabaseConfiguration(databaseConfiguration);
        this.ValidateSelectedDirectory(customOptions.PluginName, customOptions.PluginAssembly);

        var providerAssembly = this.GetType().Assembly;
        var pluginDirectory = Path.GetDirectoryName(providerAssembly.Location)
            ?? throw new InvalidOperationException("The Npgsql provider assembly has no installation directory.");
        this.backupRoot = Path.Combine(this.applicationDataPath ?? pluginDirectory, "NpgsqlMigrationBackups");
        var dependencyAssemblies = LoadAdjacentDependencies(pluginDirectory);

        // Keep this call behind the adjacent dependency checks. The no-inline
        // bootstrap owns the first method body that references the Npgsql EF
        // assembly, so a missing package file reaches our recovery diagnostic.
        var configured = NpgsqlOptionsBootstrap.Configure(
            options,
            customOptions.ConnectionString,
            customOptions.Options);
        this.connectionString = configured.ConnectionString;
        this.restoreDataSource = NpgsqlRestoreDataSource.Create(configured);

        LogProviderLoaded(
            this.logger,
            PluginMetadata.Version,
            providerAssembly.Location,
            AssemblyLoadContext.GetLoadContext(providerAssembly)?.Name ?? "Default");
        LogDatabaseEndpoint(
            this.logger,
            configured.Host,
            configured.Port,
            configured.Database,
            configured.SslMode,
            configured.Pooling,
            configured.PassfileConfigured,
            configured.CommandTimeout);

        this.LogAssembly("provider", providerAssembly);
        this.LogAssembly("host database", hostDatabaseAssembly);
        this.LogAssembly("EF Core", typeof(DbContext).Assembly);
        this.LogAssembly("Npgsql", dependencyAssemblies[0]);
        this.LogAssembly("Npgsql EF", dependencyAssemblies[1]);
    }

    /// <inheritdoc />
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Provider-specific final-model work is deliberately applied by
        // NpgsqlModelCustomizer after Jellyfin's shared configurations.
    }

    /// <inheritdoc />
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Add(_ => new NpgsqlFinalizedModelContract());

        // Jellyfin's persisted timestamps represent UTC instants. Npgsql maps
        // these to timestamp with time zone and rejects non-UTC DateTime values.
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeValueConverter>()
            .HaveColumnType("timestamp with time zone");
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetValueConverter>()
            .HaveColumnType("timestamp with time zone");
    }

    /// <inheritdoc />
    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        LogScheduledMaintenance(this.logger);
        return NpgsqlDatabaseOperations.ExecuteMaintenanceAsync(
            this.RequireFactory(cancellationToken),
            "VACUUM (ANALYZE)",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RefreshStatistics(CancellationToken cancellationToken)
    {
        LogStatisticsMaintenance(this.logger);
        return NpgsqlDatabaseOperations.ExecuteMaintenanceAsync(
            this.RequireFactory(cancellationToken),
            "ANALYZE",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RunShutdownTask(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredConnectionString = this.connectionString;
        if (configuredConnectionString is not null)
        {
            // Pool retirement is synchronous, bounded by local bookkeeping, and
            // does not contact PostgreSQL or grow with the database size.
            NpgsqlDatabaseOperations.ClearPool(configuredConnectionString);
        }

        if (this.restoreDataSource is not null)
        {
            await NpgsqlRestoreDataSource.DisposeAsync(this.restoreDataSource).ConfigureAwait(false);
        }

        this.restoreDataSource = null;
    }

    /// <inheritdoc />
    public virtual async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var key = await NpgsqlMigrationBackup.CreateAsync(
            this.RequireFactory(cancellationToken),
            this.RequireConnectionString(),
            this.RequireBackupRoot(),
            cancellationToken).ConfigureAwait(false);
        LogMigrationBackupCreated(this.logger, key);
        return key;
    }

    /// <inheritdoc />
    public virtual async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        await NpgsqlRestoreDataSource.RestoreAsync(
            this.RequireDataSourceHandle(),
            this.RequireBackupRoot(),
            key,
            cancellationToken).ConfigureAwait(false);
        LogMigrationBackupRestored(this.logger, key);
    }

    /// <inheritdoc />
    public virtual async Task DeleteBackup(string key)
    {
        await NpgsqlMigrationBackup.DeleteAsync(this.RequireBackupRoot(), key).ConfigureAwait(false);
        LogMigrationBackupDeleted(this.logger, key);
    }

    /// <inheritdoc />
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
        => NpgsqlDatabaseOperations.PurgeDatabaseAsync(dbContext, tableNames);

    /// <summary>
    /// Determines whether a host database assembly is the exact audited release contract.
    /// </summary>
    /// <param name="version">The host database assembly version.</param>
    /// <returns><see langword="true" /> when the ABI is supported.</returns>
    internal static bool SupportsHostDatabaseAbi(Version version)
        => version == new Version(12, 2, 0, 0);

    /// <summary>Detects Jellyfin's public command-line full-system restore entry point without reading its archive path.</summary>
    /// <param name="arguments">The process command line.</param>
    /// <returns><see langword="true"/> when a restore archive option is present.</returns>
    internal static bool HasFullSystemRestoreArgument(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--restore-archive", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--restore-archive=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Validates the exact active metadata required for late restore-guard registration.</summary>
    /// <param name="pluginDirectory">The early provider's installation directory.</param>
    internal static void ValidateOrdinaryPluginMetadata(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        const int MaximumMetadataBytes = 64 * 1024;
        try
        {
            var metadataPath = Path.Combine(pluginDirectory, "meta.json");
            using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumMetadataBytes)
            {
                throw MissingOrdinaryPluginMetadata();
            }

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var assemblies = root.GetProperty("assemblies").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (!string.Equals(root.GetProperty("guid").GetString(), PluginMetadata.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(root.GetProperty("name").GetString(), PluginMetadata.Name, StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("version").GetString(), PluginMetadata.Version, StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("targetAbi").GetString(), PluginMetadata.SupportedDatabaseAbi, StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("status").GetString(), "Active", StringComparison.Ordinal)
                || assemblies.Length != 1
                || !string.Equals(assemblies[0], PluginMetadata.AssemblyFileName, StringComparison.Ordinal))
            {
                throw MissingOrdinaryPluginMetadata();
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            throw MissingOrdinaryPluginMetadata(exception);
        }
    }

    private static InvalidOperationException MissingOrdinaryPluginMetadata(Exception? innerException = null)
        => new(
            "The Npgsql ordinary plugin metadata is missing, disabled, malformed, or does not match this provider build. "
            + "Jellyfin is refusing to start before any dashboard, API, scheduled, or startup restore can change Config, Data, Root, "
            + "migration history, PostgreSQL, or SQLite. Restore the complete matching package with meta.json status Active and restart, "
            + "or select a different database provider. No database connection or restore was attempted.",
            innerException);

    /// <summary>
    /// Proves that late plugin discovery installed this exact build's pre-mutation restore guard.
    /// </summary>
    /// <param name="services">The completed host service provider.</param>
    internal static void ValidateFullSystemRestoreGuard(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var serviceProbe = services.GetService<IServiceProviderIsService>();
        if (serviceProbe is not null && !serviceProbe.IsService(typeof(IBackupService)))
        {
            // Jellyfin's separate pre-initialisation migration container has
            // no backup entry point. The complete host container is checked
            // before it opens PostgreSQL or serves dashboard/API requests.
            return;
        }

        const string RegistrationTypeName = "Jellyfin.Plugin.Database.Npgsql.NpgsqlRestoreGuardRegistration";
        var providerModuleId = typeof(NpgsqlDatabaseProvider).Assembly.ManifestModule.ModuleVersionId;
        var registrationIsExact = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && assembly.ManifestModule.ModuleVersionId == providerModuleId)
            .Select(assembly => assembly.GetType(RegistrationTypeName, throwOnError: false))
            .Where(type => type is not null)
            .Select(type => services.GetService(type!))
            .Where(instance => instance is not null)
            .Any(instance => instance!.GetType().GetMethod(
                    "IsExactFinalRegistration",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null) is true);
        if (!registrationIsExact)
        {
            throw new InvalidOperationException(
                "The Npgsql full-system restore guard is unavailable or was replaced. The ordinary plugin may be disabled, deleted, "
                + "malformed, absent, or its service registration may have failed. Jellyfin is refusing to start with this PostgreSQL "
                + "provider before any dashboard, API, scheduled, or startup restore can change Config, Data, Root, migration history, "
                + "PostgreSQL, or SQLite. Restore the complete matching plugin package with status Active and restart, or select a "
                + "different database provider. No database connection or restore was attempted.");
        }
    }

    /// <summary>
    /// Validates the unmodified Jellyfin database configuration contract.
    /// </summary>
    /// <param name="databaseConfiguration">The configuration deserialized by Jellyfin from database.xml.</param>
    /// <returns>The required custom-provider section.</returns>
    internal static CustomDatabaseOptions ValidateDatabaseConfiguration(DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(databaseConfiguration);
        if (!string.Equals(databaseConfiguration.DatabaseType, PluginProviderType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Npgsql provider must be selected by setting DatabaseType to PLUGIN_PROVIDER in database.xml. "
                + "No database connection was attempted.");
        }

        if (databaseConfiguration.LockingBehavior != DatabaseLockingBehaviorTypes.NoLock)
        {
            throw new InvalidOperationException(
                "The Npgsql provider requires LockingBehavior NoLock because PostgreSQL supplies database-level concurrency control. "
                + "No database connection was attempted.");
        }

        var customOptions = databaseConfiguration.CustomProviderOptions
            ?? throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions in database.xml. No database connection was attempted.");
        if (string.IsNullOrWhiteSpace(customOptions.ConnectionString))
        {
            throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions.ConnectionString in database.xml. No database connection was attempted.");
        }

        return customOptions;
    }

    /// <summary>
    /// Validates and loads the exact plugin-shipped runtime assemblies from the installation directory.
    /// </summary>
    /// <param name="pluginDirectory">The canonical plugin installation directory.</param>
    /// <returns>The Npgsql driver and EF provider assemblies, in that order.</returns>
    internal static IReadOnlyList<Assembly> LoadAdjacentDependencies(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var loaded = new Assembly[RuntimeDependencies.Length];

        for (var index = 0; index < RuntimeDependencies.Length; index++)
        {
            var dependency = RuntimeDependencies[index];
            var dependencyPath = Path.Combine(pluginDirectory, dependency.FileName);
            if (!File.Exists(dependencyPath))
            {
                throw MissingDependency(dependency.FileName, pluginDirectory, null);
            }

            try
            {
                var identity = AssemblyName.GetAssemblyName(dependencyPath);
                if (!string.Equals(identity.Name, dependency.AssemblyName, StringComparison.Ordinal)
                    || identity.Version != dependency.Version)
                {
                    throw new FileLoadException(
                        $"Expected {dependency.AssemblyName}, Version={dependency.Version}; found {identity.FullName}.",
                        dependencyPath);
                }

                loaded[index] = Assembly.LoadFrom(dependencyPath);
            }
            catch (Exception exception) when (exception is FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                throw MissingDependency(dependency.FileName, pluginDirectory, exception);
            }

            var actualDirectory = Path.GetDirectoryName(loaded[index].Location);
            if (!string.Equals(actualDirectory, pluginDirectory, StringComparison.Ordinal)
                || AssemblyLoadContext.GetLoadContext(loaded[index]) != AssemblyLoadContext.Default)
            {
                throw new InvalidOperationException(
                    $"Required dependency '{dependency.FileName}' resolved from '{loaded[index].Location}' instead of the Npgsql plugin folder "
                    + $"'{pluginDirectory}' in the default load context. Remove stray copies, restore the complete release ZIP, and restart. "
                    + "No database connection was attempted.");
            }
        }

        return loaded;
    }

    private static InvalidOperationException MissingDependency(string dependency, string pluginDirectory, Exception? innerException)
        => new(
            $"Required Npgsql dependency '{dependency}' is missing or incompatible in '{pluginDirectory}'. Restore the complete "
            + $"Jellyfin.Plugin.Database.Npgsql {PluginMetadata.Version} ZIP in '{PluginMetadata.InstallationDirectory}', or reinstall the previous "
            + "compatible package before restarting. No database connection was attempted.",
            innerException);

    private void ValidateSelectedDirectory(string pluginName, string pluginAssembly)
    {
        var providerAssembly = this.GetType().Assembly;
        var loadContext = AssemblyLoadContext.GetLoadContext(providerAssembly);
        if (loadContext != AssemblyLoadContext.Default)
        {
            throw new InvalidOperationException(
                "The Npgsql database provider was instantiated from Jellyfin's later collectible plugin context instead of the early default "
                + "load context. Keep DatabaseType set to PLUGIN_PROVIDER and do not rely on plugin service registration. No database connection was attempted.");
        }

        var selectedDirectory = Path.GetFileName(Path.GetDirectoryName(providerAssembly.Location));
        if (string.IsNullOrWhiteSpace(pluginName)
            || !string.Equals(pluginName, this.RequiredPluginDirectory, StringComparison.Ordinal)
            || !string.Equals(selectedDirectory, pluginName, StringComparison.Ordinal)
            || !string.Equals(pluginAssembly, this.RequiredPluginAssembly, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Jellyfin selected the Npgsql provider from folder '{selectedDirectory}', but this release requires the exact folder and PluginName "
                + $"'{this.RequiredPluginDirectory}' plus PluginAssembly '{this.RequiredPluginAssembly}'. Remove ambiguous prefix-matching "
                + "folders, restore the complete release ZIP there, update database.xml atomically, and restart. No database connection was attempted.");
        }
    }

    private IDbContextFactory<JellyfinDbContext> RequireFactory(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.DbContextFactory
            ?? throw new InvalidOperationException(
                "The Npgsql provider database factory is unavailable; Jellyfin did not finish database initialization.");
    }

    private string RequireConnectionString()
        => this.connectionString
            ?? throw new InvalidOperationException("The Npgsql provider has not been initialized; no migration backup operation was attempted.");

    private object RequireDataSourceHandle()
        => this.restoreDataSource
            ?? throw new InvalidOperationException("The Npgsql provider has not been initialized; no migration restore operation was attempted.");

    private string RequireBackupRoot()
    {
        if (string.IsNullOrWhiteSpace(this.backupRoot))
        {
            throw new InvalidOperationException(
                "The Npgsql provider has not been initialized; no migration backup operation was attempted.");
        }

        return this.backupRoot;
    }

    private void LogAssembly(string role, Assembly assembly)
    {
        if (!this.logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
#pragma warning disable CA1873 // IsEnabled above prevents evaluation when information logging is disabled.
        LogAssemblyOrigin(
            this.logger,
            role,
            assembly.GetName().ToString(),
            assembly.Location,
            loadContext?.Name ?? "Default");
#pragma warning restore CA1873
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Npgsql database provider {PluginVersion} loaded from {AssemblyLocation} in {LoadContext}")]
    private static partial void LogProviderLoaded(ILogger logger, string pluginVersion, string assemblyLocation, string loadContext);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "PostgreSQL configured at {Host}:{Port} for database {Database}; TLS {SslMode}, pooling {Pooling}, passfile {PassfileConfigured}, command timeout {CommandTimeoutSeconds}s")]
    internal static partial void LogDatabaseEndpoint(
        ILogger logger,
        string host,
        int port,
        string database,
        string sslMode,
        bool pooling,
        bool passfileConfigured,
        int commandTimeoutSeconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Running PostgreSQL VACUUM and statistics maintenance")]
    private static partial void LogScheduledMaintenance(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Refreshing PostgreSQL query planner statistics")]
    private static partial void LogStatisticsMaintenance(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Created PostgreSQL migration snapshot {BackupKey}")]
    private static partial void LogMigrationBackupCreated(ILogger logger, string backupKey);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Restored PostgreSQL migration snapshot {BackupKey}")]
    private static partial void LogMigrationBackupRestored(ILogger logger, string backupKey);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Deleted PostgreSQL migration snapshot {BackupKey}")]
    private static partial void LogMigrationBackupDeleted(ILogger logger, string backupKey);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "Npgsql provider {AssemblyRole}: {AssemblyName} from {AssemblyLocation} in load context {LoadContext}")]
    private static partial void LogAssemblyOrigin(
        ILogger logger,
        string assemblyRole,
        string assemblyName,
        string assemblyLocation,
        string loadContext);
}
