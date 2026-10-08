using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Npgsql database provider loaded by Jellyfin's early custom-provider loader.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="NpgsqlDatabaseProvider"/> class.
/// </remarks>
/// <param name="logger">The host logger.</param>
public sealed partial class NpgsqlDatabaseProvider(ILogger<NpgsqlDatabaseProvider> logger) : IJellyfinDatabaseProvider
{
    private static readonly (string FileName, string AssemblyName, Version Version)[] RuntimeDependencies =
    [
        ("Npgsql.dll", "Npgsql", new Version(10, 0, 3, 0)),
        ("Npgsql.EntityFrameworkCore.PostgreSQL.dll", "Npgsql.EntityFrameworkCore.PostgreSQL", new Version(10, 0, 3, 0)),
    ];

    private readonly ILogger<NpgsqlDatabaseProvider> logger = logger;
    private string? connectionString;

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc />
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

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

        var customOptions = databaseConfiguration.CustomProviderOptions
            ?? throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions in database.xml. No database connection was attempted.");
        this.ValidateSelectedDirectory(customOptions.PluginName);

        var providerAssembly = this.GetType().Assembly;
        var pluginDirectory = Path.GetDirectoryName(providerAssembly.Location)
            ?? throw new InvalidOperationException("The Npgsql provider assembly has no installation directory.");
        var dependencyAssemblies = LoadAdjacentDependencies(pluginDirectory);

        var configuredConnectionString = customOptions.ConnectionString;
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                "The Npgsql provider requires CustomProviderOptions.ConnectionString in database.xml. No database connection was attempted.");
        }

        this.connectionString = configuredConnectionString;

        LogProviderLoaded(
            this.logger,
            PluginMetadata.Version,
            providerAssembly.Location,
            AssemblyLoadContext.GetLoadContext(providerAssembly)?.Name ?? "Default");

        // Keep this call behind the adjacent dependency checks. The no-inline
        // bootstrap owns the first method body that references the Npgsql EF
        // assembly, so a missing package file reaches our recovery diagnostic.
        NpgsqlOptionsBootstrap.Configure(options, configuredConnectionString);

        this.LogAssembly("provider", providerAssembly);
        this.LogAssembly("host database", hostDatabaseAssembly);
        this.LogAssembly("EF Core", typeof(DbContext).Assembly);
        this.LogAssembly("Npgsql", dependencyAssemblies[0]);
        this.LogAssembly("Npgsql EF", dependencyAssemblies[1]);
    }

    /// <inheritdoc />
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc />
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc />
    public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        => NpgsqlDatabaseOperations.MigrationBackupFast(this.RequireConnectionString(), this.logger, cancellationToken);

    /// <inheritdoc />
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return NpgsqlDatabaseOperations.RestoreBackupFast(this.RequireConnectionString(), this.logger, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteBackup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return NpgsqlDatabaseOperations.DeleteBackup(this.RequireConnectionString(), this.logger, key);
    }

    /// <inheritdoc />
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => Task.CompletedTask;

    /// <summary>
    /// Determines whether a host database assembly belongs to this release's supported ABI family.
    /// </summary>
    /// <param name="version">The host database assembly version.</param>
    /// <returns><see langword="true" /> when the ABI is supported.</returns>
    internal static bool SupportsHostDatabaseAbi(Version version)
        => version.Major == 12 && version.Minor == 2;

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

    private void ValidateSelectedDirectory(string pluginName)
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
            || !string.Equals(pluginName, PluginMetadata.InstallationDirectory, StringComparison.Ordinal)
            || !string.Equals(selectedDirectory, pluginName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Jellyfin selected the Npgsql provider from folder '{selectedDirectory}', but this release requires the exact folder and PluginName "
                + $"'{PluginMetadata.InstallationDirectory}'. Remove ambiguous prefix-matching folders, restore the complete release ZIP there, update "
                + "database.xml atomically, and restart. No database connection was attempted.");
        }
    }

    private static InvalidOperationException MissingDependency(string dependency, string pluginDirectory, Exception? innerException)
        => new(
            $"Required Npgsql dependency '{dependency}' is missing or incompatible in '{pluginDirectory}'. Restore the complete "
            + $"Jellyfin.Plugin.Database.Npgsql {PluginMetadata.Version} ZIP in '{PluginMetadata.InstallationDirectory}', or reinstall the previous "
            + "compatible package before restarting. No database connection was attempted.",
            innerException);

    private string RequireConnectionString()
        => this.connectionString ?? throw new InvalidOperationException("The Npgsql provider has not been initialized.");

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
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Npgsql provider {AssemblyRole}: {AssemblyName} from {AssemblyLocation} in load context {LoadContext}")]
    private static partial void LogAssemblyOrigin(
        ILogger logger,
        string assemblyRole,
        string assemblyName,
        string assemblyLocation,
        string loadContext);
}
