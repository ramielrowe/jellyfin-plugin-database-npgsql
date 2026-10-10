using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>Stages an external Jellyfin publish through contained Linux file descriptors.</summary>
public static class SafeHostPublishStager
{
    private const int MaximumEntries = 100_000;
    private const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    private const string MarkerName = ".jellyfin-npgsql-owned-artifacts";
    private const string MarkerValue = "Owned by Jellyfin.Plugin.Database.Npgsql.TestHarness\n";
    private const uint DirectoryMode = 0x1C0; // 0700
    private const uint FileMode = 0x180; // 0600

    public static async Task StageAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken,
        Action? inputVerifiedForTest = null,
        Action? openingTemporaryDirectoryForTest = null,
        Action? publishingTemporaryDirectoryForTest = null)
    {
        RequireLinux();
        ValidateExpectedHash(expectedSha256);
        var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var ownedRootPath = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The staged host destination has no parent.");
        var destinationName = Path.GetFileName(destinationPath);
        if (destinationName is not ("host-publish-input" or "unsupported-host-publish-input"))
        {
            throw new InvalidOperationException("The staged host destination name is not allowlisted.");
        }

        EnsurePathsDoNotOverlap(sourcePath, destinationPath);
        using var sourceRoot = OpenAbsoluteDirectory(sourcePath, "Jellyfin host publish root");
        using var ownedRoot = OpenAbsoluteDirectory(ownedRootPath, "owned release-verification root");
        await VerifyMarkerAsync(ownedRoot, cancellationToken).ConfigureAwait(false);
        EnsureMissing(ownedRoot, destinationName);

        var sourceManifest = await ReadManifestAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        EnsureHash(expectedSha256, sourceManifest.Hash, "Jellyfin host publish");
        if (!sourceManifest.Files.Any(file => file.RelativePath == "jellyfin.dll"))
        {
            throw new InvalidOperationException("The configured Jellyfin publish directory does not contain a regular jellyfin.dll file.");
        }

        inputVerifiedForTest?.Invoke();
        var temporaryName = ".host-publish-stage-" + Guid.NewGuid().ToString("N");
        SafeFileHandle? temporaryRoot = null;
        FileIdentity? temporaryIdentity = null;
        var temporaryDescriptorValidated = false;
        string? cleanupName = null;
        try
        {
            CreateDirectoryAt(ownedRoot, temporaryName);
            cleanupName = temporaryName;
            var temporaryStatus = InspectEntryAt(ownedRoot, temporaryName);
            RequireDirectory(temporaryStatus, temporaryName);
            temporaryIdentity = temporaryStatus.Identity;
            openingTemporaryDirectoryForTest?.Invoke();
            temporaryRoot = OpenDirectoryAt(ownedRoot, temporaryName);
            RequireSameDirectory(InspectDescriptor(temporaryRoot), temporaryIdentity.Value, temporaryName);
            temporaryDescriptorValidated = true;
            SetMode(temporaryRoot, DirectoryMode);
            foreach (var directory in sourceManifest.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreateDirectoryPathAt(temporaryRoot, directory);
            }

            var copyBudget = new ReadBudget();
            foreach (var file in sourceManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = OpenRegularFileAt(sourceRoot, file);
                using var output = CreateRegularFileAt(temporaryRoot, file.RelativePath);
                await ReadExactAsync(
                    input,
                    file,
                    copyBudget,
                    cancellationToken,
                    memory => output.WriteAsync(memory, cancellationToken).AsTask()).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var outputStatus = InspectDescriptor(output.SafeFileHandle);
                if (outputStatus.Kind != NodeKind.RegularFile || outputStatus.Size != file.Length)
                {
                    throw new InvalidOperationException($"Staged Jellyfin host publish file has an unexpected size: {file.RelativePath}");
                }

                SetMode(output.SafeFileHandle, FileMode);
            }

            var stagedManifest = await ReadManifestAsync(temporaryRoot, cancellationToken).ConfigureAwait(false);
            if (!sourceManifest.Directories.SequenceEqual(stagedManifest.Directories, StringComparer.Ordinal)
                || !sourceManifest.Files.Select(file => (file.RelativePath, file.Length))
                    .SequenceEqual(stagedManifest.Files.Select(file => (file.RelativePath, file.Length))))
            {
                throw new InvalidOperationException("The staged Jellyfin host publish shape differs from its validated input.");
            }

            EnsureHash(expectedSha256, stagedManifest.Hash, "Staged Jellyfin host publish");
            publishingTemporaryDirectoryForTest?.Invoke();
            RequireNamedDirectoryMatchesDescriptor(
                ownedRoot,
                temporaryName,
                temporaryRoot,
                temporaryIdentity.Value,
                "staged Jellyfin host publish temporary directory");
            EnsureMissing(ownedRoot, destinationName);
            RenameNoReplace(ownedRoot, temporaryName, destinationName);
            cleanupName = destinationName;
            using var publishedRoot = OpenDirectoryAt(ownedRoot, destinationName);
            RequireNamedDirectoryMatchesDescriptor(
                ownedRoot,
                destinationName,
                publishedRoot,
                temporaryIdentity.Value,
                "published Jellyfin host directory");
            cleanupName = null;
        }
        finally
        {
            if (cleanupName is not null && temporaryIdentity is not null)
            {
                var cleanupStatus = TryInspectEntryAt(ownedRoot, cleanupName);
                if (temporaryRoot is not null
                    && temporaryDescriptorValidated
                    && InspectDescriptor(temporaryRoot).Identity == temporaryIdentity.Value
                    && cleanupStatus is { Kind: NodeKind.Directory }
                    && cleanupStatus.Value.Identity == temporaryIdentity.Value)
                {
                    DeleteContentsWithoutFollowingLinks(temporaryRoot);
                }

                TryRemoveExactDirectory(ownedRoot, cleanupName, temporaryIdentity.Value);
            }

            temporaryRoot?.Dispose();
        }
    }

    public static async Task<string> HashDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        RequireLinux();
        using var root = OpenAbsoluteDirectory(Path.GetFullPath(directory), "Jellyfin host publish root");
        return (await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false)).Hash;
    }

    private static async Task VerifyMarkerAsync(SafeFileHandle root, CancellationToken cancellationToken)
    {
        var status = InspectEntryAt(root, MarkerName);
        RequireRegular(status, MarkerName);
        var expected = Encoding.UTF8.GetBytes(MarkerValue);
        if (status.Size != expected.Length)
        {
            throw new InvalidOperationException("The release-verification root does not contain the exact ownership marker.");
        }

        var entry = new FileEntry(MarkerName, status.Size, status.Identity);
        using var stream = OpenRegularFileAt(root, entry);
        using var contents = new MemoryStream(expected.Length);
        await ReadExactAsync(
            stream,
            entry,
            new ReadBudget(),
            cancellationToken,
            memory => contents.WriteAsync(memory, cancellationToken).AsTask()).ConfigureAwait(false);
        if (!contents.GetBuffer().AsSpan(0, checked((int)contents.Length)).SequenceEqual(expected))
        {
            throw new InvalidOperationException("The release-verification root does not contain the exact ownership marker.");
        }
    }

    private static async Task<DirectoryManifest> ReadManifestAsync(SafeFileHandle root, CancellationToken cancellationToken)
    {
        RequireDirectory(InspectDescriptor(root), "Jellyfin host publish root");
        var directories = new List<string>();
        var files = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        long declaredBytes = 0;
        while (pending.Count > 0)
        {
            var relativeDirectory = pending.Pop();
            using var directory = relativeDirectory.Length == 0 ? Duplicate(root) : OpenDirectoryAt(root, relativeDirectory);
            foreach (var name in EnumerateNames(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                var status = InspectEntryAt(directory, name);
                switch (status.Kind)
                {
                    case NodeKind.Directory:
                        directories.Add(relativePath);
                        pending.Push(relativePath);
                        break;
                    case NodeKind.RegularFile:
                        RejectSparse(status, relativePath);
                        declaredBytes = checked(declaredBytes + status.Size);
                        files.Add(new FileEntry(relativePath, status.Size, status.Identity));
                        break;
                    case NodeKind.SymbolicLink:
                        throw new InvalidOperationException($"The Jellyfin host publish contains a symbolic link: {relativePath}");
                    default:
                        throw new InvalidOperationException($"The Jellyfin host publish contains a special filesystem node: {relativePath}");
                }

                if (directories.Count + files.Count > MaximumEntries || declaredBytes > MaximumBytes)
                {
                    throw new InvalidOperationException($"The Jellyfin host publish exceeds the staging limit of {MaximumEntries} entries or {MaximumBytes} bytes.");
                }
            }
        }

        directories.Sort(StringComparer.Ordinal);
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        using var digest = new HostPublishDigest(files.Count);
        var budget = new ReadBudget();
        foreach (var file in files)
        {
            digest.BeginFile(file.RelativePath, file.Length);
            using var stream = OpenRegularFileAt(root, file);
            await ReadExactAsync(
                stream,
                file,
                budget,
                cancellationToken,
                memory =>
                {
                    digest.AppendFileContent(memory.Span);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }

        return new DirectoryManifest(directories, files, digest.Complete());
    }

    private static async Task ReadExactAsync(
        FileStream stream,
        FileEntry expected,
        ReadBudget budget,
        CancellationToken cancellationToken,
        Func<ReadOnlyMemory<byte>, Task> consume)
    {
        RequireSameFile(InspectDescriptor(stream.SafeFileHandle), expected);
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = expected.Length;
            while (remaining > 0)
            {
                var count = await stream.ReadAsync(rented.AsMemory(0, (int)Math.Min(rented.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new InvalidOperationException($"Jellyfin host publish file became shorter while it was read: {expected.RelativePath}");
                }

                remaining -= count;
                budget.Add(count);
                await consume(rented.AsMemory(0, count)).ConfigureAwait(false);
            }

            if (await stream.ReadAsync(rented.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException($"Jellyfin host publish file grew while it was read: {expected.RelativePath}");
            }

            RequireSameFile(InspectDescriptor(stream.SafeFileHandle), expected);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static FileStream OpenRegularFileAt(SafeFileHandle root, FileEntry expected)
    {
        var handle = OpenAt2(root, expected.RelativePath, Native.OpenReadOnly | Native.OpenCloseOnExec, 0);
        try
        {
            RequireSameFile(InspectDescriptor(handle), expected);
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream CreateRegularFileAt(SafeFileHandle root, string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : relativePath[..separator];
        var name = separator < 0 ? relativePath : relativePath[(separator + 1)..];
        using var parent = parentPath.Length == 0 ? Duplicate(root) : OpenDirectoryAt(root, parentPath);
        var handle = OpenAt2(
            parent,
            name,
            Native.OpenWriteOnly | Native.OpenCreate | Native.OpenExclusive | Native.OpenNoFollow | Native.OpenCloseOnExec,
            FileMode);
        return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false);
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string path, string description)
    {
        try
        {
            var handle = OpenAt2(null, path, Native.OpenReadOnly | Native.OpenDirectory | Native.OpenCloseOnExec, 0, beneath: false);
            RequireDirectory(InspectDescriptor(handle), description);
            return handle;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"The {description} must be a real directory with no symbolic-link components.", ex);
        }
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle root, string path)
    {
        var handle = OpenAt2(root, path, Native.OpenReadOnly | Native.OpenDirectory | Native.OpenCloseOnExec, 0);
        try
        {
            RequireDirectory(InspectDescriptor(handle), path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAt2(SafeFileHandle? root, string path, ulong flags, uint mode, bool beneath = true)
    {
        var how = new Native.OpenHow
        {
            Flags = flags,
            Mode = mode,
            Resolve = Native.ResolveNoMagicLinks | Native.ResolveNoSymbolicLinks | (beneath ? Native.ResolveBeneath : 0)
        };
        var rootDescriptor = root is null ? Native.AtWorkingDirectory : checked((int)root.DangerousGetHandle());
        var descriptor = Native.OpenAt2(rootDescriptor, path, ref how, (nuint)Marshal.SizeOf<Native.OpenHow>());
        if (descriptor < 0)
        {
            throw Error($"Could not safely open '{Path.GetFileName(path)}'");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static void CreateDirectoryPathAt(SafeFileHandle root, string relativePath)
    {
        SafeFileHandle active = Duplicate(root);
        try
        {
            foreach (var component in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var status = TryInspectEntryAt(active, component);
                if (status is null)
                {
                    CreateDirectoryAt(active, component);
                }
                else
                {
                    RequireDirectory(status.Value, component);
                }

                var child = OpenDirectoryAt(active, component);
                SetMode(child, DirectoryMode);
                active.Dispose();
                active = child;
            }
        }
        finally
        {
            active.Dispose();
        }
    }

    private static void CreateDirectoryAt(SafeFileHandle parent, string name)
    {
        if (Native.MkdirAt(checked((int)parent.DangerousGetHandle()), name, DirectoryMode) != 0)
        {
            throw Error($"Could not create staged host directory '{name}'");
        }
    }

    private static void RenameNoReplace(SafeFileHandle parent, string oldName, string newName)
    {
        var descriptor = checked((int)parent.DangerousGetHandle());
        if (Native.RenameAt2(descriptor, oldName, descriptor, newName, Native.RenameNoReplace) != 0)
        {
            throw Error("Could not atomically publish the staged Jellyfin host directory");
        }
    }

    private static void DeleteContentsWithoutFollowingLinks(SafeFileHandle directory)
    {
        foreach (var name in EnumerateNames(directory))
        {
            var status = TryInspectEntryAt(directory, name);
            if (status?.Kind == NodeKind.Directory)
            {
                using var child = OpenDirectoryAt(directory, name);
                DeleteContentsWithoutFollowingLinks(child);
                UnlinkAt(directory, name, Native.AtRemoveDirectory);
            }
            else if (status is not null)
            {
                UnlinkAt(directory, name, 0);
            }
        }
    }

    private static void TryRemoveExactDirectory(SafeFileHandle parent, string name, FileIdentity expectedIdentity)
    {
        var current = TryInspectEntryAt(parent, name);
        if (current is { Kind: NodeKind.Directory } && current.Value.Identity == expectedIdentity)
        {
            UnlinkAt(parent, name, Native.AtRemoveDirectory);
        }
    }

    private static void UnlinkAt(SafeFileHandle parent, string name, int flags)
    {
        if (Native.UnlinkAt(checked((int)parent.DangerousGetHandle()), name, flags) != 0)
        {
            throw Error($"Could not remove staged host entry '{name}'");
        }
    }

    private static IReadOnlyList<string> EnumerateNames(SafeFileHandle directory)
    {
        var descriptor = Native.Duplicate(checked((int)directory.DangerousGetHandle()));
        if (descriptor < 0)
        {
            throw Error("Could not duplicate a directory descriptor");
        }

        if (Native.Seek(descriptor, 0, 0) < 0)
        {
            Native.Close(descriptor);
            throw Error("Could not rewind a directory descriptor");
        }

        var stream = Native.FileDescriptorOpenDirectory(descriptor);
        if (stream == nint.Zero)
        {
            Native.Close(descriptor);
            throw Error("Could not enumerate a directory descriptor");
        }

        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = Native.ReadDirectory(stream);
                if (entry == nint.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        throw Error("Could not enumerate a directory descriptor");
                    }

                    return names;
                }

                var name = Marshal.PtrToStringUTF8(entry + Native.DirectoryEntryNameOffset)
                    ?? throw new IOException("A directory entry had no name.");
                if (name is not ("." or ".."))
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            Native.CloseDirectory(stream);
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        var descriptor = Native.Duplicate(checked((int)handle.DangerousGetHandle()));
        if (descriptor < 0)
        {
            throw Error("Could not duplicate a filesystem descriptor");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static DescriptorStatus InspectDescriptor(SafeFileHandle handle)
    {
        if (Native.Statx(checked((int)handle.DangerousGetHandle()), string.Empty, Native.AtEmptyPath | Native.AtNoFollow, Native.StatxBasicStats, out var status) != 0)
        {
            throw Error("Could not inspect an open filesystem descriptor");
        }

        return DescriptorStatus.From(status);
    }

    private static DescriptorStatus InspectEntryAt(SafeFileHandle directory, string name)
        => TryInspectEntryAt(directory, name) ?? throw new InvalidOperationException($"Required filesystem entry is missing: {name}");

    private static DescriptorStatus? TryInspectEntryAt(SafeFileHandle directory, string name)
    {
        if (Native.Statx(checked((int)directory.DangerousGetHandle()), name, Native.AtNoFollow, Native.StatxBasicStats, out var status) == 0)
        {
            return DescriptorStatus.From(status);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == Native.NoSuchFile)
        {
            return null;
        }

        throw Error($"Could not inspect filesystem entry '{name}'", error);
    }

    private static void EnsureMissing(SafeFileHandle directory, string name)
    {
        if (TryInspectEntryAt(directory, name) is not null)
        {
            throw new InvalidOperationException("The staged Jellyfin host publish destination must not already exist.");
        }
    }

    private static void RequireDirectory(DescriptorStatus status, string description)
    {
        if (status.Kind != NodeKind.Directory)
        {
            throw new InvalidOperationException($"The {description} is not a real directory.");
        }
    }

    private static void RequireSameDirectory(DescriptorStatus status, FileIdentity expectedIdentity, string description)
    {
        RequireDirectory(status, description);
        if (status.Identity != expectedIdentity)
        {
            throw new InvalidOperationException($"The {description} changed identity while the Jellyfin host publish was staged.");
        }
    }

    private static void RequireNamedDirectoryMatchesDescriptor(
        SafeFileHandle parent,
        string name,
        SafeFileHandle descriptor,
        FileIdentity expectedIdentity,
        string description)
    {
        RequireSameDirectory(InspectDescriptor(descriptor), expectedIdentity, description);
        RequireSameDirectory(InspectEntryAt(parent, name), expectedIdentity, description);
    }

    private static void RequireRegular(DescriptorStatus status, string description)
    {
        if (status.Kind != NodeKind.RegularFile)
        {
            throw new InvalidOperationException($"The {description} is not a regular file.");
        }

        RejectSparse(status, description);
    }

    private static void RequireSameFile(DescriptorStatus status, FileEntry expected)
    {
        RequireRegular(status, expected.RelativePath);
        if (status.Size != expected.Length || status.Identity != expected.Identity)
        {
            throw new InvalidOperationException($"Jellyfin host publish file changed identity or size while it was staged: {expected.RelativePath}");
        }
    }

    private static void RejectSparse(DescriptorStatus status, string path)
    {
        if (status.Size > 0 && status.Blocks * 512 < (ulong)status.Size)
        {
            throw new InvalidOperationException($"The Jellyfin host publish contains a sparse file: {path}");
        }
    }

    private static void SetMode(SafeFileHandle handle, uint mode)
    {
        if (Native.ChangeMode(checked((int)handle.DangerousGetHandle()), mode) != 0)
        {
            throw Error("Could not normalize staged host permissions");
        }
    }

    private static void EnsurePathsDoNotOverlap(string source, string destination)
    {
        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source))
        {
            throw new InvalidOperationException("The source and staged Jellyfin host publish destination must not overlap.");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
        => candidate == root || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void ValidateExpectedHash(string expected)
    {
        if (expected.Length != 64 || expected.Any(character => !char.IsAsciiHexDigit(character)))
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

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Safe host publish staging requires Linux openat2 containment.");
        }
    }

    private static IOException Error(string message, int? error = null)
        => new(message, new Win32Exception(error ?? Marshal.GetLastPInvokeError()));

    private sealed class ReadBudget
    {
        private long _bytes;

        public void Add(int count)
        {
            _bytes = checked(_bytes + count);
            if (_bytes > MaximumBytes)
            {
                throw new InvalidOperationException($"The Jellyfin host publish exceeds the staging byte limit of {MaximumBytes}.");
            }
        }
    }

    private sealed record DirectoryManifest(IReadOnlyList<string> Directories, IReadOnlyList<FileEntry> Files, string Hash);
    private sealed record FileEntry(string RelativePath, long Length, FileIdentity Identity);
    private readonly record struct FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode);

    private readonly record struct DescriptorStatus(NodeKind Kind, long Size, ulong Blocks, FileIdentity Identity)
    {
        public static DescriptorStatus From(Native.StatxBuffer status)
        {
            const ushort TypeMask = 0xF000;
            var kind = (status.Mode & TypeMask) switch
            {
                0x4000 => NodeKind.Directory,
                0x8000 => NodeKind.RegularFile,
                0xA000 => NodeKind.SymbolicLink,
                _ => NodeKind.Special
            };
            return new DescriptorStatus(kind, checked((long)status.Size), status.Blocks, new(status.DeviceMajor, status.DeviceMinor, status.Inode));
        }
    }

    private enum NodeKind { Directory, RegularFile, SymbolicLink, Special }

    private static class Native
    {
        internal const int AtWorkingDirectory = -100;
        internal const int AtNoFollow = 0x100;
        internal const int AtRemoveDirectory = 0x200;
        internal const int AtEmptyPath = 0x1000;
        internal const int NoSuchFile = 2;
        internal const uint StatxBasicStats = 0x07ff;
        internal const uint RenameNoReplace = 1;
        internal const int DirectoryEntryNameOffset = 19;
        internal const ulong OpenReadOnly = 0;
        internal const ulong OpenWriteOnly = 1;
        internal const ulong OpenCreate = 0x40;
        internal const ulong OpenExclusive = 0x80;
        internal const ulong OpenDirectory = 0x10000;
        internal const ulong OpenNoFollow = 0x20000;
        internal const ulong OpenCloseOnExec = 0x80000;
        internal const ulong ResolveNoMagicLinks = 0x02;
        internal const ulong ResolveNoSymbolicLinks = 0x04;
        internal const ulong ResolveBeneath = 0x08;

        [StructLayout(LayoutKind.Sequential)]
        internal struct OpenHow { internal ulong Flags; internal ulong Mode; internal ulong Resolve; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StatxTimestamp { internal long Seconds; internal uint Nanoseconds; internal int Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StatxBuffer
        {
            internal uint Mask; internal uint BlockSize; internal ulong Attributes; internal uint LinkCount;
            internal uint UserId; internal uint GroupId; internal ushort Mode; internal ushort Reserved0;
            internal ulong Inode; internal ulong Size; internal ulong Blocks; internal ulong AttributesMask;
            internal StatxTimestamp AccessTime; internal StatxTimestamp BirthTime; internal StatxTimestamp ChangeTime; internal StatxTimestamp ModificationTime;
            internal uint DeviceMajor; internal uint DeviceMinor; internal uint SpecialDeviceMajor; internal uint SpecialDeviceMinor;
            internal ulong MountId; internal uint DirectIoMemoryAlign; internal uint DirectIoOffsetAlign; internal ulong Subvolume;
            internal uint AtomicWriteUnitMin; internal uint AtomicWriteUnitMax; internal uint AtomicWriteSegmentsMax; internal uint DirectIoReadOffsetAlign;
            internal ulong Reserved1; internal ulong Reserved2; internal ulong Reserved3; internal ulong Reserved4; internal ulong Reserved5;
            internal ulong Reserved6; internal ulong Reserved7; internal ulong Reserved8; internal ulong Reserved9;
        }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern long Syscall(long number, int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);

        internal static int OpenAt2(int directoryDescriptor, string path, ref OpenHow how, nuint size)
            => checked((int)Syscall(437, directoryDescriptor, path, ref how, size));

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer buffer);
        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        internal static extern int MkdirAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        internal static extern int RenameAt2(int oldDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        internal static extern int UnlinkAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        internal static extern int ChangeMode(int descriptor, uint mode);
        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        internal static extern int Duplicate(int descriptor);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int descriptor);
        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        internal static extern long Seek(int descriptor, long offset, int whence);
        [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
        internal static extern nint FileDescriptorOpenDirectory(int descriptor);
        [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
        internal static extern nint ReadDirectory(nint directory);
        [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
        internal static extern int CloseDirectory(nint directory);
    }
}
