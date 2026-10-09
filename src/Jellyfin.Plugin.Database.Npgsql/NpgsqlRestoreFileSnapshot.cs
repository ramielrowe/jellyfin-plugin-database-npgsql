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
#pragma warning disable CA1416 // Public entry point rejects non-Linux platforms before Unix handle operations.
#pragma warning disable CA1849 // Flush(true) is the required fsync boundary after asynchronous writes.
#pragma warning disable CA2007 // Await-using resource acquisition does not expose ConfigureAwait syntax.
#pragma warning disable SA1201, SA1202, SA1204 // Safety lifecycle helpers are grouped by operation.
internal sealed class NpgsqlRestoreFileSnapshot : IAsyncDisposable
{
    private const string RecoveryDirectoryName = ".jellyfin-npgsql-restore-recovery";

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int AtFileWorkingDirectory = -100;

    private const int AtSymlinkNoFollow = 0x100;

    private const int AtEmptyPath = 0x1000;

    private const int OpenDirectoryFlag = 0x10000;

    private const int OpenNoFollow = 0x20000;

    private const int OpenCloseOnExec = 0x80000;

    private const int OpenReadOnly = 0;

    private const int OpenWriteOnly = 1;

    private const int OpenCreate = 0x40;

    private const int OpenExclusive = 0x80;

    private const int RemoveDirectory = 0x200;

    private const uint StatxLinkCount = 0x00000004;

    private const uint StatxInode = 0x00000100;

    private readonly string scratchRoot;
    private readonly string stockFilesystemArchivePath;
    private readonly List<CapturedFile> files;
    private readonly List<CapturedDirectory> directories;
    private readonly List<string> absentDirectories;
    private readonly Dictionary<string, RootHandle> roots;
    private readonly string journalPath;
    private bool committed;
    private bool cleanupDeferred;
    private bool rolledBack;
    private bool disposed;

    private NpgsqlRestoreFileSnapshot(
        string scratchRoot,
        string stockFilesystemArchivePath,
        List<CapturedFile> files,
        List<CapturedDirectory> directories,
        List<string> absentDirectories,
        Dictionary<string, RootHandle> roots,
        Guid? restoreId = null)
    {
        this.scratchRoot = scratchRoot;
        this.stockFilesystemArchivePath = stockFilesystemArchivePath;
        this.files = files;
        this.directories = directories;
        this.absentDirectories = absentDirectories;
        this.roots = roots;
        this.journalPath = Path.Combine(scratchRoot, "recovery-journal.json");
        this.RestoreId = restoreId ?? Guid.NewGuid();
    }

    /// <summary>Gets the private archive that lets stock complete without touching files or the database.</summary>
    internal string StockFilesystemArchivePath => this.stockFilesystemArchivePath;

    /// <summary>Gets the durable identity shared with the PostgreSQL commit marker.</summary>
    internal Guid RestoreId { get; }

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

