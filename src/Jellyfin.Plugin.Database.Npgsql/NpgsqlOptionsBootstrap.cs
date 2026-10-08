using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Isolates the first executable reference to the plugin-shipped Npgsql assemblies.
/// </summary>
internal static class NpgsqlOptionsBootstrap
{
    /// <summary>The default command timeout in seconds.</summary>
    internal const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>The minimum accepted timeout value.</summary>
    internal const int MinimumCommandTimeoutSeconds = 1;

    /// <summary>The maximum accepted command timeout in seconds.</summary>
    internal const int MaximumCommandTimeoutSeconds = 3600;

    /// <summary>The maximum accepted connection timeout in seconds.</summary>
    internal const int MaximumConnectionTimeoutSeconds = 120;

    /// <summary>The maximum accepted cancellation timeout in milliseconds.</summary>
    internal const int MaximumCancellationTimeoutMilliseconds = 30000;

    /// <summary>The maximum accepted connection pool size.</summary>
    internal const int MaximumPoolSize = 200;

    /// <summary>The database.xml option for overriding the command timeout.</summary>
    internal const string CommandTimeoutOption = "command-timeout";

    /// <summary>
    /// Validates database.xml settings and configures EF after adjacent dependencies are loaded.
    /// </summary>
    /// <param name="options">The host database options builder.</param>
    /// <param name="connectionString">The connection string from database.xml.</param>
    /// <param name="customOptions">The provider options from database.xml.</param>
    /// <returns>Validated settings, including the normalized private connection string.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static NpgsqlConnectionSettings Configure(
        DbContextOptionsBuilder options,
        string connectionString,
        Collection<CustomDatabaseOption>? customOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = Parse(connectionString, customOptions);

