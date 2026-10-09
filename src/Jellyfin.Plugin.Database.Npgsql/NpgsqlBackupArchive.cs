using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

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
    private const string Format = "jellyfin-npgsql-backup-v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

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
        => await BindAsync(archivePath, databaseIncluded, "[]", cancellationToken).ConfigureAwait(false);

    /// <summary>Adds provider-owned data missing from stock backup and binds the complete archive.</summary>
    /// <param name="archivePath">The completed stock archive.</param>
    /// <param name="databaseIncluded">Whether the stock archive includes its database export.</param>
    /// <param name="homeSectionsJson">HomeSection rows captured from the stock database snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing atomic archive binding.</returns>
    internal static async Task BindAsync(
        string archivePath,
        bool databaseIncluded,
        string homeSectionsJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("The stock backup archive has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.npgsql-binding");
        try
        {
            await CopyFileAsync(archivePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            using (var archive = await ZipFile.OpenAsync(
                temporaryPath,
                ZipArchiveMode.Update,
                cancellationToken).ConfigureAwait(false))
            {
                ValidateEntryNames(archive);
                if (archive.GetEntry(MarkerEntryName) is not null)
                {
                    throw new InvalidDataException($"The stock backup unexpectedly already contains '{MarkerEntryName}'.");
                }

                if (archive.GetEntry(HomeSectionsEntryName) is not null)
                {
                    throw new InvalidDataException($"The stock backup unexpectedly already contains '{HomeSectionsEntryName}'.");
                }

                if (databaseIncluded)
                {
                    ValidateHomeSectionsJson(homeSectionsJson);
                    var homeSectionsEntry = archive.CreateEntry(HomeSectionsEntryName, CompressionLevel.Optimal);
                    var homeSectionsStream = await homeSectionsEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using (homeSectionsStream.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(homeSectionsStream);
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteAsync(homeSectionsJson.AsMemory(), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                var entries = new List<BoundEntry>(archive.Entries.Count);
                foreach (var entry in archive.Entries
                    .Where(candidate => !string.Equals(candidate.FullName, HomeSectionsEntryName, StringComparison.Ordinal))
                    .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
                {
                    entries.Add(await ReadBoundEntryAsync(entry, cancellationToken).ConfigureAwait(false));
                }

                if (databaseIncluded)
                {
                    var homeSectionsBytes = System.Text.Encoding.UTF8.GetBytes(homeSectionsJson);
                    entries.Add(new BoundEntry(
                        HomeSectionsEntryName,
                        homeSectionsBytes.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(homeSectionsBytes))));
                    entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
                }

                if (databaseIncluded)
                {
                    await ValidateDatabaseShapeAsync(archive, cancellationToken, homeSectionsJson).ConfigureAwait(false);
                }

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

    /// <summary>Validates the complete archive before the stock service extracts any files or mutates history.</summary>
    /// <param name="archivePath">The candidate archive.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing complete preflight validation.</returns>
    internal static async Task ValidateAsync(string archivePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
        {
            ValidateEntryNames(archive);
            var markerEntry = archive.GetEntry(MarkerEntryName)
                ?? throw Unsupported("the provider binding is missing; SQLite, unknown-provider, and pre-binding archives cannot be restored");
            ArchiveMarker marker;
            var markerStream = await markerEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (markerStream.ConfigureAwait(false))
            {
                marker = await JsonSerializer.DeserializeAsync<ArchiveMarker>(
                    markerStream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false)
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
                var actual = await ReadBoundEntryAsync(actualEntries[index], cancellationToken).ConfigureAwait(false);
                if (expected != actual)
                {
                    throw new InvalidDataException($"Backup entry '{actual.Path}' does not match its PostgreSQL integrity binding.");
                }
            }

            if (marker.DatabaseIncluded)
            {
                await ValidateDatabaseShapeAsync(archive, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Copies and validates an immutable restore input owned by this plugin operation.</summary>
    /// <param name="archivePath">The user-selected stock archive.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>An owned validated snapshot which deletes its marker-named file when disposed.</returns>
    internal static async Task<ValidatedArchiveSnapshot> CreateValidatedSnapshotAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("The restore archive has no parent directory.");
        var snapshotPath = Path.Combine(
            directory,
            $".jellyfin-npgsql-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await CopyFileAsync(archivePath, snapshotPath, cancellationToken).ConfigureAwait(false);
            await ValidateAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
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

    private static async Task ValidateDatabaseShapeAsync(
        ZipArchive archive,
        CancellationToken cancellationToken,
        string? homeSectionsJson = null)
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

        foreach (var path in ExpectedDatabaseEntries)
        {
            var entry = archive.GetEntry(path)!;
            var entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (entryStream.ConfigureAwait(false))
            {
                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                    entryStream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Database backup entry '{path}' contains a non-object row.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        if (homeSectionsJson is not null)
        {
            ValidateHomeSectionsJson(homeSectionsJson);
        }
        else
        {
            var homeSectionsEntry = archive.GetEntry(HomeSectionsEntryName)
                ?? throw Unsupported($"the provider-owned entry '{HomeSectionsEntryName}' is missing");
            var homeSectionsStream = await homeSectionsEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (homeSectionsStream.ConfigureAwait(false))
            using (var reader = new StreamReader(homeSectionsStream))
            {
                ValidateHomeSectionsJson(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private static void ValidateHomeSectionsJson(string json)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<HomeSectionRow[]>(json, SerializerOptions)
                ?? throw new InvalidDataException("The provider-owned HomeSection dataset is null.");
            if (rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            {
                throw new InvalidDataException("The provider-owned HomeSection dataset contains duplicate identities.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The provider-owned HomeSection dataset is malformed.", exception);
        }
    }

    private static void ValidateEntryNames(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
            {
                throw new InvalidDataException($"The backup contains duplicate entry '{entry.FullName}'.");
            }

            if (Path.IsPathRooted(entry.FullName)
                || entry.FullName.Split('/').Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException($"The backup contains unsafe entry name '{entry.FullName}'.");
            }
        }
    }

    private static async Task<BoundEntry> ReadBoundEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var length = entry.Length;
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new BoundEntry(entry.FullName, length, Convert.ToHexStringLower(hash));
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var destinationStream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            131072,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (sourceStream.ConfigureAwait(false))
        await using (destinationStream.ConfigureAwait(false))
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static NotSupportedException Unsupported(string reason)
        => new(
            $"The selected database-inclusive backup is not safe for {PluginMetadata.Name}: {reason}. "
            + "The archive was rejected before Jellyfin extracted files or changed PostgreSQL.");

    private sealed record ArchiveMarker(
        string Format,
        string Provider,
        string PluginVersion,
        string DatabaseAbi,
        bool DatabaseIncluded,
        List<BoundEntry> Entries);

    private sealed record BoundEntry(string Path, long Length, string Sha256);

    private sealed record HomeSectionRow(int Id, int DisplayPreferencesId, int Order, int Type);

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
}
