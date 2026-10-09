using System.IO.Compression;
using System.Runtime.InteropServices;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Captures the exact files stock Jellyfin can overwrite and compensates a failed restore.
/// </summary>
internal sealed class NpgsqlRestoreFileSnapshot : IAsyncDisposable
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int AtFileWorkingDirectory = -100;

    private const int AtSymlinkNoFollow = 0x100;

    private const uint StatxLinkCount = 0x00000004;

    private readonly string scratchRoot;
    private readonly List<CapturedFile> files;
    private readonly List<string> absentDirectories;
    private bool committed;
    private bool disposed;

    private NpgsqlRestoreFileSnapshot(
        string scratchRoot,
        List<CapturedFile> files,
        List<string> absentDirectories)
    {
        this.scratchRoot = scratchRoot;
        this.files = files;
        this.absentDirectories = absentDirectories;
    }

    /// <inheritdoc />
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            DeleteScratch(this.scratchRoot);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Captures every stock extraction target before the first mutation.</summary>
    /// <param name="archivePath">The immutable validated restore archive.</param>
    /// <param name="paths">The exact host application paths stock restore will use.</param>
    /// <param name="cancellationToken">The bounded operation token.</param>
    /// <returns>An owned compensating snapshot.</returns>
    internal static async Task<NpgsqlRestoreFileSnapshot> CaptureAsync(
        string archivePath,
        IServerApplicationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var scratchRoot = Path.Combine(
            Path.GetTempPath(),
            ".jellyfin-npgsql-file-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scratchRoot, OwnerDirectoryMode);
        }

        var files = new List<CapturedFile>();
        var absentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long capturedBytes = 0;
            foreach (var entry in archive.Entries.OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var mapping = MapTarget(entry.FullName, paths);
                if (mapping is null)
                {
                    continue;
                }

                var (root, relative) = mapping.Value;
                var target = GetSafeTarget(root, relative);
                if (!targets.Add(target))
                {
                    throw new InvalidDataException(
                        $"Multiple archive entries resolve to stock restore target '{target}'.");
                }

                ValidateExistingPath(root, target);
                RecordAbsentDirectories(root, target, absentDirectories);
                if (!File.Exists(target))
                {
                    files.Add(new CapturedFile(target, null, null, default, default, default));
                    continue;
                }

                RejectHardLink(target);
                var info = new FileInfo(target);
                try
                {
                    capturedBytes = checked(capturedBytes + info.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("The restore filesystem snapshot size overflows.", exception);
                }

                if (info.Length > NpgsqlBackupArchive.MaximumEntryBytes
                    || capturedBytes > NpgsqlBackupArchive.MaximumExpandedBytes)
                {
                    throw new InvalidDataException("The restore filesystem snapshot exceeds its supported size limit.");
                }

                var copyPath = Path.Combine(scratchRoot, files.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                await CopyBoundedAsync(
                    target,
                    copyPath,
                    NpgsqlBackupArchive.MaximumEntryBytes,
                    cancellationToken).ConfigureAwait(false);
                UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(target);
                files.Add(new CapturedFile(
                    target,
                    copyPath,
                    mode,
                    info.Attributes,
                    info.LastWriteTimeUtc,
                    info.LastAccessTimeUtc));
            }

            return new NpgsqlRestoreFileSnapshot(
                scratchRoot,
                files,
                absentDirectories.OrderByDescending(path => path.Length).ToList());
        }
        catch
        {
            DeleteScratch(scratchRoot);
            throw;
        }
    }

    /// <summary>Marks a successful restore so disposal only removes the private snapshot.</summary>
    internal void Commit() => this.committed = true;

    /// <summary>Restores all overwritten files and removes files/directories created by stock extraction.</summary>
    /// <param name="cancellationToken">An internal bounded compensation token.</param>
    /// <returns>A task representing exact compensation.</returns>
    internal async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (this.committed)
        {
            throw new InvalidOperationException("A committed restore filesystem snapshot cannot be rolled back.");
        }

        foreach (var file in this.files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.SnapshotPath is null)
            {
                if (File.Exists(file.TargetPath))
                {
                    File.Delete(file.TargetPath);
                }

                continue;
            }

            var parent = Path.GetDirectoryName(file.TargetPath)
                ?? throw new InvalidOperationException("A restore target has no parent directory.");
            Directory.CreateDirectory(parent);
            var replacement = Path.Combine(
                parent,
                ".jellyfin-npgsql-compensate-" + Guid.NewGuid().ToString("N"));
            try
            {
                await CopyBoundedAsync(
                    file.SnapshotPath,
                    replacement,
                    NpgsqlBackupArchive.MaximumEntryBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows() && file.Mode is { } mode)
                {
                    File.SetUnixFileMode(replacement, mode);
                }

                File.Move(replacement, file.TargetPath, true);
                File.SetLastWriteTimeUtc(file.TargetPath, file.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(file.TargetPath, file.LastAccessTimeUtc);
                File.SetAttributes(file.TargetPath, file.Attributes);
            }
            finally
            {
                if (File.Exists(replacement))
                {
                    File.Delete(replacement);
                }
            }
        }

        foreach (var directory in this.absentDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static (string Root, string Relative)? MapTarget(
        string entryName,
        IServerApplicationPaths paths)
    {
        if (entryName.StartsWith("Config/", StringComparison.Ordinal))
        {
            return (paths.ConfigurationDirectoryPath, entryName["Config/".Length..]);
        }

        if (entryName.StartsWith("Data/metadata-default/", StringComparison.Ordinal))
        {
            return (paths.DefaultInternalMetadataPath, entryName["Data/metadata-default/".Length..]);
        }

        if (entryName.StartsWith("Data/metadata/", StringComparison.Ordinal))
        {
            return (paths.InternalMetadataPath, entryName["Data/metadata/".Length..]);
        }

        if (entryName.StartsWith("Data/", StringComparison.Ordinal))
        {
            return (paths.DataPath, entryName["Data/".Length..]);
        }

        if (entryName.StartsWith("Root/", StringComparison.Ordinal))
        {
            return (paths.RootFolderPath, entryName["Root/".Length..]);
        }

        return null;
    }

    private static string GetSafeTarget(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            throw new InvalidDataException("A stock restore entry has no relative target path.");
        }

        var rootPath = Path.GetFullPath(root);
        var rootPrefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Stock restore target '{target}' escapes '{rootPath}'.");
        }

        return target;
    }

    private static void ValidateExistingPath(string root, string target)
    {
        var rootPath = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(rootPath, target);
        var current = rootPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            {
                throw new InvalidDataException(
                    $"Stock restore target '{target}' traverses symbolic/reparse path '{current}'.");
            }
        }

        if (Directory.Exists(target))
        {
            throw new InvalidDataException($"Stock restore file target '{target}' is an existing directory.");
        }
    }

    private static void RecordAbsentDirectories(
        string root,
        string target,
        HashSet<string> absentDirectories)
    {
        var rootPath = Path.GetFullPath(root);
        var parent = Path.GetDirectoryName(target);
        while (parent is not null
            && !string.Equals(parent, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(parent))
            {
                absentDirectories.Add(parent);
            }

            parent = Path.GetDirectoryName(parent);
        }
    }

    private static async Task CopyBoundedAsync(
        string source,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var destinationOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 131072,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            destinationOptions.UnixCreateMode = OwnerFileMode;
        }

        var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var output = new FileStream(destination, destinationOptions);
        await using (input.ConfigureAwait(false))
        await using (output.ConfigureAwait(false))
        {
            var buffer = new byte[131072];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    copied = checked(copied + read);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("A restore filesystem snapshot overflowed its size accounting.", exception);
                }

                if (copied > maximumBytes)
                {
                    throw new InvalidDataException("A restore filesystem snapshot file exceeds its supported size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RejectHardLink(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Statx(AtFileWorkingDirectory, path, AtSymlinkNoFollow, StatxLinkCount, buffer) != 0)
            {
                throw new IOException($"Could not validate hard-link state for restore target '{path}'.");
            }

            var linkCount = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            if (linkCount != 1)
            {
                throw new InvalidDataException(
                    $"Stock restore target '{path}' has {linkCount} hard links and cannot be overwritten safely.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void DeleteScratch(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

#pragma warning disable CA2101 // LPUTF8Str explicitly matches Linux filesystem path encoding.
#pragma warning disable SYSLIB1054 // This bounded compatibility probe avoids enabling unsafe source-generated interop.
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        nint buffer);
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101

    private sealed record CapturedFile(
        string TargetPath,
        string? SnapshotPath,
        UnixFileMode? Mode,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc,
        DateTime LastAccessTimeUtc);
}
