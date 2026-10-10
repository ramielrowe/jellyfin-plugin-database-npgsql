using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Owner-only, bounded external-sort index used to validate exact relational values without
/// depending on a host database provider. Digests are only sort prefixes: equality always
/// compares the complete canonical value bytes.
/// </summary>
#pragma warning disable SA1202, SA1204, SA1600 // Internal utility members are grouped by lifecycle.
#pragma warning disable CA2000 // Writer ownership transfers directly into SetState.
internal sealed class NpgsqlExactDiskIndex : IDisposable
{
    internal const long DefaultMaximumPhysicalBytes = 8L * 1024 * 1024 * 1024;
    internal const int MaximumCanonicalKeyBytes = 1024 * 1024;
    private const int SortMemoryBytes = 16 * 1024 * 1024;
    private const int MaximumMergeReaders = 32;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly AsyncLocal<Func<ReadOnlyMemory<byte>, byte[]>?> DigestOverride = new();
    private static readonly AsyncLocal<long?> QuotaOverride = new();
    private static readonly AsyncLocal<int?> SortMemoryOverride = new();
    private readonly string root;
    private readonly long maximumPhysicalBytes;
    private readonly CancellationToken cancellationToken;
    private readonly int sortMemoryBytes;
    private readonly Dictionary<int, SetState> sets = new();
    private long physicalBytes;
    private long writes;
    private bool disposed;

    internal NpgsqlExactDiskIndex(string root, CancellationToken cancellationToken = default)
    {
        this.root = root;
        this.cancellationToken = cancellationToken;
        this.sortMemoryBytes = SortMemoryOverride.Value ?? SortMemoryBytes;
        this.maximumPhysicalBytes = QuotaOverride.Value ?? DefaultMaximumPhysicalBytes;
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static IDisposable InjectDigestForTests(Func<ReadOnlyMemory<byte>, byte[]> digest)
    {
        var previous = DigestOverride.Value;
        DigestOverride.Value = digest;
        return new CallbackScope(() => DigestOverride.Value = previous);
    }

    internal static IDisposable InjectQuotaForTests(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var previous = QuotaOverride.Value;
        QuotaOverride.Value = bytes;
        return new CallbackScope(() => QuotaOverride.Value = previous);
    }

    internal static IDisposable InjectSortMemoryForTests(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var previous = SortMemoryOverride.Value;
        SortMemoryOverride.Value = bytes;
        return new CallbackScope(() => SortMemoryOverride.Value = previous);
    }

    internal void Add(
        int setId,
        ReadOnlySpan<byte> canonicalValue,
        bool duplicatesAllowed,
        string duplicateDescription)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.cancellationToken.ThrowIfCancellationRequested();
        if (canonicalValue.Length > MaximumCanonicalKeyBytes)
        {
            throw new InvalidDataException(
                $"A restore validation key exceeds the {MaximumCanonicalKeyBytes}-byte exact-key limit.");
        }

        if (!this.sets.TryGetValue(setId, out var set))
        {
            var path = Path.Combine(this.root, $"set-{setId:D5}.unsorted");
            set = new SetState(setId, path, duplicatesAllowed, duplicateDescription, OpenWrite(path));
            this.sets.Add(setId, set);
        }
        else if (set.DuplicatesAllowed != duplicatesAllowed)
        {
            throw new InvalidOperationException("A validation set was used with inconsistent duplicate semantics.");
        }

        var value = canonicalValue.ToArray();
        var digest = DigestOverride.Value?.Invoke(value) ?? SHA256.HashData(value);
        if (digest.Length is <= 0 or > 1024)
        {
            throw new InvalidOperationException("The validation digest test seam returned an invalid length.");
        }

        var length = checked(8L + digest.Length + value.Length);
        this.Reserve(length);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, digest.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Length);
        try
        {
            NpgsqlBackupFileSystem.Checkpoint("validation-index-write");
            set.Writer.Write(header);
            set.Writer.Write(digest);
            set.Writer.Write(value);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The restore validation index could not write to its bounded disk area.", exception);
        }

