using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    private const string InitializationReceiptName = ".jellyfin-npgsql-init-receipt.json";
    private const string QuarantineSuffix = ".quarantine";
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
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsShareAll = 0x00000007;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsDispositionDelete = 0x00000001;
    private const uint WindowsDispositionPosixSemantics = 0x00000002;
    private const uint WindowsDispositionIgnoreReadonly = 0x00000010;
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
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsOwnerOnlyDirectory(parent);
        }
        else
        {
            File.SetUnixFileMode(parent, OwnerDirectoryMode);
        }

        MaintainRetention(parent);
        var rootKey = LoadOrCreateRootKey(parent);

        var ownerId = Guid.NewGuid();
        var generationName = purpose + "-" + ownerId.ToString("N");
        var stagingName = ".init-" + generationName;
        var directory = Path.Combine(parent, generationName);
        var stagingDirectory = Path.Combine(parent, stagingName);
        Directory.CreateDirectory(stagingDirectory);
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsOwnerOnlyDirectory(stagingDirectory);
        }
        else
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
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsOwnerOnlyFile(Path.Combine(stagingDirectory, OwnerMarker));
            }

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

        if (directoryHandle is not null)
        {
            CompactInitializationReceipt(parent, directory, ownerId);
        }

        EnsureRetentionCapacity(parent);

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
            EnsureRetentionCapacity(parentPath);
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
        EnsureRetentionCapacity(parentPath);
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

        if (OperatingSystem.IsLinux())
        {
            CompactPublishedInitializationReceipts(parent, purpose);
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
        MaintainRetention(parent);
    }

    private static void CompactInitializationReceipt(string rootPath, string generationPath, Guid ownerId)
    {
        using var root = OpenDirectoryHandle(rootPath);
        using var generation = OpenDirectoryHandle(generationPath);
        var proof = ReadOwnerProof(generation);
        if (proof.Owner.OwnerId != ownerId
            || proof.Owner.DirectoryIdentity != ReadIdentity(generation))
        {
            throw new InvalidDataException("The published scratch generation does not match its initialization receipt.");
        }

        var receiptName = InitializationReceiptPrefix + ownerId.ToString("N") + RootKeySuffix;
        var receipt = ReadInitializationReceipt(
            root,
            ".init-" + proof.Owner.GenerationName,
            proof.Owner.Purpose,
            ownerId,
            ReadIdentity(generation));
        if (receipt is null)
        {
            throw new InvalidDataException("The published scratch generation has no authentic initialization receipt.");
        }

        RenamePinnedBetweenNoReplace(
            root,
            receiptName,
            generation,
            InitializationReceiptName,
            receipt.MarkerIdentity,
            "Could not bind scratch initialization evidence to its generation.");
        NpgsqlBackupFileSystem.FlushHandle(generation, "flush-scratch-init-receipt-generation");
        NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-init-receipt-root");
        NpgsqlBackupFileSystem.Checkpoint("scratch-after-init-receipt-compaction");
    }

    private static void CompactPublishedInitializationReceipts(string rootPath, string purpose)
    {
        using var root = OpenDirectoryHandle(rootPath);
        var claimedByOwner = new Dictionary<Guid, List<string>>();
        foreach (var claimedPath in Directory.EnumerateDirectories(HandlePath(root), ".trash-*", SearchOption.TopDirectoryOnly))
        {
            var claimedName = Path.GetFileName(claimedPath);
            const int prefixLength = 7;
            if (claimedName.Length < prefixLength + 32
                || !Guid.TryParseExact(claimedName.Substring(prefixLength, 32), "N", out var ownerId))
            {
                continue;
            }

            if (!claimedByOwner.TryGetValue(ownerId, out var names))
            {
                names = [];
                claimedByOwner.Add(ownerId, names);
            }

            names.Add(claimedName);
        }

        foreach (var path in Directory.EnumerateFiles(HandlePath(root), InitializationReceiptPrefix + "*" + RootKeySuffix, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var receiptName = Path.GetFileName(path);
                var receipt = ReadInitializationReceipt(root, receiptName);
                if (receipt is null || !string.Equals(receipt.Purpose, purpose, StringComparison.Ordinal))
                {
                    continue;
                }

                var generationName = receipt.StagingName[".init-".Length..];
                var candidates = new[] { generationName }
                    .Concat(claimedByOwner.GetValueOrDefault(receipt.OwnerId) ?? []);
                foreach (var candidate in candidates)
                {
                    try
                    {
                        using var generation = OpenDirectoryAt(root, candidate!);
                        var proof = ReadOwnerProof(generation);
                        if (proof.Owner.OwnerId != receipt.OwnerId
                            || proof.Owner.DirectoryIdentity != receipt.DirectoryIdentity
                            || ReadIdentity(generation) != receipt.DirectoryIdentity
                            || !IsExpectedOwner(proof, purpose, rootPath))
                        {
                            continue;
                        }

                        RenamePinnedBetweenNoReplace(
                            root,
                            receiptName,
                            generation,
                            InitializationReceiptName,
                            receipt.MarkerIdentity,
                            "Could not compact scratch initialization evidence.");
                        NpgsqlBackupFileSystem.FlushHandle(generation, "flush-scratch-init-receipt-generation");
                        NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-init-receipt-root");
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                        // Try the next live or claimed basename for this receipt.
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A malformed, raced, or still-unpublished receipt is retained fail closed.
            }
        }
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
                    || ReadInitializationReceipt(parent, name, purpose, ownerId, identity) is not { } receipt)
                {
                    continue;
                }

                var claimedName = ".trash-uninitialized-" + ownerId.ToString("N") + "-" + Guid.NewGuid().ToString("N");
                WriteUninitializedClaim(parent, receipt, identity, claimedName);
                RenameNoReplace(parent, name, claimedName, "Could not claim uninitialized scratch generation.");
                using var claimed = OpenDirectoryAt(parent, claimedName);
                if (ReadIdentity(claimed) != identity || Directory.EnumerateFileSystemEntries(HandlePath(claimed)).Any())
                {
                    RestoreUnexpectedClaim(parent, claimedName, name);
                    continue;
                }

                RenamePinnedBetweenNoReplace(
                    parent,
                    InitializationReceiptPrefix + ownerId.ToString("N") + RootKeySuffix,
                    claimed,
                    InitializationReceiptName,
                    receipt.MarkerIdentity,
                    "Could not bind initialization evidence to its claimed tombstone.");
                NpgsqlBackupFileSystem.FlushHandle(claimed, "flush-scratch-uninitialized-generation");
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

    private static void WriteUninitializedClaim(
        SafeFileHandle root,
        InitializationReceipt receipt,
        ScratchIdentity directoryIdentity,
        string trashName)
    {
        var name = trashName + ".uninitialized-claim";
        var descriptor = OpenAt(
            root.DangerousGetHandle().ToInt32(),
            name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not create an authenticated uninitialized-generation claim.");
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Write);
        var key = LoadRootKey(ResolveHandlePath(root), receipt.KeyId);
        var claim = new ScratchUninitializedClaim(
            receipt,
            directoryIdentity,
            trashName,
            receipt.KeyId,
            ReadIdentity(stream.SafeFileHandle),
            string.Empty);
        claim = claim with { Proof = ComputeUninitializedClaimProof(claim, key.Secret) };
        JsonSerializer.Serialize(stream, claim);
        stream.Flush(flushToDisk: true);
        NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-uninitialized-claim-parent");
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
                    && !string.Equals(name, InitializationReceiptName, StringComparison.Ordinal)
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
        using var original = OpenWindowsObject(directory);
        var originalIdentity = ReadWindowsIdentity(original);
        Directory.Move(directory, trash);
        using var claimed = OpenWindowsObject(trash);
        if (ReadWindowsIdentity(claimed) != originalIdentity)
        {
            throw new InvalidDataException("The claimed Windows scratch generation changed identity; it was retained.");
        }

        DeleteWindowsContents(trash);
        SetWindowsDeleteDisposition(claimed);
        NpgsqlBackupFileSystem.Checkpoint("scratch-after-delete-root");
        EnsureRetentionCapacity(parent);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteWindowsContents(string directory)
    {
        foreach (var source in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            using var original = OpenWindowsObject(source);
            var originalIdentity = ReadWindowsIdentity(original);
            var retained = Path.Combine(directory, ".retained-" + Guid.NewGuid().ToString("N"));
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
            {
                Directory.Move(source, retained);
            }
            else
            {
                File.Move(source, retained);
            }

            using var claimed = OpenWindowsObject(retained);
            if (ReadWindowsIdentity(claimed) != originalIdentity)
            {
                throw new InvalidDataException("A claimed Windows scratch entry changed identity; it was retained.");
            }

            var attributes = File.GetAttributes(retained);
            if ((attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteWindowsContents(retained);
            }
            else if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            {
                RandomAccess.SetLength(claimed, 0);
                NpgsqlBackupFileSystem.FlushHandle(claimed, "flush-scratch-reclaimed-payload");
            }

            NpgsqlBackupFileSystem.Checkpoint("scratch-before-child-unlink");
            SetWindowsDeleteDisposition(claimed);
            NpgsqlBackupFileSystem.Checkpoint("scratch-after-child-delete");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenWindowsObject(string path)
    {
        var handle = CreateFileWindows(
            path,
            WindowsDelete | WindowsGenericRead | WindowsGenericWrite,
            WindowsShareAll,
            0,
            WindowsOpenExisting,
            WindowsBackupSemantics | WindowsOpenReparsePoint,
            0);
        return !handle.IsInvalid
            ? handle
            : throw new IOException("A Windows scratch object could not be pinned for safe reclamation.", new Win32Exception());
    }

    [SupportedOSPlatform("windows")]
    private static WindowsFileIdentity ReadWindowsIdentity(SafeFileHandle handle)
    {
        var info = new WindowsFileIdInfo { FileId = new byte[16] };
        if (!GetFileInformationByHandleEx(handle, 18, ref info, Marshal.SizeOf<WindowsFileIdInfo>()))
        {
            throw new IOException("A Windows scratch object could not be identified.", new Win32Exception());
        }

        return new WindowsFileIdentity(info.VolumeSerialNumber, Convert.ToHexString(info.FileId));
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsDeleteDisposition(SafeFileHandle handle)
    {
        var disposition = new WindowsFileDispositionInfoEx
        {
            Flags = WindowsDispositionDelete | WindowsDispositionPosixSemantics | WindowsDispositionIgnoreReadonly,
        };
        if (!SetFileInformationByHandle(handle, 21, ref disposition, Marshal.SizeOf<WindowsFileDispositionInfoEx>()))
        {
            throw new IOException("A pinned Windows scratch object could not be reclaimed safely.", new Win32Exception());
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwnerOnlyDirectory(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);

        var verified = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!verified.AreAccessRulesProtected || !HasWindowsOwner(verified, owner))
        {
            throw new UnauthorizedAccessException("The PostgreSQL scratch directory could not be restricted to its owner.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwnerOnlyFile(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);

        var verified = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!verified.AreAccessRulesProtected || !HasWindowsOwner(verified, owner))
        {
            throw new UnauthorizedAccessException("A PostgreSQL scratch file could not be restricted to its owner.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyFile(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (!security.AreAccessRulesProtected
            || !HasWindowsOwner(security, owner)
            || rules.Any(rule => !rule.IdentityReference.Equals(owner) || rule.AccessControlType != AccessControlType.Allow))
        {
            throw new UnauthorizedAccessException("A PostgreSQL scratch key is not restricted to its owner.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasWindowsOwner(ObjectSecurity security, SecurityIdentifier owner)
        => security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier actual
            && actual.Equals(owner);

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

    private static void RenameBetweenNoReplace(
        SafeFileHandle sourceParent,
        string source,
        SafeFileHandle destinationParent,
        string destination,
        string message)
    {
        const uint renameNoReplace = 1;
        if (RenameAt2Native(
                sourceParent.DangerousGetHandle().ToInt32(),
                source,
                destinationParent.DangerousGetHandle().ToInt32(),
                destination,
                renameNoReplace) == 0)
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

    private static void RenamePinnedBetweenNoReplace(
        SafeFileHandle sourceParent,
        string source,
        SafeFileHandle destinationParent,
        string destination,
        ScratchIdentity expectedIdentity,
        string message)
    {
        RenameBetweenNoReplace(sourceParent, source, destinationParent, destination, message);
        using var moved = OpenAnyAt(destinationParent, destination);
        if (ReadIdentity(moved) == expectedIdentity)
        {
            return;
        }

        const uint renameNoReplace = 1;
        _ = RenameAt2Native(
            destinationParent.DangerousGetHandle().ToInt32(),
            destination,
            sourceParent.DangerousGetHandle().ToInt32(),
            source,
            renameNoReplace);
        throw new InvalidDataException("Scratch initialization evidence changed identity while it was being compacted; no object was deleted.");
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
        var name = RootKeyPrefix + key.KeyId.ToString("N") + RootKeySuffix;
        var temporaryName = ".key-new-" + key.KeyId.ToString("N") + RootKeySuffix;
        if (OperatingSystem.IsWindows())
        {
            var temporaryPath = Path.Combine(rootPath, temporaryName);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                EnsureWindowsOwnerOnlyFile(temporaryPath);
                NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-temp-create");
                JsonSerializer.Serialize(stream, key);
                stream.Flush(flushToDisk: true);
            }

            NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-temp-flush");
            File.Move(temporaryPath, Path.Combine(rootPath, name), overwrite: false);
            NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-publish");
            NpgsqlBackupFileSystem.FlushDirectory(rootPath, "flush-scratch-root-key-parent");
            return;
        }

        using var root = OpenDirectoryHandle(rootPath);
        var descriptor = OpenAt(
            root.DangerousGetHandle().ToInt32(),
            temporaryName,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            384);
        if (descriptor < 0)
        {
            throw NativeFailure("Could not create the scratch root proof key.");
        }

        using (var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true))
        using (var stream = new FileStream(handle, FileAccess.Write))
        {
            NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-temp-create");
            JsonSerializer.Serialize(stream, key);
            stream.Flush(flushToDisk: true);
        }

        NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-temp-flush");
        RenameNoReplace(root, temporaryName, name, "Could not publish the scratch root proof key.");
        NpgsqlBackupFileSystem.Checkpoint("scratch-root-key-after-publish");
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
                    VerifyWindowsOwnerOnlyFile(path);
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
            var key = TryLoadRootKey(root, Path.GetFileName(path));
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static RootKey? TryLoadRootKey(SafeFileHandle root, string name)
    {
        try
        {
            using var handle = OpenAnyAt(root, name);
            var node = ReadNodeInfo(handle);
            if (!node.IsRegularFile
                || node.LinkCount != 1
                || File.GetUnixFileMode(HandlePath(handle)) != OwnerFileMode)
            {
                return null;
            }

            var descriptor = Open(HandlePath(handle), OpenReadOnly | OpenCloseOnExec);
            if (descriptor < 0)
            {
                return null;
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
                return null;
            }

            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // A single torn or malformed append-only key record never prevents
            // another valid record from authorizing recovery.
            return null;
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

    private static InitializationReceipt? ReadInitializationReceipt(SafeFileHandle root, string receiptName)
    {
        try
        {
            using var marker = OpenAnyAt(root, receiptName);
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
                || receipt.OwnerId == Guid.Empty
                || !string.Equals(receiptName, InitializationReceiptPrefix + receipt.OwnerId.ToString("N") + RootKeySuffix, StringComparison.Ordinal)
                || !string.Equals(receipt.StagingName, ".init-" + receipt.Purpose + "-" + receipt.OwnerId.ToString("N"), StringComparison.Ordinal)
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
        var (entries, bytes) = MeasureRetention(rootPath);
        if (entries > MaximumRetainedEntries || bytes > MaximumRetainedBytes)
        {
            throw new IOException(
                $"Scratch retention exceeded its fail-closed limit ({MaximumRetainedEntries} entries or {MaximumRetainedBytes} bytes). "
                + "No retained or unknown entry was deleted; an operator must inspect the owner-private scratch root.");
        }
    }

    private static (int Entries, long Bytes) MeasureRetention(string rootPath)
    {
        long bytes = 0;
        var entries = 0;
        var pending = new Stack<string>(Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsRetainedRootEntry));
        while (pending.Count != 0)
        {
            var path = pending.Pop();
            try
            {
                entries = checked(entries + 1);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    bytes = checked(bytes + new FileInfo(path).Length);
                }
                else if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
                    {
                        pending.Push(child);
                    }
                }

                if (entries > MaximumRetainedEntries || bytes > MaximumRetainedBytes)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
            {
                throw new IOException("Scratch retention could not be measured safely; new scratch work is disabled until an operator inspects the root.", exception);
            }
        }

        return (entries, bytes);
    }

    private static void MaintainRetention(string rootPath)
    {
        var usage = MeasureRetention(rootPath);
        if (OperatingSystem.IsLinux()
            && (usage.Entries > MaximumRetainedEntries * 3 / 4
                || usage.Bytes > MaximumRetainedBytes * 3 / 4))
        {
            QuarantineTerminalGenerations(rootPath);
        }

        EnsureRetentionCapacity(rootPath);
    }

    private static void QuarantineTerminalGenerations(string rootPath)
    {
        var quarantinePath = rootPath + QuarantineSuffix;
        Directory.CreateDirectory(quarantinePath);
        using var root = OpenDirectoryHandle(rootPath);
        using var quarantine = OpenDirectoryHandle(quarantinePath);
        File.SetUnixFileMode(HandlePath(quarantine), OwnerDirectoryMode);
        EnsureRenameNoReplaceSupported(root);
        foreach (var candidate in Directory.EnumerateDirectories(HandlePath(root), ".trash-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = Path.GetFileName(candidate);
                using var generation = OpenDirectoryAt(root, name);
                var identity = ReadIdentity(generation);
                ScratchClaim claim;
                try
                {
                    var owner = ReadOwnerProof(generation);
                    ValidateOwner(owner, identity, owner.Owner.Purpose, name, rootPath);
                    claim = ReadClaimSidecar(root, name);
                    ValidateClaim(claim, identity, owner.Owner.Purpose, name, allowMissingDirectory: false, rootPath);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                if (!IsTerminalGeneration(generation)
                    || ReadIdentityForNameIfPresent(root, name) != identity)
                {
                    continue;
                }

                var quarantineName = name + "-" + Guid.NewGuid().ToString("N");
                RenamePinnedBetweenNoReplace(
                    root,
                    name,
                    quarantine,
                    quarantineName,
                    identity,
                    "Could not quarantine a terminal scratch generation.");

                var sidecarName = ClaimSidecarName(name);
                try
                {
                    RenamePinnedBetweenNoReplace(
                        root,
                        sidecarName,
                        quarantine,
                        quarantineName + ".claim",
                        claim.ClaimMarkerIdentity!,
                        "Could not quarantine a terminal scratch claim.");
                }
                catch (FileNotFoundException)
                {
                    // A crash after the generation move leaves no authority to
                    // mutate it; the authenticated generation remains quarantined.
                }

                NpgsqlBackupFileSystem.FlushHandle(quarantine, "flush-scratch-quarantine");
                NpgsqlBackupFileSystem.FlushHandle(root, "flush-scratch-quarantine-root");
                _ = claim;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Quarantine only authenticated terminal generations. Unknown or
                // concurrently replaced names remain exactly where they were.
            }
        }
    }

    private static bool IsTerminalGeneration(SafeFileHandle generation)
    {
        foreach (var path in Directory.EnumerateFiles(HandlePath(generation), "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(".retained-", StringComparison.Ordinal))
            {
                if (new FileInfo(path).Length != 0)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(name, OwnerMarker, StringComparison.Ordinal)
                && !string.Equals(name, InitializationReceiptName, StringComparison.Ordinal)
                && !name.StartsWith(".entry-claim-", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRetainedRootEntry(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".trash-", StringComparison.Ordinal)
            || name.StartsWith(".init-", StringComparison.Ordinal)
            || name.StartsWith(".delete-claim-", StringComparison.Ordinal)
            || name.StartsWith(".claim-new-", StringComparison.Ordinal)
            || name.StartsWith(".key-new-", StringComparison.Ordinal)
            || name.StartsWith(".retained-", StringComparison.Ordinal)
            || name.StartsWith(".entry-claim-", StringComparison.Ordinal)
            || name.StartsWith(InitializationReceiptPrefix, StringComparison.Ordinal);
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

    private static string ComputeUninitializedClaimProof(ScratchUninitializedClaim claim, string secret)
        => ComputeHmac(
            string.Join(
                "\n",
                "uninitialized-generation-claim-v1",
                claim.Receipt.OwnerId.ToString("D"),
                claim.Receipt.StagingName,
                claim.TrashName,
                FormatIdentity(claim.DirectoryIdentity),
                claim.KeyId.ToString("D"),
                FormatIdentity(claim.ClaimMarkerIdentity),
                claim.Receipt.Proof),
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileWindows(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        ref WindowsFileIdInfo fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref WindowsFileDispositionInfoEx fileInformation,
        int bufferSize);
#pragma warning restore CA2101, SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileIdInfo
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileDispositionInfoEx
    {
        public uint Flags;
    }

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

    private sealed record ScratchUninitializedClaim(
        InitializationReceipt Receipt,
        ScratchIdentity DirectoryIdentity,
        string TrashName,
        Guid KeyId,
        ScratchIdentity? ClaimMarkerIdentity,
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

    private sealed record WindowsFileIdentity(ulong VolumeSerialNumber, string FileId);

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
#pragma warning restore SA1202, SA1204, SA1600, CA1416, CA2000
