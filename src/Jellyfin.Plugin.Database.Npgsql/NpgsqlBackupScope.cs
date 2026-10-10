using System.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Identifies the stock backup transaction without changing Jellyfin's public backup API.
/// </summary>
internal static class NpgsqlBackupScope
{
    private const string BaggageKey = "jellyfin.plugin.database.npgsql.backup";
    private const string BaggageValue = "v1";
    private const string HomeSectionsPropertyKey = "jellyfin.plugin.database.npgsql.backup.home-sections-path";

    /// <summary>Gets a value indicating whether stock backup creation is active.</summary>
    internal static bool IsActive
        => string.Equals(Activity.Current?.GetBaggageItem(BaggageKey), BaggageValue, StringComparison.Ordinal);

    /// <summary>Gets the missing stock dataset captured from the shared PostgreSQL snapshot.</summary>
    internal static string? HomeSectionsSnapshotPath
        => Activity.Current?.GetCustomProperty(HomeSectionsPropertyKey) as string;

    /// <summary>Gets the exact owned path where the interceptor streams the missing stock dataset.</summary>
    /// <returns>The unique snapshot path for the active backup.</returns>
    internal static string RequireHomeSectionsSnapshotPath()
    {
        var path = HomeSectionsSnapshotPath;
        return !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidOperationException("No stock PostgreSQL backup activity is active.");
    }

    /// <summary>Enters a scope inherited by the stock backup's asynchronous work.</summary>
    /// <returns>A handle that leaves the scope when disposed.</returns>
    internal static IDisposable Enter()
    {
        var parent = Path.Combine(NpgsqlScratchSpace.GetRoot(), "home-sections");
        NpgsqlScratchSpace.Scavenge(parent, "home-sections", "home-sections-");
        var ownedDirectory = NpgsqlScratchSpace.CreateOwnedDirectory(parent, "home-sections");
        var snapshotPath = Path.Combine(ownedDirectory, "snapshot.json");
        var activity = new Activity("Jellyfin.Npgsql.StockBackup");
        activity.AddBaggage(BaggageKey, BaggageValue);
        activity.SetCustomProperty(HomeSectionsPropertyKey, snapshotPath);
        activity.Start();
        return new Scope(activity, ownedDirectory);
    }

    private sealed class Scope(Activity activity, string ownedDirectory) : IDisposable
    {
        public void Dispose()
        {
            activity.Dispose();
            try
            {
                NpgsqlScratchSpace.ClaimAndDelete(ownedDirectory, "home-sections");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The owner marker and atomic tombstone allow safe startup/next-run scavenging.
            }
        }
    }
}