        if ((++this.writes & 4095) == 0)
        {
            this.CheckActualPhysicalBytes();
        }
    }

    internal void CompleteSet(int setId)
    {
        try
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            var set = this.RequireSet(setId);
            if (set.CompletedPath is not null)
            {
                return;
            }

            set.Writer.Flush(flushToDisk: true);
            this.CheckActualPhysicalBytes();
            set.Writer.Dispose();
            var runs = this.CreateRuns(set);
            try
            {
                set.CompletedPath = this.MergeRuns(set, runs);
            }
            finally
            {
                foreach (var run in runs)
                {
                    TryDelete(run);
                }
            }
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The restore validation index exhausted or lost its bounded disk area.", exception);
        }
    }

    internal bool ContainsMissingFrom(int referenceSetId, int principalSetId)
    {
        this.CompleteSet(referenceSetId);
        this.CompleteSet(principalSetId);
        using var references = new RecordReader(this.RequireSet(referenceSetId).CompletedPath!, sorted: true, this.cancellationToken);
        using var principals = new RecordReader(this.RequireSet(principalSetId).CompletedPath!, sorted: true, this.cancellationToken);
        var reference = references.Read();
        var principal = principals.Read();
        while (reference is not null)
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            while (principal is not null && ByteArrayComparer.Instance.Compare(principal, reference) < 0)
            {
                principal = principals.Read();
            }

            if (principal is null || !principal.AsSpan().SequenceEqual(reference))
            {
                return true;
            }

            reference = references.Read();
        }

        return false;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (var set in this.sets.Values)
        {
            set.Writer.Dispose();
        }

        // ModelValidationState owns and atomically claims the marked root after this
        // index has released every file handle.
    }

    private static FileStream OpenWrite(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 131072,
            Options = FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        return new FileStream(path, options);
    }

    private List<string> CreateRuns(SetState set)
    {
        var runs = new List<string>();
        using var reader = new RecordReader(set.UnsortedPath, sorted: false, this.cancellationToken);
        while (true)
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            var records = new List<byte[]>();
            var bytes = 0;
            while (bytes < this.sortMemoryBytes && reader.Read() is { } record)
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                records.Add(record);
                bytes = checked(bytes + record.Length + 8);
            }

            if (records.Count == 0)
            {
                break;
            }

            this.cancellationToken.ThrowIfCancellationRequested();
            NpgsqlBackupFileSystem.Checkpoint("validation-index-before-sort");
            records.Sort(ByteArrayComparer.Instance);
            this.cancellationToken.ThrowIfCancellationRequested();
            var run = Path.Combine(this.root, $"run-{Guid.NewGuid():N}");
            using (var writer = OpenWrite(run))
            {
                byte[]? previous = null;
                foreach (var record in records)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    if (previous is not null && previous.AsSpan().SequenceEqual(record))
                    {
                        if (!set.DuplicatesAllowed)
                        {
                            throw new InvalidDataException($"The database backup contains a duplicate {set.DuplicateDescription} exact relational value.");
                        }

                        continue;
                    }

                    this.WriteRecord(writer, record);
                    previous = record;
                }

                writer.Flush(flushToDisk: true);
                this.CheckActualPhysicalBytes();
            }

            runs.Add(run);
        }

        return runs;
    }

    private string MergeRuns(SetState set, List<string> runs)
    {
        var outputPath = Path.Combine(this.root, $"set-{set.Id:D5}.sorted");
        if (runs.Count == 0)
        {
            using var empty = OpenWrite(outputPath);
            empty.Flush(flushToDisk: true);
            return outputPath;
        }

        var current = runs.ToList();
        try
        {
            while (current.Count > MaximumMergeReaders)
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                var next = new List<string>();
                foreach (var batch in current.Chunk(MaximumMergeReaders))
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    var intermediate = Path.Combine(this.root, $"merge-{Guid.NewGuid():N}");
                    this.MergeBatch(set, batch, intermediate);
                    next.Add(intermediate);
                }

                foreach (var path in current.Where(path => !runs.Contains(path, StringComparer.Ordinal)))
                {
                    TryDelete(path);
                }

                current = next;
            }

            this.MergeBatch(set, current, outputPath);
            return outputPath;
        }
        finally
        {
            foreach (var path in current.Where(path => !runs.Contains(path, StringComparer.Ordinal)))
            {
                TryDelete(path);
            }
        }
    }

    private void MergeBatch(SetState set, IReadOnlyList<string> paths, string outputPath)
    {
        using var output = OpenWrite(outputPath);
        var readers = paths.Select(path => new RecordReader(path, sorted: true, this.cancellationToken)).ToArray();
        try
        {
            var queue = new PriorityQueue<(byte[] Value, int Reader), byte[]>(ByteArrayComparer.Instance);
            for (var index = 0; index < readers.Length; index++)
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                if (readers[index].Read() is { } value)
                {
                    queue.Enqueue((value, index), value);
                }
            }

            byte[]? previous = null;
            while (queue.TryDequeue(out var item, out _))
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null && previous.AsSpan().SequenceEqual(item.Value))
                {
                    if (!set.DuplicatesAllowed)
                    {
                        throw new InvalidDataException($"The database backup contains a duplicate {set.DuplicateDescription} exact relational value.");
                    }
                }
                else
                {
                    this.WriteRecord(output, item.Value);
                    previous = item.Value;
                }

                if (readers[item.Reader].Read() is { } next)
                {
                    queue.Enqueue((next, item.Reader), next);
                }
            }

            output.Flush(flushToDisk: true);
            this.CheckActualPhysicalBytes();
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private void WriteRecord(Stream writer, byte[] record)
    {
        this.cancellationToken.ThrowIfCancellationRequested();
        this.Reserve(checked(4L + record.Length));
        NpgsqlBackupFileSystem.Checkpoint("validation-index-write");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, record.Length);
        writer.Write(length);
        writer.Write(record);
    }

    private void Reserve(long bytes)
    {
        try
        {
            this.physicalBytes = checked(this.physicalBytes + bytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The restore validation disk accounting overflowed.", exception);
        }

        if (this.physicalBytes > this.maximumPhysicalBytes)
        {
            throw new InvalidDataException(
                $"The restore validation index exceeded its enforced {this.maximumPhysicalBytes}-byte disk quota.");
        }
    }

    private void CheckActualPhysicalBytes()
    {
        long actual = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(this.root, "*", SearchOption.TopDirectoryOnly))
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                actual = checked(actual + new FileInfo(path).Length);
            }
        }
        catch (Exception exception) when (exception is IOException or OverflowException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The restore validation index could not verify its actual disk use.", exception);
        }

        if (actual > this.maximumPhysicalBytes)
        {
            throw new InvalidDataException(
                $"The restore validation index actual files exceeded its enforced {this.maximumPhysicalBytes}-byte disk quota.");
        }
    }

    private SetState RequireSet(int id)
    {
        if (!this.sets.TryGetValue(id, out var set))
        {
            var path = Path.Combine(this.root, $"set-{id:D5}.unsorted");
            set = new SetState(id, path, duplicatesAllowed: true, "reference", OpenWrite(path));
            this.sets.Add(id, set);
        }

        return set;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The marker-owned validation root is scavenged on the next run.
        }
    }

    private sealed class SetState(
        int id,
        string unsortedPath,
        bool duplicatesAllowed,
        string duplicateDescription,
        FileStream writer)
    {
        internal int Id { get; } = id;

        internal string UnsortedPath { get; } = unsortedPath;

        internal bool DuplicatesAllowed { get; } = duplicatesAllowed;

        internal string DuplicateDescription { get; } = duplicateDescription;

        internal FileStream Writer { get; } = writer;

        internal string? CompletedPath { get; set; }
    }

    private sealed class RecordReader : IDisposable
    {
        private readonly FileStream stream;
        private readonly bool sorted;

        private readonly CancellationToken cancellationToken;

        internal RecordReader(string path, bool sorted, CancellationToken cancellationToken)
        {
            this.stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
            this.sorted = sorted;
            this.cancellationToken = cancellationToken;
        }

        internal byte[]? Read()
        {
            this.cancellationToken.ThrowIfCancellationRequested();
            Span<byte> header = stackalloc byte[8];
            var first = this.stream.Read(header[..1]);
            if (first == 0)
            {
                return null;
            }

            var headerLength = this.sorted ? 4 : 8;
            this.stream.ReadExactly(header[1..headerLength]);
            var digestLength = this.sorted ? 0 : BinaryPrimitives.ReadInt32LittleEndian(header);
            var valueLength = this.sorted
                ? BinaryPrimitives.ReadInt32LittleEndian(header)
                : BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if ((!this.sorted && digestLength is <= 0 or > 1024)
                || valueLength is < 0 or > MaximumCanonicalKeyBytes + 1024)
            {
                throw new InvalidDataException("The plugin-owned validation index is corrupt.");
            }

            var value = new byte[checked(digestLength + valueLength)];
            this.stream.ReadExactly(value);
            return value;
        }

        public void Dispose() => this.stream.Dispose();
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        internal static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
            => left is null ? (right is null ? 0 : -1)
                : right is null ? 1
                : left.AsSpan().SequenceCompareTo(right);
    }

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
#pragma warning restore CA2000
#pragma warning restore SA1202, SA1204, SA1600
