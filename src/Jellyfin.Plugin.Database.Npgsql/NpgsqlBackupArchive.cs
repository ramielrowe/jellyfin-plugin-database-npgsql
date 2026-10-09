using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Binds stock Jellyfin archives to the exact provider and validates them before restore.
/// </summary>
internal static class NpgsqlBackupArchive
{
    /// <summary>The provider-owned entry added to otherwise stock archives.</summary>
    internal const string MarkerEntryName = "Database/jellyfin-npgsql-provider.json";

    /// <summary>The provider-owned dataset omitted by stock Jellyfin's public DbSet enumeration.</summary>
    internal const string HomeSectionsEntryName = "Database/jellyfin-npgsql-home-sections.json";

    /// <summary>Maximum accepted compressed archive length.</summary>
    internal const long MaximumArchiveBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Maximum accepted sum of expanded entry lengths.</summary>
    internal const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>Maximum accepted expanded length of one entry.</summary>
    internal const long MaximumEntryBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Maximum accepted provider-marker length.</summary>
    internal const long MaximumMarkerBytes = 8L * 1024 * 1024;

    /// <summary>Maximum accepted stock-manifest length.</summary>
    internal const long MaximumManifestBytes = 1024 * 1024;

    /// <summary>Maximum accepted expanded-to-compressed ratio.</summary>
    internal const long MaximumCompressionRatio = 1000;

    /// <summary>Maximum accepted ZIP entry count.</summary>
    internal const int MaximumEntryCount = 20000;

    /// <summary>Maximum accepted ZIP central-directory length.</summary>
    internal const int MaximumCentralDirectoryBytes = 128 * 1024 * 1024;

    /// <summary>Maximum accepted total database row count.</summary>
    internal const long MaximumDatabaseRows = 10000000;

    /// <summary>Maximum accepted JSON string length.</summary>
    internal const int MaximumJsonStringLength = 16 * 1024 * 1024;

    /// <summary>Maximum bytes the JSON reader may consume before yielding one database row.</summary>
    internal const int MaximumJsonRowBytes = 32 * 1024 * 1024;

    private const string Format = "jellyfin-npgsql-backup-v1";

    private const string ManifestEntryName = "manifest.json";

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan RestoreDeadline = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        AllowTrailingCommas = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> ExpectedDatabaseEntries = new(StringComparer.Ordinal)
    {
        "Database/AccessSchedules.json",
        "Database/ActivityLogs.json",
        "Database/AncestorIds.json",
        "Database/ApiKeys.json",
        "Database/AttachmentStreamInfos.json",
        "Database/BaseItemImageInfos.json",
        "Database/BaseItemMetadataFields.json",
        "Database/BaseItemProviders.json",
        "Database/BaseItemTrailerTypes.json",
        "Database/BaseItems.json",
        "Database/Chapters.json",
        "Database/CustomItemDisplayPreferences.json",
        "Database/DeviceOptions.json",
        "Database/Devices.json",
        "Database/DisplayPreferences.json",
        "Database/HistoryRow.json",
        "Database/ImageInfos.json",
        "Database/ItemDisplayPreferences.json",
        "Database/ItemValues.json",
        "Database/ItemValuesMap.json",
        "Database/KeyframeData.json",
        "Database/LinkedChildren.json",
        "Database/MediaSegments.json",
        "Database/MediaStreamInfos.json",
        "Database/PeopleBaseItemMap.json",
        "Database/Peoples.json",
        "Database/Permissions.json",
        "Database/Preferences.json",
        "Database/TrickplayInfos.json",
        "Database/UserData.json",
        "Database/Users.json",
    };

