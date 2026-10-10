using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Creates, atomically claims, and scavenges owner-private plugin scratch directories.</summary>
#pragma warning disable SA1202, SA1204, SA1600, CA1416, CA2000
internal static class NpgsqlScratchSpace
{
    internal const string RootEnvironmentVariable = "JELLYFIN_NPGSQL_SCRATCH_ROOT";
    private const string OwnerMarker = ".jellyfin-npgsql-owner.json";
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenPath = 0x200000;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int RemoveDirectory = 0x200;
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly object ConfigurationLock = new();
    private static string? configuredRoot;

    internal static void ConfigureRoot(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        lock (ConfigurationLock)
        {
            configuredRoot = Path.Combine(Path.GetFullPath(applicationDataPath), ".jellyfin-npgsql-scratch");
        }
    }

    internal static string GetRoot()
    {
        var environment = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return Path.GetFullPath(environment);
        }

        lock (ConfigurationLock)
        {
            return configuredRoot ?? Path.Combine(Environment.CurrentDirectory, ".artifacts", "npgsql-scratch");
        }
    }

    internal static string GetValidationRoot() => Path.Combine(GetRoot(), "validation");

    internal static string CreateOwnedDirectory(string parent, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        Directory.CreateDirectory(parent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(parent, OwnerDirectoryMode);
        }

        var ownerId = Guid.NewGuid();
        var directory = Path.Combine(parent, purpose + "-" + ownerId.ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, OwnerDirectoryMode);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        using (var stream = new FileStream(Path.Combine(directory, OwnerMarker), options))
        {
            JsonSerializer.Serialize(stream, new ScratchOwner(
                PluginMetadata.Id.ToString("D"),
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                purpose,
                ownerId));
            stream.Flush(flushToDisk: true);
        }

        NpgsqlBackupFileSystem.FlushDirectory(directory, "flush-scratch-owner");
        NpgsqlBackupFileSystem.FlushDirectory(parent, "flush-scratch-parent");
        return directory;
    }

    /// <summary>Renames the exact opened generation to trash before deleting any contents.</summary>
    /// <param name="directory">The exact live or tombstoned owned generation.</param>
    /// <param name="purpose">The required purpose from its durable owner marker.</param>
    internal static void ClaimAndDelete(string directory, string purpose)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            DeleteWindowsOwned(directory, purpose);
            return;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parentPath = Path.GetDirectoryName(full) ?? throw new InvalidDataException("Scratch has no parent.");
        var name = Path.GetFileName(full);
        using var parent = OpenDirectoryHandle(parentPath);
        if (name.StartsWith(".trash-", StringComparison.Ordinal))
        {
            DeleteClaimedTrash(parent, parentPath, name, purpose);
            return;
        }

        using var original = OpenDirectoryAt(parent, name);
        var identity = ReadIdentity(original);
        var ownerProof = ReadOwnerProof(original);
        var owner = ownerProof.Owner;
        ValidateOwner(owner, purpose, name);
        var trashName = ".trash-" + owner.OwnerId.ToString("N") + "-" + Guid.NewGuid().ToString("N");

        NpgsqlBackupFileSystem.Checkpoint("scratch-before-claim-rename");
        if (RenameAt(parent, name, trashName) != 0)
        {
            throw NativeFailure("Could not atomically claim scratch.");
        }

        NpgsqlBackupFileSystem.Checkpoint("scratch-after-claim-rename");
        NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-claim-parent");
        using var claimed = OpenDirectoryAt(parent, trashName);
        if (ReadIdentity(claimed) != identity || ReadOwnerProof(claimed) != ownerProof)
        {
            RestoreUnexpectedClaim(parent, trashName, name, parentPath);
            throw new InvalidDataException("The claimed scratch generation changed identity; it was not deleted.");
        }

        EnsureClaimSidecar(parent, parentPath, trashName, new ScratchClaim(owner, identity));
        DeleteClaimedTrash(parent, parentPath, trashName, purpose, claimed);
    }

    internal static void ScavengeValidationDirectories(string parent) => Scavenge(parent, "validation", "validation-");

    /// <summary>Validates ownership through an already-open no-follow directory handle.</summary>
    /// <param name="directory">The stable directory handle.</param>
    /// <param name="purpose">The required owner purpose.</param>
    /// <param name="name">The live or tombstoned basename.</param>
    internal static void ValidateOwnedHandle(SafeFileHandle directory, string purpose, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var proof = ReadOwnerProof(directory);
        ValidateOwner(proof.Owner, purpose, name);
    }

    internal static void Scavenge(string parent, string purpose, string prefix)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        foreach (var candidate in Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) && !name.StartsWith(".trash-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (name.StartsWith(".trash-", StringComparison.Ordinal))
                {
                    ClaimAndDelete(candidate, purpose);
                    continue;
                }

                var owner = ReadOwnerByPath(candidate);
                if (!IsExpectedOwner(owner, purpose) || IsLiveOwner(owner))
                {
                    continue;
                }

                ClaimAndDelete(candidate, purpose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // It was concurrently claimed or changed identity. Never delete through the new name.
            }
        }

        ScavengeOrphanedClaimSidecars(parent, purpose);
    }

    private static void ScavengeOrphanedClaimSidecars(string parentPath, string purpose)
    {
        using var parent = OpenDirectoryHandle(parentPath);
        foreach (var sidecarPath in Directory.EnumerateFiles(parentPath, ".trash-*.claim", SearchOption.TopDirectoryOnly))
        {
            var sidecarName = Path.GetFileName(sidecarPath);
            var trashName = sidecarName[..^".claim".Length];
            if (Directory.Exists(Path.Combine(parentPath, trashName)))
            {
                continue;
            }

            try
            {
                var claim = ReadClaimSidecar(parent, trashName);
                ValidateOwner(claim.Owner, purpose, trashName);
                using var original = OpenAnyAt(parent, sidecarName);
                var identity = ReadIdentity(original);
                var deletingName = ".delete-claim-" + Guid.NewGuid().ToString("N");
                if (RenameAt(parent, sidecarName, deletingName) != 0)
                {
                    continue;
                }

                using var moved = OpenAnyAt(parent, deletingName);
                if (ReadIdentity(moved) != identity)
                {
                    RestoreUnexpectedClaim(parent, deletingName, sidecarName, parentPath);
                    continue;
                }

                _ = UnlinkAt(parent, deletingName, 0);
                NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-orphaned-claim-parent");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // An unrelated or concurrently replaced sidecar is never removed.
            }
        }
    }

    private static void DeleteContents(SafeFileHandle directory)
    {
        while (true)
        {
            var entries = Directory.EnumerateFileSystemEntries(HandlePath(directory), "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => string.Equals(name, OwnerMarker, StringComparison.Ordinal) ? 1 : 0)
                .ToArray();
            if (entries.Length == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                using var child = OpenAnyAt(directory, entry!);
                var identity = ReadIdentity(child);
                var claimedName = ".delete-" + Guid.NewGuid().ToString("N");
                NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-rename");
                if (RenameAt(directory, entry!, claimedName) != 0)
                {
                    if (Marshal.GetLastPInvokeError() == 2)
                    {
                        continue;
                    }

                    throw NativeFailure("Could not claim scratch child.");
                }

                using var moved = OpenAnyAt(directory, claimedName);
                if (ReadIdentity(moved) != identity)
                {
                    RestoreUnexpectedClaim(directory, claimedName, entry!, HandlePath(directory));
                    throw new InvalidDataException("A scratch child changed identity during deletion claim.");
                }

                if (IsDirectory(moved))
                {
                    using var movedDirectory = OpenDirectoryAt(directory, claimedName);
                    if (ReadIdentity(movedDirectory) != identity)
                    {
                        throw new InvalidDataException("A claimed scratch directory changed identity.");
                    }

                    DeleteContents(movedDirectory);
                    if (UnlinkAt(directory, claimedName, RemoveDirectory) != 0)
                    {
                        throw NativeFailure("Could not remove scratch child directory.");
                    }
                }
                else if (UnlinkAt(directory, claimedName, 0) != 0)
                {
                    throw NativeFailure("Could not remove scratch child file.");
                }

                NpgsqlBackupFileSystem.Checkpoint("scratch-after-child-delete");
            }
        }
    }

    private static void DeleteClaimedTrash(
        SafeFileHandle parent,
        string parentPath,
        string trashName,
        string purpose,
        SafeFileHandle? alreadyOpen = null)
    {
        var claimed = alreadyOpen ?? OpenDirectoryAt(parent, trashName);
        try
        {
            var identity = ReadIdentity(claimed);
            ScratchClaim claim;
            try
            {
                var proof = ReadOwnerProof(claimed);
                ValidateOwner(proof.Owner, purpose, trashName);
                claim = new ScratchClaim(proof.Owner, identity);
                EnsureClaimSidecar(parent, parentPath, trashName, claim);
            }
            catch (InvalidDataException)
            {
                claim = ReadClaimSidecar(parent, trashName);
                ValidateOwner(claim.Owner, purpose, trashName);
                if (claim.DirectoryIdentity != identity)
                {
                    throw new InvalidDataException("A claimed scratch tombstone changed identity.");
                }
            }

            DeleteContents(claimed);
            NpgsqlBackupFileSystem.Checkpoint("scratch-before-delete-root");
            if (UnlinkAt(parent, trashName, RemoveDirectory) != 0 && Marshal.GetLastPInvokeError() != 2)
            {
                throw NativeFailure("Could not remove claimed scratch.");
            }

            NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-delete-parent");
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-delete-root");
            _ = UnlinkAt(parent, ClaimSidecarName(trashName), 0);
            NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-claim-cleanup-parent");
        }
        finally
        {
            if (alreadyOpen is null)
            {
                claimed.Dispose();
            }
        }
    }

    private static void EnsureClaimSidecar(
        SafeFileHandle parent,
        string parentPath,
        string trashName,
        ScratchClaim claim)
    {
        var name = ClaimSidecarName(trashName);
        try
        {
            if (ReadClaimSidecar(parent, trashName) == claim)
            {
                return;
            }

            throw new InvalidDataException("A durable scratch deletion claim conflicts with the opened generation.");
        }
        catch (InvalidDataException)
        {
            // Missing sidecar is published below. A malformed existing sidecar is
            // detected again by the no-replace publication path and fails closed.
        }

        var temporaryName = ".claim-new-" + Guid.NewGuid().ToString("N");
        var descriptor = OpenAt(
            parent.DangerousGetHandle().ToInt32(),
            temporaryName,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not durably record a scratch deletion claim.");
        }

        try
        {
            using (var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true))
            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                descriptor = -1;
                JsonSerializer.Serialize(stream, claim);
                stream.Flush(flushToDisk: true);
            }

            const uint renameNoReplace = 1;
            var parentDescriptor = parent.DangerousGetHandle().ToInt32();
            if (RenameAt2Native(parentDescriptor, temporaryName, parentDescriptor, name, renameNoReplace) != 0)
            {
                if (Marshal.GetLastPInvokeError() != 17 || ReadClaimSidecar(parent, trashName) != claim)
                {
                    throw new InvalidDataException("A conflicting scratch deletion claim was published concurrently.");
                }
            }

            NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-claim-sidecar-parent");
        }
        finally
        {
            if (descriptor >= 0)
            {
                using var failedHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            }

            _ = UnlinkAt(parent, temporaryName, 0);
        }
    }

    private static ScratchClaim ReadClaimSidecar(SafeFileHandle parent, string trashName)
    {
        var descriptor = OpenAt(
            parent.DangerousGetHandle().ToInt32(),
            ClaimSidecarName(trashName),
            OpenReadOnly | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new InvalidDataException("A scratch tombstone has neither an owner marker nor a durable claim.");
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        var node = ReadNodeInfo(handle);
        if (!node.IsRegularFile || node.LinkCount != 1)
        {
            throw new InvalidDataException("A scratch claim sidecar is not an exclusively owned regular file.");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        return JsonSerializer.Deserialize<ScratchClaim>(stream)
            ?? throw new InvalidDataException("A scratch claim sidecar is empty.");
    }

    private static string ClaimSidecarName(string trashName) => trashName + ".claim";

    private static ScratchOwner ReadOwnerByPath(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            using var stream = File.OpenRead(Path.Combine(directory, OwnerMarker));
            return JsonSerializer.Deserialize<ScratchOwner>(stream) ?? throw new InvalidDataException("Scratch marker is empty.");
        }

        using var handle = OpenDirectoryHandle(directory);
        return ReadOwner(handle);
    }

    private static ScratchOwner ReadOwner(SafeFileHandle directory)
        => ReadOwnerProof(directory).Owner;

    private static ScratchOwnerProof ReadOwnerProof(SafeFileHandle directory)
    {
        var descriptor = OpenAt(directory.DangerousGetHandle().ToInt32(), OwnerMarker, OpenReadOnly | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new InvalidDataException("Scratch owner marker is missing or linked.");
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        var node = ReadNodeInfo(handle);
        if (!node.IsRegularFile || node.LinkCount != 1)
        {
            throw new InvalidDataException("Scratch owner marker is not an exclusively owned regular file.");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        var owner = JsonSerializer.Deserialize<ScratchOwner>(stream) ?? throw new InvalidDataException("Scratch marker is empty.");
        return new ScratchOwnerProof(owner, ReadIdentity(stream.SafeFileHandle));
    }

    private static void ValidateOwner(ScratchOwner owner, string purpose, string name)
    {
        if (!IsExpectedOwner(owner, purpose)
            || (!name.StartsWith(purpose + "-", StringComparison.Ordinal) && !name.StartsWith(".trash-", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Scratch owner identity or purpose is invalid.");
        }
    }

    private static bool IsExpectedOwner(ScratchOwner owner, string purpose)
        => owner.OwnerId != Guid.Empty
            && string.Equals(owner.Provider, PluginMetadata.Id.ToString("D"), StringComparison.Ordinal)
            && string.Equals(owner.Purpose, purpose, StringComparison.Ordinal);

    private static bool IsLiveOwner(ScratchOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.StartTime.ToUniversalTime() == owner.ProcessStartUtc;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void DeleteWindowsOwned(string directory, string purpose)
    {
        var owner = ReadOwnerByPath(directory);
        ValidateOwner(owner, purpose, Path.GetFileName(directory));
        var parent = Path.GetDirectoryName(directory)!;
        var trash = Path.Combine(parent, ".trash-" + owner.OwnerId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(directory, trash);
        Directory.Delete(trash, recursive: true);
    }

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var descriptor = Open(path, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        return descriptor >= 0 ? new SafeFileHandle((nint)descriptor, true) : throw NativeFailure("Could not open scratch directory.");
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name)
    {
        var descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), name, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        return descriptor >= 0 ? new SafeFileHandle((nint)descriptor, true) : throw NativeFailure("Could not open child scratch directory.");
    }

    private static SafeFileHandle OpenAnyAt(SafeFileHandle parent, string name)
    {
        var descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), name, OpenPath | OpenNoFollow | OpenCloseOnExec);
        return descriptor >= 0 ? new SafeFileHandle((nint)descriptor, true) : throw NativeFailure("Could not open scratch child.");
    }

    private static ScratchIdentity ReadIdentity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Statx(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath | AtSymlinkNoFollow, 0x100, buffer) != 0)
            {
                throw NativeFailure("Could not identify scratch object.");
            }

            return new ScratchIdentity((ulong)Marshal.ReadInt64(buffer, 32), (uint)Marshal.ReadInt32(buffer, 136), (uint)Marshal.ReadInt32(buffer, 140));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsDirectory(SafeFileHandle handle)
        => ReadNodeInfo(handle).IsDirectory;

    private static ScratchNodeInfo ReadNodeInfo(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Statx(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath | AtSymlinkNoFollow, 6, buffer) != 0)
            {
                throw NativeFailure("Could not inspect scratch object.");
            }

            var mode = (ushort)Marshal.ReadInt16(buffer, 28);
            return new ScratchNodeInfo(
                (uint)Marshal.ReadInt32(buffer, 16),
                (mode & 0xF000) == 0x4000,
                (mode & 0xF000) == 0x8000);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string HandlePath(SafeFileHandle handle)
        => "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RestoreUnexpectedClaim(
        SafeFileHandle parent,
        string claimedName,
        string originalName,
        string parentPath)
    {
        const uint renameNoReplace = 1;
        var descriptor = parent.DangerousGetHandle().ToInt32();
        if (RenameAt2Native(descriptor, claimedName, descriptor, originalName, renameNoReplace) == 0)
        {
            NpgsqlBackupFileSystem.FlushDirectory(parentPath, "flush-scratch-identity-restore-parent");
        }
    }

    private static IOException NativeFailure(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

#pragma warning disable CA2101, SYSLIB1054
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "renameat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameAtNative(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    private static int RenameAt(SafeFileHandle parent, string oldName, string newName)
        => RenameAtNative(parent.DangerousGetHandle().ToInt32(), oldName, parent.DangerousGetHandle().ToInt32(), newName);

    [DllImport("libc", EntryPoint = "renameat2", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameAt2Native(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int UnlinkAtNative(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static int UnlinkAt(SafeFileHandle parent, string name, int flags)
        => UnlinkAtNative(parent.DangerousGetHandle().ToInt32(), name, flags);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, nint buffer);
#pragma warning restore CA2101, SYSLIB1054

    private sealed record ScratchOwner(string Provider, int ProcessId, DateTime ProcessStartUtc, string Purpose, Guid OwnerId);

    private sealed record ScratchOwnerProof(ScratchOwner Owner, ScratchIdentity MarkerIdentity);

    private sealed record ScratchClaim(ScratchOwner Owner, ScratchIdentity DirectoryIdentity);

    private sealed record ScratchNodeInfo(uint LinkCount, bool IsDirectory, bool IsRegularFile);

    private sealed record ScratchIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);
}
#pragma warning restore SA1202, SA1204, SA1600, CA1416, CA2000
