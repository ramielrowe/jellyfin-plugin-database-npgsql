using System;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Stable identity shared by package metadata and future provider code.
/// </summary>
public static class PluginMetadata
{
    /// <summary>
    /// Gets the release version represented by this package.
    /// </summary>
#if NPGSQL_PREDECESSOR_FIXTURE
    public const string Version = "0.0.9.0";
#else
    public const string Version = "0.1.0.0";
#endif

    /// <summary>
    /// Gets the display name used by the Jellyfin plugin package.
    /// </summary>
    public const string Name = "Npgsql Database";

    /// <summary>
    /// Gets the exact installation directory selected by <c>database.xml</c>.
    /// </summary>
    /// <remarks>
    /// Jellyfin's vanilla early loader performs a prefix match. Including the
    /// version in the configured name and validating the selected directory in
    /// the provider prevents an older version from being selected during an
    /// upgrade or rollback.
    /// </remarks>
    public const string InstallationDirectory = Name + "_" + Version;

    /// <summary>
    /// Gets the provider assembly file name used by <c>database.xml</c>.
    /// </summary>
    public const string AssemblyFileName = "Jellyfin.Plugin.Database.Npgsql.dll";

    /// <summary>
    /// Gets the supported Jellyfin database ABI family.
    /// </summary>
    public const string SupportedDatabaseAbi = "12.2.0.0";

    /// <summary>
    /// Gets the permanent plugin identifier recorded in <c>build.yaml</c>.
    /// </summary>
    public static Guid Id { get; } = new("8a6e4c5e-2185-45df-a30f-cc828529330b");
}
