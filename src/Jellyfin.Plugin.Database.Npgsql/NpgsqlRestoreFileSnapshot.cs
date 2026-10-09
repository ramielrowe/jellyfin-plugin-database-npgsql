using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Captures the exact files stock Jellyfin can overwrite and compensates a failed restore.
/// </summary>
#pragma warning disable CA2000 // Safe handles are transferred to snapshot/root ownership or disposed by using/failure paths.
internal sealed class NpgsqlRestoreFileSnapshot : IAsyncDisposable
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int AtFileWorkingDirectory = -100;

    private const int AtSymlinkNoFollow = 0x100;

    private const int AtEmptyPath = 0x1000;

    private const int OpenDirectoryFlag = 0x10000;

    private const int OpenNoFollow = 0x20000;

    private const int OpenCloseOnExec = 0x80000;

    private const int RemoveDirectory = 0x200;

    private const uint StatxLinkCount = 0x00000004;

    private const uint StatxInode = 0x00000100;

    private readonly string scratchRoot;
    private readonly string stockFilesystemArchivePath;
    private readonly List<CapturedFile> files;
    private readonly List<CapturedDirectory> directories;
    private readonly List<string> absentDirectories;
    private readonly Dictionary<string, RootHandle> roots;
    private bool committed;
    private bool disposed;

    private NpgsqlRestoreFileSnapshot(
        string scratchRoot,
        string stockFilesystemArchivePath,
        List<CapturedFile> files,
        List<CapturedDirectory> directories,
        List<string> absentDirectories,
        Dictionary<string, RootHandle> roots)
    {
        this.scratchRoot = scratchRoot;
        this.stockFilesystemArchivePath = stockFilesystemArchivePath;
        this.files = files;
        this.directories = directories;
        this.absentDirectories = absentDirectories;
        this.roots = roots;
    }

    /// <summary>Gets the private archive that lets stock complete without touching files or the database.</summary>
    internal string StockFilesystemArchivePath => this.stockFilesystemArchivePath;

    /// <inheritdoc />
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            foreach (var root in this.roots.Values)
            {
                root.Dispose();
            }

            if (this.committed)
            {
                try
                {
                    DeleteScratch(this.scratchRoot);
                }
#pragma warning disable CA1031 // Cleanup cannot turn a committed database/filesystem restore into an apparent failure.
                catch
#pragma warning restore CA1031
                {
                    // Marker-owned scratch cleanup is best effort after the
                    // restore's irreversible success boundary.
                }
            }
            else
            {
                DeleteScratch(this.scratchRoot);
            }
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
        var directories = new Dictionary<string, CapturedDirectory>(StringComparer.OrdinalIgnoreCase);
        var absentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, RootHandle>(StringComparer.Ordinal);
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
                if (!OperatingSystem.IsLinux())
                {
                    throw new NotSupportedException(
                        "Safe stock filesystem restore is disabled because this platform does not expose the required handle-relative replacement primitives.");
                }

                var rootPath = Path.GetFullPath(root);
                if (!roots.ContainsKey(rootPath))
                {
                    roots.Add(rootPath, OpenRoot(rootPath));
                }

                var target = GetSafeTarget(root, relative);
                if (!targets.Add(target))
                {
                    throw new InvalidDataException(
                        $"Multiple archive entries resolve to stock restore target '{target}'.");
                }

                ValidateExistingPath(root, target);
                CaptureDirectoryMetadata(root, target, directories);
                RecordAbsentDirectories(root, target, absentDirectories);
                if (!File.Exists(target))
                {
                    var absentStagedPath = Path.Combine(scratchRoot, "staged-" + files.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                    await CopyEntryBoundedAsync(entry, absentStagedPath, cancellationToken).ConfigureAwait(false);
                    files.Add(new CapturedFile(root, target, null, absentStagedPath, null, default, default, default));
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
                var stagedPath = Path.Combine(scratchRoot, "staged-" + files.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                await CopyEntryBoundedAsync(entry, stagedPath, cancellationToken).ConfigureAwait(false);
                files.Add(new CapturedFile(
                    root,
                    target,
                    copyPath,
                    stagedPath,
                    mode,
                    info.Attributes,
                    info.LastWriteTimeUtc,
                    info.LastAccessTimeUtc));
            }

            var stockFilesystemArchivePath = Path.Combine(scratchRoot, "stock-filesystem.zip");
            await CreateStockFilesystemArchiveAsync(archive, stockFilesystemArchivePath, cancellationToken).ConfigureAwait(false);
            return new NpgsqlRestoreFileSnapshot(
                scratchRoot,
                stockFilesystemArchivePath,
                files,
                directories.Values.OrderByDescending(item => item.Path.Length).ToList(),
                absentDirectories.OrderByDescending(path => path.Length).ToList(),
                roots);
        }
        catch
        {
            foreach (var root in roots.Values)
            {
                root.Dispose();
            }

            DeleteScratch(scratchRoot);
            throw;
        }
    }

    /// <summary>Marks a successful restore so disposal only removes the private snapshot.</summary>
    internal void Commit() => this.committed = true;

    /// <summary>
    /// Applies pre-extracted files with atomic per-file replacement only after stock database
    /// preparation returned. Stock never reopens production filesystem paths.
    /// </summary>
    /// <param name="cancellationToken">The bounded operation token.</param>
    /// <returns>A task representing staged application.</returns>
    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        foreach (var root in this.roots.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root.VerifyNamespace();
        }

        foreach (var file in this.files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = this.roots[Path.GetFullPath(file.RootPath)];
            using var parent = OpenParentDirectory(root, file.TargetPath, create: true);
            var temporaryName = ".jellyfin-npgsql-apply-" + Guid.NewGuid().ToString("N");
            var temporaryPath = GetHandlePath(parent, temporaryName);
            try
            {
                await CopyBoundedAsync(
                    file.StagedPath,
                    temporaryPath,
                    NpgsqlBackupArchive.MaximumEntryBytes,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, GetHandlePath(parent, Path.GetFileName(file.TargetPath)), true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

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
            var root = this.roots[Path.GetFullPath(file.RootPath)];
            using var parent = OpenParentDirectory(root, file.TargetPath, create: true);
            var targetName = Path.GetFileName(file.TargetPath);
            if (file.SnapshotPath is null)
            {
                UnlinkIfPresent(parent, targetName, removeDirectory: false);
                continue;
            }

            var replacementName = ".jellyfin-npgsql-compensate-" + Guid.NewGuid().ToString("N");
            var replacement = GetHandlePath(parent, replacementName);
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

                File.SetLastWriteTimeUtc(replacement, file.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(replacement, file.LastAccessTimeUtc);
                File.SetAttributes(replacement, file.Attributes);
                File.Move(replacement, GetHandlePath(parent, targetName), true);
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
            var root = this.FindRoot(directory);
            try
            {
                using var parent = OpenParentDirectory(root, directory, create: false);
                UnlinkIfPresent(parent, Path.GetFileName(directory), removeDirectory: true);
            }
            catch (DirectoryNotFoundException)
            {
                // A deeper absent directory was already removed.
            }
        }

        foreach (var directory in this.directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = this.FindRoot(directory.Path);
            using var handle = OpenDirectory(root, directory.Path);
            if (!OperatingSystem.IsWindows() && directory.Mode is { } mode)
            {
                File.SetUnixFileMode(handle, mode);
            }

            File.SetLastWriteTimeUtc(handle, directory.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(handle, directory.LastAccessTimeUtc);
            File.SetAttributes(handle, directory.Attributes);
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
        ValidateNotLink(rootPath, target);
        var relative = Path.GetRelativePath(rootPath, target);
        var current = rootPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            ValidateNotLink(current, target);
        }

        if (Directory.Exists(target))
        {
            throw new InvalidDataException($"Stock restore file target '{target}' is an existing directory.");
        }
    }

    private static void ValidateNotLink(string current, string target)
    {
        if (!File.Exists(current) && !Directory.Exists(current))
        {
            return;
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

    private static void CaptureDirectoryMetadata(
        string root,
        string target,
        Dictionary<string, CapturedDirectory> directories)
    {
        var rootPath = Path.GetFullPath(root);
        var current = Path.GetDirectoryName(target);
        while (current is not null
            && (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase)
                || current.StartsWith(
                    Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.Exists(current) && !directories.ContainsKey(current))
            {
                var info = new DirectoryInfo(current);
                directories.Add(
                    current,
                    new CapturedDirectory(
                        current,
                        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(current),
                        info.Attributes,
                        info.LastWriteTimeUtc,
                        info.LastAccessTimeUtc));
            }

            if (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
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

    private static async Task CopyEntryBoundedAsync(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        var input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 131072,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerFileMode;
            }

            var output = new FileStream(destination, options);
            await using (output.ConfigureAwait(false))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CreateStockFilesystemArchiveAsync(
        ZipArchive source,
        string destination,
        CancellationToken cancellationToken)
    {
        var output = await ZipFile.OpenAsync(destination, ZipArchiveMode.Create, cancellationToken).ConfigureAwait(false);
        using (output)
        {
            var manifestEntry = source.GetEntry("manifest.json")
                ?? throw new InvalidDataException("The validated restore archive lost its stock manifest.");
            var manifestInput = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            JsonNode manifest;
            await using (manifestInput.ConfigureAwait(false))
            {
                manifest = await JsonNode.ParseAsync(
                    manifestInput,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The validated stock manifest is empty.");
            }

            var options = manifest["Options"] as JsonObject
                ?? throw new InvalidDataException("The validated stock manifest lost its Options object.");
            options["Database"] = false;
            var copy = output.CreateEntry("manifest.json", CompressionLevel.Optimal);
            copy.ExternalAttributes = manifestEntry.ExternalAttributes;
            copy.LastWriteTime = manifestEntry.LastWriteTime;
            var target = await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(
                    target,
                    manifest,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RejectHardLink(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException(
                "Safe stock restore is disabled because this platform cannot prove that targets have no external hard links.");
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

#pragma warning disable SA1204 // Handle-relative helpers are grouped as one safety boundary.
    private static RootHandle OpenRoot(string path)
    {
        var descriptor = NativeOpen(path, OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new InvalidDataException(
                $"Stock restore root '{path}' is missing, linked, or cannot be opened safely (errno {Marshal.GetLastPInvokeError()}).");
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            var root = new RootHandle(path, handle, ReadIdentity(handle));
            root.VerifyNamespace();
            return root;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private RootHandle FindRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = this.roots.Values
            .Where(candidate => IsWithinRoot(candidate.Path, fullPath))
            .OrderByDescending(candidate => candidate.Path.Length)
            .FirstOrDefault();
        return root ?? throw new InvalidOperationException($"No captured restore root owns '{path}'.");
    }

    private static SafeFileHandle OpenParentDirectory(RootHandle root, string targetPath, bool create)
    {
        var relative = GetRelativePath(root.Path, targetPath);
        var parent = Path.GetDirectoryName(relative);
        return OpenDirectoryComponents(root, parent, create);
    }

    private static SafeFileHandle OpenDirectory(RootHandle root, string path)
        => OpenDirectoryComponents(root, GetRelativePath(root.Path, path), create: false);

    private static SafeFileHandle OpenDirectoryComponents(RootHandle root, string? relative, bool create)
    {
        var duplicate = NativeDup(root.Handle.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
        {
            throw new IOException($"Could not duplicate the restore root handle (errno {Marshal.GetLastPInvokeError()}).");
        }

        var current = new SafeFileHandle((nint)duplicate, ownsHandle: true);
        try
        {
            if (string.IsNullOrEmpty(relative) || string.Equals(relative, ".", StringComparison.Ordinal))
            {
                return current;
            }

            foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = NativeOpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    component,
                    OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
                if (next < 0 && create && Marshal.GetLastPInvokeError() == 2)
                {
                    if (NativeMkdirAt(current.DangerousGetHandle().ToInt32(), component, 448) != 0
                        && Marshal.GetLastPInvokeError() != 17)
                    {
                        throw new IOException(
                            $"Could not create restore directory component '{component}' safely (errno {Marshal.GetLastPInvokeError()}).");
                    }

                    next = NativeOpenAt(
                        current.DangerousGetHandle().ToInt32(),
                        component,
                        OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
                }

                if (next < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 2)
                    {
                        throw new DirectoryNotFoundException(
                            $"Restore directory component '{component}' no longer exists.");
                    }

                    throw new InvalidDataException(
                        $"Restore directory component '{component}' is linked or cannot be opened safely (errno {error}).");
                }

                var previous = current;
                current = new SafeFileHandle((nint)next, ownsHandle: true);
                previous.Dispose();
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void UnlinkIfPresent(SafeFileHandle parent, string name, bool removeDirectory)
    {
        if (NativeUnlinkAt(
            parent.DangerousGetHandle().ToInt32(),
            name,
            removeDirectory ? RemoveDirectory : 0) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 2 || (removeDirectory && error == 39))
        {
            return;
        }

        throw new IOException($"Could not remove restore target '{name}' safely (errno {error}).");
    }

    private static string GetRelativePath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsWithinRoot(root, fullPath))
        {
            throw new InvalidDataException($"Restore target '{path}' escapes '{root}'.");
        }

        return Path.GetRelativePath(root, fullPath);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var rootPath = Path.GetFullPath(root);
        return string.Equals(rootPath, path, StringComparison.Ordinal)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string GetHandlePath(SafeFileHandle handle, string? name = null)
    {
        var path = "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return name is null ? path : Path.Combine(path, name);
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath | AtSymlinkNoFollow,
                StatxInode,
                buffer) != 0)
            {
                throw new IOException($"Could not identify a restore root handle (errno {Marshal.GetLastPInvokeError()}).");
            }

            return new FileIdentity(
                unchecked((ulong)Marshal.ReadInt64(buffer, 32)),
                unchecked((uint)Marshal.ReadInt32(buffer, 136)),
                unchecked((uint)Marshal.ReadInt32(buffer, 140)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
#pragma warning restore SA1204

    private static void DeleteScratch(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

#pragma warning disable CA2101 // LPUTF8Str explicitly matches Linux filesystem path encoding.
#pragma warning disable SYSLIB1054 // This bounded compatibility probe avoids enabling unsafe source-generated interop.
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        nint buffer);

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeOpenAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "mkdirat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeMkdirAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int mode);

    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeUnlinkAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeDup(int fileDescriptor);
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101

    private sealed record CapturedFile(
        string RootPath,
        string TargetPath,
        string? SnapshotPath,
        string StagedPath,
        UnixFileMode? Mode,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc,
        DateTime LastAccessTimeUtc);

    private sealed record CapturedDirectory(
        string Path,
        UnixFileMode? Mode,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc,
        DateTime LastAccessTimeUtc);

#pragma warning disable SA1202 // IDisposable visibility follows the internal handle contract.
    private sealed class RootHandle(string path, SafeFileHandle handle, FileIdentity identity) : IDisposable
    {
        internal string Path { get; } = path;

        internal SafeFileHandle Handle { get; } = handle;

        internal void VerifyNamespace()
        {
            var descriptor = NativeOpen(this.Path, OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
            if (descriptor < 0)
            {
                throw new InvalidDataException(
                    $"Restore root '{this.Path}' was replaced by a symbolic/reparse path or removed after validation "
                    + $"(errno {Marshal.GetLastPInvokeError()}).");
            }

            using var current = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            if (ReadIdentity(current) != identity)
            {
                throw new InvalidDataException($"Restore root '{this.Path}' was replaced after validation.");
            }
        }

        public void Dispose() => this.Handle.Dispose();
    }
#pragma warning restore SA1202

    private sealed record FileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);
}
#pragma warning restore CA2000
