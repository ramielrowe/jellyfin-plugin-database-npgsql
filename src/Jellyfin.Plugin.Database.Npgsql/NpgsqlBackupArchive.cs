using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Maximum accepted expanded-to-compressed ratio.</summary>
    internal const long MaximumCompressionRatio = 1000;

    /// <summary>Maximum accepted ZIP entry count.</summary>
    internal const int MaximumEntryCount = 20000;

    /// <summary>Maximum accepted total database row count.</summary>
    internal const long MaximumDatabaseRows = 10000000;

    /// <summary>Maximum accepted JSON string length.</summary>
    internal const int MaximumJsonStringLength = 16 * 1024 * 1024;

    /// <summary>Maximum bytes the JSON reader may consume before yielding one database row.</summary>
    internal const int MaximumJsonRowBytes = 32 * 1024 * 1024;

    private const string Format = "jellyfin-npgsql-backup-v1";

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
                ValidateArchiveLimits(archive);
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
                    BoundEntry homeSectionsBound;
                    var homeSectionsEntry = archive.CreateEntry(HomeSectionsEntryName, CompressionLevel.Optimal);
                    var homeSectionsStream = await homeSectionsEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using (homeSectionsStream.ConfigureAwait(false))
                    {
                        if (homeSectionsSnapshotPath is null)
                        {
                            var empty = "[]"u8.ToArray();
                            await homeSectionsStream.WriteAsync(empty, cancellationToken).ConfigureAwait(false);
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
                                await snapshotStream.CopyToAsync(homeSectionsStream, cancellationToken).ConfigureAwait(false);
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
                                        await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false)));
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

                    entries.Add(homeSectionsBound);
                    entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

                    await ValidateDatabaseShapeAsync(archive, cancellationToken).ConfigureAwait(false);
                    await WriteMarkerAsync(archive, entries, databaseIncluded, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var entries = new List<BoundEntry>(archive.Entries.Count);
                    foreach (var entry in archive.Entries.OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
                    {
                        entries.Add(await ReadBoundEntryAsync(entry, cancellationToken).ConfigureAwait(false));
                    }

                    await WriteMarkerAsync(archive, entries, databaseIncluded, cancellationToken).ConfigureAwait(false);
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var validationToken = linked.Token;
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
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
        {
            ValidateArchiveLimits(archive);
            ValidateEntryNames(archive);
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
            await CopyFileAsync(archivePath, snapshotPath, cancellationToken).ConfigureAwait(false);
            await ValidateAsync(snapshotPath, model, cancellationToken).ConfigureAwait(false);
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
        var state = new ModelValidationState(model);
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
                    state.AddEntity(entityType, item, entry.FullName);
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

                    state.AddHistory(history.MigrationId);
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

    private static void ValidateArchiveLimits(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("The backup archive exceeds the supported entry-count limit.");
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
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

    private sealed class ModelValidationState
    {
        private static readonly string[] PluginMigrationIds =
        [
            "20261009061101_InitialPostgreSqlBaseline",
            "20261009061446_AddActivityLogTypeDateIndex",
            "20261009113458_AddUuidMinMaxAggregates",
        ];

        private readonly Dictionary<IKey, HashSet<string>> keys = new();
        private readonly Dictionary<IIndex, HashSet<string>> uniqueIndexes = new();
        private readonly Dictionary<IForeignKey, HashSet<string>> foreignKeys = new();
        private readonly HashSet<string> histories = new(StringComparer.Ordinal);

        internal ModelValidationState(IModel model)
        {
            foreach (var entityType in model.GetEntityTypes())
            {
                foreach (var key in entityType.GetKeys())
                {
                    this.keys.TryAdd(key, new HashSet<string>(StringComparer.Ordinal));
                }

                foreach (var index in entityType.GetIndexes().Where(index => index.IsUnique))
                {
                    this.uniqueIndexes.TryAdd(index, new HashSet<string>(StringComparer.Ordinal));
                }

                foreach (var foreignKey in entityType.GetForeignKeys())
                {
                    this.foreignKeys.TryAdd(foreignKey, new HashSet<string>(StringComparer.Ordinal));
                }
            }
        }

        internal void AddHistory(string migrationId)
        {
            if (!this.histories.Add(migrationId))
            {
                throw new InvalidDataException($"The database migration history contains duplicate id '{migrationId}'.");
            }
        }

        internal void AddEntity(IEntityType entityType, JsonElement json, string path)
        {
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
                var hash = HashValues(key.Properties, persistedValues, allowNull: false, path);
                if (!this.keys[key].Add(hash))
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' contains a duplicate key for '{entityType.ClrType.Name}'.");
                }
            }

            foreach (var index in entityType.GetIndexes().Where(index => index.IsUnique))
            {
                var values = ReadIndexValues(index, persistedValues, path);
                if (values.Any(value => value is null))
                {
                    continue;
                }

                var hash = HashValues(values);
                if (!this.uniqueIndexes[index].Add(hash))
                {
                    throw new InvalidDataException(
                        $"Database backup entry '{path}' contains a duplicate unique value for '{entityType.ClrType.Name}'.");
                }
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

                this.foreignKeys[foreignKey].Add(HashValues(values));
            }
        }

        internal void ValidateRelationshipsAndHistory()
        {
            var expectedHistory = NpgsqlHostMigrationAudit.AuditedMigrationIds
                .Concat(PluginMigrationIds)
                .ToHashSet(StringComparer.Ordinal);
            if (!this.histories.SetEquals(expectedHistory))
            {
                var missing = expectedHistory.Except(this.histories, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var unknown = this.histories.Except(expectedHistory, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                throw Unsupported(
                    $"the migration history is not the exact supported lineage; missing [{string.Join(", ", missing)}], unknown [{string.Join(", ", unknown)}]");
            }

            foreach (var (foreignKey, references) in this.foreignKeys)
            {
                var principalKeys = this.keys[foreignKey.PrincipalKey];
                if (references.Any(reference => !principalKeys.Contains(reference)))
                {
                    throw new InvalidDataException(
                        $"The database backup contains an unresolved relationship from '{foreignKey.DeclaringEntityType.ClrType.Name}' "
                        + $"to '{foreignKey.PrincipalEntityType.ClrType.Name}'.");
                }
            }
        }

        private static string HashValues(
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

            return HashValues(values);
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

        private static string HashValues(IReadOnlyList<object?> values)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var value in values)
            {
                if (value is null)
                {
                    hash.AppendData([0]);
                    continue;
                }

                var typeBytes = Encoding.UTF8.GetBytes(value.GetType().FullName ?? value.GetType().Name);
                var valueBytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), SerializerOptions);
                hash.AppendData(BitConverter.GetBytes(typeBytes.Length));
                hash.AppendData(typeBytes);
                hash.AppendData(BitConverter.GetBytes(valueBytes.Length));
                hash.AppendData(valueBytes);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
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
    }

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
            this.consumed += read;
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