            if (this.committed && !this.cleanupDeferred)
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
            else if (this.rolledBack)
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

        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException(
                "Database-inclusive PostgreSQL restore requires Linux handle-relative no-follow filesystem primitives.");
        }

        var scratchParent = Path.Combine(paths.DataPath, RecoveryDirectoryName);
        var scratchRoot = NpgsqlScratchSpace.CreateOwnedDirectory(scratchParent, "restore");

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
                if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(paths.DataPath), StringComparison.Ordinal)
                    && (string.Equals(relative, RecoveryDirectoryName, StringComparison.Ordinal)
                        || relative.StartsWith(RecoveryDirectoryName + '/', StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        $"Stock restore entry '{entry.FullName}' targets the provider-owned recovery directory.");
                }

                if (!OperatingSystem.IsLinux())
                {
                    throw new NotSupportedException(
                        "Safe stock filesystem restore is disabled because this platform does not expose the required handle-relative replacement primitives.");
                }

                var rootPath = Path.GetFullPath(root);
                if (!roots.TryGetValue(rootPath, out var rootHandle))
                {
                    rootHandle = OpenRoot(rootPath);
                    roots.Add(rootPath, rootHandle);
                }

                var target = GetSafeTarget(root, relative);
                if (!targets.Add(target))
                {
                    throw new InvalidDataException(
                        $"Multiple archive entries resolve to stock restore target '{target}'.");
                }

                var inspection = InspectTarget(
                    rootHandle,
                    relative.Replace('/', Path.DirectorySeparatorChar),
                    directories,
                    absentDirectories);
                string? copyPath = null;
                UnixFileMode? mode = null;
                FileAttributes attributes = default;
                DateTime lastWriteTimeUtc = default;
                DateTime lastAccessTimeUtc = default;
                if (inspection is not null)
                {
                    await using (inspection.ConfigureAwait(false))
                    {
                        NpgsqlBackupFileSystem.Checkpoint("restore-capture-after-open-target");
                        try
                        {
                            capturedBytes = checked(capturedBytes + inspection.Length);
                        }
                        catch (OverflowException exception)
                        {
                            throw new InvalidDataException("The restore filesystem snapshot size overflows.", exception);
                        }

                        if (inspection.Length > NpgsqlBackupArchive.MaximumEntryBytes
                            || capturedBytes > NpgsqlBackupArchive.MaximumExpandedBytes)
                        {
                            throw new InvalidDataException("The restore filesystem snapshot exceeds its supported size limit.");
                        }

                        copyPath = Path.Combine(scratchRoot, files.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                        await CopyBoundedAsync(
                            inspection.Stream,
                            copyPath,
                            NpgsqlBackupArchive.MaximumEntryBytes,
                            cancellationToken).ConfigureAwait(false);
                        mode = inspection.Mode;
                        attributes = inspection.Attributes;
                        lastWriteTimeUtc = inspection.LastWriteTimeUtc;
                        lastAccessTimeUtc = inspection.LastAccessTimeUtc;
                    }
                }

                var stagedPath = Path.Combine(scratchRoot, "staged-" + files.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                await CopyEntryBoundedAsync(entry, stagedPath, cancellationToken).ConfigureAwait(false);
                files.Add(new CapturedFile(
                    root,
                    target,
                    copyPath,
                    stagedPath,
                    mode,
                    attributes,
                    lastWriteTimeUtc,
                    lastAccessTimeUtc));
            }

            var stockFilesystemArchivePath = Path.Combine(scratchRoot, "stock-filesystem.zip");
            await CreateStockFilesystemArchiveAsync(archive, stockFilesystemArchivePath, cancellationToken).ConfigureAwait(false);
            var snapshot = new NpgsqlRestoreFileSnapshot(
                scratchRoot,
                stockFilesystemArchivePath,
                files,
                directories.Values.OrderByDescending(item => item.Path.Length).ToList(),
                absentDirectories.OrderByDescending(path => path.Length).ToList(),
                roots);
            snapshot.WriteJournal(RestoreJournalPhase.Captured);
            return snapshot;
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

    /// <summary>
    /// Reconciles every durable interrupted restore before Jellyfin can use the database.
    /// A PostgreSQL migration-history marker written in the restore transaction resolves an
    /// otherwise indeterminate commit to the exact old or new filesystem state.
    /// </summary>
    /// <param name="applicationDataPath">The configured Jellyfin data path containing owner-marked journals.</param>
    /// <param name="connectionString">The validated private PostgreSQL connection string.</param>
    /// <param name="cancellationToken">The bounded startup recovery token.</param>
    /// <returns>A task representing complete startup reconciliation.</returns>
    internal static async Task RecoverPendingAsync(
        string applicationDataPath,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var recoveryRoot = Path.Combine(applicationDataPath, RecoveryDirectoryName);
        if (!Directory.Exists(recoveryRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(recoveryRoot, "restore-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journalPath = Path.Combine(directory, "recovery-journal.json");
            RestoreJournal journal;
            try
            {
                await using var stream = new FileStream(
                    journalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    131072,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                journal = await JsonSerializer.DeserializeAsync<RestoreJournal>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The PostgreSQL restore recovery journal is empty.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException(
                    $"PostgreSQL restore recovery is fail-closed because journal '{journalPath}' is missing or corrupt.",
                    exception);
            }

            ValidateJournal(directory, journal);
            if (journal.Phase is RestoreJournalPhase.RolledBack)
            {
                TryDeleteCommittedScratch(directory);
                continue;
            }

            var roots = new Dictionary<string, RootHandle>(StringComparer.Ordinal);
            try
            {
                foreach (var (path, expectedIdentity) in journal.Roots)
                {
                    var root = OpenRoot(path);
                    if (root.Identity != expectedIdentity)
                    {
                        root.Dispose();
                        throw new InvalidDataException(
                            $"PostgreSQL restore recovery root '{path}' no longer has its journaled identity.");
                    }

                    roots.Add(path, root);
                }

                var snapshot = new NpgsqlRestoreFileSnapshot(
                    directory,
                    journal.StockFilesystemArchivePath,
                    journal.Files,
                    journal.Directories,
                    journal.AbsentDirectories,
                    roots,
                    journal.RestoreId);
                roots = null!;
                await using (snapshot.ConfigureAwait(false))
                {
                    var databaseCommitted = journal.Phase is RestoreJournalPhase.Committed
                        || await NpgsqlRestoreCoordinator.MarkerCommittedAsync(
                            connectionString,
                            journal.RestoreId,
                            cancellationToken).ConfigureAwait(false);
                    if (databaseCommitted)
                    {
                        // Persist the database decision before replaying any file. This is
                        // especially important when a prior run already deleted the marker:
                        // a second crash must still choose the new filesystem state.
                        snapshot.WriteJournal(RestoreJournalPhase.Committed);
                        await snapshot.ApplyAsync(databaseCommitted: true, cancellationToken).ConfigureAwait(false);
                        snapshot.Commit();
                        try
                        {
                            await NpgsqlRestoreCoordinator.DeleteMarkerAsync(
                                connectionString,
                                journal.RestoreId,
                                cancellationToken).ConfigureAwait(false);
                        }
#pragma warning disable CA1031 // Marker cleanup is retryable after the committed journal boundary.
                        catch
#pragma warning restore CA1031
                        {
                            snapshot.DeferCleanup();

                            // The committed journal is authoritative on the next startup.
                        }
                    }
                    else
                    {
                        await snapshot.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (roots is not null)
                {
                    foreach (var root in roots.Values)
                    {
                        root.Dispose();
                    }
                }
            }
        }
    }

    private static void ValidateJournal(string directory, RestoreJournal journal)
    {
        if (!string.Equals(journal.Format, "jellyfin-npgsql-restore-recovery-v1", StringComparison.Ordinal)
            || journal.RestoreId == Guid.Empty
            || journal.Roots.Count == 0)
        {
            throw new InvalidDataException("The PostgreSQL restore recovery journal has an unsupported identity or format.");
        }

        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        foreach (var path in journal.Files
            .SelectMany(file => new[] { file.SnapshotPath, file.StagedPath })
            .Append(journal.StockFilesystemArchivePath)
            .Where(path => path is not null))
        {
            if (!Path.GetFullPath(path!).StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The PostgreSQL restore recovery journal contains a scratch path outside its owned directory.");
            }
        }

        foreach (var file in journal.Files)
        {
            if (!journal.Roots.Keys.Any(root => IsWithinRoot(root, Path.GetFullPath(file.TargetPath))))
            {
                throw new InvalidDataException("The PostgreSQL restore recovery journal contains a target outside its captured roots.");
            }
        }
    }

    private static void TryDeleteCommittedScratch(string path)
    {
        try
        {
            DeleteScratch(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deferred owner-marker scavenging retries without changing restore success.
        }
    }

    /// <summary>Marks a successful restore so disposal only removes the private snapshot.</summary>
    internal void Commit()
    {
        this.WriteJournal(RestoreJournalPhase.Committed);
        this.committed = true;
    }

    /// <summary>Durably records the boundary immediately before database commit.</summary>
    internal void MarkCommitIntent() => this.WriteJournal(RestoreJournalPhase.CommitIntent);

    /// <summary>Keeps a committed journal for startup marker cleanup.</summary>
    internal void DeferCleanup() => this.cleanupDeferred = true;

    /// <summary>
    /// Applies pre-extracted files with atomic per-file replacement only after stock database
    /// preparation returned. Stock never reopens production filesystem paths.
    /// </summary>
    /// <param name="cancellationToken">The bounded operation token.</param>
    /// <returns>A task representing staged application.</returns>
    internal Task ApplyAsync(CancellationToken cancellationToken)
        => this.ApplyAsync(databaseCommitted: false, cancellationToken);

    private async Task ApplyAsync(bool databaseCommitted, CancellationToken cancellationToken)
    {
        if (!databaseCommitted)
        {
            this.WriteJournal(RestoreJournalPhase.ApplyingFiles);
        }

        foreach (var root in this.roots.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root.VerifyNamespace();
        }

        foreach (var file in this.files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = this.roots[Path.GetFullPath(file.RootPath)];
            root.VerifyNamespace();
            using var parent = OpenParentDirectory(root, file.TargetPath, create: true);
            NpgsqlBackupFileSystem.Checkpoint("restore-apply-after-open-parent");
            var temporaryName = ".jellyfin-npgsql-apply-" + Guid.NewGuid().ToString("N");
            try
            {
                using var temporary = await CopyToNewHandleAsync(
                    file.StagedPath,
                    parent,
                    temporaryName,
                    NpgsqlBackupArchive.MaximumEntryBytes,
                    cancellationToken).ConfigureAwait(false);
                FlushHandle(temporary);
                NpgsqlBackupFileSystem.Checkpoint("restore-apply-before-rename");
                root.VerifyNamespace();
                RenameAt(parent, temporaryName, Path.GetFileName(file.TargetPath));
                FlushHandle(parent);
                NpgsqlBackupFileSystem.Checkpoint("restore-apply-after-rename");
                root.VerifyNamespace();
            }
            finally
            {
                UnlinkIfPresent(parent, temporaryName, removeDirectory: false);
            }
        }

        if (!databaseCommitted)
        {
            this.WriteJournal(RestoreJournalPhase.FilesApplied);
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

        this.WriteJournal(RestoreJournalPhase.RollingBack);

        var namespaceChanged = false;
        foreach (var file in this.files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = this.roots[Path.GetFullPath(file.RootPath)];
            namespaceChanged |= !root.IsVisibleNamespaceCurrent();
            using var parent = OpenParentDirectory(root, file.TargetPath, create: true);
            NpgsqlBackupFileSystem.Checkpoint("restore-rollback-after-open-parent");
            var targetName = Path.GetFileName(file.TargetPath);
            if (file.SnapshotPath is null)
            {
                UnlinkIfPresent(parent, targetName, removeDirectory: false);
                FlushHandle(parent);
                continue;
            }

            var replacementName = ".jellyfin-npgsql-compensate-" + Guid.NewGuid().ToString("N");
            try
            {
                using var replacement = await CopyToNewHandleAsync(
                    file.SnapshotPath,
                    parent,
                    replacementName,
                    NpgsqlBackupArchive.MaximumEntryBytes,
                    cancellationToken).ConfigureAwait(false);
                if (file.Mode is { } mode)
                {
                    File.SetUnixFileMode(replacement, mode);
                }

                File.SetLastWriteTimeUtc(replacement, file.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(replacement, file.LastAccessTimeUtc);
                File.SetAttributes(replacement, file.Attributes);
                FlushHandle(replacement);
                namespaceChanged |= !root.IsVisibleNamespaceCurrent();
                NpgsqlBackupFileSystem.Checkpoint("restore-rollback-before-rename");
                RenameAt(parent, replacementName, targetName);
                FlushHandle(parent);
                NpgsqlBackupFileSystem.Checkpoint("restore-rollback-after-rename");
            }
            finally
            {
                UnlinkIfPresent(parent, replacementName, removeDirectory: false);
            }
        }

        foreach (var directory in this.absentDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = this.FindRoot(directory);
            try
            {
                using var parent = OpenParentDirectory(root, directory, create: false);
                namespaceChanged |= !root.IsVisibleNamespaceCurrent();
                UnlinkIfPresent(parent, Path.GetFileName(directory), removeDirectory: true);
                FlushHandle(parent);
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
            namespaceChanged |= !root.IsVisibleNamespaceCurrent();
            if (directory.Mode is { } mode)
            {
                File.SetUnixFileMode(handle, mode);
            }

            File.SetLastWriteTimeUtc(handle, directory.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(handle, directory.LastAccessTimeUtc);
            File.SetAttributes(handle, directory.Attributes);
            FlushHandle(handle);
        }

        if (namespaceChanged)
        {
            throw new InvalidDataException(
                "A stock restore root was renamed or replaced during compensation. The held original tree was recovered without mutating the replacement namespace; restore remains fail-closed.");
        }

        this.WriteJournal(RestoreJournalPhase.RolledBack);
        this.rolledBack = true;
    }

    private void WriteJournal(RestoreJournalPhase phase)
    {
        var journal = new RestoreJournal(
            "jellyfin-npgsql-restore-recovery-v1",
            this.RestoreId,
            phase,
            this.stockFilesystemArchivePath,
            this.files,
            this.directories,
            this.absentDirectories,
            this.roots.ToDictionary(
                item => item.Key,
                item => item.Value.Identity,
                StringComparer.Ordinal));
        var temporary = this.journalPath + ".new";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = OwnerFileMode,
        };
        using (var stream = new FileStream(temporary, options))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, this.journalPath, overwrite: true);
        NpgsqlBackupFileSystem.FlushDirectory(this.scratchRoot, "flush-restore-recovery-journal");
        NpgsqlBackupFileSystem.Checkpoint("restore-journal-" + phase.ToString());
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

    private static CapturedInspection? InspectTarget(
        RootHandle root,
        string relative,
        Dictionary<string, CapturedDirectory> directories,
        HashSet<string> absentDirectories)
    {
        var components = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(component => component is "." or ".."))
        {
            throw new InvalidDataException("A stock restore target has an unsafe relative path.");
        }

        using var rootDuplicate = Duplicate(root.Handle);
        CaptureDirectoryMetadata(root.Path, string.Empty, rootDuplicate, directories);
        var current = Duplicate(root.Handle);
        try
        {
            var traversed = string.Empty;
            for (var index = 0; index < components.Length - 1; index++)
            {
                var component = components[index];
                traversed = string.IsNullOrEmpty(traversed)
                    ? component
                    : Path.Combine(traversed, component);
                var descriptor = NativeOpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    component,
                    OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 2)
                    {
                        for (var missing = index; missing < components.Length - 1; missing++)
                        {
                            var missingRelative = string.Join(Path.DirectorySeparatorChar, components.Take(missing + 1));
                            absentDirectories.Add(Path.Combine(root.Path, missingRelative));
                        }

                        return null;
                    }

                    throw new InvalidDataException(
                        $"Stock restore target '{Path.Combine(root.Path, relative)}' traverses a symbolic/reparse or non-directory component '{component}' (errno {error}).");
                }

                var next = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                current.Dispose();
                current = next;
                CaptureDirectoryMetadata(root.Path, traversed, current, directories);
            }

            var targetDescriptor = NativeOpenAt(
                current.DangerousGetHandle().ToInt32(),
                components[^1],
                OpenReadOnly | OpenNoFollow | OpenCloseOnExec);
            if (targetDescriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2)
                {
                    return null;
                }

                throw new InvalidDataException(
                    $"Stock restore target '{Path.Combine(root.Path, relative)}' is symbolic/reparse, a directory, or cannot be opened safely (errno {error}).");
            }

            var targetHandle = new SafeFileHandle((nint)targetDescriptor, ownsHandle: true);
            try
            {
                var stat = ReadStat(targetHandle);
                if ((stat.Mode & 0xF000) != 0x8000)
                {
                    throw new InvalidDataException(
                        $"Stock restore file target '{Path.Combine(root.Path, relative)}' is not a regular file.");
                }

                if (stat.LinkCount != 1)
                {
                    throw new InvalidDataException(
                        $"Stock restore target '{Path.Combine(root.Path, relative)}' has {stat.LinkCount} hard links and cannot be overwritten safely.");
                }

                var stream = new FileStream(targetHandle, FileAccess.Read, 131072, isAsync: false);
                targetHandle = null!;
                return new CapturedInspection(
                    stream,
                    stat.Length,
                    File.GetUnixFileMode(stream.SafeFileHandle),
                    File.GetAttributes(stream.SafeFileHandle),
                    File.GetLastWriteTimeUtc(stream.SafeFileHandle),
                    File.GetLastAccessTimeUtc(stream.SafeFileHandle));
            }
            finally
            {
                targetHandle?.Dispose();
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private static void CaptureDirectoryMetadata(
        string root,
        string relative,
        SafeFileHandle handle,
        Dictionary<string, CapturedDirectory> directories)
    {
        var path = string.IsNullOrEmpty(relative) ? root : Path.Combine(root, relative);
        if (directories.ContainsKey(path))
        {
            return;
        }

        var stat = ReadStat(handle);
        if ((stat.Mode & 0xF000) != 0x4000)
        {
            throw new InvalidDataException($"Restore directory '{path}' is not a directory.");
        }

        directories.Add(
            path,
            new CapturedDirectory(
                path,
                File.GetUnixFileMode(handle),
                File.GetAttributes(handle),
                File.GetLastWriteTimeUtc(handle),
                File.GetLastAccessTimeUtc(handle)));
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

    private static async Task CopyBoundedAsync(
        Stream source,
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
            UnixCreateMode = OwnerFileMode,
        };
        await using var output = new FileStream(destination, destinationOptions);
        var buffer = new byte[131072];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            copied = checked(copied + read);
            if (copied > maximumBytes)
            {
                throw new InvalidDataException("A restore filesystem snapshot file exceeds its supported size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<SafeFileHandle> CopyToNewHandleAsync(
        string source,
        SafeFileHandle parent,
        string name,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var descriptor = NativeOpenAt(
            parent.DangerousGetHandle().ToInt32(),
            name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw new IOException($"Could not create private restore replacement '{name}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            await using var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(handle, FileAccess.Write, 131072, isAsync: false))
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

                    copied = checked(copied + read);
                    if (copied > maximumBytes)
                    {
                        throw new InvalidDataException("A restore replacement exceeds its supported size limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                handle = null!;
                return Duplicate(output.SafeFileHandle);
            }
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
    }

    private static void RenameAt(SafeFileHandle parent, string source, string destination)
    {
        var descriptor = parent.DangerousGetHandle().ToInt32();
        if (NativeRenameAt(descriptor, source, descriptor, destination) != 0)
        {
            throw new IOException(
                $"Could not atomically replace restore target '{destination}' (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static void FlushHandle(SafeFileHandle handle)
    {
        if (NativeFsync(handle.DangerousGetHandle().ToInt32()) != 0)
        {
            throw new IOException($"Could not durably flush a restore file or directory (errno {Marshal.GetLastPInvokeError()}).");
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

    private static FileStat ReadStat(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath | AtSymlinkNoFollow,
                0x000007ff,
                buffer) != 0)
            {
                throw new IOException($"Could not inspect an opened restore target (errno {Marshal.GetLastPInvokeError()}).");
            }

            return new FileStat(
                unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                unchecked((ushort)Marshal.ReadInt16(buffer, 28)),
                Marshal.ReadInt64(buffer, 40));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        var descriptor = NativeDup(handle.DangerousGetHandle().ToInt32());
        if (descriptor < 0)
        {
            throw new IOException($"Could not duplicate a restore directory handle (errno {Marshal.GetLastPInvokeError()}).");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
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

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeOpenAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        int mode);

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

    [DllImport("libc", EntryPoint = "renameat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeRenameAt(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeFsync(int fileDescriptor);

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

    private sealed record FileStat(uint LinkCount, ushort Mode, long Length);

    private sealed record RestoreJournal(
        string Format,
        Guid RestoreId,
        RestoreJournalPhase Phase,
        string StockFilesystemArchivePath,
        List<CapturedFile> Files,
        List<CapturedDirectory> Directories,
        List<string> AbsentDirectories,
        Dictionary<string, FileIdentity> Roots);

    private enum RestoreJournalPhase
    {
        Captured,
        ApplyingFiles,
        FilesApplied,
        CommitIntent,
        Committed,
        RollingBack,
        RolledBack,
    }

    private sealed class CapturedInspection(
        FileStream stream,
        long length,
        UnixFileMode mode,
        FileAttributes attributes,
        DateTime lastWriteTimeUtc,
        DateTime lastAccessTimeUtc) : IAsyncDisposable
    {
        internal FileStream Stream { get; } = stream;

        internal long Length { get; } = length;

        internal UnixFileMode Mode { get; } = mode;

        internal FileAttributes Attributes { get; } = attributes;

        internal DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

        internal DateTime LastAccessTimeUtc { get; } = lastAccessTimeUtc;

        public ValueTask DisposeAsync() => this.Stream.DisposeAsync();
    }

#pragma warning disable SA1202 // IDisposable visibility follows the internal handle contract.
    private sealed class RootHandle(string path, SafeFileHandle handle, FileIdentity identity) : IDisposable
    {
        internal string Path { get; } = path;

        internal SafeFileHandle Handle { get; } = handle;

        internal FileIdentity Identity { get; } = identity;

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
            if (ReadIdentity(current) != this.Identity)
            {
                throw new InvalidDataException($"Restore root '{this.Path}' was replaced after validation.");
            }
        }

        internal bool IsVisibleNamespaceCurrent()
        {
            try
            {
                this.VerifyNamespace();
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public void Dispose() => this.Handle.Dispose();
    }
#pragma warning restore SA1202

    private sealed record FileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);
}
#pragma warning restore CA2000
#pragma warning restore CA1416
#pragma warning restore CA1849
#pragma warning restore CA2007
#pragma warning restore SA1201, SA1202, SA1204
