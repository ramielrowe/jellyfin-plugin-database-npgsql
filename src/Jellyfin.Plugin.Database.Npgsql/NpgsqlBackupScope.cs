using System.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Identifies the stock backup transaction without changing Jellyfin's public backup API.
/// </summary>
internal static class NpgsqlBackupScope
{
    private const string BaggageKey = "jellyfin.plugin.database.npgsql.backup";
    private const string BaggageValue = "v1";
    private const string HomeSectionsPropertyKey = "jellyfin.plugin.database.npgsql.backup.home-sections";

    /// <summary>Gets a value indicating whether stock backup creation is active.</summary>
    internal static bool IsActive
        => string.Equals(Activity.Current?.GetBaggageItem(BaggageKey), BaggageValue, StringComparison.Ordinal);

    /// <summary>Gets the missing stock dataset captured from the shared PostgreSQL snapshot.</summary>
    internal static string? HomeSectionsJson
        => Activity.Current?.GetCustomProperty(HomeSectionsPropertyKey) as string;

    /// <summary>Records the missing stock dataset on the process-wide backup activity.</summary>
    /// <param name="json">The serialized HomeSection rows.</param>
    internal static void SetHomeSectionsJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        (Activity.Current ?? throw new InvalidOperationException("No stock PostgreSQL backup activity is active."))
            .SetCustomProperty(HomeSectionsPropertyKey, json);
    }

    /// <summary>Enters a scope inherited by the stock backup's asynchronous work.</summary>
    /// <returns>A handle that leaves the scope when disposed.</returns>
    internal static IDisposable Enter()
    {
        var activity = new Activity("Jellyfin.Npgsql.StockBackup");
        activity.AddBaggage(BaggageKey, BaggageValue);
        activity.Start();
        return new Scope(activity);
    }

    private sealed class Scope(Activity activity) : IDisposable
    {
        public void Dispose() => activity.Dispose();
    }
}
