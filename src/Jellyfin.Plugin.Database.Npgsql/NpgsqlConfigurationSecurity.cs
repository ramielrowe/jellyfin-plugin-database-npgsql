using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Owns security-sensitive configuration checks before Npgsql can consume ambient state or named files.
/// </summary>
internal static class NpgsqlConfigurationSecurity
{
    /// <summary>The PostgreSQL environment variables consumed by Npgsql 10.0.3.</summary>
    // This is the complete PostgresEnvironment surface in Npgsql 10.0.3. In
    // particular, that release does not read PGHOST, PGPORT, PGDATABASE, or
    // PGSSLMODE. Keep this allowlist synchronized when upgrading Npgsql.
    internal static readonly string[] RecognizedPostgresEnvironmentVariables =
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

    private const long MaximumPassfileBytes = 1024 * 1024;

    /// <summary>
    /// Rejects ambient PostgreSQL settings so database.xml is the sole configuration authority.
    /// </summary>
    internal static void RejectRecognizedEnvironmentFallbacks()
    {
        foreach (var variable in RecognizedPostgresEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(variable) is not null)
            {
                throw ConfigurationError(
                    $"environment variable '{variable}' must be unset because database.xml is the only supported configuration source");
            }
        }
    }

    /// <summary>
    /// Validates configured secret files without allowing their paths or contents into an exception chain.
    /// </summary>
    /// <param name="builder">The parsed database.xml connection settings.</param>
    /// <returns>A data-source configurator that owns the preloaded secret material, or <see langword="null" />.</returns>
    internal static Action<NpgsqlDataSourceBuilder>? PrepareSecretFiles(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        List<PassfileEntry>? passfileEntries = null;
        X509Certificate2Collection? clientCertificates = null;
        X509Certificate2Collection? rootCertificates = null;

        if (!string.IsNullOrWhiteSpace(builder.Passfile))
        {
            // Npgsql gives an explicitly configured password precedence over a
            // passfile, including when the configured password is empty. Do
            // not read or retain an otherwise-unused credential file.
            if (builder.Password is null)
            {
                passfileEntries = ReadPassfile(builder.Passfile);
            }

            builder.Passfile = null;
        }
        else if (builder.Password is null)
        {
            RejectDefaultFile("password file", GetDefaultPassfilePath());
        }

        var certificatePath = builder.SslCertificate;
        var keyPath = builder.SslKey;
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            clientCertificates = LoadClientCertificates(certificatePath, keyPath, builder.SslPassword);
            builder.SslCertificate = null;
            builder.SslKey = null;
            builder.SslPassword = null;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                throw ConfigurationError("SSL Key requires SSL Certificate");
            }

            if (!string.IsNullOrWhiteSpace(builder.SslPassword))
            {
                throw ConfigurationError("SSL Password requires SSL Certificate");
            }

            RejectDefaultFile("client TLS certificate", GetDefaultCertificatePath("postgresql.crt"));
        }

        if (!string.IsNullOrWhiteSpace(builder.RootCertificate))
        {
            rootCertificates = LoadRootCertificates(builder.RootCertificate);
            builder.RootCertificate = null;
        }
        else
        {
            RejectDefaultFile("root TLS certificate", GetDefaultCertificatePath("root.crt"));
        }

        // Explicit client certificate settings have been moved into memory, so
        // a conventional home-directory certificate must not be allowed to join
        // the configured collection later when Npgsql opens a connection.
        RejectDefaultFile("client TLS certificate", GetDefaultCertificatePath("postgresql.crt"));

        if (passfileEntries is null && clientCertificates is null && rootCertificates is null)
        {
            return null;
        }

        var usePassfilePasswordProvider = passfileEntries is not null;
        return dataSourceBuilder =>
        {
            if (usePassfilePasswordProvider)
            {
                string ResolvePassword(NpgsqlConnectionStringBuilder settings)
                    => ResolvePassfilePassword(passfileEntries!, settings);

                dataSourceBuilder.UsePasswordProvider(
                    ResolvePassword,
                    (settings, _) => ValueTask.FromResult(ResolvePassword(settings)));
            }

#pragma warning disable CS0618 // This Npgsql release has no non-obsolete collection API that preserves client certificate chains.
            if (clientCertificates is not null)
            {
                dataSourceBuilder.UseClientCertificates(clientCertificates);
            }
#pragma warning restore CS0618

            if (rootCertificates is not null)
            {
                dataSourceBuilder.UseRootCertificates(rootCertificates);
            }
        };
    }

    private static List<PassfileEntry> ReadPassfile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumPassfileBytes)
            {
                throw new IOException();
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException();
            }

            var entries = new List<PassfileEntry>();
            using var reader = new StreamReader(
                new MemoryStream(bytes, writable: false),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                var candidate = line.Trim();
                if (candidate.Length == 0 || candidate[0] == '#')
                {
                    continue;
                }

                var fields = SplitPassfileLine(candidate);
                if (fields.Count != 5
                    || (fields[1] != "*" && !int.TryParse(fields[1], out _)))
                {
                    throw new FormatException();
                }

                entries.Add(new PassfileEntry(
                    WildcardToNull(fields[0]),
                    WildcardToNull(fields[1]),
                    WildcardToNull(fields[2]),
                    WildcardToNull(fields[3]),
                    WildcardToNull(fields[4])));
            }

            return entries;
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError("Passfile must name a readable, well-formed PostgreSQL password file");
        }
    }

    private static X509Certificate2Collection LoadClientCertificates(string certificatePath, string? keyPath, string? password)
    {
        try
        {
            if (string.Equals(Path.GetExtension(certificatePath), ".pfx", StringComparison.OrdinalIgnoreCase))
            {
                var certificates = X509CertificateLoader.LoadPkcs12CollectionFromFile(certificatePath, password);
                if (certificates.Count == 0)
                {
                    throw new FormatException();
                }

                return certificates;
            }

            if (string.IsNullOrWhiteSpace(keyPath))
            {
                throw new FormatException();
            }

#pragma warning disable CA2000 // Ownership transfers to the collection retained by the Npgsql data source.
            var certificate = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(certificatePath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(certificatePath, password, keyPath);
#pragma warning restore CA2000
            var result = new X509Certificate2Collection(certificate);
            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(certificatePath);
            foreach (var chainCertificate in chain)
            {
                if (!string.Equals(chainCertificate.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(chainCertificate);
                }
                else
                {
                    chainCertificate.Dispose();
                }
            }

            return result;
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError(
                "SSL Certificate and SSL Key must name a readable, valid PEM pair, or SSL Certificate must name a readable, valid PFX file");
        }
    }

    private static X509Certificate2Collection LoadRootCertificates(string path)
    {
        try
        {
            var certificates = new X509Certificate2Collection();
            if (!string.Equals(Path.GetExtension(path), ".pfx", StringComparison.OrdinalIgnoreCase))
            {
                certificates.ImportFromPemFile(path);
            }

            if (certificates.Count == 0)
            {
                certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(path, null));
            }

            return certificates;
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError("Root Certificate must name a readable, valid PEM or unencrypted PFX certificate file");
        }
    }

    private static List<string> SplitPassfileLine(string line)
    {
        var fields = new List<string>(5);
        var field = new StringBuilder();
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && index < line.Length - 1)
            {
                var escaped = line[++index];
                if (escaped is not (':' or '\\'))
                {
                    field.Append('\\');
                }

                field.Append(escaped);
            }
            else if (character == ':')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static string ResolvePassfilePassword(
        IEnumerable<PassfileEntry> entries,
        NpgsqlConnectionStringBuilder settings)
    {
        foreach (var entry in entries)
        {
            if (Matches(entry.Host, settings.Host)
                && Matches(entry.Port, settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                && Matches(entry.Database, settings.Database)
                && Matches(entry.Username, settings.Username))
            {
                return entry.Password
                    ?? throw ConfigurationError("Passfile contains no usable password for the configured PostgreSQL endpoint and identity");
            }
        }

        throw ConfigurationError("Passfile contains no entry matching the configured PostgreSQL endpoint and identity");
    }

    private static bool Matches(string? configured, string? actual)
        => configured is null || string.Equals(configured, actual, StringComparison.Ordinal);

    private static void RejectDefaultFile(string role, string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                throw ConfigurationError(
                    $"the default PostgreSQL {role} is present; configure the corresponding database.xml connection parameter explicitly or remove the default file");
            }
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError(
                $"the default PostgreSQL {role} could not be checked safely; configure the corresponding database.xml connection parameter explicitly");
        }
    }

    private static string? GetDefaultPassfilePath()
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "APPDATA" : "HOME");
        if (home is null)
        {
            return null;
        }

        try
        {
            return OperatingSystem.IsWindows()
                ? Path.Combine(home, "postgresql", "pgpass.conf")
                : Path.Combine(home, ".pgpass");
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError("the default PostgreSQL password file location could not be checked safely");
        }
    }

    private static string? GetDefaultCertificatePath(string fileName)
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "APPDATA" : "HOME");
        if (home is null)
        {
            return null;
        }

        try
        {
            return Path.Combine(home, OperatingSystem.IsWindows() ? "postgresql" : ".postgresql", fileName);
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError("the default PostgreSQL TLS file location could not be checked safely");
        }
    }

    private static string? WildcardToNull(string value)
        => value == "*" ? null : value;

    private static bool IsSecretFileFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException
            or SecurityException
            or ArgumentException
            or NotSupportedException
            or FormatException;

    private static InvalidOperationException ConfigurationError(string detail)
        => new($"The Npgsql provider configuration in database.xml is invalid: {detail}. No database connection was attempted.");

    private sealed record PassfileEntry(string? Host, string? Port, string? Database, string? Username, string? Password);
}
