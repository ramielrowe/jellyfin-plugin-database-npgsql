using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>
/// Validates and copies an externally supplied Jellyfin publish without following links.
/// </summary>
public static class SafeHostPublishStager
{
    private const int MaximumEntries = 100_000;
    private const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task StageAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken,
        Action? inputVerifiedForTest = null)
    {
        ValidateExpectedHash(expectedSha256);
        var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        EnsurePathsDoNotOverlap(sourcePath, destinationPath);
        EnsureDestinationDoesNotExist(destinationPath);

        var sourceManifest = await ReadManifestAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        EnsureHash(expectedSha256, sourceManifest.Hash, "Jellyfin host publish");
        if (!sourceManifest.Files.Any(file => string.Equals(file.RelativePath, "jellyfin.dll", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The configured Jellyfin publish directory does not contain a regular jellyfin.dll file.");
        }

        inputVerifiedForTest?.Invoke();
        Directory.CreateDirectory(destinationPath);
        SetPrivateDirectoryMode(destinationPath);
        try
        {
            foreach (var directory in sourceManifest.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(destinationPath, directory);
                Directory.CreateDirectory(target);
                SetPrivateDirectoryMode(target);
            }

            foreach (var file in sourceManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = Path.Combine(sourcePath, file.RelativePath);
                ValidateRegularFile(sourceFile, file.Length);
                var targetFile = Path.Combine(destinationPath, file.RelativePath);
                await using var input = OpenRegularFileWithoutFollowingFinalLink(sourceFile);
                ValidateRegularFile(sourceFile, file.Length);
                await using var output = new FileStream(
                    targetFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != file.Length)
                {
                    throw new InvalidOperationException($"Jellyfin host publish file changed while it was copied: {file.RelativePath}");
                }

                SetPrivateFileMode(targetFile);
            }

            var stagedManifest = await ReadManifestAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!sourceManifest.Directories.SequenceEqual(stagedManifest.Directories, StringComparer.Ordinal)
                || !sourceManifest.Files.Select(file => (file.RelativePath, file.Length))
                    .SequenceEqual(stagedManifest.Files.Select(file => (file.RelativePath, file.Length))))
            {
                throw new InvalidOperationException("The staged Jellyfin host publish shape differs from its validated input.");
            }

            EnsureHash(expectedSha256, stagedManifest.Hash, "Staged Jellyfin host publish");
        }
        catch
        {
            NormalizeTreePermissions(destinationPath);
            Directory.Delete(destinationPath, recursive: true);
            throw;
        }
    }

    public static async Task<string> HashDirectoryAsync(string directory, CancellationToken cancellationToken)
        => (await ReadManifestAsync(Path.GetFullPath(directory), cancellationToken).ConfigureAwait(false)).Hash;

    private static async Task<DirectoryManifest> ReadManifestAsync(string root, CancellationToken cancellationToken)
    {
        ValidateDirectory(root);
        var directories = new List<string>();
        var files = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            ValidateDirectory(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(root, path);
                if (relativePath == "." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The Jellyfin host publish contains a path outside its input root.");
                }

                switch (GetNodeKind(path))
                {
                    case NodeKind.Directory:
                        directories.Add(relativePath);
                        pending.Push(path);
                        break;
                    case NodeKind.RegularFile:
                        var length = new FileInfo(path).Length;
                        totalBytes = checked(totalBytes + length);
                        files.Add(new FileEntry(relativePath, length));
                        break;
                    case NodeKind.SymbolicLink:
                        throw new InvalidOperationException($"The Jellyfin host publish contains a symbolic link: {relativePath}");
                    default:
                        throw new InvalidOperationException($"The Jellyfin host publish contains a special filesystem node: {relativePath}");
                }

                if (directories.Count + files.Count > MaximumEntries || totalBytes > MaximumBytes)
                {
                    throw new InvalidOperationException(
                        $"The Jellyfin host publish exceeds the staging limit of {MaximumEntries} entries or {MaximumBytes} bytes.");
                }
            }
        }

        directories.Sort(StringComparer.Ordinal);
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath.Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData([0]);
            var sourceFile = Path.Combine(root, file.RelativePath);
            ValidateRegularFile(sourceFile, file.Length);
            await using var stream = OpenRegularFileWithoutFollowingFinalLink(sourceFile);
            ValidateRegularFile(sourceFile, file.Length);
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
            }

            hash.AppendData([0]);
        }

        return new DirectoryManifest(directories, files, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static FileStream OpenRegularFileWithoutFollowingFinalLink(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        const int ReadOnly = 0;
        const int CloseOnExec = 0x80000;
        const int NoFollow = 0x20000;
        var descriptor = NativeMethods.Open(path, ReadOnly | CloseOnExec | NoFollow);
        if (descriptor < 0)
        {
            throw new IOException($"Could not safely open Jellyfin host publish file: {Path.GetFileName(path)}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        return new FileStream(new SafeFileHandle((nint)descriptor, ownsHandle: true), FileAccess.Read, 64 * 1024, isAsync: false);
    }

    private static void ValidateDirectory(string path)
    {
        var kind = GetNodeKind(path);
        if (kind == NodeKind.SymbolicLink)
        {
            throw new InvalidOperationException("The Jellyfin host publish root and every nested directory must be real directories, not symbolic links.");
        }

        if (kind != NodeKind.Directory)
        {
            throw new InvalidOperationException("The Jellyfin host publish root is not a real directory.");
        }
    }

    private static void ValidateRegularFile(string path, long expectedLength)
    {
        if (GetNodeKind(path) != NodeKind.RegularFile || new FileInfo(path).Length != expectedLength)
        {
            throw new InvalidOperationException($"Jellyfin host publish file changed shape while it was staged: {Path.GetFileName(path)}");
        }
    }

    private static NodeKind GetNodeKind(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return NodeKind.SymbolicLink;
        }

        if (OperatingSystem.IsLinux())
        {
            const int AtFileDescriptorWorkingDirectory = -100;
            const int AtSymbolicLinkNoFollow = 0x100;
            const uint StatxType = 0x0001;
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (NativeMethods.Statx(AtFileDescriptorWorkingDirectory, path, AtSymbolicLinkNoFollow, StatxType, buffer) != 0)
                {
                    throw new IOException($"Could not inspect Jellyfin host publish path: {Path.GetFileName(path)}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                const int ModeOffset = 28;
                const int FileTypeMask = 0xF000;
                const int DirectoryType = 0x4000;
                const int RegularFileType = 0x8000;
                const int SymbolicLinkType = 0xA000;
                return (Marshal.ReadInt16(buffer, ModeOffset) & FileTypeMask) switch
                {
                    DirectoryType => NodeKind.Directory,
                    RegularFileType => NodeKind.RegularFile,
                    SymbolicLinkType => NodeKind.SymbolicLink,
                    _ => NodeKind.Special
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        if ((info.Attributes & FileAttributes.Directory) != 0)
        {
            return NodeKind.Directory;
        }

        return info.Exists && (info.Attributes & FileAttributes.Device) == 0 ? NodeKind.RegularFile : NodeKind.Special;
    }

    private static void EnsurePathsDoNotOverlap(string source, string destination)
    {
        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source))
        {
            throw new InvalidOperationException("The source and staged Jellyfin host publish destination must not overlap.");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureDestinationDoesNotExist(string destination)
    {
        var info = new FileInfo(destination);
        info.Refresh();
        if (info.Exists || info.LinkTarget is not null || Directory.Exists(destination))
        {
            throw new InvalidOperationException("The staged Jellyfin host publish destination must not already exist.");
        }
    }

    private static void ValidateExpectedHash(string expected)
    {
        if (expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The expected Jellyfin host publish SHA-256 must contain exactly 64 hexadecimal characters.");
        }
    }

    private static void EnsureHash(string expected, string actual, string description)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} SHA-256 did not match the explicitly configured value.");
        }
    }

    private static void NormalizeTreePermissions(string root)
    {
        if (!Directory.Exists(root) || OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            SetPrivateDirectoryMode(directory);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            SetPrivateFileMode(file);
        }

        SetPrivateDirectoryMode(root);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    private sealed record DirectoryManifest(IReadOnlyList<string> Directories, IReadOnlyList<FileEntry> Files, string Hash);

    private sealed record FileEntry(string RelativePath, long Length);

    private enum NodeKind
    {
        Directory,
        RegularFile,
        SymbolicLink,
        Special
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(
            int directoryFileDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            nint buffer);
    }
}
