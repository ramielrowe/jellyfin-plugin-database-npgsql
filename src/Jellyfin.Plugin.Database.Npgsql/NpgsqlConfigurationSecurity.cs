using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
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
    private const long MaximumCertificateBytes = 4 * 1024 * 1024;
    private const UnixFileMode GroupOrOtherPermissions =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

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

        string? passfilePassword = null;
        X509Certificate2Collection? clientCertificates = null;
        X509Certificate2Collection? rootCertificates = null;

        if (!string.IsNullOrWhiteSpace(builder.Passfile))
        {
            // Npgsql gives an explicitly configured password precedence over a
            // passfile, including when the configured password is empty. Do
            // not read or retain an otherwise-unused credential file.
            if (builder.Password is null)
            {
                passfilePassword = ReadPassfilePassword(builder.Passfile, builder);
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

        if (passfilePassword is null && clientCertificates is null && rootCertificates is null)
        {
            return null;
        }

        var usePassfilePasswordProvider = passfilePassword is not null;
        return dataSourceBuilder =>
        {
            if (usePassfilePasswordProvider)
            {
                dataSourceBuilder.UsePasswordProvider(
                    _ => passfilePassword!,
                    (_, _) => ValueTask.FromResult(passfilePassword!));
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

    private static string ReadPassfilePassword(string path, NpgsqlConnectionStringBuilder settings)
    {
        byte[]? bytes = null;
        try
        {
            bytes = ReadConfiguredFile(path, MaximumPassfileBytes, requireOwnerOnlyOnUnix: true);
            using var reader = new StreamReader(
                new MemoryStream(bytes, writable: false),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var fields = SplitPassfileLine(line);
                if (fields.Count != 5
                    || (fields[1] != "*" && !int.TryParse(fields[1], out _)))
                {
                    throw new FormatException();
                }

                if (Matches(WildcardToNull(fields[0]), settings.Host)
                    && Matches(WildcardToNull(fields[1]), settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    && Matches(WildcardToNull(fields[2]), settings.Database)
                    && Matches(WildcardToNull(fields[3]), settings.Username))
                {
                    // PostgreSQL uses the first matching line. Retain only the
                    // credential that can apply to this configured identity,
                    // rather than every password in the file.
                    return fields[4];
                }
            }

            throw ConfigurationError("Passfile contains no entry matching the configured PostgreSQL endpoint and identity");
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError(
                "Passfile must name a readable, well-formed PostgreSQL password file and, on Unix, use owner-only permissions");
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static X509Certificate2Collection LoadClientCertificates(string certificatePath, string? keyPath, string? password)
    {
        try
        {
            if (IsPkcs12Path(certificatePath))
            {
                var bytes = ReadConfiguredFile(certificatePath, MaximumCertificateBytes, requireOwnerOnlyOnUnix: true);
                try
                {
                    var certificates = X509CertificateLoader.LoadPkcs12Collection(
                        bytes,
                        password,
                        X509KeyStorageFlags.EphemeralKeySet,
                        Pkcs12LoaderLimits.Defaults);
                    if (certificates.Count == 0)
                    {
                        throw new FormatException();
                    }

                    return certificates;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            if (string.IsNullOrWhiteSpace(keyPath))
            {
                throw new FormatException();
            }

            var certificateBytes = ReadConfiguredFile(certificatePath, MaximumCertificateBytes, requireOwnerOnlyOnUnix: false);
            byte[]? keyBytes = null;
            char[]? certificateChars = null;
            char[]? keyChars = null;
            try
            {
                keyBytes = ReadConfiguredFile(keyPath, MaximumCertificateBytes, requireOwnerOnlyOnUnix: true);
                certificateChars = Encoding.UTF8.GetChars(certificateBytes);
                keyChars = Encoding.UTF8.GetChars(keyBytes);
#pragma warning disable CA2000 // Ownership transfers to the collection retained by the Npgsql data source.
                var certificate = string.IsNullOrEmpty(password)
                    ? X509Certificate2.CreateFromPem(certificateChars, keyChars)
                    : X509Certificate2.CreateFromEncryptedPem(certificateChars, password, keyChars);
#pragma warning restore CA2000
                var result = new X509Certificate2Collection(certificate);
                var chain = new X509Certificate2Collection();
                chain.ImportFromPem(certificateChars);
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
            finally
            {
                CryptographicOperations.ZeroMemory(certificateBytes);
                if (keyBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(keyBytes);
                }

                ZeroChars(certificateChars);
                ZeroChars(keyChars);
            }
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError(
                "SSL Certificate and SSL Key must name a readable, valid PEM pair, or SSL Certificate must name a readable, valid PFX/PKCS#12 file; on Unix, the private key or PFX/PKCS#12 file must use owner-only permissions");
        }
    }

    private static X509Certificate2Collection LoadRootCertificates(string path)
    {
        try
        {
            var certificates = new X509Certificate2Collection();
            var bytes = ReadConfiguredFile(path, MaximumCertificateBytes, requireOwnerOnlyOnUnix: false);
            try
            {
                if (!IsPkcs12Path(path))
                {
                    var chars = Encoding.UTF8.GetChars(bytes);
                    try
                    {
                        certificates.ImportFromPem(chars);
                    }
                    finally
                    {
                        ZeroChars(chars);
                    }
                }

                if (certificates.Count == 0)
                {
                    certificates.Add(X509CertificateLoader.LoadPkcs12(
                        bytes,
                        ReadOnlySpan<char>.Empty,
                        X509KeyStorageFlags.EphemeralKeySet,
                        Pkcs12LoaderLimits.Defaults));
                }

                return certificates;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
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

    private static byte[] ReadConfiguredFile(string path, long maximumBytes, bool requireOwnerOnlyOnUnix)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (!stream.CanSeek || stream.Length > maximumBytes)
        {
            throw new IOException();
        }

        // Inspect the object represented by the open handle rather than the
        // pathname. If a pathname is a symlink, or is replaced between lookup
        // and open, this validates the actual file whose bytes are consumed.
        // Windows has ACLs instead of Unix mode bits; its documented contract
        // requires operators to restrict the file ACL to the Jellyfin account.
        if (requireOwnerOnlyOnUnix && !OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(stream.SafeFileHandle);
            if ((mode & GroupOrOtherPermissions) != 0)
            {
                throw new UnauthorizedAccessException();
            }
        }

        var bytes = new byte[stream.Length];
        var complete = false;
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException();
            }

            complete = true;
            return bytes;
        }
        finally
        {
            if (!complete)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static bool Matches(string? configured, string? actual)
        => configured is null || string.Equals(configured, actual, StringComparison.Ordinal);

    private static bool IsPkcs12Path(string path)
        => string.Equals(Path.GetExtension(path), ".pfx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".p12", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".pkcs12", StringComparison.OrdinalIgnoreCase);

    private static void ZeroChars(char[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }

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
}