        options.UseNpgsql(
            configured.ConnectionString,
            npgsql =>
            {
                npgsql.MigrationsAssembly(Assembly.GetExecutingAssembly().GetName().Name);
                npgsql.CommandTimeout(configured.CommandTimeout);
                if (configured.ConfigureDataSource is not null)
                {
                    npgsql.ConfigureDataSource(configured.ConfigureDataSource);
                }
            });
        options.ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>();
        return configured;
    }

    /// <summary>
    /// Parses and validates the Npgsql settings supplied by Jellyfin's database.xml.
    /// </summary>
    /// <param name="connectionString">The configured Npgsql connection string.</param>
    /// <param name="customOptions">The custom provider option collection.</param>
    /// <returns>Validated settings.</returns>
    internal static NpgsqlConnectionSettings Parse(
        string connectionString,
        Collection<CustomDatabaseOption>? customOptions)
    {
        // Reject ambient configuration before invoking any Npgsql parsing or
        // defaults. database.xml is the sole configuration authority.
        NpgsqlConfigurationSecurity.RejectRecognizedEnvironmentFallbacks();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw ConfigurationError("CustomProviderOptions.ConnectionString is required");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Do not retain the original exception: its message may contain the
            // malformed connection string and therefore credentials.
            throw ConfigurationError("CustomProviderOptions.ConnectionString is not a valid Npgsql connection string");
        }

        RequireValue(builder.Host, "Host");
        RequireValue(builder.Database, "Database");
        RequireValue(builder.Username, "Username");

        var commandTimeout = ReadCommandTimeout(customOptions, builder.CommandTimeout);
        ValidateRange("connection Timeout", builder.Timeout, MinimumCommandTimeoutSeconds, MaximumConnectionTimeoutSeconds);
        ValidateRange(
            "connection Cancellation Timeout",
            builder.CancellationTimeout,
            MinimumCommandTimeoutSeconds,
            MaximumCancellationTimeoutMilliseconds);

        if (builder.Pooling)
        {
            ValidateRange("Minimum Pool Size", builder.MinPoolSize, 0, MaximumPoolSize);
            ValidateRange("Maximum Pool Size", builder.MaxPoolSize, 1, MaximumPoolSize);
            if (builder.MinPoolSize > builder.MaxPoolSize)
            {
                throw ConfigurationError("Minimum Pool Size cannot exceed Maximum Pool Size");
            }
        }

        if (!string.IsNullOrWhiteSpace(builder.SearchPath)
            && !string.Equals(builder.SearchPath.Trim(), "public", StringComparison.Ordinal))
        {
            throw ConfigurationError("Search Path must be exactly 'public'");
        }

        if (!string.IsNullOrWhiteSpace(builder.Options))
        {
            throw ConfigurationError("the Npgsql Options connection parameter is not supported because it can alter search_path or role state");
        }

        if (!string.IsNullOrWhiteSpace(builder.Timezone)
            && !string.Equals(builder.Timezone.Trim(), "UTC", StringComparison.OrdinalIgnoreCase))
        {
            throw ConfigurationError("Timezone must be UTC");
        }

        if (builder.LogParameters
            || builder.IncludeErrorDetail
            || builder.IncludeFailedBatchedCommand
            || builder.PersistSecurityInfo)
        {
            throw ConfigurationError(
                "Log Parameters, Include Error Detail, Include Failed Batched Command, and Persist Security Info must remain disabled to protect secrets");
        }

        if (builder.NoResetOnClose)
        {
            throw ConfigurationError("No Reset On Close must remain disabled so pooled session state cannot leak between operations");
        }

        var passfileConfigured = !string.IsNullOrWhiteSpace(builder.Passfile);
        var configureDataSource = NpgsqlConfigurationSecurity.PrepareSecretFiles(builder);

        builder.SearchPath = "public";
        builder.Timezone = "UTC";
        builder.CommandTimeout = commandTimeout;
        builder.LogParameters = false;
        builder.IncludeErrorDetail = false;
        builder.IncludeFailedBatchedCommand = false;
        builder.PersistSecurityInfo = false;
        if (string.IsNullOrWhiteSpace(builder.ApplicationName))
        {
            builder.ApplicationName = "Jellyfin";
        }

        return new NpgsqlConnectionSettings(
            builder.ConnectionString,
            builder.Host!,
            builder.Port,
            builder.Database!,
            builder.SslMode.ToString(),
            builder.Pooling,
            passfileConfigured,
            commandTimeout,
            configureDataSource);
    }

    private static int ReadCommandTimeout(
        Collection<CustomDatabaseOption>? customOptions,
        int connectionStringTimeout)
    {
        CustomDatabaseOption? configuredOption = null;
        if (customOptions is not null)
        {
            foreach (var option in customOptions)
            {
                if (string.IsNullOrWhiteSpace(option.Key))
                {
                    throw ConfigurationError("CustomProviderOptions.Options contains an empty key");
                }

                if (!string.Equals(option.Key, CommandTimeoutOption, StringComparison.OrdinalIgnoreCase))
                {
                    throw ConfigurationError($"CustomProviderOptions.Options contains unsupported key '{option.Key}'");
                }

                if (configuredOption is not null)
                {
                    throw ConfigurationError($"CustomProviderOptions.Options contains duplicate key '{CommandTimeoutOption}'");
                }

                configuredOption = option;
            }
        }

        var commandTimeout = connectionStringTimeout;
        if (configuredOption is not null
            && !int.TryParse(configuredOption.Value, NumberStyles.None, CultureInfo.InvariantCulture, out commandTimeout))
        {
            throw ConfigurationError(
                $"CustomProviderOptions.Options[{CommandTimeoutOption}] must be a whole number of seconds");
        }

        ValidateRange(
            $"CustomProviderOptions.Options[{CommandTimeoutOption}]",
            commandTimeout,
            MinimumCommandTimeoutSeconds,
            MaximumCommandTimeoutSeconds);
        return commandTimeout;
    }

    private static void RequireValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ConfigurationError($"CustomProviderOptions.ConnectionString requires {name}");
        }
    }

    private static void ValidateRange(string name, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw ConfigurationError($"{name} must be between {minimum} and {maximum}");
        }
    }

    private static InvalidOperationException ConfigurationError(string detail)
        => new($"The Npgsql provider configuration in database.xml is invalid: {detail}. No database connection was attempted.");
}
