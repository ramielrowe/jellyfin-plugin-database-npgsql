using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

[Collection(PostgresEnvironmentCollection.Name)]
public sealed class ProviderCoreTests
{
    private const string Password = "correct horse battery staple";
    private const string ValidConnectionString =
        "Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Password=" + Password;

    [Fact]
    public void VanillaDatabaseXmlIsTheAuthoritativeConfigurationSource()
    {
        const string Xml = """
            <DatabaseConfigurationOptions>
              <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
              <CustomProviderOptions>
                <PluginName>Npgsql Database_0.1.2.0</PluginName>
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
        Assert.Equal(
            NpgsqlOptionsBootstrap.PostgreSqlSessionOptions,
            new NpgsqlConnectionStringBuilder(configured.ConnectionString).Options);
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
                Options(
                    ("passfile-permission-policy", "owner-or-group-read"),
                    ("PASSFILE-PERMISSION-POLICY", "owner-or-group-read"))));
        Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                ValidConnectionString,
                Options(("command_timeout", "30"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner-only")]
    [InlineData("group-read")]
    public void Parse_RejectsUnsupportedPassfilePermissionPolicy(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                ValidConnectionString,
                Options(("passfile-permission-policy", value))));

        Assert.Contains("owner-or-group-read", exception.Message, StringComparison.Ordinal);
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
    public void Parse_PreflightsAndPreservesTlsPassfileAndPoolConfiguration()
    {
        using var files = SecretFiles.Create();
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile};"
            + $"SSL Mode=VerifyFull;Root Certificate={files.RootCertificate};Pooling=true;Minimum Pool Size=2;Maximum Pool Size=32",
            null);
        var normalized = new NpgsqlConnectionStringBuilder(configured.ConnectionString);

        Assert.Null(normalized.Passfile);
        Assert.Null(normalized.RootCertificate);
        Assert.DoesNotContain(files.Passfile, configured.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain(files.RootCertificate, configured.ConnectionString, StringComparison.Ordinal);
        Assert.NotNull(configured.ConfigureDataSource);
        Assert.Equal(SslMode.VerifyFull, normalized.SslMode);
        Assert.Equal(2, normalized.MinPoolSize);
        Assert.Equal(32, normalized.MaxPoolSize);
        Assert.Equal("public", normalized.SearchPath);
        Assert.Equal("UTC", normalized.Timezone);
        Assert.Equal("-c jit=off", normalized.Options);
        Assert.True(configured.PassfileConfigured);
        Assert.True(configured.Pooling);
    }

    [Fact]
    public void EndpointLogContainsNoCredentialsOrSecretFilePaths()
    {
        using var files = SecretFiles.Create();
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Database=jellyfin;Username=sensitive-user;Password={Password};Passfile={files.Passfile};"
            + $"SSL Mode=VerifyFull;Root Certificate={files.RootCertificate}",
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
        Assert.DoesNotContain(files.Passfile, message, StringComparison.Ordinal);
        Assert.DoesNotContain(files.RootCertificate, message, StringComparison.Ordinal);
        Assert.DoesNotContain(configured.ConnectionString, message, StringComparison.Ordinal);
    }

    public static TheoryData<string> RecognizedPostgresEnvironmentVariables
        => new(NpgsqlConfigurationSecurity.RecognizedPostgresEnvironmentVariables);

    [Fact]
    public void EnvironmentFallbackAuditMatchesTheCompleteNpgsql10Surface()
    {
        string[] expected =
        [
            "PGUSER",
            "PGPASSWORD",
            "PGPASSFILE",
            "PGSSLCERT",
            "PGSSLKEY",
            "PGSSLROOTCERT",
            "PGCLIENTENCODING",
            "PGTZ",
            "PGOPTIONS",
            "PGTARGETSESSIONATTRS",
            "PGSSLNEGOTIATION",
            "PGGSSENCMODE",
            "PGREQUIREAUTH",
            "PGAPPNAME",
        ];

        Assert.Equal(expected, NpgsqlConfigurationSecurity.RecognizedPostgresEnvironmentVariables);
    }

    [Theory]
    [MemberData(nameof(RecognizedPostgresEnvironmentVariables))]
    public void Parse_RejectsEveryNpgsql10PostgresEnvironmentFallback(string variable)
    {
        using var environment = new EnvironmentVariableScope(variable, "ambient-secret-or-setting");

        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString, null));

        Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ambient-secret-or-setting", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-c search_path=private")]
    [InlineData("-c default_transaction_read_only=on")]
    public void Parse_RejectsPgOptionsBeforeItCanChangeSessionState(string value)
    {
        using var environment = new EnvironmentVariableScope("PGOPTIONS", value);

        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(ValidConnectionString, null));

        Assert.Contains("PGOPTIONS", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IgnoresLibpqEndpointVariablesThatNpgsql10DoesNotConsume()
    {
        using var host = new EnvironmentVariableScope("PGHOST", "attacker.invalid");
        using var port = new EnvironmentVariableScope("PGPORT", "1");
        using var database = new EnvironmentVariableScope("PGDATABASE", "attacker_database");
        using var sslMode = new EnvironmentVariableScope("PGSSLMODE", "disable");

        var configured = NpgsqlOptionsBootstrap.Parse(ValidConnectionString, null);

        Assert.Equal("database.internal", configured.Host);
        Assert.Equal(5544, configured.Port);
        Assert.Equal("jellyfin", configured.Database);
        Assert.Equal(nameof(SslMode.Prefer), configured.SslMode);
    }

    [Theory]
    [InlineData("Passfile", "missing.pgpass")]
    [InlineData("Root Certificate", "missing-root.pem")]
    [InlineData("SSL Certificate", "missing-client.pem")]
    public void Parse_SecretFileErrorsDoNotExposeConfiguredPaths(string setting, string fileName)
    {
        var secretDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-sensitive-path", Guid.NewGuid().ToString("N"));
        var secretPath = Path.Combine(secretDirectory, fileName);
        var connectionString = setting switch
        {
            "Passfile" => $"Host=db;Database=jellyfin;Username=user;Passfile={secretPath}",
            "Root Certificate" => $"{ValidConnectionString};SSL Mode=VerifyFull;Root Certificate={secretPath}",
            _ => $"{ValidConnectionString};SSL Mode=Require;SSL Certificate={secretPath};SSL Key={secretPath}.key",
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(connectionString, null));

        Assert.DoesNotContain(secretDirectory, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fileName, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Parse_MalformedSecretPathDoesNotEscapeSanitization()
    {
        const string SecretPathFragment = "credential-secret";
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlOptionsBootstrap.Parse(
                $"Host=db;Database=jellyfin;Username=user;Passfile={SecretPathFragment}\0.pgpass",
                null));

        Assert.DoesNotContain(SecretPathFragment, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Parse_ExplicitPasswordKeepsPrecedenceWithoutReadingUnusedPassfile()
    {
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"{ValidConnectionString};Passfile=/missing/unused/credential.pgpass",
            null);
        var normalized = new NpgsqlConnectionStringBuilder(configured.ConnectionString);

        Assert.Equal(Password, normalized.Password);
        Assert.Null(normalized.Passfile);
        Assert.Null(configured.ConfigureDataSource);
        Assert.True(configured.PassfileConfigured);
    }

    [Fact]
    public void PreloadedPassfileAndTlsMaterialBuildADataSourceWithoutFilePaths()
    {
        using var files = SecretFiles.Create();
        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile};"
            + $"SSL Mode=VerifyFull;SSL Certificate={files.ClientCertificate};SSL Key={files.ClientKey};Root Certificate={files.RootCertificate}",
            null);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(configured.ConnectionString);

        configured.ConfigureDataSource!(dataSourceBuilder);
        using var dataSource = dataSourceBuilder.Build();

        Assert.DoesNotContain(files.Directory, dataSource.ConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void PassfileResolutionUsesEachMultiHostChildPoolsSettings()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "primary.internal:5544:jellyfin:jellyfin_runtime:primary-password\n"
            + "standby.internal:5545:jellyfin:jellyfin_runtime:standby-password\n");
        var primary = PassfileSettings("primary.internal", 5544);
        var standby = PassfileSettings("standby.internal", 5545);

        Assert.Equal(
            "primary-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(files.Passfile, primary));
        Assert.Equal(
            "standby-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(files.Passfile, standby));
    }

    [Fact]
    public void PassfileResolutionMatchesNpgsqlTrimmingEscapingAndFirstMatchPrecedence()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "   # leading whitespace still denotes a comment\n"
            + " database.internal:5544:jelly\\:fin:user\\\\name:first\\:password\\\\tail   \n"
            + "database.internal:5544:jelly\\:fin:user\\\\name:lower-precedence-secret\n"
            + "malformed-line-after-the-first-match\n");
        var settings = PassfileSettings("database.internal", 5544, "jelly:fin", @"user\name");

        var password = NpgsqlConfigurationSecurity.ResolvePassfilePassword(files.Passfile, settings);

        Assert.Equal(@"first:password\tail", password);
    }

    [Fact]
    public void PassfileResolutionReturnsFirstMatchBeforeUnrelatedCredentialsInTheSameRead()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "database.internal:5544:jellyfin:jellyfin_runtime:first-password\n"
            + "unrelated.internal:5544:other:other-user:unrelated-secret-that-must-not-survive-read-ahead\n"
            + "malformed-line-after-the-first-match\n");

        Assert.Equal(
            "first-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void PassfileResolutionMatchesStreamReaderLineEndingSemantics(string lineEnding)
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            lineEnding
            + $"other.internal:5544:jellyfin:jellyfin_runtime:other-secret{lineEnding}"
            + $"database.internal:5544:jellyfin:jellyfin_runtime:matching-password{lineEnding}"
            + $"{lineEnding}malformed-line-after-the-first-match");

        Assert.Equal(
            "matching-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));
    }

    [Fact]
    public void PassfileResolutionConsumesCrLfOnceAcrossReadBoundaries()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "#" + new string('x', 258) + "\r\n"
            + "database.internal:5544:jellyfin:jellyfin_runtime:boundary-password\r\n");

        Assert.Equal(
            "boundary-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));
    }

    [Fact]
    public void PassfileResolutionMatchesNpgsqlWildcardPasswordAndNoMatchSemantics()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "database.internal:*:*:*:*\n"
            + "malformed-line-after-the-null-match\n");

        Assert.Null(
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));

        File.WriteAllText(files.Passfile, "other.internal:5544:jellyfin:jellyfin_runtime:unused-secret\n");
        Assert.Null(
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));
    }

    [Fact]
    public void PassfileResolutionDetectsBomLikeNpgsqlStreamReader()
    {
        using var files = SecretFiles.Create();
        File.WriteAllText(
            files.Passfile,
            "database.internal:5544:jellyfin:jellyfin_runtime:utf16-password\n",
            Encoding.Unicode);

        Assert.Equal(
            "utf16-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));
    }

    [Fact]
    public void PassfileCallbackParseFailuresRemainSanitized()
    {
        using var files = SecretFiles.Create();
        const string Secret = "callback-parser-secret";
        File.WriteAllText(files.Passfile, $"malformed-{Secret}\n");

        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544)));

        Assert.Contains("No password was returned", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Directory, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    public static TheoryData<UnixFileMode> InsecureCredentialFileModes
        => new()
        {
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite,
        };

    [Fact]
    public void Parse_AcceptsOwnerOnlyUnixCredentialFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.Passfile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.ClientKey));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.ClientPkcs12));

        var passfileAndPem = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile};"
            + $"SSL Mode=VerifyFull;SSL Certificate={files.ClientCertificate};SSL Key={files.ClientKey};Root Certificate={files.RootCertificate}",
            null);
        var pkcs12 = NpgsqlOptionsBootstrap.Parse(
            $"{ValidConnectionString};SSL Mode=Require;SSL Certificate={files.ClientPkcs12};SSL Password={Password}",
            null);

        Assert.NotNull(passfileAndPem.ConfigureDataSource);
        Assert.NotNull(pkcs12.ConfigureDataSource);
    }

    [Theory]
    [MemberData(nameof(InsecureCredentialFileModes))]
    public void Parse_RejectsInsecureUnixPassfilePermissions(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();
        File.SetUnixFileMode(files.Passfile, mode);

        AssertSanitizedCredentialFileFailure(
            () => NpgsqlOptionsBootstrap.Parse(
                $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile}",
                null),
            files);
    }

    [Fact]
    public void Parse_AcceptsPrivateProcessGroupReadableUnixPassfileOnlyWhenExplicitlyConfigured()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var files = SecretFiles.Create();
        File.SetUnixFileMode(
            files.Passfile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile}",
            Options(("passfile-permission-policy", "OWNER-OR-GROUP-READ")));

        Assert.NotNull(configured.ConfigureDataSource);
        Assert.Equal(
            "test-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(
                files.Passfile,
                PassfileSettings("database.internal", 5544),
                PassfilePermissionPolicy.OwnerOrGroupRead));
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public void Parse_GroupReadablePolicyStillRejectsBroaderUnixPermissions(UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var files = SecretFiles.Create();
        File.SetUnixFileMode(files.Passfile, mode);

        AssertSanitizedCredentialFileFailure(
            () => NpgsqlOptionsBootstrap.Parse(
                $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={files.Passfile}",
                Options(("passfile-permission-policy", "owner-or-group-read"))),
            files);
    }

    [Fact]
    public void GroupReadablePolicyRequiresTheOpenedFileGroupToBelongToTheProcess()
    {
        uint[] supplementaryGroups = [1001, 1002];

        Assert.True(NpgsqlConfigurationSecurity.ProcessGroupIncludesFileGroup(1000, 1000, supplementaryGroups));
        Assert.True(NpgsqlConfigurationSecurity.ProcessGroupIncludesFileGroup(1002, 1000, supplementaryGroups));
        Assert.False(NpgsqlConfigurationSecurity.ProcessGroupIncludesFileGroup(2000, 1000, supplementaryGroups));
    }

    [Theory]
    [MemberData(nameof(InsecureCredentialFileModes))]
    public void Parse_RejectsInsecureUnixPemPrivateKeyPermissions(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();
        File.SetUnixFileMode(files.ClientKey, mode);

        AssertSanitizedCredentialFileFailure(
            () => NpgsqlOptionsBootstrap.Parse(
                $"{ValidConnectionString};SSL Mode=Require;SSL Certificate={files.ClientCertificate};SSL Key={files.ClientKey}",
                null),
            files);
    }

    [Theory]
    [MemberData(nameof(InsecureCredentialFileModes))]
    public void Parse_RejectsInsecureUnixPkcs12Permissions(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();
        File.SetUnixFileMode(files.ClientPkcs12, mode);

        AssertSanitizedCredentialFileFailure(
            () => NpgsqlOptionsBootstrap.Parse(
                $"{ValidConnectionString};SSL Mode=Require;SSL Certificate={files.ClientPkcs12};SSL Password={Password}",
                null),
            files);
    }

    [Fact]
    public void Parse_ChecksUnixPermissionsOnTheOpenedSymlinkTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();
        var passfileLink = Path.Combine(files.Directory, "pgpass-link");
        File.CreateSymbolicLink(passfileLink, files.Passfile);

        var configured = NpgsqlOptionsBootstrap.Parse(
            $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={passfileLink}",
            null);
        Assert.NotNull(configured.ConfigureDataSource);

        File.SetUnixFileMode(files.Passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        AssertSanitizedCredentialFileFailure(
            () => NpgsqlOptionsBootstrap.Parse(
                $"Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin_runtime;Passfile={passfileLink}",
                null),
            files);
    }

    [Fact]
    public void PassfileCallbackReopensAndRechecksTheSymlinkTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = SecretFiles.Create();
        var passfileLink = Path.Combine(files.Directory, "callback-pgpass-link");
        File.CreateSymbolicLink(passfileLink, files.Passfile);
        var settings = PassfileSettings("database.internal", 5544);

        Assert.Equal(
            "test-password",
            NpgsqlConfigurationSecurity.ResolvePassfilePassword(passfileLink, settings));

        File.SetUnixFileMode(
            files.Passfile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var exception = Assert.Throws<InvalidOperationException>(
            () => NpgsqlConfigurationSecurity.ResolvePassfilePassword(passfileLink, settings));

        Assert.Contains("No password was returned", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Directory, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-password", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
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
    public async Task FastBackupOperationsRequireInitializedProviderAndSafeKeys()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);

        var create = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.MigrationBackupFast(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RestoreBackupFast("unused", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteBackup("unused"));
        Assert.Contains("factory is unavailable", create.Message, StringComparison.Ordinal);
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

    private static NpgsqlConnectionStringBuilder PassfileSettings(
        string host,
        int port,
        string database = "jellyfin",
        string username = "jellyfin_runtime")
        => new()
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
        };

    private static void AssertSanitizedCredentialFileFailure(
        Action action,
        SecretFiles files)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);

        Assert.Contains("permission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Directory, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

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

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            this.originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable(this.name, this.originalValue);
    }

    private sealed class SecretFiles : IDisposable
    {
        private SecretFiles(
            string directory,
            string passfile,
            string rootCertificate,
            string clientCertificate,
            string clientKey,
            string clientPkcs12)
        {
            this.Directory = directory;
            this.Passfile = passfile;
            this.RootCertificate = rootCertificate;
            this.ClientCertificate = clientCertificate;
            this.ClientKey = clientKey;
            this.ClientPkcs12 = clientPkcs12;
        }

        public string Directory { get; }

        public string Passfile { get; }

        public string RootCertificate { get; }

        public string ClientCertificate { get; }

        public string ClientKey { get; }

        public string ClientPkcs12 { get; }

        public static SecretFiles Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-unit-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var passfile = Path.Combine(directory, "pgpass");
            File.WriteAllText(passfile, "database.internal:5544:jellyfin:jellyfin_runtime:test-password\n");

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=jellyfin-unit-root", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var rootCertificate = Path.Combine(directory, "root.pem");
            File.WriteAllText(rootCertificate, certificate.ExportCertificatePem());
            var clientCertificate = Path.Combine(directory, "client.pem");
            File.WriteAllText(clientCertificate, certificate.ExportCertificatePem());
            var clientKey = Path.Combine(directory, "client-key.pem");
            File.WriteAllText(clientKey, rsa.ExportPkcs8PrivateKeyPem());
            var clientPkcs12 = Path.Combine(directory, "client.p12");
            var pkcs12Bytes = certificate.Export(X509ContentType.Pkcs12, Password);
            try
            {
                File.WriteAllBytes(clientPkcs12, pkcs12Bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12Bytes);
            }

            if (!OperatingSystem.IsWindows())
            {
                var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.SetUnixFileMode(passfile, ownerOnly);
                File.SetUnixFileMode(clientKey, ownerOnly);
                File.SetUnixFileMode(clientPkcs12, ownerOnly);
            }

            return new SecretFiles(directory, passfile, rootCertificate, clientCertificate, clientKey, clientPkcs12);
        }

        public void Dispose()
            => System.IO.Directory.Delete(this.Directory, true);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresEnvironmentCollection
{
    public const string Name = "PostgreSQL environment";
}
