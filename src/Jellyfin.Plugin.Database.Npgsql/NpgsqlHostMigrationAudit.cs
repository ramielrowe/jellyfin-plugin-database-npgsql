using System.Globalization;
using System.Reflection;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Pins the Jellyfin code-migration lineage audited with this plugin release.
/// </summary>
internal static class NpgsqlHostMigrationAudit
{
    /// <summary>The exact host migration IDs reviewed for this plugin release.</summary>
    internal static readonly string[] AuditedMigrationIds =
    [
        "20250420000000_CreateNetworkConfiguration",
        "20250420010000_MigrateNetworkConfiguration",
        "20250420020000_MigrateMusicBrainzTimeout",
        "20250420030000_MigrateEncodingOptions",
        "20250420040000_RenameEnableGroupingIntoCollections",
        "20250420050000_DisableTranscodingThrottling",
        "20250420060000_CreateUserLoggingConfigFile",
        "20250420070000_MigrateActivityLogDb",
        "20250420080000_RemoveDuplicateExtras",
        "20250420090000_AddDefaultPluginRepository",
        "20250420100000_MigrateUserDb",
        "20250420110000_ReaddDefaultPluginRepository",
        "20250420120000_MigrateDisplayPreferencesDb",
        "20250420130000_RemoveDownloadImagesInAdvance",
        "20250420140000_MigrateAuthenticationDb",
        "20250420150000_FixPlaylistOwner",
        "20250420160000_AddDefaultCastReceivers",
        "20250420170000_UpdateDefaultPluginRepository",
        "20250420180000_FixAudioData",
        "20250420193000_MigrateLibraryDbCompatibilityCheck",
        "20250420200000_MigrateLibraryDb",
        "20250420210000_MoveExtractedFiles",
        "20250420230000_MoveTrickplayFiles",
        "20250420230000_RefreshInternalDateModified",
        "20250421000000_MigrateKeyframeData",
        "20250618010000_MigrateLibraryUserData",
        "20250620180000_FixDates",
        "20250730215000_ReseedFolderFlag",
        "20251009200000_CleanMusicArtist",
        "20260113120000_MigrateLinkedChildren",
        "20260113230000_CleanupOrphanedExtras",
        "20260115120000_FixIncorrectOwnerIdRelationships",
        "20260206200000_FixLibrarySubtitleDownloadLanguages",
        "20260508120000_MergeDuplicateMusicArtists",
        "20260508130000_MergeDuplicatePeople",
        "20260522092304_UpdateNormalizedUsername",
        "20260525010000_CleanupOrphanedExternalData",
        "20260531160000_DisableLegacyAuthorization",
        "20260610120000_RefreshCleanNamesAndValues",
        "20260722120000_RefreshForcedSortNames",
        "20260729120000_RestorePlaylistChildrenFromMetadata",
        "20260821120000_RecomputeSeriesPresentationKey",
        "20260825200000_ConsolidateLocalizedUserViews",
        "20260831100000_EnableLocalSimilarityProviders",
        "20260908120000_RepairAlternateVersionLinks",
        "20260911120000_StripEmbeddedLinkedChildren",
        "20260912120000_HarmonizeConflictingUserData",
        "20260915104305_FixNullEncoderPreset",
        "20260915120000_MigrateRatingLevels",
    ];

    /// <summary>The exact audited routines that execute instead of being seeded on fresh setup.</summary>
    internal static readonly string[] AuditedSetupMigrationIds =
    [
        "20250420090000_AddDefaultPluginRepository",
        "20250420110000_ReaddDefaultPluginRepository",
        "20250420160000_AddDefaultCastReceivers",
        "20250420170000_UpdateDefaultPluginRepository",
        "20250420230000_MoveTrickplayFiles",
    ];

    /// <summary>The exact audited routines assigned to the core-initialisation stage.</summary>
    internal static readonly string[] AuditedCoreInitialisationMigrationIds =
    [
        "20260522092304_UpdateNormalizedUsername",
        "20260531160000_DisableLegacyAuthorization",
    ];

    private const string ServerAssemblyName = "jellyfin";
    private const string MigrationAttributeName = "Jellyfin.Server.Migrations.JellyfinMigrationAttribute";

    /// <summary>Validates the real host when running inside Jellyfin.</summary>
    internal static void ValidateLoadedHostIfPresent()
    {
        var serverAssembly = FindLoadedServerAssembly();
        if (serverAssembly is null)
        {
            return;
        }

        ValidateMigrationIds(ReadMigrationIds(serverAssembly));
    }

    /// <summary>Reports whether the production Jellyfin server assembly is loaded in this process.</summary>
    /// <returns><see langword="true" /> when the stock server assembly is loaded.</returns>
    internal static bool IsLoadedHostPresent() => FindLoadedServerAssembly() is not null;

    /// <summary>Rejects any addition, removal, or duplicate in the audited migration set.</summary>
    /// <param name="actualMigrationIds">Migration IDs discovered from the host.</param>
    internal static void ValidateMigrationIds(IEnumerable<string> actualMigrationIds)
    {
        ArgumentNullException.ThrowIfNull(actualMigrationIds);
        var actual = actualMigrationIds.Order(StringComparer.Ordinal).ToArray();
        var expected = AuditedMigrationIds.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            var unknown = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new NotSupportedException(
                $"The loaded Jellyfin server code-migration set is not the version-pinned set audited for {PluginMetadata.Name} "
                + $"{PluginMetadata.Version}. Unknown: [{string.Join(", ", unknown)}]. Missing: [{string.Join(", ", missing)}]. "
                + "Install a plugin release audited for this exact Jellyfin build, or restore the previous host and plugin. "
                + "No database connection was attempted.");
        }
    }

    private static IEnumerable<string> ReadMigrationIds(Assembly serverAssembly)
    {
        foreach (var type in serverAssembly.GetTypes())
        {
            var attribute = type.CustomAttributes.SingleOrDefault(
                candidate => string.Equals(candidate.AttributeType.FullName, MigrationAttributeName, StringComparison.Ordinal));
            if (attribute is null)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count < 2
                || attribute.ConstructorArguments[0].Value is not string order
                || attribute.ConstructorArguments[1].Value is not string name)
            {
                throw new NotSupportedException(
                    $"Jellyfin migration metadata on '{type.FullName}' no longer has the audited constructor shape. "
                    + "No database connection was attempted.");
            }

            var timestamp = DateTime.Parse(order, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            yield return timestamp.ToString("yyyyMMddHHmmsss", CultureInfo.InvariantCulture) + "_" + name;
        }
    }

    private static Assembly? FindLoadedServerAssembly()
        => AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(
            assembly => string.Equals(assembly.GetName().Name, ServerAssemblyName, StringComparison.Ordinal));
}
