using System.Collections.ObjectModel;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Database.Npgsql.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class ProviderCoreTests
{
    private const string Password = "correct horse battery staple";
    private const string Passfile = "/run/secrets/jellyfin-pgpass";
    private const string RootCertificate = "/run/secrets/postgresql-root.crt";
    private const string ValidConnectionString =
        "Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Password=" + Password;

    [Fact]
    public void VanillaDatabaseXmlIsTheAuthoritativeConfigurationSource()
    {
        const string Xml = """
            <DatabaseConfigurationOptions>
              <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
              <CustomProviderOptions>
                <PluginName>Npgsql Database_0.1.0.0</PluginName>
                <PluginAssembly>Jellyfin.Plugin.Database.Npgsql.dll</PluginAssembly>
                <ConnectionString>Host=database.internal;Database=jellyfin;Username=jellyfin_runtime;Password=secret</ConnectionString>
                <Options>
                  <CustomDatabaseOption>
                    <Key>COMMAND-TIMEOUT</Key>
                    <Value>42</Value>
                  </CustomDatabaseOption>
                </Options>
              </CustomProviderOptions>
              <LockingBehavior>NoLock</LockingBehavior>
            </DatabaseConfigurationOptions>
            """;
        var serializer = new XmlSerializer(typeof(DatabaseConfigurationOptions));
        using var reader = new StringReader(Xml);
        var databaseConfiguration = Assert.IsType<DatabaseConfigurationOptions>(serializer.Deserialize(reader));

        var custom = NpgsqlDatabaseProvider.ValidateDatabaseConfiguration(databaseConfiguration);
        var configured = NpgsqlOptionsBootstrap.Parse(custom.ConnectionString, custom.Options);

        Assert.Equal(42, configured.CommandTimeout);
        Assert.Equal("database.internal", configured.Host);
        Assert.Equal("jellyfin", configured.Database);
    }

    [Fact]
    public void Configure_SelectsPluginMigrationsAndBoundedCommandTimeout()
    {
        var options = new DbContextOptionsBuilder();
        var configured = NpgsqlOptionsBootstrap.Configure(
            options,
            ValidConnectionString,
            Options(("COMMAND-TIMEOUT", "45")));

        var relational = Assert.Single(options.Options.Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal(typeof(NpgsqlDatabaseProvider).Assembly.GetName().Name, relational.MigrationsAssembly);
        Assert.Equal(45, relational.CommandTimeout);
        Assert.Equal(45, configured.CommandTimeout);
        Assert.Contains(
            options.Options.Extensions,
            extension => extension.GetType().Name.Contains("NpgsqlOptionsExtension", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Database=jellyfin;Username=jellyfin_runtime")]
    [InlineData("Host=database.internal;Username=jellyfin_runtime")]
    [InlineData("Host=database.internal;Database=jellyfin")]
    public void Parse_RejectsMissingRequiredConnectionSettings(string connectionString)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(connectionString, null));

        Assert.Contains("database.xml", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MalformedConnectionStringDoesNotExposeCredentials()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                $"Host=db;Database=jellyfin;Username=user;Password={Password};NotAKeyword=value",
                null));

        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("NotAKeyword=value", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("-1")]
    [InlineData("forever")]
    public void Parse_RejectsInvalidCommandTimeout(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString, Options(("command-timeout", value))));

        Assert.Contains("command-timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Timeout=121")]
    [InlineData("Cancellation Timeout=0")]
    [InlineData("Cancellation Timeout=30001")]
    [InlineData("Maximum Pool Size=201")]
    public void Parse_RejectsUnboundedConnectionAndPoolOptions(string option)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString + ";" + option, null));

        Assert.Contains("must be between", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDuplicateOrUnknownCustomOptions()
    {
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                ValidConnectionString,
                Options(("command-timeout", "30"), ("COMMAND-TIMEOUT", "31"))));
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                ValidConnectionString,
                Options(("command_timeout", "30"))));
    }

    [Theory]
    [InlineData("Search Path=private")]
    [InlineData("Search Path=public, private")]
    [InlineData("Options=-c search_path=private")]
    [InlineData("Timezone=America/New_York")]
    [InlineData("No Reset On Close=true")]
    public void Parse_RejectsUnsafeSchemaOrSessionConfiguration(string option)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString + ";" + option, null));

        Assert.Contains("database.xml", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Log Parameters=true")]
    [InlineData("Include Error Detail=true")]
    [InlineData("Include Failed Batched Command=true")]
    [InlineData("Persist Security Info=true")]
    public void Parse_RejectsSensitiveDiagnosticSettings(string option)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString + ";" + option, null));

        Assert.Contains("protect secrets", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PreservesTlsPassfileAndPoolConfigurationWithoutReadingFiles()
    {
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={Passfile};"
            + $"SSL Mode=VerifyFull;Root Certificate={RootCertificate};Pooling=true;Minimum Pool Size=2;Maximum Pool Size=32",
            null);
        var normalized = new NpgsqlConnectionStringBuilder(configured.ConnectionString);

        Assert.Equal(Passfile, normalized.Passfile);
        Assert.Equal(RootCertificate, normalized.RootCertificate);
        Assert.Equal(SslMode.VerifyFull, normalized.SslMode);
        Assert.Equal(2, normalized.MinPoolSize);
        Assert.Equal(32, normalized.MaxPoolSize);
        Assert.Equal("public", normalized.SearchPath);
        Assert.Equal("UTC", normalized.Timezone);
        Assert.True(configured.PassfileConfigured);
        Assert.True(configured.Pooling);
    }

    [Fact]
    public void EndpointLogContainsNoCredentialsOrSecretFilePaths()
    {
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Database=jellyfin;Username=sensitive-user;Password={Password};Passfile={Passfile};"
            + $"SSL Mode=VerifyFull;Root Certificate={RootCertificate}",
            null);
        var logger = new CollectingLogger();

        NpgsqlDatabaseProvider.LogDatabaseEndpoint(
            logger,
            configured.Host,
            configured.Port,
            configured.Database,
            configured.SslMode,
            configured.Pooling,
            configured.PassfileConfigured,
            configured.CommandTimeout);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("database.internal:5432", message, StringComparison.Ordinal);
        Assert.Contains("jellyfin", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-user", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Passfile, message, StringComparison.Ordinal);
        Assert.DoesNotContain(RootCertificate, message, StringComparison.Ordinal);
        Assert.DoesNotContain(configured.ConnectionString, message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseConfigurationRequiresPluginProviderAndNoLock()
    {
        var wrongType = CreateConfiguration();
        wrongType.DatabaseType = "Jellyfin-SQLite";
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateDatabaseConfiguration(wrongType));

        var locking = CreateConfiguration();
        locking.LockingBehavior = DatabaseLockingBehaviorTypes.Pessimistic;
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateDatabaseConfiguration(locking));
        Assert.Contains("NoLock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseConfigurationRequiresCustomOptionsAndConnectionString()
    {
        var missingCustomOptions = CreateConfiguration();
        missingCustomOptions.CustomProviderOptions = null;
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateDatabaseConfiguration(missingCustomOptions));

        var missingConnectionString = CreateConfiguration();
        missingConnectionString.CustomProviderOptions!.ConnectionString = " ";
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseProvider.ValidateDatabaseConfiguration(missingConnectionString));
        Assert.Contains("CustomProviderOptions.ConnectionString", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UtcConvertersNormalizeLocalUnspecifiedAndOffsetValues()
    {
        var dateTimeConverter = new UtcDateTimeValueConverter();
        var unspecified = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Local);
        Assert.Equal(
            DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
            Assert.IsType<DateTime>(dateTimeConverter.ConvertToProvider(unspecified)));
        Assert.Equal(
            local.ToUniversalTime(),
            Assert.IsType<DateTime>(dateTimeConverter.ConvertToProvider(local)));

        var offsetConverter = new UtcDateTimeOffsetValueConverter();
        var withOffset = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.FromHours(5));
        var normalized = Assert.IsType<DateTimeOffset>(offsetConverter.ConvertToProvider(withOffset));
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(withOffset.UtcDateTime, normalized.UtcDateTime);
    }

    [Fact]
    public void MaintenanceAndPurgeSqlQuoteAndAllowlistMappedTables()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.Entity<QuotedEntity>().ToTable("odd\"table", "custom\"schema");
        var model = modelBuilder.Model;
        var mappedName = model.FindEntityType(typeof(QuotedEntity))!.GetSchemaQualifiedTableName()!;

        Assert.Equal(
            "ANALYZE \"custom\"\"schema\".\"odd\"\"table\"",
            NpgsqlDatabaseOperations.BuildMaintenanceSql(model, "ANALYZE"));
        Assert.Equal(
            "TRUNCATE TABLE \"custom\"\"schema\".\"odd\"\"table\" RESTART IDENTITY",
            NpgsqlDatabaseOperations.BuildPurgeSql(model, [mappedName]));
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlDatabaseOperations.BuildPurgeSql(model, ["public.unknown; DROP SCHEMA public"]));
        Assert.Throws<ArgumentException>(
            () => NpgsqlDatabaseOperations.BuildMaintenanceSql(model, "VACUUM FULL"));
        Assert.Equal("\"name\"\"with-quote\"", NpgsqlDatabaseOperations.QuoteIdentifier("name\"with-quote"));
    }

    [Fact]
    public async Task MissingFactoryAndCancellationFailBeforeMaintenance()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RefreshStatistics(CancellationToken.None));
        Assert.Contains("factory is unavailable", missing.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RunScheduledOptimisation(cancellation.Token));
    }

    [Fact]
    public async Task ShutdownIsBoundedAndPoolSpecific()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        await provider.RunShutdownTask(CancellationToken.None);
        NpgsqlDatabaseOperations.ClearPool(ValidConnectionString);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RunShutdownTask(cancellation.Token));
    }

    [Fact]
    public async Task FastBackupOperationsAreDeliberatelyGuardedPendingBackupProtocol()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);

        var create = await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.MigrationBackupFast(CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.RestoreBackupFast("unused", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteBackup("unused"));
        Assert.Contains("rollback-capable", create.Message, StringComparison.Ordinal);
        Assert.Contains("before making database changes", create.Message, StringComparison.Ordinal);
    }

    private static DatabaseConfigurationOptions CreateConfiguration()
        => new()
        {
            DatabaseType = "PLUGIN_PROVIDER",
            LockingBehavior = DatabaseLockingBehaviorTypes.NoLock,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = PluginMetadata.InstallationDirectory,
                PluginAssembly = PluginMetadata.AssemblyFileName,
                ConnectionString = ValidConnectionString,
            },
        };

    private static Collection<CustomDatabaseOption> Options(params (string Key, string Value)[] values)
        => new(values.Select(value => new CustomDatabaseOption { Key = value.Key, Value = value.Value }).ToList());

    private sealed class QuotedEntity
    {
        public int Id { get; set; }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Messages.Add(formatter(state, exception));
    }
}
