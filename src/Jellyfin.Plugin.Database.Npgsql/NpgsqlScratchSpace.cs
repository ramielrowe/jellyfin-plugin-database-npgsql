using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private static readonly AsyncLocal<Func<int, DateTime, bool>?> OwnerLivenessOverride = new();
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

    internal static IDisposable InjectOwnerLivenessForTests(Func<int, DateTime, bool> callback)
    {
        var previous = OwnerLivenessOverride.Value;
        OwnerLivenessOverride.Value = callback;
        return new CallbackScope(() => OwnerLivenessOverride.Value = previous);
    }

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
        var generationName = purpose + "-" + ownerId.ToString("N");
        var stagingName = ".init-" + generationName;
        var directory = Path.Combine(parent, generationName);
        var stagingDirectory = Path.Combine(parent, stagingName);
        Directory.CreateDirectory(stagingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stagingDirectory, OwnerDirectoryMode);
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

        using var directoryHandle = OperatingSystem.IsLinux() ? OpenDirectoryHandle(stagingDirectory) : null;
        var directoryIdentity = directoryHandle is null ? null : ReadIdentity(directoryHandle);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var owner = new ScratchOwner(
            PluginMetadata.Id.ToString("D"),
            Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            purpose,
            ownerId,
            generationName,
            directoryIdentity,
            nonce,
            string.Empty);
        owner = owner with { Proof = ComputeOwnerProof(owner) };
        using (var stream = new FileStream(Path.Combine(stagingDirectory, OwnerMarker), options))
        {
            JsonSerializer.Serialize(stream, owner);
            stream.Flush(flushToDisk: true);
        }

        if (directoryHandle is not null)
        {
            NpgsqlBackupFileSystem.FlushHandle(directoryHandle, "flush-scratch-owner");
        }
        else
        {
            NpgsqlBackupFileSystem.FlushDirectory(stagingDirectory, "flush-scratch-owner");
        }

        using var parentHandle = OperatingSystem.IsLinux() ? OpenDirectoryHandle(parent) : null;
        if (parentHandle is not null)
        {
            EnsureRenameNoReplaceSupported(parentHandle);
            RenameNoReplace(parentHandle, stagingName, generationName, "Could not publish owned scratch generation.");
            NpgsqlBackupFileSystem.FlushHandle(parentHandle, "flush-scratch-parent");
        }
        else
        {
            Directory.Move(stagingDirectory, directory);
            NpgsqlBackupFileSystem.FlushDirectory(parent, "flush-scratch-parent");
        }

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
        EnsureRenameNoReplaceSupported(parent);
        if (name.StartsWith(".trash-", StringComparison.Ordinal))
        {
            DeleteClaimedTrash(parent, name, purpose);
            return;
        }

        using var original = OpenDirectoryAt(parent, name);
        var identity = ReadIdentity(original);
        var ownerProof = ReadOwnerProof(original);
        var owner = ownerProof.Owner;
        ValidateOwner(owner, identity, purpose, name);
        var trashName = ".trash-" + owner.OwnerId.ToString("N") + "-" + Guid.NewGuid().ToString("N");

        NpgsqlBackupFileSystem.Checkpoint("scratch-before-claim-rename");
        RenameNoReplace(parent, name, trashName, "Could not atomically claim scratch.");

        NpgsqlBackupFileSystem.Checkpoint("scratch-after-claim-rename");
        NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-claim-parent");
        using var claimed = OpenDirectoryAt(parent, trashName);
        if (ReadIdentity(claimed) != identity || ReadOwnerProof(claimed) != ownerProof)
        {
            RestoreUnexpectedClaim(parent, trashName, name);
            throw new InvalidDataException("The claimed scratch generation changed identity; it was not deleted.");
        }

        EnsureClaimSidecar(parent, trashName, new ScratchClaim(owner, identity, trashName));
        DeleteClaimedTrash(parent, trashName, purpose, claimed);
    }

    internal static void ScavengeValidationDirectories(string parent) => Scavenge(parent, "validation", "validation-");

    /// <summary>Replays only already-claimed tombstones below a stable recovery-root handle.</summary>
    /// <param name="parent">The pinned recovery-root generation.</param>
    /// <param name="purpose">The exact owner purpose.</param>
    internal static void ScavengeClaimedFromHandle(SafeFileHandle parent, string purpose)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var candidate in Directory.EnumerateDirectories(HandlePath(parent), ".trash-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                DeleteClaimedTrash(parent, Path.GetFileName(candidate), purpose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A concurrent or unrelated replacement is preserved fail closed.
            }
        }

        ScavengeOrphanedClaimSidecars(parent, purpose);
    }

    /// <summary>Validates ownership through an already-open no-follow directory handle.</summary>
    /// <param name="directory">The stable directory handle.</param>
    /// <param name="purpose">The required owner purpose.</param>
    /// <param name="name">The live or tombstoned basename.</param>
    internal static void ValidateOwnedHandle(SafeFileHandle directory, string purpose, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var proof = ReadOwnerProof(directory);
        ValidateOwner(proof.Owner, ReadIdentity(directory), purpose, name);
    }

    internal static void Scavenge(string parent, string purpose, string prefix)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        ScavengeUninitialized(parent, purpose);
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

    private static void ScavengeUninitialized(string parentPath, string purpose)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var parent = OpenDirectoryHandle(parentPath);
        EnsureRenameNoReplaceSupported(parent);
        var prefix = ".init-" + purpose + "-";
        foreach (var candidate in Directory.EnumerateDirectories(HandlePath(parent), prefix + "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(candidate);
            if (!Guid.TryParseExact(name[prefix.Length..], "N", out _))
            {
                continue;
            }

            using var handle = OpenDirectoryAt(parent, name);
            var entries = Directory.EnumerateFileSystemEntries(HandlePath(handle))
                .Select(Path.GetFileName)
                .ToArray();
            if (entries.Length == 1 && string.Equals(entries[0], OwnerMarker, StringComparison.Ordinal))
            {
                var proof = ReadOwnerProof(handle);
                if (!IsExpectedOwner(proof.Owner, purpose)
                    || proof.Owner.DirectoryIdentity != ReadIdentity(handle)
                    || IsLiveOwner(proof.Owner))
                {
                    continue;
                }

                RenameNoReplace(parent, name, proof.Owner.GenerationName, "Could not recover initialized scratch generation.");
                NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-initialized-recovery-parent");
                ClaimAndDelete(Path.Combine(parentPath, proof.Owner.GenerationName), purpose);
                continue;
            }

            if (entries.Length != 0
                || DateTime.UtcNow - File.GetLastWriteTimeUtc(handle) < TimeSpan.FromHours(1))
            {
                continue;
            }

            var identity = ReadIdentity(handle);
            var claimedName = ".trash-uninitialized-" + Guid.NewGuid().ToString("N");
            RenameNoReplace(parent, name, claimedName, "Could not claim uninitialized scratch generation.");
            using var claimed = OpenDirectoryAt(parent, claimedName);
            if (ReadIdentity(claimed) != identity || Directory.EnumerateFileSystemEntries(HandlePath(claimed)).Any())
            {
                RestoreUnexpectedClaim(parent, claimedName, name);
                continue;
            }

            UnlinkPinned(parent, claimedName, claimed, removeDirectory: true, "Could not remove uninitialized scratch generation.");
            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-uninitialized-parent");
        }
    }

    private static void ScavengeOrphanedClaimSidecars(string parentPath, string purpose)
    {
        using var parent = OpenDirectoryHandle(parentPath);
        ScavengeOrphanedClaimSidecars(parent, purpose);
    }

    private static void ScavengeOrphanedClaimSidecars(SafeFileHandle parent, string purpose)
    {
        foreach (var sidecarPath in Directory.EnumerateFiles(HandlePath(parent), ".trash-*.claim", SearchOption.TopDirectoryOnly))
        {
            var sidecarName = Path.GetFileName(sidecarPath);
            var trashName = sidecarName[..^".claim".Length];
            if (Directory.Exists(Path.Combine(HandlePath(parent), trashName)))
            {
                continue;
            }

            try
            {
                var claim = ReadClaimSidecar(parent, trashName);
                ValidateClaim(claim, ReadIdentityForNameIfPresent(parent, trashName), purpose, trashName, allowMissingDirectory: true);
                using var original = OpenAnyAt(parent, sidecarName);
                var identity = ReadIdentity(original);
                var deletingName = ".delete-claim-" + Guid.NewGuid().ToString("N");
                RenameNoReplace(parent, sidecarName, deletingName, "Could not atomically claim scratch sidecar.");

                using var moved = OpenAnyAt(parent, deletingName);
                if (ReadIdentity(moved) != identity)
                {
                    RestoreUnexpectedClaim(parent, deletingName, sidecarName);
                    continue;
                }

                NpgsqlBackupFileSystem.Checkpoint("scratch-before-delete-sidecar");
                UnlinkPinned(parent, deletingName, moved, removeDirectory: false, "Could not remove claimed scratch sidecar.");
                NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-orphaned-claim-parent");
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
                try
                {
                    RenameNoReplace(directory, entry!, claimedName, "Could not claim scratch child.");
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                using var moved = OpenAnyAt(directory, claimedName);
                if (ReadIdentity(moved) != identity)
                {
                    RestoreUnexpectedClaim(directory, claimedName, entry!);
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
                    NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-unlink");
                    UnlinkPinned(directory, claimedName, movedDirectory, removeDirectory: true, "Could not remove scratch child directory.");
                }
                else
                {
                    NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-unlink");
                    UnlinkPinned(directory, claimedName, moved, removeDirectory: false, "Could not remove scratch child file.");
                }

                NpgsqlBackupFileSystem.Checkpoint("scratch-after-child-delete");
            }
        }
    }

    private static void DeleteClaimedTrash(
        SafeFileHandle parent,
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
                ValidateOwner(proof.Owner, identity, purpose, trashName);
                claim = new ScratchClaim(proof.Owner, identity, trashName);
                EnsureClaimSidecar(parent, trashName, claim);
            }
            catch (InvalidDataException)
            {
                claim = ReadClaimSidecar(parent, trashName);
                ValidateClaim(claim, identity, purpose, trashName, allowMissingDirectory: false);
            }

            DeleteContents(claimed);
            NpgsqlBackupFileSystem.Checkpoint("scratch-before-delete-root");
            UnlinkPinned(parent, trashName, claimed, removeDirectory: true, "Could not remove claimed scratch.");

            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-delete-parent");
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-delete-root");
            var sidecarName = ClaimSidecarName(trashName);
            using var sidecar = OpenAnyAt(parent, sidecarName);
            var sidecarDeleteName = ".delete-claim-" + claim.Owner.OwnerId.ToString("N") + "-" + Guid.NewGuid().ToString("N");
            RenameNoReplace(parent, sidecarName, sidecarDeleteName, "Could not claim scratch sidecar cleanup.");
            UnlinkPinned(parent, sidecarDeleteName, sidecar, removeDirectory: false, "Could not remove scratch claim sidecar.");
            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-claim-cleanup-parent");
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
        var temporaryPublished = false;
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
                var error = Marshal.GetLastPInvokeError();
                if (error is 38 or 22)
                {
                    throw new PlatformNotSupportedException("Atomic no-replace scratch claims require renameat2(RENAME_NOREPLACE).");
                }

                if (error != 17 || ReadClaimSidecar(parent, trashName) != claim)
                {
                    throw new InvalidDataException("A conflicting scratch deletion claim was published concurrently.");
                }
            }
            else
            {
                temporaryPublished = true;
            }

            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-claim-sidecar-parent");
        }
        finally
        {
            if (descriptor >= 0)
            {
                using var failedHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            }

            if (!temporaryPublished)
            {
                try
                {
                    using var temporary = OpenAnyAt(parent, temporaryName);
                    UnlinkPinned(parent, temporaryName, temporary, removeDirectory: false, "Could not remove unpublished scratch claim sidecar.");
                    NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-claim-temporary-cleanup-parent");
                }
                catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 })
                {
                    // A failed create or concurrent cleanup left no private temporary.
                }
            }
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

    private static void ValidateOwner(ScratchOwner owner, ScratchIdentity identity, string purpose, string name)
    {
        if (!IsExpectedOwner(owner, purpose)
            || owner.DirectoryIdentity != identity
            || (!string.Equals(name, owner.GenerationName, StringComparison.Ordinal)
                && !name.StartsWith(".trash-" + owner.OwnerId.ToString("N") + "-", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Scratch owner identity or purpose is invalid.");
        }
    }

    private static bool IsExpectedOwner(ScratchOwner owner, string purpose)
        => owner.OwnerId != Guid.Empty
            && (OperatingSystem.IsWindows() || owner.DirectoryIdentity is not null)
            && owner.Nonce is { Length: 64 }
            && owner.Proof is { Length: 64 }
            && FixedTimeEqualsHex(owner.Proof, ComputeOwnerProof(owner with { Proof = string.Empty }))
            && string.Equals(owner.GenerationName, purpose + "-" + owner.OwnerId.ToString("N"), StringComparison.Ordinal)
            && string.Equals(owner.Provider, PluginMetadata.Id.ToString("D"), StringComparison.Ordinal)
            && string.Equals(owner.Purpose, purpose, StringComparison.Ordinal);

    private static bool IsLiveOwner(ScratchOwner owner)
    {
        if (OwnerLivenessOverride.Value is { } callback)
        {
            return callback(owner.ProcessId, owner.ProcessStartUtc);
        }

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
        if (!IsExpectedOwner(owner, purpose)
            || !string.Equals(Path.GetFileName(directory), owner.GenerationName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Scratch owner identity or purpose is invalid.");
        }

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
        string originalName)
    {
        const uint renameNoReplace = 1;
        var descriptor = parent.DangerousGetHandle().ToInt32();
        if (RenameAt2Native(descriptor, claimedName, descriptor, originalName, renameNoReplace) == 0)
        {
            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-identity-restore-parent");
        }
    }

    private static void RenameNoReplace(SafeFileHandle parent, string source, string destination, string message)
    {
        const uint renameNoReplace = 1;
        var descriptor = parent.DangerousGetHandle().ToInt32();
        if (RenameAt2Native(descriptor, source, descriptor, destination, renameNoReplace) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 2)
        {
            throw new FileNotFoundException(message, source);
        }

        if (error is 38 or 22)
        {
            throw new PlatformNotSupportedException("Atomic no-replace scratch claims require renameat2(RENAME_NOREPLACE).");
        }

        throw NativeFailure(message);
    }

    private static void EnsureRenameNoReplaceSupported(SafeFileHandle parent)
    {
        const uint renameNoReplace = 1;
        var missing = ".renameat2-probe-missing-" + Guid.NewGuid().ToString("N");
        var destination = ".renameat2-probe-destination-" + Guid.NewGuid().ToString("N");
        var descriptor = parent.DangerousGetHandle().ToInt32();
        if (RenameAt2Native(descriptor, missing, descriptor, destination, renameNoReplace) == 0)
        {
            throw new InvalidOperationException("The atomic no-replace capability probe unexpectedly mutated the scratch namespace.");
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is 38 or 22)
        {
            throw new PlatformNotSupportedException("Atomic no-replace scratch claims require renameat2(RENAME_NOREPLACE).");
        }

        if (error != 2)
        {
            throw NativeFailure("Could not verify atomic no-replace scratch support.");
        }
    }

    private static string ComputeOwnerProof(ScratchOwner owner)
    {
        var identity = owner.DirectoryIdentity is null
            ? "windows"
            : owner.DirectoryIdentity.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":"
                + owner.DirectoryIdentity.DeviceMajor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":"
                + owner.DirectoryIdentity.DeviceMinor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var material = string.Join(
            "\n",
            owner.Provider,
            owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.ProcessStartUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.Purpose,
            owner.OwnerId.ToString("D"),
            owner.GenerationName,
            identity,
            owner.Nonce);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void UnlinkPinned(
        SafeFileHandle parent,
        string name,
        SafeFileHandle pinned,
        bool removeDirectory,
        string message)
    {
        using var current = OpenAnyAt(parent, name);
        if (ReadIdentity(current) != ReadIdentity(pinned))
        {
            throw new InvalidDataException("A claimed scratch object was replaced immediately before unlink; the replacement was preserved.");
        }

        NpgsqlBackupFileSystem.Checkpoint("scratch-after-pinned-unlink-check");
        using var final = OpenAnyAt(parent, name);
        if (ReadIdentity(final) != ReadIdentity(pinned))
        {
            throw new InvalidDataException("A claimed scratch object was replaced at the unlink boundary; the replacement was preserved.");
        }

        if (UnlinkAt(parent, name, removeDirectory ? RemoveDirectory : 0) != 0)
        {
            throw NativeFailure(message);
        }
    }

    private static ScratchIdentity? ReadIdentityForNameIfPresent(SafeFileHandle parent, string name)
    {
        try
        {
            using var handle = OpenAnyAt(parent, name);
            return ReadIdentity(handle);
        }
        catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 })
        {
            return null;
        }
    }

    private static void ValidateClaim(
        ScratchClaim claim,
        ScratchIdentity? visibleIdentity,
        string purpose,
        string trashName,
        bool allowMissingDirectory)
    {
        if (!string.Equals(claim.TrashName, trashName, StringComparison.Ordinal)
            || (!allowMissingDirectory && visibleIdentity is null)
            || (visibleIdentity is not null && claim.DirectoryIdentity != visibleIdentity))
        {
            throw new InvalidDataException("A scratch claim does not bind the exact tombstone generation.");
        }

        ValidateOwner(claim.Owner, claim.DirectoryIdentity, purpose, trashName);
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

    private sealed record ScratchOwner(
        string Provider,
        int ProcessId,
        DateTime ProcessStartUtc,
        string Purpose,
        Guid OwnerId,
        string GenerationName,
        ScratchIdentity? DirectoryIdentity,
        string Nonce,
        string Proof);

    private sealed record ScratchOwnerProof(ScratchOwner Owner, ScratchIdentity MarkerIdentity);

    private sealed record ScratchClaim(ScratchOwner Owner, ScratchIdentity DirectoryIdentity, string TrashName);

    private sealed record ScratchNodeInfo(uint LinkCount, bool IsDirectory, bool IsRegularFile);

    private sealed record ScratchIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
#pragma warning restore SA1202, SA1204, SA1600, CA1416, CA2000
