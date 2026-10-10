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
    private const string RootKeyPrefix = ".jellyfin-npgsql-root-key-";
    private const string RootKeySuffix = ".json";
    private const string InitializationReceiptPrefix = ".jellyfin-npgsql-init-receipt-";
    private const int MaximumRetainedEntries = 1024;
    private const long MaximumRetainedBytes = 64L * 1024 * 1024;
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

        EnsureRetentionCapacity(parent);
        var rootKey = LoadOrCreateRootKey(parent);

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

        NpgsqlBackupFileSystem.Checkpoint("scratch-init-after-directory");

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
        if (directoryHandle is not null)
        {
            WriteInitializationReceipt(parent, stagingName, purpose, ownerId, directoryIdentity!, rootKey);
        }

        NpgsqlBackupFileSystem.Checkpoint("scratch-init-after-receipt");

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var stream = new FileStream(Path.Combine(stagingDirectory, OwnerMarker), options))
        {
            var markerIdentity = OperatingSystem.IsLinux() ? ReadIdentity(stream.SafeFileHandle) : null;
            var owner = new ScratchOwner(
                PluginMetadata.Id.ToString("D"),
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                purpose,
                ownerId,
                generationName,
                directoryIdentity,
                markerIdentity,
                rootKey.KeyId,
                nonce,
                string.Empty);
            owner = owner with { Proof = ComputeOwnerProof(owner, rootKey.Secret) };
            JsonSerializer.Serialize(stream, owner);
            stream.Flush(flushToDisk: true);
        }

        NpgsqlBackupFileSystem.Checkpoint("scratch-init-after-owner-marker");

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
        ValidateOwner(ownerProof, identity, purpose, name, parentPath);
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

        EnsureClaimSidecar(
            parent,
            trashName,
            new ScratchClaim(owner, identity, trashName, owner.KeyId, null, string.Empty));
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
        var directoryPath = ResolveHandlePath(directory);
        ValidateOwner(proof, ReadIdentity(directory), purpose, name, Path.GetDirectoryName(directoryPath)!);
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
                if (!IsExpectedOwner(owner, purpose, parent) || IsLiveOwner(owner))
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
            try
            {
                var name = Path.GetFileName(candidate);
                if (!Guid.TryParseExact(name[prefix.Length..], "N", out var ownerId))
                {
                    continue;
                }

                using var handle = OpenDirectoryAt(parent, name);
                var identity = ReadIdentity(handle);
                var entries = Directory.EnumerateFileSystemEntries(HandlePath(handle))
                    .Select(Path.GetFileName)
                    .ToArray();
                if (entries.Length == 1 && string.Equals(entries[0], OwnerMarker, StringComparison.Ordinal))
                {
                    var proof = ReadOwnerProof(handle);
                    if (!IsExpectedOwner(proof, purpose, parentPath)
                        || proof.Owner.DirectoryIdentity != identity
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
                    || DateTime.UtcNow - File.GetLastWriteTimeUtc(handle) < TimeSpan.FromHours(1)
                    || ReadInitializationReceipt(parent, name, purpose, ownerId, identity) is null)
                {
                    continue;
                }

                var claimedName = ".trash-uninitialized-" + ownerId.ToString("N") + "-" + Guid.NewGuid().ToString("N");
                RenameNoReplace(parent, name, claimedName, "Could not claim uninitialized scratch generation.");
                using var claimed = OpenDirectoryAt(parent, claimedName);
                if (ReadIdentity(claimed) != identity || Directory.EnumerateFileSystemEntries(HandlePath(claimed)).Any())
                {
                    RestoreUnexpectedClaim(parent, claimedName, name);
                    continue;
                }

                ReclaimPinned(claimed);
                NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-uninitialized-parent");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Each partial generation is isolated. Torn or malformed state is
                // retained and cannot prevent other recoveries or host startup.
            }
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
                ValidateClaim(
                    claim,
                    ReadIdentityForNameIfPresent(parent, trashName),
                    purpose,
                    trashName,
                    allowMissingDirectory: true,
                    ResolveHandlePath(parent));
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
                ReclaimPinned(moved);
                NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-orphaned-claim-parent");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // An unrelated or concurrently replaced sidecar is never removed.
            }
        }
    }

    private static void WriteEntryClaim(
        SafeFileHandle directory,
        Guid claimId,
        ScratchEntryClaim unsignedClaim,
        RootKey rootKey)
    {
        NpgsqlBackupFileSystem.Checkpoint("scratch-before-entry-claim");
        var name = ".entry-claim-" + claimId.ToString("N") + ".json";
        var descriptor = OpenAt(
            directory.DangerousGetHandle().ToInt32(),
            name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not create a durable scratch entry claim.");
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Write);
        var claim = unsignedClaim with { ClaimMarkerIdentity = ReadIdentity(stream.SafeFileHandle) };
        claim = claim with { Proof = ComputeEntryClaimProof(claim, rootKey.Secret) };
        JsonSerializer.Serialize(stream, claim);
        stream.Flush(flushToDisk: true);
        NpgsqlBackupFileSystem.FlushHandle(directory, "flush-scratch-entry-claim-parent");
        NpgsqlBackupFileSystem.Checkpoint("scratch-after-entry-claim");
    }

    private static void ReplayRetainedClaims(SafeFileHandle directory, RootKey rootKey)
    {
        foreach (var claimPath in Directory.EnumerateFiles(HandlePath(directory), ".entry-claim-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var claimHandle = OpenAnyAt(directory, Path.GetFileName(claimPath));
                var claimNode = ReadNodeInfo(claimHandle);
                if (!claimNode.IsRegularFile || claimNode.LinkCount != 1)
                {
                    continue;
                }

                using var readableDescriptor = new SafeFileHandle(
                    (nint)Open(HandlePath(claimHandle), OpenReadOnly | OpenCloseOnExec),
                    ownsHandle: true);
                if (readableDescriptor.IsInvalid)
                {
                    continue;
                }

                using var stream = new FileStream(readableDescriptor, FileAccess.Read);
                var claim = JsonSerializer.Deserialize<ScratchEntryClaim>(stream);
                if (claim is null
                    || claim.ParentIdentity != ReadIdentity(directory)
                    || claim.ClaimMarkerIdentity != ReadIdentity(claimHandle)
                    || claim.KeyId != rootKey.KeyId
                    || !FixedTimeEqualsHex(claim.Proof, ComputeEntryClaimProof(claim with { Proof = string.Empty }, rootKey.Secret)))
                {
                    continue;
                }

                using var retained = OpenAnyAt(directory, claim.RetainedName);
                if (ReadIdentity(retained) != claim.TargetIdentity)
                {
                    continue;
                }

                if (IsDirectory(retained))
                {
                    using var retainedDirectory = OpenDirectoryAt(directory, claim.RetainedName);
                    if (ReadIdentity(retainedDirectory) == claim.TargetIdentity)
                    {
                        DeleteContents(retainedDirectory, rootKey);
                    }
                }
                else
                {
                    ReclaimPinned(retained);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Torn or raced claims are retained and never authorize mutation.
            }
        }
    }

    private static void DeleteContents(SafeFileHandle directory, RootKey rootKey)
    {
        ReplayRetainedClaims(directory, rootKey);
        while (true)
        {
            var entries = Directory.EnumerateFileSystemEntries(HandlePath(directory), "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !string.Equals(name, OwnerMarker, StringComparison.Ordinal)
                    && !name!.StartsWith(".retained-", StringComparison.Ordinal)
                    && !name.StartsWith(".entry-claim-", StringComparison.Ordinal))
                .ToArray();
            if (entries.Length == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                using var child = OpenAnyAt(directory, entry!);
                var identity = ReadIdentity(child);
                var claimId = Guid.NewGuid();
                var claimedName = ".retained-" + claimId.ToString("N");
                var claim = new ScratchEntryClaim(
                    entry!,
                    claimedName,
                    identity,
                    ReadIdentity(directory),
                    rootKey.KeyId,
                    null,
                    string.Empty);
                WriteEntryClaim(directory, claimId, claim, rootKey);
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

                    DeleteContents(movedDirectory, rootKey);
                    NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-unlink");
                    ReclaimPinned(movedDirectory);
                }
                else
                {
                    NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-unlink");
                    ReclaimPinned(moved);
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
                ValidateOwner(proof, identity, purpose, trashName, ResolveHandlePath(parent));
                claim = new ScratchClaim(
                    proof.Owner,
                    identity,
                    trashName,
                    proof.Owner.KeyId,
                    null,
                    string.Empty);
                EnsureClaimSidecar(parent, trashName, claim);
            }
            catch (InvalidDataException)
            {
                claim = ReadClaimSidecar(parent, trashName);
                ValidateClaim(
                    claim,
                    identity,
                    purpose,
                    trashName,
                    allowMissingDirectory: false,
                    ResolveHandlePath(parent));
            }

            var rootKey = LoadRootKey(ResolveHandlePath(parent), claim.Owner.KeyId);
            DeleteContents(claimed, rootKey);
            NpgsqlBackupFileSystem.Checkpoint("scratch-before-delete-root");

            // Linux cannot unlink an exact open file description. The verified,
            // emptied generation therefore remains as a bounded tombstone. This
            // intentionally leaks metadata rather than risking unlinking a raced
            // replacement through a mutable directory-entry name.
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-pinned-unlink-check");

            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-delete-parent");
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-delete-root");
            _ = claim;
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
        NpgsqlBackupFileSystem.Checkpoint("scratch-before-claim-sidecar");
        var name = ClaimSidecarName(trashName);
        try
        {
            var existing = ReadClaimSidecar(parent, trashName);
            ValidateClaim(
                existing,
                claim.DirectoryIdentity,
                claim.Owner.Purpose,
                trashName,
                allowMissingDirectory: false,
                ResolveHandlePath(parent));
            if (SameClaim(existing, claim))
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
                var key = LoadRootKey(ResolveHandlePath(parent), claim.KeyId);
                var signedClaim = claim with { ClaimMarkerIdentity = ReadIdentity(stream.SafeFileHandle) };
                signedClaim = signedClaim with { Proof = ComputeScratchClaimProof(signedClaim, key.Secret) };
                JsonSerializer.Serialize(stream, signedClaim);
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

                if (error != 17)
                {
                    throw new InvalidDataException("A conflicting scratch deletion claim was published concurrently.");
                }

                var existing = ReadClaimSidecar(parent, trashName);
                ValidateClaim(
                    existing,
                    claim.DirectoryIdentity,
                    claim.Owner.Purpose,
                    trashName,
                    allowMissingDirectory: false,
                    ResolveHandlePath(parent));
                if (!SameClaim(existing, claim))
                {
                    throw new InvalidDataException("A conflicting scratch deletion claim was published concurrently.");
                }
            }
            else
            {
                temporaryPublished = true;
            }

            NpgsqlBackupFileSystem.FlushHandle(parent, "flush-scratch-claim-sidecar-parent");
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-claim-sidecar");
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
                    ReclaimPinned(temporary);
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
        var claim = JsonSerializer.Deserialize<ScratchClaim>(stream)
            ?? throw new InvalidDataException("A scratch claim sidecar is empty.");
        if (claim.ClaimMarkerIdentity != ReadIdentity(handle))
        {
            throw new InvalidDataException("A scratch claim sidecar changed identity.");
        }

        return claim;
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

    private static void ValidateOwner(
        ScratchOwnerProof ownerProof,
        ScratchIdentity identity,
        string purpose,
        string name,
        string rootPath)
    {
        var owner = ownerProof.Owner;
        if (!IsExpectedOwner(ownerProof, purpose, rootPath)
            || owner.DirectoryIdentity != identity
            || (!string.Equals(name, owner.GenerationName, StringComparison.Ordinal)
                && !name.StartsWith(".trash-" + owner.OwnerId.ToString("N") + "-", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Scratch owner identity or purpose is invalid.");
        }
    }

    private static bool IsExpectedOwner(ScratchOwner owner, string purpose, string rootPath)
        => IsExpectedOwner(new ScratchOwnerProof(owner, owner.MarkerIdentity), purpose, rootPath);

    private static bool IsExpectedOwner(ScratchOwnerProof ownerProof, string purpose, string rootPath)
    {
        var owner = ownerProof.Owner;
        RootKey key;
        try
        {
            key = LoadRootKey(rootPath, owner.KeyId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }

        return
            owner.MarkerIdentity == ownerProof.MarkerIdentity
            && owner.OwnerId != Guid.Empty
            && (OperatingSystem.IsWindows() || owner.DirectoryIdentity is not null)
            && owner.Nonce is { Length: 64 }
            && owner.Proof is { Length: 64 }
            && FixedTimeEqualsHex(owner.Proof, ComputeOwnerProof(owner with { Proof = string.Empty }, key.Secret))
            && string.Equals(owner.GenerationName, owner.Purpose + "-" + owner.OwnerId.ToString("N"), StringComparison.Ordinal)
            && string.Equals(owner.GenerationName, purpose + "-" + owner.OwnerId.ToString("N"), StringComparison.Ordinal)
            && string.Equals(owner.Provider, PluginMetadata.Id.ToString("D"), StringComparison.Ordinal)
            && string.Equals(owner.Purpose, purpose, StringComparison.Ordinal);
    }

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
        if (!IsExpectedOwner(owner, purpose, Path.GetDirectoryName(directory)!)
            || !string.Equals(Path.GetFileName(directory), owner.GenerationName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Scratch owner identity or purpose is invalid.");
        }

        var parent = Path.GetDirectoryName(directory)!;
        var trash = Path.Combine(parent, ".trash-" + owner.OwnerId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(directory, trash);

        // The managed Windows directory APIs do not expose an exact opened-object
        // recursive delete. Retain the claimed generation instead of risking a
        // name race. The root retention budget bounds this fail-safe behavior.
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
            const uint requested = 0x100 | 0x800 | 0x1000;
            if (Statx(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath | AtSymlinkNoFollow, requested, buffer) != 0)
            {
                throw NativeFailure("Could not identify scratch object.");
            }

            var mask = (uint)Marshal.ReadInt32(buffer, 0);
            return new ScratchIdentity(
                (ulong)Marshal.ReadInt64(buffer, 32),
                (uint)Marshal.ReadInt32(buffer, 136),
                (uint)Marshal.ReadInt32(buffer, 140),
                (mask & 0x1000) != 0 ? (ulong)Marshal.ReadInt64(buffer, 144) : 0,
                (mask & 0x800) != 0 ? Marshal.ReadInt64(buffer, 80) : null,
                (mask & 0x800) != 0 ? (uint)Marshal.ReadInt32(buffer, 88) : null);
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

    private static string ResolveHandlePath(SafeFileHandle handle)
        => File.ResolveLinkTarget(HandlePath(handle), returnFinalTarget: true)?.FullName
            ?? throw new InvalidDataException("Could not resolve a pinned scratch handle.");

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

    private static RootKey LoadOrCreateRootKey(string rootPath)
    {
        var keys = LoadRootKeys(rootPath).OrderByDescending(key => key.CreatedUtc).ThenByDescending(key => key.KeyId).ToArray();
        if (keys.Length != 0)
        {
            return keys[0];
        }

        // A fresh random key cannot authorize generations which reference a
        // missing/torn older key ID. Bootstrap may therefore continue while
        // preserving all unprovable state fail closed.
        var key = new RootKey(
            Guid.NewGuid(),
            DateTime.UtcNow,
            ReadRootIdentity(rootPath),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            string.Empty);
        key = key with { Integrity = ComputeRootKeyIntegrity(key) };
        WriteRootKey(rootPath, key);
        return key;
    }

    private static ScratchIdentity? ReadRootIdentity(string rootPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        using var root = OpenDirectoryHandle(rootPath);
        return ReadIdentity(root);
    }

    internal static Guid RotateRootKeyForTests(string rootPath)
    {
        _ = LoadOrCreateRootKey(rootPath);
        var key = new RootKey(
            Guid.NewGuid(),
            DateTime.UtcNow.AddTicks(1),
            ReadRootIdentity(rootPath),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            string.Empty);
        key = key with { Integrity = ComputeRootKeyIntegrity(key) };
        WriteRootKey(rootPath, key);
        return key.KeyId;
    }

    private static void WriteRootKey(string rootPath, RootKey key)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(rootPath, RootKeyPrefix + key.KeyId.ToString("N") + RootKeySuffix);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, key);
            stream.Flush(flushToDisk: true);
            NpgsqlBackupFileSystem.FlushDirectory(rootPath, "flush-scratch-root-key-parent");
            return;
        }

        using var root = OpenDirectoryHandle(rootPath);
        var name = RootKeyPrefix + key.KeyId.ToString("N") + RootKeySuffix;
        var descriptor = OpenAt(
            root.DangerousGetHandle().ToInt32(),
            name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not create the scratch root proof key.");
        }

        using (var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true))
        using (var stream = new FileStream(handle, FileAccess.Write))
        {
            JsonSerializer.Serialize(stream, key);
            stream.Flush(flushToDisk: true);
        }

        NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-root-key-parent");
    }

    private static RootKey LoadRootKey(string rootPath, Guid keyId)
        => LoadRootKeys(rootPath).SingleOrDefault(key => key.KeyId == keyId)
            ?? throw new InvalidDataException("The scratch owner references a missing or invalid root proof key.");

    private static IEnumerable<RootKey> LoadRootKeys(string rootPath)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in Directory.EnumerateFiles(rootPath, RootKeyPrefix + "*" + RootKeySuffix, SearchOption.TopDirectoryOnly))
            {
                RootKey? key = null;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    key = JsonSerializer.Deserialize<RootKey>(stream);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A torn append-only key record is ignored; old keys remain.
                }

                if (key is not null
                    && key.Secret is { Length: 64 }
                    && key.RootIdentity is null
                    && string.Equals(Path.GetFileName(path), RootKeyPrefix + key.KeyId.ToString("N") + RootKeySuffix, StringComparison.Ordinal)
                    && FixedTimeEqualsHex(key.Integrity, ComputeRootKeyIntegrity(key with { Integrity = string.Empty })))
                {
                    yield return key;
                }
            }

            yield break;
        }

        using var root = OpenDirectoryHandle(rootPath);
        foreach (var path in Directory.EnumerateFiles(HandlePath(root), RootKeyPrefix + "*" + RootKeySuffix, SearchOption.TopDirectoryOnly))
        {
            SafeFileHandle? handle = null;
            try
            {
                var name = Path.GetFileName(path);
                handle = OpenAnyAt(root, name);
                var node = ReadNodeInfo(handle);
                if (!node.IsRegularFile
                    || node.LinkCount != 1
                    || File.GetUnixFileMode(HandlePath(handle)) != OwnerFileMode)
                {
                    continue;
                }

                var descriptor = Open(HandlePath(handle), OpenReadOnly | OpenCloseOnExec);
                if (descriptor < 0)
                {
                    continue;
                }

                using var readable = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                using var stream = new FileStream(readable, FileAccess.Read);
                var key = JsonSerializer.Deserialize<RootKey>(stream);
                if (key is null
                    || key.Secret is not { Length: 64 }
                    || key.RootIdentity != ReadIdentity(root)
                    || !string.Equals(name, RootKeyPrefix + key.KeyId.ToString("N") + RootKeySuffix, StringComparison.Ordinal)
                    || !FixedTimeEqualsHex(key.Integrity, ComputeRootKeyIntegrity(key with { Integrity = string.Empty })))
                {
                    continue;
                }

                yield return key;
            }
            finally
            {
                handle?.Dispose();
            }
        }
    }

    private static void WriteInitializationReceipt(
        string rootPath,
        string stagingName,
        string purpose,
        Guid ownerId,
        ScratchIdentity directoryIdentity,
        RootKey rootKey)
    {
        using var root = OpenDirectoryHandle(rootPath);
        var receiptName = InitializationReceiptPrefix + ownerId.ToString("N") + RootKeySuffix;
        var descriptor = OpenAt(
            root.DangerousGetHandle().ToInt32(),
            receiptName,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not create scratch initialization evidence.");
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Write);
        var receipt = new InitializationReceipt(
            stagingName,
            purpose,
            ownerId,
            directoryIdentity,
            rootKey.KeyId,
            ReadIdentity(stream.SafeFileHandle),
            string.Empty);
        receipt = receipt with { Proof = ComputeInitializationReceiptProof(receipt, rootKey.Secret) };
        JsonSerializer.Serialize(stream, receipt);
        stream.Flush(flushToDisk: true);
        NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-init-receipt-parent");
    }

    private static InitializationReceipt? ReadInitializationReceipt(
        SafeFileHandle root,
        string stagingName,
        string purpose,
        Guid ownerId,
        ScratchIdentity directoryIdentity)
    {
        var name = InitializationReceiptPrefix + ownerId.ToString("N") + RootKeySuffix;
        try
        {
            using var marker = OpenAnyAt(root, name);
            var node = ReadNodeInfo(marker);
            if (!node.IsRegularFile || node.LinkCount != 1)
            {
                return null;
            }

            var descriptor = Open(HandlePath(marker), OpenReadOnly | OpenCloseOnExec);
            if (descriptor < 0)
            {
                return null;
            }

            using var readable = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            using var stream = new FileStream(readable, FileAccess.Read);
            var receipt = JsonSerializer.Deserialize<InitializationReceipt>(stream);
            if (receipt is null
                || receipt.StagingName != stagingName
                || receipt.Purpose != purpose
                || receipt.OwnerId != ownerId
                || receipt.DirectoryIdentity != directoryIdentity
                || receipt.MarkerIdentity != ReadIdentity(marker))
            {
                return null;
            }

            var key = LoadRootKey(ResolveHandlePath(root), receipt.KeyId);
            return FixedTimeEqualsHex(receipt.Proof, ComputeInitializationReceiptProof(receipt with { Proof = string.Empty }, key.Secret))
                ? receipt
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void EnsureRetentionCapacity(string rootPath)
    {
        var retained = Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith(".trash-", StringComparison.Ordinal)
                    || name.StartsWith(".init-", StringComparison.Ordinal)
                    || name.StartsWith(".delete-claim-", StringComparison.Ordinal)
                    || name.StartsWith(".claim-new-", StringComparison.Ordinal);
            })
            .Take(MaximumRetainedEntries + 1)
            .ToArray();
        long bytes = 0;
        foreach (var path in retained)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    bytes = checked(bytes + new FileInfo(path).Length);
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    bytes = checked(bytes + new FileInfo(file).Length);
                    if (bytes > MaximumRetainedBytes)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Scratch retention could not be measured safely; new scratch work is disabled until an operator inspects the root.", exception);
            }
        }

        if (retained.Length > MaximumRetainedEntries || bytes > MaximumRetainedBytes)
        {
            throw new IOException(
                $"Scratch retention exceeded its fail-closed limit ({MaximumRetainedEntries} entries or {MaximumRetainedBytes} bytes). "
                + "No retained or unknown entry was deleted; an operator must inspect the owner-private scratch root.");
        }
    }

    private static string ComputeOwnerProof(ScratchOwner owner, string secret)
    {
        var material = string.Join(
            "\n",
            owner.Provider,
            owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.ProcessStartUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.Purpose,
            owner.OwnerId.ToString("D"),
            owner.GenerationName,
            FormatIdentity(owner.DirectoryIdentity),
            FormatIdentity(owner.MarkerIdentity),
            owner.KeyId.ToString("D"),
            owner.Nonce);
        return ComputeHmac(material, secret);
    }

    private static string ComputeEntryClaimProof(ScratchEntryClaim claim, string secret)
        => ComputeHmac(
            string.Join(
                "\n",
                "entry-claim-v1",
                claim.SourceName,
                claim.RetainedName,
                FormatIdentity(claim.TargetIdentity),
                FormatIdentity(claim.ParentIdentity),
                claim.KeyId.ToString("D"),
                FormatIdentity(claim.ClaimMarkerIdentity)),
            secret);

    private static string ComputeScratchClaimProof(ScratchClaim claim, string secret)
        => ComputeHmac(
            string.Join(
                "\n",
                "generation-claim-v1",
                claim.Owner.OwnerId.ToString("D"),
                claim.TrashName,
                FormatIdentity(claim.DirectoryIdentity),
                claim.KeyId.ToString("D"),
                FormatIdentity(claim.ClaimMarkerIdentity),
                FormatIdentity(claim.Owner.MarkerIdentity)),
            secret);

    private static string ComputeInitializationReceiptProof(InitializationReceipt receipt, string secret)
        => ComputeHmac(
            string.Join(
                "\n",
                "initialization-receipt-v1",
                receipt.StagingName,
                receipt.Purpose,
                receipt.OwnerId.ToString("D"),
                FormatIdentity(receipt.DirectoryIdentity),
                receipt.KeyId.ToString("D"),
                FormatIdentity(receipt.MarkerIdentity)),
            secret);

    private static string ComputeRootKeyIntegrity(RootKey key)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "root-key-v1",
            key.KeyId.ToString("D"),
            key.CreatedUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FormatIdentity(key.RootIdentity),
            key.Secret))));

    private static string ComputeHmac(string material, string secret)
    {
        using var hmac = new HMACSHA256(Convert.FromHexString(secret));
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private static string FormatIdentity(ScratchIdentity? identity)
        => identity is null
            ? "none"
            : string.Join(
                ":",
                identity.Inode,
                identity.DeviceMajor,
                identity.DeviceMinor,
                identity.MountId,
                identity.BirthSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                identity.BirthNanoseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");

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

    private static void ReclaimPinned(SafeFileHandle pinned)
    {
        NpgsqlBackupFileSystem.Checkpoint("scratch-before-reclaim");
        NpgsqlBackupFileSystem.Checkpoint("scratch-after-pinned-unlink-check");
        var node = ReadNodeInfo(pinned);
        if (!node.IsRegularFile)
        {
            return;
        }

        var descriptor = Open(HandlePath(pinned), OpenWriteOnly | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not open the pinned scratch payload for reclamation.");
        }

        using var writable = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (ReadIdentity(writable) != ReadIdentity(pinned))
        {
            throw new InvalidDataException("The pinned scratch payload changed identity before reclamation.");
        }

        if (Ftruncate(descriptor, 0) != 0)
        {
            throw NativeFailure("Could not truncate the pinned scratch payload.");
        }

        NpgsqlBackupFileSystem.FlushHandle(writable, "flush-scratch-reclaimed-payload");
        NpgsqlBackupFileSystem.Checkpoint("scratch-after-reclaim");
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
        bool allowMissingDirectory,
        string rootPath)
    {
        if (!string.Equals(claim.TrashName, trashName, StringComparison.Ordinal)
            || (!allowMissingDirectory && visibleIdentity is null)
            || (visibleIdentity is not null && claim.DirectoryIdentity != visibleIdentity))
        {
            throw new InvalidDataException("A scratch claim does not bind the exact tombstone generation.");
        }

        var key = LoadRootKey(rootPath, claim.KeyId);
        if (claim.ClaimMarkerIdentity is null
            || claim.Proof is not { Length: 64 }
            || !FixedTimeEqualsHex(
                claim.Proof,
                ComputeScratchClaimProof(claim with { Proof = string.Empty }, key.Secret)))
        {
            throw new InvalidDataException("A scratch claim proof is invalid.");
        }

        ValidateOwner(
            new ScratchOwnerProof(
                claim.Owner,
                claim.Owner.MarkerIdentity ?? throw new InvalidDataException("Scratch owner marker identity is missing.")),
            claim.DirectoryIdentity,
            purpose,
            trashName,
            rootPath);
    }

    private static bool SameClaim(ScratchClaim signed, ScratchClaim unsigned)
        => signed.Owner == unsigned.Owner
            && signed.DirectoryIdentity == unsigned.DirectoryIdentity
            && string.Equals(signed.TrashName, unsigned.TrashName, StringComparison.Ordinal)
            && signed.KeyId == unsigned.KeyId;

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

    [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Ftruncate(int fileDescriptor, long length);

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
        ScratchIdentity? MarkerIdentity,
        Guid KeyId,
        string Nonce,
        string Proof);

    private sealed record ScratchOwnerProof(ScratchOwner Owner, ScratchIdentity? MarkerIdentity);

    private sealed record ScratchClaim(
        ScratchOwner Owner,
        ScratchIdentity DirectoryIdentity,
        string TrashName,
        Guid KeyId,
        ScratchIdentity? ClaimMarkerIdentity,
        string Proof);

    private sealed record ScratchEntryClaim(
        string SourceName,
        string RetainedName,
        ScratchIdentity TargetIdentity,
        ScratchIdentity ParentIdentity,
        Guid KeyId,
        ScratchIdentity? ClaimMarkerIdentity,
        string Proof);

    private sealed record InitializationReceipt(
        string StagingName,
        string Purpose,
        Guid OwnerId,
        ScratchIdentity DirectoryIdentity,
        Guid KeyId,
        ScratchIdentity MarkerIdentity,
        string Proof);

    private sealed record RootKey(
        Guid KeyId,
        DateTime CreatedUtc,
        ScratchIdentity? RootIdentity,
        string Secret,
        string Integrity);

    private sealed record ScratchNodeInfo(uint LinkCount, bool IsDirectory, bool IsRegularFile);

    private sealed record ScratchIdentity(
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor,
        ulong MountId,
        long? BirthSeconds,
        uint? BirthNanoseconds);

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
#pragma warning restore SA1202, SA1204, SA1600, CA1416, CA2000
