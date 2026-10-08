using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed record PristineJellyfinPublishOptions
{
    public const string PublishDirectoryEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_HOST_PUBLISH";
    public const string ArchiveEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_HOST_ARCHIVE";
    public const string Sha256EnvironmentVariable = "JELLYFIN_NPGSQL_TEST_HOST_SHA256";

    public string? PublishDirectory { get; init; }

    public string? ArchivePath { get; init; }

    public required string Sha256 { get; init; }

    public static PristineJellyfinPublishOptions FromEnvironment()
    {
        var hash = Environment.GetEnvironmentVariable(Sha256EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new InvalidOperationException(
                $"Set {Sha256EnvironmentVariable} to the exact SHA-256 for the opt-in Jellyfin publish directory or archive.");
        }

        return new PristineJellyfinPublishOptions
        {
            PublishDirectory = Environment.GetEnvironmentVariable(PublishDirectoryEnvironmentVariable),
            ArchivePath = Environment.GetEnvironmentVariable(ArchiveEnvironmentVariable),
            Sha256 = hash
        };
    }
}

/// <summary>
/// An isolated copy of an exact, integrity-checked Jellyfin publish.
/// </summary>
public sealed class PristineJellyfinPublish
{
    private PristineJellyfinPublish(string directoryPath, string integrityHash)
    {
        DirectoryPath = directoryPath;
        IntegrityHash = integrityHash;
    }

    public string DirectoryPath { get; }

    public string IntegrityHash { get; }

    public string EntryAssemblyPath => Path.Combine(DirectoryPath, "jellyfin.dll");

    public static async Task<PristineJellyfinPublish> CreateAsync(
        PristineJellyfinPublishOptions options,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateExpectedHash(options.Sha256);
        var hasDirectory = !string.IsNullOrWhiteSpace(options.PublishDirectory);
        var hasArchive = !string.IsNullOrWhiteSpace(options.ArchivePath);
        if (hasDirectory == hasArchive)
        {
            throw new InvalidOperationException(
                $"Set exactly one of {PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable} and "
                + $"{PristineJellyfinPublishOptions.ArchiveEnvironmentVariable}.");
        }

        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException("The pristine Jellyfin publish destination must not already exist.");
        }

        Directory.CreateDirectory(destination);
        try
        {
            if (hasArchive)
            {
                var archivePath = Path.GetFullPath(options.ArchivePath!);
                var archiveHash = await HashFileAsync(archivePath, cancellationToken).ConfigureAwait(false);
                EnsureHash(options.Sha256, archiveHash, "Jellyfin host archive");
                ExtractZipSafely(archivePath, destination);
            }
            else
            {
                var source = Path.GetFullPath(options.PublishDirectory!);
                if (!File.Exists(Path.Combine(source, "jellyfin.dll")))
                {
                    throw new InvalidOperationException("The configured Jellyfin publish directory does not contain jellyfin.dll.");
                }

                var sourceHash = await HashDirectoryAsync(source, cancellationToken).ConfigureAwait(false);
                EnsureHash(options.Sha256, sourceHash, "Jellyfin host publish");
                CopyDirectory(source, destination);
            }

            if (!File.Exists(Path.Combine(destination, "jellyfin.dll")))
            {
                var children = Directory.GetDirectories(destination);
                if (children.Length == 1 && File.Exists(Path.Combine(children[0], "jellyfin.dll")))
                {
                    var nested = children[0];
                    foreach (var path in Directory.EnumerateFileSystemEntries(nested))
                    {
                        var target = Path.Combine(destination, Path.GetFileName(path));
                        if (Directory.Exists(path))
                        {
                            Directory.Move(path, target);
                        }
                        else
                        {
                            File.Move(path, target);
                        }
                    }

                    Directory.Delete(nested);
                }
            }

            if (!File.Exists(Path.Combine(destination, "jellyfin.dll")))
            {
                throw new InvalidOperationException("The integrity-checked Jellyfin input does not contain a runnable jellyfin.dll publish.");
            }

            var integrityHash = await HashDirectoryAsync(destination, cancellationToken).ConfigureAwait(false);
            return new PristineJellyfinPublish(destination, integrityHash);
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public async Task VerifyUnchangedAsync(CancellationToken cancellationToken)
    {
        var currentHash = await HashDirectoryAsync(DirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(IntegrityHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The pristine Jellyfin publish changed while the black-box test was running.");
        }
    }

    public static async Task<string> HashDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The Jellyfin publish directory was not found.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(new byte[] { 0 });
            await using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
            }

            hash.AppendData(new byte[] { 0 });
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void ExtractZipSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var outputPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!outputPath.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The ZIP contains an unsafe path and was not extracted.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: false);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void ValidateExpectedHash(string expected)
    {
        if (expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                $"{PristineJellyfinPublishOptions.Sha256EnvironmentVariable} must contain exactly 64 hexadecimal characters.");
        }
    }

    private static void EnsureHash(string expected, string actual, string description)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} SHA-256 did not match the explicitly configured value.");
        }
    }
}
