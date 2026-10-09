using System.Buffers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    /// <returns>A data-source configurator that owns or safely resolves the secret material, or <see langword="null" />.</returns>
    internal static Action<NpgsqlDataSourceBuilder>? PrepareSecretFiles(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? passfilePath = null;
        X509Certificate2Collection? clientCertificates = null;
        X509Certificate2Collection? rootCertificates = null;

        if (!string.IsNullOrWhiteSpace(builder.Passfile))
        {
            // Npgsql gives an explicitly configured password precedence over a
            // passfile, including when the configured password is empty. Do
            // not read or retain an otherwise-unused credential file.
            if (builder.Password is null)
            {
                passfilePath = builder.Passfile;
                ValidateConfiguredFile(passfilePath, MaximumPassfileBytes, requireOwnerOnlyOnUnix: true);
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

        if (passfilePath is null && clientCertificates is null && rootCertificates is null)
        {
            return null;
        }

        var usePassfilePasswordProvider = passfilePath is not null;
        return dataSourceBuilder =>
        {
            if (usePassfilePasswordProvider)
            {
                // A multi-host data source clones these settings once per
                // child pool. Resolve against those per-pool settings at open
                // time so each host gets its own first matching credential.
                dataSourceBuilder.UsePasswordProvider(
                    settings => ResolvePassfilePassword(passfilePath!, settings)!,
                    (settings, _) => ValueTask.FromResult(ResolvePassfilePassword(passfilePath!, settings)!));
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

    /// <summary>
    /// Resolves the first Npgsql-compatible passfile entry for one physical
    /// connection pool. A <see langword="null" /> result deliberately matches
    /// Npgsql for either no matching entry or a matching <c>*</c> password.
    /// </summary>
    /// <param name="path">The configured passfile path.</param>
    /// <param name="settings">The settings for the physical connection pool requesting a password.</param>
    /// <returns>The first matching password, or <see langword="null" />.</returns>
    internal static string? ResolvePassfilePassword(string path, NpgsqlConnectionStringBuilder settings)
    {
        try
        {
            using var handle = OpenConfiguredFile(path, MaximumPassfileBytes, requireOwnerOnlyOnUnix: true);
            return ReadFirstMatchingPassfilePassword(handle, settings);
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            // This executes while Npgsql is opening a physical connection.
            // Keep the path, content, and original exception out of the chain.
            throw new InvalidOperationException(
                "The configured PostgreSQL passfile could not be read safely or parsed. No password was returned to PostgreSQL.");
        }
    }

    private static string? ReadFirstMatchingPassfilePassword(
        SafeFileHandle handle,
        NpgsqlConnectionStringBuilder settings)
    {
        Span<byte> prefix = stackalloc byte[4];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var count = RandomAccess.Read(handle, prefix[prefixLength..], prefixLength);
            if (count == 0)
            {
                break;
            }

            prefixLength += count;
        }

        var (encoding, bomLength) = DetectPassfileEncoding(prefix[..prefixLength]);
        var decoder = encoding.GetDecoder();
        Span<byte> byteBuffer = stackalloc byte[256];
        Span<char> charBuffer = stackalloc char[256];
        var lineBuffer = ArrayPool<char>.Shared.Rent(256);
        var lineLength = 0;
        var previousTerminatorWasCarriageReturn = false;
        long bytesRead = prefixLength;

        try
        {
            if (DecodePassfileBytes(
                    decoder,
                    prefix[bomLength..prefixLength],
                    flush: false,
                    charBuffer,
                    ref lineBuffer,
                    ref lineLength,
                    ref previousTerminatorWasCarriageReturn,
                    settings,
                    out var password))
            {
                return password;
            }

            while (true)
            {
                var count = RandomAccess.Read(handle, byteBuffer, bytesRead);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
                if (bytesRead > MaximumPassfileBytes)
                {
                    throw new IOException();
                }

                var matched = DecodePassfileBytes(
                    decoder,
                    byteBuffer[..count],
                    flush: false,
                    charBuffer,
                    ref lineBuffer,
                    ref lineLength,
                    ref previousTerminatorWasCarriageReturn,
                    settings,
                    out password);
                CryptographicOperations.ZeroMemory(byteBuffer[..count]);
                if (matched)
                {
                    return password;
                }
            }

            if (DecodePassfileBytes(
                    decoder,
                    ReadOnlySpan<byte>.Empty,
                    flush: true,
                    charBuffer,
                    ref lineBuffer,
                    ref lineLength,
                    ref previousTerminatorWasCarriageReturn,
                    settings,
                    out password))
            {
                return password;
            }

            if (lineLength > 0
                && TryResolvePassfileLine(lineBuffer.AsSpan(0, lineLength), settings, out password))
            {
                return password;
            }

            // PgPassFile.GetFirstMatchingEntry returns null when no row
            // matches. Authentication decides whether a password is required.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(byteBuffer);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(charBuffer));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(lineBuffer.AsSpan()));
            ArrayPool<char>.Shared.Return(lineBuffer, clearArray: true);
        }
    }

    private static bool DecodePassfileBytes(
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        bool flush,
        Span<char> charBuffer,
        ref char[] lineBuffer,
        ref int lineLength,
        ref bool previousTerminatorWasCarriageReturn,
        NpgsqlConnectionStringBuilder settings,
        out string? password)
    {
        do
        {
            decoder.Convert(bytes, charBuffer, flush, out var bytesUsed, out var charsUsed, out var completed);
            bytes = bytes[bytesUsed..];

            for (var index = 0; index < charsUsed; index++)
            {
                var character = charBuffer[index];
                if (previousTerminatorWasCarriageReturn)
                {
                    previousTerminatorWasCarriageReturn = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character is '\r' or '\n')
                {
                    if (TryResolvePassfileLine(lineBuffer.AsSpan(0, lineLength), settings, out password))
                    {
                        return true;
                    }

                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(lineBuffer.AsSpan(0, lineLength)));
                    lineLength = 0;
                    previousTerminatorWasCarriageReturn = character == '\r';
                    continue;
                }

                if (lineLength == lineBuffer.Length)
                {
                    GrowAndClearPassfileLineBuffer(ref lineBuffer, lineLength);
                }

                lineBuffer[lineLength++] = character;
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(charBuffer[..charsUsed]));
            if (completed)
            {
                break;
            }
        }
        while (!bytes.IsEmpty || flush);

        password = null;
        return false;
    }

    private static bool TryResolvePassfileLine(
        Span<char> line,
        NpgsqlConnectionStringBuilder settings,
        out string? password)
    {
        line = line.Trim();
        password = null;
        if (line.IsEmpty || line[0] == '#')
        {
            return false;
        }

        Span<int> fieldStarts = stackalloc int[5];
        Span<int> fieldLengths = stackalloc int[5];
        var fieldCount = 0;
        var fieldStart = 0;
        var writePosition = 0;
        for (var readPosition = 0; readPosition < line.Length; readPosition++)
        {
            var character = line[readPosition];
            if (character == '\\' && readPosition < line.Length - 1)
            {
                var escaped = line[++readPosition];
                if (escaped is not (':' or '\\'))
                {
                    line[writePosition++] = '\\';
                }

                line[writePosition++] = escaped;
            }
            else if (character == ':')
            {
                AddPassfileField(fieldStarts, fieldLengths, ref fieldCount, fieldStart, writePosition);
                fieldStart = writePosition;
            }
            else
            {
                line[writePosition++] = character;
            }
        }

        AddPassfileField(fieldStarts, fieldLengths, ref fieldCount, fieldStart, writePosition);
        if (fieldCount != 5)
        {
            throw new FormatException();
        }

        var port = GetPassfileField(line, fieldStarts, fieldLengths, 1);
        var parsedPort = 0;
        if (!IsWildcard(port) && !int.TryParse(port, out parsedPort))
        {
            throw new FormatException();
        }

        if (!PassfileFieldMatches(GetPassfileField(line, fieldStarts, fieldLengths, 0), settings.Host)
            || (!IsWildcard(port) && parsedPort != settings.Port)
            || !PassfileFieldMatches(GetPassfileField(line, fieldStarts, fieldLengths, 2), settings.Database)
            || !PassfileFieldMatches(GetPassfileField(line, fieldStarts, fieldLengths, 3), settings.Username))
        {
            return false;
        }

        var passwordField = GetPassfileField(line, fieldStarts, fieldLengths, 4);

        // Npgsql maps '*' in every field, including password, to null. The
        // immutable string required by Npgsql cannot be wiped after auth; all
        // other buffers in this parser are cleared before returning.
        password = IsWildcard(passwordField) ? null : passwordField.ToString();
        return true;
    }

    private static void AddPassfileField(
        Span<int> starts,
        Span<int> lengths,
        ref int count,
        int start,
        int end)
    {
        if (count >= starts.Length)
        {
            throw new FormatException();
        }

        starts[count] = start;
        lengths[count] = end - start;
        count++;
    }

    private static ReadOnlySpan<char> GetPassfileField(
        Span<char> line,
        ReadOnlySpan<int> starts,
        ReadOnlySpan<int> lengths,
        int index)
        => line.Slice(starts[index], lengths[index]);

    private static bool PassfileFieldMatches(ReadOnlySpan<char> field, string? value)
        => value is null || IsWildcard(field) || field.Equals(value, StringComparison.Ordinal);

    private static bool IsWildcard(ReadOnlySpan<char> value)
        => value.Length == 1 && value[0] == '*';

    private static void GrowAndClearPassfileLineBuffer(ref char[] buffer, int usedLength)
    {
        if (buffer.Length >= MaximumPassfileBytes)
        {
            throw new IOException();
        }

        var replacement = ArrayPool<char>.Shared.Rent(
            (int)Math.Min((long)buffer.Length * 2, MaximumPassfileBytes));
        buffer.AsSpan(0, usedLength).CopyTo(replacement);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        buffer = replacement;
    }

    private static (Encoding Encoding, int BomLength) DetectPassfileEncoding(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 4
            && prefix[0] == 0x00
            && prefix[1] == 0x00
            && prefix[2] == 0xFE
            && prefix[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (prefix.Length >= 4
            && prefix[0] == 0xFF
            && prefix[1] == 0xFE
            && prefix[2] == 0x00
            && prefix[3] == 0x00)
        {
            return (Encoding.UTF32, 4);
        }

        if (prefix.Length >= 3
            && prefix[0] == 0xEF
            && prefix[1] == 0xBB
            && prefix[2] == 0xBF)
        {
            return (Encoding.UTF8, 3);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        return (Encoding.UTF8, 0);
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

    private static void ValidateConfiguredFile(string path, long maximumBytes, bool requireOwnerOnlyOnUnix)
    {
        try
        {
            using var handle = OpenConfiguredFile(path, maximumBytes, requireOwnerOnlyOnUnix);
        }
        catch (Exception exception) when (IsSecretFileFailure(exception))
        {
            throw ConfigurationError(
                "Passfile must name a readable, well-formed PostgreSQL password file and, on Unix, use owner-only permissions");
        }
    }

    private static SafeFileHandle OpenConfiguredFile(string path, long maximumBytes, bool requireOwnerOnlyOnUnix)
    {
        // Keep credential bytes out of FileStream's private read-ahead buffer.
        // Callers use RandomAccess so every managed buffer is visible and can
        // be cleared deterministically.
        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);

        try
        {
            if (RandomAccess.GetLength(handle) > maximumBytes)
            {
                throw new IOException();
            }

            // Inspect the object represented by the open handle rather than
            // the pathname. A symlink or pathname swap is therefore judged by
            // the permissions of the same object whose bytes are consumed.
            if (requireOwnerOnlyOnUnix && !OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(handle);
                if ((mode & GroupOrOtherPermissions) != 0)
                {
                    throw new UnauthorizedAccessException();
                }
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static byte[] ReadConfiguredFile(string path, long maximumBytes, bool requireOwnerOnlyOnUnix)
    {
        using var handle = OpenConfiguredFile(path, maximumBytes, requireOwnerOnlyOnUnix);

        var length = RandomAccess.GetLength(handle);
        if (length > maximumBytes)
        {
            throw new IOException();
        }

        var bytes = new byte[checked((int)length)];
        var complete = false;
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += count;
            }

            Span<byte> extraByte = stackalloc byte[1];
            try
            {
                if (RandomAccess.Read(handle, extraByte, offset) != 0)
                {
                    throw new IOException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(extraByte);
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
