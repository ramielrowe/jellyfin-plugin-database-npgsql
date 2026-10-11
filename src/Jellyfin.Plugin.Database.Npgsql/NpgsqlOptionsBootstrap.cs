using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>The database.xml option controlling Unix passfile permissions.</summary>
    internal const string PassfilePermissionPolicyOption = "passfile-permission-policy";

    /// <summary>The fixed PostgreSQL startup option used for every plugin connection.</summary>
    internal const string PostgreSqlSessionOptions = "-c jit=off";

    /// <summary>The explicit policy value permitting read access through a private process group.</summary>
    internal const string OwnerOrGroupReadPolicy = "owner-or-group-read";

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
                npgsql.ExecutionStrategy(
                    dependencies => new NpgsqlHostCompatibleRetryingExecutionStrategy(dependencies));
                if (configured.ConfigureDataSource is not null)
                {
                    npgsql.ConfigureDataSource(configured.ConfigureDataSource);
                }
            });
        options
            .ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>()
            .ReplaceService<IRelationalDatabaseCreator, NpgsqlExistingDatabaseOnlyCreator>()
            .AddInterceptors(
                NpgsqlQueryExpressionInterceptor.Instance,
                NpgsqlItemValueConcurrencyInterceptor.Instance,
                NpgsqlUserDataConcurrencyInterceptor.Instance);
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

        var validatedOptions = ReadCustomOptions(customOptions, builder.CommandTimeout);
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
        var configureDataSource = NpgsqlConfigurationSecurity.PrepareSecretFiles(
            builder,
            validatedOptions.PassfilePermissionPolicy);

        builder.SearchPath = "public";
        builder.Timezone = "UTC";

        // Jellyfin's generated Continue Watching query can cross PostgreSQL's JIT
        // cost threshold while returning no rows, making compilation dominate a
        // synchronous request. Apply this only after rejecting arbitrary Options.
        builder.Options = PostgreSqlSessionOptions;
        builder.CommandTimeout = validatedOptions.CommandTimeout;
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
            validatedOptions.CommandTimeout,
            configureDataSource);
    }

    private static ValidatedCustomOptions ReadCustomOptions(
        Collection<CustomDatabaseOption>? customOptions,
        int connectionStringTimeout)
    {
        CustomDatabaseOption? commandTimeoutOption = null;
        CustomDatabaseOption? passfilePermissionPolicyOption = null;
        if (customOptions is not null)
        {
            foreach (var option in customOptions)
            {
                if (string.IsNullOrWhiteSpace(option.Key))
                {
                    throw ConfigurationError("CustomProviderOptions.Options contains an empty key");
                }

                if (string.Equals(option.Key, CommandTimeoutOption, StringComparison.OrdinalIgnoreCase))
                {
                    if (commandTimeoutOption is not null)
                    {
                        throw ConfigurationError(
                            $"CustomProviderOptions.Options contains duplicate key '{CommandTimeoutOption}'");
                    }

                    commandTimeoutOption = option;
                    continue;
                }

                if (string.Equals(option.Key, PassfilePermissionPolicyOption, StringComparison.OrdinalIgnoreCase))
                {
                    if (passfilePermissionPolicyOption is not null)
                    {
                        throw ConfigurationError(
                            $"CustomProviderOptions.Options contains duplicate key '{PassfilePermissionPolicyOption}'");
                    }

                    passfilePermissionPolicyOption = option;
                    continue;
                }

                throw ConfigurationError($"CustomProviderOptions.Options contains unsupported key '{option.Key}'");
            }
        }

        var commandTimeout = connectionStringTimeout;
        if (commandTimeoutOption is not null
            && !int.TryParse(
                commandTimeoutOption.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out commandTimeout))
        {
            throw ConfigurationError(
                $"CustomProviderOptions.Options[{CommandTimeoutOption}] must be a whole number of seconds");
        }

        ValidateRange(
            $"CustomProviderOptions.Options[{CommandTimeoutOption}]",
            commandTimeout,
            MinimumCommandTimeoutSeconds,
            MaximumCommandTimeoutSeconds);

        var passfilePermissionPolicy = PassfilePermissionPolicy.OwnerOnly;
        if (passfilePermissionPolicyOption is not null)
        {
            if (!string.Equals(
                    passfilePermissionPolicyOption.Value,
                    OwnerOrGroupReadPolicy,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw ConfigurationError(
                    $"CustomProviderOptions.Options[{PassfilePermissionPolicyOption}] must be '{OwnerOrGroupReadPolicy}'");
            }

            passfilePermissionPolicy = PassfilePermissionPolicy.OwnerOrGroupRead;
        }

        return new ValidatedCustomOptions(commandTimeout, passfilePermissionPolicy);
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

    private readonly record struct ValidatedCustomOptions(
        int CommandTimeout,
        PassfilePermissionPolicy PassfilePermissionPolicy);
}
