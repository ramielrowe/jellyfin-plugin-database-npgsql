using System;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Stable identity shared by package metadata and future provider code.
/// </summary>
public static class PluginMetadata
{
    /// <summary>
    /// Gets the display name used by the Jellyfin plugin package.
    /// </summary>
    public const string Name = "Npgsql Database";

    /// <summary>
    /// Gets the permanent plugin identifier recorded in <c>build.yaml</c>.
    /// </summary>
    public static Guid Id { get; } = new("8a6e4c5e-2185-45df-a30f-cc828529330b");
}
