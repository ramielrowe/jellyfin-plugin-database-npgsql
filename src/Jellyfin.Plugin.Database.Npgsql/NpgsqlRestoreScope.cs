using System.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Identifies calls made by the decorated stock restore service.
/// </summary>
internal static class NpgsqlRestoreScope
{
    private const string BaggageKey = "jellyfin.plugin.database.npgsql.restore";
    private const string BaggageValue = "v1";
    private const string ArchivePathBaggageKey = "jellyfin.plugin.database.npgsql.restore.archive";

    /// <summary>Gets a value indicating whether a preflighted stock restore is active.</summary>
    internal static bool IsActive
        => string.Equals(Activity.Current?.GetBaggageItem(BaggageKey), BaggageValue, StringComparison.Ordinal);

    /// <summary>Gets the archive path carried across the provider assembly load boundary.</summary>
    internal static string? ArchivePath => Activity.Current?.GetBaggageItem(ArchivePathBaggageKey);

    /// <summary>Enters the restore scope.</summary>
    /// <param name="archivePath">The validated stock archive path, when called by the host.</param>
    /// <returns>A handle that leaves the scope when disposed.</returns>
    internal static IDisposable Enter(string? archivePath = null)
    {
        var activity = new Activity("Jellyfin.Npgsql.StockRestore");
        activity.AddBaggage(BaggageKey, BaggageValue);
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            activity.AddBaggage(ArchivePathBaggageKey, Path.GetFullPath(archivePath));
        }

        activity.Start();
        return new Scope(activity);
    }

    private sealed class Scope(Activity activity) : IDisposable
    {
        public void Dispose() => activity.Dispose();
    }
}
