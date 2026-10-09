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
    private const string IdentityRestartCheckpointKey = "jellyfin.plugin.database.npgsql.restore.identity-checkpoint";

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

    /// <summary>Installs a flow-local identity-restart checkpoint for failure-path verification.</summary>
    /// <param name="checkpoint">The checkpoint called after each transactional sequence restart.</param>
    internal static void SetIdentityRestartCheckpoint(Func<int, CancellationToken, Task> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var activity = Activity.Current
            ?? throw new InvalidOperationException("An active PostgreSQL restore scope is required.");
        activity.SetCustomProperty(IdentityRestartCheckpointKey, checkpoint);
    }

    /// <summary>Invokes the flow-local identity-restart checkpoint, when one is installed.</summary>
    /// <param name="ordinal">The one-based sequence restart ordinal.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>A task representing the checkpoint.</returns>
    internal static Task ReachIdentityRestartCheckpointAsync(int ordinal, CancellationToken cancellationToken)
        => Activity.Current?.GetCustomProperty(IdentityRestartCheckpointKey)
            is Func<int, CancellationToken, Task> checkpoint
                ? checkpoint(ordinal, cancellationToken)
                : Task.CompletedTask;

    private sealed class Scope(Activity activity) : IDisposable
    {
        public void Dispose() => activity.Dispose();
    }
}