    /// <summary>Adds an integrity/provider binding without modifying Jellyfin's manifest contract.</summary>
    /// <param name="archivePath">The completed stock archive.</param>
    /// <param name="databaseIncluded">Whether the stock archive includes its database export.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing atomic archive binding.</returns>
    internal static async Task BindAsync(string archivePath, bool databaseIncluded, CancellationToken cancellationToken)
        => await BindCoreAsync(archivePath, databaseIncluded, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Adds the streamed provider-owned HomeSection snapshot and binds the complete archive.</summary>
    /// <param name="archivePath">The completed stock archive.</param>
    /// <param name="homeSectionsSnapshotPath">The bounded JSON snapshot captured from the stock database transaction.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing atomic archive binding.</returns>
    internal static async Task BindWithHomeSectionsAsync(
        string archivePath,
        string homeSectionsSnapshotPath,
        CancellationToken cancellationToken)
        => await BindCoreAsync(
            archivePath,
            databaseIncluded: true,
            homeSectionsSnapshotPath,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Implements atomic binding for stock archives with optional streamed provider data.</summary>
    /// <param name="archivePath">The stock archive path.</param>
    /// <param name="databaseIncluded">Whether database content is present.</param>
    /// <param name="homeSectionsSnapshotPath">The optional streamed HomeSection snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing archive binding.</returns>
    internal static async Task BindCoreAsync(
        string archivePath,
        bool databaseIncluded,
        string? homeSectionsSnapshotPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var deadline = new CancellationTokenSource(RestoreDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var operationToken = linked.Token;
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("The stock backup archive has no parent directory.");
        UnixFileMode? originalMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(archivePath);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.npgsql-binding");
        try
        {
            await CopyFileAsync(archivePath, temporaryPath, operationToken).ConfigureAwait(false);
            await ValidateCentralDirectoryFileAsync(temporaryPath, operationToken).ConfigureAwait(false);
            using (var archive = await ZipFile.OpenAsync(
                temporaryPath,
                ZipArchiveMode.Update,
                operationToken).ConfigureAwait(false))
            {
                ValidateArchiveLimits(archive, operationToken);
                ValidateEntryNames(archive, operationToken);
                if (archive.GetEntry(MarkerEntryName) is not null)
                {
                    throw new InvalidDataException($"The stock backup unexpectedly already contains '{MarkerEntryName}'.");
                }

                if (archive.GetEntry(HomeSectionsEntryName) is not null)
                {
                    throw new InvalidDataException($"The stock backup unexpectedly already contains '{HomeSectionsEntryName}'.");
                }

                var manifest = await ReadManifestAsync(archive, operationToken).ConfigureAwait(false);
                var existingEntries = new List<BoundEntry>(archive.Entries.Count) { manifest.Binding };
                foreach (var entry in archive.Entries
                    .Where(candidate => !string.Equals(candidate.FullName, ManifestEntryName, StringComparison.Ordinal))
                    .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
                {
                    existingEntries.Add(await ReadBoundEntryAsync(entry, operationToken).ConfigureAwait(false));
                }

                existingEntries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
                if (manifest.DatabaseIncluded != databaseIncluded)
                {
                    throw new InvalidDataException(
                        "The stock manifest database option does not agree with the backup operation that is being bound.");
                }

                if (databaseIncluded)
                {
                    BoundEntry homeSectionsBound;
                    var homeSectionsEntry = archive.CreateEntry(HomeSectionsEntryName, CompressionLevel.Optimal);
                    var homeSectionsStream = await homeSectionsEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using (homeSectionsStream.ConfigureAwait(false))
                    {
                        if (homeSectionsSnapshotPath is null)
                        {
                            var empty = "[]"u8.ToArray();
                            await homeSectionsStream.WriteAsync(empty, operationToken).ConfigureAwait(false);
                            homeSectionsBound = new BoundEntry(
                                HomeSectionsEntryName,
                                empty.LongLength,
                                Convert.ToHexStringLower(SHA256.HashData(empty)));
                        }
                        else
                        {
                            var snapshotInfo = new FileInfo(homeSectionsSnapshotPath);
                            if (!snapshotInfo.Exists || snapshotInfo.Length > MaximumEntryBytes)
                            {
                                throw new InvalidDataException("The HomeSection snapshot exceeds the supported size limit.");
                            }

                            var snapshotStream = new FileStream(
                                homeSectionsSnapshotPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                65536,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using (snapshotStream.ConfigureAwait(false))
                            {
                                await snapshotStream.CopyToAsync(homeSectionsStream, operationToken).ConfigureAwait(false);
                            }

                            var hashStream = new FileStream(
                                homeSectionsSnapshotPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                65536,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using (hashStream.ConfigureAwait(false))
                            {
                                homeSectionsBound = new BoundEntry(
                                    HomeSectionsEntryName,
                                    snapshotInfo.Length,
                                    Convert.ToHexStringLower(
                                        await SHA256.HashDataAsync(hashStream, operationToken).ConfigureAwait(false)));
                            }
                        }
                    }

                    var entries = existingEntries;
                    entries.Add(homeSectionsBound);
                    entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

                    await ValidateDatabaseShapeAsync(archive, operationToken).ConfigureAwait(false);
                    await WriteMarkerAsync(archive, entries, databaseIncluded, operationToken).ConfigureAwait(false);
                }
                else
                {
                    await ValidateDatabaseAbsenceAsync(archive).ConfigureAwait(false);
                    await WriteMarkerAsync(archive, existingEntries, databaseIncluded, operationToken).ConfigureAwait(false);
                }
            }

            if (!OperatingSystem.IsWindows() && originalMode is { } mode)
            {
                File.SetUnixFileMode(temporaryPath, mode);
            }

            File.Move(temporaryPath, archivePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

#pragma warning disable SA1202 // Keep this helper next to the binding workflow it completes.
    private static async Task WriteMarkerAsync(
        ZipArchive archive,
        List<BoundEntry> entries,
        bool databaseIncluded,
        CancellationToken cancellationToken)
    {
        var marker = new ArchiveMarker(
            Format,
            PluginMetadata.Name,
            PluginMetadata.Version.ToString(),
            PluginMetadata.SupportedDatabaseAbi,
            databaseIncluded,
            entries);
        var markerEntry = archive.CreateEntry(MarkerEntryName, CompressionLevel.Optimal);
        var markerStream = await markerEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (markerStream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                markerStream,
                marker,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Validates the complete archive before the stock service extracts any files or mutates history.</summary>
    /// <param name="archivePath">The candidate archive.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing complete preflight validation.</returns>
    internal static async Task ValidateAsync(string archivePath, CancellationToken cancellationToken)
        => await ValidateAsync(archivePath, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Validates bounds, integrity, and the exact Jellyfin persistence model before restore.</summary>
    /// <param name="archivePath">The candidate archive.</param>
    /// <param name="model">The exact finalized runtime model, when invoked by the host.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing complete preflight validation.</returns>
    internal static async Task ValidateAsync(
        string archivePath,
        IModel? model,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var deadline = new CancellationTokenSource(RestoreDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var validationToken = linked.Token;
        validationToken.ThrowIfCancellationRequested();
        var archiveInfo = new FileInfo(archivePath);
        if (!archiveInfo.Exists)
        {
            throw new FileNotFoundException("The selected backup archive does not exist.", archivePath);
        }

        if (archiveInfo.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The backup archive exceeds the supported compressed-size limit.");
        }

        var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            await ValidateCentralDirectoryAsync(stream, validationToken).ConfigureAwait(false);
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
            validationToken.ThrowIfCancellationRequested();
            ValidateArchiveLimits(archive, validationToken);
            ValidateEntryNames(archive, validationToken);
            var markerEntry = archive.GetEntry(MarkerEntryName)
                ?? throw Unsupported("the provider binding is missing; SQLite, unknown-provider, and pre-binding archives cannot be restored");
            if (markerEntry.Length > MaximumMarkerBytes)
            {
                throw new InvalidDataException("The PostgreSQL provider marker exceeds its supported size limit.");
            }

            ArchiveMarker marker;
            var markerStream = await OpenBoundedAsync(markerEntry, MaximumMarkerBytes, validationToken).ConfigureAwait(false);
            await using (markerStream.ConfigureAwait(false))
            {
                marker = await JsonSerializer.DeserializeAsync<ArchiveMarker>(
                    markerStream,
                    SerializerOptions,
                    validationToken).ConfigureAwait(false)
                    ?? throw Unsupported("the provider binding is empty");
            }

            if (!string.Equals(marker.Format, Format, StringComparison.Ordinal)
                || !string.Equals(marker.Provider, PluginMetadata.Name, StringComparison.Ordinal)
                || !string.Equals(marker.PluginVersion, PluginMetadata.Version.ToString(), StringComparison.Ordinal)
                || !string.Equals(marker.DatabaseAbi, PluginMetadata.SupportedDatabaseAbi, StringComparison.Ordinal))
            {
                throw Unsupported(
                    $"the provider/version binding is not exactly {PluginMetadata.Name} {PluginMetadata.Version} for database ABI {PluginMetadata.SupportedDatabaseAbi}");
            }

            var manifest = await ReadManifestAsync(archive, validationToken).ConfigureAwait(false);
            if (marker.DatabaseIncluded != manifest.DatabaseIncluded)
            {
                throw Unsupported("the provider binding and stock manifest disagree about whether database content is included");
            }

            var actualEntries = archive.Entries
                .Where(entry => !string.Equals(entry.FullName, MarkerEntryName, StringComparison.Ordinal))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            if (marker.Entries.Count != actualEntries.Length)
            {
                throw new InvalidDataException("The PostgreSQL backup integrity index does not cover the exact archive contents.");
            }

            for (var index = 0; index < actualEntries.Length; index++)
            {
                var expected = marker.Entries[index];
                var actual = await ReadBoundEntryAsync(actualEntries[index], validationToken).ConfigureAwait(false);
                if (expected != actual)
                {
                    throw new InvalidDataException($"Backup entry '{actual.Path}' does not match its PostgreSQL integrity binding.");
                }
            }

            if (marker.DatabaseIncluded)
            {
                await ValidateDatabaseShapeAsync(archive, validationToken, model: model).ConfigureAwait(false);
            }
            else
            {
                await ValidateDatabaseAbsenceAsync(archive).ConfigureAwait(false);
            }
        }
    }

    private static async Task ValidateCentralDirectoryFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using (stream.ConfigureAwait(false))
        {
            await ValidateCentralDirectoryAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidateCentralDirectoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const int maximumEndRecordSearch = ushort.MaxValue + 22;
        var tailLength = (int)Math.Min(stream.Length, maximumEndRecordSearch);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        var endOffset = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054b50)
            {
                endOffset = index;
                break;
            }
        }

        if (endOffset < 0)
        {
            throw new InvalidDataException("The backup archive has no bounded ZIP central-directory record.");
        }

        var record = tail.AsSpan(endOffset);
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var centralLength = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (diskEntries == ushort.MaxValue
            || totalEntries == ushort.MaxValue
            || centralLength == uint.MaxValue
            || centralOffset == uint.MaxValue)
        {
            throw new NotSupportedException("ZIP64 backup archives are disabled because stock restore does not provide a bounded central-directory reader.");
        }

        if (diskEntries != totalEntries || totalEntries > MaximumEntryCount)
        {
            throw new InvalidDataException("The backup archive exceeds the supported central-directory entry-count limit.");
        }

        if (centralLength > MaximumCentralDirectoryBytes
            || checked((long)centralOffset + centralLength) > stream.Length)
        {
            throw new InvalidDataException("The backup archive exceeds the supported central-directory size.");
        }
    }

    /// <summary>Copies and validates an immutable restore input owned by this plugin operation.</summary>
    /// <param name="archivePath">The user-selected stock archive.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>An owned validated snapshot which deletes its marker-named file when disposed.</returns>
    internal static async Task<ValidatedArchiveSnapshot> CreateValidatedSnapshotAsync(
        string archivePath,
        CancellationToken cancellationToken)
        => await CreateValidatedSnapshotAsync(archivePath, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Copies and fully validates an immutable restore input.</summary>
    /// <param name="archivePath">The user-selected stock archive.</param>
    /// <param name="model">The exact finalized runtime model, when available.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>An owned validated snapshot.</returns>
    internal static async Task<ValidatedArchiveSnapshot> CreateValidatedSnapshotAsync(
        string archivePath,
        IModel? model,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var deadline = new CancellationTokenSource(RestoreDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var operationToken = linked.Token;
        operationToken.ThrowIfCancellationRequested();
        var sourceInfo = new FileInfo(archivePath);
        if (!sourceInfo.Exists)
        {
            throw new FileNotFoundException("The selected backup archive does not exist.", archivePath);
        }

        if (sourceInfo.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The backup archive exceeds the supported compressed-size limit.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("The restore archive has no parent directory.");
        var snapshotPath = Path.Combine(
            directory,
            $".jellyfin-npgsql-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await CopyFileAsync(archivePath, snapshotPath, operationToken).ConfigureAwait(false);
            await ValidateAsync(snapshotPath, model, operationToken).ConfigureAwait(false);
            return new ValidatedArchiveSnapshot(snapshotPath);
        }
        catch
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }

            throw;
        }
    }
#pragma warning restore SA1202

    private static async Task ValidateDatabaseShapeAsync(
        ZipArchive archive,
        CancellationToken cancellationToken,
        IModel? model = null)
    {
        var actual = archive.Entries
            .Where(entry => entry.FullName.StartsWith("Database/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".json", StringComparison.Ordinal)
                && !string.Equals(entry.FullName, MarkerEntryName, StringComparison.Ordinal)
                && !string.Equals(entry.FullName, HomeSectionsEntryName, StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(ExpectedDatabaseEntries))
        {
            var missing = ExpectedDatabaseEntries.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var unknown = actual.Except(ExpectedDatabaseEntries, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw Unsupported(
                $"the database entry set is incomplete or belongs to another host version; missing [{string.Join(", ", missing)}], unknown [{string.Join(", ", unknown)}]");
        }

        if (model is null)
        {
            foreach (var path in ExpectedDatabaseEntries)
            {
                await ValidateJsonObjectArrayAsync(archive.GetEntry(path)!, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ValidateDatabaseModelAsync(archive, model, cancellationToken).ConfigureAwait(false);
        }

        var homeSectionsEntry = archive.GetEntry(HomeSectionsEntryName)
            ?? throw Unsupported($"the provider-owned entry '{HomeSectionsEntryName}' is missing");
        if (model is null)
        {
            await ValidateJsonObjectArrayAsync(homeSectionsEntry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task ValidateDatabaseAbsenceAsync(ZipArchive archive)
    {
        var unexpected = archive.Entries
            .Where(entry => entry.FullName.StartsWith("Database/", StringComparison.Ordinal)
                && !string.Equals(entry.FullName, MarkerEntryName, StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unexpected.Length != 0)
        {
            throw Unsupported(
                $"the stock manifest excludes database content but database entries are present [{string.Join(", ", unexpected)}]");
        }

        return Task.CompletedTask;
    }

    private static async Task<StockManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
        {
            throw Unsupported("the stock manifest is missing or duplicated");
        }

        var entry = entries[0];
        var entryLength = entry.Length;
        if (entryLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("The stock backup manifest exceeds its supported size limit.");
        }

        var stream = await OpenBoundedAsync(entry, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var manifestBytes = new MemoryStream((int)entryLength);
            await stream.CopyToAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            var bytes = manifestBytes.ToArray();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = SerializerOptions.MaxDepth });
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The stock backup manifest is malformed.", exception);
            }

            using (document)
            {
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                {
                    throw new InvalidDataException("The stock backup manifest must be a JSON object.");
                }

                var optionsProperties = document.RootElement.EnumerateObject()
                    .Where(property => string.Equals(property.Name, "Options", StringComparison.Ordinal))
                    .ToArray();
                if (optionsProperties.Length != 1 || optionsProperties[0].Value.ValueKind is not JsonValueKind.Object)
                {
                    throw new InvalidDataException("The stock backup manifest must contain exactly one object-valued Options property.");
                }

                var databaseProperties = optionsProperties[0].Value.EnumerateObject()
                    .Where(property => string.Equals(property.Name, "Database", StringComparison.Ordinal))
                    .ToArray();
                if (databaseProperties.Length != 1
                    || databaseProperties[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException(
                        "The stock backup manifest must contain exactly one boolean Options.Database property.");
                }

                return new StockManifest(
                    databaseProperties[0].Value.GetBoolean(),
                    new BoundEntry(
                        ManifestEntryName,
                        entryLength,
                        Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
        }
    }

    private static async Task ValidateDatabaseModelAsync(
        ZipArchive archive,
        IModel model,
        CancellationToken cancellationToken)
    {
        var dbSets = typeof(JellyfinDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
            .ToDictionary(
                property => $"Database/{property.Name}.json",
                property => property.PropertyType.GetGenericArguments()[0],
                StringComparer.Ordinal);
        using var state = new ModelValidationState(model, cancellationToken);
        long rows = 0;
        foreach (var path in ExpectedDatabaseEntries.Order(StringComparer.Ordinal))
        {
            var entry = archive.GetEntry(path)!;
            if (string.Equals(path, "Database/HistoryRow.json", StringComparison.Ordinal))
            {
                rows += await ValidateHistoryAsync(entry, state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!dbSets.TryGetValue(path, out var clrType))
                {
                    throw Unsupported($"the runtime model has no exact DbSet contract for '{path}'");
                }

                var entityType = model.FindEntityType(clrType)
                    ?? throw Unsupported($"the runtime model has no exact entity contract for '{clrType.FullName}'");
                rows += await ValidateEntityEntryAsync(entry, entityType, state, cancellationToken).ConfigureAwait(false);
            }

            if (rows > MaximumDatabaseRows)
            {
                throw new InvalidDataException("The database backup exceeds the supported total row limit.");
            }
        }

        var homeSectionsEntry = archive.GetEntry(HomeSectionsEntryName)
            ?? throw Unsupported($"the provider-owned entry '{HomeSectionsEntryName}' is missing");
        var homeSectionType = model.GetEntityTypes().SingleOrDefault(
            entity => string.Equals(entity.GetTableName(), "HomeSection", StringComparison.Ordinal))
            ?? throw Unsupported("the runtime model has no exact HomeSection contract");
        rows += await ValidateEntityEntryAsync(
            homeSectionsEntry,
            homeSectionType,
            state,
            cancellationToken).ConfigureAwait(false);
        if (rows > MaximumDatabaseRows)
        {
            throw new InvalidDataException("The database backup exceeds the supported total row limit.");
        }

        state.ValidateRelationshipsAndHistory();
    }

    private static async Task<long> ValidateEntityEntryAsync(
        ZipArchiveEntry entry,
        IEntityType entityType,
        ModelValidationState state,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        var stream = await OpenBoundedAsync(
            entry,
            MaximumEntryBytes,
            cancellationToken,
            MaximumJsonRowBytes).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Database backup entry '{entry.FullName}' contains a non-object row.");
                    }

                    ValidateJsonStrings(item, entry.FullName);
                    var entity = item.Deserialize(entityType.ClrType, SerializerOptions)
                        ?? throw new InvalidDataException($"Database backup entry '{entry.FullName}' contains a null entity.");
                    state.AddEntity(entityType, item, entry.FullName, cancellationToken);
                    rows++;
                    stream.ResetSegmentBudget();
                    if (rows > MaximumDatabaseRows)
                    {
                        throw new InvalidDataException($"Database backup entry '{entry.FullName}' exceeds the supported row limit.");
                    }
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Database backup entry '{entry.FullName}' does not match its exact CLR contract.",
                    exception);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException(
                    $"Database backup entry '{entry.FullName}' does not match its exact CLR contract.",
                    exception);
            }
        }

        return rows;
    }

    private static async Task<long> ValidateHistoryAsync(
        ZipArchiveEntry entry,
        ModelValidationState state,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        var stream = await OpenBoundedAsync(
            entry,
            MaximumEntryBytes,
            cancellationToken,
            MaximumJsonRowBytes).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await foreach (var history in JsonSerializer.DeserializeAsyncEnumerable<HistoryRow>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (history is null
                        || string.IsNullOrWhiteSpace(history.MigrationId)
                        || string.IsNullOrWhiteSpace(history.ProductVersion)
                        || history.MigrationId.Length > MaximumJsonStringLength
                        || history.ProductVersion.Length > MaximumJsonStringLength)
                    {
                        throw new InvalidDataException("The database migration history contains an invalid row.");
                    }

                    if (history.MigrationId.Length > 150
                        || history.ProductVersion.Length > 32
                        || !IsValidMigrationId(history.MigrationId)
                        || !Version.TryParse(history.ProductVersion, out _))
                    {
                        throw new InvalidDataException("The database migration history contains an invalid row.");
                    }

                    state.AddHistory(history.MigrationId, history.ProductVersion);
                    rows++;
                    stream.ResetSegmentBudget();
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The database migration history does not match its exact CLR contract.", exception);
            }
        }

        return rows;
    }

    private static async Task ValidateJsonObjectArrayAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var stream = await OpenBoundedAsync(
            entry,
            MaximumEntryBytes,
            cancellationToken,
            MaximumJsonRowBytes).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            long rows = 0;
            try
            {
                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Database backup entry '{entry.FullName}' contains a non-object row.");
                    }

                    ValidateJsonStrings(item, entry.FullName);
                    stream.ResetSegmentBudget();
                    if (++rows > MaximumDatabaseRows)
                    {
                        throw new InvalidDataException($"Database backup entry '{entry.FullName}' exceeds the supported row limit.");
                    }
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Database backup entry '{entry.FullName}' is malformed.", exception);
            }
        }
    }

    private static void ValidateJsonStrings(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Length > MaximumJsonStringLength)
                    {
                        throw new InvalidDataException($"Database backup entry '{path}' contains an oversized property name.");
                    }

                    ValidateJsonStrings(property.Value, path);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    ValidateJsonStrings(child, path);
                }

                break;
            case JsonValueKind.String:
                if ((element.GetString()?.Length ?? 0) > MaximumJsonStringLength)
                {
                    throw new InvalidDataException($"Database backup entry '{path}' contains an oversized string.");
                }

                break;
        }
    }

    private static void ValidateEntryNames(ZipArchive archive, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var canonicalNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(entry.FullName))
            {
                throw new InvalidDataException($"The backup contains duplicate entry '{entry.FullName}'.");
            }

            var isDirectory = entry.FullName.EndsWith('/');
            if (string.IsNullOrEmpty(entry.FullName)
                || Path.IsPathRooted(entry.FullName)
                || entry.FullName.StartsWith('/')
                || entry.FullName.Contains('\\', StringComparison.Ordinal)
                || entry.FullName.Contains("//", StringComparison.Ordinal)
                || entry.FullName.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The backup contains unsafe entry name '{entry.FullName}'.");
            }

            var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
            var segments = path.Split('/');
            if (segments.Length == 0
                || segments.Any(segment => segment.Length == 0
                    || segment is "." or ".."
                    || segment.Contains(':', StringComparison.Ordinal)
                    || segment.EndsWith(' ')
                    || segment.EndsWith('.')
                    || segment.Any(char.IsControl)))
            {
                throw new InvalidDataException($"The backup contains unsafe entry name '{entry.FullName}'.");
            }

            var canonical = string.Join('/', segments.Select(segment => segment.Normalize(NormalizationForm.FormC)));
            if (canonicalNames.TryGetValue(canonical, out var existingDirectory))
            {
                var kind = existingDirectory == isDirectory ? "alias" : "file/directory collision";
                throw new InvalidDataException(
                    $"The backup contains a canonical {kind} for entry '{entry.FullName}'.");
            }

            var ancestor = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                ancestor = index == 0 ? segments[index] : ancestor + "/" + segments[index];
                ancestor = ancestor.Normalize(NormalizationForm.FormC);
                if (canonicalNames.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory)
                {
                    throw new InvalidDataException(
                        $"The backup contains a file/directory collision for entry '{entry.FullName}'.");
                }
            }

            if (!isDirectory
                && canonicalNames.Keys.Any(existing => existing.StartsWith(canonical + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"The backup contains a file/directory collision for entry '{entry.FullName}'.");
            }

            canonicalNames.Add(canonical, isDirectory);
        }
    }

    private static bool IsValidMigrationId(string migrationId)
    {
        if (migrationId.Length < 16 || migrationId[14] != '_')
        {
            return false;
        }

        for (var index = 0; index < migrationId.Length; index++)
        {
            var character = migrationId[index];
            if (index < 14)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }
            else if (index > 14 && !(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateArchiveLimits(ZipArchive archive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("The backup archive exceeds the supported entry-count limit.");
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException($"Backup entry '{entry.FullName}' exceeds the supported expanded-size limit.");
            }

            try
            {
                expanded = checked(expanded + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The backup archive expanded-size declaration overflows.", exception);
            }

            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The backup archive exceeds the supported total expanded-size limit.");
            }

            if (entry.Length > 0
                && (entry.CompressedLength == 0
                    || entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio))
            {
                throw new InvalidDataException($"Backup entry '{entry.FullName}' exceeds the supported compression ratio.");
            }
        }
    }

    private static async Task<BoundedReadStream> OpenBoundedAsync(
        ZipArchiveEntry entry,
        long limit,
        CancellationToken cancellationToken,
        long? segmentLimit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new BoundedReadStream(
            await entry.OpenAsync(cancellationToken).ConfigureAwait(false),
            limit,
            entry.FullName,
            segmentLimit);
    }

    private static async Task<BoundEntry> ReadBoundEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var length = entry.Length;
        var stream = await OpenBoundedAsync(entry, MaximumEntryBytes, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new BoundEntry(entry.FullName, length, Convert.ToHexStringLower(hash));
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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

        var destinationStream = new FileStream(destination, destinationOptions);
        await using (sourceStream.ConfigureAwait(false))
        await using (destinationStream.ConfigureAwait(false))
        {
            var boundedSource = new BoundedReadStream(
                sourceStream,
                MaximumArchiveBytes,
                Path.GetFileName(source),
                segmentLimit: null);
            await using (boundedSource.ConfigureAwait(false))
            {
                await boundedSource.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            }

            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static NotSupportedException Unsupported(string reason)
        => new(
            $"The selected database-inclusive backup is not safe for {PluginMetadata.Name}: {reason}. "
            + "The archive was rejected before Jellyfin extracted files or changed PostgreSQL.");

    /// <summary>Owns the validated restore snapshot and its exact cleanup target.</summary>
    internal sealed class ValidatedArchiveSnapshot : IDisposable
    {
        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ValidatedArchiveSnapshot"/> class.
        /// </summary>
        /// <param name="path">The exact marker-owned snapshot path.</param>
        internal ValidatedArchiveSnapshot(string path) => this.Path = path;

        /// <summary>Gets the marker-named snapshot path.</summary>
        internal string Path { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            if (File.Exists(this.Path))
            {
                File.Delete(this.Path);
            }
        }
    }

    private sealed record ArchiveMarker(
        string Format,
        string Provider,
        string PluginVersion,
        string DatabaseAbi,
        bool DatabaseIncluded,
        List<BoundEntry> Entries);

    private sealed record BoundEntry(string Path, long Length, string Sha256);

    private sealed record StockManifest(bool DatabaseIncluded, BoundEntry Binding);

#pragma warning disable SA1204 // Validation helpers are kept beside the operations which use them.
    private sealed class ModelValidationState : IDisposable
    {
        private static readonly string[] PluginMigrationIds =
        [
            "20261009061101_InitialPostgreSqlBaseline",
            "20261009061446_AddActivityLogTypeDateIndex",
            "20261009113458_AddUuidMinMaxAggregates",
        ];

        private readonly Dictionary<IKey, int> keys = new();
        private readonly Dictionary<IIndex, int> uniqueIndexes = new();
        private readonly Dictionary<IForeignKey, ForeignStore> foreignKeys = new();
        private readonly HashSet<string> histories = new(StringComparer.Ordinal);
        private readonly List<string> orderedHistories = new();
        private readonly string scratchRoot;
        private readonly NpgsqlExactDiskIndex index;
        private bool disposed;

        internal ModelValidationState(IModel model, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scratchParent = NpgsqlScratchSpace.GetValidationRoot();
            NpgsqlScratchSpace.ScavengeValidationDirectories(scratchParent);
            this.scratchRoot = NpgsqlScratchSpace.CreateOwnedDirectory(scratchParent, "validation");
            try
            {
                this.index = new NpgsqlExactDiskIndex(this.scratchRoot);
            }
            catch
            {
                DeleteValidationScratch(this.scratchRoot);
                throw;
            }

            var nextSet = 0;
            foreach (var entityType in model.GetEntityTypes())
            {
                foreach (var key in entityType.GetKeys())
                {
                    this.keys.TryAdd(key, nextSet++);
                }

                foreach (var index in entityType.GetIndexes().Where(index => index.IsUnique))
                {
                    this.uniqueIndexes.TryAdd(index, nextSet++);
                }
            }

            foreach (var entityType in model.GetEntityTypes())
            {
                foreach (var foreignKey in entityType.GetForeignKeys())
                {
                    this.foreignKeys.TryAdd(
                        foreignKey,
                        new ForeignStore(nextSet++, this.keys[foreignKey.PrincipalKey]));
                }
            }
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.index.Dispose();
        }

        internal void AddHistory(string migrationId, string productVersion)
        {
            if (!this.histories.Add(migrationId))
            {
                throw new InvalidDataException($"The database migration history contains duplicate id '{migrationId}'.");
            }

            var expectedProductVersion = NpgsqlHostMigrationAudit.AuditedMigrationIds.Contains(
                migrationId,
                StringComparer.Ordinal)
                ? PluginMetadata.SupportedDatabaseAbi
                : PluginMigrationIds.Contains(migrationId, StringComparer.Ordinal)
                    ? ProductInfo.GetVersion()
                    : null;
            if (expectedProductVersion is null
                || !string.Equals(productVersion, expectedProductVersion, StringComparison.Ordinal))
            {
                throw Unsupported(
                    $"migration '{migrationId}' has ProductVersion '{productVersion}' instead of its exact supported value");
            }

            this.orderedHistories.Add(migrationId);
        }

        internal void AddEntity(
            IEntityType entityType,
            JsonElement json,
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persistedValues = new Dictionary<IProperty, object?>();
            foreach (var property in entityType.GetProperties())
            {
                var propertyInfo = property.PropertyInfo;
                if (propertyInfo is null)
                {
                    continue;
                }

                if (!json.TryGetProperty(property.Name, out var jsonValue))
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' omits required model property '{entityType.ClrType.Name}.{property.Name}'.");
                }

                ValidatePropertyJsonType(property, jsonValue, path);
                object? value;
                try
                {
                    value = jsonValue.ValueKind is JsonValueKind.Null
                        ? null
                        : jsonValue.Deserialize(property.ClrType, SerializerOptions);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' cannot convert '{entityType.ClrType.Name}.{property.Name}' to its exact model type.",
                        exception);
                }

                persistedValues.Add(property, value);
                if (!property.IsNullable && value is null)
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' has null for required property '{entityType.ClrType.Name}.{property.Name}'.");
                }

                if (value is string text && property.GetMaxLength() is { } maximum && text.Length > maximum)
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' exceeds the model length for '{entityType.ClrType.Name}.{property.Name}'.");
                }
            }

            foreach (var key in entityType.GetKeys())
            {
                this.index.Add(
                    this.keys[key],
                    EncodeValues(key.Properties, persistedValues, allowNull: false, path),
                    duplicatesAllowed: false,
                    duplicateDescription: "key");
            }

            foreach (var index in entityType.GetIndexes().Where(index => index.IsUnique))
            {
                var values = ReadIndexValues(index, persistedValues, path);
                if (values.Any(value => value is null))
                {
                    continue;
                }

                this.index.Add(
                    this.uniqueIndexes[index],
                    EncodeValues(values),
                    duplicatesAllowed: false,
                    duplicateDescription: "unique value");
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var values = ReadValues(foreignKey.Properties, persistedValues, path);
                if (values.All(value => value is null))
                {
                    continue;
                }

                if (values.Any(value => value is null))
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' contains a partially null relationship for '{entityType.ClrType.Name}'.");
                }

                this.index.Add(
                    this.foreignKeys[foreignKey].SetId,
                    EncodeValues(values),
                    duplicatesAllowed: true,
                    duplicateDescription: "relationship");
            }
        }

        internal void ValidateRelationshipsAndHistory()
        {
            var expectedHistory = NpgsqlHostMigrationAudit.AuditedMigrationIds
                .Concat(PluginMigrationIds)
                .ToArray();
            if (!this.orderedHistories.SequenceEqual(expectedHistory, StringComparer.Ordinal))
            {
                var missing = expectedHistory.Except(this.histories, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var unknown = this.histories.Except(expectedHistory, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                throw Unsupported(
                    $"the migration history is not the exact supported ordered lineage; missing [{string.Join(", ", missing)}], unknown [{string.Join(", ", unknown)}]");
            }

            foreach (var setId in this.keys.Values.Concat(this.uniqueIndexes.Values))
            {
                this.index.CompleteSet(setId);
            }

            foreach (var (foreignKey, store) in this.foreignKeys)
            {
                if (this.index.ContainsMissingFrom(store.SetId, store.PrincipalSetId))
                {
                    throw new InvalidDataException(
                        $"The database backup contains an unresolved relationship from '{foreignKey.DeclaringEntityType.ClrType.Name}' "
                        + $"to '{foreignKey.PrincipalEntityType.ClrType.Name}'.");
                }
            }
        }

        private static void DeleteValidationScratch(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static byte[] EncodeValues(
            IReadOnlyList<IProperty> properties,
            IReadOnlyDictionary<IProperty, object?> persistedValues,
            bool allowNull,
            string path)
        {
            var values = ReadValues(properties, persistedValues, path);
            if (!allowNull && values.Any(value => value is null))
            {
                throw new InvalidDataException($"Database backup entry '{path}' contains a null key value.");
            }

            return EncodeValues(values);
        }

        private static object?[] ReadValues(
            IReadOnlyList<IProperty> properties,
            IReadOnlyDictionary<IProperty, object?> persistedValues,
            string path)
            => properties.Select(property => persistedValues.TryGetValue(property, out var value)
                ? value
                : throw new InvalidDataException(
                    $"Database backup entry '{path}' uses unsupported shadow key property '{property.Name}'."))
                .ToArray();

        private static object?[] ReadIndexValues(
            IIndex index,
            Dictionary<IProperty, object?> persistedValues,
            string path)
        {
            var logicalSourceName = index.FindAnnotation(NpgsqlTextInterning.ExactIdentityAnnotation)?.Value as string;
            var logicalSource = logicalSourceName is null
                ? null
                : index.DeclaringEntityType.FindProperty(logicalSourceName);
            return index.Properties.Select(property =>
            {
                if (persistedValues.TryGetValue(property, out var value))
                {
                    return value;
                }

                if (logicalSource is not null
                    && string.Equals(property.Name, logicalSourceName + "InternId", StringComparison.Ordinal)
                    && persistedValues.TryGetValue(logicalSource, out var logicalValue))
                {
                    return logicalValue;
                }

                throw new InvalidDataException(
                    $"Database backup entry '{path}' uses unsupported shadow unique property '{property.Name}'.");
            }).ToArray();
        }

        private static byte[] EncodeValues(IReadOnlyList<object?> values)
        {
            using var stream = new MemoryStream();
            Span<byte> length = stackalloc byte[4];
            foreach (var value in values)
            {
                if (value is null)
                {
                    stream.WriteByte(0);
                    continue;
                }

                stream.WriteByte(1);
                var typeBytes = Encoding.UTF8.GetBytes(value.GetType().FullName ?? value.GetType().Name);
                var valueBytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), SerializerOptions);
                BinaryPrimitives.WriteInt32LittleEndian(length, typeBytes.Length);
                stream.Write(length);
                stream.Write(typeBytes);
                BinaryPrimitives.WriteInt32LittleEndian(length, valueBytes.Length);
                stream.Write(length);
                stream.Write(valueBytes);
            }

            return stream.ToArray();
        }

        private static void ValidatePropertyJsonType(IProperty property, JsonElement value, string path)
        {
            if (value.ValueKind is JsonValueKind.Null)
            {
                return;
            }

            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            var valid = type == typeof(string) || type == typeof(char) || type == typeof(Guid)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
                ? value.ValueKind is JsonValueKind.String
                : type == typeof(bool)
                    ? value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    : type.IsEnum || type == typeof(byte) || type == typeof(sbyte)
                        || type == typeof(short) || type == typeof(ushort)
                        || type == typeof(int) || type == typeof(uint)
                        || type == typeof(long) || type == typeof(ulong)
                        || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
                            ? value.ValueKind is JsonValueKind.Number
                            : type == typeof(byte[])
                                ? value.ValueKind is JsonValueKind.String
                                : true;
            if (!valid)
            {
                throw new InvalidDataException(
                    $"Database backup entry '{path}' has the wrong JSON type for '{property.DeclaringType.ClrType.Name}.{property.Name}'.");
            }
        }

        private sealed record ForeignStore(int SetId, int PrincipalSetId);
    }
#pragma warning restore SA1204

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream inner;
        private readonly long limit;
        private readonly long? segmentLimit;
        private readonly string entryName;
        private long consumed;
        private long segmentStart;

#pragma warning disable SA1202 // The containing stream type is private; construction remains scoped to this archive reader.
        internal BoundedReadStream(Stream inner, long limit, string entryName, long? segmentLimit = null)
#pragma warning restore SA1202
        {
            this.inner = inner;
            this.limit = limit;
            this.entryName = entryName;
            this.segmentLimit = segmentLimit;
        }

        public override bool CanRead => this.inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => this.consumed;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = this.inner.Read(buffer, offset, count);
            this.Account(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = this.inner.Read(buffer);
            this.Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await this.inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            this.Account(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await this.inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.inner.Dispose();
            }

            base.Dispose(disposing);
        }

#pragma warning disable SA1202 // The private stream exposes this checkpoint only to its containing archive reader.
        internal void ResetSegmentBudget() => this.segmentStart = this.consumed;
#pragma warning restore SA1202

        private void Account(int read)
        {
            try
            {
                this.consumed = checked(this.consumed + read);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    $"Backup entry '{this.entryName}' overflowed its streamed size accounting.",
                    exception);
            }

            if (this.consumed > this.limit)
            {
                throw new InvalidDataException($"Backup entry '{this.entryName}' exceeded its supported size while streaming.");
            }

            if (this.segmentLimit is { } rowLimit && this.consumed - this.segmentStart > rowLimit)
            {
                throw new InvalidDataException($"Backup entry '{this.entryName}' contains an oversized JSON row.");
            }
        }
    }
}
